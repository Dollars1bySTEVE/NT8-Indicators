// Indicators/Bookmap.cs
// WyckoffBookMap + Bookmap NT8 Indicator
// Rewritten for NT8 8.1.6.3 / SharpDX 2.6.3
// Adapted from itchy5/NT8-OrderFlowKit with GPU optimisations following
// the IQMainGPU / IQMainGPU_Enhanced patterns in this repository.
//
// Phase 1 — NT8 Compliance & Performance
//   • Brush cache in WyckoffRenderControl (RenderTarget-change aware)
//   • BrushToColor static Dictionary + SolidColorBrush fallback
//   • RenderTarget null/disposed guard on every draw method
//   • ForceRefresh rate-limited to ≤ 10 calls/sec (100 ms interval)
//   • BarMarginRight set once in State.DataLoaded, not every OnRender
//   • Full State.Terminated cleanup (brushes, text formats, collections)
//   • IsInHitTest used as bool (NT8 8.1.x)
//   • OnRender safety guards
//
// Phase 2 — New Features
//   • Heat gradient coloring (separate bid/ask cold→hot pairs)
//   • Scaled bubbles (user-configurable multiplier)
//   • POC (Point of Control) horizontal line
//   • Auto-center / auto-scale when price nears visible-range edge

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
#endregion

// ── Enums OUTSIDE all namespaces — required by NT8 compiler ─────────────────

/// <summary>Range of price levels shown in the BookMap ladder.</summary>
public enum _BookMapLadderRange
{
    Levels10  = 10,
    Levels20  = 20,
    Levels50  = 50,
    Levels100 = 100,
}

/// <summary>How per-bar market-order volume is accumulated.</summary>
public enum _MarketBarsCalculation
{
    EachTick,
    EachBar,
}

/// <summary>What is shown in the market-orders column.</summary>
public enum _MarketOrdersCalculation
{
    Delta,
    BidAsk,
    Total,
}

/// <summary>What is shown in the cumulative market-orders column.</summary>
public enum _MarketCummulativeCalculation
{
    DeltaAndTotal,
    Delta,
    Total,
}

// ────────────────────────────────────────────────────────────────────────────
// WyckoffBookMap — rendering + data management class
// Extends WyckoffRenderControl for GPU draw helpers.
// ────────────────────────────────────────────────────────────────────────────

namespace SightEngine
{
    public class WyckoffBookMap : WyckoffRenderControl
    {
        // ── Public data references (set by the indicator) ────────────────────

        public BookMap           bookMapData;          // live DOM snapshot
        public OrderBookLadder   orderBookLadderData;  // sidebar DOM
        public PriceLadder       priceLadderData;      // cumulative executed volume
        public MarketOrderLadder marketOrderData;      // aggressive orders for bubbles

        /// <summary>Set from the indicator's ChartBars before calling render methods.</summary>
        public NinjaTrader.Gui.Chart.ChartBars ChartBarsRef;

        // ── Indicator-level settings passed in from the NT8 indicator ────────

        public bool   IsRealtime;
        public float  filterPendingOrdersPer      = 5f;     // % threshold below which DOM bars are hidden
        public float  filterTextPendingOrdersPer  = 95f;    // % threshold above which size label appears
        public float  filterBigPendingOrders      = 100f;   // absolute filter for "big order" highlight
        public float  filterAggresiveMarketOrders = 30f;    // min volume for bubble rendering
        public float  bigPendingOrdersOpacity     = 0.9f;
        public float  backgroundOpacity           = 0.7f;
        public int    ladderRange                 = 50;

        // Colors (SharpDX) set from WPF brush properties via BrushToColor
        public SharpDX.Color bidPendingColor       = new SharpDX.Color(139, 0,   0,   255); // DarkRed
        public SharpDX.Color askPendingColor       = new SharpDX.Color(0,   100, 0,   255); // DarkGreen
        public SharpDX.Color bigPendingColor       = new SharpDX.Color(255, 165, 0,   255); // Orange
        public SharpDX.Color bidMarketColor        = new SharpDX.Color(255, 0,   0,   255); // Red
        public SharpDX.Color askMarketColor        = new SharpDX.Color(0,   255, 0,   255); // Lime
        public SharpDX.Color backgroundColor      = SharpDX.Color.Black;

        public _MarketOrdersCalculation    marketOrdersCalc      = _MarketOrdersCalculation.Delta;
        public _MarketBarsCalculation      marketBarsCalc        = _MarketBarsCalculation.EachTick;
        public _MarketCummulativeCalculation marketCummCalc      = _MarketCummulativeCalculation.DeltaAndTotal;

        // ── Text formats (created once, disposed in DisposeTextFormats) ──────

        public SharpDX.DirectWrite.TextFormat bookmapTextFormat;
        public SharpDX.DirectWrite.TextFormat cummulativeTextFormat;
        public SharpDX.DirectWrite.TextFormat orderbookTextFormat;

        // ── Scratch geometry objects (allocated once, reused every frame) ────

        private SharpDX.RectangleF     _genRect       = new SharpDX.RectangleF();
        private SharpDX.Direct2D1.Ellipse _marketEllipse = new SharpDX.Direct2D1.Ellipse();
        private SharpDX.Vector2         _lineStart     = new SharpDX.Vector2();
        private SharpDX.Vector2         _lineEnd       = new SharpDX.Vector2();

        // Base half-size for market-order bubbles (set to half a tick height)
        public float W = 4f;
        public float H = 4f;

        // ── Phase 2 — Scaled bubbles ─────────────────────────────────────────

        private float _bubbleScaleMultiplier = 5.0f;

        public void setBubbleScaleMultiplier(float multiplier)
        {
            _bubbleScaleMultiplier = Math2.Clampf(multiplier, 0.5f, 20f);
        }

        // ── Phase 2 — Heat gradient ──────────────────────────────────────────

        private bool _useHeatGradient;
        private SharpDX.Color _bidColdColor = new SharpDX.Color(128, 0, 0, 255);        // Maroon
        private SharpDX.Color _bidHotColor  = new SharpDX.Color(255, 69, 0, 255);       // OrangeRed
        private SharpDX.Color _askColdColor = new SharpDX.Color(0,   100, 0, 255);      // DarkGreen
        private SharpDX.Color _askHotColor  = new SharpDX.Color(50,  205, 50, 255);     // LimeGreen

