#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// DO NOT add "using SharpDX.Direct2D1" or "using SharpDX.DirectWrite" at the top level
// to avoid ambiguous type references with System.Windows.Media.
// Instead, fully qualify all SharpDX types inline (proven pattern from working NT8 indicators).

namespace NinjaTrader.NinjaScript.Indicators
{
    public class MarketStructureVolumeProfiles : Indicator
    {
        #region Enums
        public enum MSVPStructureType { BOS, CHoCH }
        public enum MSVPDirection { Bullish, Bearish }
        public enum MSVPProfileType { Split, Stacked }
        public enum MSVPCVDReset { CHoCH, BoSAndCHoCH, Day, Week }
        #endregion

        #region Private Classes
        private class SwingPoint
        {
            public int BarIndex { get; set; }
            public double Price { get; set; }
            public bool IsHigh { get; set; }
        }

        private class StructureEvent
        {
            public MSVPStructureType Type { get; set; }
            public MSVPDirection Direction { get; set; }
            public int StartBar { get; set; }
            public double Price { get; set; }
            public int EndBar { get; set; }
        }

        private class VolumeLevel
        {
            public double BuyVolume { get; set; }
            public double SellVolume { get; set; }
            public double TotalVolume { get { return BuyVolume + SellVolume; } }
        }

        private class SwingVolumeProfile
        {
            public int StartBar { get; set; }
            public int EndBar { get; set; }
            public double HighPrice { get; set; }
            public double LowPrice { get; set; }
            public double PriceStep { get; set; }
            public VolumeLevel[] Levels { get; set; }
            public int POCIndex { get; set; }
            public int VAHIndex { get; set; }
            public int VALIndex { get; set; }
            public double TotalVolume { get; set; }
            public double MaxLevelVolume { get; set; }
            public MSVPDirection Direction { get; set; }
            public MSVPStructureType StructureType { get; set; }
            public int LevelCount { get; set; }
        }
        #endregion

        #region Private Fields
        private List<SwingPoint> swingHighs;
        private List<SwingPoint> swingLows;
        private List<StructureEvent> structureEvents;
        private List<SwingVolumeProfile> volumeProfiles;

        private MSVPDirection currentTrend;
        private bool trendEstablished;

        // CVD tracking
        private double cvdValue;
        private double cvdSegmentOpen;

        // SharpDX resources
        private SharpDX.Direct2D1.SolidColorBrush dxBullishBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBearishBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBuyVolBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxSellVolBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxPOCBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxVABrush;
        private SharpDX.DirectWrite.TextFormat dxLabelFormat;
        private bool dxResourcesCreated;
        #endregion

        #region State Management
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description             = @"Market Structure Volume Profiles — Combines ICT/SMC market structure (BoS/CHoCH) with volume profile analysis per swing. Original concept by KioseffTrading on TradingView. NinjaTrader 8 port by Dollars1bySTEVE.";
                Name                    = "MarketStructureVolumeProfiles";
                Calculate               = Calculate.OnBarClose;
                IsOverlay               = true;
                DisplayInDataBox        = false;
                DrawOnPricePanel        = true;
                PaintPriceMarkers       = false;
                IsSuspendedWhileInactive = true;

                // 1. Structure Detection
                SwingStrength           = 3;
                ConfirmationBars        = 1;
                ShowBoS                 = true;
                ShowCHoCH               = true;

                // 2. Appearance
                BullishColor            = Brushes.LimeGreen;
                BearishColor            = Brushes.Crimson;
                LineWidth               = 2;
                LineStyle               = DashStyleHelper.Dash;
                ShowLabels              = true;
                LabelFontSize           = 10;

                // 3. Volume Profile
                ShowVolumeProfile       = true;
                ProfileSize             = 100;
                ProfileWidthPct         = 25;
                ProfileType             = MSVPProfileType.Split;
                ProfileOpacity          = 60;
                BuyVolColor             = Brushes.DodgerBlue;
                SellVolColor            = Brushes.Tomato;
                MaxProfiles             = 10;

                // 4. POC & Value Area
                ShowPOC                 = true;
                POCColor                = Brushes.Yellow;
                ShowValueArea           = true;
                VAColor                 = Brushes.DimGray;
                ValueAreaPct            = 70;

                // 5. CVD
                ShowCVD                 = true;
                CVDResetMode            = MSVPCVDReset.CHoCH;
                CVDBullColor            = Brushes.LimeGreen;
                CVDBearColor            = Brushes.Crimson;

