// CompleteInstitutionalPivots — Production-Ready NinjaTrader 8 Indicator
// Converts CME maintenance margin into institutional-grade price levels anchored
// to the daily session open or the weekly (Sunday 6PM Globex) open.
// Includes: 11 margin levels, confirmation scoring engine (0-3), dashboard,
// alerts, session filters, and full OrderFlow+/DOM graceful integration.

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// ─────────────────────────────────────────────────────────────────────────────
// NinjaTrader 8 requires custom enums declared OUTSIDE all namespaces so the
// auto-generated partial class code can resolve them without ambiguity.
// Reference: forum.ninjatrader.com threads #1182932, #95909, #1046853
// ─────────────────────────────────────────────────────────────────────────────
public enum UltimateAnchorPeriod { DailyOpen, WeeklyOpen }
public enum DashboardMode       { Minimal, Standard, Full, Custom }
public enum DayTypeFilter       { All, RangeOnly, TrendOnly }

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// CompleteInstitutionalPivots — Takes the CME maintenance margin for a futures
    /// contract, converts it into a price range, and divides that range into 11
    /// institutional-grade levels anchored to the daily or weekly (Sunday 6PM Globex) open.
    ///
    /// Levels represent real capital thresholds where institutional liquidation, hedging,
    /// and absorption activity cluster. Signal engine scores each level touch 0–3 based on
    /// wick rejection geometry, cumulative delta divergence (OF+), and DOM absorption.
    /// </summary>
    public class CompleteInstitutionalPivots : Indicator
    {
        // ═══════════════════════════════════════════════════════════════════════
        #region Private Fields

        // ── Core anchor tracking ─────────────────────────────────────────────
        private double      anchorPrice         = 0;
        private DateTime    lastWeekAnchorDate  = DateTime.MinValue;
        private double      tickUnit            = 0;          // CmeMargin / Multiplier — updated once in DataLoaded

        // ── Level values (calculated each bar, stored for reuse) ─────────────
        private double[]    targetLevels        = new double[11];

        // ── Redraw flags ─────────────────────────────────────────────────────
        private bool        needsRedraw         = true;       // set true on anchor change
        private double      lastDashboardAnchor = -1;         // track anchor for dashboard redraw

        // ── Level test counting (reset on anchor change) ─────────────────────
        private int[]       levelTestCount      = new int[11];

        // ── Signal alert cooldown tracking ───────────────────────────────────
        private int[]       lastAlertBar        = new int[11];

        // ── Cached sound file name (null-safe, .wav-guaranteed) ──────────────
        private string      alertSoundFile      = "Alert1.wav";

        // ── Performance: brushes created once in DataLoaded ──────────────────
        private SolidColorBrush _primaryBrush;
        private SolidColorBrush _structuralBrush;
        private SolidColorBrush _fractionalBrush;
        private SolidColorBrush _scalpBrush;
        private SolidColorBrush _anchorBrush;

        // ── Per-level pre-built brushes (line + label), created once in DataLoaded ──
        private SolidColorBrush[] _levelLineBrush  = new SolidColorBrush[11];
        private SolidColorBrush[] _levelLabelBrush = new SolidColorBrush[11];

        // ── Pre-built Stroke objects for Draw.HorizontalLine ─────────────────
        private Stroke[] _levelStroke = new Stroke[11];

        // ── Performance: fonts created once in DataLoaded ────────────────────
        private SimpleFont  _labelFont;
        private SimpleFont  _dashFont;

        // ── DOM polling throttle ─────────────────────────────────────────────
        private const double DOMThrottleSeconds = 1.0;        // poll DOM at most once per second
        private const int    LabelBarOffset     = -5;         // bars right of current bar for labels
        private DateTime    lastDOMCheck        = DateTime.MinValue;
        private double      lastDOMSize         = 0;          // cached DOM lots at nearest level

        // ── CumulativeDelta cached once per bar (performance: avoids repeated OF+ calls) ──
        private double      cachedDeltaClose    = 0;
        private double      cachedDeltaPrev     = 0;
        private bool        cachedDeltaValid    = false;
        private int         lastDeltaCacheBar   = -1;

        // ── Session VWAP (internal) ───────────────────────────────────────────
        private double      vwapNumerator       = 0;
        private double      vwapDenominator     = 0;
        private double      sessionVwap         = 0;

        // ── Dashboard refresh tracking ────────────────────────────────────────
        private int         lastDashboardBar    = -1;

        // ── Level metadata ────────────────────────────────────────────────────
        private static readonly double[]  LevelMultipliers = new double[]
        {  1.000,  0.500,  0.375,  0.250,  0.125,  0.000,
          -0.125, -0.250, -0.375, -0.500, -1.000 };

        private static readonly string[]  LevelNames = new string[]
        {
            "Full Margin Target",   // +100%
            "Structural Pivot",     // +50%
            "Fractional Wall",      // +37.5%
            "Risk Boundary",        // +25%
            "Scalper Line",         // +12.5%
            "Anchor",               // 0%
            "Scalper Line",         // -12.5%
            "Risk Boundary",        // -25%
            "Fractional Wall",      // -37.5%
            "Structural Pivot",     // -50%
            "Full Margin Target"    // -100%
        };

        private static readonly string[]  LevelPctLabels = new string[]
        {
            "+100.0%", "+50.0%", "+37.5%", "+25.0%", "+12.5%",
            "  0.0%",
            "-12.5%", "-25.0%", "-37.5%", "-50.0%", "-100.0%"
        };

        // Tier index lookup: 0=Primary,1=Structural,2=Fractional,3=Scalp,4=Anchor
        private static readonly int[] LevelTier = new int[]
        { 0, 1, 2, 2, 3, 4, 3, 2, 2, 1, 0 };

        // Minimum confirmation score to draw signal for each level (by tier)
        private static readonly int[] TierMinScore = new int[] { 1, 1, 1, 2, 1 };

        #endregion

        // ═══════════════════════════════════════════════════════════════════════
        #region NinjaScript State Management

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                // ── Descriptor ───────────────────────────────────────────────
                Description         = "Converts CME maintenance margin into institutional-grade price levels "
                                    + "anchored to the daily or weekly (Sunday 6PM Globex) open. "
                                    + "Includes confirmation scoring, dashboard, alerts, and session filters.";
                Name                = "CompleteInstitutionalPivots";
                Calculate           = Calculate.OnBarClose;
                IsOverlay           = true;
                DisplayInDataBox    = true;
                BarsRequiredToPlot  = 2;

                // ── 01. Core Parameters ──────────────────────────────────────
                SelectedAnchor      = UltimateAnchorPeriod.DailyOpen;
                CmeMargin           = 17500;
                Multiplier          = 20;

                // ── 02. Signal Configuration ─────────────────────────────────
                EnableSignals           = true;
                WickRatio               = 0.50;
                MinScoreToShowArrow     = 2;
                Score3BullArrowColor    = Brushes.Lime;
                Score3BearArrowColor    = Brushes.Red;
                Score2ArrowColor        = Brushes.Yellow;
                Score1MarkerColor       = Brushes.Gray;
                ArrowOffsetTicks        = 10;

                // ── 03. OrderFlow+ Confirmation ──────────────────────────────
                EnableDeltaFilter       = true;
                MinDeltaDivergence      = 500;
                EnableDOMFilter         = true;
                MinDOMSize              = 200;

                // ── 04–08. Tier Visual Defaults ───────────────────────────────
                ShowPrimary             = true;
                PrimaryColor            = Brushes.Red;
                PrimaryOpacity          = 100;
                PrimaryStyle            = DashStyleHelper.Solid;
                PrimaryThickness        = 2;

                ShowStructural          = true;
                StructuralColor         = Brushes.Orange;
                StructuralOpacity       = 100;
                StructuralStyle         = DashStyleHelper.Solid;
                StructuralThickness     = 1;

                ShowFractional          = true;
                FractionalColor         = Brushes.Yellow;
                FractionalOpacity       = 100;
                FractionalStyle         = DashStyleHelper.Dash;
                FractionalThickness     = 1;

                ShowScalp               = true;
                ScalpColor              = Brushes.Cyan;
                ScalpOpacity            = 100;
                ScalpStyle              = DashStyleHelper.Dot;
                ScalpThickness          = 1;

                ShowAnchorLine          = true;
                AnchorColor             = Brushes.White;
                AnchorOpacity           = 100;
                AnchorStyle             = DashStyleHelper.Solid;
                AnchorThickness         = 2;

                // ── 09. Label Appearance ─────────────────────────────────────
                ShowLabels              = true;
                LabelFontFamily         = "Consolas";
                LabelFontSize           = 10;
                LabelOpacity            = 100;
                ShowPriceValue          = true;
                ShowLevelName           = true;

                // ── 10. Dashboard ────────────────────────────────────────────
                ShowDashboard               = true;
                SelectedDashboardMode       = DashboardMode.Standard;
                DashboardPosition           = TextPosition.TopLeft;
                DashboardFontFamily         = "Consolas";
                DashboardFontSize           = 11;
                DashboardTextColor          = Brushes.GreenYellow;
                DashboardBackgroundColor    = Brushes.Black;
                DashboardBorderColor        = Brushes.DimGray;
                DashboardOpacity            = 85;
                ShowCustomAnchorRow         = true;
                ShowCustomNearestRow        = true;
                ShowCustomVWAPRow           = true;
                ShowCustomDayTypeRow        = true;
                ShowCustomDeltaRow          = true;
                ShowCustomDOMRow            = true;
                ShowCustomAllLevelsRow      = true;

                // ── 11. Alerts ────────────────────────────────────────────────
                EnableAlerts        = true;
                SoundFileName       = "Alert1.wav";
                MinScoreToAlert     = 2;
                AlertCooldownBars   = 10;

                // ── 12. Session Filters ───────────────────────────────────────
                RTHOnly                     = true;
                SelectedDayTypeFilter       = DayTypeFilter.All;
                RequireAboveVWAPForLong     = true;
                RequireBelowVWAPForShort    = true;

                // ── AddPlots (11 levels — for DataBox + Strategy compatibility) ──
                // Tier: Primary (±100%)
                AddPlot(new Stroke(Brushes.Red,    2), PlotStyle.Line, "Level_0_Upper100");
                // Tier: Structural (+50%)
                AddPlot(new Stroke(Brushes.Orange, 1), PlotStyle.Line, "Level_1_Upper50");
                // Tier: Fractional (+37.5%, +25%)
                AddPlot(new Stroke(Brushes.Yellow, 1), PlotStyle.Dash, "Level_2_Upper375");
                AddPlot(new Stroke(Brushes.Yellow, 1), PlotStyle.Dash, "Level_3_Upper25");
                // Tier: Scalp (+12.5%)
                AddPlot(new Stroke(Brushes.Cyan,   1), PlotStyle.Dot,  "Level_4_Upper125");
                // Tier: Anchor (0%)
                AddPlot(new Stroke(Brushes.White,  2), PlotStyle.Line, "Level_5_Anchor");
                // Tier: Scalp (-12.5%)
                AddPlot(new Stroke(Brushes.Cyan,   1), PlotStyle.Dot,  "Level_6_Lower125");
                // Tier: Fractional (-25%, -37.5%)
                AddPlot(new Stroke(Brushes.Yellow, 1), PlotStyle.Dash, "Level_7_Lower25");
                AddPlot(new Stroke(Brushes.Yellow, 1), PlotStyle.Dash, "Level_8_Lower375");
                // Tier: Structural (-50%)
                AddPlot(new Stroke(Brushes.Orange, 1), PlotStyle.Line, "Level_9_Lower50");
                // Tier: Primary (-100%)
                AddPlot(new Stroke(Brushes.Red,    2), PlotStyle.Line, "Level_10_Lower100");
            }
            else if (State == State.DataLoaded)
            {
                // ── Compute tickUnit once (no fullMoveTicks intermediate) ─────
                tickUnit = (Multiplier != 0) ? (CmeMargin / Multiplier) : 0;

                // ── Null-safe, .wav-guaranteed alert sound ───────────────────
                alertSoundFile = string.IsNullOrWhiteSpace(SoundFileName)
                    ? "Alert1.wav"
                    : (SoundFileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                        ? SoundFileName
                        : SoundFileName + ".wav");

                // ── Create brushes ONCE (performance rule) ───────────────────
                _primaryBrush    = CloneBrush(PrimaryColor);
                _structuralBrush = CloneBrush(StructuralColor);
                _fractionalBrush = CloneBrush(FractionalColor);
                _scalpBrush      = CloneBrush(ScalpColor);
                _anchorBrush     = CloneBrush(AnchorColor);

                // ── Build per-level line brushes, label brushes, and Strokes ONCE ──
                Brush[] tierBrush   = new Brush[] { PrimaryColor, StructuralColor, FractionalColor, ScalpColor, AnchorColor };
                int[]   tierOpacity = new int[]   { PrimaryOpacity, StructuralOpacity, FractionalOpacity, ScalpOpacity, AnchorOpacity };
                DashStyleHelper[] tierStyle = new DashStyleHelper[] { PrimaryStyle, StructuralStyle, FractionalStyle, ScalpStyle, AnchorStyle };
                int[]   tierThick   = new int[]   { PrimaryThickness, StructuralThickness, FractionalThickness, ScalpThickness, AnchorThickness };

                for (int i = 0; i < 11; i++)
                {
                    int    tier    = LevelTier[i];
                    var    sc      = (tierBrush[tier] as SolidColorBrush)?.Color ?? Colors.White;
                    byte   lineA   = (byte)Math.Round(tierOpacity[tier] / 100.0 * 255);
                    byte   labelA  = (byte)Math.Round((LabelOpacity / 100.0) * 255);

                    var lineBrush = new SolidColorBrush(Color.FromArgb(lineA, sc.R, sc.G, sc.B));
                    lineBrush.Freeze();
                    _levelLineBrush[i] = lineBrush;

                    var labelBrush = new SolidColorBrush(Color.FromArgb(labelA, sc.R, sc.G, sc.B));
                    labelBrush.Freeze();
                    _levelLabelBrush[i] = labelBrush;

                    _levelStroke[i] = new Stroke(lineBrush, tierStyle[tier], tierThick[tier]);
                }

                // ── Create fonts ONCE (performance rule) ─────────────────────
                _labelFont  = new SimpleFont(LabelFontFamily, LabelFontSize);
                _dashFont   = new SimpleFont(DashboardFontFamily, DashboardFontSize);

                // ── Initialize alert cooldown array ──────────────────────────
                for (int i = 0; i < 11; i++) lastAlertBar[i] = -999;

                // ── Reset state ───────────────────────────────────────────────
                needsRedraw         = true;
                lastDashboardAnchor = -1;
                vwapNumerator       = 0;
                vwapDenominator     = 0;
            }
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════
        #region OnBarUpdate

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 2) return;

            // ── 1. Update internal session VWAP ──────────────────────────────
            UpdateSessionVwap();

            // ── 2. Anchor detection ───────────────────────────────────────────
            UpdateAnchor();

            if (anchorPrice == 0) return;

            // ── 3. Cache CumulativeDelta ONCE per bar (avoids repeated OF+ calls) ──
            CacheCumulativeDelta();

            // ── 4. Compute 11 levels ──────────────────────────────────────────
            for (int i = 0; i < 11; i++)
                targetLevels[i] = anchorPrice + (tickUnit * LevelMultipliers[i]);

            // ── 5. Assign plot values (DataBox + Strategy) ────────────────────
            for (int i = 0; i < 11; i++)
                Values[i][0] = targetLevels[i];

            // ── 6. Draw lines & labels (only when anchor changes) ────────────
            if (needsRedraw)
            {
                DrawLevelLines();
                if (ShowLabels) DrawLevelLabels();
                needsRedraw = false;
            }

            // ── 7. Dashboard (once per bar on anchor change or mode change) ──
            bool dashboardDue = (ShowDashboard)
                && (anchorPrice != lastDashboardAnchor || CurrentBar != lastDashboardBar);

            if (dashboardDue)
            {
                DrawDashboard();
                lastDashboardAnchor = anchorPrice;
                lastDashboardBar    = CurrentBar;
            }

            // ── 7. Signal engine ──────────────────────────────────────────────
            if (EnableSignals) RunSignalEngine();
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════
        #region Anchor Logic (Bug-Fixed)

        private void UpdateAnchor()
        {
            if (SelectedAnchor == UltimateAnchorPeriod.DailyOpen)
            {
                // Set anchor on the FIRST bar of each trading session
                if (Bars.IsFirstBarOfSession)
                {
                    double newAnchor = Open[0];
                    if (newAnchor != anchorPrice)
                    {
                        anchorPrice  = newAnchor;
                        needsRedraw  = true;
                        ResetLevelCounters();
                    }
                }
            }
            else // WeeklyOpen — Sunday 6PM Globex CME open (bug-fixed)
            {
                // Use date comparison instead of GetWeekOfYear integer (which can collide across years)
                if (Time[0].DayOfWeek == DayOfWeek.Sunday && Bars.IsFirstBarOfSession)
                {
                    DateTime weekStart = Time[0].Date;
                    if (weekStart != lastWeekAnchorDate)
                    {
                        double newAnchor = Open[0];
                        if (newAnchor != anchorPrice)
                        {
                            anchorPrice         = newAnchor;
                            lastWeekAnchorDate  = weekStart;
                            needsRedraw         = true;
                            ResetLevelCounters();
                        }
                        else
                        {
                            lastWeekAnchorDate = weekStart;
                        }
                    }
                }
            }
        }

        private void ResetLevelCounters()
        {
            for (int i = 0; i < 11; i++) levelTestCount[i] = 0;
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════
        #region Session VWAP (Internal Calculation)

        private void UpdateSessionVwap()
        {
            // Reset VWAP on first bar of each session
            if (Bars.IsFirstBarOfSession)
            {
                vwapNumerator   = 0;
                vwapDenominator = 0;
            }

            double typicalPrice = (High[0] + Low[0] + Close[0]) / 3.0;
            double vol          = Volume[0];
            vwapNumerator      += typicalPrice * vol;
            vwapDenominator    += vol;
            sessionVwap         = (vwapDenominator > 0) ? vwapNumerator / vwapDenominator : Close[0];
        }

        /// <summary>
        /// Caches CumulativeDelta values once per bar so dashboard and signal engine
        /// both read from the same call (avoids repeated OrderFlow+ indicator lookups).
        /// Wrapped in try/catch — if OF+ is not installed, cached values remain invalid
        /// and all delta checks are silently skipped.
        /// </summary>
        private void CacheCumulativeDelta()
        {
            if (lastDeltaCacheBar == CurrentBar) return; // already cached this bar
            lastDeltaCacheBar = CurrentBar;
            cachedDeltaValid  = false;

            if (!EnableDeltaFilter) return;

            try
            {
                var cd = CumulativeDelta(BarsArray[0], CumulativeDeltaType.BidAsk, CumulativeDeltaCalculationMode.CloseBar, 0);
                if (cd != null && CurrentBar > 0)
                {
                    cachedDeltaClose = cd.DeltaClose[0];
                    cachedDeltaPrev  = cd.DeltaClose[1];
                    cachedDeltaValid = true;
                }
            }
            catch { /* OrderFlow+ not installed — delta checks will be skipped */ }
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════
        #region Line & Label Rendering

        private void DrawLevelLines()
        {
            for (int i = 0; i < 11; i++)
            {
                int  tier = LevelTier[i];
                bool showLine;
                GetTierShowFlag(tier, out showLine);

                if (!showLine) continue;

                // Use pre-built Stroke (created once in DataLoaded — no allocations here)
                Draw.HorizontalLine(this, "Level_" + i, false,
                    targetLevels[i],
                    _levelStroke[i],
                    true);
            }
        }

        private void DrawLevelLabels()
        {
            if (!ShowLabels) return;

            string anchorPrefix = (SelectedAnchor == UltimateAnchorPeriod.DailyOpen) ? "D" : "W";
            string priceFmt     = string.Format("N{0}", Instrument.MasterInstrument.PriceFormat);

            // Track last drawn Y price position for stagger logic
            double lastDrawnPrice = double.MinValue;
            // Stagger by ticks (not font pixels × TickSize) for consistent behavior across instruments
            double pixelStagger   = (LabelFontSize + 2) * TickSize;

            // Iterate top → bottom so stagger is applied downward
            for (int i = 0; i < 11; i++)
            {
                int  tier = LevelTier[i];
                bool showLine;
                GetTierShowFlag(tier, out showLine);

                if (!showLine) continue;

                double levelPrice = targetLevels[i];

                // Stagger: if this label would overlap the previous one, push it down by stagger amount
                if (lastDrawnPrice != double.MinValue && (lastDrawnPrice - levelPrice) < pixelStagger)
                    levelPrice = lastDrawnPrice - pixelStagger;

                // Build label text using string.Format (no concatenation in loop)
                string priceStr   = targetLevels[i].ToString(priceFmt, CultureInfo.InvariantCulture);
                string labelText  = string.Format("[{0} {1}]", anchorPrefix, LevelPctLabels[i].Trim());
                if (ShowPriceValue) labelText = labelText + " " + priceStr;
                if (ShowLevelName)  labelText = labelText + " | " + LevelNames[i];

                // Use pre-built label brush (created once in DataLoaded — no allocations here)
                Draw.Text(this, "Label_" + i, false, labelText,
                    LabelBarOffset,                 // bars to the right of the current bar
                    levelPrice,
                    0,
                    _levelLabelBrush[i],
                    _labelFont,
                    System.Windows.TextAlignment.Left,
                    Brushes.Transparent,
                    Brushes.Transparent,
                    LabelOpacity);

                lastDrawnPrice = levelPrice;
            }
        }

        /// <summary>Returns only the show/hide flag for a tier (used in rendering hot paths).</summary>
        private void GetTierShowFlag(int tier, out bool show)
        {
            switch (tier)
            {
                case 0:  show = ShowPrimary;    break;
                case 1:  show = ShowStructural; break;
                case 2:  show = ShowFractional; break;
                case 3:  show = ShowScalp;      break;
                default: show = ShowAnchorLine; break;
            }
        }

        /// <summary>
        /// Returns the full visual properties for a given tier (0=Primary,1=Structural,
        /// 2=Fractional,3=Scalp,4=Anchor).
        /// </summary>
        private void GetTierProperties(int tier, out bool show, out Brush color,
            out int opacity, out DashStyleHelper style, out int thickness)
        {
            switch (tier)
            {
                case 0: // Primary ±100%
                    show      = ShowPrimary;
                    color     = PrimaryColor;
                    opacity   = PrimaryOpacity;
                    style     = PrimaryStyle;
                    thickness = PrimaryThickness;
                    break;
                case 1: // Structural ±50%
                    show      = ShowStructural;
                    color     = StructuralColor;
                    opacity   = StructuralOpacity;
                    style     = StructuralStyle;
                    thickness = StructuralThickness;
                    break;
                case 2: // Fractional ±37.5%, ±25%
                    show      = ShowFractional;
                    color     = FractionalColor;
                    opacity   = FractionalOpacity;
                    style     = FractionalStyle;
                    thickness = FractionalThickness;
                    break;
                case 3: // Scalp ±12.5%
                    show      = ShowScalp;
                    color     = ScalpColor;
                    opacity   = ScalpOpacity;
                    style     = ScalpStyle;
                    thickness = ScalpThickness;
                    break;
                default: // Anchor 0%
                    show      = ShowAnchorLine;
                    color     = AnchorColor;
                    opacity   = AnchorOpacity;
                    style     = AnchorStyle;
                    thickness = AnchorThickness;
                    break;
            }
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════
        #region Dashboard

        private void DrawDashboard()
        {
            if (!ShowDashboard) return;

            string text;
            switch (SelectedDashboardMode)
            {
                case DashboardMode.Minimal:
                    text = BuildDashMinimal();
                    break;
                case DashboardMode.Full:
                    text = BuildDashFull();
                    break;
                case DashboardMode.Custom:
                    text = BuildDashCustom();
                    break;
                default: // Standard
                    text = BuildDashStandard();
                    break;
            }

            Draw.TextFixed(this, "CIP_Dashboard", text,
                DashboardPosition,
                DashboardTextColor,
                _dashFont,
                DashboardBorderColor,
                DashboardBackgroundColor,
                DashboardOpacity);
        }

        private string BuildDashMinimal()
        {
            string anchorStr = (SelectedAnchor == UltimateAnchorPeriod.DailyOpen) ? "D" : "W";
            int    nearest;
            double nearestDist;
            FindNearestLevel(out nearest, out nearestDist);

            return string.Format(
                "INSTITUTIONAL PIVOTS\n" +
                "Anchor [{0}]: {1}\n" +
                "Nearest: {2} {3}\n" +
                "Distance: {4:F2} pts → {5}",
                anchorStr,
                FormatPrice(anchorPrice),
                LevelPctLabels[nearest].Trim(),
                LevelNames[nearest],
                nearestDist,
                FormatPrice(targetLevels[nearest]));
        }

        private string BuildDashStandard()
        {
            string anchorStr = (SelectedAnchor == UltimateAnchorPeriod.DailyOpen) ? "D" : "W";
            int    nearest;
            double nearestDist;
            FindNearestLevel(out nearest, out nearestDist);
            string biasStr = (Close[0] > sessionVwap) ? "ABOVE → LONG" : "BELOW → SHORT";
            string dayType = GetDayTypeString();

            // Use cached delta (populated once per bar in CacheCumulativeDelta)
            string deltaStr = cachedDeltaValid
                ? string.Format("{0:N0}", cachedDeltaClose)
                : "N/A";

            return string.Format(
                "=== INSTITUTIONAL MARGIN INTEL ===\n" +
                "Anchor [{0}]: {1}\n" +
                "Nearest Level: {2} {3}\n" +
                "Distance: {4:F2} pts → {5}\n" +
                "─────────────────────────────────\n" +
                "VWAP: {6}  Bias: {7}\n" +
                "Day Type: {8}\n" +
                "Cum. Delta @ Level: {9}",
                anchorStr,
                FormatPrice(anchorPrice),
                LevelPctLabels[nearest].Trim(),
                LevelNames[nearest],
                nearestDist,
                FormatPrice(targetLevels[nearest]),
                FormatPrice(sessionVwap),
                biasStr,
                dayType,
                deltaStr);
        }

        private string BuildDashFull()
        {
            string anchorStr = (SelectedAnchor == UltimateAnchorPeriod.DailyOpen) ? "D" : "W";
            int    nearest;
            double nearestDist;
            FindNearestLevel(out nearest, out nearestDist);
            double nearestTicks = (TickSize > 0) ? Math.Round(nearestDist / TickSize) : 0;
            string biasStr      = (Close[0] > sessionVwap) ? "ABOVE → LONG" : "BELOW → SHORT";
            string dayType      = GetDayTypeString();

            // Use cached delta (populated once per bar in CacheCumulativeDelta)
            string deltaStr = cachedDeltaValid
                ? string.Format("{0:N0}", cachedDeltaClose)
                : "N/A";

            // DOM (throttled, cached)
            string domStr = "N/A";
            if (EnableDOMFilter)
            {
                try
                {
                    if ((DateTime.Now - lastDOMCheck).TotalSeconds >= DOMThrottleSeconds)
                    {
                        lastDOMCheck = DateTime.Now;
                        lastDOMSize  = GetDOMSizeAtLevel(targetLevels[nearest]);
                    }
                    domStr = string.Format("{0:N0} lots", lastDOMSize);
                }
                catch { }
            }

            // Last reaction distance
            string lastReaction = "N/A";

            // Build all-levels table
            string levelsTable = BuildAllLevelsTable();

            return string.Format(
                "=== INSTITUTIONAL MARGIN INTEL ===\n" +
                "Anchor [{0}]: {1}\n" +
                "Nearest: {2} {3} @ {4}\n" +
                "Distance: {5:F2} pts ({6} ticks)\n" +
                "─────────────────────────────────\n" +
                "Cum. Delta @ Nearest: {7}\n" +
                "DOM Defending: {8}\n" +
                "Level Tests Today: {9}x\n" +
                "Last Reaction: {10}\n" +
                "─────────────────────────────────\n" +
                "VWAP: {11}  Bias: {12}\n" +
                "Day Type: {13}\n" +
                "─────────────────────────────────\n" +
                "{14}",
                anchorStr,
                FormatPrice(anchorPrice),
                LevelPctLabels[nearest].Trim(),
                LevelNames[nearest],
                FormatPrice(targetLevels[nearest]),
                nearestDist,
                nearestTicks,
                deltaStr,
                domStr,
                levelTestCount[nearest],
                lastReaction,
                FormatPrice(sessionVwap),
                biasStr,
                dayType,
                levelsTable);
        }

        private string BuildDashCustom()
        {
            string anchorStr = (SelectedAnchor == UltimateAnchorPeriod.DailyOpen) ? "D" : "W";
            int    nearest;
            double nearestDist;
            FindNearestLevel(out nearest, out nearestDist);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== INSTITUTIONAL MARGIN INTEL ===");

            if (ShowCustomAnchorRow)
                sb.AppendLine(string.Format("Anchor [{0}]: {1}", anchorStr, FormatPrice(anchorPrice)));

            if (ShowCustomNearestRow)
                sb.AppendLine(string.Format("Nearest: {0} {1} @ {2}", LevelPctLabels[nearest].Trim(), LevelNames[nearest], FormatPrice(targetLevels[nearest])));

            if (ShowCustomVWAPRow)
            {
                string biasStr = (Close[0] > sessionVwap) ? "ABOVE → LONG" : "BELOW → SHORT";
                sb.AppendLine(string.Format("VWAP: {0}  Bias: {1}", FormatPrice(sessionVwap), biasStr));
            }

            if (ShowCustomDayTypeRow)
                sb.AppendLine(string.Format("Day Type: {0}", GetDayTypeString()));

            if (ShowCustomDeltaRow)
            {
                // Use cached delta (populated once per bar in CacheCumulativeDelta)
                string deltaStr = cachedDeltaValid
                    ? string.Format("{0:N0}", cachedDeltaClose)
                    : "N/A";
                sb.AppendLine(string.Format("Cum. Delta @ Level: {0}", deltaStr));
            }

            if (ShowCustomDOMRow)
            {
                string domStr = "N/A";
                if (EnableDOMFilter)
                {
                    try
                    {
                        if ((DateTime.Now - lastDOMCheck).TotalSeconds >= DOMThrottleSeconds)
                        {
                            lastDOMCheck = DateTime.Now;
                            lastDOMSize  = GetDOMSizeAtLevel(targetLevels[nearest]);
                        }
                        domStr = string.Format("{0:N0} lots", lastDOMSize);
                    }
                    catch { }
                }
                sb.AppendLine(string.Format("DOM Defending: {0}", domStr));
            }

            if (ShowCustomAllLevelsRow)
                sb.Append(BuildAllLevelsTable());

            return sb.ToString().TrimEnd();
        }

        /// <summary>Builds the symmetric two-column all-levels table used in Full mode.</summary>
        private string BuildAllLevelsTable()
        {
            // Pair upper and lower levels symmetrically
            // [+100%] upper  |  [-100%] lower  etc.
            string[] pairs = new string[]
            {
                string.Format("[+100%] {0,-12}  [-100%] {1}", FormatPrice(targetLevels[0]),  FormatPrice(targetLevels[10])),
                string.Format("[ +50%] {0,-12}  [ -50%] {1}", FormatPrice(targetLevels[1]),  FormatPrice(targetLevels[9])),
                string.Format("[+37.5%] {0,-11}  [-37.5%] {1}", FormatPrice(targetLevels[2]), FormatPrice(targetLevels[8])),
                string.Format("[ +25%] {0,-12}  [ -25%] {1}", FormatPrice(targetLevels[3]),  FormatPrice(targetLevels[7])),
                string.Format("[+12.5%] {0,-11}  [-12.5%] {1}", FormatPrice(targetLevels[4]), FormatPrice(targetLevels[6])),
                string.Format("        ANCHOR: {0}",            FormatPrice(targetLevels[5]))
            };
            return string.Join("\n", pairs);
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════
        #region Signal Engine — Confirmation Scoring (0–3)

        private void RunSignalEngine()
        {
            // ── RTH filter ───────────────────────────────────────────────────
            if (RTHOnly)
            {
                var t = Time[0].TimeOfDay;
                if (t < new TimeSpan(9, 30, 0) || t > new TimeSpan(16, 0, 0)) return;
            }

            double candleRange = High[0] - Low[0];
            if (candleRange <= 0) return;

            double upperWick = High[0] - Math.Max(Open[0], Close[0]);
            double lowerWick = Math.Min(Open[0], Close[0]) - Low[0];

            // ── Shared confirmations — use cached delta from CacheCumulativeDelta() ─
            bool hasDelta  = cachedDeltaValid;
            int  bullDelta = 0;
            int  bearDelta = 0;

            if (hasDelta)
            {
                double change = cachedDeltaClose - cachedDeltaPrev;
                // Bearish divergence: delta rising but price closes below level (distribution)
                if (change >= MinDeltaDivergence)  bearDelta = 1;
                // Bullish divergence: delta falling (negative) but price closes above (absorption)
                if (change <= -MinDeltaDivergence) bullDelta = 1;
            }

            // ── DOM confirmation (throttled, uses DOMThrottleSeconds constant) ──
            double domSize = 0;
            if (EnableDOMFilter)
            {
                try
                {
                    if ((DateTime.Now - lastDOMCheck).TotalSeconds >= DOMThrottleSeconds)
                    {
                        lastDOMCheck = DateTime.Now;
                        domSize      = GetDOMSizeAtLevel(Close[0]);
                        lastDOMSize  = domSize;
                    }
                    else
                    {
                        domSize = lastDOMSize;
                    }
                }
                catch { }
            }

            // ── Per-level scoring loop (no break — multiple levels can fire) ─
            for (int j = 0; j < 11; j++)
            {
                double currentLevel = targetLevels[j];
                int    tier         = LevelTier[j];

                bool touchesBull = Low[0]  <= currentLevel && Close[0] > currentLevel;
                bool touchesBear = High[0] >= currentLevel && Close[0] < currentLevel;

                if (!touchesBull && !touchesBear) continue;

                // Count level test
                levelTestCount[j]++;

                // ── Score computation ─────────────────────────────────────────
                int score = 0;

                if (touchesBull)
                {
                    // +1: Wick rejection geometry
                    if ((lowerWick / candleRange) >= WickRatio) score++;

                    // +1: Cumulative delta divergence (absorption: delta falling, price rises)
                    if (EnableDeltaFilter && hasDelta && bullDelta > 0) score++;

                    // +1: DOM absorption
                    if (EnableDOMFilter && domSize >= MinDOMSize) score++;
                }
                else // touchesBear
                {
                    // +1: Wick rejection geometry
                    if ((upperWick / candleRange) >= WickRatio) score++;

                    // +1: Cumulative delta divergence (distribution: delta rising, price falls)
                    if (EnableDeltaFilter && hasDelta && bearDelta > 0) score++;

                    // +1: DOM absorption
                    if (EnableDOMFilter && domSize >= MinDOMSize) score++;
                }

                // ── Tier minimum score gate ───────────────────────────────────
                int tierMin = TierMinScore[tier];
                if (score < tierMin) continue;

                // ── Minimum score to show arrow ───────────────────────────────
                if (score < MinScoreToShowArrow) continue;

                // ── VWAP bias filter ──────────────────────────────────────────
                if (touchesBull && RequireAboveVWAPForLong  && Close[0] < sessionVwap) continue;
                if (touchesBear && RequireBelowVWAPForShort && Close[0] > sessionVwap) continue;

                // ── Draw signal ───────────────────────────────────────────────
                double arrowOffset = TickSize * ArrowOffsetTicks;

                if (touchesBull)
                {
                    if (score >= 3)
                    {
                        Draw.ArrowUp(this, "Bull_" + j + "_" + CurrentBar, true,
                            0, Low[0] - arrowOffset, Score3BullArrowColor);
                    }
                    else // score == 2 (MinScoreToShowArrow guaranteed by gate above)
                    {
                        Draw.ArrowUp(this, "Bull_" + j + "_" + CurrentBar, true,
                            0, Low[0] - arrowOffset, Score2ArrowColor);
                    }
                }
                else
                {
                    if (score >= 3)
                    {
                        Draw.ArrowDown(this, "Bear_" + j + "_" + CurrentBar, true,
                            0, High[0] + arrowOffset, Score3BearArrowColor);
                    }
                    else
                    {
                        Draw.ArrowDown(this, "Bear_" + j + "_" + CurrentBar, true,
                            0, High[0] + arrowOffset, Score2ArrowColor);
                    }
                }

                // Score 1 — small diamond marker (only if MinScoreToShowArrow == 1)
                // This path is reached when score >= MinScoreToShowArrow and score == 1
                if (score == 1)
                {
                    if (touchesBull)
                        Draw.Diamond(this, "Bull_" + j + "_" + CurrentBar, true, 0,
                            Low[0] - arrowOffset, Score1MarkerColor);
                    else
                        Draw.Diamond(this, "Bear_" + j + "_" + CurrentBar, true, 0,
                            High[0] + arrowOffset, Score1MarkerColor);
                }

                // ── Alert (realtime only, with cooldown) ──────────────────────
                if (EnableAlerts && score >= MinScoreToAlert && State == State.Realtime)
                {
                    if ((CurrentBar - lastAlertBar[j]) >= AlertCooldownBars)
                    {
                        string direction = touchesBull ? "Bullish" : "Bearish";
                        string msg = string.Format(
                            "{0} Institutional Signal @ {1} (Level {2} — {3}, Score {4})",
                            direction, FormatPrice(currentLevel), j, LevelNames[j], score);

                        Alert("CIP_Alert_" + j, Priority.High, msg,
                            alertSoundFile, AlertCooldownBars,
                            Brushes.Black,
                            touchesBull ? Score3BullArrowColor : Score3BearArrowColor);

                        lastAlertBar[j] = CurrentBar;
                    }
                }
            }
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════
        #region Helpers

        /// <summary>Finds the nearest level to current close price.</summary>
        private void FindNearestLevel(out int nearestIdx, out double nearestDist)
        {
            nearestIdx  = 5; // default to anchor
            nearestDist = Math.Abs(Close[0] - targetLevels[5]);
            for (int i = 0; i < 11; i++)
            {
                double d = Math.Abs(Close[0] - targetLevels[i]);
                if (d < nearestDist) { nearestDist = d; nearestIdx = i; }
            }
        }

        /// <summary>Formats a price to instrument tick precision.</summary>
        private string FormatPrice(double price)
        {
            int decimals = Instrument?.MasterInstrument?.PriceFormat ?? 2;
            return price.ToString("N" + decimals, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Returns a day-type string for the dashboard.
        /// When DayTypeFilter is set to RangeOnly or TrendOnly the dashboard reflects the
        /// active filter setting (i.e., only that day type will produce signals).
        /// When set to All, no classification is performed and "UNFILTERED" is shown.
        /// </summary>
        private string GetDayTypeString()
        {
            if (SelectedDayTypeFilter == DayTypeFilter.RangeOnly) return "RANGE (filter active)";
            if (SelectedDayTypeFilter == DayTypeFilter.TrendOnly) return "TREND (filter active)";
            return "UNFILTERED";
        }

        /// <summary>
        /// Returns the DOM defending lot size at a given level.
        /// NOTE: Full Level 2 / SuperDOM integration requires an active Level 2 data subscription
        /// and NT8 MarketDepth event registration (OnMarketDepth). This stub returns 0,
        /// which means EnableDOMFilter will never score +1 until real L2 data is wired up.
        /// To implement: subscribe to OnMarketDepth, cache bid/ask depth at each level,
        /// and return the refreshing lot size here.
        /// </summary>
        private double GetDOMSizeAtLevel(double level)
        {
            // Placeholder — returns 0 until Level 2 depth integration is added.
            return 0;
        }

        /// <summary>Creates a frozen clone of a brush safe for use across threads.</summary>
        private SolidColorBrush CloneBrush(Brush source)
        {
            var scb = source as SolidColorBrush;
            if (scb == null) return new SolidColorBrush(Colors.White);
            var clone = new SolidColorBrush(scb.Color);
            clone.Freeze();
            return clone;
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════
        #region Properties — 01. Core Parameters

        [NinjaScriptProperty]
        [Display(Name = "Anchor Period", Description = "Anchor levels to the Daily session open or the Sunday 6PM Globex weekly open.",
            Order = 1, GroupName = "01. Core Parameters")]
        public UltimateAnchorPeriod SelectedAnchor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "CME Margin Requirement ($)", Description = "Current CME maintenance margin requirement in dollars (e.g., 17500 for NQ).",
            Order = 2, GroupName = "01. Core Parameters")]
        public double CmeMargin { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Contract Multiplier ($)", Description = "Dollar value per 1 full price point (e.g., 20 for NQ).",
            Order = 3, GroupName = "01. Core Parameters")]
        public double Multiplier { get; set; }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════
        #region Properties — 02. Signal Configuration

        [NinjaScriptProperty]
        [Display(Name = "Enable Signals", Description = "Draw entry signals at institutional margin levels.",
            Order = 1, GroupName = "02. Signal Configuration")]
        public bool EnableSignals { get; set; }

        [NinjaScriptProperty]
        [Range(0.10, 0.90)]
        [Display(Name = "Wick Rejection Ratio", Description = "Minimum fraction of candle range that must be a wick to score +1 confirmation (0.10–0.90).",
            Order = 2, GroupName = "02. Signal Configuration")]
        public double WickRatio { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3)]
        [Display(Name = "Min Score to Show Arrow", Description = "Minimum confirmation score (1–3) required to draw an arrow. Score 1 draws a diamond.",
            Order = 3, GroupName = "02. Signal Configuration")]
        public int MinScoreToShowArrow { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Score 3 Bull Arrow Color", Description = "Arrow color for score-3 bullish signals.",
            Order = 4, GroupName = "02. Signal Configuration")]
        public Brush Score3BullArrowColor { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Score 3 Bear Arrow Color", Description = "Arrow color for score-3 bearish signals.",
            Order = 5, GroupName = "02. Signal Configuration")]
        public Brush Score3BearArrowColor { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Score 2 Arrow Color", Description = "Arrow color for score-2 signals.",
            Order = 6, GroupName = "02. Signal Configuration")]
        public Brush Score2ArrowColor { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Score 1 Marker Color", Description = "Diamond color for score-1 signals.",
            Order = 7, GroupName = "02. Signal Configuration")]
        public Brush Score1MarkerColor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Arrow Offset (Ticks)", Description = "How many ticks above/below the bar to offset drawn arrows.",
            Order = 8, GroupName = "02. Signal Configuration")]
        public int ArrowOffsetTicks { get; set; }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════
        #region Properties — 03. OrderFlow+ Confirmation

        [NinjaScriptProperty]
        [Display(Name = "Enable Delta Filter", Description = "Score +1 when cumulative delta diverges from price at the level (requires OrderFlow+).",
            Order = 1, GroupName = "03. OrderFlow+ Confirmation")]
        public bool EnableDeltaFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Min Delta Divergence", Description = "Minimum absolute cumulative delta change to qualify as a divergence signal.",
            Order = 2, GroupName = "03. OrderFlow+ Confirmation")]
        public int MinDeltaDivergence { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable DOM Filter", Description = "Score +1 when large passive limit orders are detected defending a level. Requires Level 2 data subscription and OnMarketDepth integration (stub implementation — extend GetDOMSizeAtLevel to activate).",
            Order = 3, GroupName = "03. OrderFlow+ Confirmation")]
        public bool EnableDOMFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Min DOM Size (lots)", Description = "Minimum refreshing limit order size in lots to qualify as DOM absorption (effective only when GetDOMSizeAtLevel returns non-zero from a wired Level 2 source).",
            Order = 4, GroupName = "03. OrderFlow+ Confirmation")]
        public int MinDOMSize { get; set; }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════
        #region Properties — 04. Primary Levels (±100%)

        [NinjaScriptProperty]
        [Display(Name = "Show Primary Lines", Description = "Show/hide the ±100% Full Margin Target levels.",
            Order = 1, GroupName = "04. Primary Levels (\u00b1100%)")]
        public bool ShowPrimary { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Primary Color", Order = 2, GroupName = "04. Primary Levels (\u00b1100%)")]
        public Brush PrimaryColor { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Primary Opacity", Order = 3, GroupName = "04. Primary Levels (\u00b1100%)")]
        public int PrimaryOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Primary Style", Order = 4, GroupName = "04. Primary Levels (\u00b1100%)")]
        public DashStyleHelper PrimaryStyle { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Primary Thickness", Order = 5, GroupName = "04. Primary Levels (\u00b1100%)")]
        public int PrimaryThickness { get; set; }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════
        #region Properties — 05. Structural Levels (±50%)

        [NinjaScriptProperty]
        [Display(Name = "Show Structural Lines", Description = "Show/hide the ±50% Structural Pivot levels.",
            Order = 1, GroupName = "05. Structural Levels (\u00b150%)")]
        public bool ShowStructural { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Structural Color", Order = 2, GroupName = "05. Structural Levels (\u00b150%)")]
        public Brush StructuralColor { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Structural Opacity", Order = 3, GroupName = "05. Structural Levels (\u00b150%)")]
        public int StructuralOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Structural Style", Order = 4, GroupName = "05. Structural Levels (\u00b150%)")]
        public DashStyleHelper StructuralStyle { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Structural Thickness", Order = 5, GroupName = "05. Structural Levels (\u00b150%)")]
        public int StructuralThickness { get; set; }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════
        #region Properties — 06. Fractional Levels (±37.5%, ±25%)

        [NinjaScriptProperty]
        [Display(Name = "Show Fractional Lines", Description = "Show/hide the ±37.5% and ±25% Fractional Wall / Risk Boundary levels.",
            Order = 1, GroupName = "06. Fractional Levels (\u00b137.5%, \u00b125%)")]
        public bool ShowFractional { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Fractional Color", Order = 2, GroupName = "06. Fractional Levels (\u00b137.5%, \u00b125%)")]
        public Brush FractionalColor { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Fractional Opacity", Order = 3, GroupName = "06. Fractional Levels (\u00b137.5%, \u00b125%)")]
        public int FractionalOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Fractional Style", Order = 4, GroupName = "06. Fractional Levels (\u00b137.5%, \u00b125%)")]
        public DashStyleHelper FractionalStyle { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Fractional Thickness", Order = 5, GroupName = "06. Fractional Levels (\u00b137.5%, \u00b125%)")]
        public int FractionalThickness { get; set; }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════
        #region Properties — 07. Scalp Levels (±12.5%)

        [NinjaScriptProperty]
        [Display(Name = "Show Scalp Lines", Description = "Show/hide the ±12.5% Scalper Line levels.",
            Order = 1, GroupName = "07. Scalp Levels (\u00b112.5%)")]
        public bool ShowScalp { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Scalp Color", Order = 2, GroupName = "07. Scalp Levels (\u00b112.5%)")]
        public Brush ScalpColor { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Scalp Opacity", Order = 3, GroupName = "07. Scalp Levels (\u00b112.5%)")]
        public int ScalpOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Scalp Style", Order = 4, GroupName = "07. Scalp Levels (\u00b112.5%)")]
        public DashStyleHelper ScalpStyle { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Scalp Thickness", Order = 5, GroupName = "07. Scalp Levels (\u00b112.5%)")]
        public int ScalpThickness { get; set; }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════
        #region Properties — 08. Anchor Line

        [NinjaScriptProperty]
        [Display(Name = "Show Anchor Line", Description = "Show/hide the Anchor (0%) line.",
            Order = 1, GroupName = "08. Anchor Line")]
        public bool ShowAnchorLine { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Anchor Color", Order = 2, GroupName = "08. Anchor Line")]
        public Brush AnchorColor { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Anchor Opacity", Order = 3, GroupName = "08. Anchor Line")]
        public int AnchorOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Anchor Style", Order = 4, GroupName = "08. Anchor Line")]
        public DashStyleHelper AnchorStyle { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Anchor Thickness", Order = 5, GroupName = "08. Anchor Line")]
        public int AnchorThickness { get; set; }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════
        #region Properties — 09. Label Appearance

        [NinjaScriptProperty]
        [Display(Name = "Show Labels", Description = "Show/hide right-edge price labels for each level.",
            Order = 1, GroupName = "09. Label Appearance")]
        public bool ShowLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Label Font Family", Order = 2, GroupName = "09. Label Appearance")]
        public string LabelFontFamily { get; set; }

        [NinjaScriptProperty]
        [Range(6, 24)]
        [Display(Name = "Label Font Size", Order = 3, GroupName = "09. Label Appearance")]
        public int LabelFontSize { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Label Opacity", Order = 4, GroupName = "09. Label Appearance")]
        public int LabelOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Price Value", Description = "Include the formatted price in the label.",
            Order = 5, GroupName = "09. Label Appearance")]
        public bool ShowPriceValue { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Level Name", Description = "Include the level name (e.g., 'Structural Pivot') in the label.",
            Order = 6, GroupName = "09. Label Appearance")]
        public bool ShowLevelName { get; set; }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════
        #region Properties — 10. Dashboard

        [NinjaScriptProperty]
        [Display(Name = "Show Dashboard", Order = 1, GroupName = "10. Dashboard")]
        public bool ShowDashboard { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Dashboard Mode", Description = "Minimal / Standard / Full / Custom content.",
            Order = 2, GroupName = "10. Dashboard")]
        public DashboardMode SelectedDashboardMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Dashboard Position", Order = 3, GroupName = "10. Dashboard")]
        public TextPosition DashboardPosition { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Dashboard Font Family", Order = 4, GroupName = "10. Dashboard")]
        public string DashboardFontFamily { get; set; }

        [NinjaScriptProperty]
        [Range(6, 24)]
        [Display(Name = "Dashboard Font Size", Order = 5, GroupName = "10. Dashboard")]
        public int DashboardFontSize { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Dashboard Text Color", Order = 6, GroupName = "10. Dashboard")]
        public Brush DashboardTextColor { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Dashboard Background Color", Order = 7, GroupName = "10. Dashboard")]
        public Brush DashboardBackgroundColor { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Dashboard Border Color", Order = 8, GroupName = "10. Dashboard")]
        public Brush DashboardBorderColor { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Dashboard Opacity", Order = 9, GroupName = "10. Dashboard")]
        public int DashboardOpacity { get; set; }

        // Custom mode row toggles (visible when DashboardMode == Custom)
        [NinjaScriptProperty]
        [Display(Name = "Custom: Show Anchor Row", Order = 10, GroupName = "10. Dashboard")]
        public bool ShowCustomAnchorRow { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Custom: Show Nearest Row", Order = 11, GroupName = "10. Dashboard")]
        public bool ShowCustomNearestRow { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Custom: Show VWAP Row", Order = 12, GroupName = "10. Dashboard")]
        public bool ShowCustomVWAPRow { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Custom: Show Day Type Row", Order = 13, GroupName = "10. Dashboard")]
        public bool ShowCustomDayTypeRow { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Custom: Show Delta Row", Order = 14, GroupName = "10. Dashboard")]
        public bool ShowCustomDeltaRow { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Custom: Show DOM Row", Order = 15, GroupName = "10. Dashboard")]
        public bool ShowCustomDOMRow { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Custom: Show All Levels Row", Order = 16, GroupName = "10. Dashboard")]
        public bool ShowCustomAllLevelsRow { get; set; }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════
        #region Properties — 11. Alerts

        [NinjaScriptProperty]
        [Display(Name = "Enable Alerts", Order = 1, GroupName = "11. Alerts")]
        public bool EnableAlerts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Sound File Name", Description = "WAV file in NinjaTrader 'sounds' folder (e.g., Alert1.wav). '.wav' appended automatically if missing.",
            Order = 2, GroupName = "11. Alerts")]
        public string SoundFileName { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3)]
        [Display(Name = "Min Score to Alert", Description = "Minimum confirmation score (1–3) to trigger an alert.",
            Order = 3, GroupName = "11. Alerts")]
        public int MinScoreToAlert { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Alert Cooldown (Bars)", Description = "Bars to wait before re-alerting the same level.",
            Order = 4, GroupName = "11. Alerts")]
        public int AlertCooldownBars { get; set; }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════
        #region Properties — 12. Session Filters

        [NinjaScriptProperty]
        [Display(Name = "RTH Only", Description = "Only fire signals during Regular Trading Hours (9:30 AM–4:00 PM ET).",
            Order = 1, GroupName = "12. Session Filters")]
        public bool RTHOnly { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Day Type Filter", Description = "Filter signals by day type (Range/Trend). Requires OrderFlow+ for classification.",
            Order = 2, GroupName = "12. Session Filters")]
        public DayTypeFilter SelectedDayTypeFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require Above VWAP for Long", Description = "Only fire bullish signals when price is above the session VWAP.",
            Order = 3, GroupName = "12. Session Filters")]
        public bool RequireAboveVWAPForLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require Below VWAP for Short", Description = "Only fire bearish signals when price is below the session VWAP.",
            Order = 4, GroupName = "12. Session Filters")]
        public bool RequireBelowVWAPForShort { get; set; }

        #endregion
    }
}