        public void setHeatGradient(bool useHeatGradient,
            System.Windows.Media.Brush bidCold, System.Windows.Media.Brush bidHot,
            System.Windows.Media.Brush askCold, System.Windows.Media.Brush askHot)
        {
            _useHeatGradient = useHeatGradient;
            _bidColdColor    = WyckoffRenderControl.BrushToColor(bidCold);
            _bidHotColor     = WyckoffRenderControl.BrushToColor(bidHot);
            _askColdColor    = WyckoffRenderControl.BrushToColor(askCold);
            _askHotColor     = WyckoffRenderControl.BrushToColor(askHot);
        }

        // ── Phase 2 — POC line ───────────────────────────────────────────────

        private bool            _showPOC;
        private SharpDX.Color   _pocColor = new SharpDX.Color(255, 255, 0, 255); // Yellow
        private SharpDX.Vector2 _pocStart = new SharpDX.Vector2();
        private SharpDX.Vector2 _pocEnd   = new SharpDX.Vector2();

        public void setPOC(bool showPOC, System.Windows.Media.Brush pocColor)
        {
            _showPOC  = showPOC;
            _pocColor = WyckoffRenderControl.BrushToColor(pocColor);
        }

        // ── Initialise text formats ──────────────────────────────────────────

        public void InitTextFormats(float bmFontWidth, float bmFontHeight)
        {
            DisposeTextFormats();

            float sz = Math.Max(bmFontWidth, bmFontHeight);
            sz = Math2.Clampf(sz, 6f, 20f);

            bookmapTextFormat     = CreateTextFormat("Consolas", sz);
            cummulativeTextFormat = CreateTextFormat("Consolas", sz);
            orderbookTextFormat   = CreateTextFormat("Consolas", sz);
        }

        public void DisposeTextFormats()
        {
            if (bookmapTextFormat     != null && !bookmapTextFormat.IsDisposed)
                bookmapTextFormat.Dispose();
            if (cummulativeTextFormat != null && !cummulativeTextFormat.IsDisposed)
                cummulativeTextFormat.Dispose();
            if (orderbookTextFormat   != null && !orderbookTextFormat.IsDisposed)
                orderbookTextFormat.Dispose();

            bookmapTextFormat     = null;
            cummulativeTextFormat = null;
            orderbookTextFormat   = null;
        }

        // ── renderBackground ─────────────────────────────────────────────────

        /// <summary>Fills the BookMap margin column with a semi-transparent background.</summary>
        public void renderBackground()
        {
            if (RENDER_TARGET == null || RENDER_TARGET.IsDisposed) return;

            _genRect.X      = PanelW - marginRight;
            _genRect.Y      = 0f;
            _genRect.Width  = marginRight;
            _genRect.Height = PanelH;
            myFillRectangle(ref _genRect, backgroundColor, backgroundOpacity);
        }

        // ── renderOrdersLadder — DOM heatmap (pending orders) ────────────────

        /// <summary>
        /// Renders the heatmap of resting orders. Bids on the left half, asks on the
        /// right half of the BookMap margin column. Bar width is proportional to
        /// relative size within the visible price range. Heat gradient applied when
        /// <see cref="_useHeatGradient"/> is true.
        /// </summary>
        public void renderOrdersLadder()
        {
            if (RENDER_TARGET == null || RENDER_TARGET.IsDisposed) return;
            if (CHART_SCALE == null) return;
            if (bookMapData == null) return;

            double visHigh = CHART_SCALE.MaxValue;
            double visLow  = CHART_SCALE.MinValue;
            if (visHigh <= visLow) return;

            float colX     = PanelW - marginRight;
            float halfCol  = marginRight * 0.5f;

            // Take thread-safe snapshots
            var bidLevels = bookMapData.GetBidLevels();
            var askLevels = bookMapData.GetAskLevels();

            long maxBid = bookMapData.MaxBidSize;
            long maxAsk = bookMapData.MaxAskSize;
            if (maxBid <= 0) maxBid = 1;
            if (maxAsk <= 0) maxAsk = 1;

            float tickH = Math.Abs((float)(CHART_SCALE.GetYByValue(visLow)
                                         - CHART_SCALE.GetYByValue(visLow + CHART_SCALE.Instrument.MasterInstrument.TickSize)));
            if (tickH < 1f) tickH = 1f;

            // ── Bid levels ───────────────────────────────────────────────────
            foreach (var kv in bidLevels)
            {
                double price = kv.Key;
                long   size  = kv.Value;
                if (price < visLow || price > visHigh) continue;
                if (size <= 0) continue;

                float pct = (float)(Math2.Percent(maxBid, size) / 100f);
                if (pct * 100f < filterPendingOrdersPer) continue;

                float barW = halfCol * pct;
                float y    = (float)CHART_SCALE.GetYByValue(price) - tickH * 0.5f;

                _genRect.X      = colX;
                _genRect.Y      = y;
                _genRect.Width  = barW;
                _genRect.Height = tickH;

                SharpDX.Color fillColor;
                float         fillOpacity;

                if (size >= filterBigPendingOrders)
                {
                    fillColor   = bigPendingColor;
                    fillOpacity = bigPendingOrdersOpacity;
                }
                else if (_useHeatGradient)
                {
                    fillColor   = GetHeatColor(pct, _bidColdColor, _bidHotColor);
                    fillOpacity = 0.95f;
                }
                else
                {
                    fillColor   = bidPendingColor;
                    fillOpacity = 0.6f + pct * 0.35f;
                }

                myFillRectangle(ref _genRect, fillColor, fillOpacity);

                // Size label for very large orders
                if (pct * 100f >= filterTextPendingOrdersPer && bookmapTextFormat != null)
                {
                    string sizeText = size.ToString();
                    myDrawText(sizeText, bookmapTextFormat, ref _genRect,
                        SharpDX.Color.White, 0.9f);
                }
            }

            // ── Ask levels ───────────────────────────────────────────────────
            foreach (var kv in askLevels)
            {
                double price = kv.Key;
                long   size  = kv.Value;
                if (price < visLow || price > visHigh) continue;
                if (size <= 0) continue;

                float pct  = (float)(Math2.Percent(maxAsk, size) / 100f);
                if (pct * 100f < filterPendingOrdersPer) continue;

                float barW = halfCol * pct;
                float y    = (float)CHART_SCALE.GetYByValue(price) - tickH * 0.5f;

                _genRect.X      = colX + halfCol;
                _genRect.Y      = y;
                _genRect.Width  = barW;
                _genRect.Height = tickH;

                SharpDX.Color fillColor;
                float         fillOpacity;

                if (size >= filterBigPendingOrders)
                {
                    fillColor   = bigPendingColor;
                    fillOpacity = bigPendingOrdersOpacity;
                }
                else if (_useHeatGradient)
                {
                    fillColor   = GetHeatColor(pct, _askColdColor, _askHotColor);
                    fillOpacity = 0.95f;
                }
                else
                {
                    fillColor   = askPendingColor;
                    fillOpacity = 0.6f + pct * 0.35f;
                }

                myFillRectangle(ref _genRect, fillColor, fillOpacity);

                if (pct * 100f >= filterTextPendingOrdersPer && bookmapTextFormat != null)
                {
                    string sizeText = size.ToString();
                    myDrawText(sizeText, bookmapTextFormat, ref _genRect,
                        SharpDX.Color.White, 0.9f);
                }
            }
        }

