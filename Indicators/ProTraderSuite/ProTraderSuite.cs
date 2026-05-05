// ProTraderSuite — Combined GPU-rendered trading suite for NinjaTrader 8
// Features: ATR Bands, Trend Baseline, Session VWAP, Market Structure, FVG, Key Levels,
//           ATR Projections, Order Flow Bubbles, Volume Profile, DOM Heatmap, Cumulative Delta, Alerts

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;

// Unambiguous type aliases — resolve SharpDX vs System.Windows.Media conflicts
using DxBrush        = SharpDX.Direct2D1.Brush;
using DxSolidBrush   = SharpDX.Direct2D1.SolidColorBrush;
using DxEllipse      = SharpDX.Direct2D1.Ellipse;
using DxPathGeometry = SharpDX.Direct2D1.PathGeometry;
using DxFactory      = SharpDX.DirectWrite.Factory;
using MediaBrush     = System.Windows.Media.Brush;
using MediaBrushes   = System.Windows.Media.Brushes;
using MediaColor     = System.Windows.Media.Color;
using MediaSCB       = System.Windows.Media.SolidColorBrush;
#endregion

public enum PtsDeltaMode   { ProxyByClose, TrueBidAskDelta }
public enum PtsHeatmapMode { SnapshotOnly, HistoryOnly, Both }
public enum PtsCdPanelPos  { Bottom, Top }

namespace NinjaTrader.NinjaScript.Indicators
{
    public class ProTraderSuite : Indicator
    {
        // ═══════════════════════════════════════════════════════════════════════════
        #region Inner types

        private class SwingPoint
        {
            public int    BarIndex;
            public double Price;
            public bool   IsHigh;
            public string Label; // HH, HL, LH, LL
        }

        private class FvgBox
        {
            public double TopPrice;
            public double BotPrice;
            public int    StartBar;
            public bool   IsBullish;
            public bool   Mitigated;
            public bool   Inverted;
        }

        private class BubblePoint
        {
            public int    BarIndex;
            public double Price;
            public double Volume;
            public bool   IsBuy;
        }

        private class VpBucket
        {
            public double BuyVol;
            public double SellVol;
            public double Total => BuyVol + SellVol;
        }

        private class DepthSnap
        {
            public DateTime Time;
            public List<(bool IsBid, double Price, long Size)> Levels =
                new List<(bool IsBid, double Price, long Size)>();
        }

        private class CdBar
        {
            public int    BarIndex;
            public double Delta;
            public double CumDelta;
            public bool   IsUp;
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════════
        #region Private fields

        // DX state
        private bool dxReady;
        private SharpDX.DirectWrite.Factory dWriteFactory;
        private SharpDX.DirectWrite.TextFormat tfTag;

        // DX brushes — Bands
        private SharpDX.Direct2D1.SolidColorBrush dxBandUpperFill;
        private SharpDX.Direct2D1.SolidColorBrush dxBandLowerFill;
        private SharpDX.Direct2D1.SolidColorBrush dxBandUpperLine;
        private SharpDX.Direct2D1.SolidColorBrush dxBandLowerLine;

        // DX brushes — Baseline
        private SharpDX.Direct2D1.SolidColorBrush dxBaseline;

        // DX brushes — VWAP
        private SharpDX.Direct2D1.SolidColorBrush dxVwap;

        // DX brushes — Structure
        private SharpDX.Direct2D1.SolidColorBrush dxHH, dxHL, dxLH, dxLL;
        private SharpDX.Direct2D1.SolidColorBrush dxBos, dxChoch;

        // DX brushes — FVG
        private SharpDX.Direct2D1.SolidColorBrush dxFvgBull, dxFvgBear, dxFvgI;

        // DX brushes — Key Levels
        private SharpDX.Direct2D1.SolidColorBrush dxKeyLevel;

        // DX brushes — ATR Projections
        private SharpDX.Direct2D1.SolidColorBrush dxDailyProj, dxWeeklyProj;

        // DX brushes — Bubbles
        private SharpDX.Direct2D1.SolidColorBrush dxBubbleBuy, dxBubbleSell;

        // DX brushes — Volume Profile
        private SharpDX.Direct2D1.SolidColorBrush dxVpBuy, dxVpSell, dxVpTotal;
        private SharpDX.Direct2D1.SolidColorBrush dxVpPoc, dxVpVa;

        // DX brushes — Heatmap
        private SharpDX.Direct2D1.SolidColorBrush dxHeatBid, dxHeatAsk;

        // DX brushes — CD Panel
        private SharpDX.Direct2D1.SolidColorBrush dxCdUp, dxCdDown, dxCdBg;
        private SharpDX.Direct2D1.SolidColorBrush dxCdZero, dxCdDiv;

        // Series
        private Series<double> atr;
        private Series<double> upper;
        private Series<double> mid;
        private Series<double> lower;
        private Series<double> vwap;

        // VWAP accumulation
        private double vwapSumPV;
        private double vwapSumV;

        // Session tracking
        private double sessionHigh;
        private double sessionLow;

        // Prior day levels
        private double yHi, yLo, yCl, dDP, dailyAtr;

        // Weekly ATR
        private double weeklyAtr;

        // Market structure
        private List<SwingPoint> swings;

        // FVG
        private List<FvgBox> fvgs;

        // Order flow / bubbles
        private double barBuyVol;
        private double barSellVol;
        private double currentAsk;
        private double currentBid;
        private List<BubblePoint> bubbles;
        private int lastBubbleBar = -1;

        // Volume profile
        private Dictionary<int, VpBucket> vpProfile;
        private double vpTickSize;

        // DOM heatmap
        private Dictionary<(bool IsBid, int Position), (double Price, long Size)> depthSnap;
        private List<DepthSnap> depthHistory;
        private DateTime lastDepthSample = DateTime.MinValue;
        private HashSet<string> activeWalls;

        // Cumulative delta
        private double barCdDelta;
        private double cumDelta;
        private List<CdBar> cdBars;
        private int lastCdBar = -1;

        // CD divergence tracking
        private List<(int BarIndex, bool IsBull)> cdDivBars;
        private HashSet<string> cdDivKeys;

        // Alert tracking
        private HashSet<string> atrAlertFired;

        #endregion

        // ═══════════════════════════════════════════════════════════════════════════
        #region OnStateChange

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "ProTrader Suite — 12-module GPU-rendered trading indicator";
                Name                     = "ProTraderSuite";
                Calculate                = Calculate.OnEachTick;
                IsOverlay                = true;
                DrawOnPricePanel         = true;
                IsSuspendedWhileInactive = true;
                MaximumBarsLookBack      = MaximumBarsLookBack.Infinite;

                // 01. Bands
                ShowBands            = true;
                AtrPeriod            = 14;
                BandMultiplier       = 1.5;
                BandUpperFillColor   = Brushes.Red;
                BandUpperFillOpacity = 20;
                BandLowerFillColor   = Brushes.LimeGreen;
                BandLowerFillOpacity = 20;
                BandUpperLineColor   = Brushes.Red;
                BandUpperLineOpacity = 70;
                BandLowerLineColor   = Brushes.LimeGreen;
                BandLowerLineOpacity = 70;

                // 02. Baseline
                ShowBaseline     = true;
                BaselineColor    = Brushes.Black;
                BaselineOpacity  = 80;

                // 03. VWAP
                ShowVwap    = true;
                VwapColor   = Brushes.Gold;
                VwapOpacity = 85;

                // 04. Structure
                ShowStructure    = true;
                StructureStrength= 3;
                HhColor          = Brushes.LimeGreen;
                HlColor          = Brushes.DodgerBlue;
                LhColor          = Brushes.OrangeRed;
                LlColor          = Brushes.Red;
                BosColor         = Brushes.Cyan;
                ChochColor       = Brushes.Magenta;
                TagOpacity       = 85;
                BosChochOpacity  = 70;

                // 05. FVG
                ShowFvg                = true;
                FvgMinTicks            = 2;
                RequireDisplacement    = false;
                DisplacementMultiplier = 1.0;
                ShowIFvg               = true;
                FvgBullColor           = Brushes.DodgerBlue;
                FvgBearColor           = Brushes.OrangeRed;
                FvgIColor              = Brushes.Magenta;
                FvgOpacity             = 30;

                // 06. Key Levels
                ShowKeyLevels    = true;
                KeyLevelColor    = Brushes.DarkGray;
                KeyLevelOpacity  = 80;

                // 07. ATR Projections
                ShowAtrProj       = true;
                WeeklyAtrPeriod   = 5;
                DailyProjColor    = Brushes.OrangeRed;
                DailyProjOpacity  = 90;
                WeeklyProjColor   = Brushes.Purple;
                WeeklyProjOpacity = 90;

                // 08. Order Flow
                ShowBubbles     = true;
                BubbleMaxPx     = 40;
                DeltaMode       = PtsDeltaMode.ProxyByClose;
                BubbleBuyColor  = Brushes.LimeGreen;
                BubbleSellColor = Brushes.Red;
                BubbleOpacity   = 70;
                ShowVolumeText  = true;

                // 09. Text
                TagFontSize = 10f;

                // 10. Volume Profile
                ShowVp          = true;
                VpWidthPx       = 80;
                VpTickBucketSize= 1;
                VpValueAreaPct  = 70;
                VpSplitBuySell  = true;
                VpBuyColor      = Brushes.LimeGreen;
                VpSellColor     = Brushes.Red;
                VpTotalColor    = Brushes.DodgerBlue;
                VpPocColor      = Brushes.Yellow;
                VpVaColor       = Brushes.Orange;
                VpHistoOpacity  = 50;

                // 11. DOM Heatmap
                ShowHeatmap          = true;
                HeatmapMode          = PtsHeatmapMode.Both;
                HeatmapStripWidthPx  = 60;
                HeatmapDepthLevels   = 20;
                HeatmapHistorySeconds= 300;
                HeatmapSampleRateHz  = 4;
                ShowSizeLabels       = true;
                WallThreshold        = 500L;
                HeatmapBidColor      = Brushes.DodgerBlue;
                HeatmapAskColor      = Brushes.OrangeRed;
                HeatmapMaxOpacity    = 80;

                // 12. Cumulative Delta
                ShowCdPanel      = true;
                CdPanelPos       = PtsCdPanelPos.Bottom;
                CdPanelHeightPx  = 120;
                CdSessionReset   = true;
                ShowCdDivergence = true;
                CdUpColor        = Brushes.LimeGreen;
                CdDownColor      = Brushes.Red;
                CdBgColor        = Brushes.Black;
                CdZeroLineColor  = Brushes.Gray;
                CdDivColor       = Brushes.Yellow;
                CdOpacity        = 70;

                // 13. Alerts
                AlertFvgFillEnable       = false;
                AlertFvgFillSound        = "Alert2.wav";
                AlertFvgFillPriority     = Priority.Medium;
                AlertFvgFillRearm        = 15;

                AlertIFvgCreatedEnable   = false;
                AlertIFvgCreatedSound    = "Alert3.wav";
                AlertIFvgCreatedPriority = Priority.High;
                AlertIFvgCreatedRearm    = 15;

                AlertBosEnable           = false;
                AlertBosSound            = "Alert1.wav";
                AlertBosPriority         = Priority.High;
                AlertBosRearm            = 10;

                AlertChochEnable         = false;
                AlertChochSound          = "Alert1.wav";
                AlertChochPriority       = Priority.High;
                AlertChochRearm          = 10;

                AlertAtrTargetHitEnable   = false;
                AlertAtrTargetHitSound    = "Alert2.wav";
                AlertAtrTargetHitPriority = Priority.Medium;
                AlertAtrTargetHitRearm    = 30;

