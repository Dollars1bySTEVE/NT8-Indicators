// FibonacciStructureEngine.cs
// NinjaTrader 8 port of "Fibonacci Structure Engine [WillyAlgoTrader]" (TradingView)
// GPU-enhanced SharpDX rendering | v1.6.0-NT8

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.Core.FloatingPoint;

// Enums OUTSIDE namespace — required for NT8 compiler
public enum FibStructDashboardPosition { Hidden, TopLeft, TopRight, BottomLeft, BottomRight, CenterTop, CenterBottom }
public enum FibStructTheme { Auto, Dark, Light }
public enum FibStructLineStyle { Solid, Dashed, Dotted }

namespace NinjaTrader.NinjaScript.Indicators
{
    public class FibonacciStructureEngine : Indicator
    {
        #region Private Data Classes
        private class SwingPoint  { public int BarIndex; public double Price; public bool IsHigh; }
        private class BosEvent    { public int EventId; public int StartBarIdx; public int EndBarIdx; public double Price; public bool IsBull; public bool IsBos; public bool Rendered; }
        private class EqLevel     { public int StartBarIdx; public int EndBarIdx; public double Price; public bool IsHigh; public bool Active; public bool Swept; }
        private class SweepEvent  { public int BarIndex; public double Price; public bool IsHigh; }
        private class SignalEvent  { public int BarIndex; public double Price; public bool IsBuy; public string Trigger; }
        private class SwingLabel  { public int BarIndex; public double Price; public bool IsHigh; public string Label; }
        #endregion

        #region State Machine Fields
        // Swing tracking
        private double swHigh1, swHigh2;
        private int    swHigh1Idx, swHigh2Idx;
        private double swLow1, swLow2;
        private int    swLow1Idx, swLow2Idx;

        // Structure
        private int  structureBias;        // 1=bull, -1=bear, 0=neutral
        private int  lastBrokenHighIdx;
        private int  lastBrokenLowIdx;
        private int  lastChochDir;

        // Fibonacci engine
        private double fibSwingHigh, fibSwingLow;
        private int    fibSwingHighIdx, fibSwingLowIdx;
        private int    fibDirection;       // 1=bull, -1=bear, 0=none
        private bool   fibHighIsLive, fibLowIsLive;

        // EQH/EQL
        private EqLevel activeEqh, activeEql;

        // Sweep tracking
        private HashSet<int> sweptHighAnchors;
        private HashSet<int> sweptLowAnchors;
        private bool lastSweepWasHigh;

        // Signal
        private int barsSinceLastSignal;

        // Warmup
        private int warmupBars;
        private bool isWarmedUp;

        // ATR
        private ATR atrSeries;

        // Premium/discount / confluence
        private bool inPremium, inDiscount;
        private double confluenceRawWeight;  // raw weight used for setup comparisons
        private double confluenceScore;      // 0–100 score used for display and labels
        private string confluenceLabel;
        private double nearestFibLevel;

        // Body EMA for full engulfing logic
        private double bodyEma;
        private double prevBodyEma;

        // Fib engine event-tracking (process each structure event exactly once)
        private int nextEventId;
        private int lastProcessedEventId;

        // Confirmed-bar gate (prevents intrabar re-fire of structure events)
        private int lastConfirmedBar;

        // Flags set by DetectSwings on each confirmed bar; consumed by UpdateFibEngineConfirmed
        private bool detectedNewSwingHigh;
        private bool detectedNewSwingLow;

        // Dashboard state (for render thread)
        private string dashTrend, dashFibDir, dashSignal, dashConfluence, dashZone, dashLiquidity, dashFib618, dashATR, dashNearFib, dashTF;

        // Alert throttle
        private int lastBuyAlertBar, lastSellAlertBar, lastBosAlertBar, lastChochAlertBar, lastSweepHAlertBar, lastSweepLAlertBar;

        // Event lists
        private List<BosEvent>    bosEvents;
        private List<SweepEvent>  sweepEvents;
        private List<SignalEvent> signalEvents;
        private List<EqLevel>     eqhLevels, eqlLevels;
        private List<SwingLabel>  swingLabels;
        #endregion

        #region SharpDX Resource Fields
        private bool dxReady;
        // Brushes
        private SharpDX.Direct2D1.SolidColorBrush dxBullBrush, dxBearBrush, dxFibBrush, dxConfBrush, dxEqBrush, dxSweepBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxGoldenZoneBrush, dxTargetZoneBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxTextBrush, dxMutedBrush, dxDashBgBrush, dxDashBorderBrush, dxDashHeaderBrush;
        // Stroke styles
        private SharpDX.Direct2D1.StrokeStyle dxSolidStyle, dxDashStyle, dxDotStyle;
        // Text formats
        private SharpDX.DirectWrite.TextFormat dxLabelFmt, dxDashFmt, dxDashHdrFmt, dxWatermarkFmt;
        private SharpDX.DirectWrite.Factory dxWriteFactory;
        #endregion

        #region OnStateChange
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "NT8 port of Fibonacci Structure Engine [WillyAlgoTrader] v1.6.0-NT8";
                Name        = "FibonacciStructureEngine";
                Calculate   = Calculate.OnPriceChange;
                IsOverlay   = true;
                IsAutoScale = false;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = true;

                // 01. Main Settings
                SwingDetectionLength   = 10;
                UseAtrSwingFilter      = true;
                AtrFilterMultiplier    = 0.5;
                SignalCooldown         = 5;

                // 02. Fibonacci Levels
                ShowFibLevels          = true;
                FibExtensionBars       = 20;
                ShowFib0236            = false;
                ShowFib0382            = true;
                ShowFib0500            = true;
                ShowFib0618            = true;
                ShowFib0786            = true;
                ShowTargetN0618        = true;
                ShowTargetN050         = true;
                ConfluenceAtrTolerance = 0.3;

                // 03. Structure
                ShowBosCHoCH       = true;
                ShowSwingLabels    = true;
                ShowEngulfing      = true;
                StrictEngulfing    = true;
                StructureLineStyle = FibStructLineStyle.Dashed;
                StructureLineWidth = 2;

                // 04. Liquidity
                ShowEqhEql            = true;
                EqAtrTolerance        = 0.1;
                EqLineExtensionBars   = 50;
                ShowSweeps            = true;
                SweepsBoostConfluence = true;

                // 05. Signals
                ShowBuySellSignals = false;

                // 06. Dashboard
                ShowDashboard       = true;
                DashboardPosition   = FibStructDashboardPosition.TopLeft;
                DashboardOpacity    = 85;
                DashboardFontSize   = 10f;
                DashboardWidth      = 220;
                DashRowHeight       = 17;

                // 07. Alerts
                EnableAlerts       = false;
                AlertOnBuy         = true;
                AlertOnSell        = true;
                AlertOnBos         = false;
                AlertOnChoch       = true;
                AlertOnSweepHigh   = false;
                AlertOnSweepLow    = false;
                AlertPopup         = true;
                AlertSound         = false;
                AlertSoundFile     = "Alert4.wav";
                AlertEmail         = false;
                AlertEmailAddress  = "";
                WebhookJsonFormat  = false;