        // ── renderOrderBookLadder — DOM sidebar ──────────────────────────────

        /// <summary>
        /// Renders a text ladder on the right edge of the BookMap column showing
        /// current bid/ask sizes for the visible price range.
        /// </summary>
        public void renderOrderBookLadder()
        {
            if (RENDER_TARGET == null || RENDER_TARGET.IsDisposed) return;
            if (CHART_SCALE == null || orderbookTextFormat == null) return;
            if (orderBookLadderData == null) return;

            double visHigh = CHART_SCALE.MaxValue;
            double visLow  = CHART_SCALE.MinValue;
            if (visHigh <= visLow) return;

            double tickSize = CHART_SCALE.Instrument.MasterInstrument.TickSize;
            float  tickH   = Math.Abs((float)(CHART_SCALE.GetYByValue(visLow)
                                            - CHART_SCALE.GetYByValue(visLow + tickSize)));
            if (tickH < 1f) tickH = 1f;

            float labelW = 55f;
            float colX   = PanelW - labelW;

            var bidLevels = orderBookLadderData.GetBidLevels();
            var askLevels = orderBookLadderData.GetAskLevels();

            int maxLevels = ladderRange;
            int count     = 0;

            // Bids
            foreach (var kv in bidLevels)
            {
                if (count++ > maxLevels) break;
                double price = kv.Key;
                if (price < visLow || price > visHigh) continue;
                float y = (float)CHART_SCALE.GetYByValue(price) - tickH * 0.5f;

                _genRect.X      = colX;
                _genRect.Y      = y;
                _genRect.Width  = labelW * 0.5f;
                _genRect.Height = tickH;

                myDrawText(kv.Value.ToString(),
                    orderbookTextFormat, ref _genRect,
                    bidMarketColor, 0.9f);
            }

            count = 0;
            // Asks
            foreach (var kv in askLevels)
            {
                if (count++ > maxLevels) break;
                double price = kv.Key;
                if (price < visLow || price > visHigh) continue;
                float y = (float)CHART_SCALE.GetYByValue(price) - tickH * 0.5f;

                _genRect.X      = colX + labelW * 0.5f;
                _genRect.Y      = y;
                _genRect.Width  = labelW * 0.5f;
                _genRect.Height = tickH;

                myDrawText(kv.Value.ToString(),
                    orderbookTextFormat, ref _genRect,
                    askMarketColor, 0.9f);
            }
        }

        // ── renderCummulativeMarketOrderLadder ───────────────────────────────

        /// <summary>
        /// Renders the cumulative executed-trade volume per price level as horizontal
        /// bars on the left side of the BookMap column.
        /// </summary>
        public void renderCummulativeMarketOrderLadder()
        {
            if (RENDER_TARGET == null || RENDER_TARGET.IsDisposed) return;
            if (CHART_SCALE == null) return;
            if (priceLadderData == null) return;

            double visHigh = CHART_SCALE.MaxValue;
            double visLow  = CHART_SCALE.MinValue;
            if (visHigh <= visLow) return;

            var snapshot = priceLadderData.GetSnapshot();
            if (snapshot.Count == 0) return;

            long maxTotal = priceLadderData.MaxTotal;
            if (maxTotal <= 0) maxTotal = 1;

            float colX  = PanelW - marginRight;
            float colW  = marginRight * 0.3f;  // cumulative column occupies 30% of the margin

            float tickH = Math.Abs((float)(CHART_SCALE.GetYByValue(visLow)
                                         - CHART_SCALE.GetYByValue(visLow + CHART_SCALE.Instrument.MasterInstrument.TickSize)));
            if (tickH < 1f) tickH = 1f;

            foreach (var kv in snapshot)
            {
                double price = kv.Key;
                if (price < visLow || price > visHigh) continue;

                VolumeNode node = kv.Value;
                if (node.Total <= 0) continue;

                float y   = (float)CHART_SCALE.GetYByValue(price) - tickH * 0.5f;
                float pct = (float)(Math2.Percent(maxTotal, node.Total) / 100f);

                // Show bid (sell) portion in red, ask (buy) portion in green
                float bidPct = node.Total > 0 ? (float)node.Bid / node.Total : 0f;
                float askPct = 1f - bidPct;

                float barW = colW * pct;

                // Bid (sell pressure) portion — left half of bar
                _genRect.X      = colX;
                _genRect.Y      = y;
                _genRect.Width  = barW * bidPct;
                _genRect.Height = tickH;
                if (_genRect.Width > 0.5f)
                    myFillRectangle(ref _genRect, bidMarketColor, 0.7f);

                // Ask (buy pressure) portion — right half of bar
                _genRect.X      = colX + barW * bidPct;
                _genRect.Width  = barW * askPct;
                if (_genRect.Width > 0.5f)
                    myFillRectangle(ref _genRect, askMarketColor, 0.7f);

                // Volume text
                if (pct > 0.7f && cummulativeTextFormat != null)
                {
                    string label = FormatVolume(node.Total);
                    _genRect.X     = colX;
                    _genRect.Width = barW;
                    myDrawText(label, cummulativeTextFormat, ref _genRect,
                        SharpDX.Color.White, 0.9f);
                }
            }
        }

        // ── renderMarketBar — single bubble ─────────────────────────────────

