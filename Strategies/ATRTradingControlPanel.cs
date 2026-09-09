// ============================================================
//  ATRTradingControlPanel.cs
//  NinjaTrader 8 — ATR Trading Control Panel
//  Purpose     : On-chart ATR-based risk sizing and execution
//  Author      : Built for Dollars1bySTEVE
//  Version     : 1.1.0  (2026-09-09)
//  Notes       : Unmanaged-order strategy with WPF chart panel,
//                commission-aware risk sizing, market/pending
//                entries, and automatic OCO brackets.
//  Changelog   : 1) Added undersized-risk modes (Block/ShrinkStopToFit/TradeMinQtyAnyway)
//                2) Added explicit block-state guidance banner and disabled button tooltips
//                3) Compact panel layout + settings expander + panel corner + drag/collapse
//                4) Removed duplicate Draw.TextFixed status overlay
//                5) Removed redundant Manual fee preset
//                6) Updated button styling to Chart-Trader-like colors
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
    TradovateFree
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

public enum AtrUndersizedRiskMode
{
    Block,
    ShrinkStopToFit,
    TradeMinQtyAnyway
}

public enum AtrPanelCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
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
            public double AtrStopDistancePoints;
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
            public bool StopCappedByRisk;
            public bool IsRiskOvershoot;
            public double CappedStopTicks;
            public double SuggestedMaxAtrMultiplier;
            public double RequiredMaxLossForOneContract;
            public int EffectiveMinQuantity;
            public string SuggestedMicroSymbol;
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
        private TextBox atrMultiplierQuickTextBox;
        private TextBox rewardRiskQuickTextBox;
        private TextBox maxLossQuickTextBox;
        private TextBox commissionTextBox;
        private TextBox feesTextBox;
        private TextBox minQtyTextBox;
        private TextBox maxQtyTextBox;
        private TextBox pendingPriceTextBox;
        private ComboBox feePresetComboBox;
        private ComboBox instrumentFeePresetComboBox;
        private ComboBox undersizedRiskModeComboBox;
        private CheckBox showPlanLinesCheckBox;
        private CheckBox useAtrAtFillCheckBox;
        private CheckBox allowMultipleEntriesCheckBox;
        private TextBlock connectionStatusTextBlock;
        private TextBlock liveValuesTextBlock;
        private TextBlock riskBannerTextBlock;
        private TextBlock panelStatusTextBlock;
        private Button panelCollapseToggleButton;
        private Grid panelBodyGrid;
        private Grid panelHeaderGrid;
        private Expander settingsExpander;
        private Border panelBorder;
        private TranslateTransform panelTransform;

        private readonly List<Button> orderButtons = new List<Button>();
        private bool panelCollapsed;
        private bool settingsExpandedState;
        private Point dragStartPoint;
        private bool isDraggingPanel;
        private double panelDragX;
        private double panelDragY;

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
        private DateTime lastVisualRefreshUtc = DateTime.MinValue;

        private static readonly TimeSpan VisualRefreshThrottleInterval = TimeSpan.FromMilliseconds(250);

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
                UndersizedRiskMode = AtrUndersizedRiskMode.ShrinkStopToFit;
                ShowPlanLines = true;
                UseAtrAtFillForPendingOrders = true;
                AllowMultipleEntries = false;
                SettingsExpandedByDefault = false;
                settingsExpandedState = false;
                PanelPosition = AtrPanelCorner.TopRight;
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
                RemoveDrawObject("ATRCP_STATUS");
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

            TryCreateControlPanel();
            RefreshVisualsIfDue();
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
            return BuildCalculationForAtrValue(atrValue, atrPeriod, atrMultiplier, rewardRisk, maxLoss, commissionPerSide, feesPerSide, minQty, maxQty, UndersizedRiskMode);
        }

        private CalculationResult BuildCalculationForAtrValue(double atrValue, int atrPeriod, double atrMultiplier, double rewardRisk,
            double maxLoss, double commissionPerSide, double feesPerSide, int minQty, int maxQty, AtrUndersizedRiskMode undersizedRiskMode)
        {
            CalculationResult result = new CalculationResult();
            result.HasEnoughBars = CurrentBar >= Math.Max(atrPeriod, 1);

            if (Instrument == null || Instrument.MasterInstrument == null || TickSize <= 0 || !result.HasEnoughBars)
                return result;

            if (double.IsNaN(atrValue) || double.IsInfinity(atrValue) || atrValue <= 0)
                return result;

            int effectiveMinQty = Math.Max(1, minQty);
            int effectiveMaxQty = Math.Max(effectiveMinQty, maxQty);

            double atrStopPoints = RoundToTickSizeSafe(atrValue * atrMultiplier);
            if (atrStopPoints < TickSize)
                atrStopPoints = TickSize;

            double stopDistancePoints = atrStopPoints;
            double stopTicks = stopDistancePoints / TickSize;
            double targetDistancePoints = RoundToTickSizeSafe(stopDistancePoints * rewardRisk);
            if (targetDistancePoints < TickSize)
                targetDistancePoints = TickSize;
            double targetTicks = targetDistancePoints / TickSize;
            double tickValue = Instrument.MasterInstrument.PointValue * TickSize;
            double frictionPerContract = 2.0 * (commissionPerSide + feesPerSide);
            double grossRiskPerContract = stopTicks * tickValue;
            double trueRiskPerContract = grossRiskPerContract + frictionPerContract;
            double trueRewardPerContract = (targetTicks * tickValue) - frictionPerContract;
            double trueRiskReward = trueRiskPerContract > 0 ? (trueRewardPerContract / trueRiskPerContract) : 0.0;

            int rawQuantity = trueRiskPerContract > 0 && maxLoss > 0 ? (int)Math.Floor(maxLoss / trueRiskPerContract) : 0;
            int quantity = 0;
            bool undersized = trueRiskPerContract > maxLoss && trueRiskPerContract > 0 && maxLoss > 0;
            bool stopCappedByRisk = false;
            bool riskOvershoot = false;
            string microSuggestion = TryGetMicroSuggestionSymbol();

            if (!undersized)
            {
                if (trueRiskPerContract > 0 && maxLoss > 0 && rawQuantity >= 1)
                {
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
            else
            {
                if (undersizedRiskMode == AtrUndersizedRiskMode.ShrinkStopToFit && tickValue > 0)
                {
                    double maxStopTickBudget = (maxLoss - frictionPerContract) / tickValue;
                    int maxStopTicks = (int)Math.Floor(maxStopTickBudget);
                    if (maxStopTicks >= 1)
                    {
                        double cappedStopDistancePoints = maxStopTicks * TickSize;
                        double cappedStopTicks = maxStopTicks;
                        double cappedTargetDistancePoints = RoundToTickSizeSafe(cappedStopDistancePoints * rewardRisk);
                        if (cappedTargetDistancePoints < TickSize)
                            cappedTargetDistancePoints = TickSize;

                        double cappedTargetTicks = cappedTargetDistancePoints / TickSize;
                        double cappedGrossRiskPerContract = cappedStopTicks * tickValue;
                        double cappedTrueRiskPerContract = cappedGrossRiskPerContract + frictionPerContract;
                        double cappedTrueRewardPerContract = (cappedTargetTicks * tickValue) - frictionPerContract;
                        double cappedTrueRiskReward = cappedTrueRiskPerContract > 0 ? (cappedTrueRewardPerContract / cappedTrueRiskPerContract) : 0.0;

                        int shrinkQty = 1;
                        if (effectiveMinQty > 1)
                        {
                            shrinkQty = effectiveMinQty;
                            if ((shrinkQty * cappedTrueRiskPerContract) > maxLoss)
                                shrinkQty = 0;
                        }

                        if (shrinkQty > 0)
                        {
                            quantity = Math.Min(shrinkQty, effectiveMaxQty);
                            stopDistancePoints = cappedStopDistancePoints;
                            stopTicks = cappedStopTicks;
                            targetDistancePoints = cappedTargetDistancePoints;
                            targetTicks = cappedTargetTicks;
                            grossRiskPerContract = cappedGrossRiskPerContract;
                            trueRiskPerContract = cappedTrueRiskPerContract;
                            trueRewardPerContract = cappedTrueRewardPerContract;
                            trueRiskReward = cappedTrueRiskReward;
                            stopCappedByRisk = true;
                        }
                    }
                }
                else if (undersizedRiskMode == AtrUndersizedRiskMode.TradeMinQtyAnyway)
                {
                    quantity = Math.Min(Math.Max(1, effectiveMinQty), effectiveMaxQty);
                    riskOvershoot = quantity > 0;
                }
            }

            double largestStopTicksForOneContract = tickValue > 0 ? Math.Floor((maxLoss - frictionPerContract) / tickValue) : 0.0;
            double suggestedMultiplier = 0.0;
            if (largestStopTicksForOneContract > 0 && atrValue > 0)
            {
                suggestedMultiplier = (largestStopTicksForOneContract * TickSize) / atrValue;
                suggestedMultiplier = Math.Floor(suggestedMultiplier * 100.0) / 100.0;
                if (suggestedMultiplier < 0)
                    suggestedMultiplier = 0;
            }

            result.AtrValue = atrValue;
            result.AtrStopDistancePoints = atrStopPoints;
            result.StopDistancePoints = stopDistancePoints;
            result.StopTicks = stopTicks;
            result.TargetDistancePoints = targetDistancePoints;
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
            result.IsRiskTooSmall = quantity < 1 && undersized;
            result.StopCappedByRisk = stopCappedByRisk;
            result.IsRiskOvershoot = riskOvershoot;
            result.CappedStopTicks = stopTicks;
            result.SuggestedMaxAtrMultiplier = suggestedMultiplier;
            result.RequiredMaxLossForOneContract = Math.Ceiling(Math.Max(0, result.AtrStopDistancePoints / TickSize * tickValue + frictionPerContract));
            result.EffectiveMinQuantity = effectiveMinQty;
            result.SuggestedMicroSymbol = microSuggestion;

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
                    MaxQuantity,
                    UndersizedRiskMode);
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
                SetStatus(BuildNoSizeMessage(calculation), Brushes.Red);
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

                if (IsOrderWorking(plan.EntryOrder) && plan.FilledQuantity == 0)
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
                || order.OrderState == OrderState.PartFilled
                || order.OrderState == OrderState.CancelPending
                || order.OrderState == OrderState.ChangePending;
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

        private void SetStatus(string message, Brush brush)
        {
            statusMessage = message;
            statusBrush = brush ?? Brushes.DimGray;
            UpdateControlPanelAsync();
        }

        private void RefreshVisualsIfDue()
        {
            DateTime nowUtc = DateTime.UtcNow;
            if (lastVisualRefreshUtc != DateTime.MinValue && nowUtc - lastVisualRefreshUtc < VisualRefreshThrottleInterval)
                return;

            lastVisualRefreshUtc = nowUtc;
            UpdatePlanDrawings();
            UpdateControlPanelAsync();
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

                settingsExpandedState = SettingsExpandedByDefault;
                panelGrid = BuildPanelGrid();
                UserControlCollection.Add(panelGrid);
                RefreshPanelUi();
            });
        }

        private Grid BuildPanelGrid()
        {
            Grid grid = new Grid();
            grid.Name = "ATRCP_PANEL";
            ApplyPanelCornerAlignment(grid);
            grid.Margin = new Thickness(8);
            grid.Width = 236;
            grid.Background = new SolidColorBrush(Color.FromArgb(225, 22, 22, 26));
            grid.RenderTransform = panelTransform = new TranslateTransform();

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            panelBorder = new Border();
            panelBorder.BorderBrush = Brushes.DimGray;
            panelBorder.BorderThickness = new Thickness(1);
            panelBorder.Padding = new Thickness(5);
            panelBorder.Child = CreatePanelContent();

            Grid.SetRow(panelBorder, 0);
            grid.Children.Add(panelBorder);

            return grid;
        }

        private Grid CreatePanelContent()
        {
            Grid content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            panelHeaderGrid = new Grid();
            panelHeaderGrid.Margin = new Thickness(0, 0, 0, 2);
            panelHeaderGrid.Cursor = Cursors.SizeAll;
            panelHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panelHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock header = CreateTextBlock("ATR CONTROL PANEL v1.1.0", Brushes.White, FontWeights.Bold, 11);
            header.Cursor = Cursors.SizeAll;
            AddControl(panelHeaderGrid, header, 0, 0, 1);

            panelCollapseToggleButton = new Button();
            panelCollapseToggleButton.Content = "▼";
            panelCollapseToggleButton.Width = 22;
            panelCollapseToggleButton.Height = 20;
            panelCollapseToggleButton.Margin = new Thickness(2, 0, 0, 0);
            panelCollapseToggleButton.Padding = new Thickness(0);
            panelCollapseToggleButton.FontWeight = FontWeights.Bold;
            panelCollapseToggleButton.Background = Brushes.DimGray;
            panelCollapseToggleButton.Foreground = Brushes.White;
            panelCollapseToggleButton.Click += PanelCollapseToggleButton_Click;
            AddControl(panelHeaderGrid, panelCollapseToggleButton, 0, 1, 1);

            panelHeaderGrid.MouseLeftButtonDown += PanelHeader_MouseLeftButtonDown;
            panelHeaderGrid.MouseLeftButtonUp += PanelHeader_MouseLeftButtonUp;
            panelHeaderGrid.MouseMove += PanelHeader_MouseMove;
            AddControl(content, panelHeaderGrid, 0, 0, 1);

            riskBannerTextBlock = CreateTextBlock(string.Empty, Brushes.White, FontWeights.Bold, 11);
            riskBannerTextBlock.TextWrapping = TextWrapping.Wrap;
            riskBannerTextBlock.Margin = new Thickness(0, 1, 0, 3);
            riskBannerTextBlock.Visibility = Visibility.Collapsed;
            AddControl(content, riskBannerTextBlock, 1, 0, 1);

            panelBodyGrid = new Grid();
            for (int row = 0; row < 6; row++)
                panelBodyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid quickInputsGrid = new Grid();
            quickInputsGrid.Margin = new Thickness(0, 1, 0, 2);
            quickInputsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            quickInputsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            quickInputsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            AddQuickEditor(quickInputsGrid, 0, "Mult", out atrMultiplierQuickTextBox, AtrMultiplierTextBox_TextChanged);
            AddQuickEditor(quickInputsGrid, 1, "R:R", out rewardRiskQuickTextBox, RewardRiskTextBox_TextChanged);
            AddQuickEditor(quickInputsGrid, 2, "Loss $", out maxLossQuickTextBox, MaxLossTextBox_TextChanged);
            AddControl(panelBodyGrid, quickInputsGrid, 0, 0, 1);

            connectionStatusTextBlock = CreateTextBlock(string.Empty, Brushes.DarkOrange, FontWeights.SemiBold, 10);
            AddControl(panelBodyGrid, connectionStatusTextBlock, 1, 0, 1);

            liveValuesTextBlock = CreateTextBlock(string.Empty, Brushes.Gainsboro, FontWeights.Normal, 10);
            liveValuesTextBlock.TextWrapping = TextWrapping.Wrap;
            AddControl(panelBodyGrid, liveValuesTextBlock, 2, 0, 1);

            settingsExpander = new Expander();
            settingsExpander.Header = "Settings";
            settingsExpander.Foreground = Brushes.Gainsboro;
            settingsExpander.IsExpanded = settingsExpandedState || SettingsExpandedByDefault;
            settingsExpander.Margin = new Thickness(0, 2, 0, 2);
            settingsExpander.Expanded += SettingsExpander_Expanded;
            settingsExpander.Collapsed += SettingsExpander_Collapsed;

            Grid settingsGrid = new Grid();
            for (int i = 0; i < 2; i++)
                settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
            for (int row = 0; row < 12; row++)
                settingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddLabeledEditor(settingsGrid, 0, "ATR Period", out atrPeriodTextBox);
            AddLabeledEditor(settingsGrid, 1, "ATR Mult", out atrMultiplierTextBox);
            AddLabeledEditor(settingsGrid, 2, "Reward:Risk", out rewardRiskTextBox);
            AddLabeledEditor(settingsGrid, 3, "Max Loss $", out maxLossTextBox);
            AddLabeledEditor(settingsGrid, 4, "Commission/Side", out commissionTextBox);
            AddLabeledEditor(settingsGrid, 5, "Fees/Side", out feesTextBox);
            AddLabeledEditor(settingsGrid, 6, "Min Qty", out minQtyTextBox);
            AddLabeledEditor(settingsGrid, 7, "Max Qty", out maxQtyTextBox);

            feePresetComboBox = CreateComboBox();
            feePresetComboBox.ItemsSource = Enum.GetValues(typeof(AtrFeePreset));
            feePresetComboBox.SelectionChanged += FeePresetComboBox_SelectionChanged;
            AddLabeledControl(settingsGrid, 8, "Fee Preset", feePresetComboBox);

            instrumentFeePresetComboBox = CreateComboBox();
            instrumentFeePresetComboBox.ItemsSource = Enum.GetValues(typeof(AtrInstrumentFeePreset));
            instrumentFeePresetComboBox.SelectionChanged += InstrumentFeePresetComboBox_SelectionChanged;
            AddLabeledControl(settingsGrid, 9, "Instr. Fee Preset", instrumentFeePresetComboBox);

            undersizedRiskModeComboBox = CreateComboBox();
            undersizedRiskModeComboBox.ItemsSource = Enum.GetValues(typeof(AtrUndersizedRiskMode));
            undersizedRiskModeComboBox.SelectionChanged += UndersizedRiskModeComboBox_SelectionChanged;
            AddLabeledControl(settingsGrid, 10, "Undersized Risk Mode", undersizedRiskModeComboBox);

            StackPanel flagsPanel = new StackPanel();
            flagsPanel.Orientation = Orientation.Vertical;

            showPlanLinesCheckBox = CreateCheckBox("Show plan lines", ShowPlanLines, ShowPlanLinesCheckBox_Checked);
            useAtrAtFillCheckBox = CreateCheckBox("Use ATR at fill", UseAtrAtFillForPendingOrders, UseAtrAtFillCheckBox_Checked);
            allowMultipleEntriesCheckBox = CreateCheckBox("Allow multiple entries", AllowMultipleEntries, AllowMultipleEntriesCheckBox_Checked);

            flagsPanel.Children.Add(showPlanLinesCheckBox);
            flagsPanel.Children.Add(useAtrAtFillCheckBox);
            flagsPanel.Children.Add(allowMultipleEntriesCheckBox);

            AddLabeledControl(settingsGrid, 11, "Options", flagsPanel);

            settingsExpander.Content = settingsGrid;
            AddControl(panelBodyGrid, settingsExpander, 3, 0, 1);

            pendingPriceTextBox = CreateTextBox();
            pendingPriceTextBox.Margin = new Thickness(0, 1, 0, 2);
            pendingPriceTextBox.TextChanged += PendingPriceTextBox_TextChanged;
            AddControl(panelBodyGrid, pendingPriceTextBox, 4, 0, 1);

            Grid buttonsGrid = CreateButtonsGrid();
            AddControl(panelBodyGrid, buttonsGrid, 5, 0, 1);

            AddControl(content, panelBodyGrid, 2, 0, 1);

            panelStatusTextBlock = CreateTextBlock(string.Empty, Brushes.Gainsboro, FontWeights.Normal, 11);
            panelStatusTextBlock.TextWrapping = TextWrapping.Wrap;
            panelStatusTextBlock.Margin = new Thickness(0, 3, 0, 0);
            AddControl(content, panelStatusTextBlock, 3, 0, 1);

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
            buttonsGrid.Margin = new Thickness(0, 2, 0, 0);

            for (int i = 0; i < 2; i++)
                buttonsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int i = 0; i < 5; i++)
                buttonsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Button buyMarketButton = CreateButton("BUY MKT", PanelAction.BuyMarket);
            Button sellMarketButton = CreateButton("SELL MKT", PanelAction.SellMarket);
            AddButton(buttonsGrid, buyMarketButton, 0, 0);
            AddButton(buttonsGrid, sellMarketButton, 0, 1);

            Button buyLimitButton = CreateButton("BUY LIMIT @", PanelAction.BuyLimit);
            Button sellLimitButton = CreateButton("SELL LIMIT @", PanelAction.SellLimit);
            Button buyStopButton = CreateButton("BUY STOP @", PanelAction.BuyStop);
            Button sellStopButton = CreateButton("SELL STOP @", PanelAction.SellStop);
            Button flattenButton = CreateButton("FLATTEN", PanelAction.Flatten);
            Button cancelPendingButton = CreateButton("CANCEL PENDING", PanelAction.CancelPending);

            AddButton(buttonsGrid, buyLimitButton, 1, 0);
            AddButton(buttonsGrid, sellLimitButton, 1, 1);
            AddButton(buttonsGrid, buyStopButton, 2, 0);
            AddButton(buttonsGrid, sellStopButton, 2, 1);
            AddButton(buttonsGrid, flattenButton, 3, 0);
            AddButton(buttonsGrid, cancelPendingButton, 3, 1);

            return buttonsGrid;
        }

        private void AddLabeledEditor(Grid grid, int row, string label, out TextBox textBox)
        {
            textBox = CreateTextBox();
            AddLabeledControl(grid, row, label, textBox);
        }

        private void AddLabeledControl(Grid grid, int row, string label, UIElement element)
        {
            TextBlock labelBlock = CreateTextBlock(label, Brushes.Gainsboro, FontWeights.Normal, 11);
            labelBlock.Margin = new Thickness(0, 1, 6, 1);
            AddControl(grid, labelBlock, row, 0, 1);

            AddControl(grid, element, row, 1, 1);
        }

        private TextBlock CreateTextBlock(string text, Brush brush, FontWeight weight, double fontSize)
        {
            TextBlock block = new TextBlock();
            block.Text = text;
            block.Foreground = brush;
            block.FontWeight = weight;
            block.FontSize = fontSize;
            block.Margin = new Thickness(0, 1, 0, 1);
            return block;
        }

        private TextBox CreateTextBox()
        {
            TextBox box = new TextBox();
            box.FontSize = 11;
            box.Margin = new Thickness(0, 1, 0, 1);
            box.Padding = new Thickness(2, 0, 2, 0);
            return box;
        }

        private ComboBox CreateComboBox()
        {
            ComboBox comboBox = new ComboBox();
            comboBox.FontSize = 11;
            comboBox.Margin = new Thickness(0, 1, 0, 1);
            comboBox.Padding = new Thickness(2, 0, 2, 0);
            return comboBox;
        }

        private CheckBox CreateCheckBox(string label, bool isChecked, RoutedEventHandler handler)
        {
            CheckBox checkBox = new CheckBox();
            checkBox.Content = label;
            checkBox.IsChecked = isChecked;
            checkBox.FontSize = 11;
            checkBox.Margin = new Thickness(0, 1, 0, 1);
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
            button.Margin = new Thickness(0, 1, 3, 1);
            button.Padding = new Thickness(3, 1, 3, 1);
            button.Height = 24;
            button.FontWeight = FontWeights.Bold;
            button.Foreground = Brushes.White;
            if (action == PanelAction.BuyMarket || action == PanelAction.BuyLimit || action == PanelAction.BuyStop)
                button.Background = new SolidColorBrush(Color.FromArgb(255, 30, 107, 46));
            else if (action == PanelAction.SellMarket || action == PanelAction.SellLimit || action == PanelAction.SellStop)
                button.Background = new SolidColorBrush(Color.FromArgb(255, 139, 30, 30));
            else if (action == PanelAction.Flatten)
                button.Background = new SolidColorBrush(Color.FromArgb(255, 179, 106, 0));
            else
                button.Background = Brushes.DimGray;

            ToolTipService.SetShowOnDisabled(button, true);
            button.Click += OrderButton_Click;
            orderButtons.Add(button);
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

        private void AddQuickEditor(Grid grid, int column, string label, out TextBox textBox, TextChangedEventHandler handler)
        {
            StackPanel panel = new StackPanel();
            panel.Margin = new Thickness(column > 0 ? 2 : 0, 0, column < 2 ? 2 : 0, 0);

            TextBlock labelBlock = CreateTextBlock(label, Brushes.Gainsboro, FontWeights.Normal, 11);
            labelBlock.Margin = new Thickness(0, 0, 0, 0);
            panel.Children.Add(labelBlock);

            textBox = CreateTextBox();
            textBox.Width = 66;
            panel.Children.Add(textBox);
            HookTextBox(textBox, handler);

            Grid.SetColumn(panel, column);
            grid.Children.Add(panel);
        }

        private void ApplyPanelCornerAlignment(Grid grid)
        {
            if (grid == null)
                return;

            switch (PanelPosition)
            {
                case AtrPanelCorner.TopLeft:
                    grid.HorizontalAlignment = HorizontalAlignment.Left;
                    grid.VerticalAlignment = VerticalAlignment.Top;
                    break;
                case AtrPanelCorner.BottomLeft:
                    grid.HorizontalAlignment = HorizontalAlignment.Left;
                    grid.VerticalAlignment = VerticalAlignment.Bottom;
                    break;
                case AtrPanelCorner.BottomRight:
                    grid.HorizontalAlignment = HorizontalAlignment.Right;
                    grid.VerticalAlignment = VerticalAlignment.Bottom;
                    break;
                default:
                    grid.HorizontalAlignment = HorizontalAlignment.Right;
                    grid.VerticalAlignment = VerticalAlignment.Top;
                    break;
            }
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
                SetTextIfIdle(atrMultiplierQuickTextBox, AtrMultiplier.ToString("0.00", CultureInfo.InvariantCulture));
                SetTextIfIdle(rewardRiskQuickTextBox, RewardRisk.ToString("0.00", CultureInfo.InvariantCulture));
                SetTextIfIdle(maxLossQuickTextBox, MaxLossPerTrade.ToString("0.00", CultureInfo.InvariantCulture));
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
                if (undersizedRiskModeComboBox != null)
                    undersizedRiskModeComboBox.SelectedItem = UndersizedRiskMode;
                if (showPlanLinesCheckBox != null)
                    showPlanLinesCheckBox.IsChecked = ShowPlanLines;
                if (useAtrAtFillCheckBox != null)
                    useAtrAtFillCheckBox.IsChecked = UseAtrAtFillForPendingOrders;
                if (allowMultipleEntriesCheckBox != null)
                    allowMultipleEntriesCheckBox.IsChecked = AllowMultipleEntries;
                if (settingsExpander != null)
                    settingsExpander.IsExpanded = settingsExpandedState;

                ApplyPanelCornerAlignment(panelGrid);
                if (panelTransform != null)
                {
                    panelTransform.X = panelDragX;
                    panelTransform.Y = panelDragY;
                }

                if (connectionStatusTextBlock != null)
                {
                    connectionStatusTextBlock.Text = State == State.Realtime ? "● REALTIME ENABLED" : "● " + State.ToString().ToUpperInvariant();
                    connectionStatusTextBlock.Foreground = State == State.Realtime ? Brushes.LimeGreen : Brushes.DarkOrange;
                }

                if (liveValuesTextBlock != null)
                {
                    liveValuesTextBlock.Text = string.Format(
                        "ATR {0:F2} | Stop {1:F2}p/{2:F0}t | Tgt {3:F2}p/{4:F0}t\nQty {5} | Risk/ct ${6:F2} | Rwd/ct ${7:F2} | RR {8:F2}\nTotal risk ${9:F2} | Total reward ${10:F2}",
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
                        lastCalculation.TotalReward);
                }

                UpdateRiskBanner();
                UpdatePanelCollapseVisualState();

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
            if (orderButtons == null || orderButtons.Count == 0)
                return;

            bool canTrade = State == State.Realtime && lastCalculation.Quantity > 0;
            string disabledReason = GetEntryDisabledReason();
            for (int i = 0; i < orderButtons.Count; i++)
            {
                Button button = orderButtons[i];
                if (button == null)
                    continue;

                PanelAction action = (PanelAction)button.Tag;
                bool enabled;
                if (action == PanelAction.Flatten || action == PanelAction.CancelPending)
                {
                    enabled = State == State.Realtime;
                    button.ToolTip = State == State.Realtime ? null : "Waiting for realtime — buttons enable when the strategy is live.";
                }
                else
                {
                    enabled = canTrade;
                    button.ToolTip = enabled ? null : disabledReason;
                }

                button.IsEnabled = enabled;
                button.Opacity = enabled ? 1.0 : 0.45;
            }
        }

        private string GetEntryDisabledReason()
        {
            if (State != State.Realtime)
                return "Waiting for realtime — buttons enable when the strategy is live.";

            if (lastCalculation.Quantity < 1 && lastCalculation.IsRiskTooSmall)
                return BuildNoSizeMessage(lastCalculation);

            return "Entry unavailable with current settings.";
        }

        private void UpdateRiskBanner()
        {
            if (riskBannerTextBlock == null)
                return;

            if (State != State.Realtime)
            {
                riskBannerTextBlock.Visibility = Visibility.Visible;
                riskBannerTextBlock.Background = new SolidColorBrush(Color.FromArgb(230, 88, 63, 12));
                riskBannerTextBlock.Foreground = Brushes.White;
                riskBannerTextBlock.Text = "Waiting for realtime — buttons enable when the strategy is live.";
                return;
            }

            if (lastCalculation.StopCappedByRisk)
            {
                riskBannerTextBlock.Visibility = Visibility.Visible;
                riskBannerTextBlock.Background = new SolidColorBrush(Color.FromArgb(220, 138, 103, 0));
                riskBannerTextBlock.Foreground = Brushes.White;
                riskBannerTextBlock.Text = string.Format(
                    "STOP CAPPED BY RISK: ATR stop {0:F2} pts \u2192 {1:F2} pts ({2:F0}t) to fit 1 ct in ${3:F2}.",
                    lastCalculation.AtrStopDistancePoints,
                    lastCalculation.StopDistancePoints,
                    lastCalculation.StopTicks,
                    MaxLossPerTrade);
                return;
            }

            if (lastCalculation.IsRiskOvershoot)
            {
                riskBannerTextBlock.Visibility = Visibility.Visible;
                riskBannerTextBlock.Background = new SolidColorBrush(Color.FromArgb(230, 96, 24, 24));
                riskBannerTextBlock.Foreground = Brushes.White;
                if (lastCalculation.Quantity <= 1)
                {
                    riskBannerTextBlock.Text = string.Format(
                        "RISK OVERSHOOT: 1 ct risks ${0:F2} vs Max Loss ${1:F2}.",
                        lastCalculation.TrueRiskPerContract,
                        MaxLossPerTrade);
                }
                else
                {
                    riskBannerTextBlock.Text = string.Format(
                        "RISK OVERSHOOT: {0} ct risk total ${1:F2} vs Max Loss ${2:F2}.",
                        lastCalculation.Quantity,
                        lastCalculation.TotalRisk,
                        MaxLossPerTrade);
                }
                return;
            }

            if (lastCalculation.Quantity < 1 && lastCalculation.IsRiskTooSmall && lastCalculation.HasEnoughBars)
            {
                riskBannerTextBlock.Visibility = Visibility.Visible;
                riskBannerTextBlock.Background = new SolidColorBrush(Color.FromArgb(230, 96, 24, 24));
                riskBannerTextBlock.Foreground = Brushes.White;
                riskBannerTextBlock.Text = BuildNoSizeMessage(lastCalculation);
                return;
            }

            riskBannerTextBlock.Visibility = Visibility.Collapsed;
            riskBannerTextBlock.Text = string.Empty;
        }

        private string BuildNoSizeMessage(CalculationResult calc)
        {
            string maxLossText = string.Format("${0:F0}", calc.RequiredMaxLossForOneContract);
            string multiplierText = calc.SuggestedMaxAtrMultiplier > 0
                ? calc.SuggestedMaxAtrMultiplier.ToString("0.00", CultureInfo.InvariantCulture)
                : "0.00";

            string microText = string.Empty;
            if (!string.IsNullOrEmpty(calc.SuggestedMicroSymbol))
                microText = "trade " + calc.SuggestedMicroSymbol + ", ";

            return string.Format(
                "NO SIZE: 1 contract risks ${0:F2} > Max Loss ${1:F2}. Options: {2}raise Max Loss to ≥ {3}, lower ATR Mult to ≤ {4}, or set Undersized Risk Mode = ShrinkStopToFit.",
                calc.TrueRiskPerContract,
                MaxLossPerTrade,
                microText,
                maxLossText,
                multiplierText);
        }

        private void UpdatePanelCollapseVisualState()
        {
            if (panelBodyGrid != null)
                panelBodyGrid.Visibility = panelCollapsed ? Visibility.Collapsed : Visibility.Visible;

            if (panelCollapseToggleButton != null)
                panelCollapseToggleButton.Content = panelCollapsed ? "▲" : "▼";
        }

        private void PanelCollapseToggleButton_Click(object sender, RoutedEventArgs e)
        {
            panelCollapsed = !panelCollapsed;
            UpdatePanelCollapseVisualState();
        }

        private void PanelHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (panelGrid == null || panelHostChartControl == null)
                return;

            isDraggingPanel = true;
            dragStartPoint = e.GetPosition(panelHostChartControl);
            panelGrid.CaptureMouse();
        }

        private void PanelHeader_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isDraggingPanel || panelGrid == null || panelHostChartControl == null || panelTransform == null)
                return;

            Point currentPoint = e.GetPosition(panelHostChartControl);
            Vector delta = currentPoint - dragStartPoint;
            panelDragX += delta.X;
            panelDragY += delta.Y;
            panelTransform.X = panelDragX;
            panelTransform.Y = panelDragY;
            dragStartPoint = currentPoint;
        }

        private void PanelHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!isDraggingPanel || panelGrid == null)
                return;

            isDraggingPanel = false;
            panelGrid.ReleaseMouseCapture();
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
                if (atrMultiplierQuickTextBox != null)
                    atrMultiplierQuickTextBox.TextChanged -= AtrMultiplierTextBox_TextChanged;
                if (rewardRiskQuickTextBox != null)
                    rewardRiskQuickTextBox.TextChanged -= RewardRiskTextBox_TextChanged;
                if (maxLossQuickTextBox != null)
                    maxLossQuickTextBox.TextChanged -= MaxLossTextBox_TextChanged;
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
                if (undersizedRiskModeComboBox != null)
                    undersizedRiskModeComboBox.SelectionChanged -= UndersizedRiskModeComboBox_SelectionChanged;
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
                if (settingsExpander != null)
                {
                    settingsExpander.Expanded -= SettingsExpander_Expanded;
                    settingsExpander.Collapsed -= SettingsExpander_Collapsed;
                }
                if (panelCollapseToggleButton != null)
                    panelCollapseToggleButton.Click -= PanelCollapseToggleButton_Click;

                if (panelHeaderGrid != null)
                {
                    panelHeaderGrid.MouseMove -= PanelHeader_MouseMove;
                    panelHeaderGrid.MouseLeftButtonDown -= PanelHeader_MouseLeftButtonDown;
                    panelHeaderGrid.MouseLeftButtonUp -= PanelHeader_MouseLeftButtonUp;
                }
                if (panelGrid != null)
                {
                    panelGrid.ReleaseMouseCapture();
                    UserControlCollection.Remove(panelGrid);
                }

                panelGrid = null;
                panelBodyGrid = null;
                panelHeaderGrid = null;
                panelBorder = null;
                panelTransform = null;
                orderButtons.Clear();
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

        private void UndersizedRiskModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isPanelUpdating || undersizedRiskModeComboBox == null || undersizedRiskModeComboBox.SelectedItem == null)
                return;

            AtrUndersizedRiskMode selected = (AtrUndersizedRiskMode)undersizedRiskModeComboBox.SelectedItem;
            TriggerCustomEvent(delegate(object o)
            {
                UndersizedRiskMode = selected;
                UpdateControlPanelAsync();
            }, null);
        }

        private void SettingsExpander_Expanded(object sender, RoutedEventArgs e)
        {
            if (isPanelUpdating)
                return;

            settingsExpandedState = true;
        }

        private void SettingsExpander_Collapsed(object sender, RoutedEventArgs e)
        {
            if (isPanelUpdating)
                return;

            settingsExpandedState = false;
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

            TextBox source = sender as TextBox;
            string text = source != null ? source.Text : (atrMultiplierTextBox != null ? atrMultiplierTextBox.Text : string.Empty);
            double value;
            if (!TryParseDouble(text, out value) || value <= 0)
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

            TextBox source = sender as TextBox;
            string text = source != null ? source.Text : (rewardRiskTextBox != null ? rewardRiskTextBox.Text : string.Empty);
            double value;
            if (!TryParseDouble(text, out value) || value <= 0)
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

            TextBox source = sender as TextBox;
            string text = source != null ? source.Text : (maxLossTextBox != null ? maxLossTextBox.Text : string.Empty);
            double value;
            if (!TryParseDouble(text, out value) || value <= 0)
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

        private string TryGetMicroSuggestionSymbol()
        {
            if (Instrument == null || Instrument.MasterInstrument == null || string.IsNullOrWhiteSpace(Instrument.MasterInstrument.Name))
                return string.Empty;

            string name = Instrument.MasterInstrument.Name.ToUpperInvariant();
            switch (name)
            {
                case "ES":
                    return "MES";
                case "NQ":
                    return "MNQ";
                case "YM":
                    return "MYM";
                case "RTY":
                    return "M2K";
                case "CL":
                    return "MCL";
                case "GC":
                    return "MGC";
                default:
                    return string.Empty;
            }
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
        [Display(Name = "Undersized Risk Mode", GroupName = "3. Sizing", Order = 3)]
        public AtrUndersizedRiskMode UndersizedRiskMode { get; set; }

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
        [Display(Name = "Settings Expanded By Default", GroupName = "4. Panel", Order = 4)]
        public bool SettingsExpandedByDefault { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Panel Position", GroupName = "4. Panel", Order = 5)]
        public AtrPanelCorner PanelPosition { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Exit On Session Close", GroupName = "5. Safety", Order = 1)]
        public bool ExitOnSessionClose { get; set; }
    }
}
