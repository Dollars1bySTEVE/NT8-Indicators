// ============================================================
//  ATRTradingControlPanel.cs
//  NinjaTrader 8 — ATR Trading Control Panel
//  Purpose     : On-chart ATR-based risk sizing and execution
//  Author      : Built for Dollars1bySTEVE
//  Version     : 1.0.0  (2026-09-09)
//  Notes       : Unmanaged-order strategy with WPF chart panel,
//                commission-aware risk sizing, market/pending
//                entries, and automatic OCO brackets.
// ============================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

// NinjaTrader 8 requires NinjaScriptProperty enums to be declared
// outside the namespace so the auto-generated partial class code can
// resolve them correctly.
public enum AtrFeePreset
{
    Custom,
    NinjaTraderLifetime,
    NinjaTraderFree,
    TradovateFree,
    Manual
}

public enum AtrInstrumentFeePreset
{
    Custom,
    ES,
    MES,
    NQ,
    MNQ,
    YM,
    MYM,
    RTY,
    M2K,
    CL,
    MCL,
    GC,
    MGC
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public class ATRTradingControlPanel : Strategy
    {
        private enum PanelAction
        {
            BuyMarket,
            SellMarket,
            BuyLimit,
            SellLimit,
            BuyStop,
            SellStop,
            Flatten,
            CancelPending
        }

        private sealed class CalculationResult
        {
            public double AtrValue;
            public double StopDistancePoints;
            public double StopTicks;
            public double TargetDistancePoints;
            public double TargetTicks;
            public double GrossRiskPerContract;
            public double FrictionPerContract;
            public double TrueRiskPerContract;
            public int Quantity;
            public int RawQuantity;
            public double TrueRewardPerContract;
            public double TrueRiskReward;
            public double TotalRisk;
            public double TotalReward;
            public bool HasEnoughBars;
            public bool IsRiskTooSmall;
        }

        private sealed class PendingBracket
        {
            public int Quantity;
            public double StopPrice;
            public double TargetPrice;
        }

        private sealed class TradePlan
        {
            public string PlanId;
            public string EntrySignal;
            public string StopSignal;
            public string TargetSignal;
            public readonly List<string> SignalNames = new List<string>();
            public bool IsLong;
            public OrderAction EntryAction;
            public OrderType EntryType;
            public int Quantity;
            public double RequestedEntryPrice;
            public int AtrPeriodAtPlacement;
            public double AtrMultiplierAtPlacement;
            public double RewardRiskAtPlacement;
            public bool UseAtrAtFill;
            public double FrozenStopDistancePoints;
            public double FrozenTargetDistancePoints;
            public Order EntryOrder;
            public Order StopOrder;
            public Order TargetOrder;
            public string OcoId;
            public int FilledQuantity;
            public double AverageFillPrice;
            public int BracketSequence;
            public int ActiveBracketQuantity;
            public double ActiveStopPrice;
            public double ActiveTargetPrice;
            public bool WaitingForBracketRefresh;
            public PendingBracket PendingBracket;
            public bool EntryCancelled;
            public bool EntryRejected;
        }

        private Grid panelGrid;
        private TextBox atrPeriodTextBox;
        private TextBox atrMultiplierTextBox;
        private TextBox rewardRiskTextBox;
        private TextBox maxLossTextBox;
        private TextBox commissionTextBox;
        private TextBox feesTextBox;
        private TextBox minQtyTextBox;
        private TextBox maxQtyTextBox;
        private TextBox pendingPriceTextBox;
        private ComboBox feePresetComboBox;
        private ComboBox instrumentFeePresetComboBox;
        private CheckBox showPlanLinesCheckBox;
        private CheckBox useAtrAtFillCheckBox;
        private CheckBox allowMultipleEntriesCheckBox;
        private TextBlock connectionStatusTextBlock;
        private TextBlock liveValuesTextBlock;
        private TextBlock panelStatusTextBlock;

        private bool isPanelUpdating;
        private bool hasInitializedPendingPrice;
        private bool hasUserEditedPendingPrice;
        private string pendingPriceText = string.Empty;
        private ChartControl panelHostChartControl;

        private readonly List<TradePlan> activePlans = new List<TradePlan>();
        private readonly Dictionary<string, TradePlan> plansBySignalName = new Dictionary<string, TradePlan>();
        private readonly List<string> activeOrderLineTags = new List<string>();

        private AtrFeePreset lastAppliedFeePreset = AtrFeePreset.Custom;
        private AtrInstrumentFeePreset lastAppliedInstrumentFeePreset = AtrInstrumentFeePreset.Custom;

        private CalculationResult lastCalculation = new CalculationResult();
        private string statusMessage = "Waiting for realtime.";
        private Brush statusBrush = Brushes.DarkOrange;
        private bool flattenRequested;
        private bool flattenInFlight;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "ATR-based on-chart trading control panel with unmanaged bracket automation.";
                Name = "ATRTradingControlPanel";

                IsUnmanaged = true;
                Calculate = Calculate.OnPriceChange;
                EntriesPerDirection = 10;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = false;
                ExitOnSessionCloseSeconds = 30;
                BarsRequiredToTrade = 20;

                AtrPeriod = 14;
                AtrMultiplier = 1.5;
                RewardRisk = 2.0;
                MaxLossPerTrade = 100.0;
                CommissionPerSidePerContract = 0.0;
                ExchangeFeesPerSidePerContract = 0.0;
                FeePreset = AtrFeePreset.Custom;
                InstrumentFeePreset = AtrInstrumentFeePreset.Custom;
                MinQuantity = 1;
                MaxQuantity = 10;
                ShowPlanLines = true;
                UseAtrAtFillForPendingOrders = true;
                AllowMultipleEntries = false;
                ExitOnSessionClose = false;
            }
            else if (State == State.Configure)
            {
                EntriesPerDirection = Math.Max(1, MaxQuantity);
                IsExitOnSessionCloseStrategy = ExitOnSessionClose;
                ExitOnSessionCloseSeconds = 30;
                BarsRequiredToTrade = Math.Max(20, AtrPeriod + 2);
            }
            else if (State == State.Historical)
            {
                TryCreateControlPanel();
                ApplyPresetSelections();
            }
            else if (State == State.Terminated)
            {
                RemoveControlPanel();
                RemoveAllPlanDrawings();
            }
        }

        protected override void OnBarUpdate()
        {
            ApplyPresetSelections();
            CleanupInactivePlans();
            ProcessFlattenRequest();

            lastCalculation = BuildCalculation(AtrPeriod, AtrMultiplier, RewardRisk, MaxLossPerTrade, CommissionPerSidePerContract, ExchangeFeesPerSidePerContract, MinQuantity, MaxQuantity);

            if (!hasUserEditedPendingPrice)
            {
                pendingPriceText = FormatPrice(Close[0]);
                hasInitializedPendingPrice = true;
            }

            RenderStatusText();
            UpdatePlanDrawings();
            TryCreateControlPanel();
            UpdateControlPanelAsync();
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            if (order == null)
                return;

            TradePlan plan = FindPlanBySignal(order.Name);

            if (error != ErrorCode.NoError || orderState == OrderState.Rejected)
            {
                string rejectionText = string.Format(
                    "Order rejected: {0} | State={1} | Error={2} | {3}",
                    order.Name, orderState, error, comment ?? string.Empty);

                Print("ATRCP " + rejectionText);
                SetStatus(rejectionText, Brushes.Red);

                if (order.Name == "ATRCP_Flatten")
                {
                    flattenRequested = false;
                    flattenInFlight = false;
                    SetStatus(rejectionText + " Flatten order was rejected; manual intervention required.", Brushes.Red);
                    return;
                }

                if (plan != null)
                {
                    plan.EntryRejected = order.Name == plan.EntrySignal;
                    if (order.Name == plan.EntrySignal)
                        plan.EntryOrder = order;
                    else if (order.Name == plan.StopSignal)
                        plan.StopOrder = order;
                    else if (order.Name == plan.TargetSignal)
                        plan.TargetOrder = order;

                    if (order.Name == plan.EntrySignal)
                        RemovePlan(plan);
                    else
                        HandleProtectionFailure(plan, rejectionText);
                }

                return;
            }

            if (order.Name == "ATRCP_Flatten")
            {
                if (orderState == OrderState.Cancelled || orderState == OrderState.Filled)
                    flattenInFlight = false;
                ProcessFlattenRequest();
                return;
            }

            if (plan == null)
                return;

            if (order.Name == plan.EntrySignal)
            {
                plan.EntryOrder = order;

                if (orderState == OrderState.Cancelled && filled == 0)
                {
                    plan.EntryCancelled = true;
                    SetStatus("Pending entry cancelled: " + order.Name, Brushes.Goldenrod);
                    RemovePlan(plan);
                }
            }
            else if (order.Name == plan.StopSignal)
            {
                plan.StopOrder = order;

                if (orderState == OrderState.Filled)
                {
                    CancelSiblingOrder(plan.TargetOrder);
                    SetStatus("Stop filled for " + plan.EntrySignal, Brushes.OrangeRed);
                    RemovePlan(plan);
                }
                else if (orderState == OrderState.Cancelled && plan.WaitingForBracketRefresh)
                {
                    TryRefreshBracketAfterCancel(plan);
                }
            }
            else if (order.Name == plan.TargetSignal)
            {
                plan.TargetOrder = order;

                if (orderState == OrderState.Filled)
                {
                    CancelSiblingOrder(plan.StopOrder);
                    SetStatus("Target filled for " + plan.EntrySignal, Brushes.LimeGreen);
                    RemovePlan(plan);
                }
                else if (orderState == OrderState.Cancelled && plan.WaitingForBracketRefresh)
                {
                    TryRefreshBracketAfterCancel(plan);
                }
            }

            ProcessFlattenRequest();
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            if (execution.Order.Name == "ATRCP_Flatten")
            {
                flattenInFlight = false;
                ProcessFlattenRequest();
                return;
            }

            TradePlan plan = FindPlanBySignal(execution.Order.Name);
            if (plan == null)
                return;

            if (execution.Order.Name == plan.EntrySignal)
            {
                int previousFilled = plan.FilledQuantity;
                double previousValue = plan.AverageFillPrice * previousFilled;

                plan.FilledQuantity += quantity;
                if (plan.FilledQuantity > 0)
                    plan.AverageFillPrice = (previousValue + (price * quantity)) / plan.FilledQuantity;

                UpdateBracketForEntryExecution(plan);

                SetStatus(
                    string.Format(
                        "{0} filled {1}/{2} @ {3}",
                        plan.EntrySignal,
                        plan.FilledQuantity,
                        plan.Quantity,
                        FormatPrice(plan.AverageFillPrice)),
                    Brushes.DeepSkyBlue);
            }
            else if (execution.Order.Name == plan.StopSignal || execution.Order.Name == plan.TargetSignal)
            {
                if (execution.Order.Filled < plan.ActiveBracketQuantity)
                {
                    RequestFlatten("Partial exit fill detected. Flattening remaining position to avoid bracket quantity mismatch.");
                }
            }
        }

        private CalculationResult BuildCalculation(int atrPeriod, double atrMultiplier, double rewardRisk, double maxLoss,
            double commissionPerSide, double feesPerSide, int minQty, int maxQty)
        {
            CalculationResult result = new CalculationResult();
            result.HasEnoughBars = CurrentBar >= Math.Max(atrPeriod, 1);

            if (Instrument == null || Instrument.MasterInstrument == null || TickSize <= 0 || !result.HasEnoughBars)
                return result;

            double atrValue = ATR(Math.Max(1, atrPeriod))[0];
            return BuildCalculationForAtrValue(atrValue, atrPeriod, atrMultiplier, rewardRisk, maxLoss, commissionPerSide, feesPerSide, minQty, maxQty);
        }

        private CalculationResult BuildCalculationForAtrValue(double atrValue, int atrPeriod, double atrMultiplier, double rewardRisk,
            double maxLoss, double commissionPerSide, double feesPerSide, int minQty, int maxQty)
        {
            CalculationResult result = new CalculationResult();
            result.HasEnoughBars = CurrentBar >= Math.Max(atrPeriod, 1);

            if (Instrument == null || Instrument.MasterInstrument == null || TickSize <= 0 || !result.HasEnoughBars)
                return result;

            if (double.IsNaN(atrValue) || double.IsInfinity(atrValue) || atrValue <= 0)
                return result;

            double roundedStopPoints = RoundToTickSizeSafe(atrValue * atrMultiplier);
            if (roundedStopPoints < TickSize)
                roundedStopPoints = TickSize;

            double stopTicks = roundedStopPoints / TickSize;

            double roundedTargetPoints = RoundToTickSizeSafe(roundedStopPoints * rewardRisk);
            if (roundedTargetPoints < TickSize)
                roundedTargetPoints = TickSize;

            double targetTicks = roundedTargetPoints / TickSize;
            double tickValue = Instrument.MasterInstrument.PointValue * TickSize;
            double grossRiskPerContract = stopTicks * tickValue;
            double frictionPerContract = 2.0 * (commissionPerSide + feesPerSide);
            double trueRiskPerContract = grossRiskPerContract + frictionPerContract;
            int rawQuantity = 0;
            int quantity = 0;

            if (trueRiskPerContract > 0 && maxLoss > 0)
            {
                rawQuantity = (int)Math.Floor(maxLoss / trueRiskPerContract);
                if (trueRiskPerContract <= maxLoss && rawQuantity >= 1)
                {
                    int effectiveMinQty = Math.Max(1, minQty);
                    int effectiveMaxQty = Math.Max(effectiveMinQty, maxQty);

                    if (rawQuantity < effectiveMinQty)
                    {
                        if ((effectiveMinQty * trueRiskPerContract) <= maxLoss)
                            quantity = effectiveMinQty;
                    }
                    else
                    {
                        quantity = Math.Min(rawQuantity, effectiveMaxQty);
                    }
                }
            }

            double trueRewardPerContract = (targetTicks * tickValue) - frictionPerContract;
            double trueRiskReward = trueRiskPerContract > 0 ? (trueRewardPerContract / trueRiskPerContract) : 0.0;

            result.AtrValue = atrValue;
            result.StopDistancePoints = roundedStopPoints;
            result.StopTicks = stopTicks;
            result.TargetDistancePoints = roundedTargetPoints;
            result.TargetTicks = targetTicks;
            result.GrossRiskPerContract = grossRiskPerContract;
            result.FrictionPerContract = frictionPerContract;
            result.TrueRiskPerContract = trueRiskPerContract;
            result.RawQuantity = rawQuantity;
            result.Quantity = quantity;
            result.TrueRewardPerContract = trueRewardPerContract;
            result.TrueRiskReward = trueRiskReward;
            result.TotalRisk = quantity * trueRiskPerContract;
            result.TotalReward = quantity * trueRewardPerContract;
            result.IsRiskTooSmall = quantity < 1 && trueRiskPerContract > 0;

            return result;
        }

        private void UpdateBracketForEntryExecution(TradePlan plan)
        {
            if (plan == null || plan.FilledQuantity <= 0 || State != State.Realtime)
                return;

            CalculationResult bracketCalculation;
            if (plan.EntryType != OrderType.Market && plan.UseAtrAtFill)
            {
                double currentAtrValue = ATR(Math.Max(1, plan.AtrPeriodAtPlacement))[0];
                bracketCalculation = BuildCalculationForAtrValue(
                    currentAtrValue,
                    plan.AtrPeriodAtPlacement,
                    plan.AtrMultiplierAtPlacement,
                    plan.RewardRiskAtPlacement,
                    MaxLossPerTrade,
                    CommissionPerSidePerContract,
                    ExchangeFeesPerSidePerContract,
                    MinQuantity,
                    MaxQuantity);
            }
            else
            {
                bracketCalculation = new CalculationResult();
                bracketCalculation.StopDistancePoints = plan.FrozenStopDistancePoints;
                bracketCalculation.TargetDistancePoints = plan.FrozenTargetDistancePoints;
            }

            double stopDistancePoints = Math.Max(TickSize, bracketCalculation.StopDistancePoints);
            double targetDistancePoints = Math.Max(TickSize, bracketCalculation.TargetDistancePoints);

            double stopPrice = plan.IsLong
                ? RoundToTickSizeSafe(plan.AverageFillPrice - stopDistancePoints)
                : RoundToTickSizeSafe(plan.AverageFillPrice + stopDistancePoints);

            double targetPrice = plan.IsLong
                ? RoundToTickSizeSafe(plan.AverageFillPrice + targetDistancePoints)
                : RoundToTickSizeSafe(plan.AverageFillPrice - targetDistancePoints);

            PendingBracket desiredBracket = new PendingBracket();
            desiredBracket.Quantity = plan.FilledQuantity;
            desiredBracket.StopPrice = stopPrice;
            desiredBracket.TargetPrice = targetPrice;

            if (!HasWorkingBracket(plan))
            {
                SubmitBracketOrders(plan, desiredBracket);
                return;
            }

            bool sameQuantity = plan.ActiveBracketQuantity == desiredBracket.Quantity;
            bool sameStop = Math.Abs(plan.ActiveStopPrice - desiredBracket.StopPrice) < (TickSize * 0.5);
            bool sameTarget = Math.Abs(plan.ActiveTargetPrice - desiredBracket.TargetPrice) < (TickSize * 0.5);

            if (sameQuantity && sameStop && sameTarget)
                return;

            plan.PendingBracket = desiredBracket;
            plan.WaitingForBracketRefresh = true;

            CancelSiblingOrder(plan.StopOrder);
            CancelSiblingOrder(plan.TargetOrder);
        }

        private void SubmitBracketOrders(TradePlan plan, PendingBracket desiredBracket)
        {
            if (plan == null || desiredBracket == null || desiredBracket.Quantity <= 0 || State != State.Realtime)
                return;

            plan.BracketSequence++;
            plan.OcoId = "ATRCP-" + plan.PlanId + "-" + plan.BracketSequence;
            plan.StopSignal = plan.EntrySignal + "_Stop_" + plan.BracketSequence;
            plan.TargetSignal = plan.EntrySignal + "_Target_" + plan.BracketSequence;

            RegisterSignal(plan, plan.StopSignal);
            RegisterSignal(plan, plan.TargetSignal);

            OrderAction exitAction = plan.IsLong ? OrderAction.Sell : OrderAction.BuyToCover;

            plan.StopOrder = SubmitOrderUnmanaged(0, exitAction, OrderType.StopMarket, desiredBracket.Quantity, 0, desiredBracket.StopPrice, plan.OcoId, plan.StopSignal);
            plan.TargetOrder = SubmitOrderUnmanaged(0, exitAction, OrderType.Limit, desiredBracket.Quantity, desiredBracket.TargetPrice, 0, plan.OcoId, plan.TargetSignal);
            plan.ActiveBracketQuantity = desiredBracket.Quantity;
            plan.ActiveStopPrice = desiredBracket.StopPrice;
            plan.ActiveTargetPrice = desiredBracket.TargetPrice;
            plan.PendingBracket = null;
            plan.WaitingForBracketRefresh = false;
        }

        private void CleanupInactivePlans()
        {
            for (int i = activePlans.Count - 1; i >= 0; i--)
            {
                TradePlan plan = activePlans[i];
                if (plan == null)
                    continue;

                bool hasWorkingOrders = IsOrderWorking(plan.EntryOrder) || IsOrderWorking(plan.StopOrder) || IsOrderWorking(plan.TargetOrder);
                bool shouldRemove = !hasWorkingOrders
                    && Position.MarketPosition == MarketPosition.Flat
                    && (plan.EntryOrder == null || plan.EntryOrder.OrderState == OrderState.Filled || plan.EntryOrder.OrderState == OrderState.Cancelled || plan.EntryOrder.OrderState == OrderState.Rejected);

                if (shouldRemove)
                    RemovePlan(plan);
            }
        }

        private void HandleProtectionFailure(TradePlan plan, string message)
        {
            if (plan == null)
                return;

            CancelSiblingOrder(plan.StopOrder);
            CancelSiblingOrder(plan.TargetOrder);
            RequestFlatten(message + " Protection failed; flattening remaining position.");
        }

        private void TryRefreshBracketAfterCancel(TradePlan plan)
        {
            if (plan == null || !plan.WaitingForBracketRefresh)
                return;

            bool stopCleared = plan.StopOrder == null || plan.StopOrder.OrderState == OrderState.Cancelled || plan.StopOrder.OrderState == OrderState.Rejected;
            bool targetCleared = plan.TargetOrder == null || plan.TargetOrder.OrderState == OrderState.Cancelled || plan.TargetOrder.OrderState == OrderState.Rejected;

            if (!stopCleared || !targetCleared || plan.PendingBracket == null)
                return;

            plan.StopOrder = null;
            plan.TargetOrder = null;
            SubmitBracketOrders(plan, plan.PendingBracket);
        }

        private void ExecutePanelAction(PanelAction action, string requestedPriceText)
        {
            if (action == PanelAction.Flatten)
            {
                ExecuteFlatten();
                return;
            }

            if (action == PanelAction.CancelPending)
            {
                CancelPendingEntries();
                return;
            }

            if (State != State.Realtime)
            {
                SetStatus("Order buttons are only enabled in realtime.", Brushes.Red);
                return;
            }

            if (!AllowMultipleEntries && HasActivePlanOrPosition())
            {
                SetStatus("Existing position or working plan detected. Finish it before opening a new one.", Brushes.Red);
                return;
            }

            if (AllowMultipleEntries && HasOppositeDirectionExposure(isLong: action == PanelAction.BuyMarket || action == PanelAction.BuyLimit || action == PanelAction.BuyStop))
            {
                SetStatus("Allow Multiple Entries only supports adding in the same direction as the current position/plan.", Brushes.Red);
                return;
            }

            CalculationResult calculation = BuildCalculation(AtrPeriod, AtrMultiplier, RewardRisk, MaxLossPerTrade, CommissionPerSidePerContract, ExchangeFeesPerSidePerContract, MinQuantity, MaxQuantity);
            if (calculation.Quantity < 1)
            {
                SetStatus(
                    string.Format(
                        "Risk too small for 1 contract: true risk/contract = ${0:F2}",
                        calculation.TrueRiskPerContract),
                    Brushes.Red);
                return;
            }

            bool isLong = action == PanelAction.BuyMarket || action == PanelAction.BuyLimit || action == PanelAction.BuyStop;
            OrderAction entryAction = isLong ? OrderAction.Buy : OrderAction.SellShort;
            OrderType entryType = OrderType.Market;
            double limitPrice = 0.0;
            double stopPrice = 0.0;
            double requestedPrice = Close[0];

            if (action == PanelAction.BuyLimit || action == PanelAction.SellLimit || action == PanelAction.BuyStop || action == PanelAction.SellStop)
            {
                if (!TryParseDouble(requestedPriceText, out requestedPrice) || requestedPrice <= 0)
                {
                    SetStatus("Enter a valid pending price before submitting a pending order.", Brushes.Red);
                    return;
                }

                requestedPrice = RoundToTickSizeSafe(requestedPrice);

                if (action == PanelAction.BuyLimit || action == PanelAction.SellLimit)
                {
                    entryType = OrderType.Limit;
                    limitPrice = requestedPrice;
                }
                else
                {
                    entryType = OrderType.StopMarket;
                    stopPrice = requestedPrice;
                }
            }

            TradePlan plan = new TradePlan();
            plan.PlanId = Guid.NewGuid().ToString("N");
            plan.EntrySignal = "ATRCP_Entry_" + plan.PlanId;
            plan.IsLong = isLong;
            plan.EntryAction = entryAction;
            plan.EntryType = entryType;
            plan.Quantity = calculation.Quantity;
            plan.RequestedEntryPrice = requestedPrice;
            plan.AtrPeriodAtPlacement = AtrPeriod;
            plan.AtrMultiplierAtPlacement = AtrMultiplier;
            plan.RewardRiskAtPlacement = RewardRisk;
            plan.UseAtrAtFill = UseAtrAtFillForPendingOrders;
            plan.FrozenStopDistancePoints = calculation.StopDistancePoints;
            plan.FrozenTargetDistancePoints = calculation.TargetDistancePoints;

            RegisterSignal(plan, plan.EntrySignal);
            activePlans.Add(plan);

            plan.EntryOrder = SubmitOrderUnmanaged(0, entryAction, entryType, calculation.Quantity, limitPrice, stopPrice, string.Empty, plan.EntrySignal);

            string actionLabel;
            if (entryType == OrderType.Market)
                actionLabel = isLong ? "BUY MKT" : "SELL MKT";
            else if (entryType == OrderType.Limit)
                actionLabel = isLong ? "BUY LIMIT" : "SELL LIMIT";
            else
                actionLabel = isLong ? "BUY STOP" : "SELL STOP";

            SetStatus(
                string.Format(
                    "{0} submitted | Qty {1} | Stop {2:F2} pts | Target {3:F2} pts",
                    actionLabel,
                    calculation.Quantity,
                    calculation.StopDistancePoints,
                    calculation.TargetDistancePoints),
                Brushes.DeepSkyBlue);
        }

        private void ExecuteFlatten()
        {
            if (State != State.Realtime)
            {
                SetStatus("Flatten is only available in realtime.", Brushes.Red);
                return;
            }

            RequestFlatten("Flatten requested: canceling working orders before flattening the remaining position.");
        }

        private void CancelPendingEntries()
        {
            int cancelled = 0;

            for (int i = activePlans.Count - 1; i >= 0; i--)
            {
                TradePlan plan = activePlans[i];
                if (plan == null || plan.EntryOrder == null)
                    continue;

                bool entryStillWorking = plan.EntryOrder.OrderState == OrderState.Working
                    || plan.EntryOrder.OrderState == OrderState.Accepted
                    || plan.EntryOrder.OrderState == OrderState.Submitted
                    || plan.EntryOrder.OrderState == OrderState.PartFilled;

                if (entryStillWorking && plan.FilledQuantity == 0)
                {
                    CancelOrder(plan.EntryOrder);
                    cancelled++;
                }
            }

            if (cancelled > 0)
                SetStatus("Cancel pending requested for " + cancelled + " entry order(s).", Brushes.Goldenrod);
            else
                SetStatus("No pending entry orders to cancel.", Brushes.DimGray);
        }

        private void ApplyPresetSelections()
        {
            if (FeePreset != lastAppliedFeePreset)
            {
                double presetCommission;
                if (TryGetFeePresetValue(FeePreset, out presetCommission))
                    CommissionPerSidePerContract = presetCommission;

                lastAppliedFeePreset = FeePreset;
            }

            if (InstrumentFeePreset != lastAppliedInstrumentFeePreset)
            {
                double presetFees;
                if (TryGetInstrumentFeePresetValue(InstrumentFeePreset, out presetFees))
                    ExchangeFeesPerSidePerContract = presetFees;

                lastAppliedInstrumentFeePreset = InstrumentFeePreset;
            }
        }

        private bool TryGetFeePresetValue(AtrFeePreset preset, out double commission)
        {
            commission = 0.0;

            switch (preset)
            {
                case AtrFeePreset.NinjaTraderLifetime:
                    commission = 0.09;
                    return true;
                case AtrFeePreset.NinjaTraderFree:
                    commission = 0.35;
                    return true;
                case AtrFeePreset.TradovateFree:
                    commission = 0.35;
                    return true;
                default:
                    return false;
            }
        }

        private bool TryGetInstrumentFeePresetValue(AtrInstrumentFeePreset preset, out double fees)
        {
            fees = 0.0;

            switch (preset)
            {
                case AtrInstrumentFeePreset.ES:
                    fees = 1.25;
                    return true;
                case AtrInstrumentFeePreset.MES:
                    fees = 0.37;
                    return true;
                case AtrInstrumentFeePreset.NQ:
                    fees = 1.25;
                    return true;
                case AtrInstrumentFeePreset.MNQ:
                    fees = 0.37;
                    return true;
                case AtrInstrumentFeePreset.YM:
                    fees = 1.25;
                    return true;
                case AtrInstrumentFeePreset.MYM:
                    fees = 0.37;
                    return true;
                case AtrInstrumentFeePreset.RTY:
                    fees = 1.30;
                    return true;
                case AtrInstrumentFeePreset.M2K:
                    fees = 0.45;
                    return true;
                case AtrInstrumentFeePreset.CL:
                    fees = 1.40;
                    return true;
                case AtrInstrumentFeePreset.MCL:
                    fees = 0.65;
                    return true;
                case AtrInstrumentFeePreset.GC:
                    fees = 1.35;
                    return true;
                case AtrInstrumentFeePreset.MGC:
                    fees = 0.55;
                    return true;
                default:
                    return false;
            }
        }

        private bool HasActivePlanOrPosition()
        {
            if (Position.MarketPosition != MarketPosition.Flat)
                return true;

            for (int i = 0; i < activePlans.Count; i++)
            {
                TradePlan plan = activePlans[i];
                if (plan == null)
                    continue;

                if (!plan.EntryCancelled && !plan.EntryRejected)
                    return true;
            }

            return false;
        }

        private bool HasOppositeDirectionExposure(bool isLong)
        {
            if (Position.MarketPosition == MarketPosition.Long && !isLong)
                return true;

            if (Position.MarketPosition == MarketPosition.Short && isLong)
                return true;

            for (int i = 0; i < activePlans.Count; i++)
            {
                TradePlan plan = activePlans[i];
                if (plan == null || plan.EntryCancelled || plan.EntryRejected)
                    continue;

                if (plan.IsLong != isLong)
                    return true;
            }

            return false;
        }

        private bool HasWorkingBracket(TradePlan plan)
        {
            return IsOrderWorking(plan.StopOrder) && IsOrderWorking(plan.TargetOrder);
        }

        private bool IsOrderWorking(Order order)
        {
            if (order == null)
                return false;

            return order.OrderState == OrderState.Working
                || order.OrderState == OrderState.Accepted
                || order.OrderState == OrderState.Submitted
                || order.OrderState == OrderState.PartFilled;
        }

        private bool HasAnyWorkingOrders()
        {
            for (int i = 0; i < activePlans.Count; i++)
            {
                TradePlan plan = activePlans[i];
                if (plan == null)
                    continue;

                if (IsOrderWorking(plan.EntryOrder) || IsOrderWorking(plan.StopOrder) || IsOrderWorking(plan.TargetOrder))
                    return true;
            }

            return false;
        }

        private void CancelSiblingOrder(Order order)
        {
            if (order != null && IsOrderWorking(order))
                CancelOrder(order);
        }

        private void CancelAllWorkingOrders()
        {
            for (int i = 0; i < activePlans.Count; i++)
            {
                TradePlan plan = activePlans[i];
                if (plan == null)
                    continue;

                CancelSiblingOrder(plan.EntryOrder);
                CancelSiblingOrder(plan.StopOrder);
                CancelSiblingOrder(plan.TargetOrder);
            }
        }

        private void RequestFlatten(string message)
        {
            flattenRequested = true;
            flattenInFlight = false;
            CancelAllWorkingOrders();
            SetStatus(message, Brushes.Goldenrod);
            ProcessFlattenRequest();
        }

        private void ProcessFlattenRequest()
        {
            if (!flattenRequested || State != State.Realtime)
                return;

            if (HasAnyWorkingOrders() || flattenInFlight)
                return;

            if (Position.MarketPosition == MarketPosition.Flat || Position.Quantity <= 0)
            {
                flattenRequested = false;
                flattenInFlight = false;
                CleanupInactivePlans();
                SetStatus("Flatten complete. No working ATR control panel orders remain.", Brushes.LimeGreen);
                return;
            }

            if (Position.MarketPosition == MarketPosition.Long)
                SubmitOrderUnmanaged(0, OrderAction.Sell, OrderType.Market, Position.Quantity, 0, 0, string.Empty, "ATRCP_Flatten");
            else if (Position.MarketPosition == MarketPosition.Short)
                SubmitOrderUnmanaged(0, OrderAction.BuyToCover, OrderType.Market, Position.Quantity, 0, 0, string.Empty, "ATRCP_Flatten");

            flattenInFlight = true;
            SetStatus("Flatten market order submitted for the remaining net position.", Brushes.Goldenrod);
        }

        private void RegisterSignal(TradePlan plan, string signalName)
        {
            if (plan == null || string.IsNullOrEmpty(signalName))
                return;

            if (!plan.SignalNames.Contains(signalName))
                plan.SignalNames.Add(signalName);

            plansBySignalName[signalName] = plan;
        }

        private TradePlan FindPlanBySignal(string signalName)
        {
            if (string.IsNullOrEmpty(signalName))
                return null;

            TradePlan plan;
            if (plansBySignalName.TryGetValue(signalName, out plan))
                return plan;

            return null;
        }

        private void RemovePlan(TradePlan plan)
        {
            if (plan == null)
                return;

            activePlans.Remove(plan);

            for (int i = 0; i < plan.SignalNames.Count; i++)
                plansBySignalName.Remove(plan.SignalNames[i]);

            if (activePlans.Count == 0)
                RemoveActiveOrderLineDrawings();
        }

        private void RenderStatusText()
        {
            string phase = State == State.Realtime ? "REALTIME" : State.ToString().ToUpperInvariant();
            string status = string.Format(
                "ATRCP | {0}\nATR: {1:F2} | Stop: {2:F2} pts ({3:F0} ticks) | Target: {4:F2} pts ({5:F0} ticks)\nQty: {6} | Risk/Ct: ${7:F2} | Reward/Ct: ${8:F2} | True RR: {9:F2}\nTotal Risk: ${10:F2} | Total Reward: ${11:F2}\n{12}",
                phase,
                lastCalculation.AtrValue,
                lastCalculation.StopDistancePoints,
                lastCalculation.StopTicks,
                lastCalculation.TargetDistancePoints,
                lastCalculation.TargetTicks,
                lastCalculation.Quantity,
                lastCalculation.TrueRiskPerContract,
                lastCalculation.TrueRewardPerContract,
                lastCalculation.TrueRiskReward,
                lastCalculation.TotalRisk,
                lastCalculation.TotalReward,
                statusMessage);

            Draw.TextFixed(this, "ATRCP_STATUS", status, TextPosition.TopRight, statusBrush, new SimpleFont("Arial", 12), Brushes.Transparent, Brushes.Transparent, 0);
        }

        private void SetStatus(string message, Brush brush)
        {
            statusMessage = message;
            statusBrush = brush ?? Brushes.DimGray;
            UpdateControlPanelAsync();
            RenderStatusText();
        }

        private void UpdatePlanDrawings()
        {
            if (!ShowPlanLines)
            {
                RemoveAllPlanDrawings();
                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                RemovePreviewPlanDrawings();
                DrawActiveOrderLines();
                return;
            }

            RemoveActiveOrderLineDrawings();

            double pendingPrice;
            if (!TryParseDouble(pendingPriceText, out pendingPrice) || pendingPrice <= 0)
                pendingPrice = Close[0];

            pendingPrice = RoundToTickSizeSafe(pendingPrice);

            double longEntry = pendingPrice;
            double longStop = RoundToTickSizeSafe(longEntry - Math.Max(TickSize, lastCalculation.StopDistancePoints));
            double longTarget = RoundToTickSizeSafe(longEntry + Math.Max(TickSize, lastCalculation.TargetDistancePoints));

            double shortEntry = pendingPrice;
            double shortStop = RoundToTickSizeSafe(shortEntry + Math.Max(TickSize, lastCalculation.StopDistancePoints));
            double shortTarget = RoundToTickSizeSafe(shortEntry - Math.Max(TickSize, lastCalculation.TargetDistancePoints));

            Draw.HorizontalLine(this, "ATRCP_Entry_L", longEntry, Brushes.DeepSkyBlue);
            Draw.HorizontalLine(this, "ATRCP_Stop_L", longStop, Brushes.OrangeRed);
            Draw.HorizontalLine(this, "ATRCP_Tgt_L", longTarget, Brushes.LimeGreen);
            Draw.HorizontalLine(this, "ATRCP_Entry_S", shortEntry, Brushes.DeepSkyBlue);
            Draw.HorizontalLine(this, "ATRCP_Stop_S", shortStop, Brushes.OrangeRed);
            Draw.HorizontalLine(this, "ATRCP_Tgt_S", shortTarget, Brushes.LimeGreen);
        }

        private void DrawActiveOrderLines()
        {
            RemoveActiveOrderLineDrawings();

            for (int i = 0; i < activePlans.Count; i++)
            {
                TradePlan plan = activePlans[i];
                if (plan == null)
                    continue;

                string entryTag = "ATRCP_ActiveEntry_" + i;
                string stopTag = "ATRCP_ActiveStop_" + i;
                string targetTag = "ATRCP_ActiveTarget_" + i;

                if (plan.AverageFillPrice > 0)
                {
                    Draw.HorizontalLine(this, entryTag, plan.AverageFillPrice, Brushes.DeepSkyBlue);
                    activeOrderLineTags.Add(entryTag);
                }

                if (plan.StopOrder != null && plan.StopOrder.StopPrice > 0)
                {
                    Draw.HorizontalLine(this, stopTag, plan.StopOrder.StopPrice, Brushes.OrangeRed);
                    activeOrderLineTags.Add(stopTag);
                }

                if (plan.TargetOrder != null && plan.TargetOrder.LimitPrice > 0)
                {
                    Draw.HorizontalLine(this, targetTag, plan.TargetOrder.LimitPrice, Brushes.LimeGreen);
                    activeOrderLineTags.Add(targetTag);
                }
            }
        }

        private void RemovePreviewPlanDrawings()
        {
            RemoveDrawObject("ATRCP_Entry_L");
            RemoveDrawObject("ATRCP_Stop_L");
            RemoveDrawObject("ATRCP_Tgt_L");
            RemoveDrawObject("ATRCP_Entry_S");
            RemoveDrawObject("ATRCP_Stop_S");
            RemoveDrawObject("ATRCP_Tgt_S");
        }

        private void RemoveActiveOrderLineDrawings()
        {
            for (int i = 0; i < activeOrderLineTags.Count; i++)
                RemoveDrawObject(activeOrderLineTags[i]);

            activeOrderLineTags.Clear();
        }

        private void RemoveAllPlanDrawings()
        {
            RemovePreviewPlanDrawings();
            RemoveActiveOrderLineDrawings();
        }

        private void TryCreateControlPanel()
        {
            if (panelGrid != null || ChartControl == null)
                return;

            panelHostChartControl = ChartControl;
            panelHostChartControl.Dispatcher.InvokeAsync(delegate
            {
                if (panelGrid != null)
                    return;

                panelGrid = BuildPanelGrid();
                UserControlCollection.Add(panelGrid);
                RefreshPanelUi();
            });
        }

        private Grid BuildPanelGrid()
        {
            Grid grid = new Grid();
            grid.Name = "ATRCP_PANEL";
            grid.HorizontalAlignment = HorizontalAlignment.Right;
            grid.VerticalAlignment = VerticalAlignment.Top;
            grid.Margin = new Thickness(8);
            grid.Width = 350;
            grid.Background = new SolidColorBrush(Color.FromArgb(225, 22, 22, 26));

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Border border = new Border();
            border.BorderBrush = Brushes.DimGray;
            border.BorderThickness = new Thickness(1);
            border.Padding = new Thickness(8);
            border.Child = CreatePanelContent();

            Grid.SetRow(border, 0);
            Grid.SetRowSpan(border, 8);
            grid.Children.Add(border);

            return grid;
        }

        private Grid CreatePanelContent()
        {
            Grid content = new Grid();
            for (int i = 0; i < 2; i++)
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });

            for (int row = 0; row < 15; row++)
                content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock header = CreateTextBlock("ATR CONTROL PANEL", Brushes.White, FontWeights.Bold);
            header.FontSize = 14;
            AddControl(content, header, 0, 0, 2);

            connectionStatusTextBlock = CreateTextBlock(string.Empty, Brushes.DarkOrange, FontWeights.SemiBold);
            AddControl(content, connectionStatusTextBlock, 1, 0, 2);

            AddLabeledEditor(content, 2, "ATR Period", out atrPeriodTextBox);
            AddLabeledEditor(content, 3, "ATR Mult", out atrMultiplierTextBox);
            AddLabeledEditor(content, 4, "Reward:Risk", out rewardRiskTextBox);
            AddLabeledEditor(content, 5, "Max Loss $", out maxLossTextBox);
            AddLabeledEditor(content, 6, "Commission/Side", out commissionTextBox);
            AddLabeledEditor(content, 7, "Fees/Side", out feesTextBox);
            AddLabeledEditor(content, 8, "Min Qty", out minQtyTextBox);
            AddLabeledEditor(content, 9, "Max Qty", out maxQtyTextBox);

            feePresetComboBox = CreateComboBox();
            feePresetComboBox.ItemsSource = Enum.GetValues(typeof(AtrFeePreset));
            feePresetComboBox.SelectionChanged += FeePresetComboBox_SelectionChanged;
            AddLabeledControl(content, 10, "Fee Preset", feePresetComboBox);

            instrumentFeePresetComboBox = CreateComboBox();
            instrumentFeePresetComboBox.ItemsSource = Enum.GetValues(typeof(AtrInstrumentFeePreset));
            instrumentFeePresetComboBox.SelectionChanged += InstrumentFeePresetComboBox_SelectionChanged;
            AddLabeledControl(content, 11, "Instr. Fee Preset", instrumentFeePresetComboBox);

            StackPanel flagsPanel = new StackPanel();
            flagsPanel.Orientation = Orientation.Vertical;

            showPlanLinesCheckBox = CreateCheckBox("Show plan lines", ShowPlanLines, ShowPlanLinesCheckBox_Checked);
            useAtrAtFillCheckBox = CreateCheckBox("Use ATR at fill", UseAtrAtFillForPendingOrders, UseAtrAtFillCheckBox_Checked);
            allowMultipleEntriesCheckBox = CreateCheckBox("Allow multiple entries", AllowMultipleEntries, AllowMultipleEntriesCheckBox_Checked);

            flagsPanel.Children.Add(showPlanLinesCheckBox);
            flagsPanel.Children.Add(useAtrAtFillCheckBox);
            flagsPanel.Children.Add(allowMultipleEntriesCheckBox);

            AddLabeledControl(content, 12, "Options", flagsPanel);

            liveValuesTextBlock = CreateTextBlock(string.Empty, Brushes.Gainsboro, FontWeights.Normal);
            liveValuesTextBlock.TextWrapping = TextWrapping.Wrap;
            AddControl(content, liveValuesTextBlock, 13, 0, 2);

            Grid buttonsGrid = CreateButtonsGrid();
            AddControl(content, buttonsGrid, 14, 0, 2);

            HookTextBox(atrPeriodTextBox, AtrPeriodTextBox_TextChanged);
            HookTextBox(atrMultiplierTextBox, AtrMultiplierTextBox_TextChanged);
            HookTextBox(rewardRiskTextBox, RewardRiskTextBox_TextChanged);
            HookTextBox(maxLossTextBox, MaxLossTextBox_TextChanged);
            HookTextBox(commissionTextBox, CommissionTextBox_TextChanged);
            HookTextBox(feesTextBox, FeesTextBox_TextChanged);
            HookTextBox(minQtyTextBox, MinQtyTextBox_TextChanged);
            HookTextBox(maxQtyTextBox, MaxQtyTextBox_TextChanged);

            return content;
        }

        private Grid CreateButtonsGrid()
        {
            Grid buttonsGrid = new Grid();
            buttonsGrid.Margin = new Thickness(0, 8, 0, 0);

            for (int i = 0; i < 2; i++)
                buttonsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int i = 0; i < 6; i++)
                buttonsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Button buyMarketButton = CreateButton("BUY MKT", PanelAction.BuyMarket);
            Button sellMarketButton = CreateButton("SELL MKT", PanelAction.SellMarket);
            AddButton(buttonsGrid, buyMarketButton, 0, 0);
            AddButton(buttonsGrid, sellMarketButton, 0, 1);

            pendingPriceTextBox = CreateTextBox();
            pendingPriceTextBox.Margin = new Thickness(0, 0, 0, 4);
            pendingPriceTextBox.TextChanged += PendingPriceTextBox_TextChanged;
            AddControl(buttonsGrid, pendingPriceTextBox, 1, 0, 2);

            Button buyLimitButton = CreateButton("BUY LIMIT @", PanelAction.BuyLimit);
            Button sellLimitButton = CreateButton("SELL LIMIT @", PanelAction.SellLimit);
            Button buyStopButton = CreateButton("BUY STOP @", PanelAction.BuyStop);
            Button sellStopButton = CreateButton("SELL STOP @", PanelAction.SellStop);
            Button flattenButton = CreateButton("FLATTEN", PanelAction.Flatten);
            Button cancelPendingButton = CreateButton("CANCEL PENDING", PanelAction.CancelPending);

            AddButton(buttonsGrid, buyLimitButton, 2, 0);
            AddButton(buttonsGrid, sellLimitButton, 2, 1);
            AddButton(buttonsGrid, buyStopButton, 3, 0);
            AddButton(buttonsGrid, sellStopButton, 3, 1);
            AddButton(buttonsGrid, flattenButton, 4, 0);
            AddButton(buttonsGrid, cancelPendingButton, 4, 1);

            panelStatusTextBlock = CreateTextBlock(string.Empty, Brushes.Gainsboro, FontWeights.Normal);
            panelStatusTextBlock.TextWrapping = TextWrapping.Wrap;
            panelStatusTextBlock.Margin = new Thickness(0, 8, 0, 0);
            AddControl(buttonsGrid, panelStatusTextBlock, 5, 0, 2);

            return buttonsGrid;
        }

        private void AddLabeledEditor(Grid grid, int row, string label, out TextBox textBox)
        {
            textBox = CreateTextBox();
            AddLabeledControl(grid, row, label, textBox);
        }

        private void AddLabeledControl(Grid grid, int row, string label, UIElement element)
        {
            TextBlock labelBlock = CreateTextBlock(label, Brushes.Gainsboro, FontWeights.Normal);
            labelBlock.Margin = new Thickness(0, 2, 8, 2);
            AddControl(grid, labelBlock, row, 0, 1);

            AddControl(grid, element, row, 1, 1);
        }

        private TextBlock CreateTextBlock(string text, Brush brush, FontWeight weight)
        {
            TextBlock block = new TextBlock();
            block.Text = text;
            block.Foreground = brush;
            block.FontWeight = weight;
            block.Margin = new Thickness(0, 2, 0, 2);
            return block;
        }

        private TextBox CreateTextBox()
        {
            TextBox box = new TextBox();
            box.Margin = new Thickness(0, 2, 0, 2);
            box.Padding = new Thickness(4, 2, 4, 2);
            return box;
        }

        private ComboBox CreateComboBox()
        {
            ComboBox comboBox = new ComboBox();
            comboBox.Margin = new Thickness(0, 2, 0, 2);
            comboBox.Padding = new Thickness(4, 2, 4, 2);
            return comboBox;
        }

        private CheckBox CreateCheckBox(string label, bool isChecked, RoutedEventHandler handler)
        {
            CheckBox checkBox = new CheckBox();
            checkBox.Content = label;
            checkBox.IsChecked = isChecked;
            checkBox.Margin = new Thickness(0, 2, 0, 2);
            checkBox.Foreground = Brushes.Gainsboro;
            checkBox.Checked += handler;
            checkBox.Unchecked += handler;
            return checkBox;
        }

        private Button CreateButton(string label, PanelAction action)
        {
            Button button = new Button();
            button.Content = label;
            button.Tag = action;
            button.Margin = new Thickness(0, 2, 4, 2);
            button.Padding = new Thickness(4, 2, 4, 2);
            button.Click += OrderButton_Click;
            return button;
        }

        private void AddButton(Grid grid, Button button, int row, int column)
        {
            AddControl(grid, button, row, column, 1);
        }

        private void AddControl(Grid grid, UIElement element, int row, int column, int columnSpan)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            Grid.SetColumnSpan(element, columnSpan);
            grid.Children.Add(element);
        }

        private void HookTextBox(TextBox textBox, TextChangedEventHandler handler)
        {
            if (textBox != null)
                textBox.TextChanged += handler;
        }

        private void UpdateControlPanelAsync()
        {
            if (panelGrid == null || panelHostChartControl == null)
                return;

            panelHostChartControl.Dispatcher.InvokeAsync(delegate
            {
                RefreshPanelUi();
            });
        }

        private void RefreshPanelUi()
        {
            if (panelGrid == null)
                return;

            isPanelUpdating = true;
            try
            {
                SetTextIfIdle(atrPeriodTextBox, AtrPeriod.ToString(CultureInfo.InvariantCulture));
                SetTextIfIdle(atrMultiplierTextBox, AtrMultiplier.ToString("0.00", CultureInfo.InvariantCulture));
                SetTextIfIdle(rewardRiskTextBox, RewardRisk.ToString("0.00", CultureInfo.InvariantCulture));
                SetTextIfIdle(maxLossTextBox, MaxLossPerTrade.ToString("0.00", CultureInfo.InvariantCulture));
                SetTextIfIdle(commissionTextBox, CommissionPerSidePerContract.ToString("0.00", CultureInfo.InvariantCulture));
                SetTextIfIdle(feesTextBox, ExchangeFeesPerSidePerContract.ToString("0.00", CultureInfo.InvariantCulture));
                SetTextIfIdle(minQtyTextBox, MinQuantity.ToString(CultureInfo.InvariantCulture));
                SetTextIfIdle(maxQtyTextBox, MaxQuantity.ToString(CultureInfo.InvariantCulture));

                if (pendingPriceTextBox != null)
                {
                    if (!hasInitializedPendingPrice)
                        pendingPriceText = GetSuggestedPendingPriceText();

                    SetTextIfIdle(pendingPriceTextBox, pendingPriceText);
                }

                if (feePresetComboBox != null)
                    feePresetComboBox.SelectedItem = FeePreset;
                if (instrumentFeePresetComboBox != null)
                    instrumentFeePresetComboBox.SelectedItem = InstrumentFeePreset;
                if (showPlanLinesCheckBox != null)
                    showPlanLinesCheckBox.IsChecked = ShowPlanLines;
                if (useAtrAtFillCheckBox != null)
                    useAtrAtFillCheckBox.IsChecked = UseAtrAtFillForPendingOrders;
                if (allowMultipleEntriesCheckBox != null)
                    allowMultipleEntriesCheckBox.IsChecked = AllowMultipleEntries;

                if (connectionStatusTextBlock != null)
                {
                    connectionStatusTextBlock.Text = State == State.Realtime ? "● REALTIME ENABLED" : "● " + State.ToString().ToUpperInvariant();
                    connectionStatusTextBlock.Foreground = State == State.Realtime ? Brushes.LimeGreen : Brushes.DarkOrange;
                }

                if (liveValuesTextBlock != null)
                {
                    liveValuesTextBlock.Text = string.Format(
                        "ATR {0:F2}\nStop {1:F2} pts / {2:F0} ticks\nTarget {3:F2} pts / {4:F0} ticks\nQty {5} (raw {6})\nGross risk ${7:F2}\nFriction ${8:F2}\nTrue risk ${9:F2}\nTrue reward ${10:F2}\nTrue RR {11:F2}\nTotal risk ${12:F2}\nTotal reward ${13:F2}",
                        lastCalculation.AtrValue,
                        lastCalculation.StopDistancePoints,
                        lastCalculation.StopTicks,
                        lastCalculation.TargetDistancePoints,
                        lastCalculation.TargetTicks,
                        lastCalculation.Quantity,
                        lastCalculation.RawQuantity,
                        lastCalculation.GrossRiskPerContract,
                        lastCalculation.FrictionPerContract,
                        lastCalculation.TrueRiskPerContract,
                        lastCalculation.TrueRewardPerContract,
                        lastCalculation.TrueRiskReward,
                        lastCalculation.TotalRisk,
                        lastCalculation.TotalReward);
                }

                if (panelStatusTextBlock != null)
                {
                    panelStatusTextBlock.Text = statusMessage;
                    panelStatusTextBlock.Foreground = statusBrush;
                }

                UpdateButtonStates();
            }
            finally
            {
                isPanelUpdating = false;
            }
        }

        private void UpdateButtonStates()
        {
            if (panelGrid == null)
                return;

            bool canTrade = State == State.Realtime && lastCalculation.Quantity > 0;

            for (int i = 0; i < panelGrid.Children.Count; i++)
            {
                Border border = panelGrid.Children[i] as Border;
                if (border == null)
                    continue;

                Grid content = border.Child as Grid;
                if (content == null)
                    continue;

                SetButtonsEnabledRecursive(content, canTrade);
            }
        }

        private void SetButtonsEnabledRecursive(DependencyObject parent, bool canTrade)
        {
            if (parent == null)
                return;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                Button button = child as Button;
                if (button != null)
                {
                    PanelAction action = (PanelAction)button.Tag;
                    if (action == PanelAction.Flatten || action == PanelAction.CancelPending)
                        button.IsEnabled = State == State.Realtime;
                    else
                        button.IsEnabled = canTrade;
                }

                SetButtonsEnabledRecursive(child, canTrade);
            }
        }

        private void RemoveControlPanel()
        {
            ChartControl chartControl = panelHostChartControl;
            if (chartControl == null)
                return;

            chartControl.Dispatcher.InvokeAsync(delegate
            {
                if (atrPeriodTextBox != null)
                    atrPeriodTextBox.TextChanged -= AtrPeriodTextBox_TextChanged;
                if (atrMultiplierTextBox != null)
                    atrMultiplierTextBox.TextChanged -= AtrMultiplierTextBox_TextChanged;
                if (rewardRiskTextBox != null)
                    rewardRiskTextBox.TextChanged -= RewardRiskTextBox_TextChanged;
                if (maxLossTextBox != null)
                    maxLossTextBox.TextChanged -= MaxLossTextBox_TextChanged;
                if (commissionTextBox != null)
                    commissionTextBox.TextChanged -= CommissionTextBox_TextChanged;
                if (feesTextBox != null)
                    feesTextBox.TextChanged -= FeesTextBox_TextChanged;
                if (minQtyTextBox != null)
                    minQtyTextBox.TextChanged -= MinQtyTextBox_TextChanged;
                if (maxQtyTextBox != null)
                    maxQtyTextBox.TextChanged -= MaxQtyTextBox_TextChanged;
                if (pendingPriceTextBox != null)
                    pendingPriceTextBox.TextChanged -= PendingPriceTextBox_TextChanged;
                if (feePresetComboBox != null)
                    feePresetComboBox.SelectionChanged -= FeePresetComboBox_SelectionChanged;
                if (instrumentFeePresetComboBox != null)
                    instrumentFeePresetComboBox.SelectionChanged -= InstrumentFeePresetComboBox_SelectionChanged;
                if (showPlanLinesCheckBox != null)
                {
                    showPlanLinesCheckBox.Checked -= ShowPlanLinesCheckBox_Checked;
                    showPlanLinesCheckBox.Unchecked -= ShowPlanLinesCheckBox_Checked;
                }
                if (useAtrAtFillCheckBox != null)
                {
                    useAtrAtFillCheckBox.Checked -= UseAtrAtFillCheckBox_Checked;
                    useAtrAtFillCheckBox.Unchecked -= UseAtrAtFillCheckBox_Checked;
                }
                if (allowMultipleEntriesCheckBox != null)
                {
                    allowMultipleEntriesCheckBox.Checked -= AllowMultipleEntriesCheckBox_Checked;
                    allowMultipleEntriesCheckBox.Unchecked -= AllowMultipleEntriesCheckBox_Checked;
                }

                if (panelGrid != null)
                    UserControlCollection.Remove(panelGrid);

                panelGrid = null;
                panelHostChartControl = null;
            });
        }

        private void OrderButton_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            if (button == null || button.Tag == null)
                return;

            PanelAction action = (PanelAction)button.Tag;
            string requestedPrice = pendingPriceTextBox != null ? pendingPriceTextBox.Text : pendingPriceText;

            TriggerCustomEvent(delegate(object o)
            {
                ExecutePanelAction(action, requestedPrice);
            }, null);
        }

        private void PendingPriceTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (isPanelUpdating)
                return;

            pendingPriceText = pendingPriceTextBox != null ? pendingPriceTextBox.Text : string.Empty;
            hasUserEditedPendingPrice = !string.IsNullOrWhiteSpace(pendingPriceText);
        }

        private void FeePresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isPanelUpdating || feePresetComboBox == null || feePresetComboBox.SelectedItem == null)
                return;

            AtrFeePreset selected = (AtrFeePreset)feePresetComboBox.SelectedItem;
            TriggerCustomEvent(delegate(object o)
            {
                FeePreset = selected;
                ApplyPresetSelections();
                UpdateControlPanelAsync();
            }, null);
        }

        private void InstrumentFeePresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isPanelUpdating || instrumentFeePresetComboBox == null || instrumentFeePresetComboBox.SelectedItem == null)
                return;

            AtrInstrumentFeePreset selected = (AtrInstrumentFeePreset)instrumentFeePresetComboBox.SelectedItem;
            TriggerCustomEvent(delegate(object o)
            {
                InstrumentFeePreset = selected;
                ApplyPresetSelections();
                UpdateControlPanelAsync();
            }, null);
        }

        private void ShowPlanLinesCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (isPanelUpdating || showPlanLinesCheckBox == null)
                return;

            bool value = showPlanLinesCheckBox.IsChecked ?? false;
            TriggerCustomEvent(delegate(object o)
            {
                ShowPlanLines = value;
                UpdatePlanDrawings();
                UpdateControlPanelAsync();
            }, null);
        }

        private void UseAtrAtFillCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (isPanelUpdating || useAtrAtFillCheckBox == null)
                return;

            bool value = useAtrAtFillCheckBox.IsChecked ?? false;
            TriggerCustomEvent(delegate(object o)
            {
                UseAtrAtFillForPendingOrders = value;
                UpdateControlPanelAsync();
            }, null);
        }

        private void AllowMultipleEntriesCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (isPanelUpdating || allowMultipleEntriesCheckBox == null)
                return;

            bool value = allowMultipleEntriesCheckBox.IsChecked ?? false;
            TriggerCustomEvent(delegate(object o)
            {
                AllowMultipleEntries = value;
                UpdateControlPanelAsync();
            }, null);
        }

        private void AtrPeriodTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (isPanelUpdating)
                return;

            int value;
            if (!TryParseInt(atrPeriodTextBox.Text, out value) || value < 1)
                return;

            TriggerCustomEvent(delegate(object o)
            {
                AtrPeriod = value;
                BarsRequiredToTrade = Math.Max(20, AtrPeriod + 2);
                UpdateControlPanelAsync();
            }, null);
        }

        private void AtrMultiplierTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (isPanelUpdating)
                return;

            double value;
            if (!TryParseDouble(atrMultiplierTextBox.Text, out value) || value <= 0)
                return;

            TriggerCustomEvent(delegate(object o)
            {
                AtrMultiplier = value;
                UpdateControlPanelAsync();
            }, null);
        }

        private void RewardRiskTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (isPanelUpdating)
                return;

            double value;
            if (!TryParseDouble(rewardRiskTextBox.Text, out value) || value <= 0)
                return;

            TriggerCustomEvent(delegate(object o)
            {
                RewardRisk = value;
                UpdateControlPanelAsync();
            }, null);
        }

        private void MaxLossTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (isPanelUpdating)
                return;

            double value;
            if (!TryParseDouble(maxLossTextBox.Text, out value) || value <= 0)
                return;

            TriggerCustomEvent(delegate(object o)
            {
                MaxLossPerTrade = value;
                UpdateControlPanelAsync();
            }, null);
        }

        private void CommissionTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (isPanelUpdating)
                return;

            double value;
            if (!TryParseDouble(commissionTextBox.Text, out value) || value < 0)
                return;

            TriggerCustomEvent(delegate(object o)
            {
                CommissionPerSidePerContract = value;
                UpdateControlPanelAsync();
            }, null);
        }

        private void FeesTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (isPanelUpdating)
                return;

            double value;
            if (!TryParseDouble(feesTextBox.Text, out value) || value < 0)
                return;

            TriggerCustomEvent(delegate(object o)
            {
                ExchangeFeesPerSidePerContract = value;
                UpdateControlPanelAsync();
            }, null);
        }

        private void MinQtyTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (isPanelUpdating)
                return;

            int value;
            if (!TryParseInt(minQtyTextBox.Text, out value) || value < 1)
                return;

            TriggerCustomEvent(delegate(object o)
            {
                MinQuantity = value;
                if (MaxQuantity < MinQuantity)
                    MaxQuantity = MinQuantity;
                UpdateControlPanelAsync();
            }, null);
        }

        private void MaxQtyTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (isPanelUpdating)
                return;

            int value;
            if (!TryParseInt(maxQtyTextBox.Text, out value) || value < 1)
                return;

            TriggerCustomEvent(delegate(object o)
            {
                MaxQuantity = value;
                if (MinQuantity > MaxQuantity)
                    MinQuantity = MaxQuantity;
                EntriesPerDirection = Math.Max(1, MaxQuantity);
                UpdateControlPanelAsync();
            }, null);
        }

        private double RoundToTickSizeSafe(double price)
        {
            if (Instrument == null || Instrument.MasterInstrument == null || TickSize <= 0)
                return price;

            return Instrument.MasterInstrument.RoundToTickSize(price);
        }

        private bool TryParseDouble(string text, out double value)
        {
            if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value))
                return true;

            return double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value);
        }

        private bool TryParseInt(string text, out int value)
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return true;

            return int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value);
        }

        private string FormatPrice(double price)
        {
            return price.ToString("0.00########", CultureInfo.InvariantCulture);
        }

        private string GetSuggestedPendingPriceText()
        {
            return CurrentBar >= 0 ? FormatPrice(Close[0]) : string.Empty;
        }

        private void SetTextIfIdle(TextBox textBox, string text)
        {
            if (textBox == null || textBox.IsKeyboardFocusWithin)
                return;

            if (!string.Equals(textBox.Text, text, StringComparison.Ordinal))
                textBox.Text = text;
        }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "ATR Period", GroupName = "1. Risk Engine", Order = 1)]
        public int AtrPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 100.0)]
        [Display(Name = "ATR Multiplier", GroupName = "1. Risk Engine", Order = 2)]
        public double AtrMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 100.0)]
        [Display(Name = "Reward:Risk", GroupName = "1. Risk Engine", Order = 3)]
        public double RewardRisk { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, 100000.0)]
        [Display(Name = "Max Loss Per Trade ($)", GroupName = "1. Risk Engine", Order = 4)]
        public double MaxLossPerTrade { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1000.0)]
        [Display(Name = "Commission per side per contract ($)", GroupName = "2. Friction", Order = 1)]
        public double CommissionPerSidePerContract { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1000.0)]
        [Display(Name = "Exchange/clearing fees per side per contract ($)", GroupName = "2. Friction", Order = 2)]
        public double ExchangeFeesPerSidePerContract { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Fee Preset (example only)", GroupName = "2. Friction", Order = 3)]
        public AtrFeePreset FeePreset { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Instrument Fee Preset (example only)", GroupName = "2. Friction", Order = 4)]
        public AtrInstrumentFeePreset InstrumentFeePreset { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Min Quantity", GroupName = "3. Sizing", Order = 1)]
        public int MinQuantity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Max Quantity", GroupName = "3. Sizing", Order = 2)]
        public int MaxQuantity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show plan lines on chart", GroupName = "4. Panel", Order = 1)]
        public bool ShowPlanLines { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use ATR at fill for pending orders", GroupName = "4. Panel", Order = 2)]
        public bool UseAtrAtFillForPendingOrders { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow Multiple Entries", GroupName = "4. Panel", Order = 3)]
        public bool AllowMultipleEntries { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Exit On Session Close", GroupName = "5. Safety", Order = 1)]
        public bool ExitOnSessionClose { get; set; }
    }
}