        /// <summary>
        /// Renders a single market-order bubble at (X, Y) with the given color,
        /// scaling radius with volume using the bubble-scale multiplier.
        /// </summary>
        public void renderMarketBar(float X, float Y,
            SharpDX.Color color, long volume, float opacity)
        {
            if (volume < (long)filterAggresiveMarketOrders) return;
            if (RENDER_TARGET == null || RENDER_TARGET.IsDisposed) return;

            float aggressivePer = (float)(Math2.Percent(
                filterAggresiveMarketOrders * 10f, volume) / 100f);
            aggressivePer = Math2.Clampf(aggressivePer, 0f, 1f);

            // Scaled bubble: radius grows with order intensity
            float minScale = 0.8f;
            float maxScale = _bubbleScaleMultiplier;
            float scale    = minScale + aggressivePer * (maxScale - minScale);
            scale = Math2.Clampf(scale, minScale, maxScale);

            // Update scratch Ellipse struct in-place (no heap alloc for structs)
            _marketEllipse.Point   = new SharpDX.Vector2(X, Y);
            _marketEllipse.RadiusX = W * scale;
            _marketEllipse.RadiusY = H * scale;

            myDrawEllipse(ref _marketEllipse, color, aggressivePer * opacity, 2.0f);
            myFillEllipse(ref _marketEllipse, color, aggressivePer * opacity * 0.6f);
        }

        // ── renderAllMarketBars — iterate market-order history ───────────────

        /// <summary>
        /// Iterates the market-order history and calls <see cref="renderMarketBar"/>
        /// for each entry visible in the current bar range.
        /// </summary>
        public void renderAllMarketBars(
            NinjaTrader.Gui.Chart.ChartControl chartControl,
            int fromBar, int toBar)
        {
            if (RENDER_TARGET == null || RENDER_TARGET.IsDisposed) return;
            if (CHART_SCALE == null || chartControl == null) return;
            if (marketOrderData == null || ChartBarsRef == null) return;

            var orders = marketOrderData.GetSnapshot();
            foreach (var entry in orders)
            {
                if (entry.BarIndex < fromBar || entry.BarIndex > toBar) continue;

                float x = (float)chartControl.GetXByBarIndex(ChartBarsRef, entry.BarIndex);
                float y = (float)CHART_SCALE.GetYByValue(entry.Price);

                SharpDX.Color color = entry.IsBuy ? askMarketColor : bidMarketColor;
                renderMarketBar(x, y, color, entry.Volume, 0.9f);
            }
        }

        // ── renderPOCLine — Point of Control ─────────────────────────────────

        /// <summary>
        /// Draws a horizontal line at the price level with the highest cumulative
        /// executed volume (Point of Control) across the full chart panel width.
        /// </summary>
        public void renderPOCLine()
        {
            if (!_showPOC) return;
            if (RENDER_TARGET == null || RENDER_TARGET.IsDisposed) return;
            if (CHART_SCALE == null || priceLadderData == null) return;
            if (priceLadderData.MaxTotal == 0) return;

            double pocPrice = priceLadderData.GetPOCPrice();
            if (pocPrice <= 0) return;

            float pocY = (float)CHART_SCALE.GetYByValue(pocPrice);

            _pocStart.X = 0f;
            _pocStart.Y = pocY;
            _pocEnd.X   = PanelW - marginRight;
            _pocEnd.Y   = pocY;

            myDrawLine(ref _pocStart, ref _pocEnd, _pocColor, 1.0f, 2.0f, null);

            // Label
            if (bookmapTextFormat != null)
            {
                string label = "POC";
                _genRect.X      = PanelW - marginRight - 40f;
                _genRect.Y      = pocY - 10f;
                _genRect.Width  = 36f;
                _genRect.Height = 14f;
                myDrawText(label, bookmapTextFormat, ref _genRect, _pocColor, 1.0f);
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static string FormatVolume(long v)
        {
            if (v >= 1000000L) return (v / 1000000L).ToString() + "M";
            if (v >= 1000L)    return (v / 1000L).ToString()    + "K";
            return v.ToString();
        }
    }
}

// ────────────────────────────────────────────────────────────────────────────
// Bookmap — NinjaTrader 8 Indicator
// ────────────────────────────────────────────────────────────────────────────

namespace NinjaTrader.NinjaScript.Indicators
{
    public class Bookmap : Indicator
    {
        // ── Private fields ───────────────────────────────────────────────────

        private SightEngine.WyckoffBookMap    wyckoffBM;
        private SightEngine.BookMap           bookMap;
        private SightEngine.OrderBookLadder   orderBookLadder;
        private SightEngine.PriceLadder       priceLadder;
        private SightEngine.MarketOrderLadder marketOrderLadder;

        private DateTime _lastForceRefresh = DateTime.MinValue;
        private const int REFRESH_INTERVAL_MS = 100; // max 10 refreshes/sec

        // Track last bid/ask to classify trade direction
        private double _lastBid;
        private double _lastAsk;

        // ── OnStateChange ────────────────────────────────────────────────────

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "BookMap-style order-flow heatmap with DOM heatmap, " +
                                           "bubbles, heat gradient coloring, POC line, and auto-center.";
                Name                     = "Bookmap";
                Calculate                = Calculate.OnEachTick;
                IsOverlay                = true;
                DrawOnPricePanel         = false;
                DisplayInDataBox         = false;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = true;

                // ── BookMap Style defaults ────────────────────────────────────
                var darkRed = new SolidColorBrush(Colors.DarkRed);       darkRed.Freeze();
                var darkGrn = new SolidColorBrush(Colors.DarkGreen);     darkGrn.Freeze();
                var orange  = new SolidColorBrush(Colors.Orange);        orange.Freeze();
                var red     = new SolidColorBrush(Colors.Red);           red.Freeze();
                var lime    = new SolidColorBrush(Colors.Lime);          lime.Freeze();
                var black   = new SolidColorBrush(Colors.Black);         black.Freeze();
                var yellow  = new SolidColorBrush(Colors.Yellow);        yellow.Freeze();
                var maroon  = new SolidColorBrush(Colors.Maroon);        maroon.Freeze();
                var orRed   = new SolidColorBrush(Colors.OrangeRed);     orRed.Freeze();
                var limeGrn = new SolidColorBrush(Colors.LimeGreen);     limeGrn.Freeze();

                _BidPendingOrdersColor        = darkRed;
                _AskPendingOrdersColor        = darkGrn;
                _BigPendingOrdersColor        = orange;
                _BidMarketOrdersColor         = red;
                _AskMarketOrdersColor         = lime;
                _BackgroundColor              = black;

                _BackgroundColorOpacity       = 70f;
                _BigPendingOrdersOpacity      = 90f;
                _FilterPendingOrdersPer       = 5f;
                _FilterTextPendingOrdersPer   = 95f;
                _AggresiveMarketOrdersFilter  = 30;
                _FilterBigPendingOrders       = 100;
                _LadderRange                  = _BookMapLadderRange.Levels50;
                this._MarketBarsCalculation        = global::_MarketBarsCalculation.EachTick;
                this._MarketOrdersCalculation      = global::_MarketOrdersCalculation.Delta;
                this._MarketCummulativeCalculation = global::_MarketCummulativeCalculation.DeltaAndTotal;
                _BookMarginRight              = 250;
                _BookmapMinFontWidth          = 7f;
                _BookmapMinFontHeight         = 7f;

