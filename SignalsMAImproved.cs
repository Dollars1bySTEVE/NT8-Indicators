#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// ╔══════════════════════════════════════════════════════════════════════════════╗
// ║  SignalsMAImproved  v1.0                                                     ║
// ║  Enhanced accuracy version of SignalsMA                                      ║
// ║                                                                              ║
// ║  Improvements:                                                               ║
// ║  1. Consecutive Closes Confirmation  (ConsecutiveBarsRequired)               ║
// ║  2. Minimum Separation Filter        (MinSeparationTicks)                    ║
// ║  3. Volume Confirmation              (RequireVolumeConfirmation/VolumePeriod)║
// ║  4. Trend Filter                     (EnableTrendFilter/TrendFilterPeriod)   ║
// ║  5. ATR-Based Arrow Offset           (UseATROffset/ATROffsetMultiplier)      ║
// ║  6. Auto-unique SignalPrefix default to avoid draw-object collisions         ║
// ╚══════════════════════════════════════════════════════════════════════════════╝

namespace NinjaTrader.NinjaScript.Indicators
{
    // Enum declared at namespace scope so the generated code region can reference it
    // without ambiguity across partial class boundaries.
    public enum SignalsMAImprovedMAType { SMA, EMA }

    [Gui.CategoryOrder("Parameters", 1)]
    [Gui.CategoryOrder("Signals",    2)]
    [Gui.CategoryOrder("Alerts",     3)]
    public class SignalsMAImproved : Indicator
    {
        #region Members
        private Series<bool> _longEntrySignal;
        private Series<bool> _shortEntrySignal;
        #endregion

        #region OnStateChange
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = @"Moving Average crossover indicator with improved accuracy filters: "
                                         + "consecutive-close confirmation, minimum separation, volume gate, "
                                         + "trend filter, and ATR-based arrow offset.";
                Name                     = "Signals MA Improved";
                Calculate                = Calculate.OnPriceChange;
                IsOverlay                = true;
                DisplayInDataBox         = true;
                DrawOnPricePanel         = true;
                DrawHorizontalGridLines  = true;
                DrawVerticalGridLines    = true;
                PaintPriceMarkers        = true;
                ScaleJustification       = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                IsSuspendedWhileInactive = true;

                // Parameters
                MAType                    = SignalsMAImprovedMAType.SMA;
                Period                    = 9;
                ConsecutiveBarsRequired   = 1;
                MinSeparationTicks        = 0;
                RequireVolumeConfirmation = false;
                VolumePeriod              = 20;
                EnableTrendFilter         = false;
                TrendFilterPeriod         = 50;

                // Signals
                SignalOffset        = 10;
                UseATROffset        = false;
                ATROffsetMultiplier = 0.5;
                UseSignalColors     = true;
                LongSignalBrush     = Brushes.LimeGreen;
                ShortSignalBrush    = Brushes.Red;
                SignalPrefix        = "SignalsMAImp_";

                // Alerts — DefaultAlertFilePath() is only callable after State.Configure,
                // so we initialise to empty and let NT8 resolve at runtime.
                EnableAlerts    = false;
                AlertSoundsPath = string.Empty;
                LongEntryAlert  = "LongEntry.wav";
                ShortEntryAlert = "ShortEntry.wav";