                AlertCdDivergenceEnable   = false;
                AlertCdDivergenceSound    = "Alert4.wav";
                AlertCdDivergencePriority = Priority.High;
                AlertCdDivergenceRearm    = 20;

                AlertVwapCrossEnable      = false;
                AlertVwapCrossSound       = "Alert2.wav";
                AlertVwapCrossPriority    = Priority.Medium;
                AlertVwapCrossRearm       = 20;

                AlertWallAddedEnable      = false;
                AlertWallAddedSound       = "Alert3.wav";
                AlertWallAddedPriority    = Priority.High;
                AlertWallAddedRearm       = 5;

                AlertWallPulledEnable     = false;
                AlertWallPulledSound      = "Alert4.wav";
                AlertWallPulledPriority   = Priority.High;
                AlertWallPulledRearm      = 5;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Day,  1); // BIP 1 — prior day H/L/C, daily pivot
                AddDataSeries(BarsPeriodType.Week, 1); // BIP 2 — weekly ATR
            }
            else if (State == State.DataLoaded)
            {
                atr   = new Series<double>(this, MaximumBarsLookBack.Infinite);
                upper = new Series<double>(this, MaximumBarsLookBack.Infinite);
                mid   = new Series<double>(this, MaximumBarsLookBack.Infinite);
                lower = new Series<double>(this, MaximumBarsLookBack.Infinite);
                vwap  = new Series<double>(this, MaximumBarsLookBack.Infinite);

                swings       = new List<SwingPoint>();
                fvgs         = new List<FvgBox>();
                bubbles      = new List<BubblePoint>();
                vpProfile    = new Dictionary<int, VpBucket>();
                depthSnap    = new Dictionary<(bool, int), (double, long)>();
                depthHistory = new List<DepthSnap>();
                activeWalls  = new HashSet<string>();
                cdBars       = new List<CdBar>();
                cdDivBars    = new List<(int, bool)>();
                cdDivKeys    = new HashSet<string>();
                atrAlertFired= new HashSet<string>();

                vpTickSize    = Math.Max(TickSize, VpTickBucketSize * TickSize);
                dWriteFactory = new SharpDX.DirectWrite.Factory();
            }
            else if (State == State.Terminated)
            {
                DisposeDXResources();
                if (tfTag         != null) { tfTag.Dispose();         tfTag         = null; }
                if (dWriteFactory != null) { dWriteFactory.Dispose(); dWriteFactory = null; }
            }
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════════
        #region OnBarUpdate