                // ── Bubble Scale ──────────────────────────────────────────────
                _BubbleScaleMultiplier        = 5.0f;

                // ── Heat Gradient ─────────────────────────────────────────────
                _UseHeatGradient              = true;
                _BidColdColor                 = maroon;
                _BidHotColor                  = orRed;
                _AskColdColor                 = darkGrn;
                _AskHotColor                  = limeGrn;

                // ── POC Line ──────────────────────────────────────────────────
                _ShowPOCLine                  = true;
                _POCLineColor                 = yellow;

                // ── Auto Scale ────────────────────────────────────────────────
                _AutoCenterPrice              = true;
                _AutoCenterThreshold          = 20f;
            }
            else if (State == State.Configure)
            {
                bookMap           = new SightEngine.BookMap();
                orderBookLadder   = new SightEngine.OrderBookLadder();
                priceLadder       = new SightEngine.PriceLadder();
                marketOrderLadder = new SightEngine.MarketOrderLadder();

                wyckoffBM = new SightEngine.WyckoffBookMap
                {
                    bookMapData        = bookMap,
                    orderBookLadderData = orderBookLadder,
                    priceLadderData    = priceLadder,
                    marketOrderData    = marketOrderLadder,
                };
            }
            else if (State == State.DataLoaded)
            {
                // Set once here — not every OnRender frame (Phase 1.5)
                if (ChartControl != null)
                    ChartControl.Properties.BarMarginRight = _BookMarginRight;

                // Push style settings into the render helper
                ApplyBookMapStyle();
            }
            else if (State == State.Terminated)
            {
                // Restore BarMarginRight (Phase 1.6)
                if (ChartControl != null)
                    ChartControl.Properties.BarMarginRight = 0;

                // Dispose GPU resources
                wyckoffBM?.DisposeBrushCache();
                wyckoffBM?.DisposeTextFormats();

                // Release data collections
                bookMap?.Clear();
                orderBookLadder?.Clear();
                priceLadder?.Clear();
                marketOrderLadder?.Clear();

                bookMap           = null;
                orderBookLadder   = null;
                priceLadder       = null;
                marketOrderLadder = null;
                wyckoffBM         = null;
            }
        }

        // ── ApplyBookMapStyle ────────────────────────────────────────────────

        private void ApplyBookMapStyle()
        {
            if (wyckoffBM == null) return;

            wyckoffBM.bidPendingColor     = SightEngine.WyckoffRenderControl.BrushToColor(_BidPendingOrdersColor);
            wyckoffBM.askPendingColor     = SightEngine.WyckoffRenderControl.BrushToColor(_AskPendingOrdersColor);
            wyckoffBM.bigPendingColor     = SightEngine.WyckoffRenderControl.BrushToColor(_BigPendingOrdersColor);
            wyckoffBM.bidMarketColor      = SightEngine.WyckoffRenderControl.BrushToColor(_BidMarketOrdersColor);
            wyckoffBM.askMarketColor      = SightEngine.WyckoffRenderControl.BrushToColor(_AskMarketOrdersColor);
            wyckoffBM.backgroundColor    = SightEngine.WyckoffRenderControl.BrushToColor(_BackgroundColor);

            wyckoffBM.backgroundOpacity          = _BackgroundColorOpacity       / 100f;
            wyckoffBM.bigPendingOrdersOpacity     = _BigPendingOrdersOpacity     / 100f;
            wyckoffBM.filterPendingOrdersPer      = _FilterPendingOrdersPer;
            wyckoffBM.filterTextPendingOrdersPer  = _FilterTextPendingOrdersPer;
            wyckoffBM.filterAggresiveMarketOrders = _AggresiveMarketOrdersFilter;
            wyckoffBM.filterBigPendingOrders      = _FilterBigPendingOrders;
            wyckoffBM.ladderRange                 = (int)_LadderRange;
            wyckoffBM.marketOrdersCalc            = _MarketOrdersCalculation;
            wyckoffBM.marketBarsCalc              = _MarketBarsCalculation;
            wyckoffBM.marketCummCalc              = _MarketCummulativeCalculation;

            wyckoffBM.setHeatGradient(_UseHeatGradient,
                _BidColdColor, _BidHotColor,
                _AskColdColor, _AskHotColor);

            wyckoffBM.setPOC(_ShowPOCLine, _POCLineColor);

            wyckoffBM.setBubbleScaleMultiplier(_BubbleScaleMultiplier);

            wyckoffBM.InitTextFormats(_BookmapMinFontWidth, _BookmapMinFontHeight);
        }

        // ── OnRender ─────────────────────────────────────────────────────────

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            // ── Phase 1.7 — Safety guards ────────────────────────────────────
            if (RenderTarget == null || RenderTarget.IsDisposed) return;
            if (wyckoffBM == null || !wyckoffBM.IsRealtime) return;
            if (IsInHitTest) return;  // NT8 8.1.x: IsInHitTest is bool
            if (chartControl == null || ChartBars == null || ChartBars.Bars == null) return;

            // ── Supply context to the render helper ──────────────────────────
            wyckoffBM.RENDER_TARGET = RenderTarget;
            wyckoffBM.CHART_SCALE   = chartScale;
            wyckoffBM.PanelW        = (float)ChartPanel.W;
            wyckoffBM.PanelH        = (float)ChartPanel.H;
            wyckoffBM.marginRight   = _BookMarginRight;
            wyckoffBM.ChartBarsRef  = ChartBars;

            // Bubble base size = half a tick height so bubbles scale with zoom
            double tickSize = chartScale.Instrument.MasterInstrument.TickSize;
            float  tickH    = Math.Abs((float)(chartScale.GetYByValue(chartScale.MinValue)
                                             - chartScale.GetYByValue(chartScale.MinValue + tickSize)));
            wyckoffBM.W = Math.Max(2f, tickH * 0.5f);
            wyckoffBM.H = Math.Max(2f, tickH * 0.5f);

            // ── Render layers ────────────────────────────────────────────────
            try { wyckoffBM.renderBackground(); }
            catch { /* swallow — never kill D2D pipeline */ }

            try { wyckoffBM.renderCummulativeMarketOrderLadder(); }
            catch { }

            try { wyckoffBM.renderOrdersLadder(); }
            catch { }

            try { wyckoffBM.renderOrderBookLadder(); }
            catch { }

