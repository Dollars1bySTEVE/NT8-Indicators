#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// MACrossFilter - Standalone MA crossover indicator with accuracy filters
// Features:
//   1. Consecutive Closes Confirmation
//   2. Minimum Separation Filter
//   3. Volume Confirmation
//   4. Trend Filter
//   5. ATR-Based Arrow Offset

namespace NinjaTrader.NinjaScript.Indicators
{
    public enum MACrossFilterMAType { SMA, EMA }

    [Gui.CategoryOrder("Parameters", 1)]
    [Gui.CategoryOrder("Signals",    2)]
    [Gui.CategoryOrder("Alerts",     3)]
    public class MACrossFilter : Indicator
    {
        #region Members
        private Series<bool> _longSignal;
        private Series<bool> _shortSignal;
        #endregion

        #region OnStateChange
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = @"MA crossover indicator with accuracy filters: consecutive-close "
                                         + "confirmation, minimum separation, volume gate, trend filter, ATR offset.";
                Name                     = "MA Cross Filter";
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
                MAType                    = MACrossFilterMAType.SMA;
                MAPeriod                  = 9;
                ConsecutiveBars           = 1;
                MinSepTicks               = 0;
                UseVolFilter              = false;
                VolPeriod                 = 20;
                UseTrendFilter            = false;
                TrendPeriod               = 50;

                // Signals
                ArrowOffsetTicks  = 10;
                UseATROffset      = false;
                ATRMult           = 0.5;
                UseColors         = true;
                LongColor         = Brushes.LimeGreen;
                ShortColor        = Brushes.Red;
                DrawPrefix        = "MCF_";

                // Alerts
                AlertsOn        = false;
                AlertPath       = string.Empty;
                LongWav         = "LongEntry.wav";
                ShortWav        = "ShortEntry.wav";

