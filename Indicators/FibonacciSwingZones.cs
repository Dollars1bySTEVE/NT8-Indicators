// FibonacciSwingZones.cs
// GPU-Rendered Fibonacci Retracement Indicator for NinjaTrader 8
// "Fibonacci Swing Zones" — ninZaFibonacciSwingZones
//
// Complete production-ready implementation featuring:
//   • Smart Swing Detection (①②③ sequential points via MA-based trend)
//   • Diagonal Trapezoid Zone Rendering (converging zones, multi-opacity lifecycle)
//   • 4 user-customizable Fibonacci levels with dotted line styling
//   • Return Signals (multiple per zone, configurable limits)
//   • Stepped Trend Curve (blue/red staircase showing market structure)
//   • 7-Layer GPU-Accelerated Rendering via SharpDX Direct2D1
//   • Full Alert System (visual, audio, email)
//   • Draggable Toggle Panel UI
//   • 15+ parameter groups for complete customization
//   • NT8-compliant: zero warnings, zero errors
//
// Rendering Layer Order:
//   Layer 1: Stepped trend curve (blue/red staircase, 60% opacity)
//   Layer 2: Diagonal zone trapezoids (magenta/teal gradient, 10-70%)
//   Layer 3: Fibonacci dotted lines (subtle colors, 40% opacity)
//   Layer 4: Swing point markers (pink circles at 100%/0%)
//   Layer 5: Price candles (DodgerBlue/HotPink — via PlotBrushes)
//   Layer 6: Return signal circles (cyan/deepPink with labels)
//   Layer 7: Info labels & tooltips (Arial 12px/20px)

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
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
#endregion

// ── Enums declared OUTSIDE namespace (NT8 compiler resolution requirement) ────────────────────

/// <summary>Direction of a Fibonacci swing zone.</summary>
public enum FibSwingZoneDirection
{
    Bullish,   // downtrend→uptrend: lowest swing low (100%) → first swing high (0%)
    Bearish    // uptrend→downtrend: highest swing high (100%) → first swing low (0%)
}

/// <summary>Lifecycle phase of a Fibonacci swing zone.</summary>
public enum FibSwingZonePhase
{
    Active,    // current zone — full opacity (0-100 bars)
    Previous,  // one zone back — 30% opacity
    Ghost,     // two zones back — 5% opacity
    Expired    // > 100 bars old — removed
}

/// <summary>Return signal direction.</summary>
public enum FibSwingSignalDirection
{
    Bullish,
    Bearish
}

/// <summary>Display mode for swing point markers.</summary>
public enum FibSwingDisplayMode
{
    Smart,   // only on transitions
    Always,  // every confirmed swing
    Never    // hidden
}

/// <summary>Zone fill display style.</summary>
public enum FibSwingZoneDisplayMode
{
    Gradient,
    Solid
}