            int fromBar = ChartBars.FromIndex;
            int toBar   = ChartBars.ToIndex;
            try { wyckoffBM.renderAllMarketBars(chartControl, fromBar, toBar); }
            catch { }

            // ── Phase 2 — POC line ────────────────────────────────────────
            try { wyckoffBM.renderPOCLine(); }
            catch { }

            // ── Phase 2 — Auto-center ─────────────────────────────────────
            if (_AutoCenterPrice)
            {
                try
                {
                    double currentPrice = Bars.LastPrice;
                    double visibleHigh  = chartScale.MaxValue;
                    double visibleLow   = chartScale.MinValue;
                    double visibleRange = visibleHigh - visibleLow;

                    if (visibleRange > 0)
                    {
                        double threshold   = visibleRange * (_AutoCenterThreshold / 100.0);
                        bool   priceNearEdge = currentPrice > (visibleHigh - threshold)
                                            || currentPrice < (visibleLow  + threshold);
                        if (priceNearEdge)
                            ChartControl.InvalidateVisual();
                    }
                }
                catch { }
            }
        }

        // ── OnMarketDepth — rate-limited ForceRefresh ─────────────────────────

        protected override void OnMarketDepth(MarketDepthEventArgs depthMarketArgs)
        {
            if (bookMap        == null) return;
            if (orderBookLadder == null) return;

            bookMap.onMarketDepth(depthMarketArgs);
            orderBookLadder.AddOrder(Bars.LastPrice, depthMarketArgs);

            // Phase 1.4 — Rate-limit ForceRefresh to ≤ 10 calls/sec
            if ((DateTime.Now - _lastForceRefresh).TotalMilliseconds >= REFRESH_INTERVAL_MS)
            {
                if (wyckoffBM != null)
                    wyckoffBM.IsRealtime = true;

                ForceRefresh();
                _lastForceRefresh = DateTime.Now;
            }
        }

