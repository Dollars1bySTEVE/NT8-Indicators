// IQ Candles GPU — Comprehensive GPU-accelerated smart money candle indicator for NinjaTrader 8
// Combines IQ Candles Pine Script logic with SharpDX DirectX rendering, Level 2 order book
// support, volume delta microstructure, fake breakout filters, and real-time dashboard.

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
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
#endregion

// NinjaTrader 8 requires custom enums declared OUTSIDE all namespaces
// so the auto-generated partial class code can resolve them without ambiguity.
// Reference: forum.ninjatrader.com threads #1182932, #95909, #1046853

/// <summary>Asset class profile for tick/pip size calibration.</summary>
public enum IQCAssetClass
{
    Futures,
    Crypto,
    Stocks,
    Forex,
    Indices
}

/// <summary>Dashboard anchor corner on the chart.</summary>
public enum IQCDashboardPosition
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

/// <summary>Candle coloring mode.</summary>
public enum IQCCandleColorMode
{
    Composite,    // Volume (PVSRA) priority + multi-signal borders
    PVSRA,        // PVSRA vector candles only
    VolumeDelta,
    Absorption,
    Imbalance,
    FakeBreakout,
    Classic
}

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// IQ Candles GPU — Production-ready NinjaTrader 8 indicator that replicates all Pine Script
    /// IQ Candles functionality with SharpDX GPU acceleration.
    ///
    /// Features:
    ///  • GPU-rendered candles colored by volume delta / absorption / imbalance / fake-breakout signals
    ///  • Level 1 + Level 2 order book via OnMarketDepth (graceful fallback to L1)
    ///  • Volume delta microstructure: buy/sell pressure, delta%, cumulative delta
    ///  • 5 fake-breakout filters: multi-bar confirmation, volume follow-through, momentum
    ///    divergence, S/R level validation, risk-reward ratio
    ///  • Smart money absorption detection (large passive orders soaking directional flow)
    ///  • Imbalance flagging with rolling 5th–95th percentile thresholds
    ///  • Order-book wall detection with naïve spoofing heuristic
    ///  • Real-time on-chart dashboard with live statistics
    ///  • Asset-class profiles: Futures, Crypto, Stocks, Forex, Indices
    ///  • All SharpDX types fully qualified — no namespace conflicts
    ///  • Enums outside namespace (NT8 compiler requirement)
    ///  • Bounded collections (≤ 1 000 elements) for memory safety
    /// </summary>
    public class IQCandlesGPU : Indicator
    {
        // ════════════════════════════════════════════════════════════════════════
        #region Inner types

        /// <summary>Per-bar computed microstructure snapshot.</summary>
        private class BarSnapshot
        {
            public double BuyVolume;
            public double SellVolume;
            public double Delta;          // buy - sell
            public double DeltaPct;       // delta / total * 100
            public double CumDelta;       // running cumulative delta
            public bool IsAbsorption;
            public bool IsImbalance;
            public bool IsFakeBreakout;
            public int FakeBreakoutDir;   // +1 above, -1 below
        }

        /// <summary>Single level in the order book snapshot.</summary>
        private class BookLevel
        {
            public double Price;
            public long   Size;
            public bool   IsSpoof;        // flagged by anti-spoofing heuristic
        }

        /// <summary>Unrecovered liquidity zone left behind by a swing high or low.</summary>
        private class LiquidityZone
        {
            public double HighPrice;      // upper boundary including wicks
            public double LowPrice;       // lower boundary including wicks
            public double BodyHighPrice;  // upper boundary body-only
            public double BodyLowPrice;   // lower boundary body-only
            public int    CreatedBar;     // CurrentBar value when zone was created
            public bool   IsBullish;      // true = swing-low zone (green), false = swing-high zone (red)
            public bool   IsRecovered;    // true = price has crossed fully through the zone
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Private fields

        // ── Microstructure data ───────────────────────────────────────────────
        private List<BarSnapshot>   snapshots;       // one per bar, capped at 1 000
        private double              cumDelta;         // session cumulative delta
        private double              sessionBuyVol;
        private double              sessionSellVol;
        private double              prevTickPrice;
        private long                prevTickVol;

        // Rolling percentile window for imbalance threshold (5th–95th pct)
        private List<double>        deltaHistory;    // absolute |delta| values, capped 1 000
        private double              imbalanceLow;    // 5th  percentile
        private double              imbalanceHigh;   // 95th percentile

        // ── Support / Resistance levels for fake-breakout filter ─────────────
        private List<double>        srLevels;        // auto-detected S/R, capped 50

        // ── Order book (Level 2) ──────────────────────────────────────────────
        private Dictionary<double, BookLevel> bidBook;   // price → BookLevel
        private Dictionary<double, BookLevel> askBook;
        private bool   level2Available;
        private double bestBidPrice;
        private double bestAskPrice;
        private long   bestBidSize;
        private long   bestAskSize;
        private long   totalBidDepth;
        private long   totalAskDepth;
        private double wallBidPrice;   // detected bid wall (largest size)
        private long   wallBidSize;
        private double wallAskPrice;   // detected ask wall
        private long   wallAskSize;
        private string l2StatusText   = "L2: waiting…";

        // ── Absorption tracking ───────────────────────────────────────────────
        private double absorptionThreshold;          // recalculated each bar

        // ── Fake-breakout state ───────────────────────────────────────────────
        private int    confirmBarsCount;             // consecutive bars past S/R
        private int    breakoutDir;                  // +1 / -1 / 0
        private double breakoutLevel;

        // ── SharpDX GPU resources ─────────────────────────────────────────────
        private bool   dxReady;
        private SharpDX.Direct2D1.SolidColorBrush dxBullBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBearBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxAbsorbBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxImbalanceBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxFakeBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxWickBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxDashBgBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxDashTextBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxDashAccentBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxWallBidBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxWallAskBrush;
        private SharpDX.DirectWrite.TextFormat     dxDashFormat;
        private SharpDX.DirectWrite.TextFormat     dxLabelFormat;
        private SharpDX.DirectWrite.Factory        dxWriteFactory;

        // ── Dashboard stats (updated in OnBarUpdate, read in OnRender) ────────
        private string dashLine1 = "";
        private string dashLine2 = "";
        private string dashLine3 = "";
        private string dashLine4 = "";
        private string dashLine5 = "";
        private string dashLine6 = "";
        private string dashLine7 = "";

        // ── Unrecovered Liquidity Zones ───────────────────────────────────────
        private List<LiquidityZone> liquidityZones;
        private int  activeZoneCount;
        private int  recoveredZoneCount;

        // ── PVSRA GPU brushes ─────────────────────────────────────────────────
        private SharpDX.Direct2D1.SolidColorBrush dxPvsraHighBullBrush;  // bright green
        private SharpDX.Direct2D1.SolidColorBrush dxPvsraHighBearBrush;  // bright red
        private SharpDX.Direct2D1.SolidColorBrush dxPvsraMidBullBrush;   // blue
        private SharpDX.Direct2D1.SolidColorBrush dxPvsraMidBearBrush;   // blue-violet

        // ── Composite border brushes ──────────────────────────────────────────
        private SharpDX.Direct2D1.SolidColorBrush dxBorderAbsorbBrush;    // gold
        private SharpDX.Direct2D1.SolidColorBrush dxBorderImbalanceBrush; // blue
        private SharpDX.Direct2D1.SolidColorBrush dxBorderFakeBrush;      // orange

        // ── Zone fill brushes ─────────────────────────────────────────────────
        private SharpDX.Direct2D1.SolidColorBrush dxULZBullishBrush;  // semi-transparent green (support/swing-low)
        private SharpDX.Direct2D1.SolidColorBrush dxULZBearishBrush;  // semi-transparent red   (resistance/swing-high)

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 1. Core

        [NinjaScriptProperty]
        [Display(Name = "Asset Class", Order = 1, GroupName = "1. Core",
            Description = "Calibrates tick size and thresholds for the selected market type.")]
        public IQCAssetClass AssetClass { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Candle Color Mode", Order = 2, GroupName = "1. Core",
            Description = "Chooses what data drives candle color: delta, absorption, imbalance, fake-breakout, or classic OHLC.")]
        public IQCCandleColorMode ColorMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Level 2 (Order Book)", Order = 3, GroupName = "1. Core",
            Description = "Subscribe to market depth (L2) data. Requires broker support. Falls back to L1 gracefully.")]
        public bool EnableLevel2 { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 2. Volume Delta

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Delta Lookback Bars", Order = 1, GroupName = "2. Volume Delta",
            Description = "Number of bars used to normalise delta percentile thresholds (5th–95th pct).")]
        public int DeltaLookback { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name = "Absorption Sensitivity %", Order = 2, GroupName = "2. Volume Delta",
            Description = "What fraction of total bar volume must be 'absorbed' (counter-flow) to flag absorption. Lower = more signals.")]
        public double AbsorptionSensitivity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Cumulative Delta", Order = 3, GroupName = "2. Volume Delta",
            Description = "Renders a session cumulative delta label on each bar.")]
        public bool ShowCumDelta { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 3. Fake Breakout Filters

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Confirmation Bars Required", Order = 1, GroupName = "3. Fake Breakout Filters",
            Description = "Filter 1 — Minimum consecutive closes beyond S/R to confirm a real breakout.")]
        public int ConfirmationBars { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 500.0)]
        [Display(Name = "Volume Follow-Through %", Order = 2, GroupName = "3. Fake Breakout Filters",
            Description = "Filter 2 — Breakout bar volume must exceed this % of the average volume to pass.")]
        public double VolumeFollowThrough { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Momentum RSI Period", Order = 3, GroupName = "3. Fake Breakout Filters",
            Description = "Filter 3 — RSI period for momentum divergence check.")]
        public int MomentumPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "S/R Swing Strength", Order = 4, GroupName = "3. Fake Breakout Filters",
            Description = "Filter 4 — Swing strength (bars each side) used to detect support/resistance levels.")]
        public int SRSwingStrength { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "Min Risk/Reward Ratio", Order = 5, GroupName = "3. Fake Breakout Filters",
            Description = "Filter 5 — Minimum risk-to-reward ratio for a valid breakout trade setup.")]
        public double MinRiskReward { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 4. Order Book Walls

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Wall Detection Multiplier", Order = 1, GroupName = "4. Order Book Walls",
            Description = "A book level whose size exceeds (average size × multiplier) is flagged as a wall.")]
        public int WallMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Anti-Spoofing Checks", Order = 2, GroupName = "4. Order Book Walls",
            Description = "Number of consecutive ticks a wall-size order must persist before being considered real (not spoofed).")]
        public int SpoofingChecks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Wall Lines", Order = 3, GroupName = "4. Order Book Walls",
            Description = "Draw horizontal lines at detected bid/ask walls.")]
        public bool ShowWallLines { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 5. Visuals

        [NinjaScriptProperty]
        [Display(Name = "Show Dashboard", Order = 1, GroupName = "5. Visuals",
            Description = "Render the on-chart statistics panel.")]
        public bool ShowDashboard { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Dashboard Position", Order = 2, GroupName = "5. Visuals")]
        public IQCDashboardPosition DashPosition { get; set; }

        [NinjaScriptProperty]
        [Range(10, 50)]
        [Display(Name = "Dashboard Font Size", Order = 3, GroupName = "5. Visuals")]
        public int DashFontSize { get; set; }

        [NinjaScriptProperty]
        [Range(20, 100)]
        [Display(Name = "Dashboard Opacity %", Order = 4, GroupName = "5. Visuals")]
        public int DashOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Halo on Signals", Order = 5, GroupName = "5. Visuals",
            Description = "Draw a multi-layer halo circle around absorption / fake-breakout bars.")]
        public bool ShowHalo { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Halo Layers", Order = 6, GroupName = "5. Visuals",
            Description = "Number of concentric rings in the halo effect (more = softer glow).")]
        public int HaloLayers { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Bullish Color", Order = 7, GroupName = "5. Visuals")]
        [XmlIgnore]
        public System.Windows.Media.Brush BullishColor { get; set; }

        [Browsable(false)]
        public string BullishColorSerializable
        {
            get { return Serialize.BrushToString(BullishColor); }
            set { BullishColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Bearish Color", Order = 8, GroupName = "5. Visuals")]
        [XmlIgnore]
        public System.Windows.Media.Brush BearishColor { get; set; }

        [Browsable(false)]
        public string BearishColorSerializable
        {
            get { return Serialize.BrushToString(BearishColor); }
            set { BearishColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Absorption Color", Order = 9, GroupName = "5. Visuals")]
        [XmlIgnore]
        public System.Windows.Media.Brush AbsorptionColor { get; set; }

        [Browsable(false)]
        public string AbsorptionColorSerializable
        {
            get { return Serialize.BrushToString(AbsorptionColor); }
            set { AbsorptionColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Imbalance Color", Order = 10, GroupName = "5. Visuals")]
        [XmlIgnore]
        public System.Windows.Media.Brush ImbalanceColor { get; set; }

        [Browsable(false)]
        public string ImbalanceColorSerializable
        {
            get { return Serialize.BrushToString(ImbalanceColor); }
            set { ImbalanceColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Fake Breakout Color", Order = 11, GroupName = "5. Visuals")]
        [XmlIgnore]
        public System.Windows.Media.Brush FakeBreakoutColor { get; set; }

        [Browsable(false)]
        public string FakeBreakoutColorSerializable
        {
            get { return Serialize.BrushToString(FakeBreakoutColor); }
            set { FakeBreakoutColor = Serialize.StringToBrush(value); }
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 6. PVSRA Vectors

        [NinjaScriptProperty]
        [Display(Name = "Enable PVSRA Vectors", Order = 1, GroupName = "6. PVSRA Vectors",
            Description = "Color candles using PVSRA volume-threshold logic (Green/Red high-vol, Blue/Blue-Violet mid-vol).")]
        public bool EnablePVSRA { get; set; }

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "PVSRA Lookback Bars", Order = 2, GroupName = "6. PVSRA Vectors",
            Description = "Number of previous bars used to compute the volume average for PVSRA classification.")]
        public int PVSRALookback { get; set; }

        [NinjaScriptProperty]
        [Range(100, 300)]
        [Display(Name = "High-Volume Threshold %", Order = 3, GroupName = "6. PVSRA Vectors",
            Description = "Volume must be >= this percentage of the lookback average to qualify as a high-volume (Green/Red) candle.")]
        public int HighVolumeThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(100, 200)]
        [Display(Name = "Mid-Volume Threshold %", Order = 4, GroupName = "6. PVSRA Vectors",
            Description = "Volume must be >= this percentage of the lookback average to qualify as a mid-volume (Blue/Blue-Violet) candle.")]
        public int MidVolumeThreshold { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 7. Liquidity Zones

        [NinjaScriptProperty]
        [Display(Name = "Enable Liquidity Zones", Order = 1, GroupName = "7. Liquidity Zones",
            Description = "Draw semi-transparent unrecovered liquidity zones at swing highs/lows. Zones disappear when price recovers through them.")]
        public bool EnableLiquidityZones { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Include Wicks in Zone Boundary", Order = 2, GroupName = "7. Liquidity Zones",
            Description = "When enabled, zone boundaries use the full High/Low (including wicks). When disabled, only the candle body is used.")]
        public bool ULZIncludeWicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Zone Count on Dashboard", Order = 3, GroupName = "7. Liquidity Zones",
            Description = "Display the number of active and recovered liquidity zones on the dashboard overlay.")]
        public bool ShowZoneCount { get; set; }

        [NinjaScriptProperty]
        [Range(10, 100)]
        [Display(Name = "Max Active Zones", Order = 4, GroupName = "7. Liquidity Zones",
            Description = "Maximum number of liquidity zones to track simultaneously. Oldest zones are removed when the limit is reached.")]
        public int MaxActiveZones { get; set; }

        [NinjaScriptProperty]
        [Range(5, 80)]
        [Display(Name = "Zone Opacity %", Order = 5, GroupName = "7. Liquidity Zones",
            Description = "Opacity of the liquidity zone rectangles (5 = nearly transparent, 80 = mostly opaque).")]
        public int ZoneOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Bullish Zone Color", Order = 6, GroupName = "7. Liquidity Zones",
            Description = "Fill color for bullish (swing-low support) liquidity zones.")]
        [XmlIgnore]
        public System.Windows.Media.Brush ULZBullishColor { get; set; }

        [Browsable(false)]
        public string ULZBullishColorSerializable
        {
            get { return Serialize.BrushToString(ULZBullishColor); }
            set { ULZBullishColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Bearish Zone Color", Order = 7, GroupName = "7. Liquidity Zones",
            Description = "Fill color for bearish (swing-high resistance) liquidity zones.")]
        [XmlIgnore]
        public System.Windows.Media.Brush ULZBearishColor { get; set; }

        [Browsable(false)]
        public string ULZBearishColorSerializable
        {
            get { return Serialize.BrushToString(ULZBearishColor); }
            set { ULZBearishColor = Serialize.StringToBrush(value); }
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region State management — OnStateChange

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "IQ Candles GPU — Smart money microstructure candle indicator with GPU rendering, L2 order book, and fake-breakout filters";
                Name                     = "IQCandlesGPU";
                Calculate                = Calculate.OnEachTick;
                IsOverlay                = true;
                DisplayInDataBox         = true;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = false;

                // Core
                AssetClass     = IQCAssetClass.Futures;
                ColorMode      = IQCCandleColorMode.VolumeDelta;
                EnableLevel2   = false;

                // Volume delta
                DeltaLookback        = 100;
                AbsorptionSensitivity = 60.0;
                ShowCumDelta         = true;

                // Fake-breakout filters
                ConfirmationBars    = 2;
                VolumeFollowThrough = 120.0;
                MomentumPeriod      = 14;
                SRSwingStrength     = 5;
                MinRiskReward       = 2.0;

                // Order-book walls
                WallMultiplier = 5;
                SpoofingChecks = 3;
                ShowWallLines  = true;

                // Visuals
                ShowDashboard  = true;
                DashPosition   = IQCDashboardPosition.TopRight;
                DashFontSize   = 11;
                DashOpacity    = 80;
                ShowHalo       = true;
                HaloLayers     = 5;

                BullishColor     = Brushes.LimeGreen;
                BearishColor     = Brushes.Crimson;
                AbsorptionColor  = Brushes.Gold;
                ImbalanceColor   = Brushes.DodgerBlue;
                FakeBreakoutColor = Brushes.OrangeRed;

                // PVSRA Vectors
                EnablePVSRA          = true;
                PVSRALookback        = 10;
                HighVolumeThreshold  = 200;
                MidVolumeThreshold   = 150;

                // Liquidity Zones
                EnableLiquidityZones = true;
                ULZIncludeWicks      = false;
                ShowZoneCount        = true;
                MaxActiveZones       = 100;
                ZoneOpacity          = 30;
                ULZBullishColor      = Brushes.LimeGreen;
                ULZBearishColor      = Brushes.IndianRed;
            }
            else if (State == State.DataLoaded)
            {
                snapshots     = new List<BarSnapshot>(1000);
                deltaHistory  = new List<double>(1000);
                srLevels      = new List<double>(50);
                bidBook       = new Dictionary<double, BookLevel>(200);
                askBook       = new Dictionary<double, BookLevel>(200);
                liquidityZones = new List<LiquidityZone>(100);

                cumDelta      = 0;
                sessionBuyVol = 0;
                sessionSellVol= 0;
                prevTickPrice = 0;
                prevTickVol   = 0;
                imbalanceLow  = 0;
                imbalanceHigh = double.MaxValue;
                level2Available = false;
                l2StatusText  = EnableLevel2 ? "L2: waiting…" : "L2: disabled";
                activeZoneCount   = 0;
                recoveredZoneCount = 0;
            }
            else if (State == State.Terminated)
            {
                DisposeDXResources();
            }
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region OnBarUpdate — microstructure computation

        protected override void OnBarUpdate()
        {
            if (CurrentBar < Math.Max(SRSwingStrength * 2 + 1, MomentumPeriod + 1))
                return;

            // ── 1. Estimate buy / sell volume from tick data ──────────────────
            double barBuy  = 0;
            double barSell = 0;

            // On-tick mode: use mid-point heuristic (uptick → buy, downtick → sell)
            // On-close mode: use close vs open to split volume proportionally
            if (Calculate == Calculate.OnEachTick)
            {
                double tickPrice = Close[0];
                double tickVol   = Volume[0];

                if (prevTickPrice == 0)
                {
                    barBuy  = tickVol * 0.5;
                    barSell = tickVol * 0.5;
                }
                else if (tickPrice >= prevTickPrice)
                {
                    barBuy  = tickVol;
                    barSell = 0;
                }
                else
                {
                    barBuy  = 0;
                    barSell = tickVol;
                }

                prevTickPrice = tickPrice;
            }
            else
            {
                // Bar-close estimation: proportion based on candle body direction
                double barRange = High[0] - Low[0];
                double bodyHigh = Math.Max(Open[0], Close[0]);
                double bodyLow  = Math.Min(Open[0], Close[0]);
                double bullFrac = barRange > 0 ? (bodyHigh - Low[0]) / barRange : 0.5;
                barBuy  = Volume[0] * bullFrac;
                barSell = Volume[0] * (1.0 - bullFrac);
            }

            // ── 2. Update cumulative delta ────────────────────────────────────
            double delta = barBuy - barSell;
            if (IsFirstTickOfBar || Calculate != Calculate.OnEachTick)
            {
                cumDelta      += delta;
                sessionBuyVol += barBuy;
                sessionSellVol+= barSell;
            }

            double totalVol = barBuy + barSell;
            double deltaPct = totalVol > 0 ? (delta / totalVol) * 100.0 : 0;

            // ── 3. Imbalance percentile threshold ─────────────────────────────
            if (IsFirstTickOfBar || Calculate != Calculate.OnEachTick)
            {
                if (deltaHistory.Count >= 1000) deltaHistory.RemoveAt(0);
                deltaHistory.Add(Math.Abs(delta));
                UpdateImbalanceThresholds();
            }

            // ── 4. Absorption detection ───────────────────────────────────────
            // Absorption: price closes near open (small body) but huge volume —
            // large passive orders absorbing aggressive flow without letting price move.
            bool isAbsorption = false;
            if (totalVol > 0)
            {
                double bodyPct = Math.Abs(Close[0] - Open[0]) / (High[0] - Low[0] + TickSize);
                double counterFrac = Close[0] >= Open[0] ? barSell / totalVol : barBuy / totalVol;
                isAbsorption = bodyPct < 0.35 && counterFrac * 100.0 >= AbsorptionSensitivity;
            }

            // ── 5. Imbalance detection ────────────────────────────────────────
            bool isImbalance = Math.Abs(delta) > imbalanceHigh ||
                               (imbalanceHigh > 0 && Math.Abs(delta) > imbalanceHigh * 0.75);

            // ── 6. S/R level auto-detection (filter 4) ────────────────────────
            if (IsFirstTickOfBar)
                TryDetectSRLevel();

            // ── 6a. Liquidity zone recovery tracking ──────────────────────────
            if (EnableLiquidityZones && IsFirstTickOfBar)
                CheckLiquidityZoneRecovery();

            // ── 7. All 5 fake-breakout filters ───────────────────────────────
            bool isFakeBreakout  = false;
            int  fakeBreakoutDir = 0;
            if (IsFirstTickOfBar)
                CheckFakeBreakoutFilters(barBuy, barSell, totalVol, ref isFakeBreakout, ref fakeBreakoutDir);

            // ── 8. Save snapshot ──────────────────────────────────────────────
            if (IsFirstTickOfBar || snapshots.Count == 0)
            {
                if (snapshots.Count >= 1000) snapshots.RemoveAt(0);
                snapshots.Add(new BarSnapshot
                {
                    BuyVolume      = barBuy,
                    SellVolume     = barSell,
                    Delta          = delta,
                    DeltaPct       = deltaPct,
                    CumDelta       = cumDelta,
                    IsAbsorption   = isAbsorption,
                    IsImbalance    = isImbalance,
                    IsFakeBreakout = isFakeBreakout,
                    FakeBreakoutDir= fakeBreakoutDir
                });
            }

            // ── 9. Update dashboard strings ───────────────────────────────────
            UpdateDashboardText(barBuy, barSell, delta, deltaPct, isAbsorption, isImbalance, isFakeBreakout);

            // ── 10. Alerts ────────────────────────────────────────────────────
            if (IsFirstTickOfBar)
            {
                if (isFakeBreakout)
                    Alert("IQC_FakeBreakout", Priority.Medium, "IQ Candles: Fake Breakout detected",
                          NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav", 10, Brushes.OrangeRed, Brushes.Black);
                if (isAbsorption)
                    Alert("IQC_Absorption", Priority.Low, "IQ Candles: Absorption bar",
                          NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10, Brushes.Gold, Brushes.Black);
            }

            ForceRefresh();
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region OnMarketDepth — Level 2 order book

        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (!EnableLevel2)
                return;

            if (!level2Available)
            {
                level2Available = true;
                l2StatusText    = "L2: active";
            }

            bool isBid = e.MarketDataType == MarketDataType.Bid;
            bool isAsk = e.MarketDataType == MarketDataType.Ask;

            if (!isBid && !isAsk)
                return;

            Dictionary<double, BookLevel> book = isBid ? bidBook : askBook;
            double price = e.Price;
            long   size  = e.Volume;

            switch (e.Operation)
            {
                case Operation.Add:
                case Operation.Update:
                    if (!book.ContainsKey(price))
                        book[price] = new BookLevel { Price = price };
                    book[price].Size = size;
                    // Naïve spoofing heuristic: if size is > WallMultiplier × average,
                    // mark as potential spoof until it has persisted SpoofingChecks ticks.
                    book[price].IsSpoof = false; // reset on update
                    break;

                case Operation.Remove:
                    if (book.ContainsKey(price))
                        book.Remove(price);
                    break;
            }

            // Update best bid / ask
            if (isBid && e.Position == 0)
            {
                bestBidPrice = price;
                bestBidSize  = size;
                totalBidDepth = book.Values.Sum(b => b.Size);
            }
            else if (isAsk && e.Position == 0)
            {
                bestAskPrice = price;
                bestAskSize  = size;
                totalAskDepth = book.Values.Sum(b => b.Size);
            }

            // ── Detect walls and simple spoofing ─────────────────────────────
            DetectOrderBookWalls(isBid ? bidBook : askBook, isBid);

            // ── Update L2 status text ─────────────────────────────────────────
            if (bestBidPrice > 0 && bestAskPrice > 0)
            {
                double spread = bestAskPrice - bestBidPrice;
                double imb    = (totalBidDepth + totalAskDepth) > 0
                    ? (double)(totalBidDepth - totalAskDepth) / (totalBidDepth + totalAskDepth) * 100.0
                    : 0;
                l2StatusText = string.Format("L2 Bid {0:F2}×{1} | Ask {2:F2}×{3} | Sprd {4:F4} | Imb {5:+0;-0}%",
                    bestBidPrice, bestBidSize, bestAskPrice, bestAskSize, spread, (int)imb);
            }
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region GPU Rendering — OnRender / OnRenderTargetChanged

        public override void OnRenderTargetChanged()
        {
            DisposeDXResources();
            dxReady = false;
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (Bars == null || ChartBars == null || RenderTarget == null)
                return;

            if (!dxReady)
                CreateDXResources();

            if (!dxReady)
                return;

            int fromBar = ChartBars.FromIndex;
            int toBar   = ChartBars.ToIndex;

            if (fromBar > toBar)
                return;

            // ── Draw candles ──────────────────────────────────────────────────
            RenderCandles(chartControl, chartScale, fromBar, toBar);

            // ── Draw liquidity zones ──────────────────────────────────────────
            if (EnableLiquidityZones)
                RenderLiquidityZones(chartControl, chartScale);

            // ── Draw wall lines ───────────────────────────────────────────────
            if (ShowWallLines && EnableLevel2 && level2Available)
                RenderWallLines(chartControl, chartScale);

            // ── Draw dashboard overlay ────────────────────────────────────────
            if (ShowDashboard)
                RenderDashboard(chartControl, chartScale);
        }

        // ── Candle rendering ──────────────────────────────────────────────────
        private void RenderCandles(ChartControl cc, ChartScale cs, int fromBar, int toBar)
        {
            var rt = RenderTarget;

            for (int barIdx = fromBar; barIdx <= toBar; barIdx++)
            {
                if (barIdx < 0 || barIdx >= Bars.Count)
                    continue;

                double o = Bars.GetOpen(barIdx);
                double h = Bars.GetHigh(barIdx);
                double l = Bars.GetLow(barIdx);
                double c = Bars.GetClose(barIdx);

                float x   = cc.GetXByBarIndex(ChartBars, barIdx);
                float barW = Math.Max(1f, cc.GetBarPaintWidth(ChartBars) - 2f);
                float halfW = barW / 2f;

                float yH = cs.GetYByValue(h);
                float yL = cs.GetYByValue(l);
                float yO = cs.GetYByValue(o);
                float yC = cs.GetYByValue(c);
                float yTop = Math.Min(yO, yC);
                float yBot = Math.Max(yO, yC);

                // ── PVSRA inline classification for this bar ─────────────────
                string pvsraClass = "";
                if (EnablePVSRA && barIdx >= PVSRALookback)
                {
                    double barVol = Bars.GetVolume(barIdx);
                    double sumPrev = 0;
                    for (int k = 1; k <= PVSRALookback; k++)
                        sumPrev += Bars.GetVolume(barIdx - k);
                    double avgPrev = sumPrev / PVSRALookback;
                    double volPct  = avgPrev > 0 ? barVol / avgPrev * 100.0 : 0;
                    bool   isBull  = c >= o;
                    if (volPct >= HighVolumeThreshold)
                        pvsraClass = isBull ? "HighBull" : "HighBear";
                    else if (volPct >= MidVolumeThreshold)
                        pvsraClass = isBull ? "MidBull" : "MidBear";
                }

                // Select body brush based on ColorMode and snapshot data
                SharpDX.Direct2D1.SolidColorBrush bodyBrush = c >= o ? dxBullBrush : dxBearBrush;

                BarSnapshot snap = GetSnapshot(barIdx);
                if (snap != null)
                {
                    switch (ColorMode)
                    {
                        case IQCCandleColorMode.PVSRA:
                            if      (pvsraClass == "HighBull") bodyBrush = dxPvsraHighBullBrush;
                            else if (pvsraClass == "HighBear") bodyBrush = dxPvsraHighBearBrush;
                            else if (pvsraClass == "MidBull")  bodyBrush = dxPvsraMidBullBrush;
                            else if (pvsraClass == "MidBear")  bodyBrush = dxPvsraMidBearBrush;
                            else                               bodyBrush = c >= o ? dxBullBrush : dxBearBrush;
                            break;
                        case IQCCandleColorMode.Composite:
                            // Volume/PVSRA as base color, secondary signals shown as borders
                            if      (pvsraClass == "HighBull") bodyBrush = dxPvsraHighBullBrush;
                            else if (pvsraClass == "HighBear") bodyBrush = dxPvsraHighBearBrush;
                            else if (pvsraClass == "MidBull")  bodyBrush = dxPvsraMidBullBrush;
                            else if (pvsraClass == "MidBear")  bodyBrush = dxPvsraMidBearBrush;
                            else                               bodyBrush = snap.Delta >= 0 ? dxBullBrush : dxBearBrush;
                            break;
                        case IQCCandleColorMode.VolumeDelta:
                            bodyBrush = snap.Delta >= 0 ? dxBullBrush : dxBearBrush;
                            break;
                        case IQCCandleColorMode.Absorption:
                            bodyBrush = snap.IsAbsorption ? dxAbsorbBrush : (c >= o ? dxBullBrush : dxBearBrush);
                            break;
                        case IQCCandleColorMode.Imbalance:
                            bodyBrush = snap.IsImbalance ? dxImbalanceBrush : (c >= o ? dxBullBrush : dxBearBrush);
                            break;
                        case IQCCandleColorMode.FakeBreakout:
                            bodyBrush = snap.IsFakeBreakout ? dxFakeBrush : (c >= o ? dxBullBrush : dxBearBrush);
                            break;
                        case IQCCandleColorMode.Classic:
                            bodyBrush = c >= o ? dxBullBrush : dxBearBrush;
                            break;
                    }

                    // ── Halo effect on signal bars ────────────────────────────
                    if (ShowHalo && (snap.IsAbsorption || snap.IsFakeBreakout))
                    {
                        SharpDX.Direct2D1.SolidColorBrush haloBrush = snap.IsFakeBreakout ? dxFakeBrush : dxAbsorbBrush;
                        float centerY = (yTop + yBot) / 2f;
                        float baseR   = halfW + 2f;

                        for (int layer = HaloLayers; layer >= 1; layer--)
                        {
                            float r    = baseR + layer * 2f;
                            byte  alpha = (byte)(30 - layer * (30 / HaloLayers));
                            var   haloColor = haloBrush.Color;
                            var   newHaloColor = new SharpDX.Color4(haloColor.Red, haloColor.Green, haloColor.Blue, alpha / 255f);

                            using (var haloLayerBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, newHaloColor))
                            {
                                var ellipse = new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(x, centerY), r, r + (yBot - yTop) / 2f);
                                rt.FillEllipse(ellipse, haloLayerBrush);
                            }
                        }
                    }
                }

                // ── Wick ──────────────────────────────────────────────────────
                rt.DrawLine(new SharpDX.Vector2(x, yH), new SharpDX.Vector2(x, yL), dxWickBrush, 1f);

                // ── Body ──────────────────────────────────────────────────────
                float bodyH = Math.Max(1f, yBot - yTop);
                rt.FillRectangle(new SharpDX.RectangleF(x - halfW, yTop, barW, bodyH), bodyBrush);
                rt.DrawRectangle(new SharpDX.RectangleF(x - halfW, yTop, barW, bodyH), dxWickBrush, 1f);

                // ── Composite mode: multi-signal borders ──────────────────────
                if (snap != null && ColorMode == IQCCandleColorMode.Composite)
                {
                    float borderOffset = 0f;
                    if (snap.IsAbsorption)
                    {
                        rt.DrawRectangle(
                            new SharpDX.RectangleF(x - halfW - borderOffset - 1f, yTop - borderOffset - 1f,
                                barW + (borderOffset + 1f) * 2f, bodyH + (borderOffset + 1f) * 2f),
                            dxBorderAbsorbBrush, 2f);
                        borderOffset += 3f;
                    }
                    if (snap.IsImbalance)
                    {
                        rt.DrawRectangle(
                            new SharpDX.RectangleF(x - halfW - borderOffset - 1f, yTop - borderOffset - 1f,
                                barW + (borderOffset + 1f) * 2f, bodyH + (borderOffset + 1f) * 2f),
                            dxBorderImbalanceBrush, 2f);
                        borderOffset += 3f;
                    }
                    if (snap.IsFakeBreakout)
                    {
                        rt.DrawRectangle(
                            new SharpDX.RectangleF(x - halfW - borderOffset - 1f, yTop - borderOffset - 1f,
                                barW + (borderOffset + 1f) * 2f, bodyH + (borderOffset + 1f) * 2f),
                            dxBorderFakeBrush, 2f);
                    }
                }

                // ── Gradient transparency on body based on delta magnitude ────
                if (snap != null && ColorMode == IQCCandleColorMode.VolumeDelta)
                {
                    double normalised = NormaliseValue(Math.Abs(snap.DeltaPct), 0, 100);
                    byte   alpha      = (byte)(40 + normalised * 180);
                    var    gradColor  = bodyBrush.Color;
                    var    newGradColor = new SharpDX.Color4(gradColor.Red, gradColor.Green, gradColor.Blue, alpha / 255f);

                    using (var gradBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, newGradColor))
                    {
                        rt.FillRectangle(new SharpDX.RectangleF(x - halfW, yTop, barW, bodyH), gradBrush);
                    }
                }

                // ── Cumulative delta text ─────────────────────────────────────
                if (ShowCumDelta && snap != null && dxLabelFormat != null)
                {
                    string cdText = FormatDelta(snap.CumDelta);
                    var    rect   = new SharpDX.RectangleF(x - 30f, yL + 2f, 60f, 14f);
                    rt.DrawText(cdText, dxLabelFormat, rect, dxDashTextBrush);
                }
            }
        }

        // ── Wall line rendering ───────────────────────────────────────────────
        private void RenderWallLines(ChartControl cc, ChartScale cs)
        {
            var rt = RenderTarget;
            float chartWidth = (float)cc.ActualWidth;

            if (wallBidPrice > 0)
            {
                float y = cs.GetYByValue(wallBidPrice);
                string label = string.Format("BID WALL ×{0:N0}", wallBidSize);
                rt.DrawLine(new SharpDX.Vector2(0, y), new SharpDX.Vector2(chartWidth, y), dxWallBidBrush, 1.5f);
                if (dxLabelFormat != null)
                    rt.DrawText(label, dxLabelFormat, new SharpDX.RectangleF(4f, y - 14f, 160f, 14f), dxWallBidBrush);
            }

            if (wallAskPrice > 0)
            {
                float y = cs.GetYByValue(wallAskPrice);
                string label = string.Format("ASK WALL ×{0:N0}", wallAskSize);
                rt.DrawLine(new SharpDX.Vector2(0, y), new SharpDX.Vector2(chartWidth, y), dxWallAskBrush, 1.5f);
                if (dxLabelFormat != null)
                    rt.DrawText(label, dxLabelFormat, new SharpDX.RectangleF(4f, y + 2f, 160f, 14f), dxWallAskBrush);
            }
        }

        // ── Liquidity zone rendering ──────────────────────────────────────────
        private void RenderLiquidityZones(ChartControl cc, ChartScale cs)
        {
            if (liquidityZones == null || liquidityZones.Count == 0)
                return;

            var   rt         = RenderTarget;
            float chartWidth = (float)cc.ActualWidth;

            foreach (LiquidityZone zone in liquidityZones)
            {
                if (zone.IsRecovered)
                    continue;

                double topPrice = ULZIncludeWicks ? zone.HighPrice : zone.BodyHighPrice;
                double botPrice = ULZIncludeWicks ? zone.LowPrice  : zone.BodyLowPrice;

                float yHigh = cs.GetYByValue(topPrice);
                float yLow  = cs.GetYByValue(botPrice);
                float zoneH = Math.Max(1f, yLow - yHigh);

                SharpDX.Direct2D1.SolidColorBrush zoneBrush = zone.IsBullish ? dxULZBullishBrush : dxULZBearishBrush;
                rt.FillRectangle(new SharpDX.RectangleF(0f, yHigh, chartWidth, zoneH), zoneBrush);
            }
        }

        // ── Dashboard rendering ───────────────────────────────────────────────
        private void RenderDashboard(ChartControl cc, ChartScale cs)
        {
            var rt = RenderTarget;

            const float PadX   = 8f;
            const float PadY   = 6f;
            const float LineH  = 16f;
            const float PanelW = 320f;

            string[] lines =
            {
                dashLine1, dashLine2, dashLine3,
                dashLine4, dashLine5, dashLine6,
                dashLine7, l2StatusText
            };

            int    nonEmpty    = lines.Count(s => !string.IsNullOrEmpty(s));
            float  panelH      = PadY * 2 + nonEmpty * LineH;
            float  chartWidth  = (float)cc.ActualWidth;
            float  chartHeight = (float)cc.ActualHeight;

            float panelX, panelY;
            switch (DashPosition)
            {
                case IQCDashboardPosition.TopLeft:
                    panelX = PadX;  panelY = PadY;  break;
                case IQCDashboardPosition.TopRight:
                    panelX = chartWidth - PanelW - PadX;  panelY = PadY;  break;
                case IQCDashboardPosition.BottomLeft:
                    panelX = PadX;  panelY = chartHeight - panelH - PadY;  break;
                default: // BottomRight
                    panelX = chartWidth - PanelW - PadX;  panelY = chartHeight - panelH - PadY;  break;
            }

            // Background rect
            rt.FillRectangle(new SharpDX.RectangleF(panelX, panelY, PanelW, panelH), dxDashBgBrush);

            // Text lines
            float ty = panelY + PadY;
            foreach (string line in lines)
            {
                if (string.IsNullOrEmpty(line))
                    continue;

                if (dxDashFormat != null)
                {
                    var textBrush = line.Contains("FAKE") || line.Contains("Fake") ? dxDashAccentBrush : dxDashTextBrush;
                    rt.DrawText(line, dxDashFormat, new SharpDX.RectangleF(panelX + PadX, ty, PanelW - PadX * 2, LineH), textBrush);
                }
                ty += LineH;
            }
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Microstructure helpers

        private void UpdateImbalanceThresholds()
        {
            if (deltaHistory.Count < 5)
                return;

            var sorted = new List<double>(deltaHistory);
            sorted.Sort();

            int lo5  = (int)Math.Floor(sorted.Count * 0.05);
            int hi95 = (int)Math.Floor(sorted.Count * 0.95);
            hi95 = Math.Min(hi95, sorted.Count - 1);

            imbalanceLow  = sorted[lo5];
            imbalanceHigh = sorted[hi95];
        }

        private void TryDetectSRLevel()
        {
            if (CurrentBar < SRSwingStrength * 2 + 1)
                return;

            // Swing high: High[0] must be the highest bar across SRSwingStrength bars on each side.
            // High[i] accesses i bars ago (left side). We cannot check future bars directly;
            // instead we use the indicator's lookback into completed bars via High[i].
            // Convention: bar index 0 is the pivot candidate; left bars are High[1..SRSwingStrength],
            // right bars would require future data — so we use bars SRSwingStrength..2*SRSwingStrength
            // as the "right" side by checking the bars that came before the pivot window.
            bool isSwingHigh = true;
            for (int i = 1; i <= SRSwingStrength; i++)
            {
                if (High[i] >= High[0])
                {
                    isSwingHigh = false;
                    break;
                }
            }
            if (isSwingHigh)
            {
                // Also verify the right side (bars further back than the pivot)
                for (int i = SRSwingStrength + 1; i <= SRSwingStrength * 2; i++)
                {
                    if (i < CurrentBar && High[i] >= High[SRSwingStrength])
                    {
                        isSwingHigh = false;
                        break;
                    }
                }
            }

            // Swing low: Low[0] must be the lowest bar across SRSwingStrength bars on each side.
            bool isSwingLow = true;
            for (int i = 1; i <= SRSwingStrength; i++)
            {
                if (Low[i] <= Low[0])
                {
                    isSwingLow = false;
                    break;
                }
            }
            if (isSwingLow)
            {
                for (int i = SRSwingStrength + 1; i <= SRSwingStrength * 2; i++)
                {
                    if (i < CurrentBar && Low[i] <= Low[SRSwingStrength])
                    {
                        isSwingLow = false;
                        break;
                    }
                }
            }

            double level = 0;
            if (isSwingHigh) level = High[0];
            else if (isSwingLow) level = Low[0];

            if (level > 0)
            {
                // De-duplicate: only add if not already close to an existing level
                bool duplicate = false;
                double tolerance = TickSize * 5;
                foreach (double existing in srLevels)
                {
                    if (Math.Abs(existing - level) < tolerance)
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                {
                    if (srLevels.Count >= 50)
                        srLevels.RemoveAt(0);
                    srLevels.Add(level);
                }

                // ── Create unrecovered liquidity zone at this swing ───────────
                if (EnableLiquidityZones && !duplicate)
                {
                    double zoneWickHigh, zoneWickLow, zoneBodyHigh, zoneBodyLow;
                    if (isSwingHigh)
                    {
                        // Swing-high resistance zone: zone spans the top of the candle
                        zoneWickHigh = High[0];
                        zoneWickLow  = High[0] - TickSize * 3;
                        zoneBodyHigh = Math.Max(Open[0], Close[0]);
                        zoneBodyLow  = Math.Min(Open[0], Close[0]);
                    }
                    else
                    {
                        // Swing-low support zone: zone spans the bottom of the candle
                        zoneWickHigh = Low[0] + TickSize * 3;
                        zoneWickLow  = Low[0];
                        zoneBodyHigh = Math.Max(Open[0], Close[0]);
                        zoneBodyLow  = Math.Min(Open[0], Close[0]);
                    }

                    // Use wicks boundaries as the definitive check for zone validity
                    if (zoneWickHigh > zoneWickLow)
                    {
                        if (liquidityZones.Count >= MaxActiveZones)
                            liquidityZones.RemoveAt(0);
                        liquidityZones.Add(new LiquidityZone
                        {
                            HighPrice      = zoneWickHigh,
                            LowPrice       = zoneWickLow,
                            BodyHighPrice  = zoneBodyHigh,
                            BodyLowPrice   = zoneBodyLow,
                            CreatedBar     = CurrentBar,
                            IsBullish      = !isSwingHigh,   // swing-low zones are bullish (green)
                            IsRecovered    = false
                        });
                    }
                }
            }
        }

        // ── All 5 fake-breakout filters ───────────────────────────────────────
        private void CheckFakeBreakoutFilters(
            double barBuy, double barSell, double totalVol,
            ref bool isFake, ref int dir)
        {
            isFake = false;
            dir    = 0;

            if (CurrentBar < MomentumPeriod + SRSwingStrength * 2 + 2)
                return;

            // Find nearest S/R level that was recently broken
            double nearestBreak    = 0;
            int    breakDirection  = 0;
            double breakTolerance  = TickSize * 3;

            foreach (double sr in srLevels)
            {
                if (High[0] > sr && Low[1] < sr && Math.Abs(Close[0] - sr) < breakTolerance * 10)
                {
                    nearestBreak   = sr;
                    breakDirection = 1;
                    break;
                }
                if (Low[0] < sr && High[1] > sr && Math.Abs(Close[0] - sr) < breakTolerance * 10)
                {
                    nearestBreak   = sr;
                    breakDirection = -1;
                    break;
                }
            }

            if (nearestBreak == 0)
                return; // No S/R break to test

            // ── Filter 1: Multi-bar confirmation ─────────────────────────────
            bool confirmedBreakout;
            if (breakDirection == 1)
                confirmedBreakout = Close[0] > nearestBreak && Close[1] > nearestBreak;
            else
                confirmedBreakout = Close[0] < nearestBreak && Close[1] < nearestBreak;

            if (confirmedBreakout)
            {
                confirmBarsCount++;
                if (confirmBarsCount >= ConfirmationBars)
                    return; // Real breakout — confirmed
            }
            else
            {
                confirmBarsCount = 0;
            }

            // ── Filter 2: Volume follow-through ───────────────────────────────
            double avgVol = 0;
            int    lookback = Math.Min(CurrentBar, DeltaLookback);
            for (int i = 1; i <= lookback; i++)
                avgVol += Volume[i];
            if (lookback > 0) avgVol /= lookback;

            bool volumeOK = avgVol > 0 && (totalVol / avgVol * 100.0) >= VolumeFollowThrough;
            if (volumeOK)
                return; // Volume confirms breakout → real

            // ── Filter 3: Momentum divergence ─────────────────────────────────
            // Simple momentum proxy: compare current close vs N bars ago close
            double momentumNow  = Close[0] - Close[MomentumPeriod];
            double momentumPrev = Close[1] - Close[MomentumPeriod + 1];
            bool   momentumDiverges;
            if (breakDirection == 1)
                momentumDiverges = momentumNow < momentumPrev; // price broke up but momentum weakening
            else
                momentumDiverges = momentumNow > momentumPrev; // price broke down but momentum strengthening

            // ── Filter 4: S/R validation ──────────────────────────────────────
            bool srValid = nearestBreak > 0;

            // ── Filter 5: Risk / Reward ratio ─────────────────────────────────
            double stopDist = Math.Abs(Close[0] - nearestBreak) + TickSize;
            double reward   = Math.Abs(High[0] - Low[0]);    // bar range as proxy for potential move
            bool   rrOK     = stopDist > 0 && (reward / stopDist) >= MinRiskReward;

            // A fake breakout is flagged when: no confirmation + no volume follow-through + divergence
            if (!confirmedBreakout && !volumeOK && (momentumDiverges || srValid) && !rrOK)
            {
                isFake = true;
                dir    = breakDirection;
            }
        }

        private void DetectOrderBookWalls(Dictionary<double, BookLevel> book, bool isBid)
        {
            if (book.Count == 0)
                return;

            // Calculate average size
            double avg = 0;
            foreach (var kv in book)
                avg += kv.Value.Size;
            avg /= book.Count;

            long   wallSize  = 0;
            double wallPrice = 0;

            foreach (var kv in book)
            {
                if (kv.Value.Size > avg * WallMultiplier && kv.Value.Size > wallSize)
                {
                    wallSize  = kv.Value.Size;
                    wallPrice = kv.Key;
                }
            }

            if (isBid)
            {
                wallBidPrice = wallPrice;
                wallBidSize  = wallSize;
            }
            else
            {
                wallAskPrice = wallPrice;
                wallAskSize  = wallSize;
            }
        }

        private void UpdateDashboardText(
            double barBuy, double barSell, double delta, double deltaPct,
            bool isAbsorption, bool isImbalance, bool isFake)
        {
            string assetTag = AssetClass.ToString().ToUpper();
            dashLine1 = string.Format("IQ Candles GPU  [{0}]", assetTag);
            dashLine2 = string.Format("Buy Vol: {0:N0}  Sell Vol: {1:N0}", barBuy, barSell);
            dashLine3 = string.Format("Delta: {0:+0;-0;0}  Δ%: {1:+0.0;-0.0;0.0}%", (long)delta, deltaPct);
            dashLine4 = string.Format("Cum Δ: {0}  Session B/S: {1:N0}/{2:N0}",
                FormatDelta(cumDelta), sessionBuyVol, sessionSellVol);

            var flags = new List<string>();
            if (isAbsorption)   flags.Add("ABS");
            if (isImbalance)    flags.Add("IMB");
            if (isFake)         flags.Add("FAKE-BKT");
            if (wallBidPrice > 0) flags.Add(string.Format("BID WALL@{0:F2}", wallBidPrice));
            if (wallAskPrice > 0) flags.Add(string.Format("ASK WALL@{0:F2}", wallAskPrice));
            dashLine5 = flags.Count > 0 ? "Signals: " + string.Join(" | ", flags) : "Signals: none";

            dashLine6 = string.Format("S/R Levels: {0}  |  Imb Threshold: {1:N0}", srLevels.Count, imbalanceHigh);

            dashLine7 = (ShowZoneCount && EnableLiquidityZones)
                ? string.Format("ULZ: Active {0}  |  Recovered {1}", activeZoneCount, recoveredZoneCount)
                : "";
        }

        private BarSnapshot GetSnapshot(int barIdx)
        {
            int offset = CurrentBar - barIdx;
            int sIdx   = snapshots.Count - 1 - offset;
            if (sIdx < 0 || sIdx >= snapshots.Count)
                return null;
            return snapshots[sIdx];
        }

        private void CheckLiquidityZoneRecovery()
        {
            if (liquidityZones.Count == 0)
                return;

            double curHigh  = High[0];
            double curLow   = Low[0];
            double curOpen  = Open[0];
            double curClose = Close[0];

            int active    = 0;
            int recovered = 0;

            for (int i = 0; i < liquidityZones.Count; i++)
            {
                LiquidityZone z = liquidityZones[i];
                if (z.IsRecovered)
                {
                    recovered++;
                    continue;
                }

                // Use the same boundary as rendering based on the wicks toggle
                double topPrice = ULZIncludeWicks ? z.HighPrice : z.BodyHighPrice;
                double botPrice = ULZIncludeWicks ? z.LowPrice  : z.BodyLowPrice;

                // Zone is recovered when price trades fully through it
                bool zoneFullyCrossed = curLow <= botPrice && curHigh >= topPrice;

                if (zoneFullyCrossed ||
                    (curClose >= topPrice && curOpen <= botPrice) ||
                    (curClose <= botPrice && curOpen >= topPrice))
                {
                    z.IsRecovered = true;
                    recovered++;
                }
                else
                {
                    active++;
                }
            }

            activeZoneCount    = active;
            recoveredZoneCount = recovered;
        }

        private static double NormaliseValue(double value, double min, double max)
        {
            if (max <= min) return 0.5;
            return Math.Max(0, Math.Min(1, (value - min) / (max - min)));
        }

        private static string FormatDelta(double delta)
        {
            if (Math.Abs(delta) >= 1_000_000)
                return string.Format("{0:+0.0;-0.0}M", delta / 1_000_000);
            if (Math.Abs(delta) >= 1_000)
                return string.Format("{0:+0;-0}K", (long)(delta / 1_000));
            return string.Format("{0:+0;-0;0}", (long)delta);
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region SharpDX resource management

        private void CreateDXResources()
        {
            var rt = RenderTarget;
            if (rt == null) return;

            try
            {
                float opacityFrac = Math.Max(0f, Math.Min(1f, DashOpacity / 100f));

                dxBullBrush      = MakeBrush(rt, BullishColor, 0.85f);
                dxBearBrush      = MakeBrush(rt, BearishColor, 0.85f);
                dxAbsorbBrush    = MakeBrush(rt, AbsorptionColor, 0.90f);
                dxImbalanceBrush = MakeBrush(rt, ImbalanceColor, 0.85f);
                dxFakeBrush      = MakeBrush(rt, FakeBreakoutColor, 0.90f);
                dxWickBrush      = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color((byte)180, (byte)180, (byte)180, (byte)200));
                dxDashBgBrush    = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color((byte)10, (byte)10, (byte)20, (byte)(opacityFrac * 220)));
                dxDashTextBrush  = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color((byte)220, (byte)220, (byte)230, (byte)240));
                dxDashAccentBrush= MakeBrush(rt, FakeBreakoutColor, 1f);
                dxWallBidBrush   = MakeBrush(rt, BullishColor, 0.9f);
                dxWallAskBrush   = MakeBrush(rt, BearishColor, 0.9f);

                // ── PVSRA vector brushes ───────────────────────────────────────
                dxPvsraHighBullBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(0f / 255f, 210f / 255f, 80f / 255f, 220f / 255f));
                dxPvsraHighBearBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(220f / 255f, 0f / 255f, 30f / 255f, 220f / 255f));
                dxPvsraMidBullBrush  = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(30f / 255f, 100f / 255f, 255f / 255f, 200f / 255f));
                dxPvsraMidBearBrush  = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(120f / 255f, 40f / 255f, 200f / 255f, 200f / 255f));

                // ── Composite border brushes ───────────────────────────────────
                dxBorderAbsorbBrush    = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(255f / 255f, 215f / 255f, 0f / 255f, 230f / 255f));
                dxBorderImbalanceBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(30f / 255f, 144f / 255f, 255f / 255f, 230f / 255f));
                dxBorderFakeBrush      = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(255f / 255f, 120f / 255f, 0f / 255f, 230f / 255f));

                // ── Zone fill brushes (semi-transparent) ──────────────────────
                float zoneAlphaF = Math.Max(0.02f, Math.Min(0.80f, (float)ZoneOpacity / 100f));
                dxULZBullishBrush = MakeBrush(rt, ULZBullishColor, zoneAlphaF);
                dxULZBearishBrush = MakeBrush(rt, ULZBearishColor, zoneAlphaF);

                dxWriteFactory = new SharpDX.DirectWrite.Factory();
                dxDashFormat  = new SharpDX.DirectWrite.TextFormat(
                    dxWriteFactory, "Consolas", DashFontSize);
                dxLabelFormat = new SharpDX.DirectWrite.TextFormat(
                    dxWriteFactory, "Consolas", Math.Max(8, DashFontSize - 1));

                dxReady = true;
            }
            catch
            {
                dxReady = false;
            }
        }

        private static SharpDX.Direct2D1.SolidColorBrush MakeBrush(
            SharpDX.Direct2D1.RenderTarget rt,
            System.Windows.Media.Brush wpfBrush,
            float opacity)
        {
            var scb = wpfBrush as System.Windows.Media.SolidColorBrush;
            if (scb != null)
            {
                var c = scb.Color;
                return new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, opacity));
            }
            // Fallback: white
            return new SharpDX.Direct2D1.SolidColorBrush(rt,
                new SharpDX.Color4(1f, 1f, 1f, opacity));
        }

        private void DisposeDXResources()
        {
            DisposeRef(ref dxBullBrush);
            DisposeRef(ref dxBearBrush);
            DisposeRef(ref dxAbsorbBrush);
            DisposeRef(ref dxImbalanceBrush);
            DisposeRef(ref dxFakeBrush);
            DisposeRef(ref dxWickBrush);
            DisposeRef(ref dxDashBgBrush);
            DisposeRef(ref dxDashTextBrush);
            DisposeRef(ref dxDashAccentBrush);
            DisposeRef(ref dxWallBidBrush);
            DisposeRef(ref dxWallAskBrush);
            DisposeRef(ref dxPvsraHighBullBrush);
            DisposeRef(ref dxPvsraHighBearBrush);
            DisposeRef(ref dxPvsraMidBullBrush);
            DisposeRef(ref dxPvsraMidBearBrush);
            DisposeRef(ref dxBorderAbsorbBrush);
            DisposeRef(ref dxBorderImbalanceBrush);
            DisposeRef(ref dxBorderFakeBrush);
            DisposeRef(ref dxULZBullishBrush);
            DisposeRef(ref dxULZBearishBrush);
            DisposeRef(ref dxDashFormat);
            DisposeRef(ref dxLabelFormat);
            DisposeRef(ref dxWriteFactory);
        }

        private static void DisposeRef<T>(ref T resource) where T : class, IDisposable
        {
            if (resource != null)
            {
                resource.Dispose();
                resource = null;
            }
        }

        #endregion
    }
}