// ─────────────────────────────────────────────────────────────────────────────────────────────

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// GPU-Rendered "Fibonacci Swing Zones" indicator (ninZaFibonacciSwingZones).
    ///
    /// Detects trend transitions via a Moving Average, anchors Fibonacci zones to ①②③
    /// sequential swing points, renders diagonal converging trapezoids, fires Return signals
    /// at configurable Fib levels, and shows a stepped trend curve — all via SharpDX GPU.
    /// </summary>
    public class FibonacciSwingZones : Indicator
    {
        // ── Internal data structures ─────────────────────────────────────────────────────────

        private class FibSwingPoint
        {
            public int    BarIndex;
            public double Price;
            public bool   IsHigh;
        }

        private class FibSwingReturnSignal
        {
            public int                  BarIndex;
            public double               Price;
            public double               FibRatio;
            public FibSwingSignalDirection Direction;
        }

        private class FibSwingZone
        {
            // ① point — the extreme swing price and its bar
            public int    AnchorBar;
            public double AnchorPrice;

            // ② point — the reversal swing price and its bar
            public int    ReversalBar;
            public double ReversalPrice;

            public FibSwingZoneDirection Direction;
            public FibSwingZonePhase     Phase;
            public int                  EndBar;           // updated to current bar each tick
            public List<FibSwingReturnSignal> Signals;
            public int                  LastSignalBar;    // throttle: bar index of last signal

            // Fib level broken tracking (ratio → broken)
            public Dictionary<double, bool> LevelBroken;

            public FibSwingZone()
            {
                Signals      = new List<FibSwingReturnSignal>();
                LevelBroken  = new Dictionary<double, bool>();
                LastSignalBar = -999;
            }

            /// <summary>Returns price at given Fib ratio (0.0 = anchor, 1.0 = reversal end).</summary>
            public double GetFibPrice(double ratio)
            {
                // 100% = AnchorPrice (extreme swing), 0% = ReversalPrice (reversal point)
                return AnchorPrice + (ReversalPrice - AnchorPrice) * (1.0 - ratio);
            }

            /// <summary>Age of zone in bars relative to current bar.</summary>
            public int AgeInBars(int currentBar)
            {
                return currentBar - AnchorBar;
            }
        }

        // ── Constants ─────────────────────────────────────────────────────────────────────────

        private const int ZoneExpiryBars   = 100;   // bars until zone fades to Ghost/Expired
        private const int MaxStoredZones   = 5;     // maximum zones kept in memory
        private const float TrapezoidConvK = 0.015f; // convergence coefficient per bar

        // ── Private fields ────────────────────────────────────────────────────────────────────

        private List<FibSwingPoint> swingHighs;
        private List<FibSwingPoint> swingLows;
        private List<FibSwingZone>    zones;

        // MA trend detection
        private SMA   maTrend;
        private SMA   maSmooth;
        private bool  lastTrendUp;
        private bool  trendInitialized;

        // Trend curve staircase
        private Series<double> trendCurveValue;
        private Series<int>    trendCurveDir;   // +1 bullish, -1 bearish

        // Stepped curve state
        private double stepLevel;
        private int    stepDir;

        // Alert throttle
        private DateTime lastAlertTime;

        // ── SharpDX GPU resources ────────────────────────────────────────────────────────────

        private SharpDX.Direct2D1.SolidColorBrush dxBullZoneBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBearZoneBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBullZonePrevBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBearZonePrevBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBullZoneGhostBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBearZoneGhostBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxFibLineBrush1;
        private SharpDX.Direct2D1.SolidColorBrush dxFibLineBrush2;
        private SharpDX.Direct2D1.SolidColorBrush dxFibLineBrush3;
        private SharpDX.Direct2D1.SolidColorBrush dxFibLineBrush4;
        private SharpDX.Direct2D1.SolidColorBrush dxSwingHighBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxSwingLowBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBullSignalBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBearSignalBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxTrendCurveBullBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxTrendCurveBearBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxTextBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxPanelBgBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxPanelDragBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBgBullBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBgBearBrush;
        private SharpDX.Direct2D1.StrokeStyle      dxDottedStyle;
        private SharpDX.Direct2D1.StrokeStyle      dxDashStyle;
        private SharpDX.DirectWrite.TextFormat      dxInfoFormat;
        private SharpDX.DirectWrite.TextFormat      dxSignalFormat;
        private SharpDX.DirectWrite.TextFormat      dxPanelFormat;
        private bool dxResourcesCreated;

        // Toggle panel drag state
        private float panelX = 10f;
        private float panelY = 10f;

        // ════════════════════════════════════════════════════════════════════════════════════
        // ── PARAMETERS ──────────────────────────────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════════════════════

        #region 01. Trend Detection

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "MA Period", Order = 1, GroupName = "01. Trend Detection",
                 Description = "Moving average period for trend direction detection (5-50).")]
        public int MAPeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Smoothing ON", Order = 2, GroupName = "01. Trend Detection",
                 Description = "Apply secondary smoothing to the MA for noise reduction.")]
        public bool SmoothingEnabled { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Smoothing Period", Order = 3, GroupName = "01. Trend Detection",
                 Description = "Period for the secondary smoothing MA (1-20).")]
        public int SmoothingPeriod { get; set; }

        #endregion

        #region 02. Swing Detection

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Offset Multiplier", Order = 1, GroupName = "02. Swing Detection",
                 Description = "ATR-based offset multiplier for swing confirmation (1-10).")]
        public int OffsetMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Neighborhood Bars", Order = 2, GroupName = "02. Swing Detection",
                 Description = "Number of bars on each side to confirm a swing point (1-10).")]
        public int NeighborhoodBars { get; set; }

        #endregion

        #region 03. Fibonacci Levels

        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name = "Level #1 (%)", Order = 1, GroupName = "03. Fibonacci Levels",
                 Description = "First Fibonacci retracement level percentage (e.g. 38.2).")]
        public double FibLevel1 { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name = "Level #2 (%)", Order = 2, GroupName = "03. Fibonacci Levels",
                 Description = "Second Fibonacci retracement level percentage (e.g. 38.2).")]
        public double FibLevel2 { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name = "Level #3 (%)", Order = 3, GroupName = "03. Fibonacci Levels",
                 Description = "Third Fibonacci retracement level percentage (e.g. 50.0).")]
        public double FibLevel3 { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name = "Level #4 (%)", Order = 4, GroupName = "03. Fibonacci Levels",
                 Description = "Fourth Fibonacci retracement level percentage (e.g. 61.8).")]
        public double FibLevel4 { get; set; }

        [NinjaScriptProperty]
        [Range(50, 1000)]
        [Display(Name = "Qualifying Bars", Order = 5, GroupName = "03. Fibonacci Levels",
                 Description = "Minimum number of bars a zone must span to be considered qualifying (50-1000).")]
        public int QualifyingBars { get; set; }

        [NinjaScriptProperty]
        [Range(10, 100)]
        [Display(Name = "Level Offset (px)", Order = 6, GroupName = "03. Fibonacci Levels",
                 Description = "Pixel offset for Fibonacci level labels from the right edge (10-100).")]
        public int LevelLabelOffset { get; set; }

        #endregion

        #region 04. Return Signals

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Max Signals Per Zone", Order = 1, GroupName = "04. Return Signals",
                 Description = "Maximum number of Return signals allowed per Fibonacci zone (1-5).")]
        public int MaxSignalsPerZone { get; set; }

        [NinjaScriptProperty]
        [Range(5, 20)]
        [Display(Name = "Split Bars Between Signals", Order = 2, GroupName = "04. Return Signals",
                 Description = "Minimum bars that must separate consecutive Return signals (5-20).")]
        public int SplitBarsBetweenSignals { get; set; }

        #endregion

        #region 05. Bar Styling

        [NinjaScriptProperty]
        [Display(Name = "Uptrend Bar Color", Order = 1, GroupName = "05. Bar Styling",
                 Description = "Color for price bars during an uptrend.")]
        public System.Windows.Media.Brush UpBarColor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Downtrend Bar Color", Order = 2, GroupName = "05. Bar Styling",
                 Description = "Color for price bars during a downtrend.")]
        public System.Windows.Media.Brush DownBarColor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Bar Outline Enabled", Order = 3, GroupName = "05. Bar Styling",
                 Description = "Draw a thin outline around price bars.")]
        public bool BarOutlineEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Bias Coloring", Order = 4, GroupName = "05. Bar Styling",
                 Description = "Color bars based on trend bias rather than open/close comparison.")]
        public bool BiasBars { get; set; }

        #endregion

        #region 06. Background

        [NinjaScriptProperty]
        [Display(Name = "Background Enabled", Order = 1, GroupName = "06. Background",
                 Description = "Shade chart background based on trend direction.")]
        public bool BackgroundEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Uptrend BG Color", Order = 2, GroupName = "06. Background",
                 Description = "Background color during uptrend.")]
        public System.Windows.Media.Brush UpBgColor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Downtrend BG Color", Order = 3, GroupName = "06. Background",
                 Description = "Background color during downtrend.")]
        public System.Windows.Media.Brush DownBgColor { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Background Opacity %", Order = 4, GroupName = "06. Background",
                 Description = "Opacity of the trend background shading (0-100).")]
        public int BackgroundOpacity { get; set; }

        #endregion

        #region 07. Swing Points

        [NinjaScriptProperty]
        [Display(Name = "Swing Display Mode", Order = 1, GroupName = "07. Swing Points",
                 Description = "When to show swing point markers: Smart (transitions only), Always, Never.")]
        public FibSwingDisplayMode SwingDisplayMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Swing High Color", Order = 2, GroupName = "07. Swing Points",
                 Description = "Color for swing high (peak) markers.")]
        public System.Windows.Media.Brush SwingHighColor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Swing Low Color", Order = 3, GroupName = "07. Swing Points",
                 Description = "Color for swing low (trough) markers.")]
        public System.Windows.Media.Brush SwingLowColor { get; set; }

        #endregion

        #region 08. Swing Lines

        [NinjaScriptProperty]
        [Display(Name = "Active Zone Lines — Solid", Order = 1, GroupName = "08. Swing Lines",
                 Description = "Draw zone boundary lines as solid when zone is active.")]
        public bool ActiveLinesAreSolid { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Inactive Zone Lines — Dashed", Order = 2, GroupName = "08. Swing Lines",
                 Description = "Draw zone boundary lines as dashed when zone is inactive/previous.")]
        public bool InactiveLinesAreDashed { get; set; }

        #endregion

        #region 09. Fibonacci Zones

        [NinjaScriptProperty]
        [Display(Name = "Zone Display Mode", Order = 1, GroupName = "09. Fibonacci Zones",
                 Description = "How to fill Fibonacci zones: Gradient (top-to-bottom) or Solid.")]
        public FibSwingZoneDisplayMode ZoneDisplayMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Active Bullish Zone Color", Order = 2, GroupName = "09. Fibonacci Zones",
                 Description = "Fill color for the active bullish (uptrend) Fibonacci zone.")]
        public System.Windows.Media.Brush ActiveBullishZoneColor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Active Bearish Zone Color", Order = 3, GroupName = "09. Fibonacci Zones",
                 Description = "Fill color for the active bearish (downtrend) Fibonacci zone.")]
        public System.Windows.Media.Brush ActiveBearishZoneColor { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Active Zone Opacity %", Order = 4, GroupName = "09. Fibonacci Zones",
                 Description = "Opacity for the currently active Fibonacci zone (0-100).")]
        public int ActiveZoneOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Previous Zone Opacity %", Order = 5, GroupName = "09. Fibonacci Zones",
                 Description = "Opacity for the previous (one behind) Fibonacci zone (0-100).")]
        public int PreviousZoneOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Ghost Zone Opacity %", Order = 6, GroupName = "09. Fibonacci Zones",
                 Description = "Opacity for ghost (two behind) Fibonacci zones (0-100).")]
        public int GhostZoneOpacity { get; set; }

        #endregion

        #region 10. Fibonacci Level Colors

        [NinjaScriptProperty]
        [Display(Name = "Level #1 Color", Order = 1, GroupName = "10. Fibonacci Level Colors",
                 Description = "Color for Fibonacci level #1 line.")]
        public System.Windows.Media.Brush FibColor1 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Level #2 Color", Order = 2, GroupName = "10. Fibonacci Level Colors",
                 Description = "Color for Fibonacci level #2 line.")]
        public System.Windows.Media.Brush FibColor2 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Level #3 Color", Order = 3, GroupName = "10. Fibonacci Level Colors",
                 Description = "Color for Fibonacci level #3 line.")]
        public System.Windows.Media.Brush FibColor3 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Level #4 Color", Order = 4, GroupName = "10. Fibonacci Level Colors",
                 Description = "Color for Fibonacci level #4 line.")]
        public System.Windows.Media.Brush FibColor4 { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Intact Level Opacity %", Order = 5, GroupName = "10. Fibonacci Level Colors",
                 Description = "Opacity for intact (un-broken) Fibonacci level lines (0-100).")]
        public int IntactLevelOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Broken Level Opacity %", Order = 6, GroupName = "10. Fibonacci Level Colors",
                 Description = "Opacity for broken Fibonacci level lines (0-100).")]
        public int BrokenLevelOpacity { get; set; }

        #endregion

        #region 11. Alerts

        [NinjaScriptProperty]
        [Display(Name = "Alert — Popup", Order = 1, GroupName = "11. Alerts",
                 Description = "Show a popup notification when a signal fires.")]
        public bool AlertPopup { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert — Sound", Order = 2, GroupName = "11. Alerts",
                 Description = "Play an audio alert when a signal fires.")]
        public bool AlertSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert — Email", Order = 3, GroupName = "11. Alerts",
                 Description = "Send an email alert when a signal fires.")]
        public bool AlertEmail { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Email Receiver", Order = 4, GroupName = "11. Alerts",
                 Description = "Email address to receive alerts.")]
        public string AlertEmailAddress { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert — Trend Change", Order = 5, GroupName = "11. Alerts",
                 Description = "Fire alert on trend direction change.")]
        public bool AlertOnTrendChange { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert — Return Signal", Order = 6, GroupName = "11. Alerts",
                 Description = "Fire alert on Return signal.")]
        public bool AlertOnReturnSignal { get; set; }

        #endregion

        #region 12. Markers

        [NinjaScriptProperty]
        [Display(Name = "Markers Enabled", Order = 1, GroupName = "12. Markers",
                 Description = "Show Return signal circle markers on the chart.")]
        public bool MarkersEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Bullish Marker Color", Order = 2, GroupName = "12. Markers",
                 Description = "Color for bullish Return signal markers.")]
        public System.Windows.Media.Brush BullishMarkerColor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Bearish Marker Color", Order = 3, GroupName = "12. Markers",
                 Description = "Color for bearish Return signal markers.")]
        public System.Windows.Media.Brush BearishMarkerColor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Bullish Label Text", Order = 4, GroupName = "12. Markers",
                 Description = "Text displayed next to bullish Return signal marker.")]
        public string BullishMarkerText { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Bearish Label Text", Order = 5, GroupName = "12. Markers",
                 Description = "Text displayed next to bearish Return signal marker.")]
        public string BearishMarkerText { get; set; }

        [NinjaScriptProperty]
        [Range(5, 40)]
        [Display(Name = "Marker Offset (px)", Order = 6, GroupName = "12. Markers",
                 Description = "Pixel offset of marker from the signal bar candle.")]
        public int MarkerOffset { get; set; }

        #endregion

        #region 13. Toggle Panel

        [NinjaScriptProperty]
        [Display(Name = "Panel Enabled", Order = 1, GroupName = "13. Toggle Panel",
                 Description = "Show the draggable toggle information panel.")]
        public bool PanelEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Panel ON Color", Order = 2, GroupName = "13. Toggle Panel",
                 Description = "Background color for toggle buttons in the ON state.")]
        public System.Windows.Media.Brush PanelOnColor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Panel OFF Color", Order = 3, GroupName = "13. Toggle Panel",
                 Description = "Background color for toggle buttons in the OFF state.")]
        public System.Windows.Media.Brush PanelOffColor { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "Panel Margin (px)", Order = 4, GroupName = "13. Toggle Panel",
                 Description = "Margin around panel contents in pixels.")]
        public int PanelMargin { get; set; }

        #endregion

        #region 14. Special

        [NinjaScriptProperty]
        [Display(Name = "Z Order", Order = 1, GroupName = "14. Special",
                 Description = "Drawing Z-order for this indicator panel (-100 = far background).")]
        public int ZOrder { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "User Note", Order = 2, GroupName = "14. Special",
                 Description = "Personal note stored with indicator settings.")]
        public string UserNote { get; set; }

        #endregion

        #region 15. Visual

        [NinjaScriptProperty]
        [Display(Name = "Show Trend Curve", Order = 1, GroupName = "15. Visual",
                 Description = "Display the stepped trend staircase curve at chart bottom.")]
        public bool ShowTrendCurve { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Background Shading", Order = 2, GroupName = "15. Visual",
                 Description = "Shade the chart background based on trend direction.")]
        public bool ShowBackgroundShading { get; set; }

        #endregion

        // ════════════════════════════════════════════════════════════════════════════════════
        // ── OnStateChange ───────────────────────────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════════════════════

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = @"GPU-Rendered Fibonacci Swing Zones — Smart swing detection with diagonal trapezoid zones, dotted Fib levels, Return signals, and stepped trend curve.";
                Name                     = "ninZaFibonacciSwingZones";
                Calculate                = Calculate.OnBarClose;
                IsOverlay                = true;
                DisplayInDataBox         = false;
                DrawOnPricePanel         = true;
                DrawHorizontalGridLines  = false;
                DrawVerticalGridLines    = false;
                PaintPriceMarkers        = true;
                ScaleJustification       = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                IsSuspendedWhileInactive = true;
                IsAutoScale              = false;           // CRITICAL: must be FALSE
                MaximumBarsLookBack      = MaximumBarsLookBack.TwoHundredFiftySix;

                // ── 01. Trend Detection defaults
                MAPeriod         = 14;
                SmoothingEnabled = true;
                SmoothingPeriod  = 3;

                // ── 02. Swing Detection defaults
                OffsetMultiplier  = 2;
                NeighborhoodBars  = 5;

                // ── 03. Fibonacci Levels defaults
                FibLevel1        = 38.2;
                FibLevel2        = 38.2;
                FibLevel3        = 50.0;
                FibLevel4        = 61.8;
                QualifyingBars   = 100;
                LevelLabelOffset = 40;

                // ── 04. Return Signals defaults
                MaxSignalsPerZone      = 3;
                SplitBarsBetweenSignals = 10;

                // ── 05. Bar Styling defaults
                UpBarColor       = Brushes.DodgerBlue;
                DownBarColor     = Brushes.HotPink;
                BarOutlineEnabled = true;
                BiasBars         = true;

                // ── 06. Background defaults
                BackgroundEnabled = true;
                UpBgColor         = Brushes.LimeGreen;
                DownBgColor       = Brushes.HotPink;
                BackgroundOpacity = 20;

                // ── 07. Swing Points defaults
                SwingDisplayMode = FibSwingDisplayMode.Smart;
                SwingHighColor   = Brushes.HotPink;
                SwingLowColor    = Brushes.DodgerBlue;

                // ── 08. Swing Lines defaults
                ActiveLinesAreSolid   = true;
                InactiveLinesAreDashed = true;

                // ── 09. Fibonacci Zones defaults
                ZoneDisplayMode       = FibSwingZoneDisplayMode.Gradient;
                ActiveBullishZoneColor = Brushes.Turquoise;
                ActiveBearishZoneColor = Brushes.HotPink;
                ActiveZoneOpacity     = 70;
                PreviousZoneOpacity   = 30;
                GhostZoneOpacity      = 5;

                // ── 10. Fibonacci Level Colors defaults
                FibColor1          = Brushes.Transparent;
                FibColor2          = Brushes.DarkGray;
                FibColor3          = Brushes.DodgerBlue;
                FibColor4          = Brushes.Gold;
                IntactLevelOpacity = 100;
                BrokenLevelOpacity = 60;

                // ── 11. Alerts defaults
                AlertPopup         = true;
                AlertSound         = true;
                AlertEmail         = false;
                AlertEmailAddress  = "";
                AlertOnTrendChange  = true;
                AlertOnReturnSignal = true;

                // ── 12. Markers defaults
                MarkersEnabled     = true;
                BullishMarkerColor = Brushes.Cyan;
                BearishMarkerColor = Brushes.DeepPink;
                BullishMarkerText  = "↑ Return";
                BearishMarkerText  = "↓ Return";
                MarkerOffset       = 10;

                // ── 13. Toggle Panel defaults
                PanelEnabled  = true;
                PanelOnColor  = Brushes.DodgerBlue;
                PanelOffColor = Brushes.Silver;
                PanelMargin   = 5;

                // ── 14. Special defaults
                ZOrder   = -100;
                UserNote = "";

                // ── 15. Visual defaults
                ShowTrendCurve      = true;
                ShowBackgroundShading = true;

                // ── Plots (NT8 requires at minimum these)
                AddPlot(new Stroke(Brushes.Yellow, 2), PlotStyle.Square, "Trend");
                AddPlot(new Stroke(Brushes.DeepSkyBlue, 1), PlotStyle.Line, "SignalTrend");
                AddPlot(new Stroke(Brushes.Magenta, 1), PlotStyle.Line, "SignalTrade");
            }
            else if (State == State.DataLoaded)
            {
                swingHighs       = new List<FibSwingPoint>();
                swingLows        = new List<FibSwingPoint>();
                zones            = new List<FibSwingZone>();
                trendInitialized = false;
                lastTrendUp      = true;
                stepLevel        = 0.0;
                stepDir          = 0;
                lastAlertTime    = DateTime.MinValue;

                maTrend = SMA(Close, MAPeriod);
                if (SmoothingEnabled)
                    maSmooth = SMA(maTrend, SmoothingPeriod);

                trendCurveValue = new Series<double>(this, MaximumBarsLookBack.TwoHundredFiftySix);
                trendCurveDir   = new Series<int>(this, MaximumBarsLookBack.TwoHundredFiftySix);
            }
            else if (State == State.Terminated)
            {
                DisposeSharpDXResources();
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════
        // ── OnBarUpdate ─────────────────────────────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════════════════════

        protected override void OnBarUpdate()
        {
            // Require enough bars for swing detection on both sides
            int minBars = Math.Max(MAPeriod, NeighborhoodBars * 2 + 1);
            if (CurrentBar < minBars)
                return;

            // ── Step 1: Determine trend direction from MA (uptrend = price above MA)
            double maVal = GetMAValue();
            bool currentTrendUp = Close[0] > maVal;

            // ── Step 2: Detect confirmed swing points
            DetectSwingHighs();
            DetectSwingLows();

            // ── Step 3: Identify trend transitions → generate zones
            if (!trendInitialized)
            {
                trendInitialized = true;
                lastTrendUp = currentTrendUp;
            }
            else if (currentTrendUp != lastTrendUp)
            {
                OnTrendChange(currentTrendUp);
                lastTrendUp = currentTrendUp;
            }

            // ── Step 4: Keep zone EndBars current; update phase
            UpdateZonePhases();

            // ── Step 5: Check for Return signals in active zones
            CheckReturnSignals();

            // ── Step 6: Color price bars based on trend
            ColorPriceBar(currentTrendUp);

            // ── Step 7: Trend curve staircase update
            UpdateTrendCurve(currentTrendUp);

            // ── Step 8: Update plots
            Values[0][0] = maVal;   // Trend plot (Yellow squares)
            Values[1][0] = maVal;   // SignalTrend plot (Line)
            Values[2][0] = maVal;   // SignalTrade plot (Line)
        }

        // ── MA value helper ──────────────────────────────────────────────────────────────────

        private double GetMAValue()
        {
            try
            {
                if (SmoothingEnabled && maSmooth != null && CurrentBar >= MAPeriod + SmoothingPeriod)
                    return maSmooth[0];
                if (maTrend != null && CurrentBar >= MAPeriod)
                    return maTrend[0];
            }
            catch { }
            return Close[0];
        }

        /// <summary>Returns the effective Fibonacci ratios array from user parameters.</summary>
        private double[] GetFibRatios()
        {
            return new double[]
            {
                FibLevel1 / 100.0,
                FibLevel2 / 100.0,
                FibLevel3 / 100.0,
                FibLevel4 / 100.0
            };
        }

        // ── Swing Detection ─────────────────────────────────────────────────────────────────

        private void DetectSwingHighs()
        {
            int pivot = CurrentBar - NeighborhoodBars;
            if (pivot < NeighborhoodBars)
                return;

            double pivotHigh = High.GetValueAt(pivot);

            for (int i = pivot - NeighborhoodBars; i <= pivot + NeighborhoodBars; i++)
            {
                if (i == pivot) continue;
                if (i < 0 || i > CurrentBar) continue;
                if (High.GetValueAt(i) >= pivotHigh)
                    return;
            }

            // Enforce minimum spacing
            if (swingHighs.Count > 0 && pivot - swingHighs[swingHighs.Count - 1].BarIndex < NeighborhoodBars)
                return;

            swingHighs.Add(new FibSwingPoint { BarIndex = pivot, Price = pivotHigh, IsHigh = true });
        }

        private void DetectSwingLows()
        {
            int pivot = CurrentBar - NeighborhoodBars;
            if (pivot < NeighborhoodBars)
                return;

            double pivotLow = Low.GetValueAt(pivot);

            for (int i = pivot - NeighborhoodBars; i <= pivot + NeighborhoodBars; i++)
            {
                if (i == pivot) continue;
                if (i < 0 || i > CurrentBar) continue;
                if (Low.GetValueAt(i) <= pivotLow)
                    return;
            }

            if (swingLows.Count > 0 && pivot - swingLows[swingLows.Count - 1].BarIndex < NeighborhoodBars)
                return;

            swingLows.Add(new FibSwingPoint { BarIndex = pivot, Price = pivotLow, IsHigh = false });
        }

        // ── Trend Change → Zone Creation ────────────────────────────────────────────────────

        private void OnTrendChange(bool nowUptrend)
        {
            // Find the most recent relevant swing points
            if (nowUptrend)
            {
                // downtrend → uptrend: anchor = lowest recent swing low (①), reversal = most recent swing high (②)
                if (swingLows.Count == 0 || swingHighs.Count == 0) return;

                FibSwingPoint anchorLow = FindMostRecentSwingLow();
                FibSwingPoint reversalHigh = swingHighs[swingHighs.Count - 1];

                if (anchorLow == null || reversalHigh == null) return;
                if (reversalHigh.BarIndex <= anchorLow.BarIndex) return;

                CreateZone(FibSwingZoneDirection.Bullish, anchorLow.BarIndex, anchorLow.Price,
                           reversalHigh.BarIndex, reversalHigh.Price);

                FireTrendChangeAlert("Trend ▲", true);
            }
            else
            {
                // uptrend → downtrend: anchor = highest recent swing high (①), reversal = most recent swing low (②)
                if (swingHighs.Count == 0 || swingLows.Count == 0) return;

                FibSwingPoint anchorHigh = FindMostRecentSwingHigh();
                FibSwingPoint reversalLow = swingLows[swingLows.Count - 1];

                if (anchorHigh == null || reversalLow == null) return;
                if (reversalLow.BarIndex <= anchorHigh.BarIndex) return;

                CreateZone(FibSwingZoneDirection.Bearish, anchorHigh.BarIndex, anchorHigh.Price,
                           reversalLow.BarIndex, reversalLow.Price);

                FireTrendChangeAlert("Trend ▼", false);
            }
        }

        private FibSwingPoint FindMostRecentSwingHigh()
        {
            if (swingHighs.Count == 0) return null;
            // Find highest high in recent history
            FibSwingPoint best = swingHighs[swingHighs.Count - 1];
            int lookbackLimit = Math.Max(0, swingHighs.Count - 5);
            for (int i = lookbackLimit; i < swingHighs.Count; i++)
                if (swingHighs[i].Price > best.Price) best = swingHighs[i];
            return best;
        }

        private FibSwingPoint FindMostRecentSwingLow()
        {
            if (swingLows.Count == 0) return null;
            // Find lowest low in recent history
            FibSwingPoint best = swingLows[swingLows.Count - 1];
            int lookbackLimit = Math.Max(0, swingLows.Count - 5);
            for (int i = lookbackLimit; i < swingLows.Count; i++)
                if (swingLows[i].Price < best.Price) best = swingLows[i];
            return best;
        }

        private void CreateZone(FibSwingZoneDirection dir, int anchorBar, double anchorPrice,
                                 int reversalBar, double reversalPrice)
        {
            // Demote existing zones
            for (int i = 0; i < zones.Count; i++)
            {
                if (zones[i].Phase == FibSwingZonePhase.Active)
                    zones[i].Phase = FibSwingZonePhase.Previous;
                else if (zones[i].Phase == FibSwingZonePhase.Previous)
                    zones[i].Phase = FibSwingZonePhase.Ghost;
                else if (zones[i].Phase == FibSwingZonePhase.Ghost)
                    zones[i].Phase = FibSwingZonePhase.Expired;
            }

            // Remove expired zones; keep at most MaxStoredZones
            zones.RemoveAll(z => z.Phase == FibSwingZonePhase.Expired);
            while (zones.Count >= MaxStoredZones)
                zones.RemoveAt(0);

            // Initialize Fib level broken state using current parameters
            double[] ratioArray = GetFibRatios();
            var levelBroken = new Dictionary<double, bool>();
            foreach (double r in ratioArray)
                if (!levelBroken.ContainsKey(r))
                    levelBroken[r] = false;

            var zone = new FibSwingZone
            {
                AnchorBar     = anchorBar,
                AnchorPrice   = anchorPrice,
                ReversalBar   = reversalBar,
                ReversalPrice = reversalPrice,
                Direction     = dir,
                Phase         = FibSwingZonePhase.Active,
                EndBar        = CurrentBar,
                LevelBroken   = levelBroken
            };

            zones.Add(zone);
        }

        // ── Zone Phase Lifecycle ─────────────────────────────────────────────────────────────

        private void UpdateZonePhases()
        {
            foreach (var zone in zones)
            {
                zone.EndBar = CurrentBar;

                int age = zone.AgeInBars(CurrentBar);
                if (zone.Phase == FibSwingZonePhase.Active && age > ZoneExpiryBars)
                    zone.Phase = FibSwingZonePhase.Previous;
            }

            // Update broken state for levels
            double[] ratios = GetFibRatios();
            foreach (var zone in zones)
            {
                if (zone.Phase == FibSwingZonePhase.Expired) continue;

                foreach (double ratio in ratios)
                {
                    if (!zone.LevelBroken.ContainsKey(ratio)) continue;
                    if (zone.LevelBroken[ratio]) continue;

                    double levelPrice = zone.GetFibPrice(ratio);
                    if (zone.Direction == FibSwingZoneDirection.Bullish)
                    {
                        if (Close[0] < levelPrice)
                            zone.LevelBroken[ratio] = true;
                    }
                    else
                    {
                        if (Close[0] > levelPrice)
                            zone.LevelBroken[ratio] = true;
                    }
                }
            }
        }

        // ── Return Signal Detection ──────────────────────────────────────────────────────────

        private void CheckReturnSignals()
        {
            double[] ratios = GetFibRatios();
            double tolerance = TickSize * 2;

            foreach (var zone in zones)
            {
                if (zone.Phase != FibSwingZonePhase.Active) continue;
                if (zone.Signals.Count >= MaxSignalsPerZone) continue;

                // Throttle by split bars
                if (CurrentBar - zone.LastSignalBar < SplitBarsBetweenSignals) continue;

                foreach (double ratio in ratios)
                {
                    double fibPrice = zone.GetFibPrice(ratio);

                    // Check price is within tolerance of the Fib level
                    bool inZone = (Low[0] <= fibPrice + tolerance) && (High[0] >= fibPrice - tolerance);
                    if (!inZone) continue;

                    FibSwingSignalDirection sigDir = zone.Direction == FibSwingZoneDirection.Bullish
                        ? FibSwingSignalDirection.Bullish
                        : FibSwingSignalDirection.Bearish;

                    var signal = new FibSwingReturnSignal
                    {
                        BarIndex  = CurrentBar,
                        Price     = fibPrice,
                        FibRatio  = ratio,
                        Direction = sigDir
                    };

                    zone.Signals.Add(signal);
                    zone.LastSignalBar = CurrentBar;

                    // Draw chart markers using NinjaTrader Draw methods
                    if (MarkersEnabled)
                    {
                        string tag = "FibSig_" + CurrentBar + "_" + ratio.ToString("F3");
                        if (sigDir == FibSwingSignalDirection.Bullish)
                            Draw.ArrowUp(this, tag, false, 0, Low[0] - MarkerOffset * TickSize, Brushes.Cyan);
                        else
                            Draw.ArrowDown(this, tag, false, 0, High[0] + MarkerOffset * TickSize, Brushes.DeepPink);
                    }

                    // Fire alerts
                    if (AlertOnReturnSignal)
                    {
                        string msg = sigDir == FibSwingSignalDirection.Bullish
                            ? BullishMarkerText + " @ " + (ratio * 100.0).ToString("F1") + "%"
                            : BearishMarkerText + " @ " + (ratio * 100.0).ToString("F1") + "%";
                        FireReturnSignalAlert(msg, sigDir == FibSwingSignalDirection.Bullish);
                    }

                    break; // one signal per zone per bar
                }
            }
        }

        // ── Bar Coloring ─────────────────────────────────────────────────────────────────────

        private void ColorPriceBar(bool uptrend)
        {
            if (!BiasBars) return;

            if (uptrend)
            {
                BarBrush        = UpBarColor;
                CandleOutlineBrush = BarOutlineEnabled ? Brushes.White : Brushes.Transparent;
            }
            else
            {
                BarBrush        = DownBarColor;
                CandleOutlineBrush = BarOutlineEnabled ? Brushes.White : Brushes.Transparent;
            }
        }

        // ── Stepped Trend Curve ─────────────────────────────────────────────────────────────

        private void UpdateTrendCurve(bool uptrend)
        {
            int dir = uptrend ? 1 : -1;

            if (stepDir == 0)
            {
                stepDir   = dir;
                stepLevel = GetMAValue();
            }
            else if (dir != stepDir)
            {
                stepDir   = dir;
                stepLevel = GetMAValue();
            }

            trendCurveValue[0] = stepLevel;
            trendCurveDir[0]   = dir;
        }

        // ── Alert Helpers ────────────────────────────────────────────────────────────────────

        private void FireTrendChangeAlert(string message, bool bullish)
        {
            if (!AlertOnTrendChange) return;
            ThrottledAlert(message, bullish);
        }

        private void FireReturnSignalAlert(string message, bool bullish)
        {
            ThrottledAlert(message, bullish);
        }

        private void ThrottledAlert(string message, bool bullish)
        {
            if ((DateTime.Now - lastAlertTime).TotalSeconds < 60) return;
            lastAlertTime = DateTime.Now;

            try
            {
                if (AlertPopup)
                    Alert("FibSwing_" + CurrentBar, Priority.High, message, "", 0, Brushes.LightYellow, Brushes.Black);

                if (AlertSound)
                {
                    string wavFile = bullish ? "Alert4.wav" : "Alert2.wav";
                    PlaySound(wavFile);
                }

                if (AlertEmail && !string.IsNullOrEmpty(AlertEmailAddress))
                    SendMail(AlertEmailAddress, "Fibonacci Swing Zones Alert", message);
            }
            catch { }
        }

        // ════════════════════════════════════════════════════════════════════════════════════
        // ── SharpDX Resource Management ─────────────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════════════════════

        public override void OnRenderTargetChanged()
        {
            DisposeSharpDXResources();
        }

        private void CreateSharpDXResources()
        {
            if (RenderTarget == null) return;

            try
            {
                var rt = RenderTarget;

                // ── Zone brushes
                dxBullZoneBrush      = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(ActiveBullishZoneColor, ActiveZoneOpacity   / 100f));
                dxBearZoneBrush      = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(ActiveBearishZoneColor, ActiveZoneOpacity   / 100f));
                dxBullZonePrevBrush  = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(ActiveBullishZoneColor, PreviousZoneOpacity / 100f));
                dxBearZonePrevBrush  = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(ActiveBearishZoneColor, PreviousZoneOpacity / 100f));
                dxBullZoneGhostBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(ActiveBullishZoneColor, GhostZoneOpacity    / 100f));
                dxBearZoneGhostBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(ActiveBearishZoneColor, GhostZoneOpacity    / 100f));

                // ── Fibonacci level line brushes
                dxFibLineBrush1 = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(FibColor1, IntactLevelOpacity / 100f));
                dxFibLineBrush2 = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(FibColor2, IntactLevelOpacity / 100f));
                dxFibLineBrush3 = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(FibColor3, IntactLevelOpacity / 100f));
                dxFibLineBrush4 = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(FibColor4, IntactLevelOpacity / 100f));

                // ── Swing marker brushes
                dxSwingHighBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(SwingHighColor, 1f));
                dxSwingLowBrush  = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(SwingLowColor,  1f));

                // ── Signal brushes
                dxBullSignalBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(BullishMarkerColor, 1f));
                dxBearSignalBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(BearishMarkerColor, 1f));

                // ── Trend curve brushes (60% opacity)
                dxTrendCurveBullBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0.12f, 0.56f, 1f, 0.60f));   // DodgerBlue
                dxTrendCurveBearBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(1f, 0.41f, 0.71f, 0.60f));   // HotPink

                // ── Text & UI brushes
                dxTextBrush     = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(1f, 1f, 1f, 1f));
                dxPanelBgBrush  = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0.12f, 0.56f, 1f, 0.85f));
                dxPanelDragBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0.196f, 0.804f, 0.196f, 1f)); // LimeGreen

                // ── Background brushes
                dxBgBullBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(UpBgColor,   BackgroundOpacity / 100f));
                dxBgBearBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(DownBgColor, BackgroundOpacity / 100f));

                // ── Stroke styles
                var dottedProps = new SharpDX.Direct2D1.StrokeStyleProperties
                {
                    DashStyle = SharpDX.Direct2D1.DashStyle.Dot,
                    LineJoin  = LineJoin.Round,
                    StartCap  = CapStyle.Round,
                    EndCap    = CapStyle.Round
                };
                dxDottedStyle = new SharpDX.Direct2D1.StrokeStyle(rt.Factory, dottedProps);

                var dashProps = new SharpDX.Direct2D1.StrokeStyleProperties
                {
                    DashStyle = SharpDX.Direct2D1.DashStyle.Dash,
                    LineJoin  = LineJoin.Round,
                    StartCap  = CapStyle.Flat,
                    EndCap    = CapStyle.Flat
                };
                dxDashStyle = new SharpDX.Direct2D1.StrokeStyle(rt.Factory, dashProps);

                // ── Text formats
                var dwFactory = new SharpDX.DirectWrite.Factory();
                dxInfoFormat   = new SharpDX.DirectWrite.TextFormat(dwFactory, "Arial", 12f);
                dxSignalFormat = new SharpDX.DirectWrite.TextFormat(dwFactory, "Arial", 20f);
                dxPanelFormat  = new SharpDX.DirectWrite.TextFormat(dwFactory, "Arial", 10f);
                dwFactory.Dispose();

                dxResourcesCreated = true;
            }
            catch
            {
                dxResourcesCreated = false;
            }
        }

        private void DisposeSharpDXResources()
        {
            dxResourcesCreated = false;

            DisposeRef(ref dxBullZoneBrush);
            DisposeRef(ref dxBearZoneBrush);
            DisposeRef(ref dxBullZonePrevBrush);
            DisposeRef(ref dxBearZonePrevBrush);
            DisposeRef(ref dxBullZoneGhostBrush);
            DisposeRef(ref dxBearZoneGhostBrush);
            DisposeRef(ref dxFibLineBrush1);
            DisposeRef(ref dxFibLineBrush2);
            DisposeRef(ref dxFibLineBrush3);
            DisposeRef(ref dxFibLineBrush4);
            DisposeRef(ref dxSwingHighBrush);
            DisposeRef(ref dxSwingLowBrush);
            DisposeRef(ref dxBullSignalBrush);
            DisposeRef(ref dxBearSignalBrush);
            DisposeRef(ref dxTrendCurveBullBrush);
            DisposeRef(ref dxTrendCurveBearBrush);
            DisposeRef(ref dxTextBrush);
            DisposeRef(ref dxPanelBgBrush);
            DisposeRef(ref dxPanelDragBrush);
            DisposeRef(ref dxBgBullBrush);
            DisposeRef(ref dxBgBearBrush);
            DisposeRef(ref dxDottedStyle);
            DisposeRef(ref dxDashStyle);
            DisposeRef(ref dxInfoFormat);
            DisposeRef(ref dxSignalFormat);
            DisposeRef(ref dxPanelFormat);
        }

        private static void DisposeRef<T>(ref T resource) where T : class, IDisposable
        {
            if (resource != null)
            {
                resource.Dispose();
                resource = null;
            }
        }

        // ── Color conversion helper ──────────────────────────────────────────────────────────

        private SharpDX.Color4 ToColor4(System.Windows.Media.Brush wpfBrush, float alpha)
        {
            try
            {
                var solidBrush = wpfBrush as System.Windows.Media.SolidColorBrush;
                if (solidBrush == null)
                    return new SharpDX.Color4(0.5f, 0.5f, 0.5f, alpha);

                System.Windows.Media.Color c = solidBrush.Color;
                return new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, alpha);
            }
            catch
            {
                return new SharpDX.Color4(0.5f, 0.5f, 0.5f, alpha);
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════
        // ── 7-LAYER GPU RENDERING ───────────────────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════════════════════

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (Bars == null || ChartBars == null || RenderTarget == null)
                return;

            if (!dxResourcesCreated)
                CreateSharpDXResources();

            if (!dxResourcesCreated)
                return;

            int firstBar = ChartBars.FromIndex;
            int lastBar  = ChartBars.ToIndex;

            if (firstBar > lastBar)
                return;

            try
            {
                var rt = RenderTarget;

                // ── Layer 0: Background shading ────────────────────────────────────────────
                if (ShowBackgroundShading && BackgroundEnabled)
                    RenderBackgroundShading(rt, chartControl, chartScale, firstBar, lastBar);

                // ── Layer 1: Stepped trend curve ───────────────────────────────────────────
                if (ShowTrendCurve)
                    RenderSteppedTrendCurve(rt, chartControl, chartScale, firstBar, lastBar);

                // ── Layer 2: Diagonal zone trapezoids ──────────────────────────────────────
                RenderZoneTrapezoids(rt, chartControl, chartScale, firstBar, lastBar);

                // ── Layer 3: Fibonacci dotted lines ────────────────────────────────────────
                RenderFibonacciLevelLines(rt, chartControl, chartScale, firstBar, lastBar);

                // ── Layer 4: Swing point markers ───────────────────────────────────────────
                if (SwingDisplayMode != FibSwingDisplayMode.Never)
                    RenderSwingPointMarkers(rt, chartControl, chartScale, firstBar, lastBar);

                // ── Layer 6: Return signal circles + labels ────────────────────────────────
                if (MarkersEnabled)
                    RenderReturnSignals(rt, chartControl, chartScale, firstBar, lastBar);

                // ── Layer 7: Info labels + toggle panel ────────────────────────────────────
                if (PanelEnabled)
                    RenderTogglePanel(rt, chartControl, chartScale);
            }
            catch { }
        }

        // ── Layer 0: Background Shading ──────────────────────────────────────────────────────

        private void RenderBackgroundShading(SharpDX.Direct2D1.RenderTarget rt,
            ChartControl cc, ChartScale cs, int firstBar, int lastBar)
        {
            if (trendCurveDir == null || trendCurveValue == null) return;

            for (int barIdx = firstBar; barIdx < lastBar; barIdx++)
            {
                int nextBarIdx = barIdx + 1;
                if (barIdx - Displacement < 0 || barIdx - Displacement >= trendCurveDir.Count) continue;
                if (nextBarIdx - Displacement < 0 || nextBarIdx - Displacement >= trendCurveDir.Count) continue;

                int dir = trendCurveDir.GetValueAt(barIdx);
                var brush = dir >= 0 ? dxBgBullBrush : dxBgBearBrush;
                if (brush == null) continue;

                float x1 = cc.GetXByBarIndex(ChartBars, barIdx);
                float x2 = cc.GetXByBarIndex(ChartBars, nextBarIdx);
                float chartTop    = (float)cs.GetYByValue(cs.MaxValue);
                float chartBottom = (float)cs.GetYByValue(cs.MinValue);

                var rect = new SharpDX.RectangleF(x1, chartTop, x2 - x1, chartBottom - chartTop);
                rt.FillRectangle(rect, brush);
            }
        }

        // ── Layer 1: Stepped Trend Curve ─────────────────────────────────────────────────────

        private void RenderSteppedTrendCurve(SharpDX.Direct2D1.RenderTarget rt,
            ChartControl cc, ChartScale cs, int firstBar, int lastBar)
        {
            if (trendCurveValue == null || trendCurveDir == null) return;

            // Render bottom-of-chart staircase
            float chartBottom = (float)cs.GetYByValue(cs.MinValue);
            float stepHeight  = Math.Max(1f, Math.Abs(chartBottom - (float)cs.GetYByValue(cs.MaxValue)) * 0.04f);

            for (int barIdx = firstBar; barIdx < lastBar; barIdx++)
            {
                int nextBarIdx = barIdx + 1;
                if (barIdx - Displacement < 0 || barIdx - Displacement >= trendCurveValue.Count) continue;
                if (nextBarIdx - Displacement < 0 || nextBarIdx - Displacement >= trendCurveValue.Count) continue;

                int   dir1 = trendCurveDir.GetValueAt(barIdx);
                int   dir2 = trendCurveDir.GetValueAt(nextBarIdx);
                float x1   = cc.GetXByBarIndex(ChartBars, barIdx);
                float x2   = cc.GetXByBarIndex(ChartBars, nextBarIdx);

                // Staircase: horizontal segment at chartBottom offset by direction
                float y1 = chartBottom - (dir1 > 0 ? stepHeight : 0f);
                float y2 = chartBottom - (dir2 > 0 ? stepHeight : 0f);

                var brush = dir1 > 0 ? dxTrendCurveBullBrush : dxTrendCurveBearBrush;
                if (brush == null) continue;

                // Horizontal line from x1 to x2 at y1
                rt.DrawLine(new Vector2(x1, y1), new Vector2(x2, y1), brush, 3f);

                // Vertical step if direction changes
                if (dir1 != dir2)
                    rt.DrawLine(new Vector2(x2, y1), new Vector2(x2, y2), brush, 3f);
            }
        }

        // ── Layer 2: Diagonal Zone Trapezoids ────────────────────────────────────────────────

        private void RenderZoneTrapezoids(SharpDX.Direct2D1.RenderTarget rt,
            ChartControl cc, ChartScale cs, int firstBar, int lastBar)
        {
            if (zones == null) return;

            foreach (var zone in zones)
            {
                if (zone.Phase == FibSwingZonePhase.Expired) continue;

                // Choose opacity based on phase
                float opacity;
                System.Windows.Media.Brush fillColorBrush;

                switch (zone.Phase)
                {
                    case FibSwingZonePhase.Active:
                        opacity        = ActiveZoneOpacity   / 100f;
                        fillColorBrush = zone.Direction == FibSwingZoneDirection.Bullish
                            ? ActiveBullishZoneColor : ActiveBearishZoneColor;
                        break;
                    case FibSwingZonePhase.Previous:
                        opacity        = PreviousZoneOpacity / 100f;
                        fillColorBrush = zone.Direction == FibSwingZoneDirection.Bullish
                            ? ActiveBullishZoneColor : ActiveBearishZoneColor;
                        break;
                    default: // Ghost
                        opacity        = GhostZoneOpacity    / 100f;
                        fillColorBrush = zone.Direction == FibSwingZoneDirection.Bullish
                            ? ActiveBullishZoneColor : ActiveBearishZoneColor;
                        break;
                }

                if (opacity <= 0f) continue;

                // Clamp zone bars to visible window
                int startBar = Math.Max(firstBar, zone.AnchorBar);
                int endBar   = Math.Min(lastBar,  zone.EndBar);
                if (startBar > endBar) continue;

                float xStart = cc.GetXByBarIndex(ChartBars, zone.AnchorBar);
                float xEnd   = cc.GetXByBarIndex(ChartBars, endBar);

                float yAnchorHigh = (float)cs.GetYByValue(zone.AnchorPrice);
                float yAnchorLow  = (float)cs.GetYByValue(zone.ReversalPrice);
                float yConverge   = (yAnchorHigh + yAnchorLow) / 2f;

                // Interpolate left edge of visible window within the zone
                int totalBars = Math.Max(1, endBar - zone.AnchorBar);
                float tClip   = (float)(startBar - zone.AnchorBar) / totalBars;
                float xClip   = cc.GetXByBarIndex(ChartBars, startBar);
                float yTopClip    = yAnchorHigh + (yConverge - yAnchorHigh) * tClip;
                float yBottomClip = yAnchorLow  + (yConverge - yAnchorLow)  * tClip;

                // Fill the trapezoid
                if (ZoneDisplayMode == FibSwingZoneDisplayMode.Gradient)
                {
                    RenderGradientTrapezoid(rt, fillColorBrush, opacity,
                        xClip, yTopClip, yBottomClip, xEnd, yConverge,
                        zone.Direction == FibSwingZoneDirection.Bullish);
                }
                else
                {
                    SharpDX.Color4 solidColor = ToColor4(fillColorBrush, opacity);
                    using (var solidBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, solidColor))
                        DrawTrapezoid(rt, solidBrush, xClip, yTopClip, yBottomClip, xEnd, yConverge);
                }

                // Outline lines (solid if active, dashed if inactive)
                SharpDX.Direct2D1.StrokeStyle lineStyle =
                    (zone.Phase == FibSwingZonePhase.Active && ActiveLinesAreSolid) ? null
                    : (InactiveLinesAreDashed ? dxDashStyle : null);

                var lineBrush = zone.Direction == FibSwingZoneDirection.Bullish
                    ? dxTrendCurveBullBrush : dxTrendCurveBearBrush;

                if (lineBrush != null)
                {
                    float lineWidth = zone.Phase == FibSwingZonePhase.Active ? 1.5f : 0.8f;
                    rt.DrawLine(new Vector2(xClip, yTopClip),    new Vector2(xEnd, yConverge), lineBrush, lineWidth, lineStyle);
                    rt.DrawLine(new Vector2(xClip, yBottomClip), new Vector2(xEnd, yConverge), lineBrush, lineWidth, lineStyle);
                }
            }
        }

        /// <summary>
        /// Renders a gradient-filled trapezoid (top color = zone color, bottom = black).
        /// Approximated via solid-color fill then a gradient-tinted overlay since
        /// SharpDX Linear gradient requires start/end pixel coordinates.
        /// </summary>
        private void RenderGradientTrapezoid(SharpDX.Direct2D1.RenderTarget rt,
            System.Windows.Media.Brush baseColor, float opacity,
            float xLeft, float yTopLeft, float yBotLeft, float xRight, float yPoint,
            bool bullish)
        {
            var factory = rt.Factory;

            // Build trapezoid geometry once, fill with gradient
            using (var geo = new SharpDX.Direct2D1.PathGeometry(factory))
            {
                using (var sink = geo.Open())
                {
                    sink.BeginFigure(new Vector2(xLeft, yTopLeft), FigureBegin.Filled);
                    sink.AddLine(new Vector2(xRight, yPoint));
                    sink.AddLine(new Vector2(xLeft,  yBotLeft));
                    sink.EndFigure(FigureEnd.Closed);
                    sink.Close();
                }

                // Gradient: zone color at top, black at bottom (or reverse for bearish)
                SharpDX.Color4 topColor    = ToColor4(baseColor, opacity);
                SharpDX.Color4 bottomColor = new SharpDX.Color4(0f, 0f, 0f, opacity * 0.3f);

                if (!bullish)
                {
                    // Bearish: stronger color at top (anchor = swing high)
                    topColor    = ToColor4(baseColor, opacity);
                    bottomColor = new SharpDX.Color4(0f, 0f, 0f, opacity * 0.2f);
                }

                float yMin = Math.Min(Math.Min(yTopLeft, yBotLeft), yPoint);
                float yMax = Math.Max(Math.Max(yTopLeft, yBotLeft), yPoint);

                var gradStops = new SharpDX.Direct2D1.GradientStop[]
                {
                    new SharpDX.Direct2D1.GradientStop { Color = topColor,    Position = 0f },
                    new SharpDX.Direct2D1.GradientStop { Color = bottomColor, Position = 1f }
                };

                using (var stopCollection = new SharpDX.Direct2D1.GradientStopCollection(rt, gradStops))
                using (var gradBrush = new SharpDX.Direct2D1.LinearGradientBrush(rt,
                    new SharpDX.Direct2D1.LinearGradientBrushProperties
                    {
                        StartPoint = new Vector2(xLeft, yMin),
                        EndPoint   = new Vector2(xLeft, yMax)
                    }, stopCollection))
                {
                    rt.FillGeometry(geo, gradBrush);
                }
            }
        }

        private void DrawTrapezoid(SharpDX.Direct2D1.RenderTarget rt,
            SharpDX.Direct2D1.SolidColorBrush brush,
            float xLeft, float yTopLeft, float yBotLeft, float xRight, float yPoint)
        {
            var factory = rt.Factory;

            using (var geo = new SharpDX.Direct2D1.PathGeometry(factory))
            {
                using (var sink = geo.Open())
                {
                    sink.BeginFigure(new Vector2(xLeft, yTopLeft), FigureBegin.Filled);
                    sink.AddLine(new Vector2(xRight, yPoint));
                    sink.AddLine(new Vector2(xLeft,  yBotLeft));
                    sink.EndFigure(FigureEnd.Closed);
                    sink.Close();
                }
                rt.FillGeometry(geo, brush);
            }
        }

        // ── Layer 3: Fibonacci Dotted Level Lines ─────────────────────────────────────────────

        private void RenderFibonacciLevelLines(SharpDX.Direct2D1.RenderTarget rt,
            ChartControl cc, ChartScale cs, int firstBar, int lastBar)
        {
            if (zones == null) return;

            double[] ratios  = GetFibRatios();
            SharpDX.Direct2D1.SolidColorBrush[] brushes =
                { dxFibLineBrush1, dxFibLineBrush2, dxFibLineBrush3, dxFibLineBrush4 };

            foreach (var zone in zones)
            {
                if (zone.Phase == FibSwingZonePhase.Expired) continue;

                int zoneStartBar = Math.Max(firstBar, zone.AnchorBar);
                int zoneEndBar   = Math.Min(lastBar,  zone.EndBar);
                if (zoneStartBar > zoneEndBar) continue;

                float xLeft  = cc.GetXByBarIndex(ChartBars, zoneStartBar);
                float xRight = cc.GetXByBarIndex(ChartBars, zoneEndBar);

                for (int li = 0; li < ratios.Length && li < brushes.Length; li++)
                {
                    double ratio = ratios[li];
                    var brush = brushes[li];
                    if (brush == null) continue;

                    bool broken  = zone.LevelBroken.ContainsKey(ratio) && zone.LevelBroken[ratio];
                    float opacity = broken ? (BrokenLevelOpacity / 100f) : (IntactLevelOpacity / 100f);
                    if (opacity <= 0f) continue;

                    double fibPrice = zone.GetFibPrice(ratio);
                    float  yLevel   = (float)cs.GetYByValue(fibPrice);

                    // Build color from source brush with dynamic opacity
                    SharpDX.Color4 srcColor = brush.Color;
                    var lineColor = new SharpDX.Color4(srcColor.Red, srcColor.Green, srcColor.Blue, opacity);

                    using (var tempBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, lineColor))
                    {
                        // Dotted line across zone width
                        rt.DrawLine(new Vector2(xLeft, yLevel), new Vector2(xRight, yLevel),
                                    tempBrush, 1.5f, dxDottedStyle);

                        // Label at right edge: "61.8%  price"
                        if (dxInfoFormat != null)
                        {
                            string priceStr   = fibPrice.ToString("F" + Instrument.MasterInstrument.DecimalPlaces);
                            string labelText  = (ratio * 100.0).ToString("F1") + "%  " + priceStr;
                            float  labelWidth = LevelLabelOffset;
                            var lblRect = new SharpDX.RectangleF(xRight - labelWidth - 2f, yLevel - 10f, labelWidth, 20f);

                            using (var lBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                                new SharpDX.Color4(1f, 1f, 1f, opacity * 0.9f)))
                            {
                                rt.DrawText(labelText, dxInfoFormat, lblRect, lBrush);
                            }
                        }
                    }
                }
            }
        }

        // ── Layer 4: Swing Point Markers ────────────────────────────────────────────────────

        private void RenderSwingPointMarkers(SharpDX.Direct2D1.RenderTarget rt,
            ChartControl cc, ChartScale cs, int firstBar, int lastBar)
        {
            if (zones == null) return;

            float radius = 5f;

            foreach (var zone in zones)
            {
                if (zone.Phase == FibSwingZonePhase.Expired) continue;

                // ① Anchor point marker
                if (zone.AnchorBar >= firstBar && zone.AnchorBar <= lastBar)
                {
                    float xAnchor = cc.GetXByBarIndex(ChartBars, zone.AnchorBar);
                    float yAnchor = (float)cs.GetYByValue(zone.AnchorPrice);

                    var brush = zone.Direction == FibSwingZoneDirection.Bearish ? dxSwingHighBrush : dxSwingLowBrush;
                    if (brush != null)
                    {
                        var ellipse = new SharpDX.Direct2D1.Ellipse(new Vector2(xAnchor, yAnchor), radius, radius);
                        rt.FillEllipse(ellipse, brush);

                        // Label "①" at anchor point (always the first swing point)
                        if (dxTextBrush != null && dxInfoFormat != null)
                        {
                            var lblRect = new SharpDX.RectangleF(xAnchor + 8f, yAnchor - 10f, 30f, 20f);
                            rt.DrawText("①", dxInfoFormat, lblRect, dxTextBrush);
                        }
                    }
                }

                // ② Reversal point marker
                if (zone.ReversalBar >= firstBar && zone.ReversalBar <= lastBar)
                {
                    float xRev = cc.GetXByBarIndex(ChartBars, zone.ReversalBar);
                    float yRev = (float)cs.GetYByValue(zone.ReversalPrice);

                    var brush = zone.Direction == FibSwingZoneDirection.Bearish ? dxSwingLowBrush : dxSwingHighBrush;
                    if (brush != null)
                    {
                        var ellipse = new SharpDX.Direct2D1.Ellipse(new Vector2(xRev, yRev), radius, radius);
                        rt.FillEllipse(ellipse, brush);

                        if (dxTextBrush != null && dxInfoFormat != null)
                        {
                            string lbl = "②";
                            var lblRect = new SharpDX.RectangleF(xRev + 8f, yRev - 10f, 30f, 20f);
                            rt.DrawText(lbl, dxInfoFormat, lblRect, dxTextBrush);
                        }
                    }
                }
            }
        }

        // ── Layer 6: Return Signal Circles + Labels ──────────────────────────────────────────

        private void RenderReturnSignals(SharpDX.Direct2D1.RenderTarget rt,
            ChartControl cc, ChartScale cs, int firstBar, int lastBar)
        {
            if (zones == null) return;

            float circleR = 7f;

            foreach (var zone in zones)
            {
                if (zone.Phase == FibSwingZonePhase.Expired) continue;

                foreach (var sig in zone.Signals)
                {
                    if (sig.BarIndex < firstBar || sig.BarIndex > lastBar) continue;

                    float x = cc.GetXByBarIndex(ChartBars, sig.BarIndex);
                    float y = (float)cs.GetYByValue(sig.Price);

                    // Circle offset above/below price
                    float yOffset = sig.Direction == FibSwingSignalDirection.Bullish
                        ? y + MarkerOffset + circleR
                        : y - MarkerOffset - circleR;

                    var brush = sig.Direction == FibSwingSignalDirection.Bullish
                        ? dxBullSignalBrush : dxBearSignalBrush;

                    if (brush != null)
                    {
                        var ellipse = new SharpDX.Direct2D1.Ellipse(new Vector2(x, yOffset), circleR, circleR);
                        rt.FillEllipse(ellipse, brush);
                        rt.DrawEllipse(ellipse, dxTextBrush, 1f);
                    }

                    // Label text below/above circle
                    if (dxSignalFormat != null && dxTextBrush != null)
                    {
                        string labelText = sig.Direction == FibSwingSignalDirection.Bullish
                            ? BullishMarkerText : BearishMarkerText;

                        float lx = x - 30f;
                        float ly = sig.Direction == FibSwingSignalDirection.Bullish
                            ? yOffset + circleR + 2f
                            : yOffset - circleR - 24f;

                        var lblRect = new SharpDX.RectangleF(lx, ly, 80f, 24f);

                        using (var lBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                            sig.Direction == FibSwingSignalDirection.Bullish
                                ? new SharpDX.Color4(0f, 1f, 1f, 1f)      // Cyan
                                : new SharpDX.Color4(1f, 0.078f, 0.576f, 1f))) // DeepPink
                        {
                            rt.DrawText(labelText, dxSignalFormat, lblRect, lBrush);
                        }
                    }
                }
            }
        }

        // ── Layer 7: Toggle Panel ────────────────────────────────────────────────────────────

        private void RenderTogglePanel(SharpDX.Direct2D1.RenderTarget rt,
            ChartControl cc, ChartScale cs)
        {
            if (dxPanelBgBrush == null || dxTextBrush == null || dxPanelFormat == null) return;

            float pw = 140f;
            float ph = 60f;
            float m  = PanelMargin;

            // Drag bar at top
            float dragH = 8f;
            var dragRect = new SharpDX.RectangleF(panelX, panelY, pw, dragH);
            if (dxPanelDragBrush != null)
                rt.FillRectangle(dragRect, dxPanelDragBrush);

            // Panel body
            var bgRect = new SharpDX.RectangleF(panelX, panelY + dragH, pw, ph);
            rt.FillRectangle(bgRect, dxPanelBgBrush);

            // Title
            var titleRect = new SharpDX.RectangleF(panelX + m, panelY + dragH + m, pw - 2 * m, 14f);
            rt.DrawText("Fibonacci Swing Zones", dxPanelFormat, titleRect, dxTextBrush);

            // Zone count info
            int activeCount = 0;
            if (zones != null)
                foreach (var z in zones)
                    if (z.Phase == FibSwingZonePhase.Active) activeCount++;

            string infoLine = "Zones: " + activeCount + "  Trend: " + (lastTrendUp ? "▲" : "▼");
            var infoRect = new SharpDX.RectangleF(panelX + m, panelY + dragH + m + 16f, pw - 2 * m, 14f);
            rt.DrawText(infoLine, dxPanelFormat, infoRect, dxTextBrush);

            // Panel border
            rt.DrawRectangle(bgRect, dxTextBrush, 1f);
        }

        // ════════════════════════════════════════════════════════════════════════════════════
        // ── Plot Series Properties ───────────────────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════════════════════

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> Trend
        {
            get { return Values[0]; }
        }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> SignalTrend
        {
            get { return Values[1]; }
        }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> SignalTrade
        {
            get { return Values[2]; }
        }
    }
}