        protected override void OnBarUpdate()
        {
            // ── BIP 0: primary series ────────────────────────────────────────────
            if (BarsInProgress == 0)
            {
                if (CurrentBar < AtrPeriod + 1) return;

                // Finalise previous bar's order-flow data when a new bar starts
                if (CurrentBar != lastBubbleBar && lastBubbleBar >= 0 && ShowBubbles)
                {
                    double totalVol = barBuyVol + barSellVol;
                    if (totalVol > 0)
                    {
                        bool isBuy = barBuyVol >= barSellVol;
                        int  off   = CurrentBar - lastBubbleBar;
                        double bblPrice = off < CurrentBar
                            ? (isBuy ? High[off] : Low[off])
                            : Close[0];
                        bubbles.Add(new BubblePoint
                        {
                            BarIndex = lastBubbleBar,
                            Price    = bblPrice,
                            Volume   = totalVol,
                            IsBuy    = isBuy
                        });
                        if (bubbles.Count > 500)
                            bubbles.RemoveRange(0, bubbles.Count - 500);
                    }
                    barBuyVol  = 0;
                    barSellVol = 0;
                }

                // Finalise previous bar's cumulative-delta data
                if (CurrentBar != lastCdBar && lastCdBar >= 0 && ShowCdPanel)
                {
                    cumDelta += barCdDelta;
                    cdBars.Add(new CdBar
                    {
                        BarIndex = lastCdBar,
                        Delta    = barCdDelta,
                        CumDelta = cumDelta,
                        IsUp     = barCdDelta >= 0
                    });
                    if (cdBars.Count > 600)
                        cdBars.RemoveRange(0, cdBars.Count - 600);
                    barCdDelta = 0;
                }

                // Session reset
                if (Bars.IsFirstBarOfSession)
                {
                    vwapSumPV = 0;
                    vwapSumV  = 0;
                    sessionHigh = High[0];
                    sessionLow  = Low[0];
                    atrAlertFired.Clear();

                    if (CdSessionReset && ShowCdPanel)
                    {
                        cumDelta   = 0;
                        barCdDelta = 0;
                        cdBars.Clear();
                        cdDivBars.Clear();
                        cdDivKeys.Clear();
                    }
                }
                else
                {
                    sessionHigh = Math.Max(sessionHigh, High[0]);
                    sessionLow  = Math.Min(sessionLow,  Low[0]);
                }

                lastBubbleBar = CurrentBar;
                lastCdBar     = CurrentBar;

                // 1. ATR (Wilder via built-in ATR indicator)
                atr[0] = ATR(AtrPeriod)[0];

                // 2. Baseline — Wilder EMA of typical price
                double tp = (High[0] + Low[0] + Close[0]) / 3.0;
                double k  = 1.0 / AtrPeriod;
                mid[0]   = (CurrentBar == AtrPeriod) ? tp : mid[1] + k * (tp - mid[1]);
                upper[0] = mid[0] + BandMultiplier * atr[0];
                lower[0] = mid[0] - BandMultiplier * atr[0];

                // 3. Session VWAP
                vwapSumPV += tp * Volume[0];
                vwapSumV  += Volume[0];
                vwap[0]    = vwapSumV > 0 ? vwapSumPV / vwapSumV : Close[0];

                // 4. Market structure
                if (ShowStructure && CurrentBar >= StructureStrength * 2 + 1)
                    CheckSwingPoint();

                // 5. FVG update
                if (ShowFvg && CurrentBar >= 2)
                    UpdateFvgState();

                // 6. CD divergence check
                if (ShowCdPanel && ShowCdDivergence && cdBars.Count >= 4)
                    CheckCdDivergence();

                // 7. ATR projection alerts
                if (AlertAtrTargetHitEnable && State == State.Realtime)
                    CheckAtrProjectionAlerts();

                // 8. VWAP cross alert
                if (AlertVwapCrossEnable && State == State.Realtime && CurrentBar > AtrPeriod + 2)
                {
                    int s0 = Math.Sign(Close[0] - vwap[0]);
                    int s1 = Math.Sign(Close[1] - vwap[1]);
                    if (s0 != s1 && s0 != 0 && s1 != 0)
                        FireAlert("VwapCross", AlertVwapCrossEnable, AlertVwapCrossPriority,
                            "VWAP Cross " + (s0 > 0 ? "Bullish" : "Bearish"),
                            AlertVwapCrossSound, AlertVwapCrossRearm);
                }

                return;
            }

            // ── BIP 1: Day series — prior day H/L/C and daily ATR ────────────────
            if (BarsInProgress == 1 && BarsArray[1].Count > 1)
            {
                yHi = BarsArray[1].GetHigh(1);
                yLo = BarsArray[1].GetLow(1);
                yCl = BarsArray[1].GetClose(1);
                dDP = (yHi + yLo + yCl) / 3.0;

                if (BarsArray[1].Count >= AtrPeriod + 2)
                {
                    double sumTr = 0;
                    for (int i = 1; i <= AtrPeriod; i++)
                    {
                        double h  = BarsArray[1].GetHigh(i);
                        double l  = BarsArray[1].GetLow(i);
                        // Clamp to last valid index so the oldest bar uses H-L as its true range
                        int    pi = Math.Min(i + 1, BarsArray[1].Count - 1);
                        double pc = BarsArray[1].GetClose(pi);
                        double tr = Math.Max(h - l,
                                    Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
                        sumTr += tr;
                    }
                    dailyAtr = sumTr / AtrPeriod;
                }
                return;
            }

            // ── BIP 2: Week series — weekly ATR ─────────────────────────────────
            if (BarsInProgress == 2 && BarsArray[2].Count > WeeklyAtrPeriod + 1)
            {
                double sumTr = 0;
                for (int i = 1; i <= WeeklyAtrPeriod; i++)
                {
                    double h  = BarsArray[2].GetHigh(i);
                    double l  = BarsArray[2].GetLow(i);
                    // Clamp to last valid index so the oldest bar uses H-L as its true range
                    int    pi = Math.Min(i + 1, BarsArray[2].Count - 1);
                    double pc = BarsArray[2].GetClose(pi);
                    double tr = Math.Max(h - l,
                                Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
                    sumTr += tr;
                }
                weeklyAtr = sumTr / WeeklyAtrPeriod;
            }
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════════
        #region OnMarketData

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType == MarketDataType.Ask) { currentAsk = e.Price; return; }
            if (e.MarketDataType == MarketDataType.Bid) { currentBid = e.Price; return; }
            if (e.MarketDataType != MarketDataType.Last) return;

            double price = e.Price;
            double vol   = e.Volume;

            // Order-flow delta
            if (DeltaMode == PtsDeltaMode.TrueBidAskDelta)
            {
                if      (price >= currentAsk && currentAsk > 0) barBuyVol  += vol;
                else if (price <= currentBid && currentBid > 0) barSellVol += vol;
                else { barBuyVol += vol * 0.5; barSellVol += vol * 0.5; }
            }
            else
            {
                double mid0 = (High[0] + Low[0]) / 2.0;
                if (price >= mid0) barBuyVol  += vol;
                else               barSellVol += vol;
            }

            // Volume-profile bucket
            if (ShowVp && vpTickSize > 0)
            {
                int bucket = (int)Math.Round(price / vpTickSize);
                if (!vpProfile.ContainsKey(bucket))
                    vpProfile[bucket] = new VpBucket();
                bool isBuy = currentAsk > 0 && price >= currentAsk;
                if (isBuy) vpProfile[bucket].BuyVol  += vol;
                else       vpProfile[bucket].SellVol += vol;
            }

            // Cumulative-delta per tick
            if (ShowCdPanel)
            {
                double tickDelta;
                if (DeltaMode == PtsDeltaMode.TrueBidAskDelta)
                    tickDelta = (currentAsk > 0 && price >= currentAsk) ? vol : -vol;
                else
                    tickDelta = (price >= (High[0] + Low[0]) / 2.0) ? vol : -vol;
                barCdDelta += tickDelta;
            }
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════════
        #region OnMarketDepth

        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (!ShowHeatmap) return;

            bool isBid = e.MarketDataType == MarketDataType.Bid;
            bool isAsk = e.MarketDataType == MarketDataType.Ask;
            if (!isBid && !isAsk) return;

            double price = e.Price;
            long   size  = e.Volume;
            var    key   = (isBid, e.Position);

            if (e.Operation == Operation.Remove || size == 0)
                depthSnap.Remove(key);
            else
                depthSnap[key] = (price, size);

            // Throttled history sampling
            DateTime now = DateTime.Now;
            double intervalSec = 1.0 / Math.Max(1, HeatmapSampleRateHz);
            if ((now - lastDepthSample).TotalSeconds >= intervalSec)
            {
                lastDepthSample = now;
                var snap = new DepthSnap { Time = now };
                foreach (var kv in depthSnap)
                    snap.Levels.Add((kv.Key.IsBid, kv.Value.Price, kv.Value.Size));
                depthHistory.Add(snap);

                double cutoff = HeatmapHistorySeconds;
                depthHistory.RemoveAll(s => (now - s.Time).TotalSeconds > cutoff);
            }

            // Wall detection
            if (WallThreshold > 0)
            {
                string wallKey = (isBid ? "Bid" : "Ask") + "_" + price.ToString("F4");
                bool   isRemove = (e.Operation == Operation.Remove || size == 0);

                if (!isRemove && size >= WallThreshold)
                {
                    if (!activeWalls.Contains(wallKey))
                    {
                        activeWalls.Add(wallKey);
                        FireAlert("WallAdded_" + wallKey, AlertWallAddedEnable, AlertWallAddedPriority,
                            (isBid ? "Bid" : "Ask") + " wall " + size + " @ " + price.ToString("F2"),
                            AlertWallAddedSound, AlertWallAddedRearm);
                    }
                }
                else if (activeWalls.Contains(wallKey))
                {
                    activeWalls.Remove(wallKey);
                    FireAlert("WallPulled_" + wallKey, AlertWallPulledEnable, AlertWallPulledPriority,
                        (isBid ? "Bid" : "Ask") + " wall pulled @ " + price.ToString("F2"),
                        AlertWallPulledSound, AlertWallPulledRearm);
                }
            }
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════════
        #region OnRender

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (RenderTarget == null) return;

            if (!dxReady)
            {
                try { CreateDXResources(); }
                catch (Exception ex) { Print("ProTraderSuite CreateDXResources: " + ex.Message); return; }
            }

            if (!dxReady) return;

            try
            {
                if (ShowBands)     RenderBands(chartControl, chartScale);
                if (ShowBaseline)  RenderBaseline(chartControl, chartScale);
                if (ShowVwap)      RenderVwap(chartControl, chartScale);
                if (ShowFvg)       RenderFvgs(chartControl, chartScale);
                if (ShowStructure) RenderStructure(chartControl, chartScale);
                if (ShowKeyLevels) RenderKeyLevels(chartControl, chartScale);
                if (ShowAtrProj)   RenderAtrProjections(chartControl, chartScale);
                if (ShowBubbles)   RenderBubbles(chartControl, chartScale);
                if (ShowVp)        RenderVolumeProfile(chartControl, chartScale);
                if (ShowHeatmap)   RenderHeatmap(chartControl, chartScale);
                if (ShowCdPanel)   RenderCdPanel(chartControl, chartScale);
            }
            catch (SharpDX.SharpDXException sdxEx)
            {
                Print("ProTraderSuite SharpDX error: " + sdxEx.Message);
                dxReady = false;
                DisposeDXResources();
            }
            catch (Exception ex)
            {
                Print("ProTraderSuite OnRender: " + ex.Message);
            }
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════════
        #region Sub-render methods

        // ── 1. ATR Bands ─────────────────────────────────────────────────────────
        private void RenderBands(ChartControl chartControl, ChartScale chartScale)
        {
            int firstBar = Math.Max(ChartBars.FromIndex, AtrPeriod + 1);
            int lastBar  = Math.Min(ChartBars.ToIndex, CurrentBar);
            if (lastBar < firstBar) return;

            var upperPts = new List<Vector2>(lastBar - firstBar + 2);
            var midPts   = new List<Vector2>(lastBar - firstBar + 2);
            var lowerPts = new List<Vector2>(lastBar - firstBar + 2);

            for (int i = firstBar; i <= lastBar; i++)
            {
                int offset = CurrentBar - i;
                if (offset < 0 || offset >= upper.Count) continue;

                float x = (float)chartControl.GetXByBarIndex(ChartBars, i);
                upperPts.Add(new Vector2(x, (float)chartScale.GetYByValue(upper[offset])));
                midPts  .Add(new Vector2(x, (float)chartScale.GetYByValue(mid  [offset])));
                lowerPts.Add(new Vector2(x, (float)chartScale.GetYByValue(lower[offset])));
            }

            if (upperPts.Count < 2) return;

            FillBand(upperPts, midPts,   dxBandUpperFill);
            FillBand(midPts,   lowerPts, dxBandLowerFill);

            for (int i = 0; i < upperPts.Count - 1; i++)
            {
                RenderTarget.DrawLine(upperPts[i], upperPts[i + 1], dxBandUpperLine, 1.5f);
                RenderTarget.DrawLine(lowerPts[i], lowerPts[i + 1], dxBandLowerLine, 1.5f);
            }
        }

        // ── 2. Trend Baseline ────────────────────────────────────────────────────
        private void RenderBaseline(ChartControl chartControl, ChartScale chartScale)
        {
            int firstBar = Math.Max(ChartBars.FromIndex, AtrPeriod + 1);
            int lastBar  = Math.Min(ChartBars.ToIndex, CurrentBar);
            if (lastBar < firstBar) return;

            for (int i = firstBar; i <= lastBar; i++)
            {
                int offset = CurrentBar - i;
                if (offset < 0 || offset >= mid.Count) continue;

                float x = (float)chartControl.GetXByBarIndex(ChartBars, i);
                float y = (float)chartScale.GetYByValue(mid[offset]);
                RenderTarget.DrawEllipse(
                    new DxEllipse(new Vector2(x, y), 2f, 2f),
                    dxBaseline, 1.5f);
            }
        }

        // ── 3. Session VWAP ──────────────────────────────────────────────────────
        private void RenderVwap(ChartControl chartControl, ChartScale chartScale)
        {
            int firstBar = Math.Max(ChartBars.FromIndex, AtrPeriod + 1);
            int lastBar  = Math.Min(ChartBars.ToIndex, CurrentBar);
            if (lastBar < firstBar) return;

            Vector2? prev = null;
            for (int i = firstBar; i <= lastBar; i++)
            {
                int offset = CurrentBar - i;
                if (offset < 0 || offset >= vwap.Count) continue;

                float x = (float)chartControl.GetXByBarIndex(ChartBars, i);
                float y = (float)chartScale.GetYByValue(vwap[offset]);
                var   cur = new Vector2(x, y);

                if (prev.HasValue)
                    RenderTarget.DrawLine(prev.Value, cur, dxVwap, 2f);
                prev = cur;
            }

            // Label at right edge
            if (prev.HasValue && tfTag != null)
            {
                var rect = new SharpDX.RectangleF(prev.Value.X + 4, prev.Value.Y - 7, 55, 14);
                RenderTarget.DrawText("VWAP", tfTag, rect, dxVwap);
            }
        }

        // ── 4. Market Structure ──────────────────────────────────────────────────
        private void RenderStructure(ChartControl chartControl, ChartScale chartScale)
        {
            if (swings.Count == 0) return;

            int firstBar = ChartBars.FromIndex;
            int lastBar  = ChartBars.ToIndex;

            foreach (var sw in swings)
            {
                if (sw.BarIndex < firstBar || sw.BarIndex > lastBar) continue;

                float x = (float)chartControl.GetXByBarIndex(ChartBars, sw.BarIndex);
                float y = (float)chartScale.GetYByValue(sw.Price);

                SharpDX.Direct2D1.SolidColorBrush tagBrush;
                switch (sw.Label)
                {
                    case "HH": tagBrush = dxHH; break;
                    case "HL": tagBrush = dxHL; break;
                    case "LH": tagBrush = dxLH; break;
                    default:   tagBrush = dxLL; break;
                }

                float tagW = 26f, tagH = 14f;
                float tx = x - tagW / 2f;
                float ty = sw.IsHigh ? y - tagH - 4f : y + 4f;

                RenderTarget.FillRectangle(new SharpDX.RectangleF(tx, ty, tagW, tagH), tagBrush);

                if (tfTag != null)
                    RenderTarget.DrawText(sw.Label, tfTag,
                        new SharpDX.RectangleF(tx + 2, ty + 1, tagW - 4, tagH - 2),
                        dxCdBg ?? tagBrush);
            }

            RenderBosChoch(chartControl, chartScale);
        }

        private void RenderBosChoch(ChartControl chartControl, ChartScale chartScale)
        {
            var highs = swings.Where(s => s.IsHigh).ToList();
            var lows  = swings.Where(s => !s.IsHigh).ToList();

            for (int i = 1; i < highs.Count; i++)
            {
                var prev = highs[i - 1];
                var curr = highs[i];
                var brush = (curr.Label == "HH") ? dxBos : dxChoch;

                float x1 = (float)chartControl.GetXByBarIndex(ChartBars, prev.BarIndex);
                float y1 = (float)chartScale.GetYByValue(prev.Price);
                float x2 = (float)chartControl.GetXByBarIndex(ChartBars, curr.BarIndex);
                float y2 = (float)chartScale.GetYByValue(curr.Price);
                RenderTarget.DrawLine(new Vector2(x1, y1), new Vector2(x2, y2), brush, 1.5f);
            }

            for (int i = 1; i < lows.Count; i++)
            {
                var prev = lows[i - 1];
                var curr = lows[i];
                var brush = (curr.Label == "LL") ? dxBos : dxChoch;

                float x1 = (float)chartControl.GetXByBarIndex(ChartBars, prev.BarIndex);
                float y1 = (float)chartScale.GetYByValue(prev.Price);
                float x2 = (float)chartControl.GetXByBarIndex(ChartBars, curr.BarIndex);
                float y2 = (float)chartScale.GetYByValue(curr.Price);
                RenderTarget.DrawLine(new Vector2(x1, y1), new Vector2(x2, y2), brush, 1.5f);
            }
        }

        // ── 5. Fair Value Gaps ───────────────────────────────────────────────────
        private void RenderFvgs(ChartControl chartControl, ChartScale chartScale)
        {
            if (fvgs.Count == 0) return;

            float chartRight = RenderTarget.Size.Width;

            foreach (var fvg in fvgs)
            {
                if (fvg.Mitigated && !fvg.Inverted) continue;
                if (!ShowIFvg && fvg.Inverted) continue;

                float x1    = (float)chartControl.GetXByBarIndex(ChartBars, fvg.StartBar);
                float x2    = chartRight;
                float yTop  = (float)chartScale.GetYByValue(fvg.TopPrice);
                float yBot  = (float)chartScale.GetYByValue(fvg.BotPrice);

                if (yTop > yBot) { float t = yTop; yTop = yBot; yBot = t; }
                float h = yBot - yTop;
                if (h < 1f) h = 1f;

                SharpDX.Direct2D1.SolidColorBrush fill =
                    fvg.Inverted ? dxFvgI : (fvg.IsBullish ? dxFvgBull : dxFvgBear);

                RenderTarget.FillRectangle(
                    new SharpDX.RectangleF(x1, yTop, x2 - x1, h), fill);
                RenderTarget.DrawRectangle(
                    new SharpDX.RectangleF(x1, yTop, x2 - x1, h), fill, 0.5f);
            }
        }

        // ── 6. Key Levels ────────────────────────────────────────────────────────
        private void RenderKeyLevels(ChartControl chartControl, ChartScale chartScale)
        {
            if (yHi <= 0) return;

            DrawHLine(chartScale, yHi, "PDH",    dxKeyLevel, 1.5f);
            DrawHLine(chartScale, yLo, "PDL",    dxKeyLevel, 1.5f);
            DrawHLine(chartScale, yCl, "PDC",    dxKeyLevel, 1f);
            DrawHLine(chartScale, dDP, "DPivot", dxKeyLevel, 1f);
        }

        // ── 7. ATR Projections ───────────────────────────────────────────────────
        private void RenderAtrProjections(ChartControl chartControl, ChartScale chartScale)
        {
            if (dailyAtr <= 0 && weeklyAtr <= 0) return;

            var levels = new (double Price, string Label, bool Weekly)[]
            {
                (sessionLow  + dailyAtr,        "LOD+1D",  false),
                (sessionHigh - dailyAtr,        "HOD-1D",  false),
                (sessionLow  + dailyAtr  * 0.5, "LOD+.5D", false),
                (sessionHigh - dailyAtr  * 0.5, "HOD-.5D", false),
                (sessionLow  + weeklyAtr * 0.5, "LOD+.5W", true),
                (sessionHigh - weeklyAtr * 0.5, "HOD-.5W", true),
            };

            float totalH = RenderTarget.Size.Height;

            foreach (var (price, label, weekly) in levels)
            {
                if (price <= 0) continue;
                float y = (float)chartScale.GetYByValue(price);
                if (y < 0 || y > totalH) continue;

                var brush = weekly ? dxWeeklyProj : dxDailyProj;
                DrawHLine(chartScale, price, label, brush, 1f);
            }
        }

        // ── 8. Order Flow Bubbles ────────────────────────────────────────────────
        private void RenderBubbles(ChartControl chartControl, ChartScale chartScale)
        {
            if (bubbles.Count == 0) return;

            int firstBar = ChartBars.FromIndex;
            int lastBar  = ChartBars.ToIndex;

            var visible = bubbles
                .Where(b => b.BarIndex >= firstBar && b.BarIndex <= lastBar)
                .ToList();
            if (visible.Count == 0) return;

            double maxVol = visible.Max(b => b.Volume);
            if (maxVol <= 0) maxVol = 1;

            foreach (var b in visible)
            {
                float x = (float)chartControl.GetXByBarIndex(ChartBars, b.BarIndex);
                float y = (float)chartScale.GetYByValue(b.Price);
                float r = Math.Max(3f, (float)(BubbleMaxPx * Math.Sqrt(b.Volume / maxVol)));

                var brush = b.IsBuy ? dxBubbleBuy : dxBubbleSell;
                RenderTarget.FillEllipse(new DxEllipse(new Vector2(x, y), r, r), brush);

                if (ShowVolumeText && r > 10f && tfTag != null)
                {
                    string volStr = FormatVolume(b.Volume);
                    var rect = new SharpDX.RectangleF(x - r, y - 7f, r * 2f, 14f);
                    RenderTarget.DrawText(volStr, tfTag, rect, brush);
                }
            }
        }

        // ── 9. Volume Profile ────────────────────────────────────────────────────
        private void RenderVolumeProfile(ChartControl chartControl, ChartScale chartScale)
        {
            if (vpProfile.Count == 0) return;

            float totalW  = RenderTarget.Size.Width;
            float hmWidth = ShowHeatmap ? HeatmapStripWidthPx : 0f;
            float vpRight = totalW - hmWidth;
            float vpLeft  = vpRight - VpWidthPx;

            double maxBucketVol = vpProfile.Values.Max(b => b.Total);
            if (maxBucketVol <= 0) return;

            double totalVol = vpProfile.Values.Sum(b => b.Total);
            double vaTarget = totalVol * VpValueAreaPct / 100.0;

            // POC
            int pocBucket = vpProfile.OrderByDescending(kv => kv.Value.Total).First().Key;
            double pocPrice = pocBucket * vpTickSize;

            // Value area
            var sortedBuckets = vpProfile.OrderByDescending(kv => kv.Value.Total).ToList();
            double vaAccum = 0;
            var vaKeys = new HashSet<int>();
            foreach (var kv in sortedBuckets)
            {
                vaAccum += kv.Value.Total;
                vaKeys.Add(kv.Key);
                if (vaAccum >= vaTarget) break;
            }

            int vahKey = vaKeys.Count > 0 ? vaKeys.Max() : pocBucket;
            int valKey = vaKeys.Count > 0 ? vaKeys.Min() : pocBucket;
            double vahPrice = vahKey * vpTickSize;
            double valPrice = valKey * vpTickSize;

            double minPrice = chartScale.MinValue;
            double maxPrice = chartScale.MaxValue;

            foreach (var kv in vpProfile)
            {
                double bucketPrice = kv.Key * vpTickSize;
                if (bucketPrice < minPrice - vpTickSize || bucketPrice > maxPrice + vpTickSize)
                    continue;

                float yCen = (float)chartScale.GetYByValue(bucketPrice);
                float yTop = (float)chartScale.GetYByValue(bucketPrice + vpTickSize * 0.5);
                float yBot = (float)chartScale.GetYByValue(bucketPrice - vpTickSize * 0.5);
                if (yTop > yBot) { float t = yTop; yTop = yBot; yBot = t; }
                float rowH = Math.Max(1f, yBot - yTop);

                bool isPoc = (kv.Key == pocBucket);
                bool isVa  = vaKeys.Contains(kv.Key);

                if (VpSplitBuySell)
                {
                    float buyW  = (float)(VpWidthPx * kv.Value.BuyVol  / maxBucketVol);
                    float sellW = (float)(VpWidthPx * kv.Value.SellVol / maxBucketVol);
                    float splitMidpoint = vpLeft + (float)(VpWidthPx * kv.Value.Total / maxBucketVol / 2f);

                    RenderTarget.FillRectangle(
                        new SharpDX.RectangleF(splitMidpoint, yTop, buyW / 2f, rowH),
                        isPoc ? dxVpPoc : (isVa ? dxVpVa : dxVpBuy));
                    RenderTarget.FillRectangle(
                        new SharpDX.RectangleF(vpLeft, yTop, sellW / 2f, rowH),
                        isPoc ? dxVpPoc : (isVa ? dxVpVa : dxVpSell));
                }
                else
                {
                    float barW = (float)(VpWidthPx * kv.Value.Total / maxBucketVol);
                    RenderTarget.FillRectangle(
                        new SharpDX.RectangleF(vpLeft, yTop, barW, rowH),
                        isPoc ? dxVpPoc : (isVa ? dxVpVa : dxVpTotal));
                }
            }

            // POC, VAH, VAL horizontal lines
            if (pocPrice > 0)
                DrawHLine(chartScale, pocPrice, "POC", dxVpPoc, 1f);
            if (vahPrice > 0)
                DrawHLine(chartScale, vahPrice, "VAH", dxVpVa, 1f);
            if (valPrice > 0)
                DrawHLine(chartScale, valPrice, "VAL", dxVpVa, 1f);
        }

        // ── 10. DOM Heatmap ──────────────────────────────────────────────────────
        private void RenderHeatmap(ChartControl chartControl, ChartScale chartScale)
        {
            float totalW   = RenderTarget.Size.Width;
            float totalH   = RenderTarget.Size.Height;
            float stripLeft= totalW - HeatmapStripWidthPx;

            double minPrice = chartScale.MinValue;
            double maxPrice = chartScale.MaxValue;

            // Aggregate data depending on mode
            var aggregated = new Dictionary<(bool IsBid, double Price), long>();

            if (HeatmapMode == PtsHeatmapMode.SnapshotOnly || HeatmapMode == PtsHeatmapMode.Both)
            {
                foreach (var kv in depthSnap)
                    AddDepthEntry(aggregated, kv.Key.IsBid, kv.Value.Price, kv.Value.Size);
            }

            if (HeatmapMode == PtsHeatmapMode.HistoryOnly || HeatmapMode == PtsHeatmapMode.Both)
            {
                foreach (var snap in depthHistory)
                    foreach (var lvl in snap.Levels)
                        AddDepthEntry(aggregated, lvl.IsBid, lvl.Price, lvl.Size);
            }

            if (aggregated.Count == 0) return;

            long maxBid = 1, maxAsk = 1;
            foreach (var kv in aggregated)
            {
                if (kv.Key.IsBid) maxBid = Math.Max(maxBid, kv.Value);
                else              maxAsk = Math.Max(maxAsk, kv.Value);
            }

            float halfW  = HeatmapStripWidthPx / 2f;
            float tickPx = (float)Math.Abs(chartScale.GetYByValue(0) - chartScale.GetYByValue(TickSize));
            float rowH   = Math.Max(1f, tickPx);

            foreach (var kv in aggregated)
            {
                double price = kv.Key.Price;
                if (price < minPrice || price > maxPrice) continue;

                bool   isBid  = kv.Key.IsBid;
                long   size   = kv.Value;
                long   maxSz  = isBid ? maxBid : maxAsk;
                float  ratio  = (float)(Math.Log(size + 1) / Math.Log(maxSz + 1));
                float  barW   = halfW * ratio;
                float  y      = (float)chartScale.GetYByValue(price) - rowH / 2f;

                var baseBrush  = isBid ? dxHeatBid : dxHeatAsk;
                float savedOp  = baseBrush.Opacity;
                baseBrush.Opacity = ratio * (HeatmapMaxOpacity / 100f);

                float xLeft = isBid ? (stripLeft + halfW - barW) : (stripLeft + halfW);
                RenderTarget.FillRectangle(
                    new SharpDX.RectangleF(xLeft, y, barW, rowH), baseBrush);

                if (ShowSizeLabels && barW > 28f && tfTag != null)
                {
                    var rect = new SharpDX.RectangleF(xLeft + 2, y, barW - 4, rowH);
                    RenderTarget.DrawText(FormatVolume(size), tfTag, rect, baseBrush);
                }

                baseBrush.Opacity = savedOp;
            }

            // Divider between bid and ask sides
            RenderTarget.DrawLine(
                new Vector2(stripLeft + halfW, 0),
                new Vector2(stripLeft + halfW, totalH),
                dxKeyLevel ?? dxBaseline, 0.5f);
        }

        private static void AddDepthEntry(
            Dictionary<(bool, double), long> dict, bool isBid, double price, long size)
        {
            var k = (isBid, price);
            if (dict.ContainsKey(k)) dict[k] = Math.Max(dict[k], size);
            else                     dict[k]  = size;
        }

        // ── 11. Cumulative Delta Panel ───────────────────────────────────────────
        private void RenderCdPanel(ChartControl chartControl, ChartScale chartScale)
        {
            if (cdBars.Count == 0) return;

            float totalW = RenderTarget.Size.Width;
            float totalH = RenderTarget.Size.Height;

            float panelTop = CdPanelPos == PtsCdPanelPos.Bottom
                ? totalH - CdPanelHeightPx
                : 0f;
            float panelBot = panelTop + CdPanelHeightPx;
            float halfH    = CdPanelHeightPx / 2f;
            float midY     = panelTop + halfH;

            // Background
            dxCdBg.Opacity = CdOpacity / 100f;
            RenderTarget.FillRectangle(
                new SharpDX.RectangleF(0, panelTop, totalW, CdPanelHeightPx), dxCdBg);
            dxCdBg.Opacity = 1f;

            // Zero line
            RenderTarget.DrawLine(
                new Vector2(0, midY), new Vector2(totalW, midY),
                dxCdZero, 0.5f);

            // Determine range
            int firstBar = ChartBars.FromIndex;
            int lastBar  = ChartBars.ToIndex;

            var visible = cdBars
                .Where(c => c.BarIndex >= firstBar && c.BarIndex <= lastBar)
                .ToList();
            if (visible.Count == 0) return;

            double cdMax = visible.Max(c => Math.Abs(c.CumDelta));
            if (cdMax <= 0) cdMax = 1;

            float barHalfW = Math.Max(1f, (float)chartControl.BarWidth / 2f);

            foreach (var cb in visible)
            {
                float x    = (float)chartControl.GetXByBarIndex(ChartBars, cb.BarIndex);
                float barH = (float)(halfH * cb.CumDelta / cdMax);
                float y1   = midY;
                float y2   = midY - barH;

                var brush  = cb.IsUp ? dxCdUp : dxCdDown;
                float rectY = Math.Min(y1, y2);
                float rectH = Math.Max(1f, Math.Abs(y2 - y1));

                RenderTarget.FillRectangle(
                    new SharpDX.RectangleF(x - barHalfW, rectY, barHalfW * 2f, rectH), brush);
            }

            // Divergence dots
            foreach (var (barIdx, isBull) in cdDivBars)
            {
                if (barIdx < firstBar || barIdx > lastBar) continue;

                float x = (float)chartControl.GetXByBarIndex(ChartBars, barIdx);
                float y = isBull ? panelBot - 6f : panelTop + 6f;
                RenderTarget.FillEllipse(new DxEllipse(new Vector2(x, y), 4f, 4f), dxCdDiv);
            }

            // Panel label
            if (tfTag != null)
            {
                var rect = new SharpDX.RectangleF(4, panelTop + 2, 40, 13);
                RenderTarget.DrawText("CΔ", tfTag, rect, dxCdZero);
            }
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════════
        #region DX Resource management

        public override void OnRenderTargetChanged()
        {
            DisposeDXResources();
            if (RenderTarget != null)
                CreateDXResources();
        }

        private void CreateDXResources()
        {
            if (RenderTarget == null) return;

            dxReady = false;

            // Text format
            if (dWriteFactory != null)
            {
                if (tfTag != null) { tfTag.Dispose(); tfTag = null; }
                tfTag = new SharpDX.DirectWrite.TextFormat(
                    dWriteFactory, "Arial",
                    SharpDX.DirectWrite.FontWeight.Normal,
                    SharpDX.DirectWrite.FontStyle.Normal,
                    SharpDX.DirectWrite.FontStretch.Normal,
                    TagFontSize);
                tfTag.WordWrapping   = SharpDX.DirectWrite.WordWrapping.NoWrap;
                tfTag.TextAlignment  = SharpDX.DirectWrite.TextAlignment.Leading;
            }

            // Bands
            dxBandUpperFill = ToDx(BandUpperFillColor, BandUpperFillOpacity);
            dxBandLowerFill = ToDx(BandLowerFillColor, BandLowerFillOpacity);
            dxBandUpperLine = ToDx(BandUpperLineColor, BandUpperLineOpacity);
            dxBandLowerLine = ToDx(BandLowerLineColor, BandLowerLineOpacity);

            // Baseline
            dxBaseline = ToDx(BaselineColor, BaselineOpacity);

            // VWAP
            dxVwap = ToDx(VwapColor, VwapOpacity);

            // Structure
            dxHH    = ToDx(HhColor,    TagOpacity);
            dxHL    = ToDx(HlColor,    TagOpacity);
            dxLH    = ToDx(LhColor,    TagOpacity);
            dxLL    = ToDx(LlColor,    TagOpacity);
            dxBos   = ToDx(BosColor,   BosChochOpacity);
            dxChoch = ToDx(ChochColor, BosChochOpacity);

            // FVG
            dxFvgBull = ToDx(FvgBullColor, FvgOpacity);
            dxFvgBear = ToDx(FvgBearColor, FvgOpacity);
            dxFvgI    = ToDx(FvgIColor,    FvgOpacity);

            // Key levels
            dxKeyLevel = ToDx(KeyLevelColor, KeyLevelOpacity);

            // ATR projections
            dxDailyProj  = ToDx(DailyProjColor,  DailyProjOpacity);
            dxWeeklyProj = ToDx(WeeklyProjColor, WeeklyProjOpacity);

            // Bubbles
            dxBubbleBuy  = ToDx(BubbleBuyColor,  BubbleOpacity);
            dxBubbleSell = ToDx(BubbleSellColor, BubbleOpacity);

            // Volume profile
            dxVpBuy   = ToDx(VpBuyColor,   VpHistoOpacity);
            dxVpSell  = ToDx(VpSellColor,  VpHistoOpacity);
            dxVpTotal = ToDx(VpTotalColor, VpHistoOpacity);
            dxVpPoc   = ToDx(VpPocColor,   90);
            dxVpVa    = ToDx(VpVaColor,    70);

            // Heatmap
            dxHeatBid = ToDx(HeatmapBidColor, HeatmapMaxOpacity);
            dxHeatAsk = ToDx(HeatmapAskColor, HeatmapMaxOpacity);

            // CD panel
            dxCdUp   = ToDx(CdUpColor,       CdOpacity);
            dxCdDown = ToDx(CdDownColor,     CdOpacity);
            dxCdBg   = ToDx(CdBgColor,       80);
            dxCdZero = ToDx(CdZeroLineColor, 80);
            dxCdDiv  = ToDx(CdDivColor,      90);

            dxReady = true;
        }

        private void DisposeDXResources()
        {
            dxReady = false;

            if (dxBandUpperFill != null) { dxBandUpperFill.Dispose(); dxBandUpperFill = null; }
            if (dxBandLowerFill != null) { dxBandLowerFill.Dispose(); dxBandLowerFill = null; }
            if (dxBandUpperLine != null) { dxBandUpperLine.Dispose(); dxBandUpperLine = null; }
            if (dxBandLowerLine != null) { dxBandLowerLine.Dispose(); dxBandLowerLine = null; }
            if (dxBaseline      != null) { dxBaseline     .Dispose(); dxBaseline      = null; }
            if (dxVwap          != null) { dxVwap         .Dispose(); dxVwap          = null; }
            if (dxHH            != null) { dxHH           .Dispose(); dxHH            = null; }
            if (dxHL            != null) { dxHL           .Dispose(); dxHL            = null; }
            if (dxLH            != null) { dxLH           .Dispose(); dxLH            = null; }
            if (dxLL            != null) { dxLL           .Dispose(); dxLL            = null; }
            if (dxBos           != null) { dxBos          .Dispose(); dxBos           = null; }
            if (dxChoch         != null) { dxChoch        .Dispose(); dxChoch         = null; }
            if (dxFvgBull       != null) { dxFvgBull      .Dispose(); dxFvgBull       = null; }
            if (dxFvgBear       != null) { dxFvgBear      .Dispose(); dxFvgBear       = null; }
            if (dxFvgI          != null) { dxFvgI         .Dispose(); dxFvgI          = null; }
            if (dxKeyLevel      != null) { dxKeyLevel     .Dispose(); dxKeyLevel      = null; }
            if (dxDailyProj     != null) { dxDailyProj    .Dispose(); dxDailyProj     = null; }
            if (dxWeeklyProj    != null) { dxWeeklyProj   .Dispose(); dxWeeklyProj    = null; }
            if (dxBubbleBuy     != null) { dxBubbleBuy    .Dispose(); dxBubbleBuy     = null; }
            if (dxBubbleSell    != null) { dxBubbleSell   .Dispose(); dxBubbleSell    = null; }
            if (dxVpBuy         != null) { dxVpBuy        .Dispose(); dxVpBuy         = null; }
            if (dxVpSell        != null) { dxVpSell       .Dispose(); dxVpSell        = null; }
            if (dxVpTotal       != null) { dxVpTotal      .Dispose(); dxVpTotal       = null; }
            if (dxVpPoc         != null) { dxVpPoc        .Dispose(); dxVpPoc         = null; }
            if (dxVpVa          != null) { dxVpVa         .Dispose(); dxVpVa          = null; }
            if (dxHeatBid       != null) { dxHeatBid      .Dispose(); dxHeatBid       = null; }
            if (dxHeatAsk       != null) { dxHeatAsk      .Dispose(); dxHeatAsk       = null; }
            if (dxCdUp          != null) { dxCdUp         .Dispose(); dxCdUp          = null; }
            if (dxCdDown        != null) { dxCdDown       .Dispose(); dxCdDown        = null; }
            if (dxCdBg          != null) { dxCdBg         .Dispose(); dxCdBg          = null; }
            if (dxCdZero        != null) { dxCdZero       .Dispose(); dxCdZero        = null; }
            if (dxCdDiv         != null) { dxCdDiv        .Dispose(); dxCdDiv         = null; }
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════════
        #region Helper methods

        private SharpDX.Direct2D1.SolidColorBrush ToDx(System.Windows.Media.Brush wpfBrush, int opacityPct)
        {
            var mc = ((System.Windows.Media.SolidColorBrush)wpfBrush).Color;
            float a = (mc.A / 255f) * (opacityPct / 100f);
            return new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                new SharpDX.Color4(mc.R / 255f, mc.G / 255f, mc.B / 255f, a));
        }

        private SharpDX.Direct2D1.SolidColorBrush ToDxScaled(System.Windows.Media.Brush wpfBrush, float opacity)
        {
            var mc = ((System.Windows.Media.SolidColorBrush)wpfBrush).Color;
            return new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                new SharpDX.Color4(mc.R / 255f, mc.G / 255f, mc.B / 255f, opacity));
        }

        private void FireAlert(string key, bool enabled, Priority priority,
            string message, string soundFile, int rearmSec)
        {
            if (!enabled) return;
            if (State != State.Realtime) return;

            string fullSound = soundFile;
            if (!System.IO.Path.IsPathRooted(fullSound))
                fullSound = System.IO.Path.Combine(
                    NinjaTrader.Core.Globals.InstallDir, "sounds", fullSound);

            string id = "PTS_" + key + "_" + CurrentBar;
            try
            {
                Alert(id, priority, message, fullSound, rearmSec,
                    System.Windows.Media.Brushes.Black,
                    System.Windows.Media.Brushes.Yellow);
            }
            catch { }
        }

        private void CheckSwingPoint()
        {
            int s = StructureStrength;
            if (CurrentBar < s * 2 + 1) return;

            bool isSHigh = true, isSLow = true;
            // Pivot high at High[s]: must be greater than the s bars to the right (newer: i = 0..s-1)
            // and the s bars to the left (older: i = s+1..2s, accessed as High[s+1+i] for i=0..s-1).
            for (int i = 0; i < s; i++)
            {
                if (High[s] <= High[i]    || High[s] <= High[s + 1 + i]) isSHigh = false;
                if (Low[s]  >= Low[i]     || Low[s]  >= Low[s  + 1 + i]) isSLow  = false;
            }

            int pivotBar = CurrentBar - s;

            if (isSHigh)
            {
                var lastHigh = swings.LastOrDefault(x => x.IsHigh);
                string label = (lastHigh == null || High[s] > lastHigh.Price) ? "HH" : "LH";

                swings.Add(new SwingPoint
                {
                    BarIndex = pivotBar, Price = High[s], IsHigh = true, Label = label
                });

                if (lastHigh != null && State == State.Realtime)
                {
                    if      (label == "HH" && lastHigh.Label == "HH")
                        FireAlert("BOS_H", AlertBosEnable, AlertBosPriority,
                            "BOS (Bullish) @ " + High[s].ToString("F2"),
                            AlertBosSound, AlertBosRearm);
                    else if (label == "LH" && lastHigh.Label == "HH")
                        FireAlert("CHoCH_H", AlertChochEnable, AlertChochPriority,
                            "CHoCH (Bearish) @ " + High[s].ToString("F2"),
                            AlertChochSound, AlertChochRearm);
                }
            }

            if (isSLow)
            {
                var lastLow = swings.LastOrDefault(x => !x.IsHigh);
                string label = (lastLow == null || Low[s] < lastLow.Price) ? "LL" : "HL";

                swings.Add(new SwingPoint
                {
                    BarIndex = pivotBar, Price = Low[s], IsHigh = false, Label = label
                });

                if (lastLow != null && State == State.Realtime)
                {
                    if      (label == "LL" && lastLow.Label == "LL")
                        FireAlert("BOS_L", AlertBosEnable, AlertBosPriority,
                            "BOS (Bearish) @ " + Low[s].ToString("F2"),
                            AlertBosSound, AlertBosRearm);
                    else if (label == "HL" && lastLow.Label == "LL")
                        FireAlert("CHoCH_L", AlertChochEnable, AlertChochPriority,
                            "CHoCH (Bullish) @ " + Low[s].ToString("F2"),
                            AlertChochSound, AlertChochRearm);
                }
            }

            if (swings.Count > 200)
                swings.RemoveRange(0, swings.Count - 200);
        }

        private void UpdateFvgState()
        {
            if (CurrentBar < 2) return;

            double minTicks = FvgMinTicks * TickSize;

            // Bullish FVG: gap between candle[2] high and candle[0] low
            if (Low[0] - High[2] >= minTicks)
            {
                bool passes = !RequireDisplacement ||
                    Math.Abs(Close[1] - Open[1]) >= DisplacementMultiplier * atr[0];
                if (passes)
                    fvgs.Add(new FvgBox
                    {
                        TopPrice = Low[0], BotPrice = High[2],
                        StartBar = CurrentBar - 2, IsBullish = true
                    });
            }

            // Bearish FVG: gap between candle[0] high and candle[2] low
            if (Low[2] - High[0] >= minTicks)
            {
                bool passes = !RequireDisplacement ||
                    Math.Abs(Close[1] - Open[1]) >= DisplacementMultiplier * atr[0];
                if (passes)
                    fvgs.Add(new FvgBox
                    {
                        TopPrice = Low[2], BotPrice = High[0],
                        StartBar = CurrentBar - 2, IsBullish = false
                    });
            }

            // Check mitigation
            foreach (var fvg in fvgs)
            {
                if (fvg.Mitigated) continue;
                bool mit = fvg.IsBullish
                    ? Close[0] < fvg.BotPrice
                    : Close[0] > fvg.TopPrice;
                if (!mit) continue;

                fvg.Mitigated = true;
                FireAlert("FvgFill", AlertFvgFillEnable, AlertFvgFillPriority,
                    (fvg.IsBullish ? "Bull" : "Bear") + " FVG filled @ " + Close[0].ToString("F2"),
                    AlertFvgFillSound, AlertFvgFillRearm);

                if (ShowIFvg)
                {
                    fvg.Inverted = true;
                    FireAlert("IFvgCreated", AlertIFvgCreatedEnable, AlertIFvgCreatedPriority,
                        "iFVG created @ " + Close[0].ToString("F2"),
                        AlertIFvgCreatedSound, AlertIFvgCreatedRearm);
                }
            }

            if (fvgs.Count > 150)
                fvgs.RemoveRange(0, fvgs.Count - 150);
        }

        private void CheckCdDivergence()
        {
            var priceHighs = swings.Where(s =>  s.IsHigh).ToList();
            var priceLows  = swings.Where(s => !s.IsHigh).ToList();

            // Bear divergence: price HH while CD makes LH
            if (priceHighs.Count >= 2)
            {
                var ph1 = priceHighs[priceHighs.Count - 2];
                var ph2 = priceHighs[priceHighs.Count - 1];
                var cd1 = cdBars.FirstOrDefault(c => c.BarIndex == ph1.BarIndex);
                var cd2 = cdBars.FirstOrDefault(c => c.BarIndex == ph2.BarIndex);

                if (cd1 != null && cd2 != null
                    && ph2.Price > ph1.Price
                    && cd2.CumDelta < cd1.CumDelta)
                {
                    string divKey = "BearDiv_" + ph2.BarIndex;
                    if (!cdDivKeys.Contains(divKey))
                    {
                        cdDivKeys.Add(divKey);
                        cdDivBars.Add((ph2.BarIndex, false));
                        FireAlert("CdDiv_Bear", AlertCdDivergenceEnable, AlertCdDivergencePriority,
                            "CD Bear Divergence @ " + ph2.Price.ToString("F2"),
                            AlertCdDivergenceSound, AlertCdDivergenceRearm);
                    }
                }
            }

            // Bull divergence: price LL while CD makes HL
            if (priceLows.Count >= 2)
            {
                var pl1 = priceLows[priceLows.Count - 2];
                var pl2 = priceLows[priceLows.Count - 1];
                var cd1 = cdBars.FirstOrDefault(c => c.BarIndex == pl1.BarIndex);
                var cd2 = cdBars.FirstOrDefault(c => c.BarIndex == pl2.BarIndex);

                if (cd1 != null && cd2 != null
                    && pl2.Price < pl1.Price
                    && cd2.CumDelta > cd1.CumDelta)
                {
                    string divKey = "BullDiv_" + pl2.BarIndex;
                    if (!cdDivKeys.Contains(divKey))
                    {
                        cdDivKeys.Add(divKey);
                        cdDivBars.Add((pl2.BarIndex, true));
                        FireAlert("CdDiv_Bull", AlertCdDivergenceEnable, AlertCdDivergencePriority,
                            "CD Bull Divergence @ " + pl2.Price.ToString("F2"),
                            AlertCdDivergenceSound, AlertCdDivergenceRearm);
                    }
                }
            }
        }

        private void CheckAtrProjectionAlerts()
        {
            if (dailyAtr <= 0 && weeklyAtr <= 0) return;

            var levels = new (double Price, string Name)[]
            {
                (sessionLow  + dailyAtr,        "LOD+1D"),
                (sessionHigh - dailyAtr,        "HOD-1D"),
                (sessionLow  + dailyAtr  * 0.5, "LOD+.5D"),
                (sessionHigh - dailyAtr  * 0.5, "HOD-.5D"),
                (sessionLow  + weeklyAtr * 0.5, "LOD+.5W"),
                (sessionHigh - weeklyAtr * 0.5, "HOD-.5W"),
            };

            foreach (var (price, name) in levels)
            {
                if (price <= 0) continue;
                string aKey = name + "_" + Math.Round(price / TickSize);
                if (atrAlertFired.Contains(aKey)) continue;

                bool hit = Close[0] >= price - TickSize && Close[0] <= price + TickSize;
                if (hit)
                {
                    atrAlertFired.Add(aKey);
                    FireAlert("AtrTgt_" + name, AlertAtrTargetHitEnable, AlertAtrTargetHitPriority,
                        "ATR Target " + name + " hit @ " + price.ToString("F2"),
                        AlertAtrTargetHitSound, AlertAtrTargetHitRearm);
                }
            }
        }

        private void FillBand(List<Vector2> topLine, List<Vector2> botLine,
            SharpDX.Direct2D1.SolidColorBrush brush)
        {
            if (topLine.Count < 2 || RenderTarget == null || brush == null) return;
            try
            {
                using (var geo  = new DxPathGeometry(RenderTarget.Factory))
                using (var sink = geo.Open())
                {
                    sink.BeginFigure(topLine[0], FigureBegin.Filled);
                    for (int i = 1; i < topLine.Count; i++)
                        sink.AddLine(topLine[i]);
                    for (int i = botLine.Count - 1; i >= 0; i--)
                        sink.AddLine(botLine[i]);
                    sink.EndFigure(FigureEnd.Closed);
                    sink.Close();
                    RenderTarget.FillGeometry(geo, brush);
                }
            }
            catch { }
        }

        private void FillQuad(float x1, float y1Top, float y1Bot,
                              float x2, float y2Top, float y2Bot,
                              SharpDX.Direct2D1.SolidColorBrush brush)
        {
            if (RenderTarget == null || brush == null) return;
            try
            {
                using (var geo  = new DxPathGeometry(RenderTarget.Factory))
                using (var sink = geo.Open())
                {
                    sink.BeginFigure(new Vector2(x1, y1Top), FigureBegin.Filled);
                    sink.AddLine(new Vector2(x2, y2Top));
                    sink.AddLine(new Vector2(x2, y2Bot));
                    sink.AddLine(new Vector2(x1, y1Bot));
                    sink.EndFigure(FigureEnd.Closed);
                    sink.Close();
                    RenderTarget.FillGeometry(geo, brush);
                }
            }
            catch { }
        }

        private void DrawHLine(ChartScale chartScale, double price, string label,
            SharpDX.Direct2D1.SolidColorBrush brush, float strokeWidth = 1f)
        {
            if (brush == null) return;
            float y      = (float)chartScale.GetYByValue(price);
            float totalW = RenderTarget.Size.Width;

            RenderTarget.DrawLine(new Vector2(0, y), new Vector2(totalW, y), brush, strokeWidth);

            if (tfTag != null && !string.IsNullOrEmpty(label))
            {
                var rect = new SharpDX.RectangleF(5f, y - 13f, 80f, 14f);
                RenderTarget.DrawText(label, tfTag, rect, brush);
            }
        }

        private static string FormatVolume(double vol)
        {
            if (vol >= 1_000_000) return (vol / 1_000_000).ToString("F1") + "M";
            if (vol >= 1_000)     return (vol / 1_000).ToString("F1")     + "K";
            return vol.ToString("F0");
        }

        private static string FormatVolume(long vol)
            => FormatVolume((double)vol);

        #endregion

        // ═══════════════════════════════════════════════════════════════════════════
        #region Properties

        // ─── 01. Bands ────────────────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Show Bands", Order = 1, GroupName = "01. Bands")]
        public bool ShowBands { get; set; }

        [Range(1, 200)]
        [NinjaScriptProperty]
        [Display(Name = "ATR Period", Order = 2, GroupName = "01. Bands")]
        public int AtrPeriod { get; set; }

        [Range(0.1, 10.0)]
        [NinjaScriptProperty]
        [Display(Name = "Band Multiplier", Order = 3, GroupName = "01. Bands")]
        public double BandMultiplier { get; set; }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Upper Fill Color", Order = 4, GroupName = "01. Bands")]
        public MediaBrush BandUpperFillColor { get; set; }

        [Browsable(false)]
        public string BandUpperFillColorSerializable
        {
            get { return Serialize.BrushToString(BandUpperFillColor); }
            set { BandUpperFillColor = Serialize.StringToBrush(value); }
        }

        [Range(0, 100)]
        [NinjaScriptProperty]
        [Display(Name = "Upper Fill Opacity %", Order = 5, GroupName = "01. Bands")]
        public int BandUpperFillOpacity { get; set; }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Lower Fill Color", Order = 6, GroupName = "01. Bands")]
        public MediaBrush BandLowerFillColor { get; set; }

        [Browsable(false)]
        public string BandLowerFillColorSerializable
        {
            get { return Serialize.BrushToString(BandLowerFillColor); }
            set { BandLowerFillColor = Serialize.StringToBrush(value); }
        }

        [Range(0, 100)]
        [NinjaScriptProperty]
        [Display(Name = "Lower Fill Opacity %", Order = 7, GroupName = "01. Bands")]
        public int BandLowerFillOpacity { get; set; }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Upper Line Color", Order = 8, GroupName = "01. Bands")]
        public MediaBrush BandUpperLineColor { get; set; }

        [Browsable(false)]
        public string BandUpperLineColorSerializable
        {
            get { return Serialize.BrushToString(BandUpperLineColor); }
            set { BandUpperLineColor = Serialize.StringToBrush(value); }
        }

        [Range(0, 100)]
        [NinjaScriptProperty]
        [Display(Name = "Upper Line Opacity %", Order = 9, GroupName = "01. Bands")]
        public int BandUpperLineOpacity { get; set; }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Lower Line Color", Order = 10, GroupName = "01. Bands")]
        public MediaBrush BandLowerLineColor { get; set; }

        [Browsable(false)]
        public string BandLowerLineColorSerializable
        {
            get { return Serialize.BrushToString(BandLowerLineColor); }
            set { BandLowerLineColor = Serialize.StringToBrush(value); }
        }

        [Range(0, 100)]
        [NinjaScriptProperty]
        [Display(Name = "Lower Line Opacity %", Order = 11, GroupName = "01. Bands")]
        public int BandLowerLineOpacity { get; set; }

        // ─── 02. Baseline ─────────────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Show Baseline", Order = 1, GroupName = "02. Baseline")]
        public bool ShowBaseline { get; set; }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Baseline Color", Order = 2, GroupName = "02. Baseline")]
        public MediaBrush BaselineColor { get; set; }

        [Browsable(false)]
        public string BaselineColorSerializable
        {
            get { return Serialize.BrushToString(BaselineColor); }
            set { BaselineColor = Serialize.StringToBrush(value); }
        }

        [Range(0, 100)]
        [NinjaScriptProperty]
        [Display(Name = "Baseline Opacity %", Order = 3, GroupName = "02. Baseline")]
        public int BaselineOpacity { get; set; }

        // ─── 03. VWAP ─────────────────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Show VWAP", Order = 1, GroupName = "03. VWAP")]
        public bool ShowVwap { get; set; }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "VWAP Color", Order = 2, GroupName = "03. VWAP")]
        public MediaBrush VwapColor { get; set; }

