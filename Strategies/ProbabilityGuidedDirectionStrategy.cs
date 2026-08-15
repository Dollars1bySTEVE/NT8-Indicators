// ============================================================
//  ProbabilityGuidedDirectionStrategy.cs
//  NinjaTrader 8 — Probability-Guided BUY/SELL/NO TRADE system
//  Author: Dollars1bySTEVE
// ============================================================

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class ProbabilityGuidedDirectionStrategy : Strategy
    {
        public enum ModeType { SignalOnly, Automation }
        public enum DirectionSignal { NoTrade, Buy, Sell }

        private double dailyStartCumProfit;
        private DateTime lastSessionDate = DateTime.MinValue;
        private int tradesToday;
        private int closedTrades;
        private int wins;
        private int losses;
        private double maxEquity;
        private double maxDrawdown;
        private int totalSignals;
        private int noTradeSignals;
        private int sampleBars;
        private int lastTradeBar = int.MinValue;
        private bool pendingOutcome;
        private double entryCumProfit;
        private int lastEntryQty;
        private bool breakevenMoved;
        private DirectionSignal lastSignal = DirectionSignal.NoTrade;
        private double lastSignalProbability;
        private double lastStructureScore;
        private double lastMomentumScore;
        private double lastVolumeScore;
        private double lastRegimeScore;
        private DirectionSignal entrySignalSnapshot = DirectionSignal.NoTrade;
        private double entryProbabilitySnapshot;
        private double entryStructureSnapshot;
        private double entryMomentumSnapshot;
        private double entryVolumeSnapshot;
        private double entryRegimeSnapshot;
        private double runnerEntryPrice;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "ProbabilityGuidedDirectionStrategy";
                Description = "Composite probability-guided BUY/SELL/NO TRADE strategy using structure, momentum, volume and regime filters.";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                BarsRequiredToTrade = 50;

                Mode = ModeType.SignalOnly;
                MinProbability = 70;
                MinRiskReward = 1.25;
                DirectionDeadZone = 0.05;
                UseRegimeFilter = true;
                RegimeAdxThreshold = 20;
                ComponentVoteThreshold = 0.15;
                ConflictVoteTolerance = 0;
                AllowOffHours = false;

                StructureWeight = 0.35;
                MomentumWeight = 0.30;
                VolumeWeight = 0.25;
                RegimeWeight = 0.10;

                StructureLookback = 20;
                StructureEmaPeriod = 50;
                MomentumPeriod = 8;
                RsiPeriod = 14;
                VolumeLookback = 20;
                AdxPeriod = 14;

                Contracts = 2;
                StopTicks = 80;
                TargetTicks = 120;
                RunnerTargetMultiplier = 2.0;
                MoveRunnerToBreakeven = true;
                MaxTradesPerDay = 4;
                CooldownBars = 6;
                MaxDailyLoss = 600;
                MaxDailyProfit = 1200;

                SessionStartHour = 9;
                SessionStartMinute = 30;
                SessionEndHour = 15;
                SessionEndMinute = 30;

                EnableWalkForwardTagging = true;
                TrainEndDateYyyyMMdd = 20261231;
                AllowAutomationInTrainingPeriod = false;
                LogNoTradeSignals = false;
            }
            else if (State == State.Configure)
            {
                int qtyA = Math.Max(1, Contracts / 2);
                int qtyB = Math.Max(0, Contracts - qtyA);

                SetStopLoss("EntryA", CalculationMode.Ticks, StopTicks, false);
                SetProfitTarget("EntryA", CalculationMode.Ticks, TargetTicks);

                if (qtyB > 0)
                {
                    SetStopLoss("EntryB", CalculationMode.Ticks, StopTicks, false);
                    SetProfitTarget("EntryB", CalculationMode.Ticks, Math.Max(1, (int)Math.Round(TargetTicks * RunnerTargetMultiplier)));
                }
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < Math.Max(BarsRequiredToTrade, Math.Max(Math.Max(StructureEmaPeriod, StructureLookback + 2), Math.Max(RsiPeriod + 2, VolumeLookback + 2))))
                return;

            ResetDailyStateIfNeeded();

            bool inSession = IsInConfiguredSession();
            string sessionTag = inSession ? "RTH" : "OFF";
            string phaseTag = GetWalkForwardPhaseTag();

            double structure = CalculateStructureScore();
            double momentum = CalculateMomentumScore();
            double volume = CalculateVolumeScore();
            double regime = UseRegimeFilter ? CalculateRegimeScore() : 0.0;

            double weighted = (structure * StructureWeight)
                            + (momentum * MomentumWeight)
                            + (volume * VolumeWeight)
                            + (regime * RegimeWeight);
            double totalWeight = Math.Max(0.000001, StructureWeight + MomentumWeight + VolumeWeight + RegimeWeight);
            double composite = Math.Max(-1.0, Math.Min(1.0, weighted / totalWeight));

            DirectionSignal baseDirection = DirectionSignal.NoTrade;
            if (composite > DirectionDeadZone) baseDirection = DirectionSignal.Buy;
            else if (composite < -DirectionDeadZone) baseDirection = DirectionSignal.Sell;

            double probability = 50.0 + (Math.Abs(composite) * 50.0);
            probability = Math.Max(0.0, Math.Min(100.0, probability));

            bool conflict = HasDirectionalConflict(structure, momentum, volume);
            bool probabilityOk = probability >= MinProbability;
            bool rrOk = StopTicks > 0 && ((double)TargetTicks / StopTicks) >= MinRiskReward;
            bool sessionOk = AllowOffHours || inSession;
            bool regimeOk = !UseRegimeFilter || Math.Abs(regime) >= ComponentVoteThreshold;

            bool complianceOk = IsDailyComplianceOk();
            bool tradeCountOk = tradesToday < MaxTradesPerDay;
            bool cooldownOk = (CurrentBar - lastTradeBar) >= CooldownBars;
            bool trainPeriodAutomationOk = AllowAutomationInTrainingPeriod || phaseTag != "TRAIN";

            DirectionSignal finalSignal = baseDirection;
            if (baseDirection == DirectionSignal.NoTrade || !probabilityOk || !rrOk || !sessionOk || !regimeOk || conflict)
                finalSignal = DirectionSignal.NoTrade;

            sampleBars++;
            totalSignals++;
            if (finalSignal == DirectionSignal.NoTrade) noTradeSignals++;

            lastSignal = finalSignal;
            lastSignalProbability = probability;
            lastStructureScore = structure;
            lastMomentumScore = momentum;
            lastVolumeScore = volume;
            lastRegimeScore = regime;

            bool canAutomate = Mode == ModeType.Automation
                               && finalSignal != DirectionSignal.NoTrade
                               && Position.MarketPosition == MarketPosition.Flat
                               && complianceOk
                               && tradeCountOk
                               && cooldownOk
                               && trainPeriodAutomationOk;

            RenderSignal(finalSignal);
            RenderStats(phaseTag, sessionTag, composite, probability, complianceOk, tradeCountOk, cooldownOk, trainPeriodAutomationOk);
            LogSignal(finalSignal, phaseTag, sessionTag, structure, momentum, volume, regime, probability, conflict, probabilityOk, rrOk, sessionOk, regimeOk, complianceOk, tradeCountOk, cooldownOk, trainPeriodAutomationOk);

            if (MoveRunnerToBreakeven && Position.MarketPosition != MarketPosition.Flat && !breakevenMoved && lastEntryQty > 1)
            {
                if (Position.Quantity <= Math.Max(1, lastEntryQty / 2))
                {
                    SetStopLoss("EntryB", CalculationMode.Price, runnerEntryPrice, false);
                    breakevenMoved = true;
                }
            }

            if (!canAutomate)
                return;

            int qtyA = Math.Max(1, Contracts / 2);
            int qtyB = Math.Max(0, Contracts - qtyA);

            if (finalSignal == DirectionSignal.Buy)
            {
                if (qtyB > 0)
                {
                    EnterLong(qtyA, "EntryA");
                    EnterLong(qtyB, "EntryB");
                }
                else
                {
                    EnterLong(qtyA, "EntryA");
                }
            }
            else if (finalSignal == DirectionSignal.Sell)
            {
                if (qtyB > 0)
                {
                    EnterShort(qtyA, "EntryA");
                    EnterShort(qtyB, "EntryB");
                }
                else
                {
                    EnterShort(qtyA, "EntryA");
                }
            }

            tradesToday++;
            lastTradeBar = CurrentBar;
            pendingOutcome = true;
            entryCumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            lastEntryQty = qtyA + qtyB;
            breakevenMoved = false;
            runnerEntryPrice = Close[0];
            entrySignalSnapshot = finalSignal;
            entryProbabilitySnapshot = probability;
            entryStructureSnapshot = structure;
            entryMomentumSnapshot = momentum;
            entryVolumeSnapshot = volume;
            entryRegimeSnapshot = regime;
        }

        protected override void OnExecutionUpdate(Cbi.Execution execution, string executionId, double price, int quantity, Cbi.MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            if (execution.Order.Name == "EntryB" && execution.Order.OrderState == OrderState.Filled)
                runnerEntryPrice = execution.Price;
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            if (!pendingOutcome || marketPosition != MarketPosition.Flat)
                return;

            double nowCum = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            double pnl = nowCum - entryCumProfit;
            closedTrades++;
            if (pnl > 0) wins++;
            else if (pnl < 0) losses++;

            Print(string.Format(
                "PGS_OUTCOME|Time={0:yyyy-MM-dd HH:mm:ss}|Instrument={1}|Signal={2}|Prob={3:F1}|Structure={4:F2}|Momentum={5:F2}|Volume={6:F2}|Regime={7:F2}|PnL={8:F2}|Result={9}",
                Time[0], Instrument.FullName, entrySignalSnapshot, entryProbabilitySnapshot, entryStructureSnapshot, entryMomentumSnapshot, entryVolumeSnapshot, entryRegimeSnapshot, pnl, pnl >= 0 ? "WIN" : "LOSS"));

            pendingOutcome = false;
            lastEntryQty = 0;
            breakevenMoved = false;
            entrySignalSnapshot = DirectionSignal.NoTrade;
        }

        private void ResetDailyStateIfNeeded()
        {
            DateTime today = Time[0].Date;
            if (today == lastSessionDate) return;

            lastSessionDate = today;
            tradesToday = 0;
            dailyStartCumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            pendingOutcome = false;
            breakevenMoved = false;
            lastEntryQty = 0;
        }

        private bool IsDailyComplianceOk()
        {
            double dayPnL = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - dailyStartCumProfit;
            if (dayPnL <= -Math.Abs(MaxDailyLoss)) return false;
            if (dayPnL >= Math.Abs(MaxDailyProfit)) return false;
            return true;
        }

        private bool IsInConfiguredSession()
        {
            int nowTime = ToTime(Time[0]);
            int startTime = ToTime(SessionStartHour, SessionStartMinute, 0);
            int endTime = ToTime(SessionEndHour, SessionEndMinute, 0);
            return nowTime >= startTime && nowTime <= endTime;
        }

        private string GetWalkForwardPhaseTag()
        {
            if (!EnableWalkForwardTagging) return "LIVE";
            int date = ToYyyyMmDd(Time[0]);
            return date <= TrainEndDateYyyyMMdd ? "TRAIN" : "TEST";
        }

        private int ToYyyyMmDd(DateTime dt)
        {
            return dt.Year * 10000 + dt.Month * 100 + dt.Day;
        }

        private double CalculateStructureScore()
        {
            double emaNow = EMA(Close, StructureEmaPeriod)[0];
            double emaPast = EMA(Close, StructureEmaPeriod)[5];
            double slope = emaNow - emaPast;
            double hlBreak = Close[0] - ((MAX(High, StructureLookback)[1] + MIN(Low, StructureLookback)[1]) * 0.5);

            double trendPart = Close[0] > emaNow ? 0.45 : -0.45;
            double slopePart = slope > 0 ? 0.30 : -0.30;
            double breakPart = hlBreak > 0 ? 0.25 : -0.25;

            return ClampScore(trendPart + slopePart + breakPart);
        }

        private double CalculateMomentumScore()
        {
            double roc = Close[0] - Close[Math.Min(MomentumPeriod, CurrentBar)];
            double rsi = RSI(Close, RsiPeriod, 1)[0];
            double body = Close[0] - Open[0];

            double rocPart = roc > 0 ? 0.4 : -0.4;
            double rsiPart = rsi > 55 ? 0.35 : (rsi < 45 ? -0.35 : 0.0);
            double bodyPart = body > 0 ? 0.25 : (body < 0 ? -0.25 : 0.0);

            return ClampScore(rocPart + rsiPart + bodyPart);
        }

        private double CalculateVolumeScore()
        {
            double avgVol = 0;
            int lookback = Math.Min(VolumeLookback, CurrentBar + 1);
            for (int i = 0; i < lookback; i++)
                avgVol += Volume[i];
            avgVol /= Math.Max(1, lookback);

            if (avgVol <= 0) return 0;

            double volRatio = Volume[0] / avgVol;
            double signedPressure = (Close[0] - Open[0]) >= 0 ? 1.0 : -1.0;
            double pressurePart = signedPressure * Math.Min(1.0, Math.Max(0.0, (volRatio - 0.75) / 1.25));

            double participationPart = volRatio >= 1.0 ? 0.2 : -0.2;
            return ClampScore((pressurePart * 0.8) + participationPart);
        }

        private double CalculateRegimeScore()
        {
            double adx = ADX(AdxPeriod)[0];
            if (adx >= RegimeAdxThreshold)
                return 0.6;
            return 0.0;
        }

        private bool HasDirectionalConflict(double structure, double momentum, double volume)
        {
            int bullVotes = 0;
            int bearVotes = 0;

            ApplyVote(structure, ref bullVotes, ref bearVotes);
            ApplyVote(momentum, ref bullVotes, ref bearVotes);
            ApplyVote(volume, ref bullVotes, ref bearVotes);

            return bullVotes > 0 && bearVotes > 0 && Math.Abs(bullVotes - bearVotes) <= ConflictVoteTolerance;
        }

        private void ApplyVote(double componentScore, ref int bullVotes, ref int bearVotes)
        {
            if (componentScore >= ComponentVoteThreshold) bullVotes++;
            else if (componentScore <= -ComponentVoteThreshold) bearVotes++;
        }

        private double ClampScore(double value)
        {
            return Math.Max(-1.0, Math.Min(1.0, value));
        }

        private void RenderSignal(DirectionSignal signal)
        {
            if (signal == DirectionSignal.Buy)
            {
                Draw.ArrowUp(this, "PGS_BUY_" + CurrentBar, false, 0, Low[0] - (2 * TickSize), Brushes.LimeGreen);
            }
            else if (signal == DirectionSignal.Sell)
            {
                Draw.ArrowDown(this, "PGS_SELL_" + CurrentBar, false, 0, High[0] + (2 * TickSize), Brushes.OrangeRed);
            }
        }

        private void RenderStats(string phaseTag, string sessionTag, double composite, double probability, bool complianceOk, bool tradeCountOk, bool cooldownOk, bool trainPeriodAutomationOk)
        {
            double cum = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            if (cum > maxEquity) maxEquity = cum;
            maxDrawdown = Math.Max(maxDrawdown, maxEquity - cum);

            double winRate = closedTrades > 0 ? (wins * 100.0 / closedTrades) : 0.0;
            double expectancy = closedTrades > 0 ? (cum / closedTrades) : 0.0;
            double signalFreq = sampleBars > 0 ? ((totalSignals - noTradeSignals) * 100.0 / sampleBars) : 0.0;
            double rr = StopTicks > 0 ? ((double)TargetTicks / StopTicks) : 0.0;

            string text = string.Format(
                "PGS {0} | {1}\nSignal: {2} ({3:F1}%)\nComposite: {4:F2} | RR: {5:F2}\nS:{6:F2} M:{7:F2} V:{8:F2} R:{9:F2}\nTradesToday: {10}/{11} | CooldownOK:{12}\nWinRate: {13:F1}% | Expectancy: {14:F2}\nDrawdown: {15:F2} | SignalFreq: {16:F1}%\nAutomation Gates -> Compliance:{17} Count:{18} Train/Test:{19}",
                phaseTag, sessionTag, lastSignal, probability, composite, rr, lastStructureScore, lastMomentumScore, lastVolumeScore, lastRegimeScore,
                tradesToday, MaxTradesPerDay, cooldownOk ? "Y" : "N", winRate, expectancy, maxDrawdown, signalFreq,
                complianceOk ? "Y" : "N", tradeCountOk ? "Y" : "N", trainPeriodAutomationOk ? "Y" : "N");

            Draw.TextFixed(this, "PGS_STATUS", text, TextPosition.TopRight);
        }

        private void LogSignal(DirectionSignal signal, string phaseTag, string sessionTag, double structure, double momentum, double volume, double regime, double probability, bool conflict, bool probabilityOk, bool rrOk, bool sessionOk, bool regimeOk, bool complianceOk, bool tradeCountOk, bool cooldownOk, bool trainPeriodAutomationOk)
        {
            if (signal == DirectionSignal.NoTrade && !LogNoTradeSignals)
                return;

            Print(string.Format(
                "PGS_SIGNAL|Time={0:yyyy-MM-dd HH:mm:ss}|Instrument={1}|Phase={2}|Session={3}|Signal={4}|Prob={5:F1}|S={6:F2}|M={7:F2}|V={8:F2}|R={9:F2}|Gates=Conflict:{10},Prob:{11},RR:{12},Session:{13},Regime:{14},Compliance:{15},TradeCount:{16},Cooldown:{17},TrainTest:{18}",
                Time[0], Instrument.FullName, phaseTag, sessionTag, signal, probability, structure, momentum, volume, regime,
                !conflict ? "Y" : "N",
                probabilityOk ? "Y" : "N",
                rrOk ? "Y" : "N",
                sessionOk ? "Y" : "N",
                regimeOk ? "Y" : "N",
                complianceOk ? "Y" : "N",
                tradeCountOk ? "Y" : "N",
                cooldownOk ? "Y" : "N",
                trainPeriodAutomationOk ? "Y" : "N"));
        }

        [NinjaScriptProperty]
        [Display(Name = "Mode", GroupName = "1. Core", Order = 1)]
        public ModeType Mode { get; set; }

        [NinjaScriptProperty]
        [Range(50, 100)]
        [Display(Name = "Min Probability %", GroupName = "1. Core", Order = 2)]
        public int MinProbability { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 10.0)]
        [Display(Name = "Min Risk/Reward", GroupName = "1. Core", Order = 3)]
        public double MinRiskReward { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 0.4)]
        [Display(Name = "Direction Dead Zone", GroupName = "1. Core", Order = 4)]
        public double DirectionDeadZone { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Regime Filter", GroupName = "1. Core", Order = 5)]
        public bool UseRegimeFilter { get; set; }

        [NinjaScriptProperty]
        [Range(5, 60)]
        [Display(Name = "Regime ADX Threshold", GroupName = "1. Core", Order = 6)]
        public int RegimeAdxThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow Off-Hours", GroupName = "1. Core", Order = 7)]
        public bool AllowOffHours { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 0.8)]
        [Display(Name = "Structure Weight", GroupName = "2. Composite", Order = 1)]
        public double StructureWeight { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 0.8)]
        [Display(Name = "Momentum Weight", GroupName = "2. Composite", Order = 2)]
        public double MomentumWeight { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 0.8)]
        [Display(Name = "Volume Weight", GroupName = "2. Composite", Order = 3)]
        public double VolumeWeight { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 0.5)]
        [Display(Name = "Regime Weight", GroupName = "2. Composite", Order = 4)]
        public double RegimeWeight { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 0.8)]
        [Display(Name = "Component Vote Threshold", GroupName = "2. Composite", Order = 5)]
        public double ComponentVoteThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2)]
        [Display(Name = "Conflict Vote Tolerance", GroupName = "2. Composite", Order = 6)]
        public int ConflictVoteTolerance { get; set; }

        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name = "Structure EMA Period", GroupName = "3. Components", Order = 1)]
        public int StructureEmaPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name = "Structure Lookback", GroupName = "3. Components", Order = 2)]
        public int StructureLookback { get; set; }

        [NinjaScriptProperty]
        [Range(2, 100)]
        [Display(Name = "Momentum Period", GroupName = "3. Components", Order = 3)]
        public int MomentumPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "RSI Period", GroupName = "3. Components", Order = 4)]
        public int RsiPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name = "Volume Lookback", GroupName = "3. Components", Order = 5)]
        public int VolumeLookback { get; set; }

        [NinjaScriptProperty]
        [Range(5, 100)]
        [Display(Name = "ADX Period", GroupName = "3. Components", Order = 6)]
        public int AdxPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Contracts", GroupName = "4. Automation", Order = 1)]
        public int Contracts { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Stop Ticks", GroupName = "4. Automation", Order = 2)]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "Target Ticks", GroupName = "4. Automation", Order = 3)]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 5.0)]
        [Display(Name = "Runner Target Multiplier", GroupName = "4. Automation", Order = 4)]
        public double RunnerTargetMultiplier { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Move Runner To Breakeven", GroupName = "4. Automation", Order = 5)]
        public bool MoveRunnerToBreakeven { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Max Trades / Day", GroupName = "4. Automation", Order = 6)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name = "Cooldown Bars", GroupName = "4. Automation", Order = 7)]
        public int CooldownBars { get; set; }

        [NinjaScriptProperty]
        [Range(10, 50000)]
        [Display(Name = "Max Daily Loss ($)", GroupName = "4. Automation", Order = 8)]
        public double MaxDailyLoss { get; set; }

        [NinjaScriptProperty]
        [Range(10, 50000)]
        [Display(Name = "Max Daily Profit ($)", GroupName = "4. Automation", Order = 9)]
        public double MaxDailyProfit { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Session Start Hour", GroupName = "5. Session", Order = 1)]
        public int SessionStartHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Session Start Minute", GroupName = "5. Session", Order = 2)]
        public int SessionStartMinute { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Session End Hour", GroupName = "5. Session", Order = 3)]
        public int SessionEndHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Session End Minute", GroupName = "5. Session", Order = 4)]
        public int SessionEndMinute { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Walk-Forward Tagging", GroupName = "6. Validation", Order = 1)]
        public bool EnableWalkForwardTagging { get; set; }

        [NinjaScriptProperty]
        [Range(19000101, 29991231)]
        [Display(Name = "Train End Date (YYYYMMDD)", GroupName = "6. Validation", Order = 2)]
        public int TrainEndDateYyyyMMdd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow Automation In Training", GroupName = "6. Validation", Order = 3)]
        public bool AllowAutomationInTrainingPeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Log NO TRADE Signals", GroupName = "6. Validation", Order = 4)]
        public bool LogNoTradeSignals { get; set; }
    }
}
