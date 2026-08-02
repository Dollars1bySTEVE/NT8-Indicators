// ============================================================
//  ApexLegacyStrategy.cs
//  NinjaTrader 8 — Automated/Semi-Auto Strategy
//  Instruments : NQ (E-mini) / MNQ (Micro)
//  Signals     : SignalsMA (9 SMA cross) + iTrend Pro
//  Compliance  : Apex Futures Legacy $50k rules
//  Author      : Built for Dollars1bySTEVE
//  Version     : 1.0.4  (NY RTH — 2026-08-02)
//  Fix v1.0.1  : Removed invalid OnSessionChange override
//  Fix v1.0.2  : SMA/LinReg declared as ISeries<double>
//  Fix v1.0.3  : Inline double calls; added Indicators namespace
//  New v1.0.4  : iTrend Source selector — ninZaPro (real) or
//                LinRegApprox (fallback, no license needed)
//                ninZaPro settings: Period=3, Smoothing=LinReg/5,
//                CrossoverTolerance=1.25 (matches your chart)
// ============================================================

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class ApexLegacyStrategy : Strategy
    {
        // ── Enums ────────────────────────────────────────────
        public enum ExecMode       { FullAuto, SemiAuto, AlertOnly }
        public enum SignalMode     { RequireBoth, EitherOne }
        public enum InstrumentMode { AutoDetect, NQ, MNQ }
        public enum iTrendSource   { ninZaPro, LinRegApprox }

        // ── Internal state ───────────────────────────────────
        private double   dailyPnL         = 0;
        private double   sessionStartCash = 0;
        private bool     tradingAllowed   = true;
        private bool     t1Hit            = false;
        private int      nqContracts;
        private int      stopTicks;
        private int      t1Ticks;
        private int      t2Ticks;
        private DateTime lastSessionDate  = DateTime.MinValue;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Apex Legacy $50k Strategy — SignalsMA + iTrend | NY RTH";
                Name        = "ApexLegacyStrategy";

                ExecutionMode       = ExecMode.SemiAuto;
                SignalRequirement    = SignalMode.EitherOne;
                InstrumentSetting   = InstrumentMode.AutoDetect;

                // iTrend source — switch between real ninZa or fallback
                iTrendMode          = iTrendSource.ninZaPro;

                // ninZaiTrendPro settings (must match your chart)
                iTrendPeriod        = 3;
                iTrendSmoothing     = true;
                iTrendSmoothPeriod  = 5;
                iTrendTolerance     = 1.25;

                StopTicks           = 80;    // 20 pts
                T1Ticks             = 80;    // 20 pts
                T2Ticks             = 160;   // 40 pts
                MoveToBreakeven     = true;

                SignalsMAPeriod     = 9;

                EnableCompliance    = true;
                DailyProfitLimit    = 800.0;
                DailyLossLimit      = 400.0;
                AccountFloor        = 50500.0;

                SessionStartHour    = 9;
                SessionStartMinute  = 30;
                SessionEndHour      = 15;
                SessionEndMinute    = 30;

                Calculate                    = Calculate.OnBarClose;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;
                BarsRequiredToTrade          = 20;
            }
            else if (State == State.Configure)
            {
                string instName = Instrument.MasterInstrument.Name.ToUpper();
                bool isMNQ = (InstrumentSetting == InstrumentMode.MNQ) ||
                             (InstrumentSetting == InstrumentMode.AutoDetect && instName.Contains("MNQ"));

                nqContracts = isMNQ ? 10 : 2;
                stopTicks   = StopTicks;
                t1Ticks     = T1Ticks;
                t2Ticks     = T2Ticks;

                SetStopLoss("T1Entry",     CalculationMode.Ticks, stopTicks, false);
                SetStopLoss("T2Entry",     CalculationMode.Ticks, stopTicks, false);
                SetProfitTarget("T1Entry", CalculationMode.Ticks, t1Ticks);
                SetProfitTarget("T2Entry", CalculationMode.Ticks, t2Ticks);
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade) return;

            // ── Daily session reset ──────────────────────────────
            DateTime today = Time[0].Date;
            if (today != lastSessionDate)
            {
                lastSessionDate  = today;
                dailyPnL         = 0;
                tradingAllowed   = true;
                t1Hit            = false;
                sessionStartCash = Account.Get(AccountItem.CashValue, Currency.UsDollar);

                RemoveDrawObject("DailyCapHit");
                RemoveDrawObject("DailyLossHit");
                RemoveDrawObject("AccountFloorMsg");
            }

            // ── Session time gate ────────────────────────────────
            int nowTime   = ToTime(Time[0]);
            int startTime = ToTime(SessionStartHour, SessionStartMinute, 0);
            int endTime   = ToTime(SessionEndHour,   SessionEndMinute,   0);
            if (nowTime < startTime || nowTime > endTime) return;

            // ── Apex compliance checks ───────────────────────────
            if (EnableCompliance)
            {
                dailyPnL = Account.Get(AccountItem.CashValue, Currency.UsDollar) - sessionStartCash;

                if (dailyPnL >= DailyProfitLimit)
                {
                    tradingAllowed = false;
                    Draw.TextFixed(this, "DailyCapHit",
                        "DAILY PROFIT CAP HIT ($" + DailyProfitLimit + ") - Done for today!",
                        TextPosition.TopLeft, Brushes.Lime,
                        new Gui.Tools.SimpleFont("Arial", 14),
                        Brushes.Transparent, Brushes.Transparent, 0);
                }

                if (dailyPnL <= -DailyLossLimit)
                {
                    tradingAllowed = false;
                    Draw.TextFixed(this, "DailyLossHit",
                        "DAILY LOSS LIMIT (-$" + DailyLossLimit + ") HIT - Done for today!",
                        TextPosition.TopLeft, Brushes.Red,
                        new Gui.Tools.SimpleFont("Arial", 14),
                        Brushes.Transparent, Brushes.Transparent, 0);
                }

                double balance = Account.Get(AccountItem.CashValue, Currency.UsDollar);
                if (balance < AccountFloor)
                {
                    tradingAllowed = false;
                    Draw.TextFixed(this, "AccountFloorMsg",
                        "ACCOUNT BELOW FLOOR ($" + AccountFloor + ") - Trading LOCKED!",
                        TextPosition.TopLeft, Brushes.OrangeRed,
                        new Gui.Tools.SimpleFont("Arial", 14),
                        Brushes.Transparent, Brushes.Transparent, 0);
                }
            }

            if (!tradingAllowed) return;

            // ── SignalsMA ────────────────────────────────────────
            double smaVal0 = SMA(Close, SignalsMAPeriod)[0];
            double smaVal1 = SMA(Close, SignalsMAPeriod)[1];

            bool smaBullish = Close[1] < smaVal1 && Close[0] > smaVal0;
            bool smaBearish = Close[1] > smaVal1 && Close[0] < smaVal0;

            // ── iTrend direction ─────────────────────────────────
            bool iTrendBullish, iTrendBearish;

            if (iTrendMode == iTrendSource.ninZaPro)
            {
                // Real ninZaiTrendPro — DMI+ > DMI- = Bull, DMI+ < DMI- = Bear
                // Settings match your chart exactly: Period=3, LinReg smoothing/5, Tolerance=1.25
                var itp = ninZaiTrendPro(iTrendPeriod, iTrendSmoothing,
                                         ninZa_MAType.LinReg, iTrendSmoothPeriod,
                                         iTrendTolerance);
                iTrendBullish = itp.DMIPlus[0]  > itp.DMIMinus[0];
                iTrendBearish = itp.DMIPlus[0]  < itp.DMIMinus[0];
            }
            else
            {
                // LinReg approximation — no ninZa license required
                double lrFast = LinReg(Close, 3)[0];
                double lrSlow = LinReg(Close, 5)[0];
                iTrendBullish = lrFast > lrSlow;
                iTrendBearish = lrFast < lrSlow;
            }

            // ── Combined signal logic ────────────────────────────
            bool longSignal, shortSignal;

            if (SignalRequirement == SignalMode.RequireBoth)
            {
                longSignal  = smaBullish && iTrendBullish;
                shortSignal = smaBearish && iTrendBearish;
            }
            else // EitherOne
            {
                longSignal  = smaBullish || (iTrendBullish && Close[0] > smaVal0);
                shortSignal = smaBearish || (iTrendBearish && Close[0] < smaVal0);
            }

            // ── Breakeven — move T2 stop when T1 fills ───────────
            if (MoveToBreakeven && Position.MarketPosition != MarketPosition.Flat)
            {
                if (!t1Hit && Position.Quantity <= nqContracts / 2)
                {
                    t1Hit = true;
                    SetStopLoss("T2Entry", CalculationMode.Price, Position.AveragePrice, false);
                }
            }

            // ── Entry logic ──────────────────────────────────────
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                t1Hit = false;

                if (ExecutionMode == ExecMode.FullAuto)
                {
                    if (longSignal)
                    {
                        EnterLong(nqContracts / 2, "T1Entry");
                        EnterLong(nqContracts / 2, "T2Entry");
                    }
                    else if (shortSignal)
                    {
                        EnterShort(nqContracts / 2, "T1Entry");
                        EnterShort(nqContracts / 2, "T2Entry");
                    }
                }
                else if (ExecutionMode == ExecMode.SemiAuto)
                {
                    if (longSignal)
                    {
                        Draw.ArrowUp(this, "SemiLong" + CurrentBar, false, 0,
                            Low[0] - 2 * TickSize, Brushes.Cyan);
                        PlaySound(@"C:\Program Files\NinjaTrader 8\sounds\Alert4.wav");
                    }
                    else if (shortSignal)
                    {
                        Draw.ArrowDown(this, "SemiShort" + CurrentBar, false, 0,
                            High[0] + 2 * TickSize, Brushes.Magenta);
                        PlaySound(@"C:\Program Files\NinjaTrader 8\sounds\Alert3.wav");
                    }
                }
                else // AlertOnly
                {
                    if (longSignal)
                    {
                        Draw.ArrowUp(this, "AlertLong" + CurrentBar, false, 0,
                            Low[0] - 2 * TickSize, Brushes.LimeGreen);
                        PlaySound(@"C:\Program Files\NinjaTrader 8\sounds\Alert4.wav");
                    }
                    else if (shortSignal)
                    {
                        Draw.ArrowDown(this, "AlertShort" + CurrentBar, false, 0,
                            High[0] + 2 * TickSize, Brushes.OrangeRed);
                        PlaySound(@"C:\Program Files\NinjaTrader 8\sounds\Alert3.wav");
                    }
                }
            }
        }

        // ════════════════════════════════════════════════════════
        //  PARAMETERS — visible in NT8 Strategy dialog
        // ════════════════════════════════════════════════════════

        [NinjaScriptProperty]
        [Display(Name = "Execution Mode", GroupName = "1. Execution", Order = 1)]
        public ExecMode ExecutionMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Signal Requirement", GroupName = "1. Execution", Order = 2)]
        public SignalMode SignalRequirement { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Instrument Override", GroupName = "1. Execution", Order = 3)]
        public InstrumentMode InstrumentSetting { get; set; }

        [NinjaScriptProperty]
        [Range(1, 999)]
        [Display(Name = "Stop Loss (Ticks)", GroupName = "2. Order Management", Order = 1)]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 999)]
        [Display(Name = "T1 Target (Ticks)", GroupName = "2. Order Management", Order = 2)]
        public int T1Ticks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 999)]
        [Display(Name = "T2 Target (Ticks)", GroupName = "2. Order Management", Order = 3)]
        public int T2Ticks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Move T2 to Breakeven on T1", GroupName = "2. Order Management", Order = 4)]
        public bool MoveToBreakeven { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "SignalsMA Period", GroupName = "3. Signals", Order = 1)]
        public int SignalsMAPeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "iTrend Source", GroupName = "3. Signals", Order = 2)]
        public iTrendSource iTrendMode { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "iTrend Period", GroupName = "3. Signals", Order = 3)]
        public int iTrendPeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "iTrend Smoothing Enabled", GroupName = "3. Signals", Order = 4)]
        public bool iTrendSmoothing { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "iTrend Smooth Period", GroupName = "3. Signals", Order = 5)]
        public int iTrendSmoothPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 10.0)]
        [Display(Name = "iTrend Crossover Tolerance", GroupName = "3. Signals", Order = 6)]
        public double iTrendTolerance { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Apex Compliance", GroupName = "4. Apex Compliance", Order = 1)]
        public bool EnableCompliance { get; set; }

        [NinjaScriptProperty]
        [Range(0, 99999)]
        [Display(Name = "Daily Profit Limit ($)", GroupName = "4. Apex Compliance", Order = 2)]
        public double DailyProfitLimit { get; set; }

        [NinjaScriptProperty]
        [Range(0, 99999)]
        [Display(Name = "Daily Loss Limit ($)", GroupName = "4. Apex Compliance", Order = 3)]
        public double DailyLossLimit { get; set; }

        [NinjaScriptProperty]
        [Range(0, 999999)]
        [Display(Name = "Account Floor ($)", GroupName = "4. Apex Compliance", Order = 4)]
        public double AccountFloor { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Session Start Hour (ET)", GroupName = "5. Session", Order = 1)]
        public int SessionStartHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Session Start Minute", GroupName = "5. Session", Order = 2)]
        public int SessionStartMinute { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Session End Hour (ET)", GroupName = "5. Session", Order = 3)]
        public int SessionEndHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Session End Minute", GroupName = "5. Session", Order = 4)]
        public int SessionEndMinute { get; set; }
    }
}
