#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// MA Cross Filter — MA crossover indicator with accuracy filters and RedTail volume gating.
// Install: copy to  Documents\NinjaTrader 8\bin\Custom\Indicators\
//          then     Tools > Edit NinjaScript > Indicator > F5
//
// RedTail buy/sell values are OHLC-based estimates, not bid/ask-classified trade volume.

// NOTE: enum is declared at file scope (outside any namespace) so NT8's
// auto-generated cache code compiles cleanly. Do not rename this enum.
public enum MACrossFilterMAType { SMA, EMA }

namespace NinjaTrader.NinjaScript.Indicators
{
    [Gui.CategoryOrder("Parameters", 1)]
    [Gui.CategoryOrder("RedTail Volume Gate", 2)]
    [Gui.CategoryOrder("Signals",    3)]
    [Gui.CategoryOrder("Alerts",     4)]
    public class MACrossFilter : Indicator
    {
        #region Members
        private Series<bool> _longSig;
        private Series<bool> _shortSig;
        #endregion

        #region OnStateChange
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = @"MA crossover with accuracy filters: consecutive closes, "
                                         + "min separation, RedTail volume gate, trend filter, ATR offset.";
                Name                     = "RedTail MA Cross";
                Calculate                = Calculate.OnPriceChange;
                IsOverlay                = true;
                DisplayInDataBox         = true;
                DrawOnPricePanel         = true;
                DrawHorizontalGridLines  = true;
                DrawVerticalGridLines    = true;
                PaintPriceMarkers        = true;
                ScaleJustification       = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                IsSuspendedWhileInactive = true;

                MAType          = MACrossFilterMAType.EMA;
                MAPeriod        = 9;
                ConsecutiveBars = 2;
                MinSepTicks     = 8;
                UseVolFilter    = false;
                VolPeriod       = 20;
                UseTrendFilter  = false;
                TrendPeriod     = 50;

                UseRedTailGate     = true;
                MinBarVolPercent   = 100;
                RequireBuyerWinner = true;

                ArrowTicks   = 10;
                UseATROffset = true;
                ATRMult      = 0.5;
                UseColors    = true;
                LongColor    = Brushes.LimeGreen;
                ShortColor   = Brushes.Red;
                Prefix       = "MCF_";

                // Alert path is populated in Configure once NinjaScriptBase is live.
                AlertsOn  = false;
                AlertPath = string.Empty;
                LongWav   = "LongEntry.wav";
                ShortWav  = "ShortEntry.wav";

