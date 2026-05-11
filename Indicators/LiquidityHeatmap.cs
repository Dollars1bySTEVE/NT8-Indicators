#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class LiquidityHeatmap : Indicator
    {
        #region Types
        private enum TradeSide
        {
            Buy,
            Sell
        }

        private sealed class TradePrint
        {
            public DateTime Time;
            public double Price;
            public TradeSide Side;
            public long Size;
        }

        private sealed class QuotePoint
        {
            public DateTime Time;
            public double Bid;
            public double Ask;
        }

        private sealed class DepthSnapshot
        {
            public DateTime Time;
            public Dictionary<double, long> BidSizes;
            public Dictionary<double, long> AskSizes;
        }

        private sealed class RenderedDot
        {
            public TradePrint Print;
            public float X;
            public float Y;
            public float Radius;
        }
        #endregion

        #region Fields
        private readonly object syncRoot = new object();
        private const int MaxTradePrints = 10000;
        private const int PersistTradePrints = 5000;
        private const int MaxQuotePoints = 20000;

        private SortedDictionary<double, long> bidBook;
        private SortedDictionary<double, long> askBook;

        private DepthSnapshot[] snapshots;
        private int snapshotsHead;
        private int snapshotsCount;
        private DateTime lastSnapshotUtc;

        private List<TradePrint> tradePrints;
        private List<QuotePoint> quotePoints;
        private List<RenderedDot> renderedDots;

        private TradePrint hoveredPrint;
        private Point hoverPoint;
        private bool mouseHooked;

        private double currentBid;
        private double currentAsk;
        private double currentLast;

        private SolidColorBrush[] dxHeatBrushes;
        private SolidColorBrush dxWallBrush;
        private SolidColorBrush dxBidLineBrush;
        private SolidColorBrush dxAskLineBrush;
        private SolidColorBrush dxBuyDotBrush;
        private SolidColorBrush dxSellDotBrush;
        private SolidColorBrush dxBidLadderBrush;
        private SolidColorBrush dxAskLadderBrush;
        private SolidColorBrush dxWhiteBrush;
        private SolidColorBrush dxTooltipBackgroundBrush;
        private SolidColorBrush dxTooltipBorderBrush;
        private SolidColorBrush dxBrandingBrush;
        private SolidColorBrush dxBestBidFillBrush;
        private SolidColorBrush dxBestAskFillBrush;

        private TextFormat tfWall;
        private TextFormat tfLadder;
        private TextFormat tfTooltip;
        private TextFormat tfTooltipSmall;
        private TextFormat tfBranding;
        #endregion

        #region State Management
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"Bookmap-style liquidity heatmap with walls, best-bid/ask line, trade prints, and DOM ladder overlays.";
                Name = "Liquidity Heatmap";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = false;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = true;

                EnableHeatmap = true;
                VisiblePriceLevels = 20;
                SnapshotIntervalMs = 100;
                HistorySnapshots = 14400;
                BackgroundColor = Brushes.Black;
                LowSizeThreshold = 20;
                MidSizeThreshold = 80;
                MaxSizeThreshold = 200;

                EnableWalls = true;
                WallThreshold = 100;
                WallColor = Brushes.Red;

                EnableTradeDots = true;
                DotMinSize = 100;
                MinDotRadius = 4;
                MaxDotRadius = 40;
                BuyDotColor = Brushes.LimeGreen;
                SellDotColor = Brushes.Magenta;

                EnableBidAskLine = true;
                BidLineColor = Brushes.LimeGreen;
                AskLineColor = Brushes.Magenta;
                ShowForwardExtension = true;
                ShowBestBidAskLabels = true;

                EnableDomLadder = true;
                BidLadderColor = Brushes.LimeGreen;
                AskLadderColor = Brushes.Magenta;

                ShowBrandingLabel = true;
            }
            else if (State == State.Configure)
            {
                EnsureSnapshotCapacity();
            }
            else if (State == State.DataLoaded)
            {
                bidBook = new SortedDictionary<double, long>();
                askBook = new SortedDictionary<double, long>();
                tradePrints = new List<TradePrint>(2048);
                quotePoints = new List<QuotePoint>(4096);
                renderedDots = new List<RenderedDot>(2048);
                lastSnapshotUtc = Core.Globals.MinDate;
                HookMouseEvents();
            }
            else if (State == State.Terminated)
            {
                UnhookMouseEvents();
                DisposeDxResources();
            }
        }
        #endregion

        #region Data Capture
        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (e == null)
                return;

            bool isBid = e.MarketDataType == MarketDataType.Bid;
            bool isAsk = e.MarketDataType == MarketDataType.Ask;
            if (!isBid && !isAsk)
                return;

            var book = isBid ? bidBook : askBook;
            if (book == null)
                return;

            double price = SnapToTick(e.Price);
            long size = Math.Max(0L, e.Volume);

            lock (syncRoot)
            {
                if (e.Operation == Operation.Remove || size == 0)
                {
                    if (book.ContainsKey(price))
                        book.Remove(price);
                }
                else
                {
                    book[price] = size;
                }

                DateTime nowUtc = DateTime.UtcNow;
                if (lastSnapshotUtc == Core.Globals.MinDate || (nowUtc - lastSnapshotUtc).TotalMilliseconds >= SnapshotIntervalMs)
                {
                    AppendSnapshot(nowUtc);
                    lastSnapshotUtc = nowUtc;
                }
            }
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e == null)
                return;

            if (e.MarketDataType == MarketDataType.Bid)
            {
                currentBid = SnapToTick(e.Price);
                AddQuotePoint(DateTime.UtcNow);
                return;
            }

            if (e.MarketDataType == MarketDataType.Ask)
            {
                currentAsk = SnapToTick(e.Price);
                AddQuotePoint(DateTime.UtcNow);
                return;
            }

            if (e.MarketDataType != MarketDataType.Last)
                return;

            currentLast = SnapToTick(e.Price);
            if (!EnableTradeDots)
                return;

            long size = Math.Max(0L, e.Volume);
            if (size < DotMinSize)
                return;

            TradeSide? side = null;
            if (currentAsk > 0 && e.Price >= currentAsk)
                side = TradeSide.Buy;
            else if (currentBid > 0 && e.Price <= currentBid)
                side = TradeSide.Sell;

            if (!side.HasValue)
                return;

            lock (syncRoot)
            {
                tradePrints.Add(new TradePrint
                {
                    Time = DateTime.UtcNow,
                    Price = currentLast,
                    Side = side.Value,
                    Size = size
                });

                if (tradePrints.Count > MaxTradePrints)
                    tradePrints.RemoveRange(0, tradePrints.Count - MaxTradePrints);
            }
        }

        protected override void OnBarUpdate()
        {
            if (snapshots == null || snapshots.Length != HistorySnapshots)
                EnsureSnapshotCapacity();
        }
        #endregion

        #region SharpDX Resources
        public override void OnRenderTargetChanged()
        {
            DisposeDxResources();
            if (RenderTarget == null)
                return;

            try
            {
                BuildHeatmapBrushes();
                dxWallBrush = new SolidColorBrush(RenderTarget, ToDxColor4(WallColor, 0.95f));
                dxBidLineBrush = new SolidColorBrush(RenderTarget, ToDxColor4(BidLineColor, 0.95f));
                dxAskLineBrush = new SolidColorBrush(RenderTarget, ToDxColor4(AskLineColor, 0.95f));
                dxBuyDotBrush = new SolidColorBrush(RenderTarget, ToDxColor4(BuyDotColor, 0.85f));
                dxSellDotBrush = new SolidColorBrush(RenderTarget, ToDxColor4(SellDotColor, 0.85f));
                dxBidLadderBrush = new SolidColorBrush(RenderTarget, ToDxColor4(BidLadderColor, 0.95f));
                dxAskLadderBrush = new SolidColorBrush(RenderTarget, ToDxColor4(AskLadderColor, 0.95f));
                dxWhiteBrush = new SolidColorBrush(RenderTarget, new Color4(1f, 1f, 1f, 1f));
                dxTooltipBackgroundBrush = new SolidColorBrush(RenderTarget, new Color4(0f, 0f, 0f, 0.78f));
                dxTooltipBorderBrush = new SolidColorBrush(RenderTarget, new Color4(0.9f, 0.9f, 0.9f, 0.85f));
                dxBrandingBrush = new SolidColorBrush(RenderTarget, new Color4(1f, 1f, 1f, 0.40f));
                dxBestBidFillBrush = new SolidColorBrush(RenderTarget, ToDxColor4(BidLineColor, 0.95f));
                dxBestAskFillBrush = new SolidColorBrush(RenderTarget, ToDxColor4(AskLineColor, 0.95f));

                tfWall = new TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI", FontWeight.Bold, FontStyle.Normal, 12f);
                tfLadder = new TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI", FontWeight.Normal, FontStyle.Normal, 10f);
                tfTooltip = new TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI", FontWeight.SemiBold, FontStyle.Normal, 12f);
                tfTooltipSmall = new TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI", FontWeight.Normal, FontStyle.Normal, 11f);
                tfBranding = new TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI", FontWeight.Normal, FontStyle.Normal, 11f);
            }
            catch
            {
                DisposeDxResources();
            }
        }
        #endregion

        #region Rendering
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (RenderTarget == null || chartControl == null || chartScale == null || ChartBars == null || CurrentBar < 1)
                return;

            if (!mouseHooked)
                HookMouseEvents();

            try { RenderHeatmap(chartControl, chartScale); } catch { }
            try { RenderWalls(chartScale); } catch { }
            try { RenderBidAskStepLine(chartControl, chartScale); } catch { }
            try { RenderForwardExtension(chartScale); } catch { }
            try { RenderTradeDots(chartControl, chartScale); } catch { }
            try { RenderDomLadder(chartScale); } catch { }
            try { RenderBestBidAskLabels(chartScale); } catch { }
            try { RenderTooltip(); } catch { }
            try { RenderBranding(); } catch { }
        }

        private void RenderHeatmap(ChartControl cc, ChartScale cs)
        {
            if (!EnableHeatmap || snapshotsCount == 0 || dxHeatBrushes == null || TickSize <= 0)
                return;

            int firstBar = Math.Max(0, ChartBars.FromIndex);
            int lastBar = Math.Min(CurrentBar, ChartBars.ToIndex);
            if (lastBar <= firstBar)
                return;

            DateTime firstTime = Bars.GetTime(firstBar).ToUniversalTime();
            DateTime lastTime = Bars.GetTime(lastBar).ToUniversalTime();
            float leftX = cc.GetXByBarIndex(ChartBars, firstBar);
            float rightX = cc.GetXByBarIndex(ChartBars, lastBar);

            double center = currentLast > 0 ? currentLast : (currentBid > 0 && currentAsk > 0 ? (currentBid + currentAsk) * 0.5 : Close[0]);
            double minPrice = center - VisiblePriceLevels * TickSize;
            double maxPrice = center + VisiblePriceLevels * TickSize;

            lock (syncRoot)
            {
                for (int i = 0; i < snapshotsCount; i++)
                {
                    DepthSnapshot snap = GetSnapshotAt(i);
                    if (snap == null || snap.Time < firstTime || snap.Time > lastTime)
                        continue;

                    float x1 = TimeToX(snap.Time, firstTime, lastTime, leftX, rightX);
                    float x2;
                    if (i == snapshotsCount - 1)
                    {
                        x2 = Math.Max(x1 + 1f, rightX);
                    }
                    else
                    {
                        DepthSnapshot nextSnap = GetSnapshotAt(i + 1);
                        x2 = nextSnap == null ? x1 + 1f : TimeToX(nextSnap.Time, firstTime, lastTime, leftX, rightX);
                    }

                    if (x2 < ChartPanel.X || x1 > ChartPanel.X + ChartPanel.W)
                        continue;

                    RenderSnapshotLevels(snap.BidSizes, minPrice, maxPrice, x1, x2, cs);
                    RenderSnapshotLevels(snap.AskSizes, minPrice, maxPrice, x1, x2, cs);
                }
            }
        }

        private void RenderSnapshotLevels(Dictionary<double, long> levels, double minPrice, double maxPrice, float x1, float x2, ChartScale cs)
        {
            if (levels == null || levels.Count == 0)
                return;

            foreach (var kv in levels)
            {
                double price = kv.Key;
                long size = kv.Value;
                if (size <= 0 || price < minPrice || price > maxPrice)
                    continue;

                float yA = cs.GetYByValue(price + TickSize * 0.5);
                float yB = cs.GetYByValue(price - TickSize * 0.5);
                float top = Math.Min(yA, yB);
                float height = Math.Max(1f, Math.Abs(yB - yA));
                var rect = new RectangleF(x1, top, Math.Max(1f, x2 - x1), height);

                if (EnableWalls && size >= WallThreshold)
                    RenderTarget.FillRectangle(rect, dxWallBrush);
                else
                    RenderTarget.FillRectangle(rect, dxHeatBrushes[SizeToBrushIndex(size)]);
            }
        }

        private void RenderWalls(ChartScale cs)
        {
            if (!EnableWalls || WallThreshold <= 0 || tfWall == null)
                return;

            float bandX = ChartPanel.X + ChartPanel.W - 62f;
            float bandW = 60f;
            double center = currentLast > 0 ? currentLast : (currentBid > 0 && currentAsk > 0 ? (currentBid + currentAsk) * 0.5 : Close[0]);
            double minPrice = center - VisiblePriceLevels * TickSize;
            double maxPrice = center + VisiblePriceLevels * TickSize;

            lock (syncRoot)
            {
                RenderWallBand(bidBook, minPrice, maxPrice, bandX, bandW, cs);
                RenderWallBand(askBook, minPrice, maxPrice, bandX, bandW, cs);
            }
        }

        private void RenderWallBand(SortedDictionary<double, long> book, double minPrice, double maxPrice, float x, float w, ChartScale cs)
        {
            if (book == null)
                return;

            foreach (var kv in book)
            {
                if (kv.Value < WallThreshold || kv.Key < minPrice || kv.Key > maxPrice)
                    continue;

                float yA = cs.GetYByValue(kv.Key + TickSize * 0.5);
                float yB = cs.GetYByValue(kv.Key - TickSize * 0.5);
                float top = Math.Min(yA, yB);
                float h = Math.Max(1f, Math.Abs(yB - yA));
                var rect = new RectangleF(x, top, w, h);
                RenderTarget.FillRectangle(rect, dxWallBrush);
                RenderTarget.DrawText(kv.Value.ToString(CultureInfo.InvariantCulture), tfWall, rect, dxWhiteBrush, DrawTextOptions.None, MeasuringMode.Natural);
            }
        }

        private void RenderBidAskStepLine(ChartControl cc, ChartScale cs)
        {
            if (!EnableBidAskLine || quotePoints == null || quotePoints.Count < 2)
                return;

            int firstBar = Math.Max(0, ChartBars.FromIndex);
            int lastBar = Math.Min(CurrentBar, ChartBars.ToIndex);
            DateTime firstTime = Bars.GetTime(firstBar).ToUniversalTime();
            DateTime lastTime = Bars.GetTime(lastBar).ToUniversalTime();
            float leftX = cc.GetXByBarIndex(ChartBars, firstBar);
            float rightX = cc.GetXByBarIndex(ChartBars, lastBar);

            lock (syncRoot)
            {
                QuotePoint prev = null;
                for (int i = 0; i < quotePoints.Count; i++)
                {
                    QuotePoint cur = quotePoints[i];
                    if (cur.Time < firstTime || cur.Time > lastTime)
                    {
                        prev = cur;
                        continue;
                    }

                    if (prev == null)
                    {
                        prev = cur;
                        continue;
                    }

                    float xPrev = TimeToX(prev.Time, firstTime, lastTime, leftX, rightX);
                    float xCur = TimeToX(cur.Time, firstTime, lastTime, leftX, rightX);

                    if (prev.Bid > 0 && cur.Bid > 0)
                    {
                        float yPrev = cs.GetYByValue(prev.Bid);
                        float yCur = cs.GetYByValue(cur.Bid);
                        RenderTarget.DrawLine(new Vector2(xPrev, yPrev), new Vector2(xCur, yPrev), dxBidLineBrush, 1.8f);
                        RenderTarget.DrawLine(new Vector2(xCur, yPrev), new Vector2(xCur, yCur), dxBidLineBrush, 1.2f);
                    }

                    if (prev.Ask > 0 && cur.Ask > 0)
                    {
                        float yPrev = cs.GetYByValue(prev.Ask);
                        float yCur = cs.GetYByValue(cur.Ask);
                        RenderTarget.DrawLine(new Vector2(xPrev, yPrev), new Vector2(xCur, yPrev), dxAskLineBrush, 1.8f);
                        RenderTarget.DrawLine(new Vector2(xCur, yPrev), new Vector2(xCur, yCur), dxAskLineBrush, 1.2f);
                    }

                    prev = cur;
                }
            }
        }

        private void RenderForwardExtension(ChartScale cs)
        {
            if (!EnableBidAskLine || !ShowForwardExtension)
                return;

            float rightX = ChartPanel.X + ChartPanel.W;
            float fromX = rightX - 60f;
            if (currentBid > 0)
            {
                float y = cs.GetYByValue(currentBid);
                RenderTarget.DrawLine(new Vector2(fromX, y), new Vector2(rightX, y), dxBidLineBrush, 2f);
            }
            if (currentAsk > 0)
            {
                float y = cs.GetYByValue(currentAsk);
                RenderTarget.DrawLine(new Vector2(fromX, y), new Vector2(rightX, y), dxAskLineBrush, 2f);
            }
        }

        private void RenderTradeDots(ChartControl cc, ChartScale cs)
        {
            if (!EnableTradeDots || tradePrints == null || tradePrints.Count == 0)
                return;

            int firstBar = Math.Max(0, ChartBars.FromIndex);
            int lastBar = Math.Min(CurrentBar, ChartBars.ToIndex);
            DateTime firstTime = Bars.GetTime(firstBar).ToUniversalTime();
            DateTime lastTime = Bars.GetTime(lastBar).ToUniversalTime();
            float leftX = cc.GetXByBarIndex(ChartBars, firstBar);
            float rightX = cc.GetXByBarIndex(ChartBars, lastBar);

            lock (syncRoot)
            {
                renderedDots.Clear();
                for (int i = 0; i < tradePrints.Count; i++)
                {
                    TradePrint tp = tradePrints[i];
                    if (tp.Time < firstTime || tp.Time > lastTime)
                        continue;

                    float x = TimeToX(tp.Time, firstTime, lastTime, leftX, rightX);
                    float y = cs.GetYByValue(tp.Price);
                    if (x < ChartPanel.X || x > ChartPanel.X + ChartPanel.W || y < ChartPanel.Y || y > ChartPanel.Y + ChartPanel.H)
                        continue;

                    float r = SizeToRadius(tp.Size);
                    RenderTarget.FillEllipse(new Ellipse(new Vector2(x, y), r, r), tp.Side == TradeSide.Buy ? dxBuyDotBrush : dxSellDotBrush);
                    renderedDots.Add(new RenderedDot { Print = tp, X = x, Y = y, Radius = r });
                }
            }
        }

        private void RenderDomLadder(ChartScale cs)
        {
            if (!EnableDomLadder || tfLadder == null)
                return;

            double center = currentLast > 0 ? currentLast : (currentBid > 0 && currentAsk > 0 ? (currentBid + currentAsk) * 0.5 : Close[0]);
            double minPrice = center - VisiblePriceLevels * TickSize;
            double maxPrice = center + VisiblePriceLevels * TickSize;
            float x = ChartPanel.X + ChartPanel.W - 104f;

            lock (syncRoot)
            {
                foreach (var kv in askBook)
                {
                    if (kv.Key < minPrice || kv.Key > maxPrice || kv.Value <= 0)
                        continue;
                    float y = cs.GetYByValue(kv.Key) - 7f;
                    RenderTarget.DrawText(kv.Value.ToString(CultureInfo.InvariantCulture), tfLadder, new RectangleF(x, y, 36f, 14f), dxAskLadderBrush);
                }

                foreach (var kv in bidBook)
                {
                    if (kv.Key < minPrice || kv.Key > maxPrice || kv.Value <= 0)
                        continue;
                    float y = cs.GetYByValue(kv.Key) - 7f;
                    RenderTarget.DrawText(kv.Value.ToString(CultureInfo.InvariantCulture), tfLadder, new RectangleF(x + 40f, y, 36f, 14f), dxBidLadderBrush);
                }
            }
        }

        private void RenderBestBidAskLabels(ChartScale cs)
        {
            if (!EnableBidAskLine || !ShowBestBidAskLabels || tfWall == null)
                return;

            float w = 72f;
            float h = 18f;
            float x = ChartPanel.X + ChartPanel.W - w;

            if (currentAsk > 0)
            {
                float y = cs.GetYByValue(currentAsk) - h * 0.5f;
                var rect = new RectangleF(x, y, w, h);
                RenderTarget.FillRectangle(rect, dxBestAskFillBrush);
                RenderTarget.DrawText(FormatPrice(currentAsk), tfWall, rect, dxWhiteBrush, DrawTextOptions.None, MeasuringMode.Natural);
            }

            if (currentBid > 0)
            {
                float y = cs.GetYByValue(currentBid) - h * 0.5f;
                var rect = new RectangleF(x, y, w, h);
                RenderTarget.FillRectangle(rect, dxBestBidFillBrush);
                RenderTarget.DrawText(FormatPrice(currentBid), tfWall, rect, dxWhiteBrush, DrawTextOptions.None, MeasuringMode.Natural);
            }
        }

        private void RenderTooltip()
        {
            if (hoveredPrint == null || tfTooltip == null || tfTooltipSmall == null)
                return;

            string side = hoveredPrint.Side == TradeSide.Buy ? "BUY" : "SELL";
            string line1 = string.Format(CultureInfo.InvariantCulture, "{0} {1} @ {2}", hoveredPrint.Size, side, FormatPrice(hoveredPrint.Price));
            string line2 = hoveredPrint.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

            float x = (float)hoverPoint.X + 14f;
            float y = (float)hoverPoint.Y + 14f;
            float w = 160f;
            float h = 42f;
            if (x + w > ChartPanel.X + ChartPanel.W)
                x = ChartPanel.X + ChartPanel.W - w - 8f;
            if (y + h > ChartPanel.Y + ChartPanel.H)
                y = ChartPanel.Y + ChartPanel.H - h - 8f;

            var rect = new RectangleF(x, y, w, h);
            RenderTarget.FillRectangle(rect, dxTooltipBackgroundBrush);
            RenderTarget.DrawRectangle(rect, dxTooltipBorderBrush, 1f);
            RenderTarget.DrawText(line1, tfTooltip, new RectangleF(x + 6f, y + 4f, w - 10f, 18f), dxWhiteBrush);
            RenderTarget.DrawText(line2, tfTooltipSmall, new RectangleF(x + 6f, y + 22f, w - 10f, 16f), dxWhiteBrush);
        }

        private void RenderBranding()
        {
            if (!ShowBrandingLabel || tfBranding == null)
                return;

            RenderTarget.DrawText("Liquidity Heatmap", tfBranding, new RectangleF(ChartPanel.X + 8f, ChartPanel.Y + 8f, 160f, 18f), dxBrandingBrush);
        }
        #endregion

        #region Mouse Handling
        private void HookMouseEvents()
        {
            if (ChartControl == null || mouseHooked)
                return;

            ChartControl.MouseMove += OnChartMouseMove;
            ChartControl.MouseLeave += OnChartMouseLeave;
            mouseHooked = true;
        }

        private void UnhookMouseEvents()
        {
            if (ChartControl == null || !mouseHooked)
                return;

            ChartControl.MouseMove -= OnChartMouseMove;
            ChartControl.MouseLeave -= OnChartMouseLeave;
            mouseHooked = false;
        }

        private void OnChartMouseMove(object sender, MouseEventArgs e)
        {
            if (!EnableTradeDots || renderedDots == null)
                return;

            Point p = e.GetPosition(ChartControl);
            TradePrint hit = null;

            lock (syncRoot)
            {
                for (int i = 0; i < renderedDots.Count; i++)
                {
                    RenderedDot d = renderedDots[i];
                    double dx = p.X - d.X;
                    double dy = p.Y - d.Y;
                    if (dx * dx + dy * dy <= d.Radius * d.Radius)
                    {
                        hit = d.Print;
                        break;
                    }
                }
            }

            double moveDx = p.X - hoverPoint.X;
            double moveDy = p.Y - hoverPoint.Y;
            bool movedEnough = (moveDx * moveDx + moveDy * moveDy) >= 4.0;
            bool changed = !ReferenceEquals(hit, hoveredPrint) || movedEnough;
            hoveredPrint = hit;
            hoverPoint = p;
            if (changed)
                ForceRefresh();
        }

        private void OnChartMouseLeave(object sender, MouseEventArgs e)
        {
            if (hoveredPrint == null)
                return;

            hoveredPrint = null;
            ForceRefresh();
        }
        #endregion

        #region Helpers
        private void EnsureSnapshotCapacity()
        {
            int capacity = Math.Max(600, HistorySnapshots);
            var replacement = new DepthSnapshot[capacity];
            int copy = Math.Min(snapshotsCount, capacity);
            for (int i = 0; i < copy; i++)
                replacement[i] = GetSnapshotAt(snapshotsCount - copy + i);
            snapshots = replacement;
            snapshotsCount = copy;
            snapshotsHead = copy % capacity;
        }

        private void AppendSnapshot(DateTime timeUtc)
        {
            if (snapshots == null || snapshots.Length != HistorySnapshots)
                EnsureSnapshotCapacity();

            var bids = new Dictionary<double, long>(bidBook.Count);
            foreach (var kv in bidBook)
                bids[kv.Key] = kv.Value;

            var asks = new Dictionary<double, long>(askBook.Count);
            foreach (var kv in askBook)
                asks[kv.Key] = kv.Value;

            snapshots[snapshotsHead] = new DepthSnapshot
            {
                Time = timeUtc,
                BidSizes = bids,
                AskSizes = asks
            };

            snapshotsHead = (snapshotsHead + 1) % snapshots.Length;
            if (snapshotsCount < snapshots.Length)
                snapshotsCount++;
        }

        private DepthSnapshot GetSnapshotAt(int chronologicalIndex)
        {
            if (snapshots == null || snapshotsCount == 0 || chronologicalIndex < 0 || chronologicalIndex >= snapshotsCount)
                return null;

            int start = (snapshotsHead - snapshotsCount + snapshots.Length) % snapshots.Length;
            int index = (start + chronologicalIndex) % snapshots.Length;
            return snapshots[index];
        }

        private void AddQuotePoint(DateTime timeUtc)
        {
            if (quotePoints == null)
                return;

            quotePoints.Add(new QuotePoint { Time = timeUtc, Bid = currentBid, Ask = currentAsk });
            if (quotePoints.Count > MaxQuotePoints)
                quotePoints.RemoveRange(0, quotePoints.Count - MaxQuotePoints);
        }

        private void BuildHeatmapBrushes()
        {
            if (dxHeatBrushes != null)
            {
                for (int i = 0; i < dxHeatBrushes.Length; i++)
                    DisposeDx(ref dxHeatBrushes[i]);
            }

            dxHeatBrushes = new SolidColorBrush[64];
            for (int i = 0; i < dxHeatBrushes.Length; i++)
            {
                float t = (float)i / (dxHeatBrushes.Length - 1);
                long synthetic = (long)(t * Math.Max(1, MaxSizeThreshold));
                dxHeatBrushes[i] = new SolidColorBrush(RenderTarget, SizeToColor(synthetic));
            }
        }

        private int SizeToBrushIndex(long size)
        {
            if (dxHeatBrushes == null || dxHeatBrushes.Length == 0)
                return 0;
            if (size <= 0)
                return 0;
            double ratio = (double)Math.Min(size, MaxSizeThreshold) / Math.Max(1, MaxSizeThreshold);
            int idx = (int)Math.Round(ratio * (dxHeatBrushes.Length - 1));
            if (idx < 0) idx = 0;
            if (idx >= dxHeatBrushes.Length) idx = dxHeatBrushes.Length - 1;
            return idx;
        }

        private float TimeToX(DateTime t, DateTime first, DateTime last, float leftX, float rightX)
        {
            if (last <= first)
                return rightX;
            double ratio = (t - first).TotalMilliseconds / (last - first).TotalMilliseconds;
            if (ratio < 0d) ratio = 0d;
            if (ratio > 1d) ratio = 1d;
            return leftX + (float)(ratio * (rightX - leftX));
        }

        private Color4 ToDxColor4(Brush brush, float alpha)
        {
            SolidColorBrush solid = brush as SolidColorBrush;
            if (solid == null)
                solid = Brushes.White as SolidColorBrush;
            var c = solid.Color;
            return new Color4(c.R / 255f, c.G / 255f, c.B / 255f, alpha);
        }

        private string FormatPrice(double price)
        {
            return Instrument != null ? Instrument.MasterInstrument.FormatPrice(price) : price.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private void DisposeDxResources()
        {
            if (dxHeatBrushes != null)
            {
                for (int i = 0; i < dxHeatBrushes.Length; i++)
                    DisposeDx(ref dxHeatBrushes[i]);
                dxHeatBrushes = null;
            }

            DisposeDx(ref dxWallBrush);
            DisposeDx(ref dxBidLineBrush);
            DisposeDx(ref dxAskLineBrush);
            DisposeDx(ref dxBuyDotBrush);
            DisposeDx(ref dxSellDotBrush);
            DisposeDx(ref dxBidLadderBrush);
            DisposeDx(ref dxAskLadderBrush);
            DisposeDx(ref dxWhiteBrush);
            DisposeDx(ref dxTooltipBackgroundBrush);
            DisposeDx(ref dxTooltipBorderBrush);
            DisposeDx(ref dxBrandingBrush);
            DisposeDx(ref dxBestBidFillBrush);
            DisposeDx(ref dxBestAskFillBrush);

            if (tfWall != null) { tfWall.Dispose(); tfWall = null; }
            if (tfLadder != null) { tfLadder.Dispose(); tfLadder = null; }
            if (tfTooltip != null) { tfTooltip.Dispose(); tfTooltip = null; }
            if (tfTooltipSmall != null) { tfTooltipSmall.Dispose(); tfTooltipSmall = null; }
            if (tfBranding != null) { tfBranding.Dispose(); tfBranding = null; }
        }

        private void DisposeDx<T>(ref T item) where T : class, IDisposable
        {
            if (item != null)
            {
                item.Dispose();
                item = null;
            }
        }

        private Color4 SizeToColor(long size)
        {
            if (size <= 0)
                return new Color4(0f, 0f, 0f, 0.03f);

            if (size <= LowSizeThreshold)
            {
                float t = LowSizeThreshold <= 0 ? 1f : (float)size / LowSizeThreshold;
                return Lerp(new Color4(0f, 0f, 0f, 0.03f), new Color4(0f, 0.65f, 0.65f, 0.22f), t);
            }

            if (size <= MidSizeThreshold)
            {
                float t = MidSizeThreshold <= LowSizeThreshold ? 1f : (float)(size - LowSizeThreshold) / (MidSizeThreshold - LowSizeThreshold);
                return Lerp(new Color4(0f, 0.65f, 0.65f, 0.22f), new Color4(0.2f, 0.95f, 0.15f, 0.38f), t);
            }

            if (size <= MaxSizeThreshold)
            {
                float t = MaxSizeThreshold <= MidSizeThreshold ? 1f : (float)(size - MidSizeThreshold) / (MaxSizeThreshold - MidSizeThreshold);
                return Lerp(new Color4(0.20f, 0.95f, 0.15f, 0.38f), new Color4(1f, 0.48f, 0.05f, 0.68f), t);
            }

            return new Color4(1f, 0.10f, 0.05f, 0.80f);
        }

        private Color4 Lerp(Color4 a, Color4 b, float t)
        {
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            return new Color4(
                a.Red + (b.Red - a.Red) * t,
                a.Green + (b.Green - a.Green) * t,
                a.Blue + (b.Blue - a.Blue) * t,
                a.Alpha + (b.Alpha - a.Alpha) * t);
        }

        private float SizeToRadius(long size)
        {
            if (size <= DotMinSize)
                return MinDotRadius;

            double scaled = MinDotRadius + Math.Log10(Math.Max(1d, (double)size / Math.Max(1, DotMinSize))) * 12d;
            if (scaled < MinDotRadius)
                scaled = MinDotRadius;
            if (scaled > MaxDotRadius)
                scaled = MaxDotRadius;
            return (float)scaled;
        }

        private double SnapToTick(double price)
        {
            if (TickSize <= 0)
                return price;
            return Math.Round(price / TickSize, MidpointRounding.AwayFromZero) * TickSize;
        }
        #endregion

        #region Persistence
        private string SaveTradePrints()
        {
            if (tradePrints == null || tradePrints.Count == 0)
                return string.Empty;

            var sb = new StringBuilder(4096);
            int start = Math.Max(0, tradePrints.Count - PersistTradePrints);
            for (int i = start; i < tradePrints.Count; i++)
            {
                TradePrint p = tradePrints[i];
                sb.Append(p.Time.Ticks).Append(',')
                  .Append(p.Price.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append((int)p.Side).Append(',')
                  .Append(p.Size)
                  .Append(';');
            }
            return sb.ToString();
        }

        private void RestoreTradePrints(string payload)
        {
            if (tradePrints == null)
                tradePrints = new List<TradePrint>(2048);
            else
                tradePrints.Clear();

            if (string.IsNullOrEmpty(payload))
                return;

            string[] rows = payload.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < rows.Length; i++)
            {
                string[] parts = rows[i].Split(',');
                if (parts.Length != 4)
                    continue;

                long ticks;
                double price;
                int side;
                long size;
                if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks))
                    continue;
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out price))
                    continue;
                if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out side))
                    continue;
                if (!long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out size))
                    continue;

                tradePrints.Add(new TradePrint
                {
                    Time = new DateTime(ticks, DateTimeKind.Utc),
                    Price = price,
                    Side = side == 0 ? TradeSide.Buy : TradeSide.Sell,
                    Size = size
                });
            }

            if (tradePrints.Count > MaxTradePrints)
                tradePrints.RemoveRange(0, tradePrints.Count - MaxTradePrints);
        }

        [Browsable(false)]
        public string TradePrintsSerializable
        {
            get { return SaveTradePrints(); }
            set { RestoreTradePrints(value); }
        }
        #endregion

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Enable Heatmap", GroupName = "Heatmap", Order = 1)]
        public bool EnableHeatmap { get; set; }

        [NinjaScriptProperty]
        [Range(5, 100)]
        [Display(Name = "Visible Price Levels", GroupName = "Heatmap", Order = 2)]
        public int VisiblePriceLevels { get; set; }

        [NinjaScriptProperty]
        [Range(50, 1000)]
        [Display(Name = "Snapshot Interval Ms", GroupName = "Heatmap", Order = 3)]
        public int SnapshotIntervalMs { get; set; }

        [NinjaScriptProperty]
        [Range(600, 100000)]
        [Display(Name = "History Snapshots", GroupName = "Heatmap", Order = 4)]
        public int HistorySnapshots { get; set; }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Background Color", GroupName = "Heatmap", Order = 5)]
        public Brush BackgroundColor { get; set; }

        [Browsable(false)]
        public string BackgroundColorSerializable
        {
            get { return Serialize.BrushToString(BackgroundColor); }
            set { BackgroundColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Low Size Threshold", GroupName = "Heatmap", Order = 6)]
        public int LowSizeThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Mid Size Threshold", GroupName = "Heatmap", Order = 7)]
        public int MidSizeThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max Size Threshold", GroupName = "Heatmap", Order = 8)]
        public int MaxSizeThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Walls", GroupName = "Walls", Order = 1)]
        public bool EnableWalls { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Wall Threshold", GroupName = "Walls", Order = 2)]
        public int WallThreshold { get; set; }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Wall Color", GroupName = "Walls", Order = 3)]
        public Brush WallColor { get; set; }

        [Browsable(false)]
        public string WallColorSerializable
        {
            get { return Serialize.BrushToString(WallColor); }
            set { WallColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Enable Trade Dots", GroupName = "Trade Prints (Dots)", Order = 1)]
        public bool EnableTradeDots { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Dot Min Size", GroupName = "Trade Prints (Dots)", Order = 2)]
        public int DotMinSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Min Dot Radius", GroupName = "Trade Prints (Dots)", Order = 3)]
        public int MinDotRadius { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max Dot Radius", GroupName = "Trade Prints (Dots)", Order = 4)]
        public int MaxDotRadius { get; set; }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Buy Dot Color", GroupName = "Trade Prints (Dots)", Order = 5)]
        public Brush BuyDotColor { get; set; }

        [Browsable(false)]
        public string BuyDotColorSerializable
        {
            get { return Serialize.BrushToString(BuyDotColor); }
            set { BuyDotColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Sell Dot Color", GroupName = "Trade Prints (Dots)", Order = 6)]
        public Brush SellDotColor { get; set; }

        [Browsable(false)]
        public string SellDotColorSerializable
        {
            get { return Serialize.BrushToString(SellDotColor); }
            set { SellDotColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Enable Bid Ask Line", GroupName = "Bid/Ask Line", Order = 1)]
        public bool EnableBidAskLine { get; set; }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Bid Line Color", GroupName = "Bid/Ask Line", Order = 2)]
        public Brush BidLineColor { get; set; }

        [Browsable(false)]
        public string BidLineColorSerializable
        {
            get { return Serialize.BrushToString(BidLineColor); }
            set { BidLineColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Ask Line Color", GroupName = "Bid/Ask Line", Order = 3)]
        public Brush AskLineColor { get; set; }

        [Browsable(false)]
        public string AskLineColorSerializable
        {
            get { return Serialize.BrushToString(AskLineColor); }
            set { AskLineColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Show Forward Extension", GroupName = "Bid/Ask Line", Order = 4)]
        public bool ShowForwardExtension { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Best Bid Ask Labels", GroupName = "Bid/Ask Line", Order = 5)]
        public bool ShowBestBidAskLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable DOM Ladder", GroupName = "DOM Ladder", Order = 1)]
        public bool EnableDomLadder { get; set; }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Bid Ladder Color", GroupName = "DOM Ladder", Order = 2)]
        public Brush BidLadderColor { get; set; }

        [Browsable(false)]
        public string BidLadderColorSerializable
        {
            get { return Serialize.BrushToString(BidLadderColor); }
            set { BidLadderColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Ask Ladder Color", GroupName = "DOM Ladder", Order = 3)]
        public Brush AskLadderColor { get; set; }

        [Browsable(false)]
        public string AskLadderColorSerializable
        {
            get { return Serialize.BrushToString(AskLadderColor); }
            set { AskLadderColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Show Branding Label", GroupName = "Display", Order = 1)]
        public bool ShowBrandingLabel { get; set; }
        #endregion
    }
}