        // ── OnMarketData — track executed trades ─────────────────────────────

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e == null) return;

            // Update running bid/ask so we can classify trade direction
            if (e.MarketDataType == MarketDataType.Bid)
            {
                _lastBid = e.Price;
                return;
            }
            if (e.MarketDataType == MarketDataType.Ask)
            {
                _lastAsk = e.Price;
                return;
            }
            if (e.MarketDataType != MarketDataType.Last) return;

            if (priceLadder == null || marketOrderLadder == null) return;

            long volume = (long)e.Volume;
            if (volume <= 0) return;

            double price = e.Price;

            // Classify aggressor side:
            // price >= last ask → buy aggressor (lifted offer)
            // price <= last bid → sell aggressor (hit bid)
            // price between → use midpoint comparison
            bool isBuy;
            if (_lastAsk > 0 && price >= _lastAsk)
                isBuy = true;
            else if (_lastBid > 0 && price <= _lastBid)
                isBuy = false;
            else
                isBuy = _lastAsk > 0 && _lastBid > 0
                    ? price > (_lastBid + _lastAsk) * 0.5
                    : price > (_lastBid > 0 ? _lastBid : price);

            priceLadder.AddTrade(price, isBuy, volume);
            marketOrderLadder.AddOrder(price, isBuy, volume,
                e.Time, CurrentBar);

            if (wyckoffBM != null)
                wyckoffBM.IsRealtime = true;
        }

        protected override void OnBarUpdate()
        {
            // Required for Calculate.OnEachTick — no logic needed here since
            // all processing is driven by OnMarketDepth / OnMarketData.
        }

        // ────────────────────────────────────────────────────────────────────
        #region Properties — Book Map Style

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Bid Pending Orders Color", Order = 1, GroupName = "Book Map Style")]
        public Brush _BidPendingOrdersColor { get; set; }

        [Browsable(false)]
        public string _BidPendingOrdersColorSerializable
        {
            get { return Serialize.BrushToString(_BidPendingOrdersColor); }
            set { _BidPendingOrdersColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Ask Pending Orders Color", Order = 2, GroupName = "Book Map Style")]
        public Brush _AskPendingOrdersColor { get; set; }

        [Browsable(false)]
        public string _AskPendingOrdersColorSerializable
        {
            get { return Serialize.BrushToString(_AskPendingOrdersColor); }
            set { _AskPendingOrdersColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Big Pending Orders Color", Order = 3, GroupName = "Book Map Style")]
        public Brush _BigPendingOrdersColor { get; set; }

        [Browsable(false)]
        public string _BigPendingOrdersColorSerializable
        {
            get { return Serialize.BrushToString(_BigPendingOrdersColor); }
            set { _BigPendingOrdersColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Bid Market Orders Color", Order = 4, GroupName = "Book Map Style")]
        public Brush _BidMarketOrdersColor { get; set; }

        [Browsable(false)]
        public string _BidMarketOrdersColorSerializable
        {
            get { return Serialize.BrushToString(_BidMarketOrdersColor); }
            set { _BidMarketOrdersColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Ask Market Orders Color", Order = 5, GroupName = "Book Map Style")]
        public Brush _AskMarketOrdersColor { get; set; }

        [Browsable(false)]
        public string _AskMarketOrdersColorSerializable
        {
            get { return Serialize.BrushToString(_AskMarketOrdersColor); }
            set { _AskMarketOrdersColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Background Color", Order = 6, GroupName = "Book Map Style")]
        public Brush _BackgroundColor { get; set; }

        [Browsable(false)]
        public string _BackgroundColorSerializable
        {
            get { return Serialize.BrushToString(_BackgroundColor); }
            set { _BackgroundColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(0f, 100f)]
        [Display(Name = "Background Opacity (%)", Order = 7, GroupName = "Book Map Style")]
        public float _BackgroundColorOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(0f, 100f)]
        [Display(Name = "Big Pending Orders Opacity (%)", Order = 8, GroupName = "Book Map Style")]
        public float _BigPendingOrdersOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(0f, 100f)]
        [Display(Name = "Filter Pending Orders From (%)", Order = 9, GroupName = "Book Map Style")]
        public float _FilterPendingOrdersPer { get; set; }

        [NinjaScriptProperty]
        [Range(0f, 100f)]
        [Display(Name = "Filter Text Pending Orders From (%)", Order = 10, GroupName = "Book Map Style")]
        public float _FilterTextPendingOrdersPer { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10000)]
        [Display(Name = "Aggressive Market Orders Filter (min size)", Order = 11, GroupName = "Book Map Style")]
        public int _AggresiveMarketOrdersFilter { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100000)]
        [Display(Name = "Filter Big Pending Orders (min size)", Order = 12, GroupName = "Book Map Style")]
        public int _FilterBigPendingOrders { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Ladder Range", Order = 13, GroupName = "Book Map Style")]
        public _BookMapLadderRange _LadderRange { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Market Bars Calculation", Order = 14, GroupName = "Book Map Style")]
        public _MarketBarsCalculation _MarketBarsCalculation { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Market Orders Calculation", Order = 15, GroupName = "Book Map Style")]
        public _MarketOrdersCalculation _MarketOrdersCalculation { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Market Cumulative Calculation", Order = 16, GroupName = "Book Map Style")]
        public _MarketCummulativeCalculation _MarketCummulativeCalculation { get; set; }

        [NinjaScriptProperty]
        [Range(50, 1000)]
        [Display(Name = "Book Margin Right (px)", Order = 17, GroupName = "Book Map Style")]
        public int _BookMarginRight { get; set; }

        [NinjaScriptProperty]
        [Range(4f, 24f)]
        [Display(Name = "Min Font Width", Order = 18, GroupName = "Book Map Style")]
        public float _BookmapMinFontWidth { get; set; }

        [NinjaScriptProperty]
        [Range(4f, 24f)]
        [Display(Name = "Min Font Height", Order = 19, GroupName = "Book Map Style")]
        public float _BookmapMinFontHeight { get; set; }

        [NinjaScriptProperty]
        [Range(1.0f, 15.0f)]
        [Display(Name = "Bubble Scale Multiplier", Order = 25, GroupName = "Book Map Style")]
        public float _BubbleScaleMultiplier { get; set; }

        #endregion

        // ────────────────────────────────────────────────────────────────────
        #region Properties — Book Map Heat Gradient

        [NinjaScriptProperty]
        [Display(Name = "Use Heat Gradient", Order = 0, GroupName = "Book Map Heat Gradient")]
        public bool _UseHeatGradient { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Bid Cold Color (low volume)", Order = 1, GroupName = "Book Map Heat Gradient")]
        public Brush _BidColdColor { get; set; }

        [Browsable(false)]
        public string _BidColdColorSerializable
        {
            get { return Serialize.BrushToString(_BidColdColor); }
            set { _BidColdColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Bid Hot Color (high volume)", Order = 2, GroupName = "Book Map Heat Gradient")]
        public Brush _BidHotColor { get; set; }

        [Browsable(false)]
        public string _BidHotColorSerializable
        {
            get { return Serialize.BrushToString(_BidHotColor); }
            set { _BidHotColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Ask Cold Color (low volume)", Order = 3, GroupName = "Book Map Heat Gradient")]
        public Brush _AskColdColor { get; set; }

        [Browsable(false)]
        public string _AskColdColorSerializable
        {
            get { return Serialize.BrushToString(_AskColdColor); }
            set { _AskColdColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Ask Hot Color (high volume)", Order = 4, GroupName = "Book Map Heat Gradient")]
        public Brush _AskHotColor { get; set; }

        [Browsable(false)]
        public string _AskHotColorSerializable
        {
            get { return Serialize.BrushToString(_AskHotColor); }
            set { _AskHotColor = Serialize.StringToBrush(value); }
        }

        #endregion

        // ────────────────────────────────────────────────────────────────────
        #region Properties — Book Map POC

        [NinjaScriptProperty]
        [Display(Name = "Show POC Line", Order = 0, GroupName = "Book Map POC")]
        public bool _ShowPOCLine { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "POC Line Color", Order = 1, GroupName = "Book Map POC")]
        public Brush _POCLineColor { get; set; }

        [Browsable(false)]
        public string _POCLineColorSerializable
        {
            get { return Serialize.BrushToString(_POCLineColor); }
            set { _POCLineColor = Serialize.StringToBrush(value); }
        }

        #endregion

        // ────────────────────────────────────────────────────────────────────
        #region Properties — Book Map Auto Scale

        [NinjaScriptProperty]
        [Display(Name = "Auto Center Price", Order = 0, GroupName = "Book Map Auto Scale")]
        public bool _AutoCenterPrice { get; set; }

        [NinjaScriptProperty]
        [Range(5f, 40f)]
        [Display(Name = "Re-center Threshold (%)", Order = 1, GroupName = "Book Map Auto Scale")]
        public float _AutoCenterThreshold { get; set; }

        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private Bookmap[] cacheBookmap;

        public Bookmap Bookmap(
            int    aggresiveMarketOrdersFilter,
            bool   autoCenterPrice,
            float  autoCenterThreshold,
            float  backgroundColorOpacity,
            float  bigPendingOrdersOpacity,
            int    bookMarginRight,
            float  bookMapMinFontWidth,
            float  bookMapMinFontHeight,
            float  bubbleScaleMultiplier,
            int    filterBigPendingOrders,
            float  filterPendingOrdersPer,
            float  filterTextPendingOrdersPer,
            _BookMapLadderRange           ladderRange,
            _MarketBarsCalculation        marketBarsCalculation,
            _MarketCummulativeCalculation marketCummulativeCalculation,
            _MarketOrdersCalculation      marketOrdersCalculation,
            bool   showPOCLine,
            bool   useHeatGradient)
        {
            return Bookmap(Input,
                aggresiveMarketOrdersFilter, autoCenterPrice, autoCenterThreshold,
                backgroundColorOpacity, bigPendingOrdersOpacity, bookMarginRight,
                bookMapMinFontWidth, bookMapMinFontHeight, bubbleScaleMultiplier,
                filterBigPendingOrders, filterPendingOrdersPer, filterTextPendingOrdersPer,
                ladderRange, marketBarsCalculation, marketCummulativeCalculation,
                marketOrdersCalculation, showPOCLine, useHeatGradient);
        }

        public Bookmap Bookmap(
            NinjaTrader.Data.ISeries<double> input,
            int    aggresiveMarketOrdersFilter,
            bool   autoCenterPrice,
            float  autoCenterThreshold,
            float  backgroundColorOpacity,
            float  bigPendingOrdersOpacity,
            int    bookMarginRight,
            float  bookMapMinFontWidth,
            float  bookMapMinFontHeight,
            float  bubbleScaleMultiplier,
            int    filterBigPendingOrders,
            float  filterPendingOrdersPer,
            float  filterTextPendingOrdersPer,
            _BookMapLadderRange           ladderRange,
            _MarketBarsCalculation        marketBarsCalculation,
            _MarketCummulativeCalculation marketCummulativeCalculation,
            _MarketOrdersCalculation      marketOrdersCalculation,
            bool   showPOCLine,
            bool   useHeatGradient)
        {
            if (cacheBookmap != null)
            {
                for (int idx = 0; idx < cacheBookmap.Length; idx++)
                {
                    if (cacheBookmap[idx] != null
                        && cacheBookmap[idx]._AggresiveMarketOrdersFilter == aggresiveMarketOrdersFilter
                        && cacheBookmap[idx]._AutoCenterPrice             == autoCenterPrice
                        && cacheBookmap[idx]._LadderRange                 == ladderRange
                        && cacheBookmap[idx]._MarketBarsCalculation       == marketBarsCalculation
                        && cacheBookmap[idx]._ShowPOCLine                 == showPOCLine
                        && cacheBookmap[idx]._UseHeatGradient             == useHeatGradient
                        && cacheBookmap[idx].EqualsInput(input))
                    {
                        return cacheBookmap[idx];
                    }
                }
            }

            var indicator = new Bookmap();
            indicator._AggresiveMarketOrdersFilter  = aggresiveMarketOrdersFilter;
            indicator._AutoCenterPrice              = autoCenterPrice;
            indicator._AutoCenterThreshold          = autoCenterThreshold;
            indicator._BackgroundColorOpacity       = backgroundColorOpacity;
            indicator._BigPendingOrdersOpacity      = bigPendingOrdersOpacity;
            indicator._BookMarginRight              = bookMarginRight;
            indicator._BookmapMinFontWidth          = bookMapMinFontWidth;
            indicator._BookmapMinFontHeight         = bookMapMinFontHeight;
            indicator._BubbleScaleMultiplier        = bubbleScaleMultiplier;
            indicator._FilterBigPendingOrders       = filterBigPendingOrders;
            indicator._FilterPendingOrdersPer       = filterPendingOrdersPer;
            indicator._FilterTextPendingOrdersPer   = filterTextPendingOrdersPer;
            indicator._LadderRange                  = ladderRange;
            indicator._MarketBarsCalculation        = marketBarsCalculation;
            indicator._MarketCummulativeCalculation = marketCummulativeCalculation;
            indicator._MarketOrdersCalculation      = marketOrdersCalculation;
            indicator._ShowPOCLine                  = showPOCLine;
            indicator._UseHeatGradient              = useHeatGradient;

            AddChartIndicator(indicator);
            return indicator;
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.Bookmap Bookmap(
            int    aggresiveMarketOrdersFilter,
            bool   autoCenterPrice,
            float  autoCenterThreshold,
            float  backgroundColorOpacity,
            float  bigPendingOrdersOpacity,
            int    bookMarginRight,
            float  bookMapMinFontWidth,
            float  bookMapMinFontHeight,
            float  bubbleScaleMultiplier,
            int    filterBigPendingOrders,
            float  filterPendingOrdersPer,
            float  filterTextPendingOrdersPer,
            _BookMapLadderRange           ladderRange,
            _MarketBarsCalculation        marketBarsCalculation,
            _MarketCummulativeCalculation marketCummulativeCalculation,
            _MarketOrdersCalculation      marketOrdersCalculation,
            bool   showPOCLine,
            bool   useHeatGradient)
        {
            return indicator.Bookmap(Input,
                aggresiveMarketOrdersFilter, autoCenterPrice, autoCenterThreshold,
                backgroundColorOpacity, bigPendingOrdersOpacity, bookMarginRight,
                bookMapMinFontWidth, bookMapMinFontHeight, bubbleScaleMultiplier,
                filterBigPendingOrders, filterPendingOrdersPer, filterTextPendingOrdersPer,
                ladderRange, marketBarsCalculation, marketCummulativeCalculation,
                marketOrdersCalculation, showPOCLine, useHeatGradient);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.Bookmap Bookmap(
            int    aggresiveMarketOrdersFilter,
            bool   autoCenterPrice,
            float  autoCenterThreshold,
            float  backgroundColorOpacity,
            float  bigPendingOrdersOpacity,
            int    bookMarginRight,
            float  bookMapMinFontWidth,
            float  bookMapMinFontHeight,
            float  bubbleScaleMultiplier,
            int    filterBigPendingOrders,
            float  filterPendingOrdersPer,
            float  filterTextPendingOrdersPer,
            _BookMapLadderRange           ladderRange,
            _MarketBarsCalculation        marketBarsCalculation,
            _MarketCummulativeCalculation marketCummulativeCalculation,
            _MarketOrdersCalculation      marketOrdersCalculation,
            bool   showPOCLine,
            bool   useHeatGradient)
        {
            return indicator.Bookmap(Input,
                aggresiveMarketOrdersFilter, autoCenterPrice, autoCenterThreshold,
                backgroundColorOpacity, bigPendingOrdersOpacity, bookMarginRight,
                bookMapMinFontWidth, bookMapMinFontHeight, bubbleScaleMultiplier,
                filterBigPendingOrders, filterPendingOrdersPer, filterTextPendingOrdersPer,
                ladderRange, marketBarsCalculation, marketCummulativeCalculation,
                marketOrdersCalculation, showPOCLine, useHeatGradient);
        }

        public Indicators.Bookmap Bookmap(
            NinjaTrader.Data.ISeries<double> input,
            int    aggresiveMarketOrdersFilter,
            bool   autoCenterPrice,
            float  autoCenterThreshold,
            float  backgroundColorOpacity,
            float  bigPendingOrdersOpacity,
            int    bookMarginRight,
            float  bookMapMinFontWidth,
            float  bookMapMinFontHeight,
            float  bubbleScaleMultiplier,
            int    filterBigPendingOrders,
            float  filterPendingOrdersPer,
            float  filterTextPendingOrdersPer,
            _BookMapLadderRange           ladderRange,
            _MarketBarsCalculation        marketBarsCalculation,
            _MarketCummulativeCalculation marketCummulativeCalculation,
            _MarketOrdersCalculation      marketOrdersCalculation,
            bool   showPOCLine,
            bool   useHeatGradient)
        {
            return indicator.Bookmap(input,
                aggresiveMarketOrdersFilter, autoCenterPrice, autoCenterThreshold,
                backgroundColorOpacity, bigPendingOrdersOpacity, bookMarginRight,
                bookMapMinFontWidth, bookMapMinFontHeight, bubbleScaleMultiplier,
                filterBigPendingOrders, filterPendingOrdersPer, filterTextPendingOrdersPer,
                ladderRange, marketBarsCalculation, marketCummulativeCalculation,
                marketOrdersCalculation, showPOCLine, useHeatGradient);
        }
    }
}

#endregion