                AddPlot(Brushes.White,       "MALine");
                AddPlot(Brushes.Transparent, "Signal");
            }
            else if (State == State.Configure)
            {
                IsSuspendedWhileInactive = !AlertsOn;
                // Populate default alert sound folder now that NinjaScriptBase is live.
                if (string.IsNullOrWhiteSpace(AlertPath))
                    AlertPath = System.IO.Path.Combine(NinjaTrader.Core.Globals.InstallDir, "sounds");
                _longSig  = new Series<bool>(this);
                _shortSig = new Series<bool>(this);
            }
        }
        #endregion

        #region DisplayName
        public override string DisplayName
        {
            get
            {
                if (State == State.SetDefaults) return DefaultName;
                return Name + "(" + MAType + "," + MAPeriod
                    + (UseRedTailGate ? ",RTV Gate" : "") + ")";
            }
        }
        #endregion

        #region OnBarUpdate
        protected override void OnBarUpdate()
        {
            _longSig[0]  = false;
            _shortSig[0] = false;
            Signal[0]    = 0;

            int need = Math.Max(MAPeriod + ConsecutiveBars,
                       Math.Max(UseVolFilter ? VolPeriod : 0,
                       Math.Max(UseTrendFilter ? TrendPeriod : 0,
                       Math.Max(UseRedTailGate ? 30 : 0, UseATROffset ? 14 : 0))));
            if (CurrentBar < need) return;

            // Main MA
            MALine[0] = (MAType == MACrossFilterMAType.EMA)
                ? EMA(Close, MAPeriod)[0]
                : SMA(Close, MAPeriod)[0];

            // 1. Consecutive closes
            bool above = true, below = true;
            for (int i = 0; i < ConsecutiveBars; i++)
            {
                double m = (MAType == MACrossFilterMAType.EMA)
                    ? EMA(Close, MAPeriod)[i]
                    : SMA(Close, MAPeriod)[i];
                if (Close[i] <= m) above = false;
                if (Close[i] >= m) below = false;
            }
            double prev = (MAType == MACrossFilterMAType.EMA)
                ? EMA(Close, MAPeriod)[ConsecutiveBars]
                : SMA(Close, MAPeriod)[ConsecutiveBars];

            bool gl = above && (Close[ConsecutiveBars] <= prev);
            bool gs = below && (Close[ConsecutiveBars] >= prev);
            if (!gl && !gs) { Clean(); return; }

            // 2. Min separation
            if (MinSepTicks > 0 && Math.Abs(Close[0] - MALine[0]) < MinSepTicks * TickSize)
            { Clean(); return; }

            // 3. Volume
            if (UseVolFilter && Volume[0] <= SMA(Volume, VolPeriod)[0])
            { Clean(); return; }

            // 4. Trend
            if (UseTrendFilter)
            {
                double t = SMA(Close, TrendPeriod)[0];
                if (gl && Close[0] < t) gl = false;
                if (gs && Close[0] > t) gs = false;
            }
            if (!gl && !gs) { Clean(); return; }

            // RedTail volume gate
            if (UseRedTailGate)
            {
                double sum30 = 0;
                for (int i = 1; i <= 30; i++) sum30 += Volume[i];
                double avg30Bar = sum30 / 30.0;

                if (MinBarVolPercent > 0
                    && (avg30Bar <= 0 || (Volume[0] / avg30Bar) * 100.0 < MinBarVolPercent))
                {
                    gl = false;
                    gs = false;
                }

                if (RequireBuyerWinner)
                {
                    double range = High[0] - Low[0];
                    double buyVol = range > 0
                        ? Volume[0] * (Close[0] - Low[0]) / range
                        : Volume[0] * 0.5;
                    double sellVol = range > 0
                        ? Volume[0] * (High[0] - Close[0]) / range
                        : Volume[0] * 0.5;

                    if (gl && buyVol <= sellVol) gl = false;
                    if (gs && sellVol <= buyVol) gs = false;
                }
            }
            if (!gl && !gs) { Clean(); return; }

            // Arrow offset
            double off = UseATROffset ? ATR(14)[0] * ATRMult : ArrowTicks * TickSize;

            string lt = Prefix + "L" + CurrentBar;
            string st = Prefix + "S" + CurrentBar;

            if (gl)
            {
                RemoveDrawObject(st);
                _longSig[0] = true;
                Signal[0]   = 1;
                Draw.ArrowUp(this, lt, true, Time[0], Low[0] - off,
                    UseColors ? LongColor : Plots[0].Brush);
            }
            else if (gs)
            {
                RemoveDrawObject(lt);
                _shortSig[0] = true;
                Signal[0]    = -1;
                Draw.ArrowDown(this, st, true, Time[0], High[0] + off,
                    UseColors ? ShortColor : Plots[0].Brush);
            }
            else
            {
                Clean();
            }

            // Alerts (realtime only, fires on confirmed previous bar)
            if (AlertsOn && State == State.Realtime && IsFirstTickOfBar)
            {
                if (Signal[1] > 0 && !string.IsNullOrWhiteSpace(LongWav))
                    Alert("MCF_Long",  Priority.High, "Long Entry",
                        ResolveAlertFilePath(LongWav,  AlertPath), 10, Brushes.Black, LongColor);
                if (Signal[1] < 0 && !string.IsNullOrWhiteSpace(ShortWav))
                    Alert("MCF_Short", Priority.High, "Short Entry",
                        ResolveAlertFilePath(ShortWav, AlertPath), 10, Brushes.Black, ShortColor);
            }
        }
        #endregion

        #region Private helpers
        private void Clean()
        {
            RemoveDrawObject(Prefix + "L" + CurrentBar);
            RemoveDrawObject(Prefix + "S" + CurrentBar);
        }

        private string ResolveAlertFilePath(string wav, string basePath)
        {
            if (string.IsNullOrWhiteSpace(wav)) return string.Empty;
            return System.IO.Path.IsPathRooted(wav)
                ? wav
                : System.IO.Path.Combine(basePath ?? string.Empty, wav);
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
        [Display(Name = "Consecutive Bars",
            Description = "Consecutive closes on the new MA side before a signal fires. 1 = standard cross.",
            Order = 3, GroupName = "Parameters")]
        public int ConsecutiveBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Min Separation Ticks",
            Description = "Min ticks between Close and MA. Filters grazing crosses. 0 = off.",
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
        [Display(Name = "Trend Filter Period",
            Description = "Always SMA. Above = bullish; below = bearish.",
            Order = 8, GroupName = "Parameters")]
        public int TrendPeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use RedTail Volume Gate",
            Description = "Enforces estimated buy/sell dominance and bar-volume rules.",
            Order = 1, GroupName = "RedTail Volume Gate")]
        public bool UseRedTailGate { get; set; }

        [NinjaScriptProperty]
        [Range(0, 500)]
        [Display(Name = "Min Bar Vol % of 30-Bar Avg",
            Description = "Minimum current-bar volume as a percentage of the prior 30-bar average.",
            Order = 2, GroupName = "RedTail Volume Gate")]
        public int MinBarVolPercent { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require Buy/Sell Dominance",
            Description = "Longs require estimated buy volume to exceed sell volume; shorts require the reverse.",
            Order = 3, GroupName = "RedTail Volume Gate")]
        public bool RequireBuyerWinner { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Arrow Offset Ticks",
            Description = "Fixed tick offset for arrow placement. Used when ATR offset is off.",
            Order = 1, GroupName = "Signals")]
        public int ArrowTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use ATR Offset",
            Description = "Arrow offset = ATR(14) x ATR Multiplier.",
            Order = 2, GroupName = "Signals")]
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
            Description = "Prefix for draw objects. Change when using multiple instances on the same chart.",
            Order = 7, GroupName = "Signals")]
        public string Prefix { get; set; }

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
        public Series<double> Signal
        { get { return Values[1]; } }

        [Browsable(false)]
        [XmlIgnore]
        public Series<bool> LongSignal
        { get { return _longSig; } }

        [Browsable(false)]
        [XmlIgnore]
        public Series<bool> ShortSignal
        { get { return _shortSig; } }

        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private MACrossFilter[] cacheMACrossFilter;
		public MACrossFilter MACrossFilter(MACrossFilterMAType mAType, int mAPeriod, int consecutiveBars, int minSepTicks, bool useVolFilter, int volPeriod, bool useTrendFilter, int trendPeriod, bool useRedTailGate, int minBarVolPercent, bool requireBuyerWinner, int arrowTicks, bool useATROffset, double aTRMult, bool useColors, Brush longColor, Brush shortColor, string prefix, bool alertsOn, string alertPath, string longWav, string shortWav)
		{
			return MACrossFilter(Input, mAType, mAPeriod, consecutiveBars, minSepTicks, useVolFilter, volPeriod, useTrendFilter, trendPeriod, useRedTailGate, minBarVolPercent, requireBuyerWinner, arrowTicks, useATROffset, aTRMult, useColors, longColor, shortColor, prefix, alertsOn, alertPath, longWav, shortWav);
		}

		public MACrossFilter MACrossFilter(ISeries<double> input, MACrossFilterMAType mAType, int mAPeriod, int consecutiveBars, int minSepTicks, bool useVolFilter, int volPeriod, bool useTrendFilter, int trendPeriod, bool useRedTailGate, int minBarVolPercent, bool requireBuyerWinner, int arrowTicks, bool useATROffset, double aTRMult, bool useColors, Brush longColor, Brush shortColor, string prefix, bool alertsOn, string alertPath, string longWav, string shortWav)
		{
			if (cacheMACrossFilter != null)
				for (int idx = 0; idx < cacheMACrossFilter.Length; idx++)
					if (cacheMACrossFilter[idx] != null && cacheMACrossFilter[idx].MAType == mAType && cacheMACrossFilter[idx].MAPeriod == mAPeriod && cacheMACrossFilter[idx].ConsecutiveBars == consecutiveBars && cacheMACrossFilter[idx].MinSepTicks == minSepTicks && cacheMACrossFilter[idx].UseVolFilter == useVolFilter && cacheMACrossFilter[idx].VolPeriod == volPeriod && cacheMACrossFilter[idx].UseTrendFilter == useTrendFilter && cacheMACrossFilter[idx].TrendPeriod == trendPeriod && cacheMACrossFilter[idx].UseRedTailGate == useRedTailGate && cacheMACrossFilter[idx].MinBarVolPercent == minBarVolPercent && cacheMACrossFilter[idx].RequireBuyerWinner == requireBuyerWinner && cacheMACrossFilter[idx].ArrowTicks == arrowTicks && cacheMACrossFilter[idx].UseATROffset == useATROffset && cacheMACrossFilter[idx].ATRMult == aTRMult && cacheMACrossFilter[idx].UseColors == useColors && cacheMACrossFilter[idx].LongColor == longColor && cacheMACrossFilter[idx].ShortColor == shortColor && cacheMACrossFilter[idx].Prefix == prefix && cacheMACrossFilter[idx].AlertsOn == alertsOn && cacheMACrossFilter[idx].AlertPath == alertPath && cacheMACrossFilter[idx].LongWav == longWav && cacheMACrossFilter[idx].ShortWav == shortWav && cacheMACrossFilter[idx].EqualsInput(input))
						return cacheMACrossFilter[idx];
			return CacheIndicator<MACrossFilter>(new MACrossFilter(){ MAType = mAType, MAPeriod = mAPeriod, ConsecutiveBars = consecutiveBars, MinSepTicks = minSepTicks, UseVolFilter = useVolFilter, VolPeriod = volPeriod, UseTrendFilter = useTrendFilter, TrendPeriod = trendPeriod, UseRedTailGate = useRedTailGate, MinBarVolPercent = minBarVolPercent, RequireBuyerWinner = requireBuyerWinner, ArrowTicks = arrowTicks, UseATROffset = useATROffset, ATRMult = aTRMult, UseColors = useColors, LongColor = longColor, ShortColor = shortColor, Prefix = prefix, AlertsOn = alertsOn, AlertPath = alertPath, LongWav = longWav, ShortWav = shortWav }, input, ref cacheMACrossFilter);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.MACrossFilter MACrossFilter(MACrossFilterMAType mAType, int mAPeriod, int consecutiveBars, int minSepTicks, bool useVolFilter, int volPeriod, bool useTrendFilter, int trendPeriod, bool useRedTailGate, int minBarVolPercent, bool requireBuyerWinner, int arrowTicks, bool useATROffset, double aTRMult, bool useColors, Brush longColor, Brush shortColor, string prefix, bool alertsOn, string alertPath, string longWav, string shortWav)
		{
			return indicator.MACrossFilter(Input, mAType, mAPeriod, consecutiveBars, minSepTicks, useVolFilter, volPeriod, useTrendFilter, trendPeriod, useRedTailGate, minBarVolPercent, requireBuyerWinner, arrowTicks, useATROffset, aTRMult, useColors, longColor, shortColor, prefix, alertsOn, alertPath, longWav, shortWav);
		}

		public Indicators.MACrossFilter MACrossFilter(ISeries<double> input , MACrossFilterMAType mAType, int mAPeriod, int consecutiveBars, int minSepTicks, bool useVolFilter, int volPeriod, bool useTrendFilter, int trendPeriod, bool useRedTailGate, int minBarVolPercent, bool requireBuyerWinner, int arrowTicks, bool useATROffset, double aTRMult, bool useColors, Brush longColor, Brush shortColor, string prefix, bool alertsOn, string alertPath, string longWav, string shortWav)
		{
			return indicator.MACrossFilter(input, mAType, mAPeriod, consecutiveBars, minSepTicks, useVolFilter, volPeriod, useTrendFilter, trendPeriod, useRedTailGate, minBarVolPercent, requireBuyerWinner, arrowTicks, useATROffset, aTRMult, useColors, longColor, shortColor, prefix, alertsOn, alertPath, longWav, shortWav);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.MACrossFilter MACrossFilter(MACrossFilterMAType mAType, int mAPeriod, int consecutiveBars, int minSepTicks, bool useVolFilter, int volPeriod, bool useTrendFilter, int trendPeriod, bool useRedTailGate, int minBarVolPercent, bool requireBuyerWinner, int arrowTicks, bool useATROffset, double aTRMult, bool useColors, Brush longColor, Brush shortColor, string prefix, bool alertsOn, string alertPath, string longWav, string shortWav)
		{
			return indicator.MACrossFilter(Input, mAType, mAPeriod, consecutiveBars, minSepTicks, useVolFilter, volPeriod, useTrendFilter, trendPeriod, useRedTailGate, minBarVolPercent, requireBuyerWinner, arrowTicks, useATROffset, aTRMult, useColors, longColor, shortColor, prefix, alertsOn, alertPath, longWav, shortWav);
		}

		public Indicators.MACrossFilter MACrossFilter(ISeries<double> input , MACrossFilterMAType mAType, int mAPeriod, int consecutiveBars, int minSepTicks, bool useVolFilter, int volPeriod, bool useTrendFilter, int trendPeriod, bool useRedTailGate, int minBarVolPercent, bool requireBuyerWinner, int arrowTicks, bool useATROffset, double aTRMult, bool useColors, Brush longColor, Brush shortColor, string prefix, bool alertsOn, string alertPath, string longWav, string shortWav)
		{
			return indicator.MACrossFilter(Input, mAType, mAPeriod, consecutiveBars, minSepTicks, useVolFilter, volPeriod, useTrendFilter, trendPeriod, useRedTailGate, minBarVolPercent, requireBuyerWinner, arrowTicks, useATROffset, aTRMult, useColors, longColor, shortColor, prefix, alertsOn, alertPath, longWav, shortWav);
		}
	}
}

#endregion