                // 08. Colors
                BullColor       = (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xE6, 0x76));
                BearColor       = (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x52, 0x52));
                FibLineColor    = (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x42, 0xA5, 0xF5));
                ConfluenceColor = (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD6, 0x00));
                EqColor         = (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB3, 0x88, 0xFF));
                SweepColor      = (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x91, 0x00));

                // 09. Visual / Rendering
                Theme          = FibStructTheme.Auto;
                ShowWatermark  = true;
                FibLineOpacity = 70;
                ZoneOpacity    = 20;
                LineOpacity    = 90;
                MarkerSize     = 8f;

                // 10. Performance
                MaxBosEvents    = 30;
                MaxSweepEvents  = 50;
                MaxSignalEvents = 50;
                MaxEqLevels     = 20;
                MaxSwingLabels  = 50;
            }
            else if (State == State.DataLoaded)
            {
                // Initialize all lists
                bosEvents    = new List<BosEvent>();
                sweepEvents  = new List<SweepEvent>();
                signalEvents = new List<SignalEvent>();
                eqhLevels    = new List<EqLevel>();
                eqlLevels    = new List<EqLevel>();
                swingLabels  = new List<SwingLabel>();
                sweptHighAnchors = new HashSet<int>();
                sweptLowAnchors  = new HashSet<int>();

                // Reset state
                swHigh1 = swHigh2 = swLow1 = swLow2 = 0;
                swHigh1Idx = swHigh2Idx = swLow1Idx = swLow2Idx = -1;
                structureBias = 0;
                lastBrokenHighIdx = -1;
                lastBrokenLowIdx  = -1;
                lastChochDir = 0;
                fibSwingHigh = fibSwingLow = 0;
                fibSwingHighIdx = fibSwingLowIdx = -1;
                fibDirection = 0;
                fibHighIsLive = fibLowIsLive = false;
                activeEqh = null;
                activeEql = null;
                barsSinceLastSignal = 999;
                warmupBars = Math.Max(SwingDetectionLength * 3, 50);
                lastBuyAlertBar = lastSellAlertBar = lastBosAlertBar = lastChochAlertBar = lastSweepHAlertBar = lastSweepLAlertBar = -999;
                dashSignal = "—";
                bodyEma = 0;
                prevBodyEma = 0;
                nextEventId = 0;
                lastProcessedEventId = -1;
                lastConfirmedBar = -1;
                detectedNewSwingHigh = false;
                detectedNewSwingLow  = false;
                confluenceRawWeight = 0;
                confluenceScore = 0;

                atrSeries = ATR(14);
            }
            else if (State == State.Terminated)
            {
                DisposeDXResources();
            }
        }
        #endregion

        #region OnBarUpdate
        protected override void OnBarUpdate()
        {
            if (bosEvents == null) return;
            if (CurrentBar < SwingDetectionLength * 2 + 1) return;

            isWarmedUp = CurrentBar >= warmupBars;
            barsSinceLastSignal++;

            double atr = (atrSeries != null && CurrentBar >= 14) ? atrSeries[0] : TickSize * 10;
            if (atr <= 0 || double.IsNaN(atr)) atr = TickSize * 10;

            // Determine whether this update represents a confirmed (closed) bar.
            // Historical: every call is a fully confirmed bar (index 0 = the bar just closed).
            // Realtime:   IsFirstTickOfBar fires on the first tick of a new bar, meaning
            //             index 1 = the bar that just closed.
            bool isNewConfirmedBar = (CurrentBar > lastConfirmedBar) &&
                                     (State == State.Historical || IsFirstTickOfBar);
            // Confirmed bar offset: 0 during historical, 1 during realtime IsFirstTickOfBar
            int co = (State == State.Historical) ? 0 : (isNewConfirmedBar ? 1 : -1);

            // 1. EQH/EQL detection (always — logic is independent of visual toggle)
            DetectEqualLevels(atr);

            if (isNewConfirmedBar)
            {
                lastConfirmedBar = CurrentBar;

                // Update body EMA for engulfing on confirmed bar
                double body = Math.Abs(Close[co] - Open[co]);
                prevBodyEma = bodyEma;
                // EMA(14): alpha = 2 / (period + 1) = 2 / (14 + 1) = 2/15 ≈ 0.1333
                double alpha = 2.0 / 15.0;
                bodyEma = (bodyEma <= 0) ? body : alpha * body + (1.0 - alpha) * bodyEma;

                // 2. Swing detection — closed-bar-only (Pine ta.pivothigh/low only confirms
                //    after all right-side bars are closed; never confirm intrabar)
                DetectSwings(atr, co);

                // 3. Liquidity sweeps (confirmed bar, independent of visual toggle)
                DetectSweeps(atr, co);

                // 4. BOS/CHoCH structure (confirmed bar)
                DetectStructureBreaks(co);

                // 5. Fibonacci engine — process new structure events and lock/update anchors
                UpdateFibEngineConfirmed();

                // 6. Confluence and premium/discount for the same confirmed bar.
                //    These must run BEFORE signal evaluation so signals use current values.
                UpdateConfluence(atr, co);
                UpdatePremiumDiscount(co);

                // 7. Engulf/sweep/CHoCH signals evaluated with current confluence/zone values.
                // dashSignal is reset to "—" here; it retains its value during the forming bar
                // that follows, which is intentional (signal stays visible for one bar after
                // confirmation, matching Pine's bar-persistent plotting). It resets again at
                // the start of the next confirmed-bar cycle.
                dashSignal = "—";
                DetectSignals(atr, co);

                // 8. Dashboard state snapshot for the confirmed bar
                UpdateDashboardState(atr);
            }

            // Live fib edge trailing (visual only — runs every tick, no anchor changes)
            UpdateFibEdgeLive();

            // Re-compute confluence/premium with the live forming bar for dashboard display.
            // This overwrites the confirmed-bar values used for signals above, which is fine
            // because signal evaluation already completed inside the confirmed-bar block.
            UpdateConfluence(atr, 0);
            UpdatePremiumDiscount(0);

            // Dashboard refresh (picks up live confluence/zone for the forming bar)
            UpdateDashboardState(atr);

            // 9. Trim lists to performance limits
            TrimLists();
        }
        #endregion

        #region Swing Detection
        // DetectSwings is called only when isNewConfirmedBar is true.
        // offset = 0 (historical) or 1 (realtime IsFirstTickOfBar).
        // confirmedCur = CurrentBar - offset is the just-closed bar; the pivot-high/low
        // loop never looks beyond confirmedCur, matching Pine ta.pivothigh/low semantics
        // where all right-side bars must be fully closed before a pivot is confirmed.
        private void DetectSwings(double atr, int offset)
        {
            int len = SwingDetectionLength;
            int confirmedCur = CurrentBar - offset;
            int pivotBar = confirmedCur - len;
            if (pivotBar < len) return;

            detectedNewSwingHigh = false;
            detectedNewSwingLow  = false;

            // Check pivot high
            double pivotHigh = High.GetValueAt(pivotBar);
            bool isHigh = true;
            for (int i = pivotBar - len; i <= pivotBar + len; i++)
            {
                if (i == pivotBar) continue;
                if (i < 0 || i > confirmedCur) continue;   // right side: confirmed bars only
                if (High.GetValueAt(i) >= pivotHigh) { isHigh = false; break; }
            }

            if (isHigh)
            {
                bool passes = !UseAtrSwingFilter || swLow1 <= 0 || (pivotHigh - swLow1 >= atr * AtrFilterMultiplier);
                if (passes && pivotBar != swHigh1Idx)
                {
                    swHigh2    = swHigh1;
                    swHigh2Idx = swHigh1Idx;
                    swHigh1    = pivotHigh;
                    swHigh1Idx = pivotBar;
                    detectedNewSwingHigh = true;

                    if (ShowSwingLabels && isWarmedUp)
                    {
                        string lbl = (swHigh2 > 0 && swHigh1 > swHigh2) ? "HH" : (swHigh2 > 0 ? "LH" : "SH");
                        AddSwingLabel(pivotBar, pivotHigh, true, lbl);
                    }
                }
            }

            // Check pivot low
            double pivotLow = Low.GetValueAt(pivotBar);
            bool isLow = true;
            for (int i = pivotBar - len; i <= pivotBar + len; i++)
            {
                if (i == pivotBar) continue;
                if (i < 0 || i > confirmedCur) continue;   // right side: confirmed bars only
                if (Low.GetValueAt(i) <= pivotLow) { isLow = false; break; }
            }

            if (isLow)
            {
                bool passes = !UseAtrSwingFilter || swHigh1 <= 0 || (swHigh1 - pivotLow >= atr * AtrFilterMultiplier);
                if (passes && pivotBar != swLow1Idx)
                {
                    swLow2    = swLow1;
                    swLow2Idx = swLow1Idx;
                    swLow1    = pivotLow;
                    swLow1Idx = pivotBar;
                    detectedNewSwingLow = true;

                    if (ShowSwingLabels && isWarmedUp)
                    {
                        string lbl = (swLow2 > 0 && swLow1 < swLow2) ? "LL" : (swLow2 > 0 ? "HL" : "SL");
                        AddSwingLabel(pivotBar, pivotLow, false, lbl);
                    }
                }
            }
        }

        private void AddSwingLabel(int barIdx, double price, bool isHigh, string lbl)
        {
            swingLabels.Add(new SwingLabel { BarIndex = barIdx, Price = price, IsHigh = isHigh, Label = lbl });
            if (swingLabels.Count > MaxSwingLabels) swingLabels.RemoveAt(0);
        }
        #endregion

        #region Equal Levels (EQH/EQL)
        private void DetectEqualLevels(double atr)
        {
            // Detection always runs (visual toggle only affects rendering).
            double tol = atr * EqAtrTolerance;

            // EQH: two consecutive swing highs within tolerance
            if (swHigh1Idx > 0 && swHigh2Idx > 0 && Math.Abs(swHigh1 - swHigh2) <= tol)
            {
                if (activeEqh == null || activeEqh.StartBarIdx != swHigh2Idx)
                {
                    activeEqh = new EqLevel
                    {
                        StartBarIdx = swHigh2Idx,
                        EndBarIdx   = CurrentBar,
                        Price       = (swHigh1 + swHigh2) / 2.0,
                        IsHigh      = true,
                        Active      = true,
                        Swept       = false
                    };
                    eqhLevels.Add(activeEqh);
                    if (eqhLevels.Count > MaxEqLevels) eqhLevels.RemoveAt(0);
                }
            }

            // EQL: two consecutive swing lows within tolerance
            if (swLow1Idx > 0 && swLow2Idx > 0 && Math.Abs(swLow1 - swLow2) <= tol)
            {
                if (activeEql == null || activeEql.StartBarIdx != swLow2Idx)
                {
                    activeEql = new EqLevel
                    {
                        StartBarIdx = swLow2Idx,
                        EndBarIdx   = CurrentBar,
                        Price       = (swLow1 + swLow2) / 2.0,
                        IsHigh      = false,
                        Active      = true,
                        Swept       = false
                    };
                    eqlLevels.Add(activeEql);
                    if (eqlLevels.Count > MaxEqLevels) eqlLevels.RemoveAt(0);
                }
            }

            // Update end bars on active levels
            if (activeEqh != null && activeEqh.Active) activeEqh.EndBarIdx = CurrentBar;
            if (activeEql != null && activeEql.Active) activeEql.EndBarIdx = CurrentBar;
        }
        #endregion

        #region Sweeps
        private void DetectSweeps(double atr, int offset)
        {
            // Detection always runs (visual toggle only affects rendering).
            int sweepBar = CurrentBar - offset;

            // High sweep reference
            double refHigh       = (activeEqh != null && activeEqh.Active) ? activeEqh.Price : swHigh1;
            int    refHighAnchor = (activeEqh != null && activeEqh.Active) ? activeEqh.StartBarIdx : swHigh1Idx;

            if (refHigh > 0 && refHighAnchor >= 0 && !sweptHighAnchors.Contains(refHighAnchor))
            {
                if (High[offset] > refHigh && Close[offset] < refHigh && Open[offset] < refHigh)
                {
                    sweptHighAnchors.Add(refHighAnchor);
                    lastSweepWasHigh = true;
                    if (activeEqh != null && activeEqh.Active && activeEqh.StartBarIdx == refHighAnchor)
                    {
                        activeEqh.Active  = false;
                        activeEqh.Swept   = true;
                        activeEqh.EndBarIdx = sweepBar;
                    }
                    sweepEvents.Add(new SweepEvent { BarIndex = sweepBar, Price = refHigh, IsHigh = true });
                    if (sweepEvents.Count > MaxSweepEvents) sweepEvents.RemoveAt(0);
                    FireSweepAlert(true, sweepBar, Close[offset]);
                }
            }

            // Low sweep reference
            double refLow       = (activeEql != null && activeEql.Active) ? activeEql.Price : swLow1;
            int    refLowAnchor = (activeEql != null && activeEql.Active) ? activeEql.StartBarIdx : swLow1Idx;

            if (refLow > 0 && refLowAnchor >= 0 && !sweptLowAnchors.Contains(refLowAnchor))
            {
                if (Low[offset] < refLow && Close[offset] > refLow && Open[offset] > refLow)
                {
                    sweptLowAnchors.Add(refLowAnchor);
                    lastSweepWasHigh = false;
                    if (activeEql != null && activeEql.Active && activeEql.StartBarIdx == refLowAnchor)
                    {
                        activeEql.Active  = false;
                        activeEql.Swept   = true;
                        activeEql.EndBarIdx = sweepBar;
                    }
                    sweepEvents.Add(new SweepEvent { BarIndex = sweepBar, Price = refLow, IsHigh = false });
                    if (sweepEvents.Count > MaxSweepEvents) sweepEvents.RemoveAt(0);
                    FireSweepAlert(false, sweepBar, Close[offset]);
                }
            }
        }
        #endregion

        #region Structure Breaks (BOS/CHoCH)
        private void DetectStructureBreaks(int offset)
        {
            if (!isWarmedUp) return;
            int eventBar = CurrentBar - offset;

            bool bullBreak = swHigh1 > 0 && swHigh1Idx >= 0 && Close[offset] > swHigh1 && swHigh1Idx != lastBrokenHighIdx;
            bool bearBreak = swLow1  > 0 && swLow1Idx  >= 0 && Close[offset] < swLow1  && swLow1Idx  != lastBrokenLowIdx;

            // Conflict resolution
            if (bullBreak && bearBreak)
            {
                if (structureBias <= 0) bearBreak = false;
                else                    bullBreak = false;
            }

            if (bullBreak)
            {
                bool isBos = structureBias > 0;
                structureBias     = 1;
                lastBrokenHighIdx = swHigh1Idx;

                if (lastChochDir != 1)
                {
                    // CHoCH direction flip — reset BOTH sweep trackers (Pine: lastSweptHighIdx := na; lastSweptLowIdx := na)
                    lastChochDir = 1;
                    sweptHighAnchors.Clear();
                    sweptLowAnchors.Clear();
                }

                var ev = new BosEvent { EventId = nextEventId++, StartBarIdx = swHigh1Idx, EndBarIdx = eventBar, Price = swHigh1, IsBull = true, IsBos = isBos };
                bosEvents.Add(ev);
                if (bosEvents.Count > MaxBosEvents) bosEvents.RemoveAt(0);

                FireBosChochAlert(true, isBos, eventBar, Close[offset]);
            }

            if (bearBreak)
            {
                bool isBos = structureBias < 0;
                structureBias    = -1;
                lastBrokenLowIdx = swLow1Idx;

                if (lastChochDir != -1)
                {
                    // CHoCH direction flip — reset BOTH sweep trackers
                    lastChochDir = -1;
                    sweptHighAnchors.Clear();
                    sweptLowAnchors.Clear();
                }

                var ev = new BosEvent { EventId = nextEventId++, StartBarIdx = swLow1Idx, EndBarIdx = eventBar, Price = swLow1, IsBull = false, IsBos = isBos };
                bosEvents.Add(ev);
                if (bosEvents.Count > MaxBosEvents) bosEvents.RemoveAt(0);

                FireBosChochAlert(false, isBos, eventBar, Close[offset]);
            }
        }
        #endregion

        #region Fibonacci Engine
        // Confirmed-bar portion: process new structure events and lock/update anchors.
        // Called once per newly confirmed bar, after DetectSwings and DetectStructureBreaks.
        private void UpdateFibEngineConfirmed()
        {
            bool newEventProcessed = false;

            // Process new structure events exactly once per event (not every tick).
            if (bosEvents.Count > 0)
            {
                var latest = bosEvents[bosEvents.Count - 1];
                if (latest.EventId != lastProcessedEventId)
                {
                    lastProcessedEventId = latest.EventId;
                    newEventProcessed = true;

                    // breakBarOffset: how many bars ago the break bar is relative to CurrentBar
                    int breakBarOffset = CurrentBar - latest.EndBarIdx;

                    if (!latest.IsBos)
                    {
                        // CHoCH: flip fib direction
                        // Bullish CHoCH: fibDir=1; low anchors to latest swing low (locked),
                        //               high starts at break-bar high (live).
                        // Bearish CHoCH: fibDir=-1; high anchors to latest swing high (locked),
                        //               low starts at break-bar low (live).
                        fibDirection = latest.IsBull ? 1 : -1;

                        if (fibDirection == 1)
                        {
                            if (swLow1 > 0)
                            {
                                fibSwingLow    = swLow1;
                                fibSwingLowIdx = swLow1Idx;
                                fibLowIsLive   = false;
                            }
                            // Live high starts at the break bar's high (Pine-equivalent)
                            fibSwingHigh    = High[breakBarOffset];
                            fibSwingHighIdx = latest.EndBarIdx;
                            fibHighIsLive   = true;
                        }
                        else
                        {
                            if (swHigh1 > 0)
                            {
                                fibSwingHigh    = swHigh1;
                                fibSwingHighIdx = swHigh1Idx;
                                fibHighIsLive   = false;
                            }
                            // Live low starts at the break bar's low (Pine-equivalent)
                            fibSwingLow    = Low[breakBarOffset];
                            fibSwingLowIdx = latest.EndBarIdx;
                            fibLowIsLive   = true;
                        }
                    }
                    else
                    {
                        // BOS: preserve direction; advance the live anchor to the break bar
                        // high/low (Pine uses break bar, not swHigh1/swLow1 directly).
                        if (fibDirection == 1)
                        {
                            // Update locked low if a new confirmed swing low is available
                            if (swLow1 > 0 && !fibLowIsLive)
                            {
                                fibSwingLow    = swLow1;
                                fibSwingLowIdx = swLow1Idx;
                            }
                            // Live high advances to break bar's high
                            fibSwingHigh    = High[breakBarOffset];
                            fibSwingHighIdx = latest.EndBarIdx;
                            fibHighIsLive   = true;
                        }
                        else if (fibDirection == -1)
                        {
                            // Update locked high if a new confirmed swing high is available
                            if (swHigh1 > 0 && !fibHighIsLive)
                            {
                                fibSwingHigh    = swHigh1;
                                fibSwingHighIdx = swHigh1Idx;
                            }
                            // Live low advances to break bar's low
                            fibSwingLow    = Low[breakBarOffset];
                            fibSwingLowIdx = latest.EndBarIdx;
                            fibLowIsLive   = true;
                        }
                    }
                }
            }

            // Live edge locking: when a confirmed swing arrives while the live edge is active
            // (and no new structure event displaced it this same bar), lock the anchor.
            if (!newEventProcessed)
            {
                if (detectedNewSwingHigh && fibHighIsLive)
                {
                    fibSwingHigh    = swHigh1;
                    fibSwingHighIdx = swHigh1Idx;
                    fibHighIsLive   = false;
                }
                if (detectedNewSwingLow && fibLowIsLive)
                {
                    fibSwingLow    = swLow1;
                    fibSwingLowIdx = swLow1Idx;
                    fibLowIsLive   = false;
                }
            }

            // Locked anchor update: when a new confirmed swing arrives and the corresponding
            // anchor is already locked (not live), update it if the swing price changed.
            if (detectedNewSwingHigh && !fibHighIsLive && fibSwingHigh != swHigh1)
            {
                fibSwingHigh    = swHigh1;
                fibSwingHighIdx = swHigh1Idx;
            }
            if (detectedNewSwingLow && !fibLowIsLive && fibSwingLow != swLow1)
            {
                fibSwingLow    = swLow1;
                fibSwingLowIdx = swLow1Idx;
            }
        }

        // Intrabar live edge trailing (visual only — runs every tick, no anchor changes).
        private void UpdateFibEdgeLive()
        {
            if (fibHighIsLive && High[0] > fibSwingHigh)
            {
                fibSwingHigh    = High[0];
                fibSwingHighIdx = CurrentBar;
            }
            if (fibLowIsLive && Low[0] < fibSwingLow)
            {
                fibSwingLow    = Low[0];
                fibSwingLowIdx = CurrentBar;
            }
        }

        private double GetFibPrice(double ratio)
        {
            if (fibDirection == 1 && fibSwingHigh > 0 && fibSwingLow > 0)
                return fibSwingHigh - (fibSwingHigh - fibSwingLow) * ratio;
            if (fibDirection == -1 && fibSwingHigh > 0 && fibSwingLow > 0)
                return fibSwingLow + (fibSwingHigh - fibSwingLow) * ratio;
            return 0;
        }
        #endregion

        #region Confluence
        // offset = co (confirmed bar) for signal evaluation; offset = 0 for live dashboard.
        private void UpdateConfluence(double atr, int offset)
        {
            if (fibDirection == 0)
            {
                confluenceRawWeight = 0;
                confluenceScore     = 0;
                confluenceLabel     = "None";
                nearestFibLevel     = 0;
                return;
            }
            double tol = atr * ConfluenceAtrTolerance;
            if (tol <= 0 || double.IsNaN(tol)) tol = TickSize;

            double price = Close[offset];
            double rawWeight = 0;
            double nearestDist = double.MaxValue;
            nearestFibLevel = 0;

            // Pine-exact retracement scoring:
            //   0.236 → +1.0 only when ShowFib0236 is true (Pine uses showFib0236 guard)
            //   0.382 → +1.5 regardless of display toggle
            //   0.500 → +2.0 regardless of display toggle
            //   0.618 → +2.5 regardless of display toggle
            //   0.786 → +1.5 regardless of display toggle
            // Target levels (-0.5, -0.618) do NOT contribute to the score (Pine omits them).
            double[] scoreRatios  = { 0.236, 0.382, 0.500, 0.618, 0.786 };
            double[] scoreWeights = { 1.0,   1.5,   2.0,   2.5,   1.5   };
            bool[]   scoreGated   = { ShowFib0236, true, true, true, true };

            for (int i = 0; i < scoreRatios.Length; i++)
            {
                double fp = GetFibPrice(scoreRatios[i]);
                if (fp <= 0) continue;
                bool near = Math.Abs(price - fp) <= tol || (Low[offset] <= fp + tol && High[offset] >= fp - tol);
                if (near && scoreGated[i]) rawWeight += scoreWeights[i];
                double dist = Math.Abs(price - fp);
                if (dist < nearestDist) { nearestDist = dist; nearestFibLevel = scoreRatios[i]; }
            }

            // Target levels: scan for nearest-fib display only — no scoring weight added
            double[] displayRatios = { -0.5, -0.618 };
            foreach (double r in displayRatios)
            {
                double fp = GetFibPrice(r);
                if (fp <= 0) continue;
                double dist = Math.Abs(price - fp);
                if (dist < nearestDist) { nearestDist = dist; nearestFibLevel = r; }
            }

            // Latest swing high/low confluence: +1.0 each
            if (swHigh1 > 0 && (Math.Abs(price - swHigh1) <= tol || (Low[offset] <= swHigh1 + tol && High[offset] >= swHigh1 - tol))) rawWeight += 1.0;
            if (swLow1  > 0 && (Math.Abs(price - swLow1)  <= tol || (Low[offset] <= swLow1  + tol && High[offset] >= swLow1  - tol))) rawWeight += 1.0;

            // Current-bar sweep boost: +2.0 if enabled.
            // A sweep on the confirmed bar (distance 0) or the bar just before (distance 1)
            // qualifies so the boost applies correctly in both historical and realtime modes.
            int confirmedBar = CurrentBar - offset;
            bool recentSweep = SweepsBoostConfluence && sweepEvents.Count > 0 && (confirmedBar - sweepEvents[sweepEvents.Count - 1].BarIndex) <= 1;
            if (recentSweep) rawWeight += 2.0;

            confluenceRawWeight = rawWeight;
            confluenceScore     = Math.Min(rawWeight * 10.0, 100.0);
            confluenceLabel     = confluenceScore >= 60 ? "Strong" : confluenceScore >= 30 ? "Moderate" : confluenceScore > 0 ? "Weak" : "None";
        }
        #endregion

        #region Premium / Discount
        private void UpdatePremiumDiscount(int offset)
        {
            double midPrice = GetFibPrice(0.500);
            if (midPrice <= 0) { inPremium = false; inDiscount = false; return; }
            if (fibDirection == 1)  { inPremium = Close[offset] > midPrice; inDiscount = !inPremium; }
            else                    { inPremium = Close[offset] < midPrice; inDiscount = !inPremium; }
        }
        #endregion

        #region Engulfing Patterns
        // Full Pine-parity engulfing: checks body size, EMA body average, isLongBody, isSmallPrev, bodyIsBigger.
        private bool IsEngulfingBull(bool strict, int offset)
        {
            if (CurrentBar < offset + 1) return false;
            bool currBull  = Close[offset]     > Open[offset];
            bool prevBear  = Close[offset + 1] < Open[offset + 1];
            if (!currBull || !prevBear) return false;

            double body     = Math.Abs(Close[offset]     - Open[offset]);
            double priorBody = Math.Abs(Close[offset + 1] - Open[offset + 1]);

            // EMA body averages (running EMA updated each confirmed bar)
            double bodyAvg  = bodyEma     > 0 ? bodyEma     : body;
            double priorAvg = prevBodyEma > 0 ? prevBodyEma : priorBody;

            bool isLongBody   = body      > bodyAvg;
            bool isSmallPrev  = priorBody < priorAvg;
            bool bodyIsBigger = body      > priorBody;

            if (strict)
            {
                // Strict: current long, bigger than previous, previous small, full body engulf
                return isLongBody && bodyIsBigger && isSmallPrev
                    && Open[offset] < Close[offset + 1]
                    && Close[offset] > Open[offset + 1];
            }
            else
            {
                // Loose: require same body filters as strict; allow equality on one engulf side
                bool strictHigh = Close[offset]  > Open[offset + 1];
                bool strictLow  = Open[offset]   < Close[offset + 1];
                double currBodyH = Math.Max(Open[offset],     Close[offset]);
                double currBodyL = Math.Min(Open[offset],     Close[offset]);
                double prevBodyH = Math.Max(Open[offset + 1], Close[offset + 1]);
                double prevBodyL = Math.Min(Open[offset + 1], Close[offset + 1]);
                return isLongBody && isSmallPrev && bodyIsBigger && (strictHigh || strictLow)
                    && currBodyH >= prevBodyH && currBodyL <= prevBodyL;
            }
        }

        private bool IsEngulfingBear(bool strict, int offset)
        {
            if (CurrentBar < offset + 1) return false;
            bool currBear  = Close[offset]     < Open[offset];
            bool prevBull  = Close[offset + 1] > Open[offset + 1];
            if (!currBear || !prevBull) return false;

            double body     = Math.Abs(Close[offset]     - Open[offset]);
            double priorBody = Math.Abs(Close[offset + 1] - Open[offset + 1]);

            double bodyAvg  = bodyEma     > 0 ? bodyEma     : body;
            double priorAvg = prevBodyEma > 0 ? prevBodyEma : priorBody;

            bool isLongBody   = body      > bodyAvg;
            bool isSmallPrev  = priorBody < priorAvg;
            bool bodyIsBigger = body      > priorBody;

            if (strict)
            {
                return isLongBody && bodyIsBigger && isSmallPrev
                    && Open[offset]  > Close[offset + 1]
                    && Close[offset] < Open[offset + 1];
            }
            else
            {
                // Loose: require same body filters as strict; allow equality on one engulf side
                bool strictHigh = Open[offset]  > Close[offset + 1];
                bool strictLow  = Close[offset] < Open[offset + 1];
                double currBodyH = Math.Max(Open[offset],     Close[offset]);
                double currBodyL = Math.Min(Open[offset],     Close[offset]);
                double prevBodyH = Math.Max(Open[offset + 1], Close[offset + 1]);
                double prevBodyL = Math.Min(Open[offset + 1], Close[offset + 1]);
                return isLongBody && isSmallPrev && bodyIsBigger && (strictHigh || strictLow)
                    && currBodyH >= prevBodyH && currBodyL <= prevBodyL;
            }
        }
        #endregion

        #region Signals
        private void DetectSignals(double atr, int offset)
        {
            if (!isWarmedUp) return;
            if (CurrentBar < offset + 2) return;

            int confirmedBar = CurrentBar - offset;

            // Sweep happened on the confirmed bar
            bool sweepBull = sweepEvents.Count > 0 && !sweepEvents[sweepEvents.Count - 1].IsHigh && sweepEvents[sweepEvents.Count - 1].BarIndex == confirmedBar;
            bool sweepBear = sweepEvents.Count > 0 &&  sweepEvents[sweepEvents.Count - 1].IsHigh  && sweepEvents[sweepEvents.Count - 1].BarIndex == confirmedBar;

            // Use confluenceRawWeight for setup filters (not the 0–100 score)
            bool engulfBull = ShowEngulfing && IsEngulfingBull(StrictEngulfing, offset) && (inDiscount || confluenceRawWeight >= 1.5) && structureBias >= 0;
            bool engulfBear = ShowEngulfing && IsEngulfingBear(StrictEngulfing, offset) && (inPremium  || confluenceRawWeight >= 1.5) && structureBias <= 0;

            // CHoCH happened on the confirmed bar
            bool chochBull = bosEvents.Count > 0 && !bosEvents[bosEvents.Count - 1].IsBos &&  bosEvents[bosEvents.Count - 1].IsBull && bosEvents[bosEvents.Count - 1].EndBarIdx == confirmedBar;
            bool chochBear = bosEvents.Count > 0 && !bosEvents[bosEvents.Count - 1].IsBos && !bosEvents[bosEvents.Count - 1].IsBull && bosEvents[bosEvents.Count - 1].EndBarIdx == confirmedBar;

            bool buyRaw  = chochBull || (sweepBull && (inDiscount || confluenceRawWeight >= 1.5)) || engulfBull;
            bool sellRaw = chochBear || (sweepBear && (inPremium  || confluenceRawWeight >= 1.5)) || engulfBear;

            if (buyRaw && sellRaw) { buyRaw = false; sellRaw = false; }

            bool cooldownOk = barsSinceLastSignal >= SignalCooldown;

            if (buyRaw && cooldownOk)
            {
                string trigger = BuildTrigger(chochBull, sweepBull, engulfBull);
                double sigPrice = Close[offset];
                signalEvents.Add(new SignalEvent { BarIndex = confirmedBar, Price = Low[offset] - atr * 0.3, IsBuy = true, Trigger = trigger });
                if (signalEvents.Count > MaxSignalEvents) signalEvents.RemoveAt(0);
                barsSinceLastSignal = 0;
                dashSignal = "BUY (" + trigger + ")";
                FireSignalAlert(true, trigger, confirmedBar, sigPrice);
            }
            else if (sellRaw && cooldownOk)
            {
                string trigger = BuildTrigger(chochBear, sweepBear, engulfBear);
                double sigPrice = Close[offset];
                signalEvents.Add(new SignalEvent { BarIndex = confirmedBar, Price = High[offset] + atr * 0.3, IsBuy = false, Trigger = trigger });
                if (signalEvents.Count > MaxSignalEvents) signalEvents.RemoveAt(0);
                barsSinceLastSignal = 0;
                dashSignal = "SELL (" + trigger + ")";
                FireSignalAlert(false, trigger, confirmedBar, sigPrice);
            }
        }

        private string BuildTrigger(bool choch, bool sweep, bool engulf)
        {
            var parts = new List<string>();
            if (choch)  parts.Add("choch");
            if (sweep)  parts.Add("sweep");
            if (engulf) parts.Add("engulf");
            return parts.Count > 0 ? string.Join("+", parts) : "n/a";
        }
        #endregion

        #region Dashboard State
        private void UpdateDashboardState(double atr)
        {
            dashTrend  = structureBias > 0 ? "Bullish" : structureBias < 0 ? "Bearish" : "Neutral";
            // Pine: "Long ↑" / "Short ↓" / "—"
            dashFibDir = fibDirection  > 0 ? "Long ↑"  : fibDirection  < 0 ? "Short ↓"  : "—";
            if (dashSignal == null) dashSignal = "—";
            // Dashboard uses confluenceScore (0–100) for display
            dashConfluence = string.Format("{0:F0}% {1}", confluenceScore, confluenceLabel);
            dashZone       = inPremium ? "Premium" : inDiscount ? "Discount" : "—";
            bool hasEqh    = activeEqh != null && activeEqh.Active;
            bool hasEql    = activeEql != null && activeEql.Active;
            dashLiquidity  = (hasEqh && hasEql) ? "EQH+EQL" : hasEqh ? "EQH" : hasEql ? "EQL" : "—";
            double p618    = GetFibPrice(0.618);
            // Use FormatPrice for safe NT8 price display (avoids invalid Digits property)
            dashFib618     = p618 > 0 ? Instrument.MasterInstrument.FormatPrice(p618) : "—";
            dashATR        = Instrument.MasterInstrument.FormatPrice(atr);
            // Near Fib: show ratio plus ATR distance, e.g. "0.618 (0.12 ATR)"
            if (nearestFibLevel != 0 && atr > 0)
            {
                double nearFibPrice = GetFibPrice(nearestFibLevel);
                double atrDist      = nearFibPrice > 0 ? Math.Abs(Close[0] - nearFibPrice) / atr : 0;
                dashNearFib = string.Format("{0:F3} ({1:F2} ATR)", nearestFibLevel, atrDist);
            }
            else dashNearFib = "—";
            dashTF = BarsPeriod.Value + " " + BarsPeriod.BarsPeriodType.ToString();
        }
        #endregion

        #region List Trimming
        private void TrimLists()
        {
            while (bosEvents.Count    > MaxBosEvents)    bosEvents.RemoveAt(0);
            while (sweepEvents.Count  > MaxSweepEvents)  sweepEvents.RemoveAt(0);
            while (signalEvents.Count > MaxSignalEvents) signalEvents.RemoveAt(0);
            while (eqhLevels.Count    > MaxEqLevels)     eqhLevels.RemoveAt(0);
            while (eqlLevels.Count    > MaxEqLevels)     eqlLevels.RemoveAt(0);
            while (swingLabels.Count  > MaxSwingLabels)  swingLabels.RemoveAt(0);
        }
        #endregion

        #region Alert Helpers
        private void FireSweepAlert(bool isHigh, int sigBar, double sigPrice)
        {
            if (!EnableAlerts) return;
            int lastBar = isHigh ? lastSweepHAlertBar : lastSweepLAlertBar;
            if (CurrentBar - lastBar < 5) return;
            if (isHigh) { if (!AlertOnSweepHigh) return; lastSweepHAlertBar = CurrentBar; }
            else         { if (!AlertOnSweepLow)  return; lastSweepLAlertBar = CurrentBar; }
            string msg = WebhookJsonFormat
                ? string.Format("{{\"event\":\"sweep_{0}\",\"instrument\":\"{1}\",\"price\":{2:F4},\"bar\":{3}}}",
                    isHigh ? "high" : "low", Instrument.FullName, sigPrice, sigBar)
                : string.Format("FibStructEngine: Liquidity Sweep {0} @ {1:F4}", isHigh ? "HIGH" : "LOW", sigPrice);
            try
            {
                if (AlertPopup) Alert("FSE_Sweep_" + sigBar, Priority.Medium, msg, AlertSoundFile, 0, Brushes.Orange, Brushes.Black);
                if (AlertSound) PlaySound(AlertSoundFile);
                if (AlertEmail && !string.IsNullOrEmpty(AlertEmailAddress)) SendMail(AlertEmailAddress, "FibStructEngine Sweep Alert", msg);
            }
            catch { }
        }

        private void FireBosChochAlert(bool bull, bool isBos, int sigBar, double sigPrice)
        {
            if (!EnableAlerts) return;
            if (isBos && !AlertOnBos) return;
            if (!isBos && !AlertOnChoch) return;
            // Use separate throttle for BOS vs CHoCH so one cannot suppress the other
            int lastAlertBar = isBos ? lastBosAlertBar : lastChochAlertBar;
            if (CurrentBar - lastAlertBar < 5) return;
            if (isBos) lastBosAlertBar = CurrentBar;
            else       lastChochAlertBar = CurrentBar;
            string evType = isBos ? "BOS" : "CHoCH";
            string msg = WebhookJsonFormat
                ? string.Format("{{\"event\":\"{0}\",\"direction\":\"{1}\",\"instrument\":\"{2}\",\"price\":{3:F4},\"bar\":{4}}}",
                    evType, bull ? "bull" : "bear", Instrument.FullName, sigPrice, sigBar)
                : string.Format("FibStructEngine: {0} {1} @ {2:F4}", evType, bull ? "Bullish" : "Bearish", sigPrice);
            try
            {
                if (AlertPopup) Alert("FSE_" + evType + "_" + sigBar, Priority.High, msg, AlertSoundFile, 0, bull ? Brushes.LimeGreen : Brushes.Crimson, Brushes.Black);
                if (AlertSound) PlaySound(AlertSoundFile);
                if (AlertEmail && !string.IsNullOrEmpty(AlertEmailAddress)) SendMail(AlertEmailAddress, "FibStructEngine " + evType + " Alert", msg);
            }
            catch { }
        }

        private void FireSignalAlert(bool isBuy, string trigger, int sigBar, double sigPrice)
        {
            if (!EnableAlerts) return;
            if (isBuy  && !AlertOnBuy)  return;
            if (!isBuy && !AlertOnSell) return;
            int lastBar = isBuy ? lastBuyAlertBar : lastSellAlertBar;
            if (CurrentBar - lastBar < SignalCooldown) return;
            if (isBuy) lastBuyAlertBar = CurrentBar; else lastSellAlertBar = CurrentBar;
            string msg = WebhookJsonFormat
                ? string.Format("{{\"event\":\"{0}\",\"trigger\":\"{1}\",\"instrument\":\"{2}\",\"price\":{3:F4},\"bar\":{4}}}",
                    isBuy ? "buy" : "sell", trigger, Instrument.FullName, sigPrice, sigBar)
                : string.Format("FibStructEngine: {0} Signal [{1}] @ {2:F4}", isBuy ? "BUY" : "SELL", trigger, sigPrice);
            try
            {
                System.Windows.Media.Brush bg = isBuy
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xE6, 0x76))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x52, 0x52));
                if (AlertPopup) Alert("FSE_Sig_" + sigBar, Priority.High, msg, AlertSoundFile, 0, bg, Brushes.Black);
                if (AlertSound) PlaySound(AlertSoundFile);
                if (AlertEmail && !string.IsNullOrEmpty(AlertEmailAddress)) SendMail(AlertEmailAddress, "FibStructEngine Signal Alert", msg);
            }
            catch { }
        }
        #endregion

        #region SharpDX Resource Management
        private void CreateDXResources()
        {
            if (RenderTarget == null) return;
            try
            {
                var rt = RenderTarget;
                bool dark = IsDarkTheme();
                float fib_a  = FibLineOpacity   / 100f;
                float zone_a = ZoneOpacity      / 100f;
                float line_a = LineOpacity      / 100f;
                float dash_a = DashboardOpacity / 100f;

                dxBullBrush    = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(BullColor,       line_a));
                dxBearBrush    = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(BearColor,       line_a));
                dxFibBrush     = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(FibLineColor,    fib_a));
                dxConfBrush    = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(ConfluenceColor, 1f));
                dxEqBrush      = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(EqColor,         line_a));
                dxSweepBrush   = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(SweepColor,      1f));

                dxGoldenZoneBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(FibLineColor,    zone_a));
                dxTargetZoneBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(ConfluenceColor, zone_a));

                // Theme-aware dashboard colors
                if (dark)
                {
                    dxDashBgBrush     = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0.08f, 0.08f, 0.12f, dash_a));
                    dxDashBorderBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0.25f, 0.25f, 0.35f, dash_a));
                    dxDashHeaderBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0.15f, 0.15f, 0.25f, dash_a));
                    dxTextBrush       = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0.95f, 0.95f, 0.95f, 1f));
                    dxMutedBrush      = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0.55f, 0.55f, 0.55f, 0.6f));
                }
                else
                {
                    // Light theme: dark text on light background
                    dxDashBgBrush     = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0.94f, 0.94f, 0.96f, dash_a));
                    dxDashBorderBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0.65f, 0.65f, 0.70f, dash_a));
                    dxDashHeaderBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0.80f, 0.80f, 0.86f, dash_a));
                    dxTextBrush       = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0.08f, 0.08f, 0.10f, 1f));
                    dxMutedBrush      = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0.40f, 0.40f, 0.44f, 0.8f));
                }

                // Stroke styles
                var solidProps = new SharpDX.Direct2D1.StrokeStyleProperties { LineJoin = SharpDX.Direct2D1.LineJoin.Round };
                dxSolidStyle = new SharpDX.Direct2D1.StrokeStyle(rt.Factory, solidProps);

                var dashProps = new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Dash, LineJoin = SharpDX.Direct2D1.LineJoin.Round };
                dxDashStyle  = new SharpDX.Direct2D1.StrokeStyle(rt.Factory, dashProps);

                var dotProps = new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Dot, LineJoin = SharpDX.Direct2D1.LineJoin.Round };
                dxDotStyle   = new SharpDX.Direct2D1.StrokeStyle(rt.Factory, dotProps);

                // Text formats — use proper constructor overload (FontWeight cannot be set via object initializer)
                dxWriteFactory = new SharpDX.DirectWrite.Factory(SharpDX.DirectWrite.FactoryType.Shared);
                dxLabelFmt    = new SharpDX.DirectWrite.TextFormat(dxWriteFactory, "Arial",
                    SharpDX.DirectWrite.FontWeight.Normal, SharpDX.DirectWrite.FontStyle.Normal,
                    SharpDX.DirectWrite.FontStretch.Normal, DashboardFontSize);
                dxDashFmt     = new SharpDX.DirectWrite.TextFormat(dxWriteFactory, "Arial",
                    SharpDX.DirectWrite.FontWeight.Normal, SharpDX.DirectWrite.FontStyle.Normal,
                    SharpDX.DirectWrite.FontStretch.Normal, DashboardFontSize);
                dxDashHdrFmt  = new SharpDX.DirectWrite.TextFormat(dxWriteFactory, "Arial",
                    SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal,
                    SharpDX.DirectWrite.FontStretch.Normal, DashboardFontSize + 1f);
                dxWatermarkFmt = new SharpDX.DirectWrite.TextFormat(dxWriteFactory, "Arial",
                    SharpDX.DirectWrite.FontWeight.Normal, SharpDX.DirectWrite.FontStyle.Italic,
                    SharpDX.DirectWrite.FontStretch.Normal, DashboardFontSize - 1f);
                dxWatermarkFmt.TextAlignment      = SharpDX.DirectWrite.TextAlignment.Center;
                dxWatermarkFmt.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;

                dxReady = true;
            }
            catch { dxReady = false; }
        }

        private void DisposeDXResources()
        {
            dxReady = false;
            DisposeRef(ref dxBullBrush);       DisposeRef(ref dxBearBrush);
            DisposeRef(ref dxFibBrush);        DisposeRef(ref dxConfBrush);
            DisposeRef(ref dxEqBrush);         DisposeRef(ref dxSweepBrush);
            DisposeRef(ref dxGoldenZoneBrush); DisposeRef(ref dxTargetZoneBrush);
            DisposeRef(ref dxDashBgBrush);     DisposeRef(ref dxDashBorderBrush);
            DisposeRef(ref dxDashHeaderBrush); DisposeRef(ref dxTextBrush);
            DisposeRef(ref dxMutedBrush);
            DisposeRef(ref dxSolidStyle);      DisposeRef(ref dxDashStyle);
            DisposeRef(ref dxDotStyle);
            DisposeRef(ref dxLabelFmt);        DisposeRef(ref dxDashFmt);
            DisposeRef(ref dxDashHdrFmt);      DisposeRef(ref dxWatermarkFmt);
            DisposeRef(ref dxWriteFactory);
        }

        private static void DisposeRef<T>(ref T r) where T : class, IDisposable
        { if (r != null) { r.Dispose(); r = null; } }

        public override void OnRenderTargetChanged() { DisposeDXResources(); }
        #endregion

        #region OnRender
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (Bars == null || ChartBars == null || RenderTarget == null) return;

            if (!dxReady) { try { CreateDXResources(); } catch { return; } }
            if (!dxReady) return;

            var rt = RenderTarget;
            int fromBar = ChartBars.FromIndex;
            int toBar   = ChartBars.ToIndex;
            if (fromBar > toBar) return;

            float rtW = rt.Size.Width;
            float rtH = rt.Size.Height;

            try { RenderEqLevels(chartControl, chartScale, fromBar, toBar); }
            catch { }

            try { RenderFibLevels(chartControl, chartScale, fromBar, toBar, rtW); }
            catch { }

            try { RenderBosEvents(chartControl, chartScale, fromBar, toBar); }
            catch { }

            try { RenderSweepMarkers(chartControl, chartScale, fromBar, toBar); }
            catch { }

            try { RenderSwingLabels(chartControl, chartScale, fromBar, toBar); }
            catch { }

            try { if (ShowBuySellSignals) RenderSignals(chartControl, chartScale, fromBar, toBar); }
            catch { }

            try { if (ShowDashboard && DashboardPosition != FibStructDashboardPosition.Hidden) RenderDashboard(chartControl, chartScale, rtW, rtH); }
            catch { }

            try { if (ShowWatermark) RenderWatermark(rtW, rtH); }
            catch { }

        }
        #endregion

        #region Render — EQ Levels
        private void RenderEqLevels(ChartControl cc, ChartScale cs, int fromBar, int toBar)
        {
            if (!ShowEqhEql) return;
            var rt = RenderTarget;
            var stroke = dxDashStyle;
            foreach (var eq in eqhLevels) RenderEqLine(rt, cc, cs, eq, fromBar, toBar, stroke);
            foreach (var eq in eqlLevels) RenderEqLine(rt, cc, cs, eq, fromBar, toBar, stroke);
        }

        private void RenderEqLine(SharpDX.Direct2D1.RenderTarget rt, ChartControl cc, ChartScale cs,
            EqLevel eq, int fromBar, int toBar, SharpDX.Direct2D1.StrokeStyle stroke)
        {
            int startB = Math.Max(eq.StartBarIdx, fromBar);
            int endB   = Math.Min(eq.EndBarIdx + EqLineExtensionBars, toBar);
            if (startB > endB) return;

            float x1 = cc.GetXByBarIndex(ChartBars, startB);
            float x2 = cc.GetXByBarIndex(ChartBars, endB);
            float y  = (float)cs.GetYByValue(eq.Price);

            var brush = eq.Active ? dxEqBrush : dxMutedBrush;
            rt.DrawLine(new SharpDX.Vector2(x1, y), new SharpDX.Vector2(x2, y), brush, 1.5f, stroke);
            if (eq.Active)
            {
                string lbl = eq.IsHigh ? "EQH" : "EQL";
                rt.DrawText(lbl, dxLabelFmt, new SharpDX.RectangleF(x2 + 2, y - 8, 40, 16), dxEqBrush);
            }
        }
        #endregion

        #region Render — Fib Levels
        private void RenderFibLevels(ChartControl cc, ChartScale cs, int fromBar, int toBar, float rtW)
        {
            if (!ShowFibLevels || fibDirection == 0) return;
            if (fibSwingHigh <= 0 || fibSwingLow <= 0) return;

            var rt = RenderTarget;
            float xStart = cc.GetXByBarIndex(ChartBars, Math.Max(Math.Min(fibSwingHighIdx, fibSwingLowIdx), fromBar));
            float xEnd   = Math.Min(rtW, cc.GetXByBarIndex(ChartBars, CurrentBar) + FibExtensionBars * 6f);

            // Golden zone fill (0.500 to 0.786)
            {
                double gTop = GetFibPrice(fibDirection == 1 ? 0.500 : 0.786);
                double gBot = GetFibPrice(fibDirection == 1 ? 0.786 : 0.500);
                if (gTop > 0 && gBot > 0 && gTop != gBot)
                {
                    float yTop = (float)cs.GetYByValue(Math.Max(gTop, gBot));
                    float yBot = (float)cs.GetYByValue(Math.Min(gTop, gBot));
                    if (yBot > yTop)
                        rt.FillRectangle(new SharpDX.RectangleF(xStart, yTop, xEnd - xStart, yBot - yTop), dxGoldenZoneBrush);
                }
            }

            // Target zone fill (-0.5 to -0.618)
            {
                double tTop = GetFibPrice(-0.5);
                double tBot = GetFibPrice(-0.618);
                if (tTop > 0 && tBot > 0 && tTop != tBot)
                {
                    float yTop = (float)cs.GetYByValue(Math.Max(tTop, tBot));
                    float yBot = (float)cs.GetYByValue(Math.Min(tTop, tBot));
                    if (yBot > yTop)
                        rt.FillRectangle(new SharpDX.RectangleF(xStart, yTop, xEnd - xStart, yBot - yTop), dxTargetZoneBrush);
                }
            }

            double[] ratios = { 0.236, 0.382, 0.500, 0.618, 0.786, -0.5, -0.618 };
            bool[]   shown  = { ShowFib0236, ShowFib0382, ShowFib0500, ShowFib0618, ShowFib0786, ShowTargetN050, ShowTargetN0618 };
            string[] labels = { "0.236", "0.382", "0.500", "0.618", "0.786", "-0.5", "-0.618" };

            var strokeStyle = GetStrokeStyle(StructureLineStyle);
            for (int i = 0; i < ratios.Length; i++)
            {
                if (!shown[i]) continue;
                double price = GetFibPrice(ratios[i]);
                if (price <= 0) continue;
                float y = (float)cs.GetYByValue(price);
                rt.DrawLine(new SharpDX.Vector2(xStart, y), new SharpDX.Vector2(xEnd, y), dxFibBrush, 1f, strokeStyle);
                string lbl = string.Format("{0}  {1:F4}", labels[i], price);
                rt.DrawText(lbl, dxLabelFmt, new SharpDX.RectangleF(xEnd + 2, y - 8, 100, 16), dxFibBrush);
            }
        }
        #endregion

        #region Render — BOS Events
        private void RenderBosEvents(ChartControl cc, ChartScale cs, int fromBar, int toBar)
        {
            if (!ShowBosCHoCH) return;
            var rt = RenderTarget;
            var stroke = GetStrokeStyle(StructureLineStyle);

            foreach (var ev in bosEvents)
            {
                if (ev.EndBarIdx < fromBar || ev.StartBarIdx > toBar) continue;
                int s = Math.Max(ev.StartBarIdx, fromBar);
                int e = Math.Min(ev.EndBarIdx,   toBar);
                float x1 = cc.GetXByBarIndex(ChartBars, s);
                float x2 = cc.GetXByBarIndex(ChartBars, e);
                float y  = (float)cs.GetYByValue(ev.Price);
                var brush = ev.IsBull ? dxBullBrush : dxBearBrush;
                rt.DrawLine(new SharpDX.Vector2(x1, y), new SharpDX.Vector2(x2, y), brush, StructureLineWidth, stroke);
                string lbl = ev.IsBos ? "BOS" : "CHoCH";
                float lx = x1 + (x2 - x1) / 2f - 15;
                float ly = ev.IsBull ? y - 14 : y + 2;
                rt.DrawText(lbl, dxLabelFmt, new SharpDX.RectangleF(lx, ly, 50, 14), brush);
            }
        }
        #endregion

        #region Render — Sweep Markers
        private void RenderSweepMarkers(ChartControl cc, ChartScale cs, int fromBar, int toBar)
        {
            if (!ShowSweeps) return;
            var rt = RenderTarget;
            foreach (var sw in sweepEvents)
            {
                if (sw.BarIndex < fromBar || sw.BarIndex > toBar) continue;
                float x = cc.GetXByBarIndex(ChartBars, sw.BarIndex);
                float y = (float)cs.GetYByValue(sw.Price);
                float r = MarkerSize / 2f;
                var pts = new SharpDX.Vector2[]
                {
                    new SharpDX.Vector2(x,     y - r),
                    new SharpDX.Vector2(x + r, y),
                    new SharpDX.Vector2(x,     y + r),
                    new SharpDX.Vector2(x - r, y)
                };
                for (int i = 0; i < 4; i++)
                    rt.DrawLine(pts[i], pts[(i + 1) % 4], dxSweepBrush, 1.5f);
                string lbl = sw.IsHigh ? "↓SWP" : "↑SWP";
                rt.DrawText(lbl, dxLabelFmt, new SharpDX.RectangleF(x - 20, sw.IsHigh ? y - 20 : y + 4, 50, 14), dxSweepBrush);
            }
        }
        #endregion

        #region Render — Swing Labels
        private void RenderSwingLabels(ChartControl cc, ChartScale cs, int fromBar, int toBar)
        {
            if (!ShowSwingLabels) return;
            var rt = RenderTarget;
            foreach (var sl in swingLabels)
            {
                if (sl.BarIndex < fromBar || sl.BarIndex > toBar) continue;
                float x = cc.GetXByBarIndex(ChartBars, sl.BarIndex);
                float y = (float)cs.GetYByValue(sl.Price);
                var brush = sl.IsHigh ? dxBearBrush : dxBullBrush;
                float ly = sl.IsHigh ? y - 16 : y + 4;
                rt.DrawText(sl.Label, dxLabelFmt, new SharpDX.RectangleF(x - 10, ly, 30, 14), brush);
            }
        }
        #endregion

        #region Render — Signals
        private void RenderSignals(ChartControl cc, ChartScale cs, int fromBar, int toBar)
        {
            var rt = RenderTarget;
            foreach (var sig in signalEvents)
            {
                if (sig.BarIndex < fromBar || sig.BarIndex > toBar) continue;
                float x = cc.GetXByBarIndex(ChartBars, sig.BarIndex);
                float y = (float)cs.GetYByValue(sig.Price);
                var brush = sig.IsBuy ? dxBullBrush : dxBearBrush;
                float r = MarkerSize;
                if (sig.IsBuy)
                {
                    rt.DrawLine(new SharpDX.Vector2(x - r, y + r), new SharpDX.Vector2(x, y - r),     brush, 2f);
                    rt.DrawLine(new SharpDX.Vector2(x, y - r),     new SharpDX.Vector2(x + r, y + r), brush, 2f);
                    rt.DrawLine(new SharpDX.Vector2(x + r, y + r), new SharpDX.Vector2(x - r, y + r), brush, 2f);
                }
                else
                {
                    rt.DrawLine(new SharpDX.Vector2(x - r, y - r), new SharpDX.Vector2(x, y + r),     brush, 2f);
                    rt.DrawLine(new SharpDX.Vector2(x, y + r),     new SharpDX.Vector2(x + r, y - r), brush, 2f);
                    rt.DrawLine(new SharpDX.Vector2(x + r, y - r), new SharpDX.Vector2(x - r, y - r), brush, 2f);
                }
                rt.DrawText(sig.IsBuy ? "B" : "S", dxLabelFmt, new SharpDX.RectangleF(x - 5, y - 7, 15, 14), dxTextBrush);
            }
        }
        #endregion

        #region Render — Dashboard
        private void RenderDashboard(ChartControl cc, ChartScale cs, float rtW, float rtH)
        {
            var rt = RenderTarget;

            int   dw   = DashboardWidth;
            int   rowH = DashRowHeight;
            int   pad  = 8;
            int   numRows = 12;   // header + 11 data rows
            float dh   = pad * 2 + (rowH * numRows);

            float panelX = (float)ChartPanel.X;
            float panelY = (float)ChartPanel.Y;
            float panelW = (float)ChartPanel.W;
            float panelH = (float)ChartPanel.H;

            float ox, oy;
            switch (DashboardPosition)
            {
                case FibStructDashboardPosition.TopRight:     ox = panelX + panelW - dw - 10; oy = panelY + 10; break;
                case FibStructDashboardPosition.BottomLeft:   ox = panelX + 10;               oy = panelY + panelH - dh - 10; break;
                case FibStructDashboardPosition.BottomRight:  ox = panelX + panelW - dw - 10; oy = panelY + panelH - dh - 10; break;
                case FibStructDashboardPosition.CenterTop:    ox = panelX + (panelW - dw) / 2f; oy = panelY + 10; break;
                case FibStructDashboardPosition.CenterBottom: ox = panelX + (panelW - dw) / 2f; oy = panelY + panelH - dh - 10; break;
                default: /* TopLeft */                        ox = panelX + 10;               oy = panelY + 10; break;
            }

            var bgRect = new SharpDX.RectangleF(ox, oy, dw, dh);
            rt.FillRectangle(bgRect, dxDashBgBrush);
            rt.DrawRectangle(bgRect, dxDashBorderBrush, 1f);

            // Header row
            var hdrRect = new SharpDX.RectangleF(ox, oy, dw, rowH + 4);
            rt.FillRectangle(hdrRect, dxDashHeaderBrush);
            rt.DrawText("⬡ FibStruct Engine  v1.6.0-NT8", dxDashHdrFmt,
                new SharpDX.RectangleF(ox + pad, oy + 2, dw - pad * 2, rowH), dxTextBrush);

            float y = oy + rowH + 4 + 2;
            float colW = dw - pad * 2;

            DrawDashRow(rt, ox + pad, y, colW, rowH, "Trend",      dashTrend      ?? "—", GetTrendBrush(dashTrend));      y += rowH;
            DrawDashRow(rt, ox + pad, y, colW, rowH, "Fib Dir",    dashFibDir     ?? "—", GetFibDirBrush(dashFibDir));    y += rowH;
            DrawDashRow(rt, ox + pad, y, colW, rowH, "Signal",     dashSignal     ?? "—", GetSignalBrush(dashSignal));    y += rowH;
            DrawDashRow(rt, ox + pad, y, colW, rowH, "Confluence", dashConfluence ?? "—", GetConfBrush());                y += rowH;
            DrawDashRow(rt, ox + pad, y, colW, rowH, "Zone",       dashZone       ?? "—", GetZoneBrush(dashZone));        y += rowH;
            DrawDashRow(rt, ox + pad, y, colW, rowH, "Liquidity",  dashLiquidity  ?? "—", dxEqBrush);                    y += rowH;
            DrawDashRow(rt, ox + pad, y, colW, rowH, "Fib .618",   dashFib618     ?? "—", dxFibBrush);                   y += rowH;
            DrawDashRow(rt, ox + pad, y, colW, rowH, "ATR(14)",    dashATR        ?? "—", dxMutedBrush);                 y += rowH;
            DrawDashRow(rt, ox + pad, y, colW, rowH, "Near Fib",   dashNearFib    ?? "—", dxFibBrush);                   y += rowH;
            DrawDashRow(rt, ox + pad, y, colW, rowH, "TF",         dashTF         ?? "—", dxMutedBrush);                 y += rowH;
            DrawDashRow(rt, ox + pad, y, colW, rowH, "Version",    "v1.6.0-NT8",          dxMutedBrush);
        }

        private void RenderWatermark(float rtW, float rtH)
        {
            if (dxWatermarkFmt == null || dxMutedBrush == null) return;
            var rt = RenderTarget;
            float ww = 200f;
            float wh = 20f;
            float wx = (rtW - ww) / 2f;
            float wy = rtH - wh - 6f;
            rt.DrawText("WillyAlgoTrader", dxWatermarkFmt,
                new SharpDX.RectangleF(wx, wy, ww, wh), dxMutedBrush);
        }

        private void DrawDashRow(SharpDX.Direct2D1.RenderTarget rt, float x, float y, float w, float rowH,
            string key, string val, SharpDX.Direct2D1.SolidColorBrush valBrush)
        {
            float halfW = w / 2f;
            rt.DrawText(key, dxDashFmt, new SharpDX.RectangleF(x, y, halfW, rowH), dxMutedBrush);
            rt.DrawText(val, dxDashFmt, new SharpDX.RectangleF(x + halfW, y, halfW, rowH), valBrush ?? dxTextBrush);
        }

        private SharpDX.Direct2D1.SolidColorBrush GetTrendBrush(string trend)
        {
            if (trend == null) return dxTextBrush;
            if (trend.Contains("Bull")) return dxBullBrush;
            if (trend.Contains("Bear")) return dxBearBrush;
            return dxMutedBrush;
        }

        private SharpDX.Direct2D1.SolidColorBrush GetFibDirBrush(string dir)
        {
            if (dir == null) return dxTextBrush;
            if (dir.Contains("Long"))  return dxBullBrush;
            if (dir.Contains("Short")) return dxBearBrush;
            return dxMutedBrush;
        }

        private SharpDX.Direct2D1.SolidColorBrush GetSignalBrush(string sig)
        {
            if (sig == null) return dxTextBrush;
            if (sig.StartsWith("BUY"))  return dxBullBrush;
            if (sig.StartsWith("SELL")) return dxBearBrush;
            return dxMutedBrush;
        }

        private SharpDX.Direct2D1.SolidColorBrush GetConfBrush()
        {
            // confluenceScore is 0–100
            if (confluenceScore >= 60) return dxConfBrush;
            if (confluenceScore >= 30) return dxFibBrush;
            return dxMutedBrush;
        }

        private SharpDX.Direct2D1.SolidColorBrush GetZoneBrush(string zone)
        {
            if (zone == null) return dxTextBrush;
            if (zone == "Premium")  return dxBearBrush;
            if (zone == "Discount") return dxBullBrush;
            return dxMutedBrush;
        }
        #endregion

        #region Render Helpers
        private bool IsDarkTheme()
        {
            if (Theme == FibStructTheme.Dark)  return true;
            if (Theme == FibStructTheme.Light) return false;
            try
            {
                if (ChartControl != null)
                {
                    var bg = ChartControl.Properties.ChartBackground as System.Windows.Media.SolidColorBrush;
                    if (bg != null)
                    {
                        double lum = 0.299 * bg.Color.R + 0.587 * bg.Color.G + 0.114 * bg.Color.B;
                        return lum < 128;
                    }
                }
            }
            catch { }
            return true;
        }

        private SharpDX.Direct2D1.StrokeStyle GetStrokeStyle(FibStructLineStyle style)
        {
            switch (style)
            {
                case FibStructLineStyle.Dotted: return dxDotStyle;
                case FibStructLineStyle.Dashed: return dxDashStyle;
                default:                        return dxSolidStyle;
            }
        }

        private SharpDX.Color4 ToColor4(System.Windows.Media.Brush wpf, float alpha)
        {
            try
            {
                var sb = wpf as System.Windows.Media.SolidColorBrush;
                if (sb == null) return new SharpDX.Color4(0.5f, 0.5f, 0.5f, alpha);
                var c = sb.Color;
                return new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, alpha);
            }
            catch { return new SharpDX.Color4(0.5f, 0.5f, 0.5f, alpha); }
        }
        #endregion

        #region Properties — 01. Main Settings
        [NinjaScriptProperty]
        [Range(2, 50)]
        [Display(Name = "Swing Detection Length", Order = 1, GroupName = "01. Main Settings")]
        public int SwingDetectionLength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use ATR Swing Filter", Order = 2, GroupName = "01. Main Settings")]
        public bool UseAtrSwingFilter { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 3.0)]
        [Display(Name = "ATR Filter Multiplier", Order = 3, GroupName = "01. Main Settings")]
        public double AtrFilterMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Signal Cooldown (bars)", Order = 4, GroupName = "01. Main Settings")]
        public int SignalCooldown { get; set; }
        #endregion

        #region Properties — 02. Fibonacci Levels
        [NinjaScriptProperty]
        [Display(Name = "Show Fib Levels", Order = 1, GroupName = "02. Fibonacci Levels")]
        public bool ShowFibLevels { get; set; }

        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name = "Fib Extension Bars", Order = 2, GroupName = "02. Fibonacci Levels")]
        public int FibExtensionBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show 0.236", Order = 3, GroupName = "02. Fibonacci Levels")]
        public bool ShowFib0236 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show 0.382", Order = 4, GroupName = "02. Fibonacci Levels")]
        public bool ShowFib0382 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show 0.500", Order = 5, GroupName = "02. Fibonacci Levels")]
        public bool ShowFib0500 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show 0.618", Order = 6, GroupName = "02. Fibonacci Levels")]
        public bool ShowFib0618 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show 0.786", Order = 7, GroupName = "02. Fibonacci Levels")]
        public bool ShowFib0786 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Target -0.618", Order = 8, GroupName = "02. Fibonacci Levels")]
        public bool ShowTargetN0618 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Target -0.50", Order = 9, GroupName = "02. Fibonacci Levels")]
        public bool ShowTargetN050 { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 2.0)]
        [Display(Name = "Confluence ATR Tolerance", Order = 10, GroupName = "02. Fibonacci Levels")]
        public double ConfluenceAtrTolerance { get; set; }
        #endregion

        #region Properties — 03. Structure
        [NinjaScriptProperty]
        [Display(Name = "Show BOS / CHoCH", Order = 1, GroupName = "03. Structure")]
        public bool ShowBosCHoCH { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Swing Labels", Order = 2, GroupName = "03. Structure")]
        public bool ShowSwingLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Engulfing", Order = 3, GroupName = "03. Structure")]
        public bool ShowEngulfing { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Strict Engulfing", Order = 4, GroupName = "03. Structure")]
        public bool StrictEngulfing { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Structure Line Style", Order = 5, GroupName = "03. Structure")]
        public FibStructLineStyle StructureLineStyle { get; set; }

        [NinjaScriptProperty]
        [Range(1, 4)]
        [Display(Name = "Structure Line Width", Order = 6, GroupName = "03. Structure")]
        public int StructureLineWidth { get; set; }
        #endregion

        #region Properties — 04. Liquidity
        [NinjaScriptProperty]
        [Display(Name = "Show EQH / EQL", Order = 1, GroupName = "04. Liquidity")]
        public bool ShowEqhEql { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, 1.0)]
        [Display(Name = "EQ ATR Tolerance", Order = 2, GroupName = "04. Liquidity")]
        public double EqAtrTolerance { get; set; }

        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name = "EQ Line Extension Bars", Order = 3, GroupName = "04. Liquidity")]
        public int EqLineExtensionBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Sweeps", Order = 4, GroupName = "04. Liquidity")]
        public bool ShowSweeps { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Sweeps Boost Confluence", Order = 5, GroupName = "04. Liquidity")]
        public bool SweepsBoostConfluence { get; set; }
        #endregion

        #region Properties — 05. Signals
        [NinjaScriptProperty]
        [Display(Name = "Show Buy/Sell Signals", Order = 1, GroupName = "05. Signals")]
        public bool ShowBuySellSignals { get; set; }
        #endregion

        #region Properties — 06. Dashboard
        [NinjaScriptProperty]
        [Display(Name = "Show Dashboard", Order = 1, GroupName = "06. Dashboard")]
        public bool ShowDashboard { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Dashboard Position", Order = 2, GroupName = "06. Dashboard")]
        public FibStructDashboardPosition DashboardPosition { get; set; }

        [NinjaScriptProperty]
        [Range(20, 100)]
        [Display(Name = "Dashboard Opacity (%)", Order = 3, GroupName = "06. Dashboard")]
        public int DashboardOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(7f, 18f)]
        [Display(Name = "Dashboard Font Size", Order = 4, GroupName = "06. Dashboard")]
        public float DashboardFontSize { get; set; }

        [NinjaScriptProperty]
        [Range(150, 400)]
        [Display(Name = "Dashboard Width", Order = 5, GroupName = "06. Dashboard")]
        public int DashboardWidth { get; set; }

        [NinjaScriptProperty]
        [Range(12, 30)]
        [Display(Name = "Dashboard Row Height", Order = 6, GroupName = "06. Dashboard")]
        public int DashRowHeight { get; set; }
        #endregion

        #region Properties — 07. Alerts
        [NinjaScriptProperty]
        [Display(Name = "Enable Alerts", Order = 1, GroupName = "07. Alerts")]
        public bool EnableAlerts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert on Buy Signal", Order = 2, GroupName = "07. Alerts")]
        public bool AlertOnBuy { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert on Sell Signal", Order = 3, GroupName = "07. Alerts")]
        public bool AlertOnSell { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert on BOS", Order = 4, GroupName = "07. Alerts")]
        public bool AlertOnBos { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert on CHoCH", Order = 5, GroupName = "07. Alerts")]
        public bool AlertOnChoch { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert on Sweep High", Order = 6, GroupName = "07. Alerts")]
        public bool AlertOnSweepHigh { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert on Sweep Low", Order = 7, GroupName = "07. Alerts")]
        public bool AlertOnSweepLow { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert Popup", Order = 8, GroupName = "07. Alerts")]
        public bool AlertPopup { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert Sound", Order = 9, GroupName = "07. Alerts")]
        public bool AlertSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert Sound File", Order = 10, GroupName = "07. Alerts")]
        public string AlertSoundFile { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert Email", Order = 11, GroupName = "07. Alerts")]
        public bool AlertEmail { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert Email Address", Order = 12, GroupName = "07. Alerts")]
        public string AlertEmailAddress { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Webhook JSON Format", Order = 13, GroupName = "07. Alerts")]
        public bool WebhookJsonFormat { get; set; }
        #endregion

        #region Properties — 08. Colors
        [NinjaScriptProperty]
        [Display(Name = "Bull Color", Order = 1, GroupName = "08. Colors")]
        [XmlIgnore]
        public System.Windows.Media.Brush BullColor { get; set; }

        [Browsable(false)]
        public string BullColorSerializable
        {
            get { return Serialize.BrushToString(BullColor); }
            set { BullColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Bear Color", Order = 2, GroupName = "08. Colors")]
        [XmlIgnore]
        public System.Windows.Media.Brush BearColor { get; set; }

        [Browsable(false)]
        public string BearColorSerializable
        {
            get { return Serialize.BrushToString(BearColor); }
            set { BearColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Fib Line Color", Order = 3, GroupName = "08. Colors")]
        [XmlIgnore]
        public System.Windows.Media.Brush FibLineColor { get; set; }

        [Browsable(false)]
        public string FibLineColorSerializable
        {
            get { return Serialize.BrushToString(FibLineColor); }
            set { FibLineColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Confluence Color", Order = 4, GroupName = "08. Colors")]
        [XmlIgnore]
        public System.Windows.Media.Brush ConfluenceColor { get; set; }

        [Browsable(false)]
        public string ConfluenceColorSerializable
        {
            get { return Serialize.BrushToString(ConfluenceColor); }
            set { ConfluenceColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "EQ Level Color", Order = 5, GroupName = "08. Colors")]
        [XmlIgnore]
        public System.Windows.Media.Brush EqColor { get; set; }

        [Browsable(false)]
        public string EqColorSerializable
        {
            get { return Serialize.BrushToString(EqColor); }
            set { EqColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Sweep Color", Order = 6, GroupName = "08. Colors")]
        [XmlIgnore]
        public System.Windows.Media.Brush SweepColor { get; set; }

        [Browsable(false)]
        public string SweepColorSerializable
        {
            get { return Serialize.BrushToString(SweepColor); }
            set { SweepColor = Serialize.StringToBrush(value); }
        }
        #endregion

        #region Properties — 09. Visual / Rendering
        [NinjaScriptProperty]
        [Display(Name = "Theme", Order = 1, GroupName = "09. Visual / Rendering")]
        public FibStructTheme Theme { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Watermark", Order = 2, GroupName = "09. Visual / Rendering",
            Description = "Render 'WillyAlgoTrader' watermark at bottom-center of chart")]
        public bool ShowWatermark { get; set; }

        [NinjaScriptProperty]
        [Range(10, 100)]
        [Display(Name = "Fib Line Opacity (%)", Order = 3, GroupName = "09. Visual / Rendering")]
        public int FibLineOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(5, 80)]
        [Display(Name = "Zone Opacity (%)", Order = 4, GroupName = "09. Visual / Rendering")]
        public int ZoneOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(20, 100)]
        [Display(Name = "Line Opacity (%)", Order = 5, GroupName = "09. Visual / Rendering")]
        public int LineOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(4f, 24f)]
        [Display(Name = "Marker Size", Order = 6, GroupName = "09. Visual / Rendering")]
        public float MarkerSize { get; set; }
        #endregion

        #region Properties — 10. Performance
        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name = "Max BOS Events", Order = 1, GroupName = "10. Performance")]
        public int MaxBosEvents { get; set; }

        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name = "Max Sweep Events", Order = 2, GroupName = "10. Performance")]
        public int MaxSweepEvents { get; set; }

        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name = "Max Signal Events", Order = 3, GroupName = "10. Performance")]
        public int MaxSignalEvents { get; set; }

        [NinjaScriptProperty]
        [Range(5, 100)]
        [Display(Name = "Max EQ Levels", Order = 4, GroupName = "10. Performance")]
        public int MaxEqLevels { get; set; }

        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name = "Max Swing Labels", Order = 5, GroupName = "10. Performance")]
        public int MaxSwingLabels { get; set; }
        #endregion

        #region NinjaScript generated code
        // This region is intentionally left empty.
        // NinjaTrader will auto-generate the cache accessor methods on compilation.
        // The incomplete manually-generated section has been removed to avoid
        // exposing only SwingDetectionLength while the indicator has many [NinjaScriptProperty] values.
        #endregion
    }
}