        [Browsable(false)]
        public string VwapColorSerializable
        {
            get { return Serialize.BrushToString(VwapColor); }
            set { VwapColor = Serialize.StringToBrush(value); }
        }

        [Range(0, 100)]
        [NinjaScriptProperty]
        [Display(Name = "VWAP Opacity %", Order = 3, GroupName = "03. VWAP")]
        public int VwapOpacity { get; set; }

        // ─── 04. Structure ────────────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Show Structure", Order = 1, GroupName = "04. Structure")]
        public bool ShowStructure { get; set; }

        [Range(1, 20)]
        [NinjaScriptProperty]
        [Display(Name = "Structure Strength", Order = 2, GroupName = "04. Structure")]
        public int StructureStrength { get; set; }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "HH Color", Order = 3, GroupName = "04. Structure")]
        public MediaBrush HhColor { get; set; }

        [Browsable(false)]
        public string HhColorSerializable
        {
            get { return Serialize.BrushToString(HhColor); }
            set { HhColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "HL Color", Order = 4, GroupName = "04. Structure")]
        public MediaBrush HlColor { get; set; }

        [Browsable(false)]
        public string HlColorSerializable
        {
            get { return Serialize.BrushToString(HlColor); }
            set { HlColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "LH Color", Order = 5, GroupName = "04. Structure")]
        public MediaBrush LhColor { get; set; }

        [Browsable(false)]
        public string LhColorSerializable
        {
            get { return Serialize.BrushToString(LhColor); }
            set { LhColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "LL Color", Order = 6, GroupName = "04. Structure")]
        public MediaBrush LlColor { get; set; }

        [Browsable(false)]
        public string LlColorSerializable
        {
            get { return Serialize.BrushToString(LlColor); }
            set { LlColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "BOS Color", Order = 7, GroupName = "04. Structure")]
        public MediaBrush BosColor { get; set; }

        [Browsable(false)]
        public string BosColorSerializable
        {
            get { return Serialize.BrushToString(BosColor); }
            set { BosColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "CHoCH Color", Order = 8, GroupName = "04. Structure")]
        public MediaBrush ChochColor { get; set; }

        [Browsable(false)]
        public string ChochColorSerializable
        {
            get { return Serialize.BrushToString(ChochColor); }
            set { ChochColor = Serialize.StringToBrush(value); }
        }

        [Range(0, 100)]
        [NinjaScriptProperty]
        [Display(Name = "Tag Opacity %", Order = 9, GroupName = "04. Structure")]
        public int TagOpacity { get; set; }

        [Range(0, 100)]
        [NinjaScriptProperty]
        [Display(Name = "BOS/CHoCH Opacity %", Order = 10, GroupName = "04. Structure")]
        public int BosChochOpacity { get; set; }

        // ─── 05. FVG ──────────────────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Show FVG", Order = 1, GroupName = "05. FVG")]
        public bool ShowFvg { get; set; }

        [Range(0, 50)]
        [NinjaScriptProperty]
        [Display(Name = "FVG Min Ticks", Order = 2, GroupName = "05. FVG")]
        public int FvgMinTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require Displacement", Order = 3, GroupName = "05. FVG")]
        public bool RequireDisplacement { get; set; }

        [Range(0.1, 5.0)]
        [NinjaScriptProperty]
        [Display(Name = "Displacement Multiplier", Order = 4, GroupName = "05. FVG")]
        public double DisplacementMultiplier { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show iFVG (Inverted)", Order = 5, GroupName = "05. FVG")]
        public bool ShowIFvg { get; set; }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Bull FVG Color", Order = 6, GroupName = "05. FVG")]
        public MediaBrush FvgBullColor { get; set; }

        [Browsable(false)]
        public string FvgBullColorSerializable
        {
            get { return Serialize.BrushToString(FvgBullColor); }
            set { FvgBullColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Bear FVG Color", Order = 7, GroupName = "05. FVG")]
        public MediaBrush FvgBearColor { get; set; }

        [Browsable(false)]
        public string FvgBearColorSerializable
        {
            get { return Serialize.BrushToString(FvgBearColor); }
            set { FvgBearColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "iFVG Color", Order = 8, GroupName = "05. FVG")]
        public MediaBrush FvgIColor { get; set; }

        [Browsable(false)]
        public string FvgIColorSerializable
        {
            get { return Serialize.BrushToString(FvgIColor); }
            set { FvgIColor = Serialize.StringToBrush(value); }
        }

        [Range(0, 100)]
        [NinjaScriptProperty]
        [Display(Name = "FVG Opacity %", Order = 9, GroupName = "05. FVG")]
        public int FvgOpacity { get; set; }

        // ─── 06. Key Levels ───────────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Show Key Levels", Order = 1, GroupName = "06. Key Levels")]
        public bool ShowKeyLevels { get; set; }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Key Level Color", Order = 2, GroupName = "06. Key Levels")]
        public MediaBrush KeyLevelColor { get; set; }

        [Browsable(false)]
        public string KeyLevelColorSerializable
        {
            get { return Serialize.BrushToString(KeyLevelColor); }
            set { KeyLevelColor = Serialize.StringToBrush(value); }
        }

        [Range(0, 100)]
        [NinjaScriptProperty]
        [Display(Name = "Key Level Opacity %", Order = 3, GroupName = "06. Key Levels")]
        public int KeyLevelOpacity { get; set; }

        // ─── 07. ATR Projections ──────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Show ATR Projections", Order = 1, GroupName = "07. ATR Projections")]
        public bool ShowAtrProj { get; set; }

        [Range(1, 52)]
        [NinjaScriptProperty]
        [Display(Name = "Weekly ATR Period", Order = 2, GroupName = "07. ATR Projections")]
        public int WeeklyAtrPeriod { get; set; }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Daily Proj Color", Order = 3, GroupName = "07. ATR Projections")]
        public MediaBrush DailyProjColor { get; set; }

        [Browsable(false)]
        public string DailyProjColorSerializable
        {
            get { return Serialize.BrushToString(DailyProjColor); }
            set { DailyProjColor = Serialize.StringToBrush(value); }
        }

        [Range(0, 100)]
        [NinjaScriptProperty]
        [Display(Name = "Daily Proj Opacity %", Order = 4, GroupName = "07. ATR Projections")]
        public int DailyProjOpacity { get; set; }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Weekly Proj Color", Order = 5, GroupName = "07. ATR Projections")]
        public MediaBrush WeeklyProjColor { get; set; }

        [Browsable(false)]
        public string WeeklyProjColorSerializable
        {
            get { return Serialize.BrushToString(WeeklyProjColor); }
            set { WeeklyProjColor = Serialize.StringToBrush(value); }
        }

        [Range(0, 100)]
        [NinjaScriptProperty]
        [Display(Name = "Weekly Proj Opacity %", Order = 6, GroupName = "07. ATR Projections")]
        public int WeeklyProjOpacity { get; set; }

        // ─── 08. Order Flow ───────────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Show Bubbles", Order = 1, GroupName = "08. Order Flow")]
        public bool ShowBubbles { get; set; }

        [Range(5, 100)]
        [NinjaScriptProperty]
        [Display(Name = "Bubble Max Px", Order = 2, GroupName = "08. Order Flow")]
        public int BubbleMaxPx { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Delta Mode", Order = 3, GroupName = "08. Order Flow")]
        public PtsDeltaMode DeltaMode { get; set; }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Buy Bubble Color", Order = 4, GroupName = "08. Order Flow")]
        public MediaBrush BubbleBuyColor { get; set; }

        [Browsable(false)]
        public string BubbleBuyColorSerializable
        {
            get { return Serialize.BrushToString(BubbleBuyColor); }
            set { BubbleBuyColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Sell Bubble Color", Order = 5, GroupName = "08. Order Flow")]
        public MediaBrush BubbleSellColor { get; set; }

        [Browsable(false)]
        public string BubbleSellColorSerializable
        {
            get { return Serialize.BrushToString(BubbleSellColor); }
            set { BubbleSellColor = Serialize.StringToBrush(value); }
        }

        [Range(0, 100)]
        [NinjaScriptProperty]
        [Display(Name = "Bubble Opacity %", Order = 6, GroupName = "08. Order Flow")]
        public int BubbleOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Volume Text", Order = 7, GroupName = "08. Order Flow")]
        public bool ShowVolumeText { get; set; }

        // ─── 09. Text ─────────────────────────────────────────────────────────────

        [Range(6f, 24f)]
        [NinjaScriptProperty]
        [Display(Name = "Tag Font Size", Order = 1, GroupName = "09. Text")]
        public float TagFontSize { get; set; }

        // ─── 10. Volume Profile ───────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Show Volume Profile", Order = 1, GroupName = "10. Volume Profile")]
        public bool ShowVp { get; set; }

        [Range(10, 300)]
        [NinjaScriptProperty]
        [Display(Name = "VP Width Px", Order = 2, GroupName = "10. Volume Profile")]
        public int VpWidthPx { get; set; }

        [Range(1, 50)]
        [NinjaScriptProperty]
        [Display(Name = "VP Tick Bucket Size", Order = 3, GroupName = "10. Volume Profile")]
        public int VpTickBucketSize { get; set; }

        [Range(50, 95)]
        [NinjaScriptProperty]
        [Display(Name = "Value Area %", Order = 4, GroupName = "10. Volume Profile")]
        public int VpValueAreaPct { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Split Buy/Sell", Order = 5, GroupName = "10. Volume Profile")]
        public bool VpSplitBuySell { get; set; }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Buy Color", Order = 6, GroupName = "10. Volume Profile")]
        public MediaBrush VpBuyColor { get; set; }

        [Browsable(false)]
        public string VpBuyColorSerializable
        {
            get { return Serialize.BrushToString(VpBuyColor); }
            set { VpBuyColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Sell Color", Order = 7, GroupName = "10. Volume Profile")]
        public MediaBrush VpSellColor { get; set; }

        [Browsable(false)]
        public string VpSellColorSerializable
        {
            get { return Serialize.BrushToString(VpSellColor); }
            set { VpSellColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Total Color", Order = 8, GroupName = "10. Volume Profile")]
        public MediaBrush VpTotalColor { get; set; }

        [Browsable(false)]
        public string VpTotalColorSerializable
        {
            get { return Serialize.BrushToString(VpTotalColor); }
            set { VpTotalColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "POC Color", Order = 9, GroupName = "10. Volume Profile")]
        public MediaBrush VpPocColor { get; set; }

        [Browsable(false)]
        public string VpPocColorSerializable
        {
            get { return Serialize.BrushToString(VpPocColor); }
            set { VpPocColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Value Area Color", Order = 10, GroupName = "10. Volume Profile")]
        public MediaBrush VpVaColor { get; set; }

        [Browsable(false)]
        public string VpVaColorSerializable
        {
            get { return Serialize.BrushToString(VpVaColor); }
            set { VpVaColor = Serialize.StringToBrush(value); }
        }

        [Range(0, 100)]
        [NinjaScriptProperty]
        [Display(Name = "Histogram Opacity %", Order = 11, GroupName = "10. Volume Profile")]
        public int VpHistoOpacity { get; set; }

        // ─── 11. DOM Heatmap ──────────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Show Heatmap", Order = 1, GroupName = "11. DOM Heatmap")]
        public bool ShowHeatmap { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Heatmap Mode", Order = 2, GroupName = "11. DOM Heatmap")]
        public PtsHeatmapMode HeatmapMode { get; set; }

        [Range(20, 200)]
        [NinjaScriptProperty]
        [Display(Name = "Strip Width Px", Order = 3, GroupName = "11. DOM Heatmap")]
        public int HeatmapStripWidthPx { get; set; }

        [Range(5, 50)]
        [NinjaScriptProperty]
        [Display(Name = "Depth Levels", Order = 4, GroupName = "11. DOM Heatmap")]
        public int HeatmapDepthLevels { get; set; }

        [Range(30, 3600)]
        [NinjaScriptProperty]
        [Display(Name = "History Seconds", Order = 5, GroupName = "11. DOM Heatmap")]
        public int HeatmapHistorySeconds { get; set; }

        [Range(1, 20)]
        [NinjaScriptProperty]
        [Display(Name = "Sample Rate Hz", Order = 6, GroupName = "11. DOM Heatmap")]
        public int HeatmapSampleRateHz { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Size Labels", Order = 7, GroupName = "11. DOM Heatmap")]
        public bool ShowSizeLabels { get; set; }

        [Range(0L, 100000L)]
        [NinjaScriptProperty]
        [Display(Name = "Wall Threshold (lots)", Order = 8, GroupName = "11. DOM Heatmap")]
        public long WallThreshold { get; set; }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Bid Color", Order = 9, GroupName = "11. DOM Heatmap")]
        public MediaBrush HeatmapBidColor { get; set; }

        [Browsable(false)]
        public string HeatmapBidColorSerializable
        {
            get { return Serialize.BrushToString(HeatmapBidColor); }
            set { HeatmapBidColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Ask Color", Order = 10, GroupName = "11. DOM Heatmap")]
        public MediaBrush HeatmapAskColor { get; set; }

        [Browsable(false)]
        public string HeatmapAskColorSerializable
        {
            get { return Serialize.BrushToString(HeatmapAskColor); }
            set { HeatmapAskColor = Serialize.StringToBrush(value); }
        }

        [Range(10, 100)]
        [NinjaScriptProperty]
        [Display(Name = "Max Opacity %", Order = 11, GroupName = "11. DOM Heatmap")]
        public int HeatmapMaxOpacity { get; set; }

        // ─── 12. Cumulative Delta ─────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Show CD Panel", Order = 1, GroupName = "12. Cumulative Delta")]
        public bool ShowCdPanel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Panel Position", Order = 2, GroupName = "12. Cumulative Delta")]
        public PtsCdPanelPos CdPanelPos { get; set; }

        [Range(40, 400)]
        [NinjaScriptProperty]
        [Display(Name = "Panel Height Px", Order = 3, GroupName = "12. Cumulative Delta")]
        public int CdPanelHeightPx { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session Reset", Order = 4, GroupName = "12. Cumulative Delta")]
        public bool CdSessionReset { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Divergence", Order = 5, GroupName = "12. Cumulative Delta")]
        public bool ShowCdDivergence { get; set; }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Up Color", Order = 6, GroupName = "12. Cumulative Delta")]
        public MediaBrush CdUpColor { get; set; }

        [Browsable(false)]
        public string CdUpColorSerializable
        {
            get { return Serialize.BrushToString(CdUpColor); }
            set { CdUpColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Down Color", Order = 7, GroupName = "12. Cumulative Delta")]
        public MediaBrush CdDownColor { get; set; }

        [Browsable(false)]
        public string CdDownColorSerializable
        {
            get { return Serialize.BrushToString(CdDownColor); }
            set { CdDownColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Background Color", Order = 8, GroupName = "12. Cumulative Delta")]
        public MediaBrush CdBgColor { get; set; }

        [Browsable(false)]
        public string CdBgColorSerializable
        {
            get { return Serialize.BrushToString(CdBgColor); }
            set { CdBgColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Zero Line Color", Order = 9, GroupName = "12. Cumulative Delta")]
        public MediaBrush CdZeroLineColor { get; set; }

        [Browsable(false)]
        public string CdZeroLineColorSerializable
        {
            get { return Serialize.BrushToString(CdZeroLineColor); }
            set { CdZeroLineColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [NinjaScriptProperty]
        [Display(Name = "Divergence Dot Color", Order = 10, GroupName = "12. Cumulative Delta")]
        public MediaBrush CdDivColor { get; set; }

        [Browsable(false)]
        public string CdDivColorSerializable
        {
            get { return Serialize.BrushToString(CdDivColor); }
            set { CdDivColor = Serialize.StringToBrush(value); }
        }

        [Range(0, 100)]
        [NinjaScriptProperty]
        [Display(Name = "Panel Opacity %", Order = 11, GroupName = "12. Cumulative Delta")]
        public int CdOpacity { get; set; }

        // ─── 13. Alerts ───────────────────────────────────────────────────────────

        // FVG Fill
        [NinjaScriptProperty]
        [Display(Name = "FVG Fill — Enable", Order = 1, GroupName = "13. Alerts")]
        public bool AlertFvgFillEnable { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "FVG Fill — Sound", Order = 2, GroupName = "13. Alerts")]
        public string AlertFvgFillSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "FVG Fill — Priority", Order = 3, GroupName = "13. Alerts")]
        public Priority AlertFvgFillPriority { get; set; }

        [Range(1, 3600)]
        [NinjaScriptProperty]
        [Display(Name = "FVG Fill — Rearm Sec", Order = 4, GroupName = "13. Alerts")]
        public int AlertFvgFillRearm { get; set; }

        // iFVG Created
        [NinjaScriptProperty]
        [Display(Name = "iFVG Created — Enable", Order = 5, GroupName = "13. Alerts")]
        public bool AlertIFvgCreatedEnable { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "iFVG Created — Sound", Order = 6, GroupName = "13. Alerts")]
        public string AlertIFvgCreatedSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "iFVG Created — Priority", Order = 7, GroupName = "13. Alerts")]
        public Priority AlertIFvgCreatedPriority { get; set; }

        [Range(1, 3600)]
        [NinjaScriptProperty]
        [Display(Name = "iFVG Created — Rearm Sec", Order = 8, GroupName = "13. Alerts")]
        public int AlertIFvgCreatedRearm { get; set; }

        // BOS
        [NinjaScriptProperty]
        [Display(Name = "BOS — Enable", Order = 9, GroupName = "13. Alerts")]
        public bool AlertBosEnable { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "BOS — Sound", Order = 10, GroupName = "13. Alerts")]
        public string AlertBosSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "BOS — Priority", Order = 11, GroupName = "13. Alerts")]
        public Priority AlertBosPriority { get; set; }

        [Range(1, 3600)]
        [NinjaScriptProperty]
        [Display(Name = "BOS — Rearm Sec", Order = 12, GroupName = "13. Alerts")]
        public int AlertBosRearm { get; set; }

        // CHoCH
        [NinjaScriptProperty]
        [Display(Name = "CHoCH — Enable", Order = 13, GroupName = "13. Alerts")]
        public bool AlertChochEnable { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "CHoCH — Sound", Order = 14, GroupName = "13. Alerts")]
        public string AlertChochSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "CHoCH — Priority", Order = 15, GroupName = "13. Alerts")]
        public Priority AlertChochPriority { get; set; }

        [Range(1, 3600)]
        [NinjaScriptProperty]
        [Display(Name = "CHoCH — Rearm Sec", Order = 16, GroupName = "13. Alerts")]
        public int AlertChochRearm { get; set; }

        // ATR Target Hit
        [NinjaScriptProperty]
        [Display(Name = "ATR Target Hit — Enable", Order = 17, GroupName = "13. Alerts")]
        public bool AlertAtrTargetHitEnable { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ATR Target Hit — Sound", Order = 18, GroupName = "13. Alerts")]
        public string AlertAtrTargetHitSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ATR Target Hit — Priority", Order = 19, GroupName = "13. Alerts")]
        public Priority AlertAtrTargetHitPriority { get; set; }

        [Range(1, 3600)]
        [NinjaScriptProperty]
        [Display(Name = "ATR Target Hit — Rearm Sec", Order = 20, GroupName = "13. Alerts")]
        public int AlertAtrTargetHitRearm { get; set; }

        // CD Divergence
        [NinjaScriptProperty]
        [Display(Name = "CD Divergence — Enable", Order = 21, GroupName = "13. Alerts")]
        public bool AlertCdDivergenceEnable { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "CD Divergence — Sound", Order = 22, GroupName = "13. Alerts")]
        public string AlertCdDivergenceSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "CD Divergence — Priority", Order = 23, GroupName = "13. Alerts")]
        public Priority AlertCdDivergencePriority { get; set; }

        [Range(1, 3600)]
        [NinjaScriptProperty]
        [Display(Name = "CD Divergence — Rearm Sec", Order = 24, GroupName = "13. Alerts")]
        public int AlertCdDivergenceRearm { get; set; }

        // VWAP Cross
        [NinjaScriptProperty]
        [Display(Name = "VWAP Cross — Enable", Order = 25, GroupName = "13. Alerts")]
        public bool AlertVwapCrossEnable { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "VWAP Cross — Sound", Order = 26, GroupName = "13. Alerts")]
        public string AlertVwapCrossSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "VWAP Cross — Priority", Order = 27, GroupName = "13. Alerts")]
        public Priority AlertVwapCrossPriority { get; set; }

        [Range(1, 3600)]
        [NinjaScriptProperty]
        [Display(Name = "VWAP Cross — Rearm Sec", Order = 28, GroupName = "13. Alerts")]
        public int AlertVwapCrossRearm { get; set; }

        // Wall Added
        [NinjaScriptProperty]
        [Display(Name = "Wall Added — Enable", Order = 29, GroupName = "13. Alerts")]
        public bool AlertWallAddedEnable { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Wall Added — Sound", Order = 30, GroupName = "13. Alerts")]
        public string AlertWallAddedSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Wall Added — Priority", Order = 31, GroupName = "13. Alerts")]
        public Priority AlertWallAddedPriority { get; set; }

        [Range(1, 3600)]
        [NinjaScriptProperty]
        [Display(Name = "Wall Added — Rearm Sec", Order = 32, GroupName = "13. Alerts")]
        public int AlertWallAddedRearm { get; set; }

        // Wall Pulled
        [NinjaScriptProperty]
        [Display(Name = "Wall Pulled — Enable", Order = 33, GroupName = "13. Alerts")]
        public bool AlertWallPulledEnable { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Wall Pulled — Sound", Order = 34, GroupName = "13. Alerts")]
        public string AlertWallPulledSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Wall Pulled — Priority", Order = 35, GroupName = "13. Alerts")]
        public Priority AlertWallPulledPriority { get; set; }

        [Range(1, 3600)]
        [NinjaScriptProperty]
        [Display(Name = "Wall Pulled — Rearm Sec", Order = 36, GroupName = "13. Alerts")]
        public int AlertWallPulledRearm { get; set; }

        #endregion
    }
}
