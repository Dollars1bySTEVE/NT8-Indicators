#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

public enum RedTailEmaSignalMode { EmaCrossover, PriceCross }
public enum RedTailEmaAlertTiming { Intrabar, BarClose }
public enum RedTailEmaMomentumFilter { None, RSI, MACD }

namespace NinjaTrader.NinjaScript.Indicators
{
    public class RedTailEmaMomentum : Indicator
    {
        private EMA fastEma;
        private EMA middleEma;
        private EMA slowEma;
        private RSI momentum;
        private MACD macd;
        private readonly string instanceId = Guid.NewGuid().ToString("N");
        private double previousDifference;
        private bool hasPreviousDifference;
        private bool waitingForRealtimeBaseline;
        private int lastClosedBar = -1;
        private int lastLongBar = -1;
        private int lastShortBar = -1;
        private string reportedLongSound;
        private string reportedShortSound;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "RedTailEmaMomentum";
                Description = "Configurable EMA signals with optional RSI/MACD confirmation and intrabar or bar-close audio.";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DrawOnPricePanel = true;
                IsSuspendedWhileInactive = false;
                EmaCount = 3;
                FastPeriod = 9;
                MiddlePeriod = 21;
                SlowPeriod = 50;
                SignalMode = RedTailEmaSignalMode.EmaCrossover;
                AlertTiming = RedTailEmaAlertTiming.Intrabar;
                MomentumFilter = RedTailEmaMomentumFilter.RSI;
                RsiPeriod = 14;
                RsiMidpoint = 50;
                MacdFastPeriod = 12;
                MacdSlowPeriod = 26;
                MacdSignalPeriod = 9;
                ShowArrows = true;
                SignalOffsetTicks = 10;
                LongColor = Brushes.LimeGreen;
                ShortColor = Brushes.Red;
                EnableAlerts = true;
                LongSound = Path.Combine(NinjaTrader.Core.Globals.InstallDir, "sounds", "Alert1.wav");
                ShortSound = Path.Combine(NinjaTrader.Core.Globals.InstallDir, "sounds", "Alert2.wav");
                AddPlot(Brushes.Gold, "FastEMA");
                AddPlot(Brushes.DodgerBlue, "MiddleEMA");
                AddPlot(Brushes.MediumOrchid, "SlowEMA");
            }
            else if (State == State.Configure)
            {
                if (SignalMode == RedTailEmaSignalMode.EmaCrossover && EmaCount < 2)
                    throw new ArgumentException("EMA crossover requires at least two EMAs. Use PriceCross for one EMA.");
                if (EmaCount >= 2 && FastPeriod >= MiddlePeriod)
                    throw new ArgumentException("Fast EMA period must be smaller than Middle EMA period.");
                if (EmaCount == 3 && MiddlePeriod >= SlowPeriod)
                    throw new ArgumentException("Middle EMA period must be smaller than Slow EMA period.");
                if (Calculate != Calculate.OnEachTick)
                    throw new ArgumentException("Keep Calculate set to On each tick; use Alert Timing to select bar-close signals.");
                if (MomentumFilter == RedTailEmaMomentumFilter.MACD && MacdFastPeriod >= MacdSlowPeriod)
                    throw new ArgumentException("MACD fast period must be smaller than MACD slow period.");
            }
            else if (State == State.DataLoaded)
            {
                fastEma = EMA(Input, FastPeriod);
                if (EmaCount >= 2) middleEma = EMA(Input, MiddlePeriod);
                if (EmaCount == 3) slowEma = EMA(Input, SlowPeriod);
                if (MomentumFilter == RedTailEmaMomentumFilter.RSI) momentum = RSI(Input, RsiPeriod, 3);
                if (MomentumFilter == RedTailEmaMomentumFilter.MACD)
                    macd = MACD(Input, MacdFastPeriod, MacdSlowPeriod, MacdSignalPeriod);
            }
            else if (State == State.Realtime)
            {
                // Do not announce a crossover that occurred before the indicator started.
                waitingForRealtimeBaseline = true;
                lastClosedBar = CurrentBar - 1;
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;

            Values[0][0] = fastEma[0];
            if (EmaCount >= 2) Values[1][0] = middleEma[0];
            else Values[1].Reset();
            if (EmaCount == 3) Values[2][0] = slowEma[0];
            else Values[2].Reset();
            int warmup = FastPeriod;
            if (EmaCount >= 2) warmup = Math.Max(warmup, MiddlePeriod);
            if (EmaCount == 3) warmup = Math.Max(warmup, SlowPeriod);
            if (MomentumFilter == RedTailEmaMomentumFilter.RSI) warmup = Math.Max(warmup, RsiPeriod);
            if (MomentumFilter == RedTailEmaMomentumFilter.MACD)
                warmup = Math.Max(warmup, Math.Max(MacdSlowPeriod, MacdSignalPeriod));

            // Without Tick Replay historical OnEachTick updates contain bar closes only.
            bool historicalCloses = State == State.Historical && !Bars.IsTickReplay;
            int barsAgo = AlertTiming == RedTailEmaAlertTiming.BarClose && !historicalCloses ? 1 : 0;
            if (CurrentBar - barsAgo < warmup) return;

            if (AlertTiming == RedTailEmaAlertTiming.BarClose)
            {
                if (!historicalCloses && !IsFirstTickOfBar) return;
                int closedBar = CurrentBar - barsAgo;
                if (closedBar <= lastClosedBar) return;
                lastClosedBar = closedBar;
                EvaluateCross(barsAgo, Difference(barsAgo + 1), Difference(barsAgo));
                return;
            }

            double difference = Difference(0);
            if (!hasPreviousDifference || (State == State.Realtime && waitingForRealtimeBaseline))
            {
                previousDifference = difference;
                hasPreviousDifference = true;
                waitingForRealtimeBaseline = false;
                return;
            }

            EvaluateCross(0, previousDifference, difference);
            previousDifference = difference;
        }