                AddPlot(Brushes.White, "MovingAverage");
                AddPlot(Brushes.Transparent, "Signals");
            }
            else if (State == State.Configure)
            {
                IsSuspendedWhileInactive = !EnableAlerts;

                // Populate default alert path now that we are inside NinjaScriptBase context.
                if (string.IsNullOrEmpty(AlertSoundsPath))
                    AlertSoundsPath = DefaultAlertFilePath();

                _longEntrySignal  = new Series<bool>(this);
                _shortEntrySignal = new Series<bool>(this);
            }
        }
        #endregion

        #region DisplayName
        public override string DisplayName
        {
            get
            {
                if (State == State.SetDefaults)
                    return DefaultName;
                return Name + "(" + MAType + "," + Period + ")";
            }
        }
        #endregion

        #region OnBarUpdate
        protected override void OnBarUpdate()
        {
            _longEntrySignal[0]  = false;
            _shortEntrySignal[0] = false;
            Signals[0]           = 0;

            // Enough bars for all active features
            int minBars = Math.Max(Period + ConsecutiveBarsRequired,
                          Math.Max(RequireVolumeConfirmation ? VolumePeriod      : 0,
                                   EnableTrendFilter         ? TrendFilterPeriod : 0));
            if (CurrentBar < minBars)
                return;

            // ── Main MA ───────────────────────────────────────────────────────
            MovingAverage[0] = (MAType == SignalsMAImprovedMAType.EMA)
                ? EMA(Close, Period)[0]
                : SMA(Close, Period)[0];

            // ── 1. Consecutive Closes Confirmation ────────────────────────────
            bool allAbove = true;
            bool allBelow = true;

            for (int i = 0; i < ConsecutiveBarsRequired; i++)
            {
                double maI = (MAType == SignalsMAImprovedMAType.EMA)
                    ? EMA(Close, Period)[i]
                    : SMA(Close, Period)[i];

                if (Close[i] <= maI) allAbove = false;
                if (Close[i] >= maI) allBelow = false;
            }

            double maPrev = (MAType == SignalsMAImprovedMAType.EMA)
                ? EMA(Close, Period)[ConsecutiveBarsRequired]
                : SMA(Close, Period)[ConsecutiveBarsRequired];

            bool longCross  = allAbove && (Close[ConsecutiveBarsRequired] <= maPrev);
            bool shortCross = allBelow && (Close[ConsecutiveBarsRequired] >= maPrev);

            if (!longCross && !shortCross) { CleanArrows(); return; }

            // ── 2. Minimum Separation Filter ──────────────────────────────────
            if (MinSeparationTicks > 0)
            {
                if (Math.Abs(Close[0] - MovingAverage[0]) < MinSeparationTicks * TickSize)
                { CleanArrows(); return; }
            }

            // ── 3. Volume Confirmation ────────────────────────────────────────
            if (RequireVolumeConfirmation)
            {
                if (Volume[0] <= SMA(Volume, VolumePeriod)[0])
                { CleanArrows(); return; }
            }

            // ── 4. Trend Filter ───────────────────────────────────────────────
            if (EnableTrendFilter)
            {
                double trendMA = SMA(Close, TrendFilterPeriod)[0];
                if (longCross  && Close[0] < trendMA) longCross  = false;
                if (shortCross && Close[0] > trendMA) shortCross = false;
            }

            if (!longCross && !shortCross) { CleanArrows(); return; }

            // ── 5. Arrow offset ───────────────────────────────────────────────
            double offset = UseATROffset
                ? ATR(14)[0] * ATROffsetMultiplier
                : SignalOffset * TickSize;

            // ── Fire signals ──────────────────────────────────────────────────
            var    barTime = Time[0];
            string lTag    = string.Format("{0}LongEntry{1}",  SignalPrefix, CurrentBar);
            string sTag    = string.Format("{0}ShortEntry{1}", SignalPrefix, CurrentBar);

            if (longCross)
            {
                RemoveDrawObject(sTag);
                _longEntrySignal[0] = true;
                Signals[0]          = 1;
                Draw.ArrowUp(this, lTag, true, barTime,
                    Low[0] - offset,
                    UseSignalColors ? LongSignalBrush : Plots[0].Brush);
            }
            else if (shortCross)
            {
                RemoveDrawObject(lTag);
                _shortEntrySignal[0] = true;
                Signals[0]           = -1;
                Draw.ArrowDown(this, sTag, true, barTime,
                    High[0] + offset,
                    UseSignalColors ? ShortSignalBrush : Plots[0].Brush);
            }
            else
            {
                CleanArrows();
            }

            // ── Alerts ────────────────────────────────────────────────────────
            if (EnableAlerts && State == State.Realtime && IsFirstTickOfBar)
            {
                if (Signals[1] > 0 && !string.IsNullOrWhiteSpace(LongEntryAlert))
                    Alert("LongEntryAlert",  Priority.High, "Long Entry",
                        ResolveAlertFilePath(LongEntryAlert,  AlertSoundsPath),
                        10, Brushes.Black, LongSignalBrush);

                if (Signals[1] < 0 && !string.IsNullOrWhiteSpace(ShortEntryAlert))
                    Alert("ShortEntryAlert", Priority.High, "Short Entry",
                        ResolveAlertFilePath(ShortEntryAlert, AlertSoundsPath),
                        10, Brushes.Black, ShortSignalBrush);
            }
        }
        #endregion

        #region Private helpers
        private void CleanArrows()
        {
            RemoveDrawObject(string.Format("{0}LongEntry{1}",  SignalPrefix, CurrentBar));
            RemoveDrawObject(string.Format("{0}ShortEntry{1}", SignalPrefix, CurrentBar));
        }
        #endregion

        #region Properties

        // ── Parameters ────────────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "MA Type", Description = "SMA or EMA.", Order = 1, GroupName = "Parameters")]
        public SignalsMAImprovedMAType MAType
        { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Period", Description = "Period of the Moving Average.", Order = 2, GroupName = "Parameters")]
        public int Period
        { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Consecutive Bars Required",
            Description = "Consecutive bar closes on the new side of the MA required before a signal fires. 1 = original behavior.",
            Order = 3, GroupName = "Parameters")]
        public int ConsecutiveBarsRequired
        { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Min Separation Ticks",
            Description = "Minimum ticks between Close[0] and the MA. Filters grazing crosses. 0 = disabled.",
            Order = 4, GroupName = "Parameters")]
        public int MinSeparationTicks
        { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require Volume Confirmation",
            Description = "Only fire when current bar volume exceeds the volume SMA.",
            Order = 5, GroupName = "Parameters")]
        public bool RequireVolumeConfirmation
        { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Volume Period", Description = "SMA period for volume confirmation.", Order = 6, GroupName = "Parameters")]
        public int VolumePeriod
        { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Trend Filter",
            Description = "Longs only above trend SMA; shorts only below it.",
            Order = 7, GroupName = "Parameters")]
        public bool EnableTrendFilter
        { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Trend Filter Period",
            Description = "Period for the trend SMA (always SMA). Above = bullish; below = bearish.",
            Order = 8, GroupName = "Parameters")]
        public int TrendFilterPeriod
        { get; set; }

        // ── Signals ───────────────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Signal Offset (ticks)",
            Description = "Fixed tick offset for arrow placement. Used when Use ATR Offset is off.",
            Order = 1, GroupName = "Signals")]
        public int SignalOffset
        { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use ATR Offset",
            Description = "Arrow offset = ATR(14) x ATR Offset Multiplier instead of fixed ticks.",
            Order = 2, GroupName = "Signals")]
        public bool UseATROffset
        { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "ATR Offset Multiplier", Description = "Multiplier applied to ATR(14).", Order = 3, GroupName = "Signals")]
        public double ATROffsetMultiplier
        { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Signal Colors",
            Description = "Use Long/Short color settings. When off, uses the MA line color.",
            Order = 4, GroupName = "Signals")]
        public bool UseSignalColors
        { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Long Signal Color", Order = 5, GroupName = "Signals")]
        public Brush LongSignalBrush
        { get; set; }

        [Browsable(false)]
        public string LongSignalBrushSerializable
        {
            get { return Serialize.BrushToString(LongSignalBrush); }
            set { LongSignalBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Short Signal Color", Order = 6, GroupName = "Signals")]
        public Brush ShortSignalBrush
        { get; set; }

        [Browsable(false)]
        public string ShortSignalBrushSerializable
        {
            get { return Serialize.BrushToString(ShortSignalBrush); }
            set { ShortSignalBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Signal Prefix",
            Description = "Prefix for draw-object names. Change when using multiple instances on the same chart.",
            Order = 7, GroupName = "Signals")]
        public string SignalPrefix
        { get; set; }

        // ── Alerts ────────────────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Enable Alerts", Description = "Audio alerts on confirmed signals (realtime only).", Order = 1, GroupName = "Alerts")]
        public bool EnableAlerts
        { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert Sounds Path", Description = "Folder containing .wav alert files.", Order = 2, GroupName = "Alerts")]
        public string AlertSoundsPath
        { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Long Entry Alert", Description = "WAV file for confirmed LONG signals.", Order = 3, GroupName = "Alerts")]
        public string LongEntryAlert
        { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Short Entry Alert", Description = "WAV file for confirmed SHORT signals.", Order = 4, GroupName = "Alerts")]
        public string ShortEntryAlert
        { get; set; }

        // ── Output series ─────────────────────────────────────────────────────

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> MovingAverage
        { get { return Values[0]; } }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> Signals
        { get { return Values[1]; } }

        [Browsable(false)]
        [XmlIgnore]
        public Series<bool> LongEntrySignal
        { get { return _longEntrySignal; } }

        [Browsable(false)]
        [XmlIgnore]
        public Series<bool> ShortEntrySignal
        { get { return _shortEntrySignal; } }

        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        // Field renamed to cacheSignalsMAImp to avoid collision with any other partial class members.
        private SignalsMAImproved[] cacheSignalsMAImp;

        public SignalsMAImproved SignalsMAImproved(
            SignalsMAImprovedMAType mAType, int period,
            int consecutiveBarsRequired, int minSeparationTicks,
            bool requireVolumeConfirmation, int volumePeriod,
            bool enableTrendFilter, int trendFilterPeriod,
            int signalOffset, bool useATROffset, double aTROffsetMultiplier,
            bool useSignalColors, Brush longSignalBrush, Brush shortSignalBrush,
            string signalPrefix, bool enableAlerts, string alertSoundsPath,
            string longEntryAlert, string shortEntryAlert)
        {
            return SignalsMAImproved(Input,
                mAType, period, consecutiveBarsRequired, minSeparationTicks,
                requireVolumeConfirmation, volumePeriod,
                enableTrendFilter, trendFilterPeriod,
                signalOffset, useATROffset, aTROffsetMultiplier,
                useSignalColors, longSignalBrush, shortSignalBrush,
                signalPrefix, enableAlerts, alertSoundsPath,
                longEntryAlert, shortEntryAlert);
        }

        public SignalsMAImproved SignalsMAImproved(ISeries<double> input,
            SignalsMAImprovedMAType mAType, int period,
            int consecutiveBarsRequired, int minSeparationTicks,
            bool requireVolumeConfirmation, int volumePeriod,
            bool enableTrendFilter, int trendFilterPeriod,
            int signalOffset, bool useATROffset, double aTROffsetMultiplier,
            bool useSignalColors, Brush longSignalBrush, Brush shortSignalBrush,
            string signalPrefix, bool enableAlerts, string alertSoundsPath,
            string longEntryAlert, string shortEntryAlert)
        {
            if (cacheSignalsMAImp != null)
                for (int idx = 0; idx < cacheSignalsMAImp.Length; idx++)
                {
                    var c = cacheSignalsMAImp[idx];
                    if (c != null
                        && c.MAType                    == mAType
                        && c.Period                    == period
                        && c.ConsecutiveBarsRequired   == consecutiveBarsRequired
                        && c.MinSeparationTicks        == minSeparationTicks
                        && c.RequireVolumeConfirmation == requireVolumeConfirmation
                        && c.VolumePeriod              == volumePeriod
                        && c.EnableTrendFilter         == enableTrendFilter
                        && c.TrendFilterPeriod         == trendFilterPeriod
                        && c.SignalOffset              == signalOffset
                        && c.UseATROffset              == useATROffset
                        && c.ATROffsetMultiplier       == aTROffsetMultiplier
                        && c.UseSignalColors           == useSignalColors
                        && c.LongSignalBrush           == longSignalBrush
                        && c.ShortSignalBrush          == shortSignalBrush
                        && c.SignalPrefix              == signalPrefix
                        && c.EnableAlerts              == enableAlerts
                        && c.AlertSoundsPath           == alertSoundsPath
                        && c.LongEntryAlert            == longEntryAlert
                        && c.ShortEntryAlert           == shortEntryAlert
                        && c.EqualsInput(input))
                        return cacheSignalsMAImp[idx];
                }

            return CacheIndicator<SignalsMAImproved>(new SignalsMAImproved()
            {
                MAType                    = mAType,
                Period                    = period,
                ConsecutiveBarsRequired   = consecutiveBarsRequired,
                MinSeparationTicks        = minSeparationTicks,
                RequireVolumeConfirmation = requireVolumeConfirmation,
                VolumePeriod              = volumePeriod,
                EnableTrendFilter         = enableTrendFilter,
                TrendFilterPeriod         = trendFilterPeriod,
                SignalOffset              = signalOffset,
                UseATROffset              = useATROffset,
                ATROffsetMultiplier       = aTROffsetMultiplier,
                UseSignalColors           = useSignalColors,
                LongSignalBrush           = longSignalBrush,
                ShortSignalBrush          = shortSignalBrush,
                SignalPrefix              = signalPrefix,
                EnableAlerts              = enableAlerts,
                AlertSoundsPath           = alertSoundsPath,
                LongEntryAlert            = longEntryAlert,
                ShortEntryAlert           = shortEntryAlert,
            }, input, ref cacheSignalsMAImp);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.SignalsMAImproved SignalsMAImproved(
            Indicators.SignalsMAImprovedMAType mAType, int period,
            int consecutiveBarsRequired, int minSeparationTicks,
            bool requireVolumeConfirmation, int volumePeriod,
            bool enableTrendFilter, int trendFilterPeriod,
            int signalOffset, bool useATROffset, double aTROffsetMultiplier,
            bool useSignalColors, Brush longSignalBrush, Brush shortSignalBrush,
            string signalPrefix, bool enableAlerts, string alertSoundsPath,
            string longEntryAlert, string shortEntryAlert)
        {
            return indicator.SignalsMAImproved(Input,
                mAType, period, consecutiveBarsRequired, minSeparationTicks,
                requireVolumeConfirmation, volumePeriod,
                enableTrendFilter, trendFilterPeriod,
                signalOffset, useATROffset, aTROffsetMultiplier,
                useSignalColors, longSignalBrush, shortSignalBrush,
                signalPrefix, enableAlerts, alertSoundsPath,
                longEntryAlert, shortEntryAlert);
        }

        public Indicators.SignalsMAImproved SignalsMAImproved(ISeries<double> input,
            Indicators.SignalsMAImprovedMAType mAType, int period,
            int consecutiveBarsRequired, int minSeparationTicks,
            bool requireVolumeConfirmation, int volumePeriod,
            bool enableTrendFilter, int trendFilterPeriod,
            int signalOffset, bool useATROffset, double aTROffsetMultiplier,
            bool useSignalColors, Brush longSignalBrush, Brush shortSignalBrush,
            string signalPrefix, bool enableAlerts, string alertSoundsPath,
            string longEntryAlert, string shortEntryAlert)
        {
            return indicator.SignalsMAImproved(input,
                mAType, period, consecutiveBarsRequired, minSeparationTicks,
                requireVolumeConfirmation, volumePeriod,
                enableTrendFilter, trendFilterPeriod,
                signalOffset, useATROffset, aTROffsetMultiplier,
                useSignalColors, longSignalBrush, shortSignalBrush,
                signalPrefix, enableAlerts, alertSoundsPath,
                longEntryAlert, shortEntryAlert);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.SignalsMAImproved SignalsMAImproved(
            Indicators.SignalsMAImprovedMAType mAType, int period,
            int consecutiveBarsRequired, int minSeparationTicks,
            bool requireVolumeConfirmation, int volumePeriod,
            bool enableTrendFilter, int trendFilterPeriod,
            int signalOffset, bool useATROffset, double aTROffsetMultiplier,
            bool useSignalColors, Brush longSignalBrush, Brush shortSignalBrush,
            string signalPrefix, bool enableAlerts, string alertSoundsPath,
            string longEntryAlert, string shortEntryAlert)
        {
            return indicator.SignalsMAImproved(Input,
                mAType, period, consecutiveBarsRequired, minSeparationTicks,
                requireVolumeConfirmation, volumePeriod,
                enableTrendFilter, trendFilterPeriod,
                signalOffset, useATROffset, aTROffsetMultiplier,
                useSignalColors, longSignalBrush, shortSignalBrush,
                signalPrefix, enableAlerts, alertSoundsPath,
                longEntryAlert, shortEntryAlert);
        }

        public Indicators.SignalsMAImproved SignalsMAImproved(ISeries<double> input,
            Indicators.SignalsMAImprovedMAType mAType, int period,
            int consecutiveBarsRequired, int minSeparationTicks,
            bool requireVolumeConfirmation, int volumePeriod,
            bool enableTrendFilter, int trendFilterPeriod,
            int signalOffset, bool useATROffset, double aTROffsetMultiplier,
            bool useSignalColors, Brush longSignalBrush, Brush shortSignalBrush,
            string signalPrefix, bool enableAlerts, string alertSoundsPath,
            string longEntryAlert, string shortEntryAlert)
        {
            return indicator.SignalsMAImproved(input,
                mAType, period, consecutiveBarsRequired, minSeparationTicks,
                requireVolumeConfirmation, volumePeriod,
                enableTrendFilter, trendFilterPeriod,
                signalOffset, useATROffset, aTROffsetMultiplier,
                useSignalColors, longSignalBrush, shortSignalBrush,
                signalPrefix, enableAlerts, alertSoundsPath,
                longEntryAlert, shortEntryAlert);
        }
    }
}

#endregion