                AddPlot(new Stroke(Brushes.DimGray, 2), PlotStyle.Bar, "CVD");
            }
            else if (State == State.DataLoaded)
            {
                swingHighs          = new List<SwingPoint>();
                swingLows           = new List<SwingPoint>();
                structureEvents     = new List<StructureEvent>();
                volumeProfiles      = new List<SwingVolumeProfile>();
                trendEstablished    = false;
                cvdValue            = 0;
                cvdSegmentOpen      = 0;
            }
            else if (State == State.Terminated)
            {
                DisposeSharpDXResources();
            }
        }
        #endregion

        #region Bar Update
        protected override void OnBarUpdate()
        {
            if (CurrentBar < SwingStrength * 2 + 2)
                return;

            CheckTimedCVDReset();
            UpdateCVD();
            DetectSwingPoints();
            DetectStructureBreaks();
        }
        #endregion

        #region CVD
        private void CheckTimedCVDReset()
        {
            if (CurrentBar < 1) return;

            if (CVDResetMode == MSVPCVDReset.Day)
            {
                // Reset at the start of each new trading day
                if (Time[0].Date != Time[1].Date)
                    ResetCVD();
            }
            else if (CVDResetMode == MSVPCVDReset.Week)
            {
                // Reset at the start of each new trading week (Monday or day after Sunday)
                DayOfWeek prev = Time[1].DayOfWeek;
                DayOfWeek curr = Time[0].DayOfWeek;
                bool newWeek = (curr == DayOfWeek.Monday && prev != DayOfWeek.Monday) ||
                               (Time[0].Date - Time[1].Date).TotalDays >= 2;
                if (newWeek)
                    ResetCVD();
            }
        }

        private void UpdateCVD()
        {
            bool isUpBar = Close[0] >= Open[0];
            double barVol = Volume[0];
            double delta = isUpBar ? barVol : -barVol;
            cvdValue += delta;

            if (ShowCVD)
            {
                Values[0][0] = cvdValue;
                PlotBrushes[0][0] = cvdValue >= cvdSegmentOpen ? CVDBullColor : CVDBearColor;
            }
            else
            {
                Values[0][0] = 0;
            }
        }

        private void ResetCVD()
        {
            cvdSegmentOpen = cvdValue;
        }
        #endregion

        #region Swing Point Detection
        private void DetectSwingPoints()
        {
            int lookback     = SwingStrength;
            int candidateBar = CurrentBar - lookback;

            if (candidateBar < lookback)
                return;

            // Swing High
            double candidateHigh = High.GetValueAt(candidateBar);
            bool isSwingHigh = true;
            for (int i = 1; i <= lookback; i++)
            {
                if (High.GetValueAt(candidateBar - i) >= candidateHigh ||
                    High.GetValueAt(candidateBar + i) >= candidateHigh)
                {
                    isSwingHigh = false;
                    break;
                }
            }
            if (isSwingHigh && (swingHighs.Count == 0 || swingHighs[swingHighs.Count - 1].BarIndex != candidateBar))
                swingHighs.Add(new SwingPoint { BarIndex = candidateBar, Price = candidateHigh, IsHigh = true });

            // Swing Low
            double candidateLow = Low.GetValueAt(candidateBar);
            bool isSwingLow = true;
            for (int i = 1; i <= lookback; i++)
            {
                if (Low.GetValueAt(candidateBar - i) <= candidateLow ||
                    Low.GetValueAt(candidateBar + i) <= candidateLow)
                {
                    isSwingLow = false;
                    break;
                }
            }
            if (isSwingLow && (swingLows.Count == 0 || swingLows[swingLows.Count - 1].BarIndex != candidateBar))
                swingLows.Add(new SwingPoint { BarIndex = candidateBar, Price = candidateLow, IsHigh = false });
        }
        #endregion

        #region Structure Break Detection
        private void DetectStructureBreaks()
        {
            if (swingHighs.Count < 2 && swingLows.Count < 2)
                return;

            if (!trendEstablished)
            {
                if (swingHighs.Count >= 2 && swingLows.Count >= 2)
                {
                    var lastSH = swingHighs[swingHighs.Count - 1];
                    var prevSH = swingHighs[swingHighs.Count - 2];
                    var lastSL = swingLows[swingLows.Count - 1];
                    var prevSL = swingLows[swingLows.Count - 2];

                    if (lastSH.Price > prevSH.Price && lastSL.Price > prevSL.Price)
                        currentTrend = MSVPDirection.Bullish;
                    else if (lastSH.Price < prevSH.Price && lastSL.Price < prevSL.Price)
                        currentTrend = MSVPDirection.Bearish;
                    else
                        currentTrend = MSVPDirection.Bullish;

                    trendEstablished = true;
                }
                else
                    return;
            }

            // Check bullish break (break above swing high)
            if (swingHighs.Count >= 1)
            {
                var lastHigh = swingHighs[swingHighs.Count - 1];
                if (HasConfirmedBreakAbove(lastHigh.Price, lastHigh.BarIndex) &&
                    !EventExistsAt(lastHigh.Price, MSVPDirection.Bullish))
                {
                    int breakBar = FindBreakBarAbove(lastHigh.Price, lastHigh.BarIndex);
                    if (breakBar > 0)
                    {
                        var type        = currentTrend == MSVPDirection.Bullish ? MSVPStructureType.BOS : MSVPStructureType.CHoCH;
                        bool trendFlip  = (type == MSVPStructureType.CHoCH);
                        if (trendFlip) currentTrend = MSVPDirection.Bullish;

                        structureEvents.Add(new StructureEvent
                        {
                            Type        = type,
                            Direction   = MSVPDirection.Bullish,
                            StartBar    = lastHigh.BarIndex,
                            Price       = lastHigh.Price,
                            EndBar      = breakBar
                        });

                        bool shouldReset = CVDResetMode == MSVPCVDReset.BoSAndCHoCH ||
                                           (CVDResetMode == MSVPCVDReset.CHoCH && trendFlip);
                        if (shouldReset) ResetCVD();

                        BuildVolumeProfile(lastHigh.BarIndex, breakBar, MSVPDirection.Bullish, type);
                    }
                }
            }

            // Check bearish break (break below swing low)
            if (swingLows.Count >= 1)
            {
                var lastLow = swingLows[swingLows.Count - 1];
                if (HasConfirmedBreakBelow(lastLow.Price, lastLow.BarIndex) &&
                    !EventExistsAt(lastLow.Price, MSVPDirection.Bearish))
                {
                    int breakBar = FindBreakBarBelow(lastLow.Price, lastLow.BarIndex);
                    if (breakBar > 0)
                    {
                        var type        = currentTrend == MSVPDirection.Bearish ? MSVPStructureType.BOS : MSVPStructureType.CHoCH;
                        bool trendFlip  = (type == MSVPStructureType.CHoCH);
                        if (trendFlip) currentTrend = MSVPDirection.Bearish;

                        structureEvents.Add(new StructureEvent
                        {
                            Type        = type,
                            Direction   = MSVPDirection.Bearish,
                            StartBar    = lastLow.BarIndex,
                            Price       = lastLow.Price,
                            EndBar      = breakBar
                        });

                        bool shouldReset = CVDResetMode == MSVPCVDReset.BoSAndCHoCH ||
                                           (CVDResetMode == MSVPCVDReset.CHoCH && trendFlip);
                        if (shouldReset) ResetCVD();

                        BuildVolumeProfile(lastLow.BarIndex, breakBar, MSVPDirection.Bearish, type);
                    }
                }
            }
        }
        #endregion

        #region Volume Profile Calculation
        private void BuildVolumeProfile(int startBar, int endBar, MSVPDirection direction, MSVPStructureType structType)
        {
            if (endBar <= startBar || ProfileSize < 5)
                return;

            int barCount = Bars.Count;

            // Determine price range of swing
            double highPrice = double.MinValue;
            double lowPrice  = double.MaxValue;
            for (int i = startBar; i <= endBar && i < barCount; i++)
            {
                double h = High.GetValueAt(i);
                double l = Low.GetValueAt(i);
                if (h > highPrice) highPrice = h;
                if (l < lowPrice)  lowPrice  = l;
            }

            if (highPrice <= lowPrice)
                return;

            double priceStep = (highPrice - lowPrice) / ProfileSize;
            if (priceStep <= 0)
                return;

            var levels = new VolumeLevel[ProfileSize];
            for (int li = 0; li < ProfileSize; li++)
                levels[li] = new VolumeLevel();

            // Accumulate volume into price levels
            for (int i = startBar; i <= endBar && i < barCount; i++)
            {
                double barHigh  = High.GetValueAt(i);
                double barLow   = Low.GetValueAt(i);
                double barVol   = Volume.GetValueAt(i);
                double barClose = Close.GetValueAt(i);
                double barOpen  = Open.GetValueAt(i);
                bool   isUpBar  = barClose >= barOpen;
                double barRange = barHigh - barLow;

                if (barVol <= 0) continue;

                // Find the level indices that overlap with this bar's range
                int liLow  = (int)Math.Floor((barLow  - lowPrice) / priceStep);
                int liHigh = (int)Math.Floor((barHigh - lowPrice) / priceStep);
                liLow  = Math.Max(0, liLow);
                liHigh = Math.Min(ProfileSize - 1, liHigh);

                if (liLow > liHigh) continue;

                int    numLevels = liHigh - liLow + 1;
                double volPerLevel = barRange > 0
                    ? barVol * (priceStep / barRange)
                    : barVol / numLevels;

                for (int li = liLow; li <= liHigh; li++)
                {
                    if (isUpBar) levels[li].BuyVolume  += volPerLevel;
                    else         levels[li].SellVolume += volPerLevel;
                }
            }

            // Find POC (max volume level)
            double maxVol  = 0;
            double totVol  = 0;
            int    pocIdx  = 0;
            for (int li = 0; li < ProfileSize; li++)
            {
                double lv = levels[li].TotalVolume;
                totVol += lv;
                if (lv > maxVol) { maxVol = lv; pocIdx = li; }
            }

            if (totVol <= 0) return;

            // Calculate Value Area (expanding outward from POC)
            double vaTarget = totVol * (ValueAreaPct / 100.0);
            double vaVol    = maxVol;
            int    vahIdx   = pocIdx;
            int    valIdx   = pocIdx;

            while (vaVol < vaTarget)
            {
                int    nextUp  = vahIdx + 1;
                int    nextDn  = valIdx - 1;
                double upVol   = nextUp < ProfileSize ? levels[nextUp].TotalVolume : 0;
                double dnVol   = nextDn >= 0           ? levels[nextDn].TotalVolume : 0;

                if (upVol <= 0 && dnVol <= 0) break;

                if (upVol >= dnVol && nextUp < ProfileSize)
                { vahIdx = nextUp; vaVol += upVol; }
                else if (nextDn >= 0)
                { valIdx = nextDn; vaVol += dnVol; }
                else if (nextUp < ProfileSize)
                { vahIdx = nextUp; vaVol += upVol; }
                else
                    break;
            }

            var profile = new SwingVolumeProfile
            {
                StartBar        = startBar,
                EndBar          = endBar,
                HighPrice       = highPrice,
                LowPrice        = lowPrice,
                PriceStep       = priceStep,
                Levels          = levels,
                POCIndex        = pocIdx,
                VAHIndex        = vahIdx,
                VALIndex        = valIdx,
                TotalVolume     = totVol,
                MaxLevelVolume  = maxVol,
                Direction       = direction,
                StructureType   = structType,
                LevelCount      = ProfileSize
            };

            volumeProfiles.Add(profile);

            // Keep only the most recent N profiles
            int maxP = Math.Max(1, MaxProfiles);
            while (volumeProfiles.Count > maxP)
                volumeProfiles.RemoveAt(0);
        }
        #endregion

        #region Confirmation Helpers
        private bool HasConfirmedBreakAbove(double price, int swingBar)
        {
            int consecutive = 0;
            for (int i = CurrentBar; i > swingBar; i--)
            {
                if (Close.GetValueAt(i) > price)
                {
                    consecutive++;
                    if (consecutive >= ConfirmationBars) return true;
                }
                else
                    consecutive = 0;
            }
            return false;
        }

        private bool HasConfirmedBreakBelow(double price, int swingBar)
        {
            int consecutive = 0;
            for (int i = CurrentBar; i > swingBar; i--)
            {
                if (Close.GetValueAt(i) < price)
                {
                    consecutive++;
                    if (consecutive >= ConfirmationBars) return true;
                }
                else
                    consecutive = 0;
            }
            return false;
        }

        private int FindBreakBarAbove(double price, int swingBar)
        {
            for (int i = swingBar + 1; i <= CurrentBar; i++)
                if (Close.GetValueAt(i) > price) return i;
            return -1;
        }

        private int FindBreakBarBelow(double price, int swingBar)
        {
            for (int i = swingBar + 1; i <= CurrentBar; i++)
                if (Close.GetValueAt(i) < price) return i;
            return -1;
        }

        private bool EventExistsAt(double price, MSVPDirection direction)
        {
            int start = Math.Max(0, structureEvents.Count - 30);
            for (int i = structureEvents.Count - 1; i >= start; i--)
            {
                var e = structureEvents[i];
                if (Math.Abs(e.Price - price) < TickSize && e.Direction == direction)
                    return true;
            }
            return false;
        }
        #endregion

        #region SharpDX Rendering
        private SharpDX.Color4 WpfBrushToColor4(System.Windows.Media.Brush wpfBrush, float alpha)
        {
            var c = ((System.Windows.Media.SolidColorBrush)wpfBrush).Color;
            return new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, alpha);
        }

        private void CreateSharpDXResources(SharpDX.Direct2D1.RenderTarget rt)
        {
            if (dxResourcesCreated) return;

            float profAlpha = ProfileOpacity / 100f;

            dxBullishBrush  = new SharpDX.Direct2D1.SolidColorBrush(rt, WpfBrushToColor4(BullishColor, 1f));
            dxBearishBrush  = new SharpDX.Direct2D1.SolidColorBrush(rt, WpfBrushToColor4(BearishColor, 1f));
            dxBuyVolBrush   = new SharpDX.Direct2D1.SolidColorBrush(rt, WpfBrushToColor4(BuyVolColor,  profAlpha));
            dxSellVolBrush  = new SharpDX.Direct2D1.SolidColorBrush(rt, WpfBrushToColor4(SellVolColor, profAlpha));
            dxPOCBrush      = new SharpDX.Direct2D1.SolidColorBrush(rt, WpfBrushToColor4(POCColor, 1f));
            dxVABrush       = new SharpDX.Direct2D1.SolidColorBrush(rt, WpfBrushToColor4(VAColor, 0.8f));

            int clampedSize = Math.Max(8, Math.Min(LabelFontSize, 14));
            dxLabelFormat = new SharpDX.DirectWrite.TextFormat(
                NinjaTrader.Core.Globals.DirectWriteFactory,
                "Arial",
                SharpDX.DirectWrite.FontWeight.Bold,
                SharpDX.DirectWrite.FontStyle.Normal,
                SharpDX.DirectWrite.FontStretch.Normal,
                clampedSize);
            dxLabelFormat.TextAlignment          = SharpDX.DirectWrite.TextAlignment.Center;
            dxLabelFormat.ParagraphAlignment     = SharpDX.DirectWrite.ParagraphAlignment.Center;

            dxResourcesCreated = true;
        }

        private void DisposeSharpDXResources()
        {
            if (dxBullishBrush  != null) { dxBullishBrush.Dispose();  dxBullishBrush  = null; }
            if (dxBearishBrush  != null) { dxBearishBrush.Dispose();  dxBearishBrush  = null; }
            if (dxBuyVolBrush   != null) { dxBuyVolBrush.Dispose();   dxBuyVolBrush   = null; }
            if (dxSellVolBrush  != null) { dxSellVolBrush.Dispose();  dxSellVolBrush  = null; }
            if (dxPOCBrush      != null) { dxPOCBrush.Dispose();      dxPOCBrush      = null; }
            if (dxVABrush       != null) { dxVABrush.Dispose();       dxVABrush       = null; }
            if (dxLabelFormat   != null) { dxLabelFormat.Dispose();   dxLabelFormat   = null; }
            dxResourcesCreated = false;
        }

        public override void OnRenderTargetChanged()
        {
            DisposeSharpDXResources();
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (chartControl == null || chartScale == null || ChartBars == null || Bars == null)
                return;

            var rt = RenderTarget;
            if (rt == null) return;

            if (!dxResourcesCreated)
                CreateSharpDXResources(rt);

            int   firstBar    = ChartBars.FromIndex;
            int   lastBar     = ChartBars.ToIndex;
            float canvasLeft  = (float)chartControl.CanvasLeft;
            float canvasRight = (float)chartControl.CanvasRight;
            float canvasWidth = canvasRight - canvasLeft;

            if (canvasWidth <= 0) return;

            // Draw volume profiles first (behind structure lines)
            if (ShowVolumeProfile)
                RenderVolumeProfiles(rt, chartControl, chartScale, firstBar, lastBar, canvasWidth);

            // Draw structure events on top
            RenderStructureEvents(rt, chartControl, chartScale, firstBar, lastBar, canvasRight);
        }

        private void RenderStructureEvents(
            SharpDX.Direct2D1.RenderTarget rt,
            ChartControl cc, ChartScale cs,
            int firstBar, int lastBar,
            float canvasRight)
        {
            if (structureEvents == null) return;

            foreach (var evt in structureEvents)
            {
                if (!ShowBoS   && evt.Type == MSVPStructureType.BOS)   continue;
                if (!ShowCHoCH && evt.Type == MSVPStructureType.CHoCH) continue;
                if (evt.EndBar < firstBar || evt.StartBar > lastBar)   continue;

                float xStart = cc.GetXByBarIndex(ChartBars, Math.Max(evt.StartBar, firstBar));
                float xEnd   = cc.GetXByBarIndex(ChartBars, Math.Min(evt.EndBar,   lastBar));
                float y      = cs.GetYByValue(evt.Price);

                var lineBrush = evt.Direction == MSVPDirection.Bullish ? dxBullishBrush : dxBearishBrush;

                // Draw dashed structure line
                if (LineStyle != DashStyleHelper.Solid)
                {
                    var strokeProps = new SharpDX.Direct2D1.StrokeStyleProperties();
                    switch (LineStyle)
                    {
                        case DashStyleHelper.Dash:       strokeProps.DashStyle = SharpDX.Direct2D1.DashStyle.Dash;       break;
                        case DashStyleHelper.DashDot:    strokeProps.DashStyle = SharpDX.Direct2D1.DashStyle.DashDot;    break;
                        case DashStyleHelper.DashDotDot: strokeProps.DashStyle = SharpDX.Direct2D1.DashStyle.DashDotDot; break;
                        case DashStyleHelper.Dot:        strokeProps.DashStyle = SharpDX.Direct2D1.DashStyle.Dot;        break;
                        default:                         strokeProps.DashStyle = SharpDX.Direct2D1.DashStyle.Dash;       break;
                    }
                    using (var style = new SharpDX.Direct2D1.StrokeStyle(rt.Factory, strokeProps))
                        rt.DrawLine(new SharpDX.Vector2(xStart, y), new SharpDX.Vector2(xEnd, y), lineBrush, LineWidth, style);
                }
                else
                    rt.DrawLine(new SharpDX.Vector2(xStart, y), new SharpDX.Vector2(xEnd, y), lineBrush, LineWidth);

                // Draw label
                if (ShowLabels && dxLabelFormat != null)
                {
                    string label  = evt.Type == MSVPStructureType.BOS ? "BoS" : "CHoCH";
                    float  labelX = (xStart + xEnd) / 2f;
                    float  labelY = evt.Direction == MSVPDirection.Bullish ? y - 20f : y + 4f;

                    using (var tl = new SharpDX.DirectWrite.TextLayout(
                        NinjaTrader.Core.Globals.DirectWriteFactory, label, dxLabelFormat, 80f, 20f))
                    {
                        rt.DrawTextLayout(new SharpDX.Vector2(labelX - 40f, labelY), tl, lineBrush);
                    }
                }
            }
        }

        private void RenderVolumeProfiles(
            SharpDX.Direct2D1.RenderTarget rt,
            ChartControl cc, ChartScale cs,
            int firstBar, int lastBar,
            float canvasWidth)
        {
            if (volumeProfiles == null) return;

            foreach (var profile in volumeProfiles)
            {
                if (profile.EndBar < firstBar || profile.StartBar > lastBar) continue;
                if (profile.TotalVolume <= 0 || profile.MaxLevelVolume <= 0) continue;

                // Anchor the profile at the right edge of the swing range
                int   anchorBar   = Math.Min(profile.EndBar, lastBar);
                float xRight      = cc.GetXByBarIndex(ChartBars, anchorBar);
                float profileWidth = canvasWidth * (ProfileWidthPct / 100f);
                float xLeft       = xRight - profileWidth;
                if (xLeft < (float)cc.CanvasLeft) xLeft = (float)cc.CanvasLeft;
                if (xRight <= xLeft) continue;

                float availWidth  = xRight - xLeft;

                // Draw each price level
                for (int li = 0; li < profile.LevelCount; li++)
                {
                    var level = profile.Levels[li];
                    if (level.TotalVolume <= 0) continue;

                    double priceLow  = profile.LowPrice + li * profile.PriceStep;
                    double priceHigh = priceLow + profile.PriceStep;

                    float yTop = cs.GetYByValue(priceHigh);
                    float yBot = cs.GetYByValue(priceLow);
                    if (yTop > yBot) { float tmp = yTop; yTop = yBot; yBot = tmp; }
                    float barH = Math.Max(1f, yBot - yTop);

                    float totalBarWidth = availWidth * (float)(level.TotalVolume / profile.MaxLevelVolume);

                    if (ProfileType == MSVPProfileType.Split)
                    {
                        // Split: buy volume bars anchor right, sell volume bars extend left
                        float buyFrac  = (float)(level.TotalVolume > 0 ? level.BuyVolume  / level.TotalVolume : 0.5);
                        float buyW     = totalBarWidth * buyFrac;
                        float sellW    = totalBarWidth - buyW;

                        if (buyW > 0)
                        {
                            var rect = new SharpDX.RectangleF(xRight - buyW, yTop, buyW, barH);
                            rt.FillRectangle(rect, dxBuyVolBrush);
                        }
                        if (sellW > 0)
                        {
                            var rect = new SharpDX.RectangleF(xRight - buyW - sellW, yTop, sellW, barH);
                            rt.FillRectangle(rect, dxSellVolBrush);
                        }
                    }
                    else
                    {
                        // Stacked: single bar colored by swing direction
                        var brush = profile.Direction == MSVPDirection.Bullish ? dxBuyVolBrush : dxSellVolBrush;
                        var rect  = new SharpDX.RectangleF(xRight - totalBarWidth, yTop, totalBarWidth, barH);
                        rt.FillRectangle(rect, brush);
                    }
                }

                // Draw POC line
                if (ShowPOC)
                {
                    double pocPrice = profile.LowPrice + (profile.POCIndex + 0.5) * profile.PriceStep;
                    float  yPOC    = cs.GetYByValue(pocPrice);
                    rt.DrawLine(new SharpDX.Vector2(xLeft, yPOC), new SharpDX.Vector2(xRight, yPOC), dxPOCBrush, 2f);
                }

                // Draw Value Area High / Low lines
                if (ShowValueArea)
                {
                    double vahPrice = profile.LowPrice + (profile.VAHIndex + 1.0) * profile.PriceStep;
                    double valPrice = profile.LowPrice + profile.VALIndex * profile.PriceStep;
                    float  yVAH    = cs.GetYByValue(vahPrice);
                    float  yVAL    = cs.GetYByValue(valPrice);
                    rt.DrawLine(new SharpDX.Vector2(xLeft, yVAH), new SharpDX.Vector2(xRight, yVAH), dxVABrush, 1f);
                    rt.DrawLine(new SharpDX.Vector2(xLeft, yVAL), new SharpDX.Vector2(xRight, yVAL), dxVABrush, 1f);
                }
            }
        }
        #endregion

        #region User Properties

        // ── 1. Structure Detection ──────────────────────────────────────────────
        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Swing Strength",
                 Description = "Number of bars on each side required to confirm a swing high/low.",
                 Order = 1, GroupName = "1. Structure Detection")]
        public int SwingStrength { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Confirmation Bars",
                 Description = "Consecutive closes beyond the swing level needed to confirm the break.",
                 Order = 2, GroupName = "1. Structure Detection")]
        public int ConfirmationBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show BoS",
                 Description = "Display Break of Structure lines and labels.",
                 Order = 3, GroupName = "1. Structure Detection")]
        public bool ShowBoS { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show CHoCH",
                 Description = "Display Change of Character lines and labels.",
                 Order = 4, GroupName = "1. Structure Detection")]
        public bool ShowCHoCH { get; set; }

        // ── 2. Appearance ───────────────────────────────────────────────────────
        [XmlIgnore]
        [Display(Name = "Bullish Color",
                 Description = "Color for bullish BoS/CHoCH lines and labels.",
                 Order = 1, GroupName = "2. Appearance")]
        public System.Windows.Media.Brush BullishColor { get; set; }

        [Browsable(false)]
        public string BullishColorSerializable
        {
            get { return Serialize.BrushToString(BullishColor); }
            set { BullishColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Bearish Color",
                 Description = "Color for bearish BoS/CHoCH lines and labels.",
                 Order = 2, GroupName = "2. Appearance")]
        public System.Windows.Media.Brush BearishColor { get; set; }

        [Browsable(false)]
        public string BearishColorSerializable
        {
            get { return Serialize.BrushToString(BearishColor); }
            set { BearishColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Line Width",
                 Description = "Thickness of structure lines.",
                 Order = 3, GroupName = "2. Appearance")]
        public int LineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Line Style",
                 Description = "Dash style for structure lines.",
                 Order = 4, GroupName = "2. Appearance")]
        public DashStyleHelper LineStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Labels",
                 Description = "Display BoS / CHoCH text labels on structure break lines.",
                 Order = 5, GroupName = "2. Appearance")]
        public bool ShowLabels { get; set; }

        [NinjaScriptProperty]
        [Range(8, 14)]
        [Display(Name = "Label Font Size",
                 Description = "Font size for structure labels (8–14 px).",
                 Order = 6, GroupName = "2. Appearance")]
        public int LabelFontSize { get; set; }

        // ── 3. Volume Profile ───────────────────────────────────────────────────
        [NinjaScriptProperty]
        [Display(Name = "Show Volume Profile",
                 Description = "Render volume profile histograms for each market structure swing.",
                 Order = 1, GroupName = "3. Volume Profile")]
        public bool ShowVolumeProfile { get; set; }

        [NinjaScriptProperty]
        [Range(5, 500)]
        [Display(Name = "Profile Levels",
                 Description = "Number of price levels to divide each swing profile into.",
                 Order = 2, GroupName = "3. Volume Profile")]
        public int ProfileSize { get; set; }

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "Profile Width %",
                 Description = "Width of each volume profile as a percentage of the visible chart width.",
                 Order = 3, GroupName = "3. Volume Profile")]
        public int ProfileWidthPct { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Profile Type",
                 Description = "Split: buy/sell volumes side-by-side. Stacked: single bar colored by swing direction.",
                 Order = 4, GroupName = "3. Volume Profile")]
        public MSVPProfileType ProfileType { get; set; }

        [NinjaScriptProperty]
        [Range(10, 90)]
        [Display(Name = "Profile Opacity %",
                 Description = "Transparency of volume profile bars.",
                 Order = 5, GroupName = "3. Volume Profile")]
        public int ProfileOpacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Buy Volume Color",
                 Description = "Color for buy/up volume bars in the profile.",
                 Order = 6, GroupName = "3. Volume Profile")]
        public System.Windows.Media.Brush BuyVolColor { get; set; }

        [Browsable(false)]
        public string BuyVolColorSerializable
        {
            get { return Serialize.BrushToString(BuyVolColor); }
            set { BuyVolColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Sell Volume Color",
                 Description = "Color for sell/down volume bars in the profile.",
                 Order = 7, GroupName = "3. Volume Profile")]
        public System.Windows.Media.Brush SellVolColor { get; set; }

        [Browsable(false)]
        public string SellVolColorSerializable
        {
            get { return Serialize.BrushToString(SellVolColor); }
            set { SellVolColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Max Profiles",
                 Description = "Maximum number of recent swing profiles to display.",
                 Order = 8, GroupName = "3. Volume Profile")]
        public int MaxProfiles { get; set; }

        // ── 4. POC & Value Area ─────────────────────────────────────────────────
        [NinjaScriptProperty]
        [Display(Name = "Show POC",
                 Description = "Draw a horizontal line at the Point of Control (highest volume level) of each profile.",
                 Order = 1, GroupName = "4. POC & Value Area")]
        public bool ShowPOC { get; set; }

        [XmlIgnore]
        [Display(Name = "POC Color",
                 Description = "Color of the Point of Control line.",
                 Order = 2, GroupName = "4. POC & Value Area")]
        public System.Windows.Media.Brush POCColor { get; set; }

        [Browsable(false)]
        public string POCColorSerializable
        {
            get { return Serialize.BrushToString(POCColor); }
            set { POCColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Show Value Area",
                 Description = "Draw VAH and VAL lines indicating where the majority of volume traded.",
                 Order = 3, GroupName = "4. POC & Value Area")]
        public bool ShowValueArea { get; set; }

        [XmlIgnore]
        [Display(Name = "VA Color",
                 Description = "Color of the Value Area High and Low lines.",
                 Order = 4, GroupName = "4. POC & Value Area")]
        public System.Windows.Media.Brush VAColor { get; set; }

        [Browsable(false)]
        public string VAColorSerializable
        {
            get { return Serialize.BrushToString(VAColor); }
            set { VAColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(50, 95)]
        [Display(Name = "Value Area %",
                 Description = "Percentage of total volume contained within the value area (default 70%).",
                 Order = 5, GroupName = "4. POC & Value Area")]
        public int ValueAreaPct { get; set; }

        // ── 5. CVD ──────────────────────────────────────────────────────────────
        [NinjaScriptProperty]
        [Display(Name = "Show CVD",
                 Description = "Display Cumulative Volume Delta as a bar chart plot below the price panel.",
                 Order = 1, GroupName = "5. CVD")]
        public bool ShowCVD { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "CVD Reset Mode",
                 Description = "When to reset the CVD baseline: CHoCH = trend change only, BoS+CHoCH = every structure break, Day/Week = time-based.",
                 Order = 2, GroupName = "5. CVD")]
        public MSVPCVDReset CVDResetMode { get; set; }

        [XmlIgnore]
        [Display(Name = "CVD Positive Color",
                 Description = "Color of CVD bars when cumulative delta is positive (buying pressure).",
                 Order = 3, GroupName = "5. CVD")]
        public System.Windows.Media.Brush CVDBullColor { get; set; }

        [Browsable(false)]
        public string CVDBullColorSerializable
        {
            get { return Serialize.BrushToString(CVDBullColor); }
            set { CVDBullColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "CVD Negative Color",
                 Description = "Color of CVD bars when cumulative delta is negative (selling pressure).",
                 Order = 4, GroupName = "5. CVD")]
        public System.Windows.Media.Brush CVDBearColor { get; set; }

        [Browsable(false)]
        public string CVDBearColorSerializable
        {
            get { return Serialize.BrushToString(CVDBearColor); }
            set { CVDBearColor = Serialize.StringToBrush(value); }
        }

        #endregion
    }
}