        private double Difference(int barsAgo)
        {
            return SignalMode == RedTailEmaSignalMode.EmaCrossover
                ? fastEma[barsAgo] - middleEma[barsAgo]
                : Input[barsAgo] - fastEma[barsAgo];
        }

        private void EvaluateCross(int barsAgo, double previous, double current)
        {
            int direction = previous <= 0 && current > 0 ? 1
                : previous >= 0 && current < 0 ? -1 : 0;
            if (direction == 0) return;
            if (SignalMode == RedTailEmaSignalMode.PriceCross)
            {
                if (EmaCount >= 2 && direction * (fastEma[barsAgo] - middleEma[barsAgo]) <= 0) return;
                if (EmaCount == 3 && direction * (middleEma[barsAgo] - slowEma[barsAgo]) <= 0) return;
            }
            else if (EmaCount == 3 && direction * (Input[barsAgo] - slowEma[barsAgo]) <= 0)
                return;

            if (MomentumFilter == RedTailEmaMomentumFilter.RSI
                && direction * (momentum[barsAgo] - RsiMidpoint) <= 0) return;
            if (MomentumFilter == RedTailEmaMomentumFilter.MACD
                && direction * macd.Diff[barsAgo] <= 0) return;
            int signalBar = CurrentBar - barsAgo;
            if (direction > 0 ? lastLongBar == signalBar : lastShortBar == signalBar) return;
            if (direction > 0) lastLongBar = signalBar;
            else lastShortBar = signalBar;

            string tag = "RTEM_" + instanceId + "_" + signalBar + "_" + direction;
            if (ShowArrows)
            {
                if (direction > 0)
                    Draw.ArrowUp(this, tag, false, barsAgo, Low[barsAgo] - SignalOffsetTicks * TickSize, LongColor);
                else
                    Draw.ArrowDown(this, tag, false, barsAgo, High[barsAgo] + SignalOffsetTicks * TickSize, ShortColor);
            }

            if (State != State.Realtime || !EnableAlerts) return;
            string sound = (direction > 0 ? LongSound : ShortSound) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(sound) || !File.Exists(sound))
            {
                string reported = direction > 0 ? reportedLongSound : reportedShortSound;
                if (reported != sound || reported == null)
                {
                    string error = Name + ": " + (direction > 0 ? "Long" : "Short")
                        + " alert sound not found: " + sound + ". Set a valid WAV path in Alerts.";
                    Print(error);
                    Log(error, LogLevel.Error);
                    if (direction > 0) reportedLongSound = sound;
                    else reportedShortSound = sound;
                }
                return;
            }
            string message = Name + ": " + (direction > 0 ? "LONG" : "SHORT")
                + " " + Instrument.FullName + " (" + AlertTiming + ", " + SignalMode + ")";
            Alert(tag, Priority.High, message, sound, 0, Brushes.Black, direction > 0 ? LongColor : ShortColor);
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, 3)]
        [Display(Name = "Number of EMAs", Order = 1, GroupName = "1. EMAs")]
        public int EmaCount { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Fast EMA period", Order = 2, GroupName = "1. EMAs")]
        public int FastPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Middle EMA period", Order = 3, GroupName = "1. EMAs")]
        public int MiddlePeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Slow EMA period", Order = 4, GroupName = "1. EMAs")]
        public int SlowPeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Signal mode", Order = 1, GroupName = "2. Signals")]
        public RedTailEmaSignalMode SignalMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert timing", Order = 2, GroupName = "2. Signals")]
        public RedTailEmaAlertTiming AlertTiming { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Momentum filter", Order = 1, GroupName = "3. Momentum")]
        public RedTailEmaMomentumFilter MomentumFilter { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "RSI period", Order = 2, GroupName = "3. Momentum")]
        public int RsiPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name = "RSI midpoint", Order = 3, GroupName = "3. Momentum")]
        public double RsiMidpoint { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "MACD fast period", Order = 4, GroupName = "3. Momentum")]
        public int MacdFastPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "MACD slow period", Order = 5, GroupName = "3. Momentum")]
        public int MacdSlowPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "MACD signal period", Order = 6, GroupName = "3. Momentum")]
        public int MacdSignalPeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show arrows", Order = 1, GroupName = "4. Appearance")]
        public bool ShowArrows { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Signal offset (ticks)", Order = 2, GroupName = "4. Appearance")]
        public int SignalOffsetTicks { get; set; }

        [XmlIgnore]
        [Display(Name = "Long signal color", Order = 3, GroupName = "4. Appearance")]
        public Brush LongColor { get; set; }

        [Browsable(false)]
        public string LongColorSerialized
        {
            get { return Serialize.BrushToString(LongColor); }
            set { LongColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Short signal color", Order = 4, GroupName = "4. Appearance")]
        public Brush ShortColor { get; set; }

        [Browsable(false)]
        public string ShortColorSerialized
        {
            get { return Serialize.BrushToString(ShortColor); }
            set { ShortColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Enable audio alerts", Order = 1, GroupName = "5. Alerts")]
        public bool EnableAlerts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Long sound (full WAV path)", Order = 2, GroupName = "5. Alerts")]
        public string LongSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Short sound (full WAV path)", Order = 3, GroupName = "5. Alerts")]
        public string ShortSound { get; set; }
        #endregion
    }
}