                AddPlot(Brushes.White, "MALine");
                AddPlot(Brushes.Transparent, "CrossSignal");
            }
            else if (State == State.Configure)
            {
                IsSuspendedWhileInactive = !AlertsOn;
                if (string.IsNullOrEmpty(AlertPath))
                    AlertPath = DefaultAlertFilePath();
                _longSignal  = new Series<bool>(this);
                _shortSignal = new Series<bool>(this);
            }
        }
        #endregion

        #region DisplayName
        public override string DisplayName
        {
            get
            {
                if (State == State.SetDefaults) return DefaultName;
                return Name + "(" + MAType + "," + MAPeriod + ")";
            }
        }
        #endregion

        #region OnBarUpdate
        protected override void OnBarUpdate()
        {
            _longSignal[0]  = false;
            _shortSignal[0] = false;
            CrossSignal[0]  = 0;

            int minBars = Math.Max(MAPeriod + ConsecutiveBars,
                          Math.Max(UseVolFilter    ? VolPeriod    : 0,
                                   UseTrendFilter  ? TrendPeriod  : 0));
            if (CurrentBar < minBars) return;

            // Main MA
            MALine[0] = (MAType == MACrossFilterMAType.EMA)
                ? EMA(Close, MAPeriod)[0]
                : SMA(Close, MAPeriod)[0];

            // 1. Consecutive closes check
            bool allAbove = true, allBelow = true;
            for (int i = 0; i < ConsecutiveBars; i++)
            {
                double maI = (MAType == MACrossFilterMAType.EMA)
                    ? EMA(Close, MAPeriod)[i]
                    : SMA(Close, MAPeriod)[i];
                if (Close[i] <= maI) allAbove = false;
                if (Close[i] >= maI) allBelow = false;
            }

            double maPrev = (MAType == MACrossFilterMAType.EMA)
                ? EMA(Close, MAPeriod)[ConsecutiveBars]
                : SMA(Close, MAPeriod)[ConsecutiveBars];

            bool goLong  = allAbove && (Close[ConsecutiveBars] <= maPrev);
            bool goShort = allBelow && (Close[ConsecutiveBars] >= maPrev);
            if (!goLong && !goShort) { Cleanup(); return; }

            // 2. Min separation
            if (MinSepTicks > 0 && Math.Abs(Close[0] - MALine[0]) < MinSepTicks * TickSize)
            { Cleanup(); return; }

            // 3. Volume filter
            if (UseVolFilter && Volume[0] <= SMA(Volume, VolPeriod)[0])
            { Cleanup(); return; }

            // 4. Trend filter
            if (UseTrendFilter)
            {
                double tma = SMA(Close, TrendPeriod)[0];
                if (goLong  && Close[0] < tma) goLong  = false;
                if (goShort && Close[0] > tma) goShort = false;
            }
            if (!goLong && !goShort) { Cleanup(); return; }

            // 5. Offset
            double off = UseATROffset ? ATR(14)[0] * ATRMult : ArrowOffsetTicks * TickSize;

            string lt = DrawPrefix + "L" + CurrentBar;
            string st = DrawPrefix + "S" + CurrentBar;
            var    bt = Time[0];

            if (goLong)
            {
                RemoveDrawObject(st);
                _longSignal[0] = true;
                CrossSignal[0] = 1;
                Draw.ArrowUp(this, lt, true, bt, Low[0] - off, UseColors ? LongColor : Plots[0].Brush);
            }
            else if (goShort)
            {
                RemoveDrawObject(lt);
                _shortSignal[0] = true;
                CrossSignal[0]  = -1;
                Draw.ArrowDown(this, st, true, bt, High[0] + off, UseColors ? ShortColor : Plots[0].Brush);
            }
            else
            {
                Cleanup();
            }

            // Alerts
            if (AlertsOn && State == State.Realtime && IsFirstTickOfBar)
            {
                if (CrossSignal[1] > 0 && !string.IsNullOrWhiteSpace(LongWav))
                    Alert("LongAlert",  Priority.High, "Long Entry",
                        ResolveAlertFilePath(LongWav,  AlertPath), 10, Brushes.Black, LongColor);
                if (CrossSignal[1] < 0 && !string.IsNullOrWhiteSpace(ShortWav))
                    Alert("ShortAlert", Priority.High, "Short Entry",
                        ResolveAlertFilePath(ShortWav, AlertPath), 10, Brushes.Black, ShortColor);
            }
        }
        #endregion

        #region Private helpers
        private void Cleanup()
        {
            RemoveDrawObject(DrawPrefix + "L" + CurrentBar);
            RemoveDrawObject(DrawPrefix + "S" + CurrentBar);
        }
        #endregion

        #region Properties

        [NinjaScriptProperty]
        [Display(Name = "MA Type", Description = "SMA or EMA.", Order = 1, GroupName = "Parameters")]
        public MACrossFilterMAType MAType { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "MA Period", Order = 2, GroupName = "Parameters")]
        public int MAPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Consecutive Bars Required",
            Description = "Number of consecutive closes on the new MA side before a signal fires. 1 = standard single-bar cross.",
            Order = 3, GroupName = "Parameters")]
        public int ConsecutiveBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Min Separation Ticks",
            Description = "Minimum ticks between Close and MA at signal time. Filters grazing crosses. 0 = off.",
            Order = 4, GroupName = "Parameters")]
        public int MinSepTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Volume Filter",
            Description = "Only signal when volume exceeds its SMA.",
            Order = 5, GroupName = "Parameters")]
        public bool UseVolFilter { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Volume Period", Order = 6, GroupName = "Parameters")]
        public int VolPeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trend Filter",
            Description = "Longs only above trend SMA; shorts only below it.",
            Order = 7, GroupName = "Parameters")]
        public bool UseTrendFilter { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Trend Filter Period", Description = "Always SMA. Above = bullish; below = bearish.", Order = 8, GroupName = "Parameters")]
        public int TrendPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Arrow Offset (ticks)", Description = "Fixed tick offset for arrow placement. Used when Use ATR Offset is off.", Order = 1, GroupName = "Signals")]
        public int ArrowOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use ATR Offset", Description = "Arrow offset = ATR(14) x ATR Multiplier.", Order = 2, GroupName = "Signals")]
        public bool UseATROffset { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "ATR Multiplier", Order = 3, GroupName = "Signals")]
        public double ATRMult { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Signal Colors", Order = 4, GroupName = "Signals")]
        public bool UseColors { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Long Color", Order = 5, GroupName = "Signals")]
        public Brush LongColor { get; set; }

        [Browsable(false)]
        public string LongColorSerializable
        {
            get { return Serialize.BrushToString(LongColor); }
            set { LongColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Short Color", Order = 6, GroupName = "Signals")]
        public Brush ShortColor { get; set; }

        [Browsable(false)]
        public string ShortColorSerializable
        {
            get { return Serialize.BrushToString(ShortColor); }
            set { ShortColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Draw Prefix",
            Description = "Prefix for draw objects. Change if running multiple instances on the same chart.",
            Order = 7, GroupName = "Signals")]
        public string DrawPrefix { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Alerts", Description = "Audio alerts on confirmed signals (realtime only).", Order = 1, GroupName = "Alerts")]
        public bool AlertsOn { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert Sounds Path", Order = 2, GroupName = "Alerts")]
        public string AlertPath { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Long Alert WAV", Order = 3, GroupName = "Alerts")]
        public string LongWav { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Short Alert WAV", Order = 4, GroupName = "Alerts")]
        public string ShortWav { get; set; }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> MALine
        { get { return Values[0]; } }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> CrossSignal
        { get { return Values[1]; } }

        [Browsable(false)]
        [XmlIgnore]
        public Series<bool> LongSignal
        { get { return _longSignal; } }

        [Browsable(false)]
        [XmlIgnore]
        public Series<bool> ShortSignal
        { get { return _shortSignal; } }

        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private MACrossFilter[] cacheMACrossFilter;

        public MACrossFilter MACrossFilter(MACrossFilterMAType mAType, int mAPeriod,
            int consecutiveBars, int minSepTicks,
            bool useVolFilter, int volPeriod,
            bool useTrendFilter, int trendPeriod,
            int arrowOffsetTicks, bool useATROffset, double aTRMult,
            bool useColors, Brush longColor, Brush shortColor,
            string drawPrefix, bool alertsOn, string alertPath,
            string longWav, string shortWav)
        {
            return MACrossFilter(Input, mAType, mAPeriod, consecutiveBars, minSepTicks,
                useVolFilter, volPeriod, useTrendFilter, trendPeriod,
                arrowOffsetTicks, useATROffset, aTRMult,
                useColors, longColor, shortColor,
                drawPrefix, alertsOn, alertPath, longWav, shortWav);
        }

        public MACrossFilter MACrossFilter(ISeries<double> input, MACrossFilterMAType mAType, int mAPeriod,
            int consecutiveBars, int minSepTicks,
            bool useVolFilter, int volPeriod,
            bool useTrendFilter, int trendPeriod,
            int arrowOffsetTicks, bool useATROffset, double aTRMult,
            bool useColors, Brush longColor, Brush shortColor,
            string drawPrefix, bool alertsOn, string alertPath,
            string longWav, string shortWav)
        {
            if (cacheMACrossFilter != null)
                for (int i = 0; i < cacheMACrossFilter.Length; i++)
                {
                    var c = cacheMACrossFilter[i];
                    if (c != null
                        && c.MAType          == mAType
                        && c.MAPeriod        == mAPeriod
                        && c.ConsecutiveBars == consecutiveBars
                        && c.MinSepTicks     == minSepTicks
                        && c.UseVolFilter    == useVolFilter
                        && c.VolPeriod       == volPeriod
                        && c.UseTrendFilter  == useTrendFilter
                        && c.TrendPeriod     == trendPeriod
                        && c.ArrowOffsetTicks == arrowOffsetTicks
                        && c.UseATROffset    == useATROffset
                        && c.ATRMult         == aTRMult
                        && c.UseColors       == useColors
                        && c.LongColor       == longColor
                        && c.ShortColor      == shortColor
                        && c.DrawPrefix      == drawPrefix
                        && c.AlertsOn        == alertsOn
                        && c.AlertPath       == alertPath
                        && c.LongWav         == longWav
                        && c.ShortWav        == shortWav
                        && c.EqualsInput(input))
                        return cacheMACrossFilter[i];
                }

            return CacheIndicator<MACrossFilter>(new MACrossFilter()
            {
                MAType          = mAType,
                MAPeriod        = mAPeriod,
                ConsecutiveBars = consecutiveBars,
                MinSepTicks     = minSepTicks,
                UseVolFilter    = useVolFilter,
                VolPeriod       = volPeriod,
                UseTrendFilter  = useTrendFilter,
                TrendPeriod     = trendPeriod,
                ArrowOffsetTicks = arrowOffsetTicks,
                UseATROffset    = useATROffset,
                ATRMult         = aTRMult,
                UseColors       = useColors,
                LongColor       = longColor,
                ShortColor      = shortColor,
                DrawPrefix      = drawPrefix,
                AlertsOn        = alertsOn,
                AlertPath       = alertPath,
                LongWav         = longWav,
                ShortWav        = shortWav,
            }, input, ref cacheMACrossFilter);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.MACrossFilter MACrossFilter(Indicators.MACrossFilterMAType mAType, int mAPeriod,
            int consecutiveBars, int minSepTicks,
            bool useVolFilter, int volPeriod,
            bool useTrendFilter, int trendPeriod,
            int arrowOffsetTicks, bool useATROffset, double aTRMult,
            bool useColors, Brush longColor, Brush shortColor,
            string drawPrefix, bool alertsOn, string alertPath,
            string longWav, string shortWav)
        {
            return indicator.MACrossFilter(Input, mAType, mAPeriod, consecutiveBars, minSepTicks,
                useVolFilter, volPeriod, useTrendFilter, trendPeriod,
                arrowOffsetTicks, useATROffset, aTRMult,
                useColors, longColor, shortColor,
                drawPrefix, alertsOn, alertPath, longWav, shortWav);
        }

        public Indicators.MACrossFilter MACrossFilter(ISeries<double> input, Indicators.MACrossFilterMAType mAType, int mAPeriod,
            int consecutiveBars, int minSepTicks,
            bool useVolFilter, int volPeriod,
            bool useTrendFilter, int trendPeriod,
            int arrowOffsetTicks, bool useATROffset, double aTRMult,
            bool useColors, Brush longColor, Brush shortColor,
            string drawPrefix, bool alertsOn, string alertPath,
            string longWav, string shortWav)
        {
            return indicator.MACrossFilter(input, mAType, mAPeriod, consecutiveBars, minSepTicks,
                useVolFilter, volPeriod, useTrendFilter, trendPeriod,
                arrowOffsetTicks, useATROffset, aTRMult,
                useColors, longColor, shortColor,
                drawPrefix, alertsOn, alertPath, longWav, shortWav);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.MACrossFilter MACrossFilter(Indicators.MACrossFilterMAType mAType, int mAPeriod,
            int consecutiveBars, int minSepTicks,
            bool useVolFilter, int volPeriod,
            bool useTrendFilter, int trendPeriod,
            int arrowOffsetTicks, bool useATROffset, double aTRMult,
            bool useColors, Brush longColor, Brush shortColor,
            string drawPrefix, bool alertsOn, string alertPath,
            string longWav, string shortWav)
        {
            return indicator.MACrossFilter(Input, mAType, mAPeriod, consecutiveBars, minSepTicks,
                useVolFilter, volPeriod, useTrendFilter, trendPeriod,
                arrowOffsetTicks, useATROffset, aTRMult,
                useColors, longColor, shortColor,
                drawPrefix, alertsOn, alertPath, longWav, shortWav);
        }

        public Indicators.MACrossFilter MACrossFilter(ISeries<double> input, Indicators.MACrossFilterMAType mAType, int mAPeriod,
            int consecutiveBars, int minSepTicks,
            bool useVolFilter, int volPeriod,
            bool useTrendFilter, int trendPeriod,
            int arrowOffsetTicks, bool useATROffset, double aTRMult,
            bool useColors, Brush longColor, Brush shortColor,
            string drawPrefix, bool alertsOn, string alertPath,
            string longWav, string shortWav)
        {
            return indicator.MACrossFilter(input, mAType, mAPeriod, consecutiveBars, minSepTicks,
                useVolFilter, volPeriod, useTrendFilter, trendPeriod,
                arrowOffsetTicks, useATROffset, aTRMult,
                useColors, longColor, shortColor,
                drawPrefix, alertsOn, alertPath, longWav, shortWav);
        }
    }
}

#endregion
