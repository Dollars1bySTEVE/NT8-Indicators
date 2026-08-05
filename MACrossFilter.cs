#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// MA Cross Filter — standalone MA crossover indicator with accuracy filters
// Drop into Documents\NinjaTrader 8\bin\Custom\Indicators\ and compile with F5.
//
// Features:
//   1. Consecutive Closes Confirmation  (ConsecutiveBars)
//   2. Minimum Separation Filter        (MinSepTicks)
//   3. Volume Confirmation              (UseVolFilter / VolPeriod)
//   4. Trend Filter                     (UseTrendFilter / TrendPeriod)
//   5. ATR-Based Arrow Offset           (UseATROffset / ATRMult)

namespace NinjaTrader.NinjaScript.Indicators
{
    public enum MACFMAType { SMA, EMA }

    [Gui.CategoryOrder("Parameters", 1)]
    [Gui.CategoryOrder("Signals",    2)]
    [Gui.CategoryOrder("Alerts",     3)]
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
                                         + "min separation, volume gate, trend filter, ATR offset.";
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

                MAType           = MACFMAType.SMA;
                MAPeriod         = 9;
                ConsecutiveBars  = 1;
                MinSepTicks      = 0;
                UseVolFilter     = false;
                VolPeriod        = 20;
                UseTrendFilter   = false;
                TrendPeriod      = 50;

                ArrowTicks  = 10;
                UseATROffset = false;
                ATRMult      = 0.5;
                UseColors    = true;
                LongColor    = Brushes.LimeGreen;
                ShortColor   = Brushes.Red;
                Prefix       = "MCF_";

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
                if (string.IsNullOrEmpty(AlertPath))
                    AlertPath = DefaultAlertFilePath();
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
                return Name + "(" + MAType + "," + MAPeriod + ")";
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
                       Math.Max(UseVolFilter   ? VolPeriod   : 0,
                                UseTrendFilter ? TrendPeriod : 0));
            if (CurrentBar < need) return;

            // Main MA
            MALine[0] = (MAType == MACFMAType.EMA)
                ? EMA(Close, MAPeriod)[0]
                : SMA(Close, MAPeriod)[0];

            // 1. Consecutive closes
            bool above = true, below = true;
            for (int i = 0; i < ConsecutiveBars; i++)
            {
                double m = (MAType == MACFMAType.EMA) ? EMA(Close, MAPeriod)[i] : SMA(Close, MAPeriod)[i];
                if (Close[i] <= m) above = false;
                if (Close[i] >= m) below = false;
            }
            double prev = (MAType == MACFMAType.EMA)
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

            // 5. Offset
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

            // Alerts
            if (AlertsOn && State == State.Realtime && IsFirstTickOfBar)
            {
                if (Signal[1] > 0 && !string.IsNullOrWhiteSpace(LongWav))
                    Alert("LA", Priority.High, "Long Entry",
                        ResolveAlertFilePath(LongWav, AlertPath), 10, Brushes.Black, LongColor);
                if (Signal[1] < 0 && !string.IsNullOrWhiteSpace(ShortWav))
                    Alert("SA", Priority.High, "Short Entry",
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
        #endregion

        #region Properties

        [NinjaScriptProperty]
        [Display(Name = "MA Type", Description = "SMA or EMA.", Order = 1, GroupName = "Parameters")]
        public MACFMAType MAType { get; set; }

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
            Description = "Prefix for draw objects. Change when using multiple instances.",
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
