// FibonacciRetracement.cs
// GPU-Rendered Fibonacci Retracement Indicator for NinjaTrader 8
//
// Automatically identifies Fibonacci retracement zones by detecting trend transitions
// (uptrend → downtrend or downtrend → uptrend) and anchoring Fibonacci levels to:
//   100% = Highest swing high (in uptrend) or Lowest swing low (in downtrend)
//     0% = First swing point of the new opposite trend
//
// Return signals are triggered when price retraces to 38.2%, 50%, or 61.8% levels.
// Full SharpDX Direct2D1 GPU rendering with optimized resource management.

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
#endregion

// ── Enums declared OUTSIDE the namespace (NT8 auto-generated code resolution requirement) ──

/// <summary>Lifecycle state of a Fibonacci zone.</summary>
public enum FibonacciZoneState
{
    Idle,
    HighestSwingIdentified,
    FirstSwingIdentified,
    ZonesActive,
    SignalTriggered
}

/// <summary>Broad market environment classification.</summary>
public enum MarketEnvironment
{
    Trending,
    Sideways,
    Transitioning
}

/// <summary>Type of Return signal generated at a Fibonacci level.</summary>
public enum SignalType
{
    BullishReturn,
    BearishReturn,
    None
}

/// <summary>Direction of a Fibonacci zone.</summary>
public enum FibZoneDirection
{
    Bullish,  // Downtrend → Uptrend: lowest swing low (100%) → first swing high (0%)
    Bearish   // Uptrend → Downtrend: highest swing high (100%) → first swing low (0%)
}

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// GPU-Rendered Fibonacci Retracement indicator.
    /// Detects trend transitions, draws Fibonacci zones anchored to key swing points,
    /// and generates Return signals when price retraces to golden ratio levels.
    /// </summary>
    public class FibonacciRetracement : Indicator
    {
        // ── Private Data Structures ─────────────────────────────────────────────────

        private struct SwingPoint
        {
            public int    BarIndex;
            public double Price;
            public bool   IsHigh;
        }

        private class FibonacciZone
        {
            public int              StartBar;   // bar index of the 100% swing point
            public int              EndBar;     // extends to current bar (updated each tick)
            public double           Price100;   // 100% level — extreme swing price
            public double           Price0;     // 0% level — first opposite swing price
            public FibZoneDirection Direction;
            public FibonacciZoneState State;
            public List<int>        SignalBars; // absolute bar indices where Return signals fired

            public FibonacciZone()
            {
                SignalBars = new List<int>();
                State      = FibonacciZoneState.ZonesActive;
            }

            /// <summary>Returns price at the given Fibonacci ratio (e.g. 0.382, 0.500, 0.618).</summary>
            public double GetLevel(double ratio)
            {
                return Price0 + (Price100 - Price0) * ratio;
            }
        }

        private class ReturnSignal
        {
            public int       BarIndex;
            public double    Price;
            public SignalType Type;
            public double    FibRatio;  // which ratio triggered (0.382, 0.50, 0.618)
        }

        // ── Private Fields ──────────────────────────────────────────────────────────

        private List<SwingPoint>    swingHighs;
        private List<SwingPoint>    swingLows;
        private List<FibonacciZone> activeZones;
        private Queue<ReturnSignal> recentSignals;

        // Trend tracking state
        private bool   inUptrend;
        private bool   trendEstablished;
        private double highestHighPrice;
        private int    highestHighBar;
        private double lowestLowPrice;
        private int    lowestLowBar;

        // Per-bar signal series for rendering lookup
        private Series<int> signalSeries;  // 1 = bullish return, -1 = bearish return, 0 = none

        // SharpDX GPU resources (created once per render target, disposed on target change)
        private SharpDX.Direct2D1.SolidColorBrush dxBullishLineBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBearishLineBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBullishFillBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBearishFillBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBullishSignalBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBearishSignalBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxTextBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxPanelBgBrush;
        private SharpDX.Direct2D1.StrokeStyle      dxDashedStyle;
        private SharpDX.DirectWrite.TextFormat      dxLabelFormat;
        private bool dxResourcesCreated;

        // ── User-Configurable Parameters ────────────────────────────────────────────

        #region Properties — Detection

        [NinjaScriptProperty]
        [Range(3, 50)]
        [Display(Name = "Swing Lookback (bars)", Order = 1, GroupName = "1. Detection",
                 Description = "Bars on each side required to confirm a swing high or low. Higher = fewer but more significant swings.")]
        public int SwingLookback { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Zone Width (ticks)", Order = 2, GroupName = "1. Detection",
                 Description = "Price tolerance in ticks for triggering a Return signal at a Fibonacci level.")]
        public int ZoneWidthTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Max Active Zones", Order = 3, GroupName = "1. Detection",
                 Description = "Maximum number of Fibonacci zones to keep visible. Oldest zones are removed first.")]
        public int MaxActiveZones { get; set; }

        #endregion

        #region Properties — Fibonacci Levels

        [NinjaScriptProperty]
        [Display(Name = "Show 23.6% (dashed)", Order = 1, GroupName = "2. Fibonacci Levels")]
        public bool ShowLevel236 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show 38.2% (solid)", Order = 2, GroupName = "2. Fibonacci Levels")]
        public bool ShowLevel382 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show 50.0% (solid)", Order = 3, GroupName = "2. Fibonacci Levels")]
        public bool ShowLevel500 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show 61.8% (solid)", Order = 4, GroupName = "2. Fibonacci Levels")]
        public bool ShowLevel618 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show 78.6% (dashed)", Order = 5, GroupName = "2. Fibonacci Levels")]
        public bool ShowLevel786 { get; set; }

        #endregion

        #region Properties — Visuals

        [NinjaScriptProperty]
        [Display(Name = "Bullish Zone Color", Order = 1, GroupName = "3. Visuals")]
        public System.Windows.Media.Brush BullishColor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Bearish Zone Color", Order = 2, GroupName = "3. Visuals")]
        public System.Windows.Media.Brush BearishColor { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Fill Opacity %", Order = 3, GroupName = "3. Visuals",
                 Description = "Opacity of the zone fill background (0 = transparent, 100 = solid).")]
        public int FillOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 5.0)]
        [Display(Name = "Line Width (px)", Order = 4, GroupName = "3. Visuals",
                 Description = "Thickness of Fibonacci level lines in pixels.")]
        public double LineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Level Labels", Order = 5, GroupName = "3. Visuals",
                 Description = "Display percentage and price labels next to each Fibonacci level.")]
        public bool ShowLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Signal Markers", Order = 6, GroupName = "3. Visuals",
                 Description = "Draw triangle markers on bars where Return signals are triggered.")]
        public bool ShowSignalMarkers { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Signal Panel", Order = 7, GroupName = "3. Visuals",
                 Description = "Display a panel in the top-left corner showing the 5 most recent Return signals.")]
        public bool ShowSignalPanel { get; set; }

        #endregion

        #region Properties — Alerts

        [NinjaScriptProperty]
        [Display(Name = "Enable Alerts", Order = 1, GroupName = "4. Alerts",
                 Description = "Play an audio alert and show a chart notification when a Return signal triggers.")]
        public bool EnableAlerts { get; set; }

        #endregion

        // ── OnStateChange ───────────────────────────────────────────────────────────

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = @"GPU-Rendered Fibonacci Retracement — Automatically detects swing highs/lows at trend intersections, draws Fibonacci zones, and generates Return signals at golden ratio levels (38.2%, 50%, 61.8%).";
                Name                     = "FibonacciRetracement";
                Calculate                = Calculate.OnBarClose;
                IsOverlay                = true;
                DisplayInDataBox         = false;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = true;

                // Detection defaults
                SwingLookback  = 10;
                ZoneWidthTicks = 2;
                MaxActiveZones = 5;

                // Fibonacci level defaults — all on
                ShowLevel236 = true;
                ShowLevel382 = true;
                ShowLevel500 = true;
                ShowLevel618 = true;
                ShowLevel786 = true;

                // Visual defaults
                BullishColor      = Brushes.LimeGreen;
                BearishColor      = Brushes.Crimson;
                FillOpacity       = 15;
                LineWidth         = 1.5;
                ShowLabels        = true;
                ShowSignalMarkers = true;
                ShowSignalPanel   = true;

                // Alert defaults
                EnableAlerts = false;
            }
            else if (State == State.DataLoaded)
            {
                swingHighs    = new List<SwingPoint>();
                swingLows     = new List<SwingPoint>();
                activeZones   = new List<FibonacciZone>();
                recentSignals = new Queue<ReturnSignal>();

                // Trend state initialisation
                highestHighPrice = double.MinValue;
                highestHighBar   = -1;
                lowestLowPrice   = double.MaxValue;
                lowestLowBar     = -1;
                inUptrend        = true;
                trendEstablished = false;

                signalSeries = new Series<int>(this, MaximumBarsLookBack.TwoHundredFiftySix);
            }
            else if (State == State.Terminated)
            {
                DisposeSharpDXResources();
            }
        }

        // ── OnBarUpdate ─────────────────────────────────────────────────────────────

        protected override void OnBarUpdate()
        {
            signalSeries[0] = 0;

            // Require enough bars for swing detection on both sides
            if (CurrentBar < SwingLookback * 2 + 1)
                return;

            // Step 1: Detect confirmed swing highs and lows
            DetectSwingHighs();
            DetectSwingLows();

            // Step 2: Identify trend transitions and generate Fibonacci zones
            IdentifyTrendTransitions();

            // Step 3: Check active zones for Return signals
            CheckRetracementSignals();

            // Keep EndBar current so zones extend to the right edge
            foreach (var zone in activeZones)
                zone.EndBar = CurrentBar;
        }

        // ── Swing High / Low Detection ──────────────────────────────────────────────

        /// <summary>
        /// Detects confirmed pivot highs: the bar at (CurrentBar - SwingLookback) must be
        /// strictly higher than every bar within SwingLookback bars on either side.
        /// </summary>
        private void DetectSwingHighs()
        {
            int pivot = CurrentBar - SwingLookback;
            if (pivot < SwingLookback)
                return;

            double pivotHigh = High.GetValueAt(pivot);

            for (int i = pivot - SwingLookback; i <= pivot + SwingLookback; i++)
            {
                if (i == pivot) continue;
                if (i < 0 || i > CurrentBar) continue;
                if (High.GetValueAt(i) >= pivotHigh)
                    return;  // not a swing high
            }

            // Enforce minimum spacing between confirmed swing highs
            if (swingHighs.Count > 0 && pivot - swingHighs[swingHighs.Count - 1].BarIndex < SwingLookback)
                return;

            swingHighs.Add(new SwingPoint { BarIndex = pivot, Price = pivotHigh, IsHigh = true });
        }

        /// <summary>
        /// Detects confirmed pivot lows: the bar at (CurrentBar - SwingLookback) must be
        /// strictly lower than every bar within SwingLookback bars on either side.
        /// </summary>
        private void DetectSwingLows()
        {
            int pivot = CurrentBar - SwingLookback;
            if (pivot < SwingLookback)
                return;

            double pivotLow = Low.GetValueAt(pivot);

            for (int i = pivot - SwingLookback; i <= pivot + SwingLookback; i++)
            {
                if (i == pivot) continue;
                if (i < 0 || i > CurrentBar) continue;
                if (Low.GetValueAt(i) <= pivotLow)
                    return;  // not a swing low
            }

            if (swingLows.Count > 0 && pivot - swingLows[swingLows.Count - 1].BarIndex < SwingLookback)
                return;

            swingLows.Add(new SwingPoint { BarIndex = pivot, Price = pivotLow, IsHigh = false });
        }

        // ── Trend Transition & Zone Generation ──────────────────────────────────────

        /// <summary>
        /// Analyses the two most recent confirmed swing highs and lows to determine whether
        /// a trend transition has occurred, then generates a Fibonacci zone accordingly.
        /// </summary>
        private void IdentifyTrendTransitions()
        {
            if (swingHighs.Count < 2 || swingLows.Count < 2)
                return;

            SwingPoint lastHigh = swingHighs[swingHighs.Count - 1];
            SwingPoint prevHigh = swingHighs[swingHighs.Count - 2];
            SwingPoint lastLow  = swingLows[swingLows.Count - 1];
            SwingPoint prevLow  = swingLows[swingLows.Count - 2];

            // Establish initial trend direction on first opportunity
            if (!trendEstablished)
            {
                if (lastHigh.Price > prevHigh.Price && lastLow.Price > prevLow.Price)
                {
                    inUptrend        = true;
                    trendEstablished = true;
                    highestHighPrice = lastHigh.Price;
                    highestHighBar   = lastHigh.BarIndex;
                }
                else if (lastHigh.Price < prevHigh.Price && lastLow.Price < prevLow.Price)
                {
                    inUptrend        = false;
                    trendEstablished = true;
                    lowestLowPrice   = lastLow.Price;
                    lowestLowBar     = lastLow.BarIndex;
                }
                return;
            }

            bool higherHighs = lastHigh.Price > prevHigh.Price;
            bool higherLows  = lastLow.Price  > prevLow.Price;
            bool lowerHighs  = lastHigh.Price < prevHigh.Price;
            bool lowerLows   = lastLow.Price  < prevLow.Price;

            if (inUptrend)
            {
                // Update the highest high while still in uptrend
                if (lastHigh.Price > highestHighPrice)
                {
                    highestHighPrice = lastHigh.Price;
                    highestHighBar   = lastHigh.BarIndex;
                }

                // Transition: uptrend → downtrend (lower highs AND lower lows)
                if (lowerHighs && lowerLows && highestHighBar >= 0 && lastLow.BarIndex > highestHighBar)
                {
                    CreateBearishZone(highestHighBar, highestHighPrice, lastLow.BarIndex, lastLow.Price);

                    inUptrend        = false;
                    lowestLowPrice   = lastLow.Price;
                    lowestLowBar     = lastLow.BarIndex;
                    highestHighPrice = double.MinValue;
                    highestHighBar   = -1;
                }
            }
            else
            {
                // Update the lowest low while still in downtrend
                if (lastLow.Price < lowestLowPrice)
                {
                    lowestLowPrice = lastLow.Price;
                    lowestLowBar   = lastLow.BarIndex;
                }

                // Transition: downtrend → uptrend (higher highs AND higher lows)
                if (higherHighs && higherLows && lowestLowBar >= 0 && lastHigh.BarIndex > lowestLowBar)
                {
                    CreateBullishZone(lowestLowBar, lowestLowPrice, lastHigh.BarIndex, lastHigh.Price);

                    inUptrend        = true;
                    highestHighPrice = lastHigh.Price;
                    highestHighBar   = lastHigh.BarIndex;
                    lowestLowPrice   = double.MaxValue;
                    lowestLowBar     = -1;
                }
            }
        }

        /// <summary>
        /// Creates a Bearish Fibonacci zone.
        /// 100% = highest swing high of the completed uptrend.
        /// 0%   = first swing low of the new downtrend.
        /// Retracements back up toward 100% are bearish Return signals.
        /// </summary>
        private void CreateBearishZone(int highBar, double highPrice, int lowBar, double lowPrice)
        {
            var zone = new FibonacciZone
            {
                StartBar  = highBar,
                EndBar    = CurrentBar,
                Price100  = highPrice,
                Price0    = lowPrice,
                Direction = FibZoneDirection.Bearish,
                State     = FibonacciZoneState.ZonesActive
            };
            AddZone(zone);
        }

        /// <summary>
        /// Creates a Bullish Fibonacci zone.
        /// 100% = lowest swing low of the completed downtrend.
        /// 0%   = first swing high of the new uptrend.
        /// Retracements back down toward 100% are bullish Return signals.
        /// </summary>
        private void CreateBullishZone(int lowBar, double lowPrice, int highBar, double highPrice)
        {
            var zone = new FibonacciZone
            {
                StartBar  = lowBar,
                EndBar    = CurrentBar,
                Price100  = lowPrice,
                Price0    = highPrice,
                Direction = FibZoneDirection.Bullish,
                State     = FibonacciZoneState.ZonesActive
            };
            AddZone(zone);
        }

        private void AddZone(FibonacciZone zone)
        {
            while (activeZones.Count >= MaxActiveZones)
                activeZones.RemoveAt(0);

            activeZones.Add(zone);
        }

        // ── Return Signal Detection ─────────────────────────────────────────────────

        /// <summary>
        /// For each active zone, checks whether the current close price has retraced to
        /// one of the three primary Fibonacci levels (38.2%, 50%, 61.8%).
        /// Fires a Return signal when price is within ZoneWidthTicks of a level.
        /// </summary>
        private void CheckRetracementSignals()
        {
            if (activeZones == null || activeZones.Count == 0)
                return;

            double tolerance  = ZoneWidthTicks * TickSize;
            double closePrice = Close[0];

            // Primary signal levels only (solid lines)
            double[] primaryLevels = { 0.382, 0.500, 0.618 };

            foreach (var zone in activeZones)
            {
                switch (zone.State)
                {
                    case FibonacciZoneState.ZonesActive:
                    case FibonacciZoneState.SignalTriggered:
                        break;
                    default:
                        continue;
                }

                bool resetState = true;

                foreach (double ratio in primaryLevels)
                {
                    double levelPrice = zone.GetLevel(ratio);

                    if (Math.Abs(closePrice - levelPrice) <= tolerance)
                    {
                        resetState = false;

                        // One signal per bar per zone
                        if (zone.SignalBars.Contains(CurrentBar))
                            continue;

                        zone.SignalBars.Add(CurrentBar);
                        zone.State = FibonacciZoneState.SignalTriggered;

                        SignalType sigType = zone.Direction == FibZoneDirection.Bullish
                            ? SignalType.BullishReturn
                            : SignalType.BearishReturn;

                        var signal = new ReturnSignal
                        {
                            BarIndex = CurrentBar,
                            Price    = closePrice,
                            FibRatio = ratio,
                            Type     = sigType
                        };

                        while (recentSignals.Count >= 20)
                            recentSignals.Dequeue();
                        recentSignals.Enqueue(signal);

                        signalSeries[0] = sigType == SignalType.BullishReturn ? 1 : -1;

                        if (EnableAlerts)
                        {
                            string msg = string.Format(
                                "Fibonacci {0} at {1:P1} — Price: {2}",
                                sigType, ratio, closePrice.ToString("F2"));

                            Alert("FibReturn", Priority.High, msg,
                                  NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav",
                                  10, Brushes.White, Brushes.DarkBlue);
                        }
                    }
                }

                // Reset state when price moves away from all primary levels
                if (resetState && zone.State == FibonacciZoneState.SignalTriggered)
                    zone.State = FibonacciZoneState.ZonesActive;
            }
        }

        // ── SharpDX Resource Management ─────────────────────────────────────────────

        public override void OnRenderTargetChanged()
        {
            DisposeSharpDXResources();
        }

        private SharpDX.Color4 ToColor4(System.Windows.Media.Brush wpfBrush, float alpha)
        {
            if (wpfBrush is System.Windows.Media.SolidColorBrush scb)
            {
                System.Windows.Media.Color c = scb.Color;
                return new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, alpha);
            }
            return new SharpDX.Color4(0.5f, 0.5f, 0.5f, alpha);
        }

        private void CreateSharpDXResources(SharpDX.Direct2D1.RenderTarget rt)
        {
            if (dxResourcesCreated || rt == null)
                return;

            try
            {
                float fillAlpha = Math.Max(0f, Math.Min(1f, FillOpacity / 100f));

                dxBullishLineBrush   = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(BullishColor, 0.90f));
                dxBearishLineBrush   = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(BearishColor, 0.90f));
                dxBullishFillBrush   = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(BullishColor, fillAlpha));
                dxBearishFillBrush   = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(BearishColor, fillAlpha));
                dxBullishSignalBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0.20f, 1.00f, 0.40f, 1.00f));
                dxBearishSignalBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(1.00f, 0.20f, 0.20f, 1.00f));
                dxTextBrush          = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(1.00f, 1.00f, 1.00f, 0.85f));
                dxPanelBgBrush       = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0.00f, 0.00f, 0.00f, 0.60f));

                // Dashed stroke style for secondary Fibonacci levels (23.6%, 78.6%)
                var dashProps = new SharpDX.Direct2D1.StrokeStyleProperties
                {
                    DashStyle  = SharpDX.Direct2D1.DashStyle.Dash,
                    DashOffset = 0f
                };
                dxDashedStyle = new SharpDX.Direct2D1.StrokeStyle(rt.Factory, dashProps);

                // Label text format using NT8's shared DirectWrite factory
                dxLabelFormat = new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory,
                    "Arial",
                    SharpDX.DirectWrite.FontWeight.Normal,
                    SharpDX.DirectWrite.FontStyle.Normal,
                    SharpDX.DirectWrite.FontStretch.Normal,
                    10f);

                dxResourcesCreated = true;
            }
            catch (Exception ex)
            {
                Print("FibonacciRetracement: SharpDX resource creation failed — " + ex.Message);
            }
        }

        private void DisposeSharpDXResources()
        {
            SafeDispose(ref dxBullishLineBrush);
            SafeDispose(ref dxBearishLineBrush);
            SafeDispose(ref dxBullishFillBrush);
            SafeDispose(ref dxBearishFillBrush);
            SafeDispose(ref dxBullishSignalBrush);
            SafeDispose(ref dxBearishSignalBrush);
            SafeDispose(ref dxTextBrush);
            SafeDispose(ref dxPanelBgBrush);
            SafeDispose(ref dxDashedStyle);
            SafeDispose(ref dxLabelFormat);
            dxResourcesCreated = false;
        }

        private void SafeDispose<T>(ref T resource) where T : class, IDisposable
        {
            if (resource != null) { resource.Dispose(); resource = null; }
        }

        // ── OnRender — GPU Drawing ──────────────────────────────────────────────────

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (chartControl == null || chartScale == null || ChartBars == null || Bars == null)
                return;

            var rt = RenderTarget;
            if (rt == null)
                return;

            if (!dxResourcesCreated)
                CreateSharpDXResources(rt);

            if (!dxResourcesCreated)
                return;

            if (activeZones == null || activeZones.Count == 0)
                return;

            int firstBar = ChartBars.FromIndex;
            int lastBar  = ChartBars.ToIndex;

            // Draw Fibonacci zones from oldest to newest (newest on top)
            foreach (var zone in activeZones)
            {
                if (zone == null) continue;
                RenderZone(rt, chartControl, chartScale, zone, firstBar, lastBar);
            }

            // Overlay: recent signal panel
            if (ShowSignalPanel && recentSignals != null && recentSignals.Count > 0)
                RenderSignalPanel(rt);
        }

        // ── Zone Rendering ──────────────────────────────────────────────────────────

        private void RenderZone(
            SharpDX.Direct2D1.RenderTarget rt,
            ChartControl cc,
            ChartScale cs,
            FibonacciZone zone,
            int firstBar,
            int lastBar)
        {
            bool isBullish = zone.Direction == FibZoneDirection.Bullish;
            var  lineBrush = isBullish ? dxBullishLineBrush : dxBearishLineBrush;
            var  fillBrush = isBullish ? dxBullishFillBrush : dxBearishFillBrush;

            if (lineBrush == null || fillBrush == null)
                return;

            // Clamp to visible bar range
            int visStart = Math.Max(zone.StartBar, firstBar);
            int visEnd   = Math.Min(zone.EndBar,   lastBar);

            if (visStart > lastBar || visEnd < firstBar)
                return;

            float xStart = cc.GetXByBarIndex(ChartBars, visStart);
            float xEnd   = cc.GetXByBarIndex(ChartBars, visEnd);

            if (xEnd <= xStart) xEnd = xStart + 2f;

            // Zone fill — translucent rectangle from 0% to 100%
            float y0   = cs.GetYByValue(zone.Price0);
            float y100 = cs.GetYByValue(zone.Price100);
            float yTop = Math.Min(y0, y100);
            float yBot = Math.Max(y0, y100);

            if (yBot - yTop > 1f)
            {
                rt.FillRectangle(
                    new SharpDX.RectangleF(xStart, yTop, xEnd - xStart, yBot - yTop),
                    fillBrush);
            }

            // Fibonacci level definitions
            // ratio | enabled flag       | solid (true) or dashed (false) | label text
            double[] ratios        = { 0.000, 0.236,        0.382,        0.500,        0.618,        0.786,        1.000 };
            bool[]   enabled       = { true,  ShowLevel236, ShowLevel382, ShowLevel500, ShowLevel618, ShowLevel786, true  };
            bool[]   isPrimary     = { true,  false,        true,         true,         true,         false,        true  };
            string[] labels        = { "0%",  "23.6%",      "38.2%",      "50.0%",      "61.8%",      "78.6%",      "100%" };

            float lw = (float)LineWidth;

            for (int i = 0; i < ratios.Length; i++)
            {
                if (!enabled[i]) continue;

                double levelPrice = zone.GetLevel(ratios[i]);
                float  yLevel     = cs.GetYByValue(levelPrice);

                var p1 = new SharpDX.Vector2(xStart, yLevel);
                var p2 = new SharpDX.Vector2(xEnd,   yLevel);

                if (isPrimary[i])
                    rt.DrawLine(p1, p2, lineBrush, lw);
                else
                    rt.DrawLine(p1, p2, lineBrush, lw * 0.65f, dxDashedStyle);

                // Price label at right edge of zone
                if (ShowLabels && dxTextBrush != null && dxLabelFormat != null)
                {
                    string labelText = string.Format("{0}  {1}", labels[i], levelPrice.ToString("F2"));

                    using (var layout = new SharpDX.DirectWrite.TextLayout(
                        NinjaTrader.Core.Globals.DirectWriteFactory,
                        labelText,
                        dxLabelFormat,
                        200f, 18f))
                    {
                        rt.DrawTextLayout(
                            new SharpDX.Vector2(xEnd + 3f, yLevel - 9f),
                            layout,
                            dxTextBrush);
                    }
                }
            }

            // Signal markers: triangles at bars where Return signals fired
            if (ShowSignalMarkers && zone.SignalBars.Count > 0)
            {
                var sigBrush = isBullish ? dxBullishSignalBrush : dxBearishSignalBrush;
                if (sigBrush == null) return;

                foreach (int sigBar in zone.SignalBars)
                {
                    if (sigBar < firstBar || sigBar > lastBar) continue;

                    float sx       = cc.GetXByBarIndex(ChartBars, sigBar);
                    double sigClose = Bars.GetClose(sigBar);
                    float  sy       = cs.GetYByValue(sigClose);

                    if (isBullish)
                        DrawTriangle(rt, sigBrush, sx, sy + 14f, 8f, true);   // arrow up below price
                    else
                        DrawTriangle(rt, sigBrush, sx, sy - 14f, 8f, false);  // arrow down above price
                }
            }
        }

        // ── Signal Triangle Helper ──────────────────────────────────────────────────

        private void DrawTriangle(
            SharpDX.Direct2D1.RenderTarget rt,
            SharpDX.Direct2D1.SolidColorBrush brush,
            float cx, float cy, float size, bool pointUp)
        {
            float half = size * 0.5f;

            using (var geo = new SharpDX.Direct2D1.PathGeometry(rt.Factory))
            {
                using (var sink = geo.Open())
                {
                    if (pointUp)
                    {
                        sink.BeginFigure(new SharpDX.Vector2(cx,        cy - size), SharpDX.Direct2D1.FigureBegin.Filled);
                        sink.AddLine(    new SharpDX.Vector2(cx + half,  cy));
                        sink.AddLine(    new SharpDX.Vector2(cx - half,  cy));
                    }
                    else
                    {
                        sink.BeginFigure(new SharpDX.Vector2(cx,        cy + size), SharpDX.Direct2D1.FigureBegin.Filled);
                        sink.AddLine(    new SharpDX.Vector2(cx + half,  cy));
                        sink.AddLine(    new SharpDX.Vector2(cx - half,  cy));
                    }

                    sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
                    sink.Close();
                }
                rt.FillGeometry(geo, brush);
            }
        }

        // ── Signal History Panel ────────────────────────────────────────────────────

        private void RenderSignalPanel(SharpDX.Direct2D1.RenderTarget rt)
        {
            if (dxTextBrush == null || dxLabelFormat == null || dxPanelBgBrush == null)
                return;

            ReturnSignal[] signals = recentSignals.ToArray();
            int            count   = Math.Min(signals.Length, 5);

            if (count == 0) return;

            const float panelX  = 8f;
            const float panelY  = 8f;
            const float lineH   = 17f;
            const float panelW  = 230f;
            float       panelH  = lineH * (count + 1) + 10f;

            // Panel background
            rt.FillRectangle(new SharpDX.RectangleF(panelX, panelY, panelW, panelH), dxPanelBgBrush);

            // Header row
            using (var headerLayout = new SharpDX.DirectWrite.TextLayout(
                NinjaTrader.Core.Globals.DirectWriteFactory,
                "Fibonacci Return Signals",
                dxLabelFormat,
                panelW - 8f, lineH))
            {
                rt.DrawTextLayout(new SharpDX.Vector2(panelX + 4f, panelY + 4f), headerLayout, dxTextBrush);
            }

            // Signal rows — most recent last in queue, display newest at top
            for (int i = count - 1; i >= 0; i--)
            {
                ReturnSignal sig   = signals[i];
                var          brush = sig.Type == SignalType.BullishReturn ? dxBullishSignalBrush : dxBearishSignalBrush;

                string arrow = sig.Type == SignalType.BullishReturn ? "BUY" : "SELL";
                string row   = string.Format("{0}  {1:P1}  @  {2}",
                    arrow, sig.FibRatio, sig.Price.ToString("F2"));

                float rowY = panelY + 4f + lineH * (count - i);

                using (var layout = new SharpDX.DirectWrite.TextLayout(
                    NinjaTrader.Core.Globals.DirectWriteFactory,
                    row, dxLabelFormat, panelW - 8f, lineH))
                {
                    rt.DrawTextLayout(
                        new SharpDX.Vector2(panelX + 4f, rowY),
                        layout,
                        brush ?? dxTextBrush);
                }
            }
        }

        // ── Series Accessor (optional external use) ─────────────────────────────────

        [Browsable(false)]
        [XmlIgnore]
        public Series<int> ReturnSignals
        {
            get { return signalSeries; }
        }
    }
}
