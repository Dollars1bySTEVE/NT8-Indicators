// IQMainUltimate — Standalone ultimate version of IQMainGPU_Enhanced for NinjaTrader 8.
// Includes ALL features from IQMainGPU_Enhanced plus full TPO/Market Profile integration.
// Original files (IQMainGPU.cs, IQMainGPU_Enhanced.cs) are unchanged.
//
// New TPO Features (group 17. TPO Settings):
//   • Point of Control (POC) — highest-volume price per session, rendered as gold horizontal line
//   • Value Area High/Low (VAH/VAL) — 70% volume distribution, cyan dashed lines + blue fill
//   • Initial Balance (IB) — first 60 minutes of session, bracket + extension targets
//   • Naked TPO Levels — POC/VAH/VAL from prior sessions not yet revisited, orange dashed lines
//   • Profile Shape Classification — Normal, TrendDay (D), DoubleDistribution (b), Balanced (P)
//
// Dashboard Enhancements:
//   • Monitoring Dashboard: TPO section (POC, VAH, VAL, Profile Shape, Naked POC)
//   • Entry Mode Dashboard: TPO-aware stops (VAL) and targets (VAH / IB Extension)
//   • UltimateStopMode.TPOBased: always use VAL for stops
//   • UltimateTargetMode.VAH / IBExtension: use current VAH or IB extension for targets

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
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// NinjaTrader 8 requires custom enums declared OUTSIDE all namespaces
// so the auto-generated partial class code can resolve them without ambiguity.
// Reference: forum.ninjatrader.com threads #1182932, #95909, #1046853
//
// NOTE: IQMDashboardPosition, IQMLineStyle, IQMAssetClass, IQMCandleColorMode, VwapSessionAnchor
// are declared in IQMainGPU.cs (same compilation unit) and must NOT be re-declared here.
// DashboardPositionType, StopPlacementMode, TargetPlacementMode, ConflictDescriptionLevel
// are declared in IQMainGPU_Enhanced.cs and must NOT be re-declared here.
// IQMainUltimate.cs coexists alongside IQMainGPU.cs and IQMainGPU_Enhanced.cs in the
// NT8 Indicators folder; all three are compiled together into a single assembly.

// ── New enums exclusive to IQMainUltimate ────────────────────────────────────

/// <summary>TPO session profile shape classification.
/// Normal = balanced bell curve; TrendDay = D-shape (directional close);
/// DoubleDistribution = b-shape (two acceptance areas); Balanced = P-shape (rotational day).</summary>
public enum TPOProfileShape { Normal, TrendDay, DoubleDistribution, Balanced }

/// <summary>Stop-loss algorithm for IQMainUltimate Entry Mode.
/// Extends the base StopPlacementMode with a TPO-aware option.</summary>
public enum UltimateStopMode { AutoDetected, PivotBased, HVNBased, ManualInput, TPOBased }

/// <summary>Take-profit algorithm for IQMainUltimate Entry Mode.
/// Extends the base TargetPlacementMode with TPO-aware options.</summary>
public enum UltimateTargetMode { AutoDetected, PivotR1, PivotR2, ManualInput, VAH, IBExtension }

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// IQMainUltimate — Standalone ultimate indicator: all IQMainGPU_Enhanced features
    /// plus complete TPO/Market Profile integration (POC, Value Area, Initial Balance,
    /// Naked Levels, Profile Shape Classification). Uses SharpDX GPU rendering throughout.
    ///
    /// TPO Features (group 17. TPO Settings):
    ///  • POC Line: bright gold, extends from session start, labelled with price + volume
    ///  • Value Area: cyan dashed VAH/VAL lines + semi-transparent blue fill (70% vol)
    ///  • Initial Balance: IB range bracket at session start + dotted extension targets
    ///  • Naked Levels: orange dashed forward-projecting lines for unvisited POC/VAH/VAL
    ///  • Profile Shape: Normal / TrendDay / DoubleDistribution / Balanced classification
    ///
    /// Dashboard Enhancements:
    ///  • Monitoring Dashboard now shows POC, VAH, VAL, Profile Shape, Naked POC info
    ///  • Entry Mode Dashboard labels stops/targets with TPO level names
    ///  • UltimateStopMode.TPOBased uses VAL for stops
    ///  • UltimateTargetMode.VAH / IBExtension provide TPO-based targets
    /// </summary>
    public class IQMainUltimate : Indicator
    {
        // ════════════════════════════════════════════════════════════════════════
        #region Inner types

        /// <summary>One completed session window for rendering the session box.</summary>
        private class SessionBox
        {
            public DateTime StartTime;
            public DateTime EndTime;
            public int      StartBarIndex;
            public int      EndBarIndex;
            public double   SessionHigh;
            public double   SessionLow;
            public bool     IsComplete;
            public int      SessionId;   // 0=London,1=NY,2=Tokyo,3=HK,4=Sydney,5=EUBrinks,6=USBrinks,7=Frankfurt
        }

        /// <summary>Snapshot of pivot levels for a trading day.</summary>
        private class PivotSnapshot
        {
            public double PP, R1, R2, R3, S1, S2, S3;
            public double M0, M1, M2, M3, M4, M5;
            public int    StartBarIndex;
        }

        /// <summary>Tracks the open price and bar range for a single trading day.</summary>
        private class DailyOpenEntry
        {
            public double OpenPrice;
            public int    StartBarIndex;
            public int    EndBarIndex;
        }

        /// <summary>Tracks the open price and bar range for a timed session open (ETH, Asia, London, US).</summary>
        private class SessionOpenEntry
        {
            public double   OpenPrice;
            public int      StartBarIndex;
            public int      EndBarIndex;
            public DateTime SessionStart;
            public DateTime SessionEnd;
        }

        /// <summary>Per-bar computed microstructure snapshot.</summary>
        private class BarSnapshot
        {
            public double BuyVolume;
            public double SellVolume;
            public double Delta;
            public double DeltaPct;
            public double CumDelta;
            public bool IsAbsorption;
            public bool IsImbalance;
            public bool IsFakeBreakout;
            public int FakeBreakoutDir;
        }

        /// <summary>Single level in the order book snapshot.</summary>
        private class BookLevel
        {
            public double Price;
            public long   Size;
            public bool   IsSpoof;
        }

        /// <summary>Unrecovered liquidity zone left behind by a PVSRA candle.</summary>
        private class LiquidityZone
        {
            public double HighPrice;
            public double LowPrice;
            public double BodyHighPrice;
            public double BodyLowPrice;
            public int    CreatedBar;
            public int    OriginBarIndex;
            public bool   IsBullish;
            public bool   IsAbsorption;
            public bool   IsRecovered;
            public double PartialRecoveryHigh;
            public double PartialRecoveryLow;
        }

        /// <summary>Per-bar VWAP and standard deviation band data.</summary>
        private class VwapBarData
        {
            public double Vwap;
            public double StdDev;
            public double Band1Upper;
            public double Band1Lower;
            public double Band2Upper;
            public double Band2Lower;
            public double Band3Upper;
            public double Band3Lower;
        }

        /// <summary>OTE (Optimal Trade Entry) zone based on ICT Fibonacci retracement levels.</summary>
        private class OTEZone
        {
            public double SwingHigh;
            public double SwingLow;
            public double Level62;
            public double Level705;
            public double Level79;
            public int    SwingHighBar;
            public int    SwingLowBar;
            public int    CreatedBar;
            public bool   IsBullish;
            public bool   IsActive;
        }

        // ════════════════════════════════════════════════════════════════════════
        // TPO / Market Profile inner classes (IQMainUltimate additions)
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>Volume accumulated at a single price level during a TPO session.
        /// Used to build the volume profile for POC and Value Area calculations.</summary>
        private class TPOPriceLevel
        {
            /// <summary>Price value of this level (rounded to instrument tick size).</summary>
            public double Price;
            /// <summary>Total volume traded at this price level during the session.</summary>
            public double Volume;
        }

        /// <summary>One trading session's TPO / Market Profile data.
        /// Calculated once per session using volume-at-price accumulation.</summary>
        private class TPOSession
        {
            // ── Identity ──────────────────────────────────────────────────────
            /// <summary>0=London, 1=NY, 2=Tokyo, 3=HK, 4=Sydney, 5=EUBrinks, 6=USBrinks, 7=Frankfurt</summary>
            public int      SessionId;
            public DateTime StartTime;
            public DateTime EndTime;
            public int      StartBarIndex;
            public int      EndBarIndex;
            public bool     IsComplete;

            // ── Volume profile ────────────────────────────────────────────────
            /// <summary>Dictionary keyed by price (rounded to tick) → accumulated volume.</summary>
            public Dictionary<double, double> VolumeProfile;

            // ── POC ───────────────────────────────────────────────────────────
            /// <summary>Point of Control: price level with highest volume in this session.</summary>
            public double POCPrice;
            /// <summary>Total volume at the POC level.</summary>
            public double POCVolume;

            // ── Value Area ────────────────────────────────────────────────────
            /// <summary>Value Area High: upper boundary of the 70% volume distribution.</summary>
            public double ValueAreaHigh;
            /// <summary>Value Area Low: lower boundary of the 70% volume distribution.</summary>
            public double ValueAreaLow;
            /// <summary>Percentage of total volume contained in the value area (default 70%).</summary>
            public double ValueAreaPct;

            // ── Initial Balance ───────────────────────────────────────────────
            /// <summary>Highest price during the first 60 minutes of this session.</summary>
            public double IBHigh;
            /// <summary>Lowest price during the first 60 minutes of this session.</summary>
            public double IBLow;
            /// <summary>IBHigh - IBLow range in price terms.</summary>
            public double IBRange;
            /// <summary>IB High + IBRange × 1.0 (first upside extension target).</summary>
            public double IBHighExtension;
            /// <summary>IB Low  - IBRange × 1.0 (first downside extension target).</summary>
            public double IBLowExtension;
            /// <summary>True once the initial-balance 60-minute window has closed.</summary>
            public bool   IBComplete;
            /// <summary>Bar index when the IB window closed.</summary>
            public int    IBEndBarIndex;

            // ── Profile Shape ─────────────────────────────────────────────────
            /// <summary>Classified profile shape for this session.</summary>
            public TPOProfileShape ProfileShape;

            /// <summary>Initialise an empty TPOSession for a given session window.</summary>
            public TPOSession(int sessionId, DateTime start, DateTime end, int startBar)
            {
                SessionId    = sessionId;
                StartTime    = start;
                EndTime      = end;
                StartBarIndex = startBar;
                EndBarIndex  = startBar;
                IsComplete   = false;
                VolumeProfile = new Dictionary<double, double>(200);
                IBHigh       = double.MinValue;
                IBLow        = double.MaxValue;
                IBComplete   = false;
                IBEndBarIndex = startBar;
                ValueAreaPct = 70.0;
            }
        }

        /// <summary>A single "naked" TPO level from a completed session that has not yet been
        /// revisited by price. Remains active (orange dashed line) until price touches it
        /// or the level exceeds its maximum age.</summary>
        private class NakedTPOLevel
        {
            /// <summary>Exact price of this naked level.</summary>
            public double Price;
            /// <summary>Type: "POC", "VAH", or "VAL".</summary>
            public string LevelType;
            /// <summary>Session label (e.g. "NY", "London") for the originating session.</summary>
            public string SessionLabel;
            /// <summary>Date/time when the originating session started (used for age check).</summary>
            public DateTime SessionDate;
            /// <summary>Bar index when this naked level was created.</summary>
            public int CreatedBar;
            /// <summary>True once price has traded through this level — stops rendering.</summary>
            public bool IsClosed;
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Private fields — Sessions / Pivots / Ranges

        private NinjaTrader.NinjaScript.Indicators.EMA    ema5Ind, ema13Ind, ema50Ind, ema200Ind, ema800Ind;
        private NinjaTrader.NinjaScript.Indicators.StdDev stdDev100Ind;
        private NinjaTrader.Gui.Tools.SimpleFont labelFont;

        private PivotSnapshot currentPivot;
        private double        dayHigh, dayLow, dayClose;
        private double        prevDayHigh, prevDayLow, prevDayClose;
        private bool          prevDayLoaded;

        private double yesterdayHigh, yesterdayLow;
        private double lastWeekHigh, lastWeekLow;
        private int    currentDayOfWeek;
        private double weekHigh, weekLow;
        private bool   weekDataLoaded;
        private int    currentMonth;
        private double monthHigh, monthLow;
        private bool   monthDataLoaded;

        private Queue<double> dailyRanges;
        private Queue<double> weeklyRanges;
        private Queue<double> monthlyRanges;
        private Queue<double> rdRanges;
        private Queue<double> rwRanges;

        private double adrValue, awrValue, amrValue, rdValue, rwValue;
        private double adrHigh, adrLow, awrHigh, awrLow, amrHigh, amrLow;
        private double rdHigh, rdLow, rwHigh, rwLow;
        private double dailyOpen;
        private bool   dailyOpenSet;

        private List<DailyOpenEntry> dailyOpenEntries;
        private DailyOpenEntry       currentDailyOpenEntry;

        // ETH Daily Open (6 PM ET) and RTH Session Opens (Asia, London, US)
        private List<SessionOpenEntry> ethOpenEntries;
        private SessionOpenEntry       currentEthOpenEntry;
        private List<SessionOpenEntry> rthAsiaOpenEntries;
        private SessionOpenEntry       currentRthAsiaEntry;
        private List<SessionOpenEntry> rthLondonOpenEntries;
        private SessionOpenEntry       currentRthLondonEntry;
        private List<SessionOpenEntry> rthUsOpenEntries;
        private SessionOpenEntry       currentRthUsEntry;

        // VWAP tracking (ETH-anchored and RTH-anchored, one entry per bar)
        private List<VwapBarData> vwapEthData;
        private double            vwapEthCumulativePV;
        private double            vwapEthCumulativeVolume;
        private double            vwapEthCumulativeTPVSq;
        private DateTime          vwapEthSessionStart;

        private List<VwapBarData> vwapRthData;
        private double            vwapRthCumulativePV;
        private double            vwapRthCumulativeVolume;
        private double            vwapRthCumulativeTPVSq;
        private DateTime          vwapRthSessionStart;

        private List<SessionBox> sessionBoxes;
        private SessionBox[]     activeSessions;
        private readonly object  _sessionLock = new object();

        // ── ET time-zone helper (shared by all session/VWAP/TPO math) ─────────
        // "Eastern Standard Time" is the Windows TZ ID; it correctly handles EST↔EDT.
        private static readonly TimeZoneInfo EtZone = SafeFindEtZone();
        private static TimeZoneInfo SafeFindEtZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch (Exception) { return TimeZoneInfo.CreateCustomTimeZone("ET-Fallback", TimeSpan.FromHours(-5), "ET-Fallback", "ET-Fallback"); }
        }
        /// <summary>Convert the current bar's time to Eastern Time, regardless of chart display TZ.</summary>
        private DateTime BarTimeEt()
        {
            DateTime t = Bars.GetTime(CurrentBar);
            DateTime tUnspec = DateTime.SpecifyKind(t, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTime(tUnspec, Bars.TradingHours.TimeZoneInfo, EtZone);
        }

        private double psyWeekHigh, psyWeekLow;
        private int    psyWeekStartBar;
        private double psyDayHigh, psyDayLow;
        private int    psyDayStartBar;

        private bool alertAdrHighFired, alertAdrLowFired;
        private bool alertAwrHighFired, alertAwrLowFired;
        private bool alertAmrHighFired, alertAmrLowFired;

        // Fallback-log gating flags — reset on each new calendar day to prevent per-tick spam
        private bool _tpoStopBearishFallbackLogged;
        private bool _tpoStopBullishFallbackLogged;
        private bool _ibTargetBearishFallbackLogged;
        private bool _ibTargetBullishFallbackLogged;

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Private fields — Candles / Microstructure

        private List<BarSnapshot>   snapshots;
        private double              cumDelta;
        private double              sessionBuyVol;
        private double              sessionSellVol;
        private double              prevTickPrice;
        private long                prevTickVol;

        private List<double>        deltaHistory;
        private double              imbalanceLow;
        private double              imbalanceHigh;

        private List<double>        srLevels;

        private Dictionary<double, BookLevel> bidBook;
        private Dictionary<double, BookLevel> askBook;
        private bool   level2Available;
        private double bestBidPrice;
        private double bestAskPrice;
        private long   bestBidSize;
        private long   bestAskSize;
        private long   totalBidDepth;
        private long   totalAskDepth;
        private double wallBidPrice;
        private long   wallBidSize;
        private double wallAskPrice;
        private long   wallAskSize;
        private string l2StatusText = "L2: waiting…";

        private double absorptionThreshold;
        private int    confirmBarsCount;
        private int    breakoutDir;
        private double breakoutLevel;

        private List<LiquidityZone> liquidityZones;
        private int  activeZoneCount;
        private int  recoveredZoneCount;

        // OTE zone tracking
        private List<OTEZone> oteZones;
        private int    oteLastSwingHighBar;
        private int    oteLastSwingLowBar;
        private double oteLastSwingHigh;
        private double oteLastSwingLow;

        private string dashLine2 = "";
        private string dashLine3 = "";
        private string dashLine4 = "";
        private string dashLine5 = "";
        private string dashLine6 = "";
        private string dashLine7 = "";

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Private fields — Enhanced Dashboard

        // Computed once per render frame in CalculateDashboardMetrics()
        private double dashboardEntryPrice;
        private double dashboardStopPrice;
        private double dashboardTargetPrice;
        private int    dashboardConfidence;
        private string dashboardPrimarySignal  = "";
        private bool   dashboardConflictDetected;
        private string dashboardConflictText   = "";

        // Signal timestamp tracking for stale/expired signal detection
        private DateTime lastSignalDetectedTime = DateTime.MinValue;
        private string   lastTrackedSignal      = "";
        private bool     signalIsStale          = false;

        // MaxTPOStopTicks / MaxTPOTargetTicks are now user properties (see group 17. TPO Settings)

        // Cached bar values — set every OnBarUpdate call so OnRender always reads the freshest tick
        private double _latestClose;
        private double _cachedVwapValue;
        private double _cachedEma50Value;

        // Severity tag constants used in VeryDetailed conflict descriptions
        private const string SeverityCritical = "\u26a0 [CRITICAL]";
        private const string SeverityHigh     = "\u26a0 [HIGH]";
        private const string SeverityModerate = "\u26a0 [MODERATE]";

        // Session IDs considered high-participation (London=0, NewYork=1, EuBrinks=5, UsBrinks=6)
        private static readonly int[] HighParticipationSessionIds = { 0, 1, 5, 6 };

        // SharpDX resources for the new Enhanced dashboard panels
        private SharpDX.Direct2D1.SolidColorBrush dxEnhDashBgBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxEnhDashTextBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxEnhDashHeaderBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxEnhDashWarningBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxEnhDashGreenBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxEnhDashRedBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxEnhDashNeutralBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxEntryLineBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxStopLineBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxTargetLineBrush;
        private SharpDX.Direct2D1.SolidColorBrush _brushDimmedText;
        private bool                               _dashboardSkipDueToRR;

        // Source-tracking enums and fields for stop/target label transparency (PR-B4)
        private enum TargetSource { None, IBExtHigh, IBExtLow, VAH, VAL, PivotR1, PivotR2, PivotS1, PivotS2, NakedPOC, SRLevel, ADR, Manual }
        private enum StopSource   { None, VAH, VAL, PrevVAH, PrevVAL, PivotS1, PivotR1, LiquidityZone, SRLevel, ADR, Manual }

        private TargetSource _lastTargetSource      = TargetSource.None;
        private StopSource   _lastStopSource        = StopSource.None;
        private bool         _lastTargetWasFallback = false;
        private bool         _lastStopWasFallback   = false;
        private string       _lastTargetSourceDetail = "";
        private string       _lastStopSourceDetail   = "";
        private SharpDX.DirectWrite.TextFormat     dxMainDashFormat;
        private SharpDX.DirectWrite.TextFormat     dxEnhDashFormat;
        private SharpDX.DirectWrite.TextFormat     dxEnhMonFormat;

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Private fields — TPO / Market Profile (IQMainUltimate additions)

        // ── TPO session tracking ──────────────────────────────────────────────
        /// <summary>All completed and in-progress TPO sessions.</summary>
        private List<TPOSession> tpoSessions;

        /// <summary>Per session-ID (0-7): the currently open TPO session being accumulated.</summary>
        private TPOSession[] activeTPOSessions;

        /// <summary>Most recent completed TPO session — used for stop/target calculations.</summary>
        private TPOSession previousDayTPO;

        // ── Naked TPO levels ──────────────────────────────────────────────────
        /// <summary>Unvisited POC/VAH/VAL levels from prior sessions (forward-projecting orange lines).</summary>
        private List<NakedTPOLevel> nakedTPOLevels;

        // ── TPO cached metrics (updated each bar for dashboard display) ────────
        private double tpoCurrentPOC;
        private double tpoCurrentVAH;
        private double tpoCurrentVAL;
        private double tpoCurrentIBHigh;
        private double tpoCurrentIBLow;
        private double tpoCurrentIBHighExt;
        private double tpoCurrentIBLowExt;
        private TPOProfileShape tpoCurrentShape = TPOProfileShape.Normal;
        private string tpoCurrentSessionLabel   = "";

        // ── Nearest naked level (for dashboards) ─────────────────────────────
        private NakedTPOLevel tpoNearestNakedLevel;

        // ── Label Y-position de-collision tracking (cleared at start of RenderTPOLevels) ──
        private HashSet<int> _usedLabelYPositions = new HashSet<int>();

        // ── TPO SharpDX brushes ───────────────────────────────────────────────
        /// <summary>Gold brush for POC horizontal lines (#FFD700).</summary>
        private SharpDX.Direct2D1.SolidColorBrush dxTPOPocBrush;
        /// <summary>Cyan brush for VAH/VAL dashed lines (#00FFFF).</summary>
        private SharpDX.Direct2D1.SolidColorBrush dxTPOVALineBrush;
        /// <summary>Semi-transparent steel-blue fill for the Value Area rectangle.</summary>
        private SharpDX.Direct2D1.SolidColorBrush dxTPOVAFillBrush;
        /// <summary>White/light brush for IB bracket vertical lines.</summary>
        private SharpDX.Direct2D1.SolidColorBrush dxTPOIBBrush;
        /// <summary>Orange brush for naked TPO level lines (#FF8C00).</summary>
        private SharpDX.Direct2D1.SolidColorBrush dxTPONakedBrush;
        /// <summary>Label format for TPO level text (Consolas 11pt).</summary>
        private SharpDX.DirectWrite.TextFormat     dxTPOLabelFormat;

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Private fields — SharpDX GPU resources

        private bool dxReady;

        // Shared write factory and formats
        private SharpDX.DirectWrite.Factory        dxWriteFactory;
        private SharpDX.DirectWrite.TextFormat     dxLabelFormat;
        private SharpDX.DirectWrite.TextFormat     dxSmallFormat;
        private SharpDX.DirectWrite.TextFormat     dxDashFormat;

        // Pivot brushes
        private SharpDX.Direct2D1.SolidColorBrush dxPPBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxRLevelBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxSLevelBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxMLevelBrush;

        // Yesterday / last week brushes
        private SharpDX.Direct2D1.SolidColorBrush dxYesterdayBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxLastWeekBrush;

        // ADR / AWR / AMR brushes
        private SharpDX.Direct2D1.SolidColorBrush dxAdrBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxAwrBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxAmrBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxRdBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxRwBrush;

        // Daily open brush
        private SharpDX.Direct2D1.SolidColorBrush dxDailyOpenBrush;

        // ETH Daily Open and RTH Session Open brushes
        private SharpDX.Direct2D1.SolidColorBrush dxEthDailyOpenBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxAsiaOpenBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxLondonOpenBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxUsOpenBrush;

        // Session box brushes
        private SharpDX.Direct2D1.SolidColorBrush[] dxSessionBoxBrush;
        private SharpDX.Direct2D1.SolidColorBrush[] dxSessionBorderBrush;

        // Psy level brush
        private SharpDX.Direct2D1.SolidColorBrush dxPsyBrush;

        // Dashboard brushes (shared between session table and candle dashboard)
        private SharpDX.Direct2D1.SolidColorBrush dxDashBgBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxDashTextBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxDashHeaderBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxDashAccentBrush;

        // Candle body brushes
        private SharpDX.Direct2D1.SolidColorBrush dxBullBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBearBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxAbsorbBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxImbalanceBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxFakeBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxWickBrush;

        // Wall brushes
        private SharpDX.Direct2D1.SolidColorBrush dxWallBidBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxWallAskBrush;

        // PVSRA brushes
        private SharpDX.Direct2D1.SolidColorBrush dxPvsraHighBullBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxPvsraHighBearBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxPvsraMidBullBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxPvsraMidBearBrush;

        // Composite border brushes
        private SharpDX.Direct2D1.SolidColorBrush dxBorderAbsorbBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBorderImbalanceBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBorderFakeBrush;

        // Zone fill brushes
        private SharpDX.Direct2D1.SolidColorBrush dxULZBullishBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxULZBearishBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxULZBlueBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxULZPinkBrush;

        // VWAP brushes
        private SharpDX.Direct2D1.SolidColorBrush dxVwapAboveBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxVwapBelowBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxVwapNeutralBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxVwapBand1Brush;
        private SharpDX.Direct2D1.SolidColorBrush dxVwapBand2Brush;
        private SharpDX.Direct2D1.SolidColorBrush dxVwapBand3Brush;
        private SharpDX.Direct2D1.SolidColorBrush dxVwapFillBrush;

        // OTE zone brushes
        private SharpDX.Direct2D1.SolidColorBrush dxOTEBullishBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxOTEBearishBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxOTELineBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxOTEOptimalBrush;

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 1. Core

        [NinjaScriptProperty]
        [Display(Name = "Asset Class", Order = 1, GroupName = "1. Core",
            Description = "Calibrates tick size and thresholds for the selected market type.")]
        public IQMAssetClass AssetClass { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Candle Color Mode", Order = 2, GroupName = "1. Core")]
        public IQMCandleColorMode ColorMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Level 2 (Order Book)", Order = 3, GroupName = "1. Core",
            Description = "Subscribe to market depth (L2) data. Requires broker support.")]
        public bool EnableLevel2 { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 2. EMAs

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Label X Offset (bars)", Order = 1, GroupName = "2. EMAs")]
        public int LabelOffsetBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "Label Y Offset (ticks)", Order = 2, GroupName = "2. EMAs")]
        public int LabelOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show EMA 5", Order = 3, GroupName = "2. EMAs")]
        public bool ShowEma5 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA 5 Color", Order = 4, GroupName = "2. EMAs")]
        [XmlIgnore]
        public System.Windows.Media.Brush Ema5Color { get; set; }
        [Browsable(false)]
        public string Ema5ColorSerializable { get => Serialize.BrushToString(Ema5Color); set => Ema5Color = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "EMA 5 Thickness", Order = 5, GroupName = "2. EMAs")]
        public int Ema5Thickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show EMA 13", Order = 6, GroupName = "2. EMAs")]
        public bool ShowEma13 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA 13 Color", Order = 7, GroupName = "2. EMAs")]
        [XmlIgnore]
        public System.Windows.Media.Brush Ema13Color { get; set; }
        [Browsable(false)]
        public string Ema13ColorSerializable { get => Serialize.BrushToString(Ema13Color); set => Ema13Color = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "EMA 13 Thickness", Order = 8, GroupName = "2. EMAs")]
        public int Ema13Thickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show EMA 50", Order = 9, GroupName = "2. EMAs")]
        public bool ShowEma50 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA 50 Color", Order = 10, GroupName = "2. EMAs")]
        [XmlIgnore]
        public System.Windows.Media.Brush Ema50Color { get; set; }
        [Browsable(false)]
        public string Ema50ColorSerializable { get => Serialize.BrushToString(Ema50Color); set => Ema50Color = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "EMA 50 Thickness", Order = 11, GroupName = "2. EMAs")]
        public int Ema50Thickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show EMA 50 Cloud", Order = 12, GroupName = "2. EMAs")]
        public bool ShowEma50Cloud { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Cloud Fill Color", Order = 13, GroupName = "2. EMAs")]
        [XmlIgnore]
        public System.Windows.Media.Brush CloudFillColor { get; set; }
        [Browsable(false)]
        public string CloudFillColorSerializable { get => Serialize.BrushToString(CloudFillColor); set => CloudFillColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Cloud Fill Opacity %", Order = 14, GroupName = "2. EMAs")]
        public int CloudFillOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show EMA 200", Order = 15, GroupName = "2. EMAs")]
        public bool ShowEma200 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA 200 Color", Order = 16, GroupName = "2. EMAs")]
        [XmlIgnore]
        public System.Windows.Media.Brush Ema200Color { get; set; }
        [Browsable(false)]
        public string Ema200ColorSerializable { get => Serialize.BrushToString(Ema200Color); set => Ema200Color = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "EMA 200 Thickness", Order = 17, GroupName = "2. EMAs")]
        public int Ema200Thickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show EMA 800", Order = 18, GroupName = "2. EMAs")]
        public bool ShowEma800 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA 800 Color", Order = 19, GroupName = "2. EMAs")]
        [XmlIgnore]
        public System.Windows.Media.Brush Ema800Color { get; set; }
        [Browsable(false)]
        public string Ema800ColorSerializable { get => Serialize.BrushToString(Ema800Color); set => Ema800Color = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "EMA 800 Thickness", Order = 20, GroupName = "2. EMAs")]
        public int Ema800Thickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show EMA Labels", Order = 21, GroupName = "2. EMAs")]
        public bool ShowEmaLabels { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 3. Pivot Points

        [NinjaScriptProperty]
        [Display(Name = "Show Pivot PP", Order = 1, GroupName = "3. Pivot Points")]
        public bool ShowPP { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show R1/S1", Order = 2, GroupName = "3. Pivot Points")]
        public bool ShowLevel1 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show R2/S2", Order = 3, GroupName = "3. Pivot Points")]
        public bool ShowLevel2 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show R3/S3", Order = 4, GroupName = "3. Pivot Points")]
        public bool ShowLevel3 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Pivot Labels", Order = 5, GroupName = "3. Pivot Points")]
        public bool ShowPivotLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Pivot Line Style", Order = 6, GroupName = "3. Pivot Points")]
        public IQMLineStyle PivotLineStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PP Color", Order = 7, GroupName = "3. Pivot Points")]
        [XmlIgnore]
        public System.Windows.Media.Brush PPColor { get; set; }
        [Browsable(false)]
        public string PPColorSerializable { get => Serialize.BrushToString(PPColor); set => PPColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "R Levels Color", Order = 8, GroupName = "3. Pivot Points")]
        [XmlIgnore]
        public System.Windows.Media.Brush RLevelColor { get; set; }
        [Browsable(false)]
        public string RLevelColorSerializable { get => Serialize.BrushToString(RLevelColor); set => RLevelColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "S Levels Color", Order = 9, GroupName = "3. Pivot Points")]
        [XmlIgnore]
        public System.Windows.Media.Brush SLevelColor { get; set; }
        [Browsable(false)]
        public string SLevelColorSerializable { get => Serialize.BrushToString(SLevelColor); set => SLevelColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "Show M Levels", Order = 10, GroupName = "3. Pivot Points")]
        public bool ShowMLevels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show M Labels", Order = 11, GroupName = "3. Pivot Points")]
        public bool ShowMLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "M Level Color", Order = 12, GroupName = "3. Pivot Points")]
        [XmlIgnore]
        public System.Windows.Media.Brush MLevelColor { get; set; }
        [Browsable(false)]
        public string MLevelColorSerializable { get => Serialize.BrushToString(MLevelColor); set => MLevelColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "M Level Opacity %", Order = 13, GroupName = "3. Pivot Points")]
        public int MLevelOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "M Line Style", Order = 14, GroupName = "3. Pivot Points")]
        public IQMLineStyle MLevelLineStyle { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 4. Sessions — London

        [NinjaScriptProperty]
        [Display(Name = "Show London Session", Order = 1, GroupName = "4. Sessions — London")]
        public bool ShowLondon { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "London Label", Order = 2, GroupName = "4. Sessions — London")]
        public string LondonLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "London Show Label", Order = 3, GroupName = "4. Sessions — London")]
        public bool LondonShowLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "London Show Opening Range", Order = 4, GroupName = "4. Sessions — London")]
        public bool LondonShowOpeningRange { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "London Box Color", Order = 5, GroupName = "4. Sessions — London")]
        [XmlIgnore]
        public System.Windows.Media.Brush LondonColor { get; set; }
        [Browsable(false)]
        public string LondonColorSerializable { get => Serialize.BrushToString(LondonColor); set => LondonColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "London Opacity %", Order = 6, GroupName = "4. Sessions — London")]
        public int LondonOpacity { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 4. Sessions — New York

        [NinjaScriptProperty]
        [Display(Name = "Show New York Session", Order = 1, GroupName = "4. Sessions — New York")]
        public bool ShowNewYork { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "New York Label", Order = 2, GroupName = "4. Sessions — New York")]
        public string NewYorkLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "New York Show Label", Order = 3, GroupName = "4. Sessions — New York")]
        public bool NewYorkShowLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "New York Show Opening Range", Order = 4, GroupName = "4. Sessions — New York")]
        public bool NewYorkShowOpeningRange { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "New York Box Color", Order = 5, GroupName = "4. Sessions — New York")]
        [XmlIgnore]
        public System.Windows.Media.Brush NewYorkColor { get; set; }
        [Browsable(false)]
        public string NewYorkColorSerializable { get => Serialize.BrushToString(NewYorkColor); set => NewYorkColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "New York Opacity %", Order = 6, GroupName = "4. Sessions — New York")]
        public int NewYorkOpacity { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 4. Sessions — Tokyo

        [NinjaScriptProperty]
        [Display(Name = "Show Tokyo Session", Order = 1, GroupName = "4. Sessions — Tokyo")]
        public bool ShowTokyo { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Tokyo Label", Order = 2, GroupName = "4. Sessions — Tokyo")]
        public string TokyoLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Tokyo Show Label", Order = 3, GroupName = "4. Sessions — Tokyo")]
        public bool TokyoShowLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Tokyo Show Opening Range", Order = 4, GroupName = "4. Sessions — Tokyo")]
        public bool TokyoShowOpeningRange { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Tokyo Box Color", Order = 5, GroupName = "4. Sessions — Tokyo")]
        [XmlIgnore]
        public System.Windows.Media.Brush TokyoColor { get; set; }
        [Browsable(false)]
        public string TokyoColorSerializable { get => Serialize.BrushToString(TokyoColor); set => TokyoColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Tokyo Opacity %", Order = 6, GroupName = "4. Sessions — Tokyo")]
        public int TokyoOpacity { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 4. Sessions — Hong Kong

        [NinjaScriptProperty]
        [Display(Name = "Show Hong Kong Session", Order = 1, GroupName = "4. Sessions — Hong Kong")]
        public bool ShowHongKong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Hong Kong Label", Order = 2, GroupName = "4. Sessions — Hong Kong")]
        public string HongKongLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Hong Kong Show Label", Order = 3, GroupName = "4. Sessions — Hong Kong")]
        public bool HongKongShowLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Hong Kong Show Opening Range", Order = 4, GroupName = "4. Sessions — Hong Kong")]
        public bool HongKongShowOpeningRange { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Hong Kong Box Color", Order = 5, GroupName = "4. Sessions — Hong Kong")]
        [XmlIgnore]
        public System.Windows.Media.Brush HongKongColor { get; set; }
        [Browsable(false)]
        public string HongKongColorSerializable { get => Serialize.BrushToString(HongKongColor); set => HongKongColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Hong Kong Opacity %", Order = 6, GroupName = "4. Sessions — Hong Kong")]
        public int HongKongOpacity { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 4. Sessions — Sydney

        [NinjaScriptProperty]
        [Display(Name = "Show Sydney Session", Order = 1, GroupName = "4. Sessions — Sydney")]
        public bool ShowSydney { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Sydney Label", Order = 2, GroupName = "4. Sessions — Sydney")]
        public string SydneyLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Sydney Show Label", Order = 3, GroupName = "4. Sessions — Sydney")]
        public bool SydneyShowLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Sydney Show Opening Range", Order = 4, GroupName = "4. Sessions — Sydney")]
        public bool SydneyShowOpeningRange { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Sydney Box Color", Order = 5, GroupName = "4. Sessions — Sydney")]
        [XmlIgnore]
        public System.Windows.Media.Brush SydneyColor { get; set; }
        [Browsable(false)]
        public string SydneyColorSerializable { get => Serialize.BrushToString(SydneyColor); set => SydneyColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Sydney Opacity %", Order = 6, GroupName = "4. Sessions — Sydney")]
        public int SydneyOpacity { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 4. Sessions — EU Brinks

        [NinjaScriptProperty]
        [Display(Name = "Show EU Brinks Session", Order = 1, GroupName = "4. Sessions — EU Brinks")]
        public bool ShowEuBrinks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EU Brinks Label", Order = 2, GroupName = "4. Sessions — EU Brinks")]
        public string EuBrinksLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EU Brinks Show Label", Order = 3, GroupName = "4. Sessions — EU Brinks")]
        public bool EuBrinksShowLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EU Brinks Show Opening Range", Order = 4, GroupName = "4. Sessions — EU Brinks")]
        public bool EuBrinksShowOpeningRange { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EU Brinks Box Color", Order = 5, GroupName = "4. Sessions — EU Brinks")]
        [XmlIgnore]
        public System.Windows.Media.Brush EuBrinksColor { get; set; }
        [Browsable(false)]
        public string EuBrinksColorSerializable { get => Serialize.BrushToString(EuBrinksColor); set => EuBrinksColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "EU Brinks Opacity %", Order = 6, GroupName = "4. Sessions — EU Brinks")]
        public int EuBrinksOpacity { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 4. Sessions — US Brinks

        [NinjaScriptProperty]
        [Display(Name = "Show US Brinks Session", Order = 1, GroupName = "4. Sessions — US Brinks")]
        public bool ShowUsBrinks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "US Brinks Label", Order = 2, GroupName = "4. Sessions — US Brinks")]
        public string UsBrinksLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "US Brinks Show Label", Order = 3, GroupName = "4. Sessions — US Brinks")]
        public bool UsBrinksShowLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "US Brinks Show Opening Range", Order = 4, GroupName = "4. Sessions — US Brinks")]
        public bool UsBrinksShowOpeningRange { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "US Brinks Box Color", Order = 5, GroupName = "4. Sessions — US Brinks")]
        [XmlIgnore]
        public System.Windows.Media.Brush UsBrinksColor { get; set; }
        [Browsable(false)]
        public string UsBrinksColorSerializable { get => Serialize.BrushToString(UsBrinksColor); set => UsBrinksColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "US Brinks Opacity %", Order = 6, GroupName = "4. Sessions — US Brinks")]
        public int UsBrinksOpacity { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 4. Sessions — Frankfurt

        [NinjaScriptProperty]
        [Display(Name = "Show Frankfurt Session", Order = 1, GroupName = "4. Sessions — Frankfurt")]
        public bool ShowFrankfurt { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Frankfurt Label", Order = 2, GroupName = "4. Sessions — Frankfurt")]
        public string FrankfurtLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Frankfurt Show Label", Order = 3, GroupName = "4. Sessions — Frankfurt")]
        public bool FrankfurtShowLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Frankfurt Show Opening Range", Order = 4, GroupName = "4. Sessions — Frankfurt")]
        public bool FrankfurtShowOpeningRange { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Frankfurt Box Color", Order = 5, GroupName = "4. Sessions — Frankfurt")]
        [XmlIgnore]
        public System.Windows.Media.Brush FrankfurtColor { get; set; }
        [Browsable(false)]
        public string FrankfurtColorSerializable { get => Serialize.BrushToString(FrankfurtColor); set => FrankfurtColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Frankfurt Opacity %", Order = 6, GroupName = "4. Sessions — Frankfurt")]
        public int FrankfurtOpacity { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 5. Range Levels — ADR

        [NinjaScriptProperty]
        [Display(Name = "Show ADR", Order = 1, GroupName = "5. Range Levels — ADR")]
        public bool ShowAdr { get; set; }

        [NinjaScriptProperty]
        [Range(1, 31)]
        [Display(Name = "ADR Length (days)", Order = 2, GroupName = "5. Range Levels — ADR")]
        public int AdrLength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ADR Use Daily Open", Order = 3, GroupName = "5. Range Levels — ADR")]
        public bool AdrUseDailyOpen { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show ADR 50%", Order = 4, GroupName = "5. Range Levels — ADR")]
        public bool ShowAdr50 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show ADR Labels", Order = 5, GroupName = "5. Range Levels — ADR")]
        public bool ShowAdrLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show ADR Range Label", Order = 6, GroupName = "5. Range Levels — ADR")]
        public bool ShowAdrRangeLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ADR Color", Order = 7, GroupName = "5. Range Levels — ADR")]
        [XmlIgnore]
        public System.Windows.Media.Brush AdrColor { get; set; }
        [Browsable(false)]
        public string AdrColorSerializable { get => Serialize.BrushToString(AdrColor); set => AdrColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "ADR Opacity %", Order = 8, GroupName = "5. Range Levels — ADR")]
        public int AdrOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ADR Line Style", Order = 9, GroupName = "5. Range Levels — ADR")]
        public IQMLineStyle AdrLineStyle { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 5. Range Levels — AWR

        [NinjaScriptProperty]
        [Display(Name = "Show AWR", Order = 1, GroupName = "5. Range Levels — AWR")]
        public bool ShowAwr { get; set; }

        [NinjaScriptProperty]
        [Range(1, 52)]
        [Display(Name = "AWR Length (weeks)", Order = 2, GroupName = "5. Range Levels — AWR")]
        public int AwrLength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show AWR 50%", Order = 3, GroupName = "5. Range Levels — AWR")]
        public bool ShowAwr50 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show AWR Labels", Order = 4, GroupName = "5. Range Levels — AWR")]
        public bool ShowAwrLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AWR Color", Order = 5, GroupName = "5. Range Levels — AWR")]
        [XmlIgnore]
        public System.Windows.Media.Brush AwrColor { get; set; }
        [Browsable(false)]
        public string AwrColorSerializable { get => Serialize.BrushToString(AwrColor); set => AwrColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "AWR Opacity %", Order = 6, GroupName = "5. Range Levels — AWR")]
        public int AwrOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AWR Line Style", Order = 7, GroupName = "5. Range Levels — AWR")]
        public IQMLineStyle AwrLineStyle { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 5. Range Levels — AMR

        [NinjaScriptProperty]
        [Display(Name = "Show AMR", Order = 1, GroupName = "5. Range Levels — AMR")]
        public bool ShowAmr { get; set; }

        [NinjaScriptProperty]
        [Range(1, 24)]
        [Display(Name = "AMR Length (months)", Order = 2, GroupName = "5. Range Levels — AMR")]
        public int AmrLength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show AMR 50%", Order = 3, GroupName = "5. Range Levels — AMR")]
        public bool ShowAmr50 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show AMR Labels", Order = 4, GroupName = "5. Range Levels — AMR")]
        public bool ShowAmrLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AMR Color", Order = 5, GroupName = "5. Range Levels — AMR")]
        [XmlIgnore]
        public System.Windows.Media.Brush AmrColor { get; set; }
        [Browsable(false)]
        public string AmrColorSerializable { get => Serialize.BrushToString(AmrColor); set => AmrColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "AMR Opacity %", Order = 6, GroupName = "5. Range Levels — AMR")]
        public int AmrOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AMR Line Style", Order = 7, GroupName = "5. Range Levels — AMR")]
        public IQMLineStyle AmrLineStyle { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 5. Range Levels — RD

        [NinjaScriptProperty]
        [Display(Name = "Show RD Hi/Lo", Order = 1, GroupName = "5. Range Levels — RD")]
        public bool ShowRd { get; set; }

        [NinjaScriptProperty]
        [Range(1, 31)]
        [Display(Name = "RD Length (days)", Order = 2, GroupName = "5. Range Levels — RD")]
        public int RdLength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show RD Labels", Order = 3, GroupName = "5. Range Levels — RD")]
        public bool ShowRdLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RD Color", Order = 4, GroupName = "5. Range Levels — RD")]
        [XmlIgnore]
        public System.Windows.Media.Brush RdColor { get; set; }
        [Browsable(false)]
        public string RdColorSerializable { get => Serialize.BrushToString(RdColor); set => RdColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "RD Opacity %", Order = 5, GroupName = "5. Range Levels — RD")]
        public int RdOpacity { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 5. Range Levels — RW

        [NinjaScriptProperty]
        [Display(Name = "Show RW Hi/Lo", Order = 1, GroupName = "5. Range Levels — RW")]
        public bool ShowRw { get; set; }

        [NinjaScriptProperty]
        [Range(1, 52)]
        [Display(Name = "RW Length (weeks)", Order = 2, GroupName = "5. Range Levels — RW")]
        public int RwLength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show RW Labels", Order = 3, GroupName = "5. Range Levels — RW")]
        public bool ShowRwLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RW Color", Order = 4, GroupName = "5. Range Levels — RW")]
        [XmlIgnore]
        public System.Windows.Media.Brush RwColor { get; set; }
        [Browsable(false)]
        public string RwColorSerializable { get => Serialize.BrushToString(RwColor); set => RwColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "RW Opacity %", Order = 5, GroupName = "5. Range Levels — RW")]
        public int RwOpacity { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 6. Daily/Weekly Levels

        [NinjaScriptProperty]
        [Display(Name = "Show Yesterday Hi/Lo", Order = 1, GroupName = "6. Daily/Weekly Levels")]
        public bool ShowYesterday { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Yesterday Labels", Order = 2, GroupName = "6. Daily/Weekly Levels")]
        public bool ShowYesterdayLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Yesterday Color", Order = 3, GroupName = "6. Daily/Weekly Levels")]
        [XmlIgnore]
        public System.Windows.Media.Brush YesterdayColor { get; set; }
        [Browsable(false)]
        public string YesterdayColorSerializable { get => Serialize.BrushToString(YesterdayColor); set => YesterdayColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "Show Last Week Hi/Lo", Order = 4, GroupName = "6. Daily/Weekly Levels")]
        public bool ShowLastWeek { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Last Week Labels", Order = 5, GroupName = "6. Daily/Weekly Levels")]
        public bool ShowLastWeekLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Last Week Color", Order = 6, GroupName = "6. Daily/Weekly Levels")]
        [XmlIgnore]
        public System.Windows.Media.Brush LastWeekColor { get; set; }
        [Browsable(false)]
        public string LastWeekColorSerializable { get => Serialize.BrushToString(LastWeekColor); set => LastWeekColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "Show Daily Open", Order = 7, GroupName = "6. Daily/Weekly Levels")]
        public bool ShowDailyOpen { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Historical Daily Opens", Order = 8, GroupName = "6. Daily/Weekly Levels")]
        public bool ShowHistoricalDailyOpens { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Daily Open Color", Order = 9, GroupName = "6. Daily/Weekly Levels")]
        [XmlIgnore]
        public System.Windows.Media.Brush DailyOpenColor { get; set; }
        [Browsable(false)]
        public string DailyOpenColorSerializable { get => Serialize.BrushToString(DailyOpenColor); set => DailyOpenColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "Daily Open Line Style", Order = 10, GroupName = "6. Daily/Weekly Levels")]
        public IQMLineStyle DailyOpenLineStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Daily Psy Levels", Order = 11, GroupName = "6. Daily/Weekly Levels")]
        public bool ShowDailyPsy { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Weekly Psy Levels", Order = 12, GroupName = "6. Daily/Weekly Levels")]
        public bool ShowWeeklyPsy { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "Psy Round Increment (ticks)", Order = 13, GroupName = "6. Daily/Weekly Levels")]
        public int PsyRoundIncrement { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Psy Use Crypto (Sydney) Start", Order = 14, GroupName = "6. Daily/Weekly Levels")]
        public bool PsyUseSydney { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Psy Labels", Order = 15, GroupName = "6. Daily/Weekly Levels")]
        public bool ShowPsyLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Psy Level Color", Order = 16, GroupName = "6. Daily/Weekly Levels")]
        [XmlIgnore]
        public System.Windows.Media.Brush PsyColor { get; set; }
        [Browsable(false)]
        public string PsyColorSerializable { get => Serialize.BrushToString(PsyColor); set => PsyColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Psy Opacity %", Order = 17, GroupName = "6. Daily/Weekly Levels")]
        public int PsyOpacity { get; set; }

        // ── ETH Daily Open (6 PM ET) ──────────────────────────────────────────
        [NinjaScriptProperty]
        [Display(Name = "Show ETH Daily Open (6PM ET)", Order = 18, GroupName = "6. Daily/Weekly Levels")]
        public bool ShowEthDailyOpen { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ETH Daily Open Color", Order = 19, GroupName = "6. Daily/Weekly Levels")]
        [XmlIgnore]
        public System.Windows.Media.Brush EthDailyOpenColor { get; set; }
        [Browsable(false)]
        public string EthDailyOpenColorSerializable { get => Serialize.BrushToString(EthDailyOpenColor); set => EthDailyOpenColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "ETH Daily Open Label", Order = 20, GroupName = "6. Daily/Weekly Levels")]
        public string EthDailyOpenLabel { get; set; }

        // ── Asia RTH Session Open ─────────────────────────────────────────────
        [NinjaScriptProperty]
        [Display(Name = "Show Asia Open (00:00–06:00 UTC)", Order = 21, GroupName = "6. Daily/Weekly Levels")]
        public bool ShowAsiaOpen { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Asia Open Color", Order = 22, GroupName = "6. Daily/Weekly Levels")]
        [XmlIgnore]
        public System.Windows.Media.Brush AsiaOpenColor { get; set; }
        [Browsable(false)]
        public string AsiaOpenColorSerializable { get => Serialize.BrushToString(AsiaOpenColor); set => AsiaOpenColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "Asia Open Label", Order = 23, GroupName = "6. Daily/Weekly Levels")]
        public string AsiaOpenLabel { get; set; }

        // ── London RTH Session Open ───────────────────────────────────────────
        [NinjaScriptProperty]
        [Display(Name = "Show London Open (UK DST)", Order = 24, GroupName = "6. Daily/Weekly Levels")]
        public bool ShowLondonOpen { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "London Open Color", Order = 25, GroupName = "6. Daily/Weekly Levels")]
        [XmlIgnore]
        public System.Windows.Media.Brush LondonOpenColor { get; set; }
        [Browsable(false)]
        public string LondonOpenColorSerializable { get => Serialize.BrushToString(LondonOpenColor); set => LondonOpenColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "London Open Label", Order = 26, GroupName = "6. Daily/Weekly Levels")]
        public string LondonOpenLabel { get; set; }

        // ── US RTH Session Open ───────────────────────────────────────────────
        [NinjaScriptProperty]
        [Display(Name = "Show US Open (US DST)", Order = 27, GroupName = "6. Daily/Weekly Levels")]
        public bool ShowUsOpen { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "US Open Color", Order = 28, GroupName = "6. Daily/Weekly Levels")]
        [XmlIgnore]
        public System.Windows.Media.Brush UsOpenColor { get; set; }
        [Browsable(false)]
        public string UsOpenColorSerializable { get => Serialize.BrushToString(UsOpenColor); set => UsOpenColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "US Open Label", Order = 29, GroupName = "6. Daily/Weekly Levels")]
        public string UsOpenLabel { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 7. PVSRA Vectors

        [NinjaScriptProperty]
        [Display(Name = "Enable PVSRA Vectors", Order = 1, GroupName = "7. PVSRA Vectors")]
        public bool EnablePVSRA { get; set; }

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "PVSRA Lookback Bars", Order = 2, GroupName = "7. PVSRA Vectors")]
        public int PVSRALookback { get; set; }

        [NinjaScriptProperty]
        [Range(100, 300)]
        [Display(Name = "High-Volume Threshold %", Order = 3, GroupName = "7. PVSRA Vectors")]
        public int HighVolumeThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(100, 200)]
        [Display(Name = "Mid-Volume Threshold %", Order = 4, GroupName = "7. PVSRA Vectors")]
        public int MidVolumeThreshold { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 8. Liquidity Zones

        [NinjaScriptProperty]
        [Display(Name = "Enable Liquidity Zones", Order = 1, GroupName = "8. Liquidity Zones")]
        public bool EnableLiquidityZones { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Include Wicks in Zone Boundary", Order = 2, GroupName = "8. Liquidity Zones")]
        public bool ULZIncludeWicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Zone Count on Dashboard", Order = 3, GroupName = "8. Liquidity Zones")]
        public bool ShowZoneCount { get; set; }

        [NinjaScriptProperty]
        [Range(10, 100)]
        [Display(Name = "Max Active Zones", Order = 4, GroupName = "8. Liquidity Zones")]
        public int MaxActiveZones { get; set; }

        [NinjaScriptProperty]
        [Range(5, 80)]
        [Display(Name = "Zone Opacity %", Order = 5, GroupName = "8. Liquidity Zones")]
        public int ZoneOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Bullish Zone Color", Order = 6, GroupName = "8. Liquidity Zones")]
        [XmlIgnore]
        public System.Windows.Media.Brush ULZBullishColor { get; set; }
        [Browsable(false)]
        public string ULZBullishColorSerializable { get { return Serialize.BrushToString(ULZBullishColor); } set { ULZBullishColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Display(Name = "Bearish Zone Color", Order = 7, GroupName = "8. Liquidity Zones")]
        [XmlIgnore]
        public System.Windows.Media.Brush ULZBearishColor { get; set; }
        [Browsable(false)]
        public string ULZBearishColorSerializable { get { return Serialize.BrushToString(ULZBearishColor); } set { ULZBearishColor = Serialize.StringToBrush(value); } }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 9. Volume Delta

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Delta Lookback Bars", Order = 1, GroupName = "9. Volume Delta")]
        public int DeltaLookback { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name = "Absorption Sensitivity %", Order = 2, GroupName = "9. Volume Delta")]
        public double AbsorptionSensitivity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Cumulative Delta", Order = 3, GroupName = "9. Volume Delta")]
        public bool ShowCumDelta { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 10. Fake Breakout

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Confirmation Bars Required", Order = 1, GroupName = "10. Fake Breakout")]
        public int ConfirmationBars { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 500.0)]
        [Display(Name = "Volume Follow-Through %", Order = 2, GroupName = "10. Fake Breakout")]
        public double VolumeFollowThrough { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Momentum RSI Period", Order = 3, GroupName = "10. Fake Breakout")]
        public int MomentumPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "S/R Swing Strength", Order = 4, GroupName = "10. Fake Breakout")]
        public int SRSwingStrength { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "Min Risk/Reward Ratio", Order = 5, GroupName = "10. Fake Breakout")]
        public double MinRiskReward { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 11. Order Book

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Wall Detection Multiplier", Order = 1, GroupName = "11. Order Book")]
        public int WallMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Anti-Spoofing Checks", Order = 2, GroupName = "11. Order Book")]
        public int SpoofingChecks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Wall Lines", Order = 3, GroupName = "11. Order Book")]
        public bool ShowWallLines { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 12. Signal Colors & Halo

        // ShowDashboard and DashPosition are legacy IQMainGPU properties not used in IQMainUltimate
        // (dashboards are controlled in group 16). Keep backing fields for XML serialization
        // compatibility but hide them from the settings UI.
        [NinjaScriptProperty]
        [Browsable(false)]
        public bool ShowDashboard { get; set; }

        [NinjaScriptProperty]
        [Browsable(false)]
        public IQMDashboardPosition DashPosition { get; set; }

        // DashFontSize / DashOpacity drive the Range Table and DST Table rendering — they
        // belong visually with the Tables group (13) but are kept here for serialization order.
        [NinjaScriptProperty]
        [Range(10, 50)]
        [Display(Name = "Table Font Size", Order = 10, GroupName = "13. Tables",
            Description = "Font size for Range and DST tables.")]
        public int DashFontSize { get; set; }

        [NinjaScriptProperty]
        [Range(20, 100)]
        [Display(Name = "Table Background Opacity %", Order = 11, GroupName = "13. Tables",
            Description = "Background opacity for Range and DST tables (20–100%).")]
        public int DashOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Halo on Signals", Order = 1, GroupName = "12. Signal Colors & Halo")]
        public bool ShowHalo { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Halo Layers", Order = 2, GroupName = "12. Signal Colors & Halo")]
        public int HaloLayers { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Bullish Color", Order = 3, GroupName = "12. Signal Colors & Halo")]
        [XmlIgnore]
        public System.Windows.Media.Brush BullishColor { get; set; }
        [Browsable(false)]
        public string BullishColorSerializable { get { return Serialize.BrushToString(BullishColor); } set { BullishColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Display(Name = "Bearish Color", Order = 4, GroupName = "12. Signal Colors & Halo")]
        [XmlIgnore]
        public System.Windows.Media.Brush BearishColor { get; set; }
        [Browsable(false)]
        public string BearishColorSerializable { get { return Serialize.BrushToString(BearishColor); } set { BearishColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Display(Name = "Absorption Color", Order = 5, GroupName = "12. Signal Colors & Halo")]
        [XmlIgnore]
        public System.Windows.Media.Brush AbsorptionColor { get; set; }
        [Browsable(false)]
        public string AbsorptionColorSerializable { get { return Serialize.BrushToString(AbsorptionColor); } set { AbsorptionColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Display(Name = "Imbalance Color", Order = 6, GroupName = "12. Signal Colors & Halo")]
        [XmlIgnore]
        public System.Windows.Media.Brush ImbalanceColor { get; set; }
        [Browsable(false)]
        public string ImbalanceColorSerializable { get { return Serialize.BrushToString(ImbalanceColor); } set { ImbalanceColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Display(Name = "Fake Breakout Color", Order = 7, GroupName = "12. Signal Colors & Halo")]
        [XmlIgnore]
        public System.Windows.Media.Brush FakeBreakoutColor { get; set; }
        [Browsable(false)]
        public string FakeBreakoutColorSerializable { get { return Serialize.BrushToString(FakeBreakoutColor); } set { FakeBreakoutColor = Serialize.StringToBrush(value); } }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 13. Tables

        [NinjaScriptProperty]
        [Display(Name = "Show Range Table", Order = 1, GroupName = "13. Tables")]
        public bool ShowRangeTable { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show ADR in Pips", Order = 2, GroupName = "13. Tables")]
        public bool TableShowAdrPips { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show ADR in Currency", Order = 3, GroupName = "13. Tables")]
        public bool TableShowAdrCurrency { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show RD Pips", Order = 4, GroupName = "13. Tables")]
        public bool TableShowRdPips { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Table Position", Order = 5, GroupName = "13. Tables")]
        public IQMDashboardPosition TablePosition { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Table Background Color", Order = 6, GroupName = "13. Tables")]
        [XmlIgnore]
        public System.Windows.Media.Brush TableBgColor { get; set; }
        [Browsable(false)]
        public string TableBgColorSerializable { get => Serialize.BrushToString(TableBgColor); set => TableBgColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "Table Text Color", Order = 7, GroupName = "13. Tables")]
        [XmlIgnore]
        public System.Windows.Media.Brush TableTextColor { get; set; }
        [Browsable(false)]
        public string TableTextColorSerializable { get => Serialize.BrushToString(TableTextColor); set => TableTextColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "Show DST Table", Order = 8, GroupName = "13. Tables")]
        public bool ShowDstTable { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "DST Table Position", Order = 9, GroupName = "13. Tables")]
        public IQMDashboardPosition DstTablePosition { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 14. VWAP

        [NinjaScriptProperty]
        [Display(Name = "Show VWAP", Order = 1, GroupName = "14. VWAP")]
        public bool ShowVwap { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "VWAP Session Anchor", Order = 2, GroupName = "14. VWAP",
            Description = "ETH = reset at 6 PM ET; RTH_US = reset at US market open (13:30 UTC with DST); Both = show both.")]
        public VwapSessionAnchor VwapAnchor { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "VWAP Line Thickness", Order = 3, GroupName = "14. VWAP")]
        public int VwapLineThickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "VWAP Line Style", Order = 4, GroupName = "14. VWAP")]
        public IQMLineStyle VwapLineStyle { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "VWAP Opacity %", Order = 5, GroupName = "14. VWAP")]
        public int VwapOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show VWAP Label", Order = 6, GroupName = "14. VWAP")]
        public bool ShowVwapLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ETH VWAP Label", Order = 7, GroupName = "14. VWAP")]
        public string VwapEthLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RTH VWAP Label", Order = 8, GroupName = "14. VWAP")]
        public string VwapRthLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Dynamic Color (price vs VWAP)", Order = 9, GroupName = "14. VWAP")]
        public bool VwapDynamicColor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "VWAP Color (Price Above)", Order = 10, GroupName = "14. VWAP")]
        [XmlIgnore]
        public System.Windows.Media.Brush VwapAboveColor { get; set; }
        [Browsable(false)]
        public string VwapAboveColorSerializable { get => Serialize.BrushToString(VwapAboveColor); set => VwapAboveColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "VWAP Color (Price Below)", Order = 11, GroupName = "14. VWAP")]
        [XmlIgnore]
        public System.Windows.Media.Brush VwapBelowColor { get; set; }
        [Browsable(false)]
        public string VwapBelowColorSerializable { get => Serialize.BrushToString(VwapBelowColor); set => VwapBelowColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "VWAP Color (Neutral/Default)", Order = 12, GroupName = "14. VWAP")]
        [XmlIgnore]
        public System.Windows.Media.Brush VwapNeutralColor { get; set; }
        [Browsable(false)]
        public string VwapNeutralColorSerializable { get => Serialize.BrushToString(VwapNeutralColor); set => VwapNeutralColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "Show ±1σ Bands", Order = 13, GroupName = "14. VWAP")]
        public bool ShowVwapBand1 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Band 1 Color", Order = 14, GroupName = "14. VWAP")]
        [XmlIgnore]
        public System.Windows.Media.Brush VwapBand1Color { get; set; }
        [Browsable(false)]
        public string VwapBand1ColorSerializable { get => Serialize.BrushToString(VwapBand1Color); set => VwapBand1Color = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Band 1 Opacity %", Order = 15, GroupName = "14. VWAP")]
        public int VwapBand1Opacity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3)]
        [Display(Name = "Band 1 Thickness", Order = 16, GroupName = "14. VWAP")]
        public int VwapBand1Thickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show ±2σ Bands", Order = 17, GroupName = "14. VWAP")]
        public bool ShowVwapBand2 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Band 2 Color", Order = 18, GroupName = "14. VWAP")]
        [XmlIgnore]
        public System.Windows.Media.Brush VwapBand2Color { get; set; }
        [Browsable(false)]
        public string VwapBand2ColorSerializable { get => Serialize.BrushToString(VwapBand2Color); set => VwapBand2Color = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Band 2 Opacity %", Order = 19, GroupName = "14. VWAP")]
        public int VwapBand2Opacity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3)]
        [Display(Name = "Band 2 Thickness", Order = 20, GroupName = "14. VWAP")]
        public int VwapBand2Thickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show ±3σ Bands", Order = 21, GroupName = "14. VWAP")]
        public bool ShowVwapBand3 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Band 3 Color", Order = 22, GroupName = "14. VWAP")]
        [XmlIgnore]
        public System.Windows.Media.Brush VwapBand3Color { get; set; }
        [Browsable(false)]
        public string VwapBand3ColorSerializable { get => Serialize.BrushToString(VwapBand3Color); set => VwapBand3Color = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Band 3 Opacity %", Order = 23, GroupName = "14. VWAP")]
        public int VwapBand3Opacity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3)]
        [Display(Name = "Band 3 Thickness", Order = 24, GroupName = "14. VWAP")]
        public int VwapBand3Thickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Fill Between Bands", Order = 25, GroupName = "14. VWAP")]
        public bool VwapFillBands { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Band Fill Opacity %", Order = 26, GroupName = "14. VWAP")]
        public int VwapFillOpacity { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 15. OTE Zones

        [NinjaScriptProperty]
        [Display(Name = "Show OTE Zones", Order = 1, GroupName = "15. OTE Zones",
            Description = "Enable GPU-rendered OTE (Optimal Trade Entry) zones based on ICT Fibonacci retracement levels.")]
        public bool ShowOTE { get; set; }

        [NinjaScriptProperty]
        [Range(3, 20)]
        [Display(Name = "Swing Detection Strength", Order = 2, GroupName = "15. OTE Zones",
            Description = "Number of bars on each side required to confirm a swing high or low.")]
        public int OTESwingStrength { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Max OTE Zones to Display", Order = 3, GroupName = "15. OTE Zones",
            Description = "Maximum number of OTE zones shown on the chart. Oldest zones are removed first.")]
        public int OTEMaxZones { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Bullish OTE Zone Color", Order = 4, GroupName = "15. OTE Zones")]
        [XmlIgnore]
        public System.Windows.Media.Brush OTEBullishColor { get; set; }
        [Browsable(false)]
        public string OTEBullishColorSerializable { get => Serialize.BrushToString(OTEBullishColor); set => OTEBullishColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "Bearish OTE Zone Color", Order = 5, GroupName = "15. OTE Zones")]
        [XmlIgnore]
        public System.Windows.Media.Brush OTEBearishColor { get; set; }
        [Browsable(false)]
        public string OTEBearishColorSerializable { get => Serialize.BrushToString(OTEBearishColor); set => OTEBearishColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "Zone Fill Opacity %", Order = 6, GroupName = "15. OTE Zones",
            Description = "Opacity of the shaded zone fill between the 62% and 79% levels.")]
        public int OTEZoneOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "OTE Level Line Color", Order = 7, GroupName = "15. OTE Zones",
            Description = "Color for the 62% and 79% boundary lines.")]
        [XmlIgnore]
        public System.Windows.Media.Brush OTELineColor { get; set; }
        [Browsable(false)]
        public string OTELineColorSerializable { get => Serialize.BrushToString(OTELineColor); set => OTELineColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "70.5% Optimal Line Color", Order = 8, GroupName = "15. OTE Zones",
            Description = "Color for the 70.5% optimal entry line.")]
        [XmlIgnore]
        public System.Windows.Media.Brush OTEOptimalColor { get; set; }
        [Browsable(false)]
        public string OTEOptimalColorSerializable { get => Serialize.BrushToString(OTEOptimalColor); set => OTEOptimalColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 3)]
        [Display(Name = "OTE Line Thickness", Order = 9, GroupName = "15. OTE Zones")]
        public int OTELineThickness { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3)]
        [Display(Name = "70.5% Line Thickness", Order = 10, GroupName = "15. OTE Zones")]
        public int OTEOptimalThickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "OTE Line Style", Order = 11, GroupName = "15. OTE Zones")]
        public IQMLineStyle OTELineStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show OTE Labels", Order = 12, GroupName = "15. OTE Zones")]
        public bool ShowOTELabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "OTE Label Prefix", Order = 13, GroupName = "15. OTE Zones")]
        public string OTELabelPrefix { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 16. Dashboards

        // ── Main Dashboard (original stats) ──────────────────────────────────────
        [NinjaScriptProperty]
        [Display(Name = "Show Main Dashboard", Order = 1, GroupName = "16. Dashboards",
            Description = "Show the Main Dashboard (original IQMainGPU stats: buy/sell volume, delta, signals, ADR, session).")]
        public bool ShowMainDashboard { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Main Dashboard Position", Order = 2, GroupName = "16. Dashboards",
            Description = "Position of the Main Dashboard. Hidden = disabled. CenterTop/CenterBottom = horizontally centred.")]
        public DashboardPositionType MainDashboardPosition { get; set; }

        [NinjaScriptProperty]
        [Range(10, 16)]
        [Display(Name = "Main Dashboard Font Size", Order = 3, GroupName = "16. Dashboards",
            Description = "Font size for Main Dashboard text (10-16pt).")]
        public int MainDashboardFontSize { get; set; }

        // ── Monitoring Dashboard (market health) ─────────────────────────────────
        [NinjaScriptProperty]
        [Display(Name = "Show Monitoring Dashboard", Order = 4, GroupName = "16. Dashboards",
            Description = "Show the Monitoring Dashboard (range, session, volume, VWAP vs EMA50, liquidity zones, conflict warnings).")]
        public bool ShowMonitoringDashboard { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Monitoring Dashboard Position", Order = 5, GroupName = "16. Dashboards",
            Description = "Position of the Monitoring Dashboard (independent of other dashboards). Hidden = disabled.")]
        public DashboardPositionType MonitoringDashboardPosition { get; set; }

        [NinjaScriptProperty]
        [Range(10, 16)]
        [Display(Name = "Monitoring Dashboard Font Size", Order = 6, GroupName = "16. Dashboards",
            Description = "Font size for Monitoring Dashboard text (10-16pt).")]
        public int MonitoringDashboardFontSize { get; set; }

        // ── Entry Mode Dashboard (trade setup) ───────────────────────────────────
        [NinjaScriptProperty]
        [Display(Name = "Show Entry Mode Dashboard", Order = 7, GroupName = "16. Dashboards",
            Description = "Show the Entry Mode Dashboard (signal, confidence, entry/stop/target, R/R ratio).")]
        public bool ShowEntryModeDashboard { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Entry Mode Dashboard Position", Order = 8, GroupName = "16. Dashboards",
            Description = "Position of the Entry Mode Dashboard (independent of other dashboards). Hidden = disabled.")]
        public DashboardPositionType EntryModeDashboardPosition { get; set; }

        [NinjaScriptProperty]
        [Range(12, 20)]
        [Display(Name = "Entry Mode Dashboard Font Size", Order = 9, GroupName = "16. Dashboards",
            Description = "Font size for Entry Mode Dashboard text (12-20pt).")]
        public int EntryModeDashboardFontSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Stop/Target Lines", Order = 10, GroupName = "16. Dashboards",
            Description = "Draw horizontal lines on chart: white solid = entry, red dashed = stop, green dashed = target (Entry Mode only).")]
        public bool ShowStopTargetLines { get; set; }

        // ── Shared Dashboard Settings ─────────────────────────────────────────────
        [NinjaScriptProperty]
        [Display(Name = "Show Conflict Warnings", Order = 11, GroupName = "16. Dashboards",
            Description = "Show warnings when volume direction conflicts with price, fake breakouts are detected, or session participation is low.")]
        public bool ShowConflictWarnings { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Conflict Description Level", Order = 12, GroupName = "16. Dashboards",
            Description = "Brief = '⚠ Conflict detected'. Detailed = describes the conflict. VeryDetailed = includes severity level [CRITICAL/HIGH/MODERATE].")]
        public ConflictDescriptionLevel ConflictLevel { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Dashboard Opacity %", Order = 13, GroupName = "16. Dashboards",
            Description = "Background opacity for all three dashboards (1-100%).")]
        public int DashboardOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stop Placement Mode", Order = 14, GroupName = "16. Dashboards",
            Description = "AutoDetected = liquidity zones → VAL → pivot S1 → swings. PivotBased = always pivot S1. HVNBased = highest SR level below price. ManualInput = Stop Distance ticks. TPOBased = always use current session VAL.")]
        public UltimateStopMode StopMode { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Stop Distance (ticks)", Order = 15, GroupName = "16. Dashboards",
            Description = "Stop distance in ticks used when StopMode = ManualInput (or as fallback).")]
        public int StopDistanceTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Target Placement Mode", Order = 16, GroupName = "16. Dashboards",
            Description = "AutoDetected = VAH → pivot R1/R2. PivotR1 = always R1. PivotR2 = always R2. ManualInput = Target Distance ticks. VAH = current session VAH. IBExtension = IB High Extension.")]
        public UltimateTargetMode TargetMode { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Target Distance (ticks)", Order = 17, GroupName = "16. Dashboards",
            Description = "Target distance in ticks used when TargetMode = ManualInput (or as fallback).")]
        public int TargetDistanceTicks { get; set; }

        // ── Signal Expiry Settings ────────────────────────────────────────────
        [NinjaScriptProperty]
        [Range(1, 240)]
        [Display(Name = "Signal Stale (minutes)", Order = 18, GroupName = "16. Dashboards",
            Description = "Minutes after which an unchanged signal is considered stale and a warning is shown on the Entry Mode Dashboard. Must be less than Signal Expire Minutes.")]
        public int SignalStaleMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 480)]
        [Display(Name = "Signal Expire (minutes)", Order = 19, GroupName = "16. Dashboards",
            Description = "Minutes after which a stale signal is expired entirely and the dashboard resets to 'No Active Signal / Waiting for signal detection...'.")]
        public int SignalExpireMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Low-Participation Warning", Order = 20, GroupName = "16. Dashboards",
            Description = "When ON, shows a conflict warning during low-participation hours (outside London/NY/EU Brinks/US Brinks). Turn OFF to silence off-hours noise.")]
        public bool ShowLowParticipationWarning { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Gate Display by Min R:R", Order = 21, GroupName = "16. Dashboards",
            Description = "When ON, the Entry Mode dashboard greys out and the on-chart entry/stop/target lines are suppressed when the computed R:R is below MinRiskReward. Helps skip low-quality setups visually.")]
        public bool MinRiskRewardDisplay { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 17. TPO Settings

        [NinjaScriptProperty]
        [Display(Name = "Show POC Lines", Order = 1, GroupName = "17. TPO Settings",
            Description = "Display the Point of Control (highest-volume price) as a gold horizontal line extending from session start.")]
        public bool ShowPOC { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "POC Line Color", Order = 2, GroupName = "17. TPO Settings")]
        [XmlIgnore]
        public System.Windows.Media.Brush POCColor { get; set; }
        [Browsable(false)]
        public string POCColorSerializable { get => Serialize.BrushToString(POCColor); set => POCColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "Show Value Area", Order = 3, GroupName = "17. TPO Settings",
            Description = "Shade the 70% Value Area and draw VAH/VAL as cyan dashed lines.")]
        public bool ShowValueArea { get; set; }

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "Value Area Fill Opacity %", Order = 4, GroupName = "17. TPO Settings",
            Description = "Opacity of the semi-transparent blue Value Area fill rectangle (5-50%).")]
        public int ValueAreaOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Initial Balance", Order = 5, GroupName = "17. TPO Settings",
            Description = "Draw a vertical bracket at session start showing the first 60-minute high/low range.")]
        public bool ShowInitialBalance { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show IB Extensions", Order = 6, GroupName = "17. TPO Settings",
            Description = "Draw dotted lines at IBHigh + IBRange × 1.0 and IBLow - IBRange × 1.0 as extension targets.")]
        public bool ShowIBExtensions { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Naked TPO Levels", Order = 7, GroupName = "17. TPO Settings",
            Description = "Display unvisited POC/VAH/VAL from prior sessions as forward-projecting orange dashed lines.")]
        public bool ShowNakedLevels { get; set; }

        [NinjaScriptProperty]
        [Range(3, 20)]
        [Display(Name = "Max Naked Levels to Track", Order = 8, GroupName = "17. TPO Settings",
            Description = "Maximum number of naked TPO levels to keep active simultaneously (3-20).")]
        public int MaxNakedLevels { get; set; }

        [NinjaScriptProperty]
        [Range(3, 30)]
        [Display(Name = "Naked Level Age (days)", Order = 9, GroupName = "17. TPO Settings",
            Description = "Auto-remove naked levels older than this many calendar days (3-30).")]
        public int NakedLevelMaxAgeDays { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Profile Shape Label", Order = 10, GroupName = "17. TPO Settings",
            Description = "Display the session profile shape (Normal / TrendDay / DoubleDistribution / Balanced) in the Monitoring Dashboard.")]
        public bool ShowProfileShape { get; set; }

        [NinjaScriptProperty]
        [Range(10, 2000)]
        [Display(Name = "Max TPO Stop Distance (ticks)", Order = 11, GroupName = "17. TPO Settings",
            Description = "Hard cap on TPO-derived stop distance from price. When a TPO stop level exceeds this, the indicator falls back to the ADR-based stop. Increase for small-tick instruments (CL, bonds).")]
        public int MaxTPOStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(10, 4000)]
        [Display(Name = "Max TPO Target Distance (ticks)", Order = 12, GroupName = "17. TPO Settings",
            Description = "Hard cap on TPO-derived target distance from price. When a TPO target level exceeds this, the indicator falls back to the ADR-based target. Increase for small-tick instruments.")]
        public int MaxTPOTargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "TPO Bin Size Multiplier", Order = 13, GroupName = "17. TPO Settings",
            Description = "Multiplier applied to TickSize when bucketing TPO price distribution. 1 = native tick resolution. Increase for small-tick instruments (CL, bonds) to reduce memory and speed up POC/VA computation. Higher values produce coarser profiles.")]
        public int TPOBinSizeMultiplier { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "IQMainUltimate — Standalone ultimate indicator. All IQMainGPU_Enhanced features plus full TPO/Market Profile integration (POC, Value Area, Initial Balance, Naked Levels, Profile Shape).";
                Name                     = "IQMainUltimate";
                Calculate                = Calculate.OnEachTick;
                IsOverlay                = true;
                IsAutoScale              = false;
                DisplayInDataBox         = true;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = false;
                MaximumBarsLookBack      = MaximumBarsLookBack.TwoHundredFiftySix;

                // 1. Core
                AssetClass   = IQMAssetClass.Futures;
                ColorMode    = IQMCandleColorMode.VolumeDelta;
                EnableLevel2 = false;

                // 2. EMAs
                LabelOffsetBars  = 2;
                LabelOffsetTicks = 1;
                ShowEma5         = true;
                var ema5Brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(254, 234, 74));
                ema5Brush.Freeze();
                Ema5Color        = ema5Brush;
                Ema5Thickness    = 1;
                ShowEma13        = true;
                var ema13Brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(253, 84, 87));
                ema13Brush.Freeze();
                Ema13Color       = ema13Brush;
                Ema13Thickness   = 1;
                ShowEma50        = true;
                var ema50Brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(31, 188, 211));
                ema50Brush.Freeze();
                Ema50Color       = ema50Brush;
                Ema50Thickness   = 2;
                ShowEma50Cloud   = true;
                var cloudBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(155, 47, 174));
                cloudBrush.Freeze();
                CloudFillColor   = cloudBrush;
                CloudFillOpacity = 24;
                ShowEma200       = true;
                Ema200Color      = Brushes.White;
                Ema200Thickness  = 2;
                ShowEma800       = true;
                var ema800Brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(50, 34, 144));
                ema800Brush.Freeze();
                Ema800Color      = ema800Brush;
                Ema800Thickness  = 2;
                ShowEmaLabels    = true;

                // EMA plots: 5 lines + 2 transparent cloud band series
                AddPlot(new Stroke(Ema5Color,           Ema5Thickness),   PlotStyle.Line, "EMA5");
                AddPlot(new Stroke(Ema13Color,          Ema13Thickness),  PlotStyle.Line, "EMA13");
                AddPlot(new Stroke(Ema50Color,          Ema50Thickness),  PlotStyle.Line, "EMA50");
                AddPlot(new Stroke(Ema200Color,         Ema200Thickness), PlotStyle.Line, "EMA200");
                AddPlot(new Stroke(Ema800Color,         Ema800Thickness), PlotStyle.Line, "EMA800");
                AddPlot(new Stroke(Brushes.Transparent, 1),               PlotStyle.Line, "CloudUpper");
                AddPlot(new Stroke(Brushes.Transparent, 1),               PlotStyle.Line, "CloudLower");

                // 3. Pivot Points
                ShowPP           = true;
                ShowLevel1       = true;
                ShowLevel2       = true;
                ShowLevel3       = false;
                ShowPivotLabels  = true;
                PivotLineStyle   = IQMLineStyle.Dashed;
                PPColor          = Brushes.Yellow;
                RLevelColor      = Brushes.LimeGreen;
                SLevelColor      = Brushes.IndianRed;
                ShowMLevels      = true;
                ShowMLabels      = true;
                MLevelColor      = Brushes.White;
                MLevelOpacity    = 50;
                MLevelLineStyle  = IQMLineStyle.Dotted;

                // 4. Sessions — London
                ShowLondon             = true;
                LondonLabel            = "London";
                LondonShowLabel        = true;
                LondonShowOpeningRange = true;
                LondonColor            = System.Windows.Media.Brushes.SteelBlue;
                LondonOpacity          = 15;

                // 4. Sessions — New York
                ShowNewYork             = true;
                NewYorkLabel            = "New York";
                NewYorkShowLabel        = true;
                NewYorkShowOpeningRange = true;
                NewYorkColor            = System.Windows.Media.Brushes.ForestGreen;
                NewYorkOpacity          = 15;

                // 4. Sessions — Tokyo
                ShowTokyo             = true;
                TokyoLabel            = "Tokyo";
                TokyoShowLabel        = true;
                TokyoShowOpeningRange = true;
                TokyoColor            = System.Windows.Media.Brushes.Crimson;
                TokyoOpacity          = 15;

                // 4. Sessions — Hong Kong
                ShowHongKong             = true;
                HongKongLabel            = "Hong Kong";
                HongKongShowLabel        = true;
                HongKongShowOpeningRange = true;
                HongKongColor            = System.Windows.Media.Brushes.Orange;
                HongKongOpacity          = 15;

                // 4. Sessions — Sydney
                ShowSydney             = true;
                SydneyLabel            = "Sydney";
                SydneyShowLabel        = true;
                SydneyShowOpeningRange = true;
                SydneyColor            = System.Windows.Media.Brushes.DarkViolet;
                SydneyOpacity          = 15;

                // 4. Sessions — EU Brinks
                ShowEuBrinks             = true;
                EuBrinksLabel            = "EU Brinks";
                EuBrinksShowLabel        = true;
                EuBrinksShowOpeningRange = true;
                EuBrinksColor            = System.Windows.Media.Brushes.DeepSkyBlue;
                EuBrinksOpacity          = 20;

                // 4. Sessions — US Brinks
                ShowUsBrinks             = true;
                UsBrinksLabel            = "US Brinks";
                UsBrinksShowLabel        = true;
                UsBrinksShowOpeningRange = true;
                UsBrinksColor            = System.Windows.Media.Brushes.LimeGreen;
                UsBrinksOpacity          = 20;

                // 4. Sessions — Frankfurt
                ShowFrankfurt             = true;
                FrankfurtLabel            = "Frankfurt";
                FrankfurtShowLabel        = true;
                FrankfurtShowOpeningRange = true;
                FrankfurtColor            = System.Windows.Media.Brushes.Gold;
                FrankfurtOpacity          = 12;

                // 5. ADR
                ShowAdr          = true;
                AdrLength        = 14;
                AdrUseDailyOpen  = false;
                ShowAdr50        = true;
                ShowAdrLabels    = true;
                ShowAdrRangeLabel= true;
                AdrColor         = Brushes.DodgerBlue;
                AdrOpacity       = 70;
                AdrLineStyle     = IQMLineStyle.Dashed;

                // 5. AWR
                ShowAwr          = true;
                AwrLength        = 4;
                ShowAwr50        = true;
                ShowAwrLabels    = true;
                AwrColor         = Brushes.Orange;
                AwrOpacity       = 50;
                AwrLineStyle     = IQMLineStyle.Dashed;

                // 5. AMR
                ShowAmr          = true;
                AmrLength        = 6;
                ShowAmr50        = true;
                ShowAmrLabels    = true;
                AmrColor         = Brushes.IndianRed;
                AmrOpacity       = 50;
                AmrLineStyle     = IQMLineStyle.Dashed;

                // 5. RD
                ShowRd           = true;
                RdLength         = 15;
                ShowRdLabels     = true;
                RdColor          = Brushes.Crimson;
                RdOpacity        = 30;

                // 5. RW
                ShowRw           = true;
                RwLength         = 13;
                ShowRwLabels     = true;
                RwColor          = Brushes.SteelBlue;
                RwOpacity        = 30;

                // 6. Daily/Weekly Levels
                ShowYesterday            = true;
                ShowYesterdayLabels      = true;
                YesterdayColor           = Brushes.CornflowerBlue;
                ShowLastWeek             = true;
                ShowLastWeekLabels       = true;
                LastWeekColor            = Brushes.MediumSeaGreen;
                ShowDailyOpen            = true;
                ShowHistoricalDailyOpens = false;
                var dailyOpenBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(254, 234, 78));
                dailyOpenBrush.Freeze();
                DailyOpenColor           = dailyOpenBrush;
                DailyOpenLineStyle       = IQMLineStyle.Solid;
                ShowDailyPsy             = true;
                ShowWeeklyPsy            = true;
                PsyRoundIncrement        = 50;
                PsyUseSydney             = false;
                ShowPsyLabels            = true;
                PsyColor                 = Brushes.Orange;
                PsyOpacity               = 30;

                // 6. ETH Daily Open
                ShowEthDailyOpen = true;
                var ethOpenBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 200, 50));
                ethOpenBrush.Freeze();
                EthDailyOpenColor = ethOpenBrush;
                EthDailyOpenLabel = "ETH Open";

                // 6. Asia RTH Open
                ShowAsiaOpen  = true;
                AsiaOpenColor = System.Windows.Media.Brushes.Crimson;
                AsiaOpenLabel = "Asia Open";

                // 6. London RTH Open
                ShowLondonOpen  = true;
                LondonOpenColor = System.Windows.Media.Brushes.SteelBlue;
                LondonOpenLabel = "LDN Open";

                // 6. US RTH Open
                ShowUsOpen  = true;
                UsOpenColor = System.Windows.Media.Brushes.ForestGreen;
                UsOpenLabel = "US Open";

                // 7. PVSRA Vectors
                EnablePVSRA          = true;
                PVSRALookback        = 10;
                HighVolumeThreshold  = 200;
                MidVolumeThreshold   = 150;

                // 8. Liquidity Zones
                EnableLiquidityZones = true;
                ULZIncludeWicks      = false;
                ShowZoneCount        = true;
                MaxActiveZones       = 100;
                ZoneOpacity          = 30;
                ULZBullishColor      = Brushes.LimeGreen;
                ULZBearishColor      = Brushes.IndianRed;

                // 9. Volume Delta
                DeltaLookback         = 100;
                AbsorptionSensitivity = 60.0;
                ShowCumDelta          = true;

                // 10. Fake Breakout
                ConfirmationBars    = 2;
                VolumeFollowThrough = 120.0;
                MomentumPeriod      = 14;
                SRSwingStrength     = 5;
                MinRiskReward       = 2.0;

                // 11. Order Book
                WallMultiplier = 5;
                SpoofingChecks = 3;
                ShowWallLines  = true;

                // 12. Dashboard
                ShowDashboard     = true;
                DashPosition      = IQMDashboardPosition.TopRight;
                DashFontSize      = 11;
                DashOpacity       = 80;
                ShowHalo          = true;
                HaloLayers        = 5;
                BullishColor      = Brushes.LimeGreen;
                BearishColor      = Brushes.Crimson;
                AbsorptionColor   = Brushes.Gold;
                ImbalanceColor    = Brushes.DodgerBlue;
                FakeBreakoutColor = Brushes.OrangeRed;

                // 13. Tables
                ShowRangeTable       = true;
                TableShowAdrPips     = true;
                TableShowAdrCurrency = false;
                TableShowRdPips      = true;
                TablePosition        = IQMDashboardPosition.TopRight;
                TableBgColor         = Brushes.Black;
                TableTextColor       = Brushes.White;
                ShowDstTable         = false;
                DstTablePosition     = IQMDashboardPosition.BottomLeft;

                // 14. VWAP
                ShowVwap          = true;
                VwapAnchor        = VwapSessionAnchor.ETH;
                VwapLineThickness = 2;
                VwapLineStyle     = IQMLineStyle.Solid;
                VwapOpacity       = 90;
                ShowVwapLabel     = true;
                VwapEthLabel      = "ETH VWAP";
                VwapRthLabel      = "RTH VWAP";
                VwapDynamicColor  = true;
                VwapAboveColor    = Brushes.LimeGreen;
                VwapBelowColor    = Brushes.Crimson;
                VwapNeutralColor  = Brushes.Yellow;
                ShowVwapBand1     = true;
                VwapBand1Color    = Brushes.DodgerBlue;
                VwapBand1Opacity  = 60;
                VwapBand1Thickness = 1;
                ShowVwapBand2     = true;
                VwapBand2Color    = Brushes.Orange;
                VwapBand2Opacity  = 50;
                VwapBand2Thickness = 1;
                ShowVwapBand3     = false;
                VwapBand3Color    = Brushes.Purple;
                VwapBand3Opacity  = 40;
                VwapBand3Thickness = 1;
                VwapFillBands     = false;
                VwapFillOpacity   = 15;

                // 15. OTE Zones
                ShowOTE            = false;
                OTESwingStrength   = 5;
                OTEMaxZones        = 3;
                OTEBullishColor    = System.Windows.Media.Brushes.DodgerBlue;
                OTEBearishColor    = System.Windows.Media.Brushes.Crimson;
                OTEZoneOpacity     = 15;
                OTELineColor       = System.Windows.Media.Brushes.DodgerBlue;
                OTEOptimalColor    = System.Windows.Media.Brushes.Gold;
                OTELineThickness   = 1;
                OTEOptimalThickness = 2;
                OTELineStyle       = IQMLineStyle.Dashed;
                ShowOTELabels      = true;
                OTELabelPrefix     = "OTE";

                // 16. Dashboards — Main Dashboard (original stats)
                ShowMainDashboard          = true;
                MainDashboardPosition      = DashboardPositionType.TopLeft;
                MainDashboardFontSize      = 11;

                // 16. Dashboards — Monitoring Dashboard (market health)
                ShowMonitoringDashboard    = true;
                MonitoringDashboardPosition = DashboardPositionType.BottomLeft;
                MonitoringDashboardFontSize = 11;

                // 16. Dashboards — Entry Mode Dashboard (trade setup)
                ShowEntryModeDashboard     = true;
                EntryModeDashboardPosition = DashboardPositionType.BottomRight;
                EntryModeDashboardFontSize = 14;
                ShowStopTargetLines        = true;

                // 16. Dashboards — Shared settings
                ShowConflictWarnings   = true;
                ConflictLevel          = ConflictDescriptionLevel.Detailed;
                DashboardOpacity       = 80;
                StopMode               = UltimateStopMode.AutoDetected;
                StopDistanceTicks      = 10;
                TargetMode             = UltimateTargetMode.AutoDetected;
                TargetDistanceTicks    = 20;
                SignalStaleMinutes     = 15;
                SignalExpireMinutes    = 30;
                ShowLowParticipationWarning = true;
                MinRiskRewardDisplay        = true;

                // 17. TPO Settings
                ShowPOC              = true;
                POCColor             = Brushes.Gold;
                ShowValueArea        = true;
                ValueAreaOpacity     = 20;
                ShowInitialBalance   = true;
                ShowIBExtensions     = true;
                ShowNakedLevels      = true;
                MaxNakedLevels       = 10;
                NakedLevelMaxAgeDays = 10;
                ShowProfileShape     = true;
                MaxTPOStopTicks      = 200;
                MaxTPOTargetTicks    = 400;
                TPOBinSizeMultiplier = 1;
            }
            else if (State == State.Configure)
            {
                // Use Infinite lookback for higher timeframes (1hr+) so long-period EMAs (50, 200, 800)
                // have enough historical bars to calculate. Lower timeframes keep TwoHundredFiftySix for
                // better memory efficiency.
                if (BarsPeriod != null &&
                    BarsPeriod.BarsPeriodType == BarsPeriodType.Minute &&
                    BarsPeriod.Value >= 60)
                {
                    MaximumBarsLookBack = MaximumBarsLookBack.Infinite;
                }
                else if (BarsPeriod != null &&
                         (BarsPeriod.BarsPeriodType == BarsPeriodType.Day   ||
                          BarsPeriod.BarsPeriodType == BarsPeriodType.Week  ||
                          BarsPeriod.BarsPeriodType == BarsPeriodType.Month))
                {
                    MaximumBarsLookBack = MaximumBarsLookBack.Infinite;
                }
                if (!ShowLondon && !ShowNewYork)
                    Print("[IQMainUltimate] Both London and New York sessions are disabled — TPO previous-day reference levels will not update. Enable at least one to use TPO-based stops/targets.");
            }
            else if (State == State.DataLoaded)
            {
                // Sessions / pivot / range collections
                dailyRanges   = new Queue<double>(32);
                weeklyRanges  = new Queue<double>(56);
                monthlyRanges = new Queue<double>(26);
                rdRanges      = new Queue<double>(32);
                rwRanges      = new Queue<double>(56);

                sessionBoxes   = new List<SessionBox>(200);
                activeSessions = new SessionBox[8];

                prevDayLoaded    = false;
                weekDataLoaded   = false;
                currentDayOfWeek = -1;
                monthDataLoaded  = false;
                currentMonth     = -1;
                dailyOpenSet     = false;

                alertAdrHighFired = alertAdrLowFired = false;
                alertAwrHighFired = alertAwrLowFired = false;
                alertAmrHighFired = alertAmrLowFired = false;

                dailyOpenEntries      = new List<DailyOpenEntry>(200);
                currentDailyOpenEntry = null;

                ethOpenEntries        = new List<SessionOpenEntry>(200);
                currentEthOpenEntry   = null;
                rthAsiaOpenEntries    = new List<SessionOpenEntry>(200);
                currentRthAsiaEntry   = null;
                rthLondonOpenEntries  = new List<SessionOpenEntry>(200);
                currentRthLondonEntry = null;
                rthUsOpenEntries      = new List<SessionOpenEntry>(200);
                currentRthUsEntry     = null;

                // VWAP data lists
                vwapEthData              = new List<VwapBarData>(1000);
                vwapEthCumulativePV      = 0;
                vwapEthCumulativeVolume  = 0;
                vwapEthCumulativeTPVSq   = 0;
                vwapEthSessionStart      = DateTime.MinValue;

                vwapRthData              = new List<VwapBarData>(1000);
                vwapRthCumulativePV      = 0;
                vwapRthCumulativeVolume  = 0;
                vwapRthCumulativeTPVSq   = 0;
                vwapRthSessionStart      = DateTime.MinValue;

                // Candle / microstructure collections
                snapshots      = new List<BarSnapshot>(1000);
                deltaHistory   = new List<double>(1000);
                srLevels       = new List<double>(50);
                bidBook        = new Dictionary<double, BookLevel>(200);
                askBook        = new Dictionary<double, BookLevel>(200);
                liquidityZones = new List<LiquidityZone>(100);

                // OTE zone data
                oteZones           = new List<OTEZone>(20);
                oteLastSwingHigh   = double.NaN;
                oteLastSwingLow    = double.NaN;
                oteLastSwingHighBar = 0;
                oteLastSwingLowBar  = 0;

                // TPO / Market Profile collections
                tpoSessions        = new List<TPOSession>(100);
                activeTPOSessions  = new TPOSession[8];
                nakedTPOLevels     = new List<NakedTPOLevel>(MaxNakedLevels + 5);
                previousDayTPO     = null;
                tpoCurrentPOC = tpoCurrentVAH = tpoCurrentVAL = 0;
                tpoCurrentIBHigh = tpoCurrentIBLow = 0;
                tpoCurrentIBHighExt = tpoCurrentIBLowExt = 0;
                tpoCurrentShape = TPOProfileShape.Normal;
                tpoCurrentSessionLabel = "";
                tpoNearestNakedLevel   = null;

                cumDelta       = 0;
                sessionBuyVol  = 0;
                sessionSellVol = 0;
                prevTickPrice  = 0;
                prevTickVol    = 0;
                imbalanceLow   = 0;
                imbalanceHigh  = double.MaxValue;
                level2Available = false;
                l2StatusText   = EnableLevel2 ? "L2: waiting…" : "L2: disabled";
                activeZoneCount    = 0;
                recoveredZoneCount = 0;

                // Cache EMA/StdDev indicators
                ema5Ind      = EMA(5);
                ema13Ind     = EMA(13);
                ema50Ind     = EMA(50);
                ema200Ind    = EMA(200);
                ema800Ind    = EMA(800);
                stdDev100Ind = StdDev(Close, 100);

                Plots[0].Brush = Ema5Color;   Plots[0].Width = Ema5Thickness;
                Plots[1].Brush = Ema13Color;  Plots[1].Width = Ema13Thickness;
                Plots[2].Brush = Ema50Color;  Plots[2].Width = Ema50Thickness;
                Plots[3].Brush = Ema200Color; Plots[3].Width = Ema200Thickness;
                Plots[4].Brush = Ema800Color; Plots[4].Width = Ema800Thickness;

                labelFont = new NinjaTrader.Gui.Tools.SimpleFont("Consolas", 12);

                if (Bars != null)
                {
                    Print(string.Format("=== {0} session anchors (ET, year-round) ===", Name));
                    Print("  ETH Daily Open:  18:00 ET (prev trading day)");
                    Print("  Asia Open:       19:00 ET prev → 04:00 ET (Tokyo)");
                    Print("  London Open:     03:00 ET → 11:30 ET");
                    Print("  US Open:         09:30 ET → 16:00 ET");
                    Print(string.Format("  TradingHours TZ: {0}", Bars.TradingHours != null && Bars.TradingHours.TimeZoneInfo != null ? Bars.TradingHours.TimeZoneInfo.Id : "(unknown)"));
                    Print(string.Format("  ET zone resolved: {0}", EtZone != null ? EtZone.Id : "(null)"));
                    Print("=== end session anchors ===");
                }
            }
            else if (State == State.Terminated)
            {
                DisposeDXResources();
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 1)
                return;

            // Cache latest close and indicator values so OnRender always reads the freshest tick
            _latestClose = Close[0];
            Values[0][0] = (ShowEma5   && CurrentBar >= 5)   ? ema5Ind[0]   : double.NaN;
            Values[1][0] = (ShowEma13  && CurrentBar >= 13)  ? ema13Ind[0]  : double.NaN;
            Values[2][0] = (ShowEma50  && CurrentBar >= 50)  ? ema50Ind[0]  : double.NaN;
            Values[3][0] = (ShowEma200 && CurrentBar >= 200) ? ema200Ind[0] : double.NaN;
            Values[4][0] = (ShowEma800 && CurrentBar >= 800) ? ema800Ind[0] : double.NaN;

            if (CurrentBar >= 100)
            {
                double ema50val = ema50Ind[0];
                double stdDev   = stdDev100Ind[0] / 4.0;
                Values[5][0]    = ema50val + stdDev;
                Values[6][0]    = ema50val - stdDev;
            }
            else
            {
                Values[5][0] = double.NaN;
                Values[6][0] = double.NaN;
            }

            if (ShowEma50Cloud && CurrentBar >= 100)
            {
                int   barsBack     = Math.Max(0, Math.Min(CurrentBar - 100, 254));
                Brush outlineBrush = Brushes.Transparent;
                Draw.Region(this, "Ema50Cloud", 0, barsBack,
                    Values[5], Values[6], outlineBrush, CloudFillColor, CloudFillOpacity);
            }
            else
            {
                RemoveDrawObject("Ema50Cloud");
            }

            // EMA labels at rightmost bar
            if (CurrentBar == Count - 2 && ShowEmaLabels)
            {
                if (ShowEma5 && CurrentBar >= 5)
                    Draw.Text(this, "Ema5Label", false, "5", 0, ema5Ind[0], 0, Ema5Color,
                        labelFont, System.Windows.TextAlignment.Left,
                        Brushes.Transparent, Brushes.Transparent, 0);
                else RemoveDrawObject("Ema5Label");

                if (ShowEma13 && CurrentBar >= 13)
                    Draw.Text(this, "Ema13Label", false, "13", 0, ema13Ind[0], 0, Ema13Color,
                        labelFont, System.Windows.TextAlignment.Left,
                        Brushes.Transparent, Brushes.Transparent, 0);
                else RemoveDrawObject("Ema13Label");

                if (ShowEma50 && CurrentBar >= 50)
                    Draw.Text(this, "Ema50Label", false, "50", 0, ema50Ind[0], 0, Ema50Color,
                        labelFont, System.Windows.TextAlignment.Left,
                        Brushes.Transparent, Brushes.Transparent, 0);
                else RemoveDrawObject("Ema50Label");

                if (ShowEma200 && CurrentBar >= 200)
                    Draw.Text(this, "Ema200Label", false, "200", 0, ema200Ind[0], 0, Ema200Color,
                        labelFont, System.Windows.TextAlignment.Left,
                        Brushes.Transparent, Brushes.Transparent, 0);
                else RemoveDrawObject("Ema200Label");

                if (ShowEma800 && CurrentBar >= 800)
                    Draw.Text(this, "Ema800Label", false, "800", 0, ema800Ind[0], 0, Ema800Color,
                        labelFont, System.Windows.TextAlignment.Left,
                        Brushes.Transparent, Brushes.Transparent, 0);
                else RemoveDrawObject("Ema800Label");
            }

            // ── Sessions / day / week / month tracking ────────────────────────
            if (IsFirstTickOfBar || Calculate == Calculate.OnBarClose)
            {
                DateTime barTime = Time[0];
                DateTime prevDate = CurrentBar > 0 ? Time[1].Date : barTime.Date;
                bool newDay = barTime.Date != prevDate;

                if (newDay || CurrentBar == 0)
                {
                    if (prevDayLoaded)
                    {
                        yesterdayHigh  = dayHigh;
                        yesterdayLow   = dayLow;
                        prevDayHigh    = dayHigh;
                        prevDayLow     = dayLow;
                        prevDayClose   = dayClose;

                        double dRange = dayHigh - dayLow;
                        if (dailyRanges.Count >= AdrLength) dailyRanges.Dequeue();
                        dailyRanges.Enqueue(dRange);
                        if (rdRanges.Count >= RdLength) rdRanges.Dequeue();
                        rdRanges.Enqueue(dRange);

                        ComputePivots();
                    }

                    if (currentDailyOpenEntry != null)
                        currentDailyOpenEntry.EndBarIndex = CurrentBar - 1;

                    dayHigh      = High[0];
                    dayLow       = Low[0];
                    dayClose     = Close[0];
                    dailyOpen    = Open[0];
                    dailyOpenSet = true;
                    prevDayLoaded = true;

                    psyDayHigh     = High[0];
                    psyDayLow      = Low[0];
                    psyDayStartBar = CurrentBar;

                    currentDailyOpenEntry = new DailyOpenEntry
                    {
                        OpenPrice     = Open[0],
                        StartBarIndex = CurrentBar,
                        EndBarIndex   = CurrentBar
                    };
                    lock (_sessionLock)
                    {
                        if (dailyOpenEntries.Count >= 200) dailyOpenEntries.RemoveAt(0);
                        dailyOpenEntries.Add(currentDailyOpenEntry);
                    }

                    alertAdrHighFired = alertAdrLowFired = false;
                    _tpoStopBearishFallbackLogged  = false;
                    _tpoStopBullishFallbackLogged  = false;
                    _ibTargetBearishFallbackLogged = false;
                    _ibTargetBullishFallbackLogged = false;
                }
                else
                {
                    if (!dailyOpenSet)
                    {
                        dailyOpen    = Open[0];
                        dailyOpenSet = true;
                        currentDailyOpenEntry = new DailyOpenEntry
                        {
                            OpenPrice     = Open[0],
                            StartBarIndex = CurrentBar,
                            EndBarIndex   = CurrentBar
                        };
                        lock (_sessionLock)
                        {
                            if (dailyOpenEntries.Count >= 200) dailyOpenEntries.RemoveAt(0);
                            dailyOpenEntries.Add(currentDailyOpenEntry);
                        }
                    }
                    if (High[0] > dayHigh) dayHigh = High[0];
                    if (Low[0]  < dayLow)  dayLow  = Low[0];
                    dayClose = Close[0];

                    if (psyDayStartBar > 0)
                    {
                        if (High[0] > psyDayHigh) psyDayHigh = High[0];
                        if (Low[0]  < psyDayLow)  psyDayLow  = Low[0];
                    }
                }

                if (currentDailyOpenEntry != null)
                    currentDailyOpenEntry.EndBarIndex = CurrentBar;

                int dow = (int)barTime.DayOfWeek;
                if (dow != currentDayOfWeek)
                {
                    bool newWeek = (dow < currentDayOfWeek) || (currentDayOfWeek == -1);
                    if (newWeek && weekDataLoaded)
                    {
                        lastWeekHigh = weekHigh;
                        lastWeekLow  = weekLow;

                        double wRange = weekHigh - weekLow;
                        if (weeklyRanges.Count >= AwrLength) weeklyRanges.Dequeue();
                        weeklyRanges.Enqueue(wRange);
                        if (rwRanges.Count >= RwLength) rwRanges.Dequeue();
                        rwRanges.Enqueue(wRange);

                        alertAwrHighFired = alertAwrLowFired = false;

                        weekHigh = High[0];
                        weekLow  = Low[0];

                        psyWeekHigh     = High[0];
                        psyWeekLow      = Low[0];
                        psyWeekStartBar = CurrentBar;
                    }
                    else if (!weekDataLoaded)
                    {
                        weekHigh = High[0];
                        weekLow  = Low[0];
                        psyWeekHigh     = High[0];
                        psyWeekLow      = Low[0];
                        psyWeekStartBar = CurrentBar;
                    }

                    currentDayOfWeek = dow;
                    weekDataLoaded   = true;
                }

                if (weekDataLoaded)
                {
                    if (High[0] > weekHigh) weekHigh = High[0];
                    if (Low[0]  < weekLow)  weekLow  = Low[0];
                }

                int mon = barTime.Month;
                if (mon != currentMonth)
                {
                    if (monthDataLoaded && monthHigh > 0 && monthLow > 0)
                    {
                        double mRange = monthHigh - monthLow;
                        if (monthlyRanges.Count >= AmrLength) monthlyRanges.Dequeue();
                        monthlyRanges.Enqueue(mRange);
                        alertAmrHighFired = alertAmrLowFired = false;
                    }
                    monthHigh       = High[0];
                    monthLow        = Low[0];
                    currentMonth    = mon;
                    monthDataLoaded = true;
                }
                else if (monthDataLoaded)
                {
                    if (High[0] > monthHigh) monthHigh = High[0];
                    if (Low[0]  < monthLow)  monthLow  = Low[0];
                }
            }

            // ── ADR / AWR / AMR / RD / RW ─────────────────────────────────────
            if (dailyRanges.Count > 0)
            {
                adrValue = dailyRanges.Average();
                double todayRange = dayHigh - dayLow;
                if (AdrUseDailyOpen && dailyOpen > 0)
                {
                    adrHigh = dailyOpen + adrValue / 2.0;
                    adrLow  = dailyOpen - adrValue / 2.0;
                }
                else
                {
                    double slack = (adrValue - todayRange) / 2.0;
                    adrHigh = dayHigh + slack;
                    adrLow  = dayLow  - slack;
                }
            }

            if (weeklyRanges.Count > 0)
            {
                awrValue = weeklyRanges.Average();
                double wSlack = (awrValue - (weekHigh - weekLow)) / 2.0;
                awrHigh = weekHigh + wSlack;
                awrLow  = weekLow  - wSlack;
            }

            if (monthlyRanges.Count > 0)
            {
                amrValue = monthlyRanges.Average();
                if (dailyOpen > 0)
                {
                    amrHigh = dailyOpen + amrValue / 2.0;
                    amrLow  = dailyOpen - amrValue / 2.0;
                }
            }

            if (rdRanges.Count > 0)
            {
                rdValue = rdRanges.Average();
                double rdSlack = (rdValue - (dayHigh - dayLow)) / 2.0;
                rdHigh = dayHigh + rdSlack;
                rdLow  = dayLow  - rdSlack;
            }

            if (rwRanges.Count > 0)
            {
                rwValue = rwRanges.Average();
                double rwSlack = (rwValue - (weekHigh - weekLow)) / 2.0;
                rwHigh = weekHigh + rwSlack;
                rwLow  = weekLow  - rwSlack;
            }

            // ── Session tracking ──────────────────────────────────────────────
            if (IsFirstTickOfBar || Calculate == Calculate.OnBarClose)
            {
                DateTime barEt = BarTimeEt();
                UpdateSessions(barEt);
                UpdateEthAndRthOpens(barEt);
                UpdateVwap(barEt);
                UpdateTPOSession(barEt);

                if (psyWeekStartBar > 0)
                {
                    if (High[0] > psyWeekHigh) psyWeekHigh = High[0];
                    if (Low[0]  < psyWeekLow)  psyWeekLow  = Low[0];
                }
            }

            // Cache VWAP and EMA50 values so CalculateEntryScore() and RenderMonitoringDashboard()
            // always reference the same bar data regardless of render timing (Bug 8 fix)
            if (vwapEthData != null && vwapEthData.Count > 0)
            {
                var vd = vwapEthData[vwapEthData.Count - 1];
                if (vd != null) _cachedVwapValue = vd.Vwap;
            }
            if (CurrentBar >= 50)
                _cachedEma50Value = ema50Ind[0];

            // ── Sessions alerts ───────────────────────────────────────────────
            if (IsFirstTickOfBar && adrValue > 0)
            {
                double c = Close[0];
                if (!alertAdrHighFired && c >= adrHigh)
                {
                    alertAdrHighFired = true;
                    Alert("IQMU_AdrHigh", Priority.Medium, "IQMainUltimate: ADR High reached",
                        NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav", 10, Brushes.DodgerBlue, Brushes.Black);
                }
                if (!alertAdrLowFired && c <= adrLow)
                {
                    alertAdrLowFired = true;
                    Alert("IQMU_AdrLow", Priority.Medium, "IQMainUltimate: ADR Low reached",
                        NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav", 10, Brushes.DodgerBlue, Brushes.Black);
                }
                if (awrValue > 0)
                {
                    if (!alertAwrHighFired && c >= awrHigh)
                    {
                        alertAwrHighFired = true;
                        Alert("IQMU_AwrHigh", Priority.Low, "IQMainUltimate: AWR High reached",
                            NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10, Brushes.Orange, Brushes.Black);
                    }
                    if (!alertAwrLowFired && c <= awrLow)
                    {
                        alertAwrLowFired = true;
                        Alert("IQMU_AwrLow", Priority.Low, "IQMainUltimate: AWR Low reached",
                            NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10, Brushes.Orange, Brushes.Black);
                    }
                }
                if (amrValue > 0 && !alertAmrHighFired && c >= amrHigh)
                {
                    alertAmrHighFired = true;
                    Alert("IQMU_AmrHigh", Priority.Low, "IQMainUltimate: AMR High reached",
                        NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert3.wav", 10, Brushes.IndianRed, Brushes.Black);
                }
                if (amrValue > 0 && !alertAmrLowFired && c <= amrLow)
                {
                    alertAmrLowFired = true;
                    Alert("IQMU_AmrLow", Priority.Low, "IQMainUltimate: AMR Low reached",
                        NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert3.wav", 10, Brushes.IndianRed, Brushes.Black);
                }

                if (currentPivot != null)
                {
                    double prev = CurrentBar > 0 ? Close[1] : c;
                    CheckPivotCrossAlert(prev, c, currentPivot.PP, "PP");
                    CheckPivotCrossAlert(prev, c, currentPivot.R1, "R1");
                    CheckPivotCrossAlert(prev, c, currentPivot.R2, "R2");
                    CheckPivotCrossAlert(prev, c, currentPivot.R3, "R3");
                    CheckPivotCrossAlert(prev, c, currentPivot.S1, "S1");
                    CheckPivotCrossAlert(prev, c, currentPivot.S2, "S2");
                    CheckPivotCrossAlert(prev, c, currentPivot.S3, "S3");
                }
            }

            // ── PVSRA-based liquidity zone creation ───────────────────────────
            if (EnableLiquidityZones && IsFirstTickOfBar && CurrentBar > PVSRALookback + 1)
            {
                int vb = (Calculate == Calculate.OnBarClose) ? 0 : 1;
                if (IsPVSRAGreen(vb))
                    CreatePVSRAZone(true,  false, vb);
                else if (IsPVSRARed(vb))
                    CreatePVSRAZone(false, false, vb);
                else if (IsPVSRABlue(vb))
                    CreatePVSRAZone(true,  true,  vb);
                else if (IsPVSRAPink(vb))
                    CreatePVSRAZone(false, true,  vb);
            }

            // ── Candle microstructure — guard ─────────────────────────────────
            if (CurrentBar < Math.Max(SRSwingStrength * 2 + 1, MomentumPeriod + 1))
            {
                ForceRefresh();
                return;
            }

            // ── 1. Estimate buy / sell volume ─────────────────────────────────
            double barBuy  = 0;
            double barSell = 0;

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
                double barRange = High[0] - Low[0];
                double bodyHigh = Math.Max(Open[0], Close[0]);
                double bodyLow  = Math.Min(Open[0], Close[0]);
                double bullFrac = barRange > 0 ? (bodyHigh - Low[0]) / barRange : 0.5;
                barBuy  = Volume[0] * bullFrac;
                barSell = Volume[0] * (1.0 - bullFrac);
            }

            // ── 2. Cumulative delta ───────────────────────────────────────────
            double delta = barBuy - barSell;
            if (IsFirstTickOfBar || Calculate != Calculate.OnEachTick)
            {
                cumDelta       += delta;
                sessionBuyVol  += barBuy;
                sessionSellVol += barSell;
            }

            double totalVol = barBuy + barSell;
            double deltaPct = totalVol > 0 ? (delta / totalVol) * 100.0 : 0;

            // ── 3. Imbalance percentile ───────────────────────────────────────
            if (IsFirstTickOfBar || Calculate != Calculate.OnEachTick)
            {
                if (deltaHistory.Count >= 1000) deltaHistory.RemoveAt(0);
                deltaHistory.Add(Math.Abs(delta));
                UpdateImbalanceThresholds();
            }

            // ── 4. Absorption detection ───────────────────────────────────────
            bool isAbsorption = false;
            if (totalVol > 0)
            {
                double bodyPct     = Math.Abs(Close[0] - Open[0]) / (High[0] - Low[0] + TickSize);
                double counterFrac = Close[0] >= Open[0] ? barSell / totalVol : barBuy / totalVol;
                isAbsorption = bodyPct < 0.35 && counterFrac * 100.0 >= AbsorptionSensitivity;
            }

            // ── 5. Imbalance detection ────────────────────────────────────────
            bool isImbalance = Math.Abs(delta) > imbalanceHigh ||
                               (imbalanceHigh > 0 && Math.Abs(delta) > imbalanceHigh * 0.75);

            // ── 6. S/R level auto-detection ───────────────────────────────────
            if (IsFirstTickOfBar)
                TryDetectSRLevel();

            // ── 6a. Liquidity zone recovery ───────────────────────────────────
            if (EnableLiquidityZones && IsFirstTickOfBar)
                CheckLiquidityZoneRecovery();

            // ── 6b. OTE zone detection ────────────────────────────────────────
            if (ShowOTE && IsFirstTickOfBar)
                UpdateOTEZones();

            // ── 7. Fake-breakout filters ──────────────────────────────────────
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
                    BuyVolume       = barBuy,
                    SellVolume      = barSell,
                    Delta           = delta,
                    DeltaPct        = deltaPct,
                    CumDelta        = cumDelta,
                    IsAbsorption    = isAbsorption,
                    IsImbalance     = isImbalance,
                    IsFakeBreakout  = isFakeBreakout,
                    FakeBreakoutDir = fakeBreakoutDir
                });
            }

            // ── 9. Dashboard strings ──────────────────────────────────────────
            UpdateDashboardText(barBuy, barSell, delta, deltaPct, isAbsorption, isImbalance, isFakeBreakout);

            // ── 10. Candle alerts ─────────────────────────────────────────────
            if (IsFirstTickOfBar)
            {
                if (isFakeBreakout)
                    Alert("IQMU_FakeBreakout", Priority.Medium, "IQMainUltimate: Fake Breakout detected",
                          NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav", 10, Brushes.OrangeRed, Brushes.Black);
                if (isAbsorption)
                    Alert("IQMU_Absorption", Priority.Low, "IQMainUltimate: Absorption bar",
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
                    book[price].Size   = size;
                    book[price].IsSpoof = false;
                    break;
                case Operation.Remove:
                    if (book.ContainsKey(price))
                        book.Remove(price);
                    break;
            }

            if (isBid && e.Position == 0)
            {
                bestBidPrice  = price;
                bestBidSize   = size;
                totalBidDepth = book.Values.Sum(b => b.Size);
            }
            else if (isAsk && e.Position == 0)
            {
                bestAskPrice  = price;
                bestAskSize   = size;
                totalAskDepth = book.Values.Sum(b => b.Size);
            }

            DetectOrderBookWalls(isBid ? bidBook : askBook, isBid);

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
        #region GPU Rendering — OnRenderTargetChanged / OnRender

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
            {
                try { CreateDXResources(); }
                catch (Exception ex)
                {
                    Print("IQMainUltimate: Unexpected exception from CreateDXResources: " + ex.Message);
                    return;
                }
            }

            if (!dxReady)
                return;

            var rt   = RenderTarget;
            float rtW = rt.Size.Width;
            float rtH = rt.Size.Height;

            int fromBar = ChartBars.FromIndex;
            int toBar   = ChartBars.ToIndex;

            if (fromBar > toBar)
                return;

            // Bug C Fix — clear label collision tracking once per render frame so OTE labels
            // and TPO labels share the same collision space across all render methods.
            _usedLabelYPositions.Clear();

            // ── 1. Session boxes ──────────────────────────────────────────────
            try { RenderSessionBoxes(chartControl, chartScale, rtW, rtH); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQMainUltimate: SharpDX error RenderSessionBoxes: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQMainUltimate: RenderSessionBoxes [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 2. Liquidity zones ────────────────────────────────────────────
            try { if (EnableLiquidityZones) RenderLiquidityZones(chartControl, chartScale); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQMainUltimate: SharpDX error RenderLiquidityZones: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQMainUltimate: RenderLiquidityZones [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 3. Pivot levels ───────────────────────────────────────────────
            try { if (currentPivot != null) RenderPivots(chartControl, chartScale, rtW); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQMainUltimate: SharpDX error RenderPivots: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQMainUltimate: RenderPivots [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 4. Yesterday / last week Hi/Lo ────────────────────────────────
            try
            {
                RenderYesterdayLevels(chartControl, chartScale, rtW);
                RenderLastWeekLevels(chartControl, chartScale, rtW);
            }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQMainUltimate: SharpDX error RenderYesterday: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQMainUltimate: RenderYesterday [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 5. ADR / AWR / AMR / RD / RW bands ───────────────────────────
            try
            {
                if (adrValue > 0 && ShowAdr) RenderHorizontalBand(chartControl, chartScale, rtW, adrHigh, adrLow, dxAdrBrush, "ADR H", "ADR L", ShowAdrLabels, adrHigh, adrLow, ShowAdr50, AdrLineStyle);
                if (awrValue > 0 && ShowAwr) RenderHorizontalBand(chartControl, chartScale, rtW, awrHigh, awrLow, dxAwrBrush, "AWR H", "AWR L", ShowAwrLabels, awrHigh, awrLow, ShowAwr50, AwrLineStyle);
                if (amrValue > 0 && ShowAmr) RenderHorizontalBand(chartControl, chartScale, rtW, amrHigh, amrLow, dxAmrBrush, "AMR H", "AMR L", ShowAmrLabels, amrHigh, amrLow, ShowAmr50, AmrLineStyle);
                if (rdValue  > 0 && ShowRd)  RenderHorizontalBand(chartControl, chartScale, rtW, rdHigh,  rdLow,  dxRdBrush,  "RD H",  "RD L",  ShowRdLabels,  rdHigh,  rdLow,  false);
                if (rwValue  > 0 && ShowRw)  RenderHorizontalBand(chartControl, chartScale, rtW, rwHigh,  rwLow,  dxRwBrush,  "RW H",  "RW L",  ShowRwLabels,  rwHigh,  rwLow,  false);
            }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQMainUltimate: SharpDX error RenderBands: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQMainUltimate: RenderBands [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 6. Daily open lines ───────────────────────────────────────────
            try { if (ShowDailyOpen) RenderDailyOpenLines(chartControl, chartScale); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQMainUltimate: SharpDX error RenderDailyOpen: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQMainUltimate: RenderDailyOpen [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 6b. ETH Daily Open (6 PM ET) ─────────────────────────────────
            try { if (ShowEthDailyOpen) RenderSessionOpenLines(chartControl, chartScale, ethOpenEntries, dxEthDailyOpenBrush, EthDailyOpenLabel); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQMainUltimate: SharpDX error RenderEthOpen: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQMainUltimate: RenderEthOpen [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 6c. RTH Session Opens (Asia, London, US) ──────────────────────
            try
            {
                if (ShowAsiaOpen)   RenderSessionOpenLines(chartControl, chartScale, rthAsiaOpenEntries,   dxAsiaOpenBrush,   AsiaOpenLabel);
                if (ShowLondonOpen) RenderSessionOpenLines(chartControl, chartScale, rthLondonOpenEntries, dxLondonOpenBrush, LondonOpenLabel);
                if (ShowUsOpen)     RenderSessionOpenLines(chartControl, chartScale, rthUsOpenEntries,     dxUsOpenBrush,     UsOpenLabel);
            }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQMainUltimate: SharpDX error RenderRthOpens: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQMainUltimate: RenderRthOpens [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 7. Psy levels (daily + weekly round numbers) ─────────────────
            try
            {
                double psyStep = PsyRoundIncrement * TickSize;
                if (psyStep > 0 && dxPsyBrush != null)
                {
                    if (ShowDailyPsy && psyDayHigh > 0)
                    {
                        double dPsyH = Math.Ceiling(psyDayHigh / psyStep) * psyStep;
                        double dPsyL = Math.Floor(psyDayLow   / psyStep) * psyStep;
                        RenderSingleLine(chartControl, chartScale, rtW, dPsyH, dxPsyBrush, ShowPsyLabels ? "DPsy H" : "", ShowPsyLabels);
                        RenderSingleLine(chartControl, chartScale, rtW, dPsyL, dxPsyBrush, ShowPsyLabels ? "DPsy L" : "", ShowPsyLabels);
                    }
                    if (ShowWeeklyPsy && psyWeekHigh > 0)
                    {
                        double wPsyH = Math.Ceiling(psyWeekHigh / psyStep) * psyStep;
                        double wPsyL = Math.Floor(psyWeekLow   / psyStep) * psyStep;
                        RenderSingleLine(chartControl, chartScale, rtW, wPsyH, dxPsyBrush, ShowPsyLabels ? "WPsy H" : "", ShowPsyLabels);
                        RenderSingleLine(chartControl, chartScale, rtW, wPsyL, dxPsyBrush, ShowPsyLabels ? "WPsy L" : "", ShowPsyLabels);
                    }
                }
            }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQMainUltimate: SharpDX error RenderPsy: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQMainUltimate: RenderPsy [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 8. Candles ────────────────────────────────────────────────────
            try { RenderCandles(chartControl, chartScale, fromBar, toBar); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQMainUltimate: SharpDX error RenderCandles: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQMainUltimate: RenderCandles [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 8b. VWAP ──────────────────────────────────────────────────────
            try { if (ShowVwap) RenderVwap(chartControl, chartScale, fromBar, toBar); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQMainUltimate: SharpDX error RenderVwap: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQMainUltimate: RenderVwap [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 8c. OTE Zones ─────────────────────────────────────────────────
            try { if (ShowOTE) RenderOTEZones(chartControl, chartScale); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQMainUltimate: SharpDX error RenderOTEZones: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQMainUltimate: RenderOTEZones [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 8d. TPO / Market Profile Levels ───────────────────────────────
            try { if (ShowPOC || ShowValueArea || ShowInitialBalance || ShowNakedLevels) RenderTPOLevels(chartControl, chartScale, rtW); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQMainUltimate: SharpDX error RenderTPOLevels: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQMainUltimate: RenderTPOLevels [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 9. Wall lines ─────────────────────────────────────────────────
            try { if (ShowWallLines && EnableLevel2 && level2Available) RenderWallLines(chartControl, chartScale); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQMainUltimate: SharpDX error RenderWallLines: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQMainUltimate: RenderWallLines [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 10. Range table ───────────────────────────────────────────────
            try { if (ShowRangeTable) RenderRangeTable(chartControl, chartScale, rtW, rtH); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQMainUltimate: SharpDX error RenderRangeTable: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQMainUltimate: RenderRangeTable [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 11. DST table ─────────────────────────────────────────────────
            try { if (ShowDstTable) RenderDstTable(chartControl, chartScale, rtW, rtH); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQMainUltimate: SharpDX error RenderDstTable: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQMainUltimate: RenderDstTable [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 12. Unified dashboard overlay ─────────────────────────────────
            try { RenderDashboard(chartControl, chartScale, rtW, rtH); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQMainUltimate: SharpDX error RenderDashboard: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQMainUltimate: RenderDashboard [" + ex.GetType().Name + "]: " + ex.Message); }
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Render helpers — sessions, pivots, bands, daily open

        private void RenderSessionBoxes(ChartControl cc, ChartScale cs, float rtW, float rtH)
        {
            if (sessionBoxes == null || sessionBoxes.Count == 0) return;
            var rt = RenderTarget;
            if (rt == null) return;

            List<SessionBox> snapshot;
            lock (_sessionLock)
            {
                snapshot = sessionBoxes.ToList();
            }

            foreach (var box in snapshot)
            {
                if (!SessionShowOpeningRange(box.SessionId)) continue;
                int sid = box.SessionId;
                if (dxSessionBoxBrush == null || dxSessionBoxBrush[sid] == null) continue;

                int startIdx = Math.Max(ChartBars.FromIndex, box.StartBarIndex);
                int endIdx   = Math.Min(ChartBars.ToIndex,   box.EndBarIndex);
                if (startIdx > endIdx) continue;

                float xLeft  = cc.GetXByBarIndex(ChartBars, startIdx) - cc.GetBarPaintWidth(ChartBars) / 2f;
                float xRight = cc.GetXByBarIndex(ChartBars, endIdx)   + cc.GetBarPaintWidth(ChartBars) / 2f;
                float yHigh  = cs.GetYByValue(box.SessionHigh);
                float yLow   = cs.GetYByValue(box.SessionLow);
                float boxH   = Math.Max(1f, yLow - yHigh);
                var   rect   = new SharpDX.RectangleF(xLeft, yHigh, xRight - xLeft, boxH);

                rt.FillRectangle(rect, dxSessionBoxBrush[sid]);

                if (dxSessionBorderBrush != null && dxSessionBorderBrush[sid] != null)
                {
                    rt.DrawLine(new SharpDX.Vector2(xLeft,  yHigh), new SharpDX.Vector2(xRight, yHigh), dxSessionBorderBrush[sid], 1.5f);
                    rt.DrawLine(new SharpDX.Vector2(xLeft,  yLow),  new SharpDX.Vector2(xRight, yLow),  dxSessionBorderBrush[sid], 1.5f);
                }

                if (SessionShowLabel(sid) && dxLabelFormat != null)
                {
                    string lbl = GetSessionLabel(sid);
                    var    lr  = new SharpDX.RectangleF(xLeft + 3f, yHigh + 2f, 120f, 16f);
                    if (dxSessionBorderBrush != null && dxSessionBorderBrush[sid] != null)
                        rt.DrawText(lbl, dxLabelFormat, lr, dxSessionBorderBrush[sid]);
                }
            }
        }

        private void RenderDailyOpenLines(ChartControl cc, ChartScale cs)
        {
            if (dxDailyOpenBrush == null || dailyOpenEntries == null) return;
            var rt = RenderTarget;
            if (rt == null) return;

            List<DailyOpenEntry> snapshot;
            lock (_sessionLock)
            {
                snapshot = dailyOpenEntries.ToList();
            }

            if (!ShowHistoricalDailyOpens && snapshot.Count > 0)
                snapshot = new List<DailyOpenEntry> { snapshot[snapshot.Count - 1] };

            float barHalfWidth = cc.GetBarPaintWidth(ChartBars) / 2f;

            foreach (var entry in snapshot)
            {
                if (entry.OpenPrice == 0) continue;
                int startIdx = Math.Max(ChartBars.FromIndex, entry.StartBarIndex);
                int endIdx   = Math.Min(ChartBars.ToIndex,   entry.EndBarIndex);
                if (startIdx > endIdx) continue;

                float xLeft  = cc.GetXByBarIndex(ChartBars, startIdx) - barHalfWidth;
                float xRight = cc.GetXByBarIndex(ChartBars, endIdx)   + barHalfWidth;
                float y      = cs.GetYByValue(entry.OpenPrice);

                DrawStyledLine(xLeft, y, xRight, y, dxDailyOpenBrush, 1.5f, DailyOpenLineStyle);

                if (dxLabelFormat != null)
                {
                    string txt  = "DO " + Instrument.MasterInstrument.FormatPrice(entry.OpenPrice);
                    rt.DrawText(txt, dxLabelFormat,
                        new SharpDX.RectangleF(xRight + 4f, y + 4f, 120f, 16f), dxDailyOpenBrush);
                }
            }
        }

        private void RenderSessionOpenLines(ChartControl cc, ChartScale cs,
            List<SessionOpenEntry> entries, SharpDX.Direct2D1.SolidColorBrush brush, string labelText)
        {
            if (brush == null || entries == null) return;
            var rt = RenderTarget;
            if (rt == null) return;

            List<SessionOpenEntry> snapshot;
            lock (_sessionLock)
            {
                snapshot = entries.ToList();
            }

            float barHalfWidth = cc.GetBarPaintWidth(ChartBars) / 2f;

            foreach (var entry in snapshot)
            {
                if (entry.OpenPrice == 0) continue;
                int startIdx = Math.Max(ChartBars.FromIndex, entry.StartBarIndex);
                int endIdx   = Math.Min(ChartBars.ToIndex,   entry.EndBarIndex);
                if (startIdx > endIdx) continue;

                float xLeft  = cc.GetXByBarIndex(ChartBars, startIdx) - barHalfWidth;
                float xRight = cc.GetXByBarIndex(ChartBars, endIdx)   + barHalfWidth;
                float y      = cs.GetYByValue(entry.OpenPrice);

                DrawStyledLine(xLeft, y, xRight, y, brush, 1.5f, DailyOpenLineStyle);

                if (dxLabelFormat != null)
                {
                    string txt = labelText + " " + Instrument.MasterInstrument.FormatPrice(entry.OpenPrice);
                    rt.DrawText(txt, dxLabelFormat,
                        new SharpDX.RectangleF(xRight + 4f, y + 4f, 120f, 16f), brush);
                }
            }
        }

        private void RenderPivots(ChartControl cc, ChartScale cs, float rtW)
        {
            if (currentPivot == null) return;

            float barHalfWidth = cc.GetBarPaintWidth(ChartBars) / 2f;
            int   startIdx     = Math.Max(ChartBars.FromIndex, currentPivot.StartBarIndex);
            float xLeft        = cc.GetXByBarIndex(ChartBars, startIdx) - barHalfWidth;

            if (ShowPP     && dxPPBrush     != null) RenderPivotLine(cs, xLeft, rtW, currentPivot.PP, dxPPBrush,     "PP", ShowPivotLabels, PivotLineStyle);
            if (ShowLevel1 && dxRLevelBrush != null) RenderPivotLine(cs, xLeft, rtW, currentPivot.R1, dxRLevelBrush, "R1", ShowPivotLabels, PivotLineStyle);
            if (ShowLevel1 && dxSLevelBrush != null) RenderPivotLine(cs, xLeft, rtW, currentPivot.S1, dxSLevelBrush, "S1", ShowPivotLabels, PivotLineStyle);
            if (ShowLevel2 && dxRLevelBrush != null) RenderPivotLine(cs, xLeft, rtW, currentPivot.R2, dxRLevelBrush, "R2", ShowPivotLabels, PivotLineStyle);
            if (ShowLevel2 && dxSLevelBrush != null) RenderPivotLine(cs, xLeft, rtW, currentPivot.S2, dxSLevelBrush, "S2", ShowPivotLabels, PivotLineStyle);
            if (ShowLevel3 && dxRLevelBrush != null) RenderPivotLine(cs, xLeft, rtW, currentPivot.R3, dxRLevelBrush, "R3", ShowPivotLabels, PivotLineStyle);
            if (ShowLevel3 && dxSLevelBrush != null) RenderPivotLine(cs, xLeft, rtW, currentPivot.S3, dxSLevelBrush, "S3", ShowPivotLabels, PivotLineStyle);

            if (ShowMLevels && dxMLevelBrush != null)
            {
                RenderPivotLine(cs, xLeft, rtW, currentPivot.M0, dxMLevelBrush, ShowMLabels ? "M0" : "", ShowMLabels, MLevelLineStyle);
                RenderPivotLine(cs, xLeft, rtW, currentPivot.M1, dxMLevelBrush, ShowMLabels ? "M1" : "", ShowMLabels, MLevelLineStyle);
                RenderPivotLine(cs, xLeft, rtW, currentPivot.M2, dxMLevelBrush, ShowMLabels ? "M2" : "", ShowMLabels, MLevelLineStyle);
                RenderPivotLine(cs, xLeft, rtW, currentPivot.M3, dxMLevelBrush, ShowMLabels ? "M3" : "", ShowMLabels, MLevelLineStyle);
                RenderPivotLine(cs, xLeft, rtW, currentPivot.M4, dxMLevelBrush, ShowMLabels ? "M4" : "", ShowMLabels, MLevelLineStyle);
                RenderPivotLine(cs, xLeft, rtW, currentPivot.M5, dxMLevelBrush, ShowMLabels ? "M5" : "", ShowMLabels, MLevelLineStyle);
            }
        }

        private void RenderPivotLine(ChartScale cs, float xLeft, float rtW, double price,
            SharpDX.Direct2D1.SolidColorBrush brush, string label, bool showLabel, IQMLineStyle style)
        {
            if (price == 0) return;
            float y = cs.GetYByValue(price);
            DrawStyledLine(xLeft, y, rtW, y, brush, 1f, style);
            if (showLabel && label.Length > 0 && dxLabelFormat != null)
            {
                string txt    = label + " " + Instrument.MasterInstrument.FormatPrice(price);
                int    lblBar = Math.Min(CurrentBar, ChartBars.ToIndex);
                float  labelX = ChartControl.GetXByBarIndex(ChartBars, lblBar) + 6f;
                RenderTarget.DrawText(txt, dxLabelFormat,
                    new SharpDX.RectangleF(labelX, y + 4f, 120f, 16f), brush);
            }
        }

        private void RenderYesterdayLevels(ChartControl cc, ChartScale cs, float rtW)
        {
            if (!ShowYesterday || yesterdayHigh == 0) return;
            RenderSingleLine(cc, cs, rtW, yesterdayHigh, dxYesterdayBrush, ShowYesterdayLabels ? "YH" : "", ShowYesterdayLabels);
            RenderSingleLine(cc, cs, rtW, yesterdayLow,  dxYesterdayBrush, ShowYesterdayLabels ? "YL" : "", ShowYesterdayLabels);
        }

        private void RenderLastWeekLevels(ChartControl cc, ChartScale cs, float rtW)
        {
            if (!ShowLastWeek || lastWeekHigh == 0) return;
            RenderSingleLine(cc, cs, rtW, lastWeekHigh, dxLastWeekBrush, ShowLastWeekLabels ? "LWH" : "", ShowLastWeekLabels);
            RenderSingleLine(cc, cs, rtW, lastWeekLow,  dxLastWeekBrush, ShowLastWeekLabels ? "LWL" : "", ShowLastWeekLabels);
        }

        private void RenderSingleLine(ChartControl cc, ChartScale cs, float rtW, double price,
            SharpDX.Direct2D1.SolidColorBrush brush, string label, bool showLabel)
        {
            if (price == 0 || brush == null) return;
            float y = cs.GetYByValue(price);
            DrawStyledLine(0f, y, rtW, y, brush, 1.5f, IQMLineStyle.Solid);
            if (showLabel && label.Length > 0 && dxLabelFormat != null)
            {
                string txt    = label + " " + Instrument.MasterInstrument.FormatPrice(price);
                int    lblBar = Math.Min(CurrentBar, ChartBars.ToIndex);
                float  labelX = cc.GetXByBarIndex(ChartBars, lblBar) + 6f;
                RenderTarget.DrawText(txt, dxLabelFormat,
                    new SharpDX.RectangleF(labelX, y + 4f, 120f, 16f), brush);
            }
        }

        private void RenderHorizontalBand(ChartControl cc, ChartScale cs, float rtW,
            double high, double low, SharpDX.Direct2D1.SolidColorBrush brush,
            string highLabel, string lowLabel, bool showLabels,
            double h, double l, bool show50,
            IQMLineStyle lineStyle = IQMLineStyle.Dashed)
        {
            if (brush == null || high == 0 || low == 0) return;
            float yH = cs.GetYByValue(high);
            float yL = cs.GetYByValue(low);
            DrawStyledLine(0f, yH, rtW, yH, brush, 1.5f, lineStyle);
            DrawStyledLine(0f, yL, rtW, yL, brush, 1.5f, lineStyle);
            if (showLabels && dxLabelFormat != null)
            {
                int   lblBar = Math.Min(CurrentBar, ChartBars.ToIndex);
                float labelX = cc.GetXByBarIndex(ChartBars, lblBar) + 6f;
                RenderTarget.DrawText(highLabel + " " + Instrument.MasterInstrument.FormatPrice(high),
                    dxLabelFormat, new SharpDX.RectangleF(labelX, yH + 4f, 140f, 16f), brush);
                RenderTarget.DrawText(lowLabel  + " " + Instrument.MasterInstrument.FormatPrice(low),
                    dxLabelFormat, new SharpDX.RectangleF(labelX, yL + 4f, 140f, 16f), brush);
            }
            if (show50)
            {
                double mid  = (high + low) / 2.0;
                float  yMid = cs.GetYByValue(mid);
                DrawStyledLine(0f, yMid, rtW, yMid, brush, 1f, IQMLineStyle.Dotted);
            }
        }

        private void DrawStyledLine(float x1, float y1, float x2, float y2,
            SharpDX.Direct2D1.SolidColorBrush brush, float strokeWidth, IQMLineStyle style)
        {
            if (brush == null) return;
            var rt = RenderTarget;

            if (style == IQMLineStyle.Solid)
            {
                rt.DrawLine(new SharpDX.Vector2(x1, y1), new SharpDX.Vector2(x2, y2), brush, strokeWidth);
                return;
            }

            float dashLen  = style == IQMLineStyle.Dashed ? 8f : 3f;
            float gapLen   = style == IQMLineStyle.Dashed ? 4f : 3f;
            float totalLen = (float)Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
            if (totalLen < 1f) return;
            float dx = (x2 - x1) / totalLen;
            float dy = (y2 - y1) / totalLen;
            float pos    = 0f;
            bool  drawing = true;

            while (pos < totalLen)
            {
                float segLen = drawing ? dashLen : gapLen;
                float endPos = Math.Min(pos + segLen, totalLen);
                if (drawing)
                {
                    rt.DrawLine(
                        new SharpDX.Vector2(x1 + dx * pos,    y1 + dy * pos),
                        new SharpDX.Vector2(x1 + dx * endPos, y1 + dy * endPos),
                        brush, strokeWidth);
                }
                pos    += segLen;
                drawing = !drawing;
            }
        }

        private void RenderRangeTable(ChartControl cc, ChartScale cs, float rtW, float rtH)
        {
            if (dxDashBgBrush == null || dxDashTextBrush == null || dxDashFormat == null)
                return;

            double pipSize = TickSize;
            string[] rows =
            {
                string.Format("ADR  {0,6:F0} pips", adrValue / pipSize),
                string.Format("AWR  {0,6:F0} pips", awrValue / pipSize),
                string.Format("AMR  {0,6:F0} pips", amrValue / pipSize),
                string.Format("RD   {0,6:F0} pips", rdValue  / pipSize),
                string.Format("RW   {0,6:F0} pips", rwValue  / pipSize),
            };

            float cellH  = 18f;
            float tableW = 220f;
            float tableH = rows.Length * cellH + 24f;
            float margin = 8f;

            float tx, ty;
            GetTablePosition(TablePosition, rtW, rtH, tableW, tableH, margin, out tx, out ty);
            float clampedW;
            ty = ClampTableY(ty, tableH, rtH, tableW, rtW, out clampedW);
            if (clampedW > 0 && clampedW < tableW) tableW = clampedW;

            RenderTarget.FillRectangle(new SharpDX.RectangleF(tx, ty, tableW, tableH), dxDashBgBrush);
            RenderTarget.DrawText("Range Statistics", dxDashFormat,
                new SharpDX.RectangleF(tx + 4f, ty + 2f, tableW - 8f, 18f), dxDashHeaderBrush ?? dxDashTextBrush);

            for (int i = 0; i < rows.Length; i++)
                RenderTarget.DrawText(rows[i], dxDashFormat,
                    new SharpDX.RectangleF(tx + 4f, ty + 22f + i * cellH, tableW - 8f, cellH), dxDashTextBrush);
        }

        private void RenderDstTable(ChartControl cc, ChartScale cs, float rtW, float rtH)
        {
            if (dxDashBgBrush == null || dxDashTextBrush == null || dxDashFormat == null)
                return;

            string[] rows =
            {
                "UK DST: Last Sun Mar → Last Sun Oct",
                "US DST: 2nd Sun Mar → 1st Sun Nov",
                "AU DST: 1st Sun Oct → 1st Sun Apr",
                "London  08:00-16:30 UTC (UK DST -1)",
                "NY      14:30-21:00 UTC (US DST -1)",
                "Tokyo   00:00-06:00 UTC (no DST)",
                "HK      01:30-08:00 UTC (no DST)",
                "Sydney  22:00-06:00 UTC (AU DST -1)",
                "EUBrnks 08:00-09:00 UTC (UK DST -1)",
                "USBrnks 14:00-15:00 UTC (US DST -1)",
                "Frankft 07:00-16:30 UTC (UK DST -1)",
            };

            float cellH  = 18f;
            float tableW = 340f;
            float tableH = rows.Length * cellH + 24f;
            float margin = 8f;

            float tx, ty;
            GetTablePosition(DstTablePosition, rtW, rtH, tableW, tableH, margin, out tx, out ty);
            float clampedWDst;
            ty = ClampTableY(ty, tableH, rtH, tableW, rtW, out clampedWDst);
            if (clampedWDst > 0 && clampedWDst < tableW) tableW = clampedWDst;

            RenderTarget.FillRectangle(new SharpDX.RectangleF(tx, ty, tableW, tableH), dxDashBgBrush);
            RenderTarget.DrawText("DST Reference", dxDashFormat,
                new SharpDX.RectangleF(tx + 4f, ty + 2f, tableW - 8f, 18f), dxDashHeaderBrush ?? dxDashTextBrush);

            for (int i = 0; i < rows.Length; i++)
                RenderTarget.DrawText(rows[i], dxSmallFormat ?? dxDashFormat,
                    new SharpDX.RectangleF(tx + 4f, ty + 22f + i * cellH, tableW - 8f, cellH), dxDashTextBrush);
        }

        private const float TableTimeAxisBuffer = 60f;   // buffer for time axis at chart bottom (increased from 28f)
        private const float TableEdgePadding    = 8f;    // padding from chart edge when clamping (increased from 4f)
        private const float TableMinHeight      = 60f;   // minimum panel height
        private const float TableMinWidth       = 200f;  // minimum panel width

        private static void GetTablePosition(IQMDashboardPosition pos,
            float rtW, float rtH, float tableW, float tableH, float margin,
            out float tx, out float ty)
        {
            switch (pos)
            {
                case IQMDashboardPosition.TopLeft:    tx = margin;                  ty = margin;                  break;
                case IQMDashboardPosition.TopRight:   tx = rtW - tableW - margin;  ty = margin;                  break;
                case IQMDashboardPosition.BottomLeft: tx = margin;                  ty = rtH - tableH - margin;  break;
                default:                              tx = rtW - tableW - margin;  ty = rtH - tableH - margin;  break;
            }
        }

        /// <summary>Clamps ty so the panel stays within render area with proper buffer for time axis.
        /// Also clamps tableW to fit within the render area (minimum TableMinWidth), returned via clampedW.</summary>
        private static float ClampTableY(float ty, float tableH, float rtH, float tableW, float rtW, out float clampedW)
        {
            float chartBottom = rtH - TableTimeAxisBuffer;

            // Clamp width to available horizontal space, never below the minimum
            clampedW = Math.Max(TableMinWidth, Math.Min(tableW, rtW - TableEdgePadding * 2));

            // Ensure panel doesn't go below time axis
            if (ty + tableH > chartBottom)
                ty = chartBottom - tableH - TableEdgePadding;

            // Ensure panel doesn't go above top
            if (ty < TableEdgePadding)
                ty = TableEdgePadding;

            return ty;
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Render helpers — candles, walls, liquidity zones, unified dashboard

        private void RenderCandles(ChartControl cc, ChartScale cs, int fromBar, int toBar)
        {
            var rt = RenderTarget;

            for (int barIdx = fromBar; barIdx <= toBar; barIdx++)
            {
                if (barIdx < 0 || barIdx >= Bars.Count) continue;

                double o = Bars.GetOpen(barIdx);
                double h = Bars.GetHigh(barIdx);
                double l = Bars.GetLow(barIdx);
                double c = Bars.GetClose(barIdx);

                float x    = cc.GetXByBarIndex(ChartBars, barIdx);
                float barW = Math.Max(1f, cc.GetBarPaintWidth(ChartBars) - 2f);
                float halfW = barW / 2f;

                float yH   = cs.GetYByValue(h);
                float yL   = cs.GetYByValue(l);
                float yO   = cs.GetYByValue(o);
                float yC   = cs.GetYByValue(c);
                float yTop = Math.Min(yO, yC);
                float yBot = Math.Max(yO, yC);

                // PVSRA inline classification
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
                    if      (volPct >= HighVolumeThreshold) pvsraClass = isBull ? "HighBull" : "HighBear";
                    else if (volPct >= MidVolumeThreshold)  pvsraClass = isBull ? "MidBull"  : "MidBear";
                }

                SharpDX.Direct2D1.SolidColorBrush bodyBrush = c >= o ? dxBullBrush : dxBearBrush;
                BarSnapshot snap = GetSnapshot(barIdx);

                if (snap != null)
                {
                    switch (ColorMode)
                    {
                        case IQMCandleColorMode.PVSRA:
                            if      (pvsraClass == "HighBull") bodyBrush = dxPvsraHighBullBrush;
                            else if (pvsraClass == "HighBear") bodyBrush = dxPvsraHighBearBrush;
                            else if (pvsraClass == "MidBull")  bodyBrush = dxPvsraMidBullBrush;
                            else if (pvsraClass == "MidBear")  bodyBrush = dxPvsraMidBearBrush;
                            else                               bodyBrush = c >= o ? dxBullBrush : dxBearBrush;
                            break;
                        case IQMCandleColorMode.Composite:
                            if      (pvsraClass == "HighBull") bodyBrush = dxPvsraHighBullBrush;
                            else if (pvsraClass == "HighBear") bodyBrush = dxPvsraHighBearBrush;
                            else if (pvsraClass == "MidBull")  bodyBrush = dxPvsraMidBullBrush;
                            else if (pvsraClass == "MidBear")  bodyBrush = dxPvsraMidBearBrush;
                            else                               bodyBrush = snap.Delta >= 0 ? dxBullBrush : dxBearBrush;
                            break;
                        case IQMCandleColorMode.VolumeDelta:
                            bodyBrush = snap.Delta >= 0 ? dxBullBrush : dxBearBrush;
                            break;
                        case IQMCandleColorMode.Absorption:
                            bodyBrush = snap.IsAbsorption ? dxAbsorbBrush : (c >= o ? dxBullBrush : dxBearBrush);
                            break;
                        case IQMCandleColorMode.Imbalance:
                            bodyBrush = snap.IsImbalance ? dxImbalanceBrush : (c >= o ? dxBullBrush : dxBearBrush);
                            break;
                        case IQMCandleColorMode.FakeBreakout:
                            bodyBrush = snap.IsFakeBreakout ? dxFakeBrush : (c >= o ? dxBullBrush : dxBearBrush);
                            break;
                        case IQMCandleColorMode.Classic:
                            bodyBrush = c >= o ? dxBullBrush : dxBearBrush;
                            break;
                    }

                    // Halo on signal bars
                    if (ShowHalo && (snap.IsAbsorption || snap.IsFakeBreakout))
                    {
                        SharpDX.Direct2D1.SolidColorBrush haloBrush = snap.IsFakeBreakout ? dxFakeBrush : dxAbsorbBrush;
                        float centerY = (yTop + yBot) / 2f;
                        float baseR   = halfW + 2f;

                        for (int layer = HaloLayers; layer >= 1; layer--)
                        {
                            float r     = baseR + layer * 2f;
                            byte  alpha = (byte)(30 - layer * (30 / HaloLayers));
                            var   haloColor    = haloBrush.Color;
                            var   newHaloColor = new SharpDX.Color4(haloColor.Red, haloColor.Green, haloColor.Blue, alpha / 255f);
                            using (var haloLayerBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, newHaloColor))
                            {
                                var ellipse = new SharpDX.Direct2D1.Ellipse(
                                    new SharpDX.Vector2(x, centerY), r, r + (yBot - yTop) / 2f);
                                rt.FillEllipse(ellipse, haloLayerBrush);
                            }
                        }
                    }
                }

                // Wick
                rt.DrawLine(new SharpDX.Vector2(x, yH), new SharpDX.Vector2(x, yL), dxWickBrush, 1f);

                // Body
                float bodyH = Math.Max(1f, yBot - yTop);
                rt.FillRectangle(new SharpDX.RectangleF(x - halfW, yTop, barW, bodyH), bodyBrush);
                rt.DrawRectangle(new SharpDX.RectangleF(x - halfW, yTop, barW, bodyH), dxWickBrush, 1f);

                // Composite mode: multi-signal borders
                if (snap != null && ColorMode == IQMCandleColorMode.Composite)
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

                // Gradient transparency on body based on delta magnitude (VolumeDelta mode)
                if (snap != null && ColorMode == IQMCandleColorMode.VolumeDelta)
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

                // Cumulative delta text
                if (ShowCumDelta && snap != null && dxLabelFormat != null)
                {
                    string cdText = FormatDelta(snap.CumDelta);
                    rt.DrawText(cdText, dxLabelFormat,
                        new SharpDX.RectangleF(x - 30f, yL + 2f, 60f, 14f), dxDashTextBrush);
                }
            }
        }

        private void RenderWallLines(ChartControl cc, ChartScale cs)
        {
            var rt = RenderTarget;
            float chartWidth = rt != null ? (float)rt.Size.Width : (float)cc.ActualWidth;

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

        private void RenderLiquidityZones(ChartControl cc, ChartScale cs)
        {
            if (liquidityZones == null || liquidityZones.Count == 0) return;
            var rt = RenderTarget;
            if (rt == null) return;

            float chartWidth = (float)rt.Size.Width;

            foreach (LiquidityZone zone in liquidityZones.ToList())
            {
                if (zone.IsRecovered) continue;

                double topPrice = zone.PartialRecoveryHigh;
                double botPrice = zone.PartialRecoveryLow;

                if (topPrice <= botPrice) continue;

                float yTop = cs.GetYByValue(topPrice);
                float yBot = cs.GetYByValue(botPrice);

                if (float.IsNaN(yTop) || float.IsNaN(yBot)) continue;

                float rectTop = Math.Min(yTop, yBot);
                float zoneH   = Math.Abs(yBot - yTop);

                if (zoneH < 1f) continue;

                SharpDX.Direct2D1.SolidColorBrush zoneBrush;
                if      ( zone.IsBullish && !zone.IsAbsorption) zoneBrush = dxULZBullishBrush;
                else if ( zone.IsBullish &&  zone.IsAbsorption) zoneBrush = dxULZBlueBrush;
                else if (!zone.IsBullish && !zone.IsAbsorption) zoneBrush = dxULZBearishBrush;
                else                                             zoneBrush = dxULZPinkBrush;

                if (zoneBrush == null) continue;

                try
                {
                    float xLeft;
                    if (zone.OriginBarIndex < ChartBars.FromIndex)
                        xLeft = 0f;
                    else if (zone.OriginBarIndex > ChartBars.ToIndex)
                        continue;
                    else
                    {
                        xLeft = cc.GetXByBarIndex(ChartBars, zone.OriginBarIndex);
                        if (float.IsNaN(xLeft) || xLeft < 0f) xLeft = 0f;
                    }

                    float zoneW = Math.Max(0f, chartWidth - xLeft);
                    if (zoneW <= 0f) continue;

                    rt.FillRectangle(new SharpDX.RectangleF(xLeft, rectTop, zoneW, zoneH), zoneBrush);
                }
                catch { }
            }
        }

        private void RenderOTEZones(ChartControl cc, ChartScale cs)
        {
            if (oteZones == null || oteZones.Count == 0) return;
            var rt = RenderTarget;
            if (rt == null) return;

            float chartWidth = (float)rt.Size.Width;

            // Bug B Fix 3 — respect OTEMaxZones at render time: take only the most recently
            // created active zones, up to OTEMaxZones.
            var activeZonesList = oteZones.Where(z => z.IsActive)
                                           .OrderByDescending(z => z.CreatedBar)
                                           .Take(OTEMaxZones)
                                           .ToList();

            foreach (var zone in activeZonesList)
            {
                float y62  = cs.GetYByValue(zone.Level62);
                float y705 = cs.GetYByValue(zone.Level705);
                float y79  = cs.GetYByValue(zone.Level79);

                if (float.IsNaN(y62) || float.IsNaN(y705) || float.IsNaN(y79)) continue;

                // X start from the later of the two swing points
                int startBar = Math.Max(zone.SwingHighBar, zone.SwingLowBar);
                if (startBar < ChartBars.FromIndex) startBar = ChartBars.FromIndex;
                if (startBar > ChartBars.ToIndex)   continue;

                float xStart;
                try   { xStart = cc.GetXByBarIndex(ChartBars, startBar); }
                catch { xStart = 0f; }
                if (float.IsNaN(xStart) || xStart < 0f) xStart = 0f;

                float zoneWidth = Math.Max(0f, chartWidth - xStart);
                if (zoneWidth <= 0f) continue;

                // Zone fill between 62% and 79%
                float zoneTop    = Math.Min(y62, y79);
                float zoneBottom = Math.Max(y62, y79);
                float zoneHeight = zoneBottom - zoneTop;

                if (zoneHeight >= 1f)
                {
                    var zoneBrush = zone.IsBullish ? dxOTEBullishBrush : dxOTEBearishBrush;
                    if (zoneBrush != null)
                        rt.FillRectangle(new SharpDX.RectangleF(xStart, zoneTop, zoneWidth, zoneHeight), zoneBrush);
                }

                // 62% and 79% boundary lines
                if (dxOTELineBrush != null)
                {
                    DrawStyledLine(xStart, y62, chartWidth, y62, dxOTELineBrush, OTELineThickness, OTELineStyle);
                    DrawStyledLine(xStart, y79, chartWidth, y79, dxOTELineBrush, OTELineThickness, OTELineStyle);
                }

                // 70.5% optimal entry line (bolder, gold)
                if (dxOTEOptimalBrush != null)
                    DrawStyledLine(xStart, y705, chartWidth, y705, dxOTEOptimalBrush, OTEOptimalThickness, IQMLineStyle.Solid);

                // Position labels at the START of the zone (left side), like session labels
                // Bug C Fix — apply collision avoidance so overlapping zones don't mangle each other's labels
                if (ShowOTELabels && dxLabelFormat != null)
                {
                    string prefix = OTELabelPrefix;
                    float labelX  = xStart + 4f;
                    float labelW  = 160f;

                    float safe62  = GetNonCollidingLabelY(y62  - 14f);
                    float safe705 = GetNonCollidingLabelY(y705 - 14f);
                    float safe79  = GetNonCollidingLabelY(y79  +  2f);

                    string lbl62  = prefix + " 62% "   + Instrument.MasterInstrument.FormatPrice(zone.Level62);
                    string lbl705 = prefix + " 70.5% " + Instrument.MasterInstrument.FormatPrice(zone.Level705);
                    string lbl79  = prefix + " 79% "   + Instrument.MasterInstrument.FormatPrice(zone.Level79);

                    if (dxOTELineBrush != null)
                    {
                        rt.DrawText(lbl62,  dxLabelFormat, new SharpDX.RectangleF(labelX, safe62,  labelW, 16f), dxOTELineBrush);
                        rt.DrawText(lbl79,  dxLabelFormat, new SharpDX.RectangleF(labelX, safe79,  labelW, 16f), dxOTELineBrush);
                    }
                    if (dxOTEOptimalBrush != null)
                        rt.DrawText(lbl705, dxLabelFormat, new SharpDX.RectangleF(labelX, safe705, labelW, 16f), dxOTEOptimalBrush);
                }
            }
        }

        private void RenderDashboard(ChartControl cc, ChartScale cs, float rtW, float rtH)
        {
            // Calculate metrics once if any metric-dependent dashboard is visible
            bool needMetrics =
                (ShowMonitoringDashboard && MonitoringDashboardPosition != DashboardPositionType.Hidden) ||
                (ShowEntryModeDashboard  && EntryModeDashboardPosition  != DashboardPositionType.Hidden);
            if (needMetrics) CalculateDashboardMetrics();

            // Per-corner stack cursors — track accumulated rendered height so panels sharing a corner
            // stack vertically instead of overlapping.  Indexed by (int)DashboardPositionType:
            //   0=Hidden (unused), 1=TopLeft, 2=TopRight, 3=BottomLeft, 4=BottomRight, 5=CenterTop, 6=CenterBottom
            const float StackGutter = 8f;
            var sc = new float[7]; // all 0f — each element accumulates (panelH + gutter) for that corner

            // Render order: Main → Monitoring → Entry (deterministic; earlier panels claim cursor space)
            if (ShowMainDashboard && MainDashboardPosition != DashboardPositionType.Hidden)
            {
                float h = RenderMainDashboard(cc, cs, rtW, rtH, sc[(int)MainDashboardPosition]);
                sc[(int)MainDashboardPosition] += h + StackGutter;
            }

            if (ShowMonitoringDashboard && MonitoringDashboardPosition != DashboardPositionType.Hidden)
            {
                float h = RenderMonitoringDashboard(cc, cs, rtW, rtH, sc[(int)MonitoringDashboardPosition]);
                sc[(int)MonitoringDashboardPosition] += h + StackGutter;
            }

            if (ShowEntryModeDashboard && EntryModeDashboardPosition != DashboardPositionType.Hidden)
            {
                float h = RenderEntryModeDashboard(cc, cs, rtW, rtH, sc[(int)EntryModeDashboardPosition]);
                sc[(int)EntryModeDashboardPosition] += h + StackGutter;
                if (ShowStopTargetLines) DrawStopTargetLinesOnChart(cc, cs);
            }
        }

        /// <summary>Compute all dashboard metrics once per render frame.</summary>
        private void CalculateDashboardMetrics()
        {
            dashboardEntryPrice    = _latestClose;
            dashboardConfidence    = CalculateEntryScore();
            dashboardPrimarySignal = GetPrimarySignal();
            dashboardStopPrice     = CalculateDynamicStop();
            dashboardTargetPrice   = CalculateDynamicTarget();

            // Track signal timestamp: record when an actionable signal first appears or changes.
            // DateTime.Now is intentional here — stale detection measures real wall-clock time
            // so that live traders know how long ago (in actual minutes) the signal fired.
            bool isActionable = dashboardPrimarySignal != "NEUTRAL" && dashboardPrimarySignal != "No Data";
            if (isActionable && dashboardPrimarySignal != lastTrackedSignal)
            {
                lastSignalDetectedTime = DateTime.Now;
                lastTrackedSignal      = dashboardPrimarySignal;
            }
            else if (!isActionable && lastTrackedSignal != "")
            {
                lastSignalDetectedTime  = DateTime.MinValue;
                lastTrackedSignal       = "";
                _lastTargetSource       = TargetSource.None;
                _lastStopSource         = StopSource.None;
                _lastTargetWasFallback  = false;
                _lastStopWasFallback    = false;
            }
            signalIsStale = IsSignalStale();

            if (ShowConflictWarnings)
                dashboardConflictDetected = DetectConflicts(out dashboardConflictText);
            else
            {
                dashboardConflictDetected = false;
                dashboardConflictText     = "";
            }
        }

        /// <summary>Returns a human-readable elapsed time since the signal was first detected.</summary>
        private string GetSignalElapsedTime()
        {
            if (lastSignalDetectedTime == DateTime.MinValue) return "";
            TimeSpan elapsed = DateTime.Now - lastSignalDetectedTime;
            if (elapsed.TotalSeconds < 60)
                return string.Format("{0}s ago", (int)elapsed.TotalSeconds);
            if (elapsed.TotalMinutes < 60)
                return string.Format("{0}m ago", (int)elapsed.TotalMinutes);
            return string.Format("{0}h {1}m ago", (int)elapsed.TotalHours, (int)elapsed.Minutes);
        }

        /// <summary>Returns true when the current signal has been active for more than SignalStaleMinutes.</summary>
        private bool IsSignalStale()
        {
            if (lastSignalDetectedTime == DateTime.MinValue) return false;
            return (DateTime.Now - lastSignalDetectedTime).TotalMinutes >= SignalStaleMinutes;
        }

        /// <summary>Returns true when the current signal has been active for more than SignalExpireMinutes.</summary>
        private bool HasSignalExpired()
        {
            if (lastSignalDetectedTime == DateTime.MinValue) return false;
            return (DateTime.Now - lastSignalDetectedTime).TotalMinutes >= SignalExpireMinutes;
        }

        /// <summary>Compute a 0-100 confidence score for the current bar's setup.</summary>
        private int CalculateEntryScore()
        {
            int score = 50;

            if (snapshots != null && snapshots.Count > 0)
            {
                var snap = snapshots[snapshots.Count - 1];
                if (snap.Delta > 0) score += 10; else if (snap.Delta < 0) score -= 10;
                if (snap.IsAbsorption)  score += 15;
                if (snap.IsImbalance)   score += 10;
                if (snap.IsFakeBreakout) score -= 20;
            }

            if (_cachedVwapValue > 0)
            {
                if (_latestClose > _cachedVwapValue) score += 10;
                else                                  score -= 10;
            }

            if (CurrentBar >= 50)
            {
                if (_latestClose > _cachedEma50Value) score += 5;
                else                                   score -= 5;
            }

            return Math.Max(0, Math.Min(100, score));
        }

        /// <summary>Returns true when the current primary signal is directionally bullish.</summary>
        private bool IsBullishSignal()
        {
            return dashboardPrimarySignal != null &&
                   (dashboardPrimarySignal.Contains("BULLISH") ||
                    dashboardPrimarySignal == "BUY PRESSURE");
        }

        /// <summary>Returns true when the current primary signal is directionally bearish.</summary>
        private bool IsBearishSignal()
        {
            return dashboardPrimarySignal != null &&
                   (dashboardPrimarySignal.Contains("BEARISH")    ||
                    dashboardPrimarySignal == "SELL PRESSURE"      ||
                    dashboardPrimarySignal == "FAKE BREAKOUT");
        }

        /// <summary>Returns an ADR-proportional stop distance (15% of ADR) with a floor of
        /// StopDistanceTicks * TickSize. Used as a sensible fallback when a TPO level is
        /// beyond the MaxTPOStopTicks cap.</summary>
        private double GetAdrBasedStopDistance()
        {
            double adrFallback    = adrValue > 0 ? adrValue * 0.15 : 0;
            double manualFallback = StopDistanceTicks * TickSize;
            return Math.Max(adrFallback, manualFallback);
        }

        /// <summary>Returns an ADR-proportional target distance (30% of ADR) with a floor of
        /// TargetDistanceTicks * TickSize. Used as a sensible fallback when a TPO level is
        /// beyond the MaxTPOTargetTicks cap.</summary>
        private double GetAdrBasedTargetDistance()
        {
            double adrFallback    = adrValue > 0 ? adrValue * 0.30 : 0;
            double manualFallback = TargetDistanceTicks * TickSize;
            return Math.Max(adrFallback, manualFallback);
        }

        /// <summary>Calculate the stop price based on StopMode setting.
        /// Priority for AutoDetected: VAL (if available) → liquidity zones → pivot S1 → manual fallback.
        /// TPOBased: always uses the current session VAL.
        /// Direction-aware: bearish signals place stop above price, bullish signals below.</summary>
        private double CalculateDynamicStop()
        {
            double close    = _latestClose;
            bool   bearish  = IsBearishSignal();
            double maxDist  = MaxTPOStopTicks * TickSize;

            _lastStopSource      = StopSource.None;
            _lastStopWasFallback = false;
            _lastStopSourceDetail = "";

            switch (StopMode)
            {
                case UltimateStopMode.TPOBased:
                    if (bearish)
                    {
                        // Bearish: stop above price — use VAH
                        if (tpoCurrentVAH > 0 && tpoCurrentVAH > close)
                        {
                            if (Math.Abs(close - tpoCurrentVAH) <= maxDist)
                            {
                                _lastStopSource = StopSource.VAH;
                                return tpoCurrentVAH;
                            }
                            Print(string.Format("IQMainUltimate: TPOBased bearish stop — VAH {0} skipped ({1:F0}t beyond cap {2}t)",
                                tpoCurrentVAH, Math.Abs(close - tpoCurrentVAH) / TickSize, MaxTPOStopTicks));
                        }
                        if (previousDayTPO != null && previousDayTPO.ValueAreaHigh > 0 && previousDayTPO.ValueAreaHigh > close)
                        {
                            if (Math.Abs(close - previousDayTPO.ValueAreaHigh) <= maxDist)
                            {
                                _lastStopSource = StopSource.PrevVAH;
                                _lastStopWasFallback = true;
                                return previousDayTPO.ValueAreaHigh;
                            }
                            Print(string.Format("IQMainUltimate: TPOBased bearish stop — prev VAH {0} skipped ({1:F0}t beyond cap {2}t)",
                                previousDayTPO.ValueAreaHigh, Math.Abs(close - previousDayTPO.ValueAreaHigh) / TickSize, MaxTPOStopTicks));
                        }
                        _lastStopSource = StopSource.ADR;
                        _lastStopWasFallback = true;
                        if (!_tpoStopBearishFallbackLogged)
                        {
                            Print("IQMainUltimate: TPOBased bearish stop — no usable VAH/prev-VAH; falling back to ADR");
                            _tpoStopBearishFallbackLogged = true;
                        }
                        return close + GetAdrBasedStopDistance();
                    }
                    // Bullish: stop below price — use VAL
                    if (tpoCurrentVAL > 0 && tpoCurrentVAL < close)
                    {
                        if (Math.Abs(close - tpoCurrentVAL) <= maxDist)
                        {
                            _lastStopSource = StopSource.VAL;
                            return tpoCurrentVAL;
                        }
                        Print(string.Format("IQMainUltimate: TPOBased bullish stop — VAL {0} skipped ({1:F0}t beyond cap {2}t)",
                            tpoCurrentVAL, Math.Abs(close - tpoCurrentVAL) / TickSize, MaxTPOStopTicks));
                    }
                    if (previousDayTPO != null && previousDayTPO.ValueAreaLow > 0 && previousDayTPO.ValueAreaLow < close)
                    {
                        if (Math.Abs(close - previousDayTPO.ValueAreaLow) <= maxDist)
                        {
                            _lastStopSource = StopSource.PrevVAL;
                            _lastStopWasFallback = true;
                            return previousDayTPO.ValueAreaLow;
                        }
                        Print(string.Format("IQMainUltimate: TPOBased bullish stop — prev VAL {0} skipped ({1:F0}t beyond cap {2}t)",
                            previousDayTPO.ValueAreaLow, Math.Abs(close - previousDayTPO.ValueAreaLow) / TickSize, MaxTPOStopTicks));
                    }
                    _lastStopSource = StopSource.ADR;
                    _lastStopWasFallback = true;
                    if (!_tpoStopBullishFallbackLogged)
                    {
                        Print("IQMainUltimate: TPOBased bullish stop — no usable VAL/prev-VAL; falling back to ADR");
                        _tpoStopBullishFallbackLogged = true;
                    }
                    return close - GetAdrBasedStopDistance();

                case UltimateStopMode.AutoDetected:
                    if (bearish)
                    {
                        // Bearish: stop above price
                        // 1. Previous day VAH
                        if (previousDayTPO != null && previousDayTPO.ValueAreaHigh > 0 && previousDayTPO.ValueAreaHigh > close)
                        {
                            if (Math.Abs(close - previousDayTPO.ValueAreaHigh) <= maxDist)
                            {
                                _lastStopSource = StopSource.PrevVAH;
                                return previousDayTPO.ValueAreaHigh + TickSize;
                            }
                        }
                        // 2. Current session VAH
                        if (tpoCurrentVAH > 0 && tpoCurrentVAH > close)
                        {
                            if (Math.Abs(close - tpoCurrentVAH) <= maxDist)
                            {
                                _lastStopSource = StopSource.VAH;
                                return tpoCurrentVAH + TickSize;
                            }
                        }
                        // 3. Bearish liquidity zone highs — find nearest above price
                        if (liquidityZones != null)
                        {
                            double nearestZoneHigh = double.MaxValue;
                            foreach (LiquidityZone z in liquidityZones)
                            {
                                if (!z.IsRecovered && !z.IsBullish && z.HighPrice > close && z.HighPrice < nearestZoneHigh)
                                    nearestZoneHigh = z.HighPrice;
                            }
                            if (nearestZoneHigh < double.MaxValue)
                            {
                                if (Math.Abs(close - nearestZoneHigh) <= maxDist)
                                {
                                    _lastStopSource = StopSource.LiquidityZone;
                                    return nearestZoneHigh + TickSize;
                                }
                                else
                                    Print(string.Format("IQMainUltimate: AutoDetected bearish stop — liquidity zone {0} skipped ({1:F0}t beyond cap)", nearestZoneHigh, Math.Abs(close - nearestZoneHigh) / TickSize));
                            }
                        }
                        // 4. Pivot R1
                        if (currentPivot != null && currentPivot.R1 > close)
                        {
                            if (Math.Abs(close - currentPivot.R1) <= maxDist)
                            {
                                _lastStopSource = StopSource.PivotR1;
                                return currentPivot.R1;
                            }
                            else
                                Print(string.Format("IQMainUltimate: AutoDetected bearish stop — pivot R1 {0} skipped ({1:F0}t beyond cap)", currentPivot.R1, Math.Abs(close - currentPivot.R1) / TickSize));
                        }
                        _lastStopSource = StopSource.ADR;
                        _lastStopWasFallback = true;
                        return close + GetAdrBasedStopDistance();
                    }
                    // Bullish: stop below price
                    // 1. Check yesterday's VAL (if price is above it)
                    if (previousDayTPO != null && previousDayTPO.ValueAreaLow > 0 && previousDayTPO.ValueAreaLow < close)
                    {
                        if (Math.Abs(close - previousDayTPO.ValueAreaLow) <= maxDist)
                        {
                            _lastStopSource = StopSource.PrevVAL;
                            return previousDayTPO.ValueAreaLow - TickSize;
                        }
                    }
                    // 2. Check current session VAL
                    if (tpoCurrentVAL > 0 && tpoCurrentVAL < close)
                    {
                        if (Math.Abs(close - tpoCurrentVAL) <= maxDist)
                        {
                            _lastStopSource = StopSource.VAL;
                            return tpoCurrentVAL - TickSize;
                        }
                    }
                    // 3. Check liquidity zones (original logic)
                    if (liquidityZones != null)
                    {
                        foreach (LiquidityZone z in liquidityZones)
                        {
                            if (!z.IsRecovered && z.IsBullish && z.LowPrice < close)
                            {
                                if (Math.Abs(close - z.LowPrice) <= maxDist)
                                {
                                    _lastStopSource = StopSource.LiquidityZone;
                                    return z.LowPrice - TickSize;
                                }
                                else
                                    Print(string.Format("IQMainUltimate: AutoDetected bullish stop — liquidity zone {0} skipped ({1:F0}t beyond cap)", z.LowPrice, Math.Abs(close - z.LowPrice) / TickSize));
                            }
                        }
                    }
                    // 4. Check pivot S1
                    if (currentPivot != null && currentPivot.S1 > 0 && currentPivot.S1 < close)
                    {
                        if (Math.Abs(close - currentPivot.S1) <= maxDist)
                        {
                            _lastStopSource = StopSource.PivotS1;
                            return currentPivot.S1;
                        }
                        else
                            Print(string.Format("IQMainUltimate: AutoDetected bullish stop — pivot S1 {0} skipped ({1:F0}t beyond cap)", currentPivot.S1, Math.Abs(close - currentPivot.S1) / TickSize));
                    }
                    _lastStopSource = StopSource.ADR;
                    _lastStopWasFallback = true;
                    return close - GetAdrBasedStopDistance();

                case UltimateStopMode.PivotBased:
                    if (bearish)
                    {
                        if (currentPivot != null && currentPivot.R1 > 0 && currentPivot.R1 > close)
                        {
                            if (Math.Abs(close - currentPivot.R1) <= maxDist)
                            {
                                _lastStopSource = StopSource.PivotR1;
                                return currentPivot.R1;
                            }
                            else
                                Print(string.Format("IQMainUltimate: PivotBased bearish stop — pivot R1 {0} skipped ({1:F0}t beyond cap)", currentPivot.R1, Math.Abs(close - currentPivot.R1) / TickSize));
                        }
                        _lastStopSource = StopSource.Manual;
                        _lastStopWasFallback = true;
                        return close + StopDistanceTicks * TickSize;
                    }
                    if (currentPivot != null && currentPivot.S1 > 0 && currentPivot.S1 < close)
                    {
                        if (Math.Abs(close - currentPivot.S1) <= maxDist)
                        {
                            _lastStopSource = StopSource.PivotS1;
                            return currentPivot.S1;
                        }
                        else
                            Print(string.Format("IQMainUltimate: PivotBased bullish stop — pivot S1 {0} skipped ({1:F0}t beyond cap)", currentPivot.S1, Math.Abs(close - currentPivot.S1) / TickSize));
                    }
                    _lastStopSource = StopSource.Manual;
                    _lastStopWasFallback = true;
                    return close - StopDistanceTicks * TickSize;

                case UltimateStopMode.HVNBased:
                    if (bearish)
                    {
                        double bestSrAbove = double.MaxValue;
                        if (srLevels != null)
                        {
                            foreach (double sr in srLevels)
                            {
                                if (sr > close && sr < bestSrAbove) bestSrAbove = sr;
                            }
                        }
                        if (bestSrAbove < double.MaxValue && Math.Abs(close - bestSrAbove) <= maxDist)
                        {
                            _lastStopSource = StopSource.SRLevel;
                            return bestSrAbove + TickSize;
                        }
                        if (bestSrAbove < double.MaxValue)
                            Print(string.Format("IQMainUltimate: HVNBased bearish stop — SR {0} skipped ({1:F0}t beyond cap)", bestSrAbove, Math.Abs(close - bestSrAbove) / TickSize));
                        _lastStopSource = StopSource.Manual;
                        _lastStopWasFallback = true;
                        return close + StopDistanceTicks * TickSize;
                    }
                    double bestSr = 0;
                    if (srLevels != null)
                    {
                        foreach (double sr in srLevels)
                        {
                            if (sr < close && sr > bestSr) bestSr = sr;
                        }
                    }
                    if (bestSr > 0 && Math.Abs(close - bestSr) <= maxDist)
                    {
                        _lastStopSource = StopSource.SRLevel;
                        return bestSr - TickSize;
                    }
                    if (bestSr > 0)
                        Print(string.Format("IQMainUltimate: HVNBased bullish stop — SR {0} skipped ({1:F0}t beyond cap)", bestSr, Math.Abs(close - bestSr) / TickSize));
                    _lastStopSource = StopSource.Manual;
                    _lastStopWasFallback = true;
                    return close - StopDistanceTicks * TickSize;

                case UltimateStopMode.ManualInput:
                default:
                    _lastStopSource = StopSource.Manual;
                    return bearish ? close + StopDistanceTicks * TickSize : close - StopDistanceTicks * TickSize;
            }
        }

        /// <summary>Calculate the target price based on TargetMode setting.
        /// Priority for AutoDetected: VAH → IB High Extension (if trending) → naked POC → pivot R1/R2.
        /// VAH: use current session VAH. IBExtension: use IB high extension.
        /// Direction-aware: bearish signals target below price, bullish signals above.</summary>
        private double CalculateDynamicTarget()
        {
            double close    = _latestClose;
            bool   bearish  = IsBearishSignal();
            double maxDist  = MaxTPOTargetTicks * TickSize;

            _lastTargetSource      = TargetSource.None;
            _lastTargetWasFallback = false;
            _lastTargetSourceDetail = "";

            switch (TargetMode)
            {
                case UltimateTargetMode.VAH:
                    if (bearish)
                    {
                        // Bearish: target the current session Value Area Low
                        if (tpoCurrentVAL > 0 && tpoCurrentVAL < close)
                        {
                            if (Math.Abs(close - tpoCurrentVAL) <= maxDist)
                            {
                                _lastTargetSource = TargetSource.VAL;
                                return tpoCurrentVAL;
                            }
                        }
                        _lastTargetSource = TargetSource.ADR;
                        _lastTargetWasFallback = true;
                        return close - GetAdrBasedTargetDistance();
                    }
                    // Bullish: target the current session Value Area High
                    if (tpoCurrentVAH > close)
                    {
                        if (Math.Abs(close - tpoCurrentVAH) <= maxDist)
                        {
                            _lastTargetSource = TargetSource.VAH;
                            return tpoCurrentVAH;
                        }
                    }
                    _lastTargetSource = TargetSource.ADR;
                    _lastTargetWasFallback = true;
                    return close + GetAdrBasedTargetDistance();

                case UltimateTargetMode.IBExtension:
                    if (bearish)
                    {
                        // Bearish: target IB Low Extension if price is trending down
                        if (tpoCurrentIBLowExt > 0 && tpoCurrentIBLowExt < close)
                        {
                            if (Math.Abs(close - tpoCurrentIBLowExt) <= maxDist)
                            {
                                _lastTargetSource = TargetSource.IBExtLow;
                                return tpoCurrentIBLowExt;
                            }
                            Print(string.Format("IQMainUltimate: IBExtension bearish target — IB low ext {0} skipped ({1:F0}t beyond cap {2}t)",
                                tpoCurrentIBLowExt, Math.Abs(close - tpoCurrentIBLowExt) / TickSize, MaxTPOTargetTicks));
                        }
                        else if (tpoCurrentIBLowExt == 0)
                        {
                            if (!_ibTargetBearishFallbackLogged)
                            {
                                Print("IQMainUltimate: IBExtension bearish target — IB low ext unavailable (IB window not finalized or sanity guard tripped); falling back to VAL");
                                _ibTargetBearishFallbackLogged = true;
                            }
                        }
                        // Fallback to VAL
                        if (tpoCurrentVAL > 0 && tpoCurrentVAL < close)
                        {
                            if (Math.Abs(close - tpoCurrentVAL) <= maxDist)
                            {
                                _lastTargetSource = TargetSource.VAL;
                                _lastTargetWasFallback = true;
                                return tpoCurrentVAL;
                            }
                        }
                        _lastTargetSource = TargetSource.ADR;
                        _lastTargetWasFallback = true;
                        return close - GetAdrBasedTargetDistance();
                    }
                    // Bullish: Target the IB High Extension if price is trending up
                    if (tpoCurrentIBHighExt > close)
                    {
                        if (Math.Abs(close - tpoCurrentIBHighExt) <= maxDist)
                        {
                            _lastTargetSource = TargetSource.IBExtHigh;
                            return tpoCurrentIBHighExt;
                        }
                        Print(string.Format("IQMainUltimate: IBExtension bullish target — IB high ext {0} skipped ({1:F0}t beyond cap {2}t)",
                            tpoCurrentIBHighExt, Math.Abs(close - tpoCurrentIBHighExt) / TickSize, MaxTPOTargetTicks));
                    }
                    else if (tpoCurrentIBHighExt == 0)
                    {
                        if (!_ibTargetBullishFallbackLogged)
                        {
                            Print("IQMainUltimate: IBExtension bullish target — IB high ext unavailable (IB window not finalized or sanity guard tripped); falling back to VAH");
                            _ibTargetBullishFallbackLogged = true;
                        }
                    }
                    // Fallback to VAH
                    if (tpoCurrentVAH > close)
                    {
                        if (Math.Abs(close - tpoCurrentVAH) <= maxDist)
                        {
                            _lastTargetSource = TargetSource.VAH;
                            _lastTargetWasFallback = true;
                            return tpoCurrentVAH;
                        }
                    }
                    _lastTargetSource = TargetSource.ADR;
                    _lastTargetWasFallback = true;
                    return close + GetAdrBasedTargetDistance();

                case UltimateTargetMode.AutoDetected:
                    if (bearish)
                    {
                        // 1. Check current session VAL (if price is above it)
                        if (tpoCurrentVAL > 0 && tpoCurrentVAL < close)
                        {
                            if (Math.Abs(close - tpoCurrentVAL) <= maxDist)
                            {
                                _lastTargetSource = TargetSource.VAL;
                                return tpoCurrentVAL;
                            }
                        }
                        // 2. Check IB Low Extension (if session is in trend mode)
                        if (tpoCurrentShape == TPOProfileShape.TrendDay && tpoCurrentIBLowExt > 0 && tpoCurrentIBLowExt < close)
                        {
                            if (Math.Abs(close - tpoCurrentIBLowExt) <= maxDist)
                            {
                                _lastTargetSource = TargetSource.IBExtLow;
                                return tpoCurrentIBLowExt;
                            }
                        }
                        // 3. Check nearest naked POC from previous days (below price)
                        if (nakedTPOLevels != null)
                        {
                            double bestNaked      = double.MinValue;
                            string bestNakedLabel = "";
                            foreach (var nl in nakedTPOLevels)
                            {
                                if (!nl.IsClosed && nl.LevelType == "POC" && nl.Price < close && nl.Price > bestNaked)
                                {
                                    bestNaked      = nl.Price;
                                    bestNakedLabel = nl.SessionLabel;
                                }
                            }
                            if (bestNaked > double.MinValue)
                            {
                                if (Math.Abs(close - bestNaked) <= maxDist)
                                {
                                    _lastTargetSource       = TargetSource.NakedPOC;
                                    _lastTargetSourceDetail = bestNakedLabel;
                                    return bestNaked;
                                }
                                else
                                    Print(string.Format("IQMainUltimate: AutoDetected bearish target — naked POC {0} skipped ({1:F0}t beyond cap)", bestNaked, Math.Abs(close - bestNaked) / TickSize));
                            }
                        }
                        // 4. Check pivot S1/S2
                        if (currentPivot != null && currentPivot.S1 > 0 && currentPivot.S1 < close)
                        {
                            if (Math.Abs(close - currentPivot.S1) <= maxDist)
                            {
                                _lastTargetSource = TargetSource.PivotS1;
                                return currentPivot.S1;
                            }
                            else
                                Print(string.Format("IQMainUltimate: AutoDetected bearish target — pivot S1 {0} skipped ({1:F0}t beyond cap)", currentPivot.S1, Math.Abs(close - currentPivot.S1) / TickSize));
                        }
                        if (currentPivot != null && currentPivot.S2 > 0 && currentPivot.S2 < close)
                        {
                            if (Math.Abs(close - currentPivot.S2) <= maxDist)
                            {
                                _lastTargetSource = TargetSource.PivotS2;
                                return currentPivot.S2;
                            }
                            else
                                Print(string.Format("IQMainUltimate: AutoDetected bearish target — pivot S2 {0} skipped ({1:F0}t beyond cap)", currentPivot.S2, Math.Abs(close - currentPivot.S2) / TickSize));
                        }
                        _lastTargetSource = TargetSource.ADR;
                        _lastTargetWasFallback = true;
                        return close - GetAdrBasedTargetDistance();
                    }
                    // Bullish
                    // 1. Check current session VAH (if price is below it)
                    if (tpoCurrentVAH > close)
                    {
                        if (Math.Abs(close - tpoCurrentVAH) <= maxDist)
                        {
                            _lastTargetSource = TargetSource.VAH;
                            return tpoCurrentVAH;
                        }
                    }
                    // 2. Check IB High Extension (if session is in trending mode)
                    if (tpoCurrentShape == TPOProfileShape.TrendDay && tpoCurrentIBHighExt > close)
                    {
                        if (Math.Abs(close - tpoCurrentIBHighExt) <= maxDist)
                        {
                            _lastTargetSource = TargetSource.IBExtHigh;
                            return tpoCurrentIBHighExt;
                        }
                    }
                    // 3. Check nearest naked POC from previous days (above price)
                    if (nakedTPOLevels != null)
                    {
                        double bestNaked      = double.MaxValue;
                        string bestNakedLabel = "";
                        foreach (var nl in nakedTPOLevels)
                        {
                            if (!nl.IsClosed && nl.LevelType == "POC" && nl.Price > close && nl.Price < bestNaked)
                            {
                                bestNaked      = nl.Price;
                                bestNakedLabel = nl.SessionLabel;
                            }
                        }
                        if (bestNaked < double.MaxValue)
                        {
                            if (Math.Abs(close - bestNaked) <= maxDist)
                            {
                                _lastTargetSource       = TargetSource.NakedPOC;
                                _lastTargetSourceDetail = bestNakedLabel;
                                return bestNaked;
                            }
                            else
                                Print(string.Format("IQMainUltimate: AutoDetected bullish target — naked POC {0} skipped ({1:F0}t beyond cap)", bestNaked, Math.Abs(close - bestNaked) / TickSize));
                        }
                    }
                    // 4. Check pivot R1/R2
                    if (currentPivot != null && currentPivot.R1 > close)
                    {
                        if (Math.Abs(close - currentPivot.R1) <= maxDist)
                        {
                            _lastTargetSource = TargetSource.PivotR1;
                            return currentPivot.R1;
                        }
                        else
                            Print(string.Format("IQMainUltimate: AutoDetected bullish target — pivot R1 {0} skipped ({1:F0}t beyond cap)", currentPivot.R1, Math.Abs(close - currentPivot.R1) / TickSize));
                    }
                    if (currentPivot != null && currentPivot.R2 > close)
                    {
                        if (Math.Abs(close - currentPivot.R2) <= maxDist)
                        {
                            _lastTargetSource = TargetSource.PivotR2;
                            return currentPivot.R2;
                        }
                        else
                            Print(string.Format("IQMainUltimate: AutoDetected bullish target — pivot R2 {0} skipped ({1:F0}t beyond cap)", currentPivot.R2, Math.Abs(close - currentPivot.R2) / TickSize));
                    }
                    _lastTargetSource = TargetSource.ADR;
                    _lastTargetWasFallback = true;
                    return close + GetAdrBasedTargetDistance();

                case UltimateTargetMode.PivotR1:
                    if (bearish)
                    {
                        if (currentPivot != null && currentPivot.S1 > 0)
                        {
                            if (Math.Abs(close - currentPivot.S1) <= maxDist)
                            {
                                _lastTargetSource = TargetSource.PivotS1;
                                return currentPivot.S1;
                            }
                            else
                                Print(string.Format("IQMainUltimate: PivotR1 bearish target — pivot S1 {0} skipped ({1:F0}t beyond cap)", currentPivot.S1, Math.Abs(close - currentPivot.S1) / TickSize));
                        }
                        _lastTargetSource = TargetSource.Manual;
                        _lastTargetWasFallback = true;
                        return close - TargetDistanceTicks * TickSize;
                    }
                    if (currentPivot != null && currentPivot.R1 > 0)
                    {
                        if (Math.Abs(close - currentPivot.R1) <= maxDist)
                        {
                            _lastTargetSource = TargetSource.PivotR1;
                            return currentPivot.R1;
                        }
                        else
                            Print(string.Format("IQMainUltimate: PivotR1 bullish target — pivot R1 {0} skipped ({1:F0}t beyond cap)", currentPivot.R1, Math.Abs(close - currentPivot.R1) / TickSize));
                    }
                    _lastTargetSource = TargetSource.Manual;
                    _lastTargetWasFallback = true;
                    return close + TargetDistanceTicks * TickSize;

                case UltimateTargetMode.PivotR2:
                    if (bearish)
                    {
                        if (currentPivot != null && currentPivot.S2 > 0)
                        {
                            if (Math.Abs(close - currentPivot.S2) <= maxDist)
                            {
                                _lastTargetSource = TargetSource.PivotS2;
                                return currentPivot.S2;
                            }
                            else
                                Print(string.Format("IQMainUltimate: PivotR2 bearish target — pivot S2 {0} skipped ({1:F0}t beyond cap)", currentPivot.S2, Math.Abs(close - currentPivot.S2) / TickSize));
                        }
                        _lastTargetSource = TargetSource.Manual;
                        _lastTargetWasFallback = true;
                        return close - TargetDistanceTicks * TickSize;
                    }
                    if (currentPivot != null && currentPivot.R2 > 0)
                    {
                        if (Math.Abs(close - currentPivot.R2) <= maxDist)
                        {
                            _lastTargetSource = TargetSource.PivotR2;
                            return currentPivot.R2;
                        }
                        else
                            Print(string.Format("IQMainUltimate: PivotR2 bullish target — pivot R2 {0} skipped ({1:F0}t beyond cap)", currentPivot.R2, Math.Abs(close - currentPivot.R2) / TickSize));
                    }
                    _lastTargetSource = TargetSource.Manual;
                    _lastTargetWasFallback = true;
                    return close + TargetDistanceTicks * TickSize;

                case UltimateTargetMode.ManualInput:
                default:
                    _lastTargetSource = TargetSource.Manual;
                    return bearish ? close - TargetDistanceTicks * TickSize : close + TargetDistanceTicks * TickSize;
            }
        }

        /// <summary>Return the primary signal label for the current bar.</summary>
        private string GetPrimarySignal()
        {
            if (snapshots == null || snapshots.Count == 0) return "No Data";
            var snap = snapshots[snapshots.Count - 1];

            if (snap.IsFakeBreakout)                          return "FAKE BREAKOUT";
            if (snap.IsAbsorption && snap.Delta > 0)          return "BULLISH ABSORPTION";
            if (snap.IsAbsorption && snap.Delta < 0)          return "BEARISH ABSORPTION";
            if (snap.IsImbalance  && snap.Delta > 0)          return "BULLISH IMBALANCE";
            if (snap.IsImbalance  && snap.Delta < 0)          return "BEARISH IMBALANCE";
            if (snap.Delta > 0)                               return "BUY PRESSURE";
            if (snap.Delta < 0)                               return "SELL PRESSURE";
            return "NEUTRAL";
        }

        /// <summary>Return the name of the currently active session (or "Off-Hours").</summary>
        private string GetActiveSessionName()
        {
            if (activeSessions == null) return "—";
            for (int id = 0; id < 8; id++)
            {
                if (activeSessions[id] != null && !activeSessions[id].IsComplete)
                    return GetSessionLabel(id);
            }
            return "Off-Hours";
        }

        /// <summary>
        /// B2/B13 fix: Returns the current session name and participation level using wall-clock
        /// time (DateTime.UtcNow → ET) rather than the bar's open time. This guarantees that
        /// large-interval bars (30m, 60m) whose open time precedes a session-window boundary
        /// still display the correct session, and that the main-dashboard header and monitoring
        /// panel always agree by construction (both call this single method).
        /// Falls back to BarTimeEt() only if the UTC clock conversion fails.
        /// </summary>
        private string GetCurrentSessionInfo(out bool isHighParticipation)
        {
            DateTime nowEt;
            try
            {
                nowEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, EtZone);
            }
            catch
            {
                nowEt = BarTimeEt();
            }

            for (int id = 0; id < 8; id++)
            {
                if (!IsSessionEnabled(id)) continue;
                DateTime sStart, sEnd;
                GetSessionEtTimes(id, nowEt, out sStart, out sEnd);
                if (nowEt >= sStart && nowEt < sEnd)
                {
                    isHighParticipation = System.Array.IndexOf(HighParticipationSessionIds, id) >= 0;
                    return GetSessionLabel(id);
                }
            }
            isHighParticipation = false;
            return "Off-Hours";
        }

        /// <summary>Draw a graphical volume bar representing the buy/sell ratio.</summary>
        private void DrawVolumeBar(SharpDX.Direct2D1.RenderTarget rt,
            float x, float y, float width, float height,
            double buyVolume, double sellVolume)
        {
            if (rt == null) return;

            double totalVolume = buyVolume + sellVolume;
            if (totalVolume <= 0) return;

            float bullishRatio = (float)(buyVolume / totalVolume);
            float filledWidth  = width * bullishRatio;

            SharpDX.Direct2D1.SolidColorBrush fillBrush;
            string statusLabel;

            if (bullishRatio > 0.55f)
            {
                fillBrush   = dxEnhDashGreenBrush;
                statusLabel = "BULLISH";
            }
            else if (bullishRatio < 0.45f)
            {
                fillBrush   = dxEnhDashRedBrush;
                statusLabel = "BEARISH";
            }
            else
            {
                fillBrush   = dxEnhDashNeutralBrush;
                statusLabel = "NEUTRAL";
            }

            // Draw background (full bar in gray)
            if (dxEnhDashNeutralBrush != null)
                rt.FillRectangle(new SharpDX.RectangleF(x, y, width, height), dxEnhDashNeutralBrush);

            // Draw filled portion (buy volume ratio)
            if (filledWidth > 0 && fillBrush != null)
                rt.FillRectangle(new SharpDX.RectangleF(x, y, filledWidth, height), fillBrush);

            // Draw border
            if (dxEnhDashTextBrush != null)
                rt.DrawRectangle(new SharpDX.RectangleF(x, y, width, height), dxEnhDashTextBrush, 1f);

            // Draw percentage and status label to the right of the bar
            if (dxEnhMonFormat != null && dxEnhDashTextBrush != null)
            {
                const float LabelSpacing     = 8f;   // px gap between bar right edge and label
                const float LabelVertOffset  = -2f;  // slight upward nudge to align label with bar centre
                const float LabelMaxWidth    = 120f; // max width of "100% BEARISH" text
                string barLabel = string.Format("{0:F0}% {1}", bullishRatio * 100f, statusLabel);
                rt.DrawText(barLabel, dxEnhMonFormat,
                    new SharpDX.RectangleF(x + width + LabelSpacing, y + LabelVertOffset, LabelMaxWidth, height + 4f),
                    dxEnhDashTextBrush);
            }
        }

        /// <summary>Returns true when London, NY, EU Brinks, or US Brinks session is active.</summary>
        private bool IsHighParticipationSession()
        {
            if (activeSessions == null) return false;
            foreach (int id in HighParticipationSessionIds)
            {
                if (activeSessions[id] != null && !activeSessions[id].IsComplete)
                    return true;
            }
            return false;
        }

        /// <summary>Detects conflicts and returns true if any were found, setting description.</summary>
        private bool DetectConflicts(out string description)
        {
            description = "";
            if (snapshots == null || snapshots.Count == 0)
                return false;

            var snap = snapshots[snapshots.Count - 1];

            var  parts        = new System.Text.StringBuilder();
            bool anyConflict  = false;
            var  addedLines   = new HashSet<string>();   // Bug 1: deduplicate identical conflict lines

            // B1 fix: derive volumeBullish from the same session-scoped buy/sell totals
            // used by the graphical volume bar (DrawVolumeBar) so both widgets always agree.
            // snap.Delta is per-bar and can diverge from the session ratio shown in the bar.
            bool volumeBullish  = sessionBuyVol > sessionSellVol;
            bool priceAboveVwap = _cachedVwapValue > 0 && _latestClose > _cachedVwapValue;

            // Volume vs VWAP direction conflict
            if (_cachedVwapValue > 0 && volumeBullish && !priceAboveVwap)
            {
                anyConflict = true;
                string line1 = "", line2 = "";
                switch (ConflictLevel)
                {
                    case ConflictDescriptionLevel.Brief:
                        line1 = "\u26a0 Conflict detected"; break;
                    case ConflictDescriptionLevel.Detailed:
                        line1 = "\u26a0 VOLUME BULLISH BUT PRICE BELOW VWAP (Exhaustion Risk)"; break;
                    case ConflictDescriptionLevel.VeryDetailed:
                        line1 = SeverityHigh + " VOLUME BULLISH BUT PRICE BELOW VWAP";
                        line2 = "  \u2192 Possible exhaustion or accumulation phase"; break;
                }
                if (!string.IsNullOrEmpty(line1) && addedLines.Add(line1)) parts.AppendLine(line1);
                if (!string.IsNullOrEmpty(line2) && addedLines.Add(line2)) parts.AppendLine(line2);
            }
            else if (_cachedVwapValue > 0 && !volumeBullish && priceAboveVwap)
            {
                anyConflict = true;
                string line1 = "", line2 = "";
                switch (ConflictLevel)
                {
                    case ConflictDescriptionLevel.Brief:
                        line1 = "\u26a0 Conflict detected"; break;
                    case ConflictDescriptionLevel.Detailed:
                        line1 = "\u26a0 VOLUME BEARISH BUT PRICE ABOVE VWAP (Distribution Risk)"; break;
                    case ConflictDescriptionLevel.VeryDetailed:
                        line1 = SeverityHigh + " VOLUME BEARISH BUT PRICE ABOVE VWAP";
                        line2 = "  \u2192 Possible distribution or reversal setup"; break;
                }
                if (!string.IsNullOrEmpty(line1) && addedLines.Add(line1)) parts.AppendLine(line1);
                if (!string.IsNullOrEmpty(line2) && addedLines.Add(line2)) parts.AppendLine(line2);
            }

            // Fake breakout conflict
            if (snap.IsFakeBreakout)
            {
                anyConflict = true;
                string line1 = "", line2 = "";
                switch (ConflictLevel)
                {
                    case ConflictDescriptionLevel.Brief:
                        line1 = "\u26a0 Fake breakout"; break;
                    case ConflictDescriptionLevel.Detailed:
                        line1 = "\u26a0 FAKE BREAKOUT DETECTED (High-Risk Entry)"; break;
                    case ConflictDescriptionLevel.VeryDetailed:
                        line1 = SeverityCritical + " FAKE BREAKOUT DETECTED";
                        line2 = "  \u2192 Avoid entries \u2014 wait for confirmation close"; break;
                }
                if (!string.IsNullOrEmpty(line1) && addedLines.Add(line1)) parts.AppendLine(line1);
                if (!string.IsNullOrEmpty(line2) && addedLines.Add(line2)) parts.AppendLine(line2);
            }

            // Low participation session — use wall-clock session (B2/B13 fix) so this warning
            // matches the session displayed in both dashboard panels.
            bool sessionHighParticipation;
            GetCurrentSessionInfo(out sessionHighParticipation);
            if (ShowLowParticipationWarning && !sessionHighParticipation)
            {
                anyConflict = true;
                string line1 = "", line2 = "";
                switch (ConflictLevel)
                {
                    case ConflictDescriptionLevel.Brief:
                        line1 = "\u26a0 Low participation"; break;
                    case ConflictDescriptionLevel.Detailed:
                        line1 = "\u26a0 LOW PARTICIPATION SESSION (Thin Market)"; break;
                    case ConflictDescriptionLevel.VeryDetailed:
                        line1 = SeverityModerate + " LOW PARTICIPATION SESSION";
                        line2 = "  \u2192 Off-hours: fills may be poor, spread elevated"; break;
                }
                if (!string.IsNullOrEmpty(line1) && addedLines.Add(line1)) parts.AppendLine(line1);
                if (!string.IsNullOrEmpty(line2) && addedLines.Add(line2)) parts.AppendLine(line2);
            }

            description = parts.ToString().TrimEnd();
            return anyConflict;
        }

        /// <summary>Returns true when the given position is at the top of the chart (stacks downward).
        /// Top positions: TopLeft, TopRight, CenterTop. Bottom positions: BottomLeft, BottomRight, CenterBottom.</summary>
        private static bool IsTopPosition(DashboardPositionType pos)
        {
            return pos == DashboardPositionType.TopLeft  ||
                   pos == DashboardPositionType.TopRight ||
                   pos == DashboardPositionType.CenterTop;
        }

        /// <summary>Resolve a DashboardPositionType to pixel X/Y coordinates with boundary clamping.
        /// Handles all 7 positions (Hidden returns off-screen coords). Clamps result to stay within
        /// the render area with a 60px time-axis buffer at the bottom and 8px edge padding.</summary>
        private static void GetDashboardPosition(DashboardPositionType pos,
            float rtW, float rtH, float panelW, float panelH,
            out float tx, out float ty)
        {
            const float margin              = 8f;
            const float timeAxisBuffer      = 60f;
            const float centerTopExtraMargin = 40f; // extra clearance below toolbar/title area when centred at top

            float centerX = (rtW - panelW) / 2f;

            switch (pos)
            {
                case DashboardPositionType.TopLeft:
                    tx = margin;                           ty = margin;                                        break;
                case DashboardPositionType.TopRight:
                    tx = rtW - panelW - margin;            ty = margin;                                        break;
                case DashboardPositionType.BottomLeft:
                    tx = margin;                           ty = rtH - panelH - timeAxisBuffer - margin;        break;
                case DashboardPositionType.BottomRight:
                    tx = rtW - panelW - margin;            ty = rtH - panelH - timeAxisBuffer - margin;        break;
                case DashboardPositionType.CenterTop:
                    tx = centerX;                          ty = margin + centerTopExtraMargin;                 break;
                case DashboardPositionType.CenterBottom:
                    tx = centerX;                          ty = rtH - panelH - timeAxisBuffer - margin;        break;
                case DashboardPositionType.Hidden:
                default:
                    tx = -9999f; ty = -9999f; return; // off-screen; caller guards on Hidden before calling
            }

            // Safety clamp — never let the panel be cut off at any edge
            if (tx < margin)                         tx = margin;
            if (tx + panelW > rtW - margin)          tx = rtW - panelW - margin;
            if (ty < margin)                          ty = margin;
            // Ensure the panel's bottom edge stays above the time axis + a small padding buffer
            if (ty + panelH > rtH - timeAxisBuffer)  ty = rtH - panelH - timeAxisBuffer - margin;
        }

        /// <summary>Render the Entry Mode dashboard — a focused trade setup view.</summary>
        /// <param name="stackOffset">Y offset applied to stack this panel below/above a same-corner panel.</param>
        /// <returns>The rendered panel height so the caller can advance its stack cursor.</returns>
        private float RenderEntryModeDashboard(ChartControl cc, ChartScale cs, float rtW, float rtH, float stackOffset)
        {
            var rt = RenderTarget;
            if (rt == null || dxEnhDashBgBrush == null || dxEnhDashFormat == null) return 0f;

            const float PadX              = 10f;
            const float PadY              = 8f;
            const float LineHeightMult    = 2.2f;
            const float PanelHeightBuffer = 4f;
            float       lineH  = EntryModeDashboardFontSize * LineHeightMult;
            float       panelW = 480f;
            // U13: pre-compute clamped panel width so WrapTextToLines uses the post-clamp usable width
            float clampedW;
            ClampTableY(0f, 0f, rtH, panelW, rtW, out clampedW);
            float usableW = (clampedW > 0 && clampedW < panelW ? clampedW : panelW) - PadX * 2;
            bool  signalExpired  = HasSignalExpired();
            bool  noSignal       = dashboardPrimarySignal == "NEUTRAL" || dashboardPrimarySignal == "No Data";
            bool  showNoSignal   = noSignal || signalExpired;
            string elapsedTime   = GetSignalElapsedTime();

            // Reset per-frame R/R gate
            _dashboardSkipDueToRR = false;

            var lines = new List<string>();
            lines.Add(string.Format("IQMainUltimate [{0}]", AssetClass.ToString().ToUpper()));

            if (showNoSignal)
            {
                lines.Add("No Active Signal");
                lines.Add("Waiting for signal detection...");
            }
            else
            {
                // Compute R/R early so we can gate the display before building lines
                double rr = 0;
                if (IsBullishSignal())
                {
                    double riskDist   = dashboardEntryPrice - dashboardStopPrice;
                    double rewardDist = dashboardTargetPrice - dashboardEntryPrice;
                    if (riskDist > 0 && rewardDist > 0) rr = rewardDist / riskDist;
                }
                else if (IsBearishSignal())
                {
                    double riskDist   = dashboardStopPrice - dashboardEntryPrice;
                    double rewardDist = dashboardEntryPrice - dashboardTargetPrice;
                    if (riskDist > 0 && rewardDist > 0) rr = rewardDist / riskDist;
                }
                else
                {
                    // Neutral: try both directions
                    if (dashboardEntryPrice > dashboardStopPrice && dashboardStopPrice > 0)
                    {
                        double riskDist   = dashboardEntryPrice - dashboardStopPrice;
                        double rewardDist = dashboardTargetPrice - dashboardEntryPrice;
                        if (riskDist > 0) rr = rewardDist / riskDist;
                    }
                }
                _dashboardSkipDueToRR = MinRiskRewardDisplay && rr > 0 && rr < MinRiskReward;

                // Signal line with elapsed time
                string signalBase = elapsedTime.Length > 0
                    ? string.Format("Signal:     {0} ({1})", dashboardPrimarySignal, elapsedTime)
                    : string.Format("Signal:     {0}", dashboardPrimarySignal);
                lines.Add(_dashboardSkipDueToRR ? signalBase + " \u2014 SKIP (R/R below min)" : signalBase);
                lines.Add(string.Format("Confidence: {0}%", dashboardConfidence));

                // Build TPO context strings for stop/target labels
                string stopLabel   = BuildStopLabel(dashboardStopPrice, dashboardEntryPrice);
                string targetLabel = BuildTargetLabel(dashboardTargetPrice, dashboardEntryPrice);

                lines.Add(string.Format("Entry:      {0}", Instrument.MasterInstrument.FormatPrice(dashboardEntryPrice)));
                lines.Add(stopLabel);
                lines.Add(targetLabel);

                if (_dashboardSkipDueToRR)
                    lines.Add(string.Format("R/R:        {0:F2}:1  (< min {1})", rr, MinRiskReward.ToString("0.0")));
                else
                    lines.Add(rr > 0 ? string.Format("R/R:        {0:F2}:1", rr) : "R/R:        N/A");

                // TPO Context section
                bool hasTpoContext = tpoCurrentPOC > 0 || tpoCurrentVAH > 0 || tpoCurrentVAL > 0;
                if (hasTpoContext)
                {
                    lines.Add("");
                    double closePx = _latestClose;
                    if (tpoCurrentVAH > 0 && tpoCurrentVAL > 0)
                    {
                        bool  atVAH = Math.Abs(closePx - tpoCurrentVAH) <= 3 * TickSize;
                        bool  atVAL = Math.Abs(closePx - tpoCurrentVAL) <= 3 * TickSize;
                        bool  inVA  = closePx >= tpoCurrentVAL && closePx <= tpoCurrentVAH;
                        string vaContext = atVAH ? "testing upper value" : atVAL ? "testing lower value" : inVA ? "inside value area" : closePx > tpoCurrentVAH ? "above value area" : "below value area";
                        lines.Add(string.Format("Context: Price {0}.", vaContext));
                    }
                    if (tpoCurrentIBHigh > 0 && tpoCurrentIBLow > 0)
                    {
                        double ibRange = tpoCurrentIBHigh - tpoCurrentIBLow;
                        double ibRatioP = ibRange > 0 ? (dayHigh - dayLow) / ibRange : 0;
                        lines.Add(string.Format("IB {0:F0}p, today {1:F1}x IB.",
                            ibRange / TickSize, ibRatioP));
                    }
                    if (tpoNearestNakedLevel != null && !tpoNearestNakedLevel.IsClosed)
                        lines.Add(string.Format("Nearest resistance: Naked {0} {1} @ {2}.",
                            tpoNearestNakedLevel.LevelType,
                            tpoNearestNakedLevel.SessionLabel,
                            Instrument.MasterInstrument.FormatPrice(tpoNearestNakedLevel.Price)));
                }

                // Stale/expiry warning
                if (signalIsStale)
                {
                    double elapsedMin  = (DateTime.Now - lastSignalDetectedTime).TotalMinutes;
                    double remaining   = SignalExpireMinutes - elapsedMin;
                    string expiryLine  = remaining <= 1.0
                        ? "\u23f3 Expiring imminently — wait for fresh signal"
                        : string.Format("\u23f3 Expires in {0}m — consider fresh signal", (int)Math.Ceiling(remaining));
                    lines.Add("");
                    lines.Add(string.Format("\u26a0 [STALE] Signal detected {0}", elapsedTime));
                    lines.Add(expiryLine);
                }

                // Conflict warnings — show here only if the Monitoring Dashboard is not already visible
                bool monitoringVisible = ShowMonitoringDashboard && MonitoringDashboardPosition != DashboardPositionType.Hidden;
                if (!monitoringVisible && dashboardConflictDetected && ShowConflictWarnings && !string.IsNullOrEmpty(dashboardConflictText))
                {
                    lines.Add("");
                    foreach (string part in dashboardConflictText.Split('\n'))
                    {
                        string trimmed = part.TrimEnd('\r');
                        if (string.IsNullOrEmpty(trimmed)) continue;
                        foreach (string wrapped in WrapTextToLines(trimmed, usableW, EntryModeDashboardFontSize))
                            lines.Add(wrapped);
                    }
                }
            }

            // Bug 1: deduplicate consecutive identical lines before rendering
            for (int i = lines.Count - 1; i > 0; i--)
                if (lines[i] == lines[i - 1])
                    lines.RemoveAt(i);

            float panelH = PadY * 2 + lines.Count * lineH + PanelHeightBuffer;

            float panelX, panelY;
            GetDashboardPosition(EntryModeDashboardPosition, rtW, rtH, panelW, panelH, out panelX, out panelY);
            // Apply stack offset: top positions stack downward, bottom positions stack upward
            if (IsTopPosition(EntryModeDashboardPosition)) panelY += stackOffset; else panelY -= stackOffset;

            // Clamp to keep panel within render area
            panelY = ClampTableY(panelY, panelH, rtH, panelW, rtW, out clampedW);
            if (clampedW > 0 && clampedW < panelW) panelW = clampedW;

            rt.FillRectangle(new SharpDX.RectangleF(panelX, panelY, panelW, panelH), dxEnhDashBgBrush);

            float ty    = panelY + PadY;
            bool  first = true;
            foreach (string line in lines)
            {
                SharpDX.Direct2D1.SolidColorBrush brush;
                if (_dashboardSkipDueToRR && _brushDimmedText != null)
                    brush = _brushDimmedText;
                else if (first)                                             brush = dxEnhDashHeaderBrush  ?? dxEnhDashTextBrush;
                else if (line.Contains("\u26a0") || line.Contains("["))    brush = dxEnhDashWarningBrush ?? dxEnhDashTextBrush;
                else if (line.StartsWith("Target"))                        brush = dxEnhDashGreenBrush   ?? dxEnhDashTextBrush;
                else if (line.StartsWith("Stop"))                          brush = dxEnhDashRedBrush     ?? dxEnhDashTextBrush;
                else                                                       brush = dxEnhDashTextBrush;

                if (brush != null && !string.IsNullOrEmpty(line))
                    rt.DrawText(line, dxEnhDashFormat,
                        new SharpDX.RectangleF(panelX + PadX, ty, panelW - PadX * 2, lineH), brush);

                ty += lineH;
                first = false;
            }
            return panelH;
        }

        /// <summary>Render the Monitoring Dashboard — compact market health view.</summary>
        /// <param name="stackOffset">Y offset applied to stack this panel below/above a same-corner panel.</param>
        /// <returns>The rendered panel height so the caller can advance its stack cursor.</returns>
        private float RenderMonitoringDashboard(ChartControl cc, ChartScale cs, float rtW, float rtH, float stackOffset)
        {
            var rt = RenderTarget;
            if (rt == null || dxEnhDashBgBrush == null || dxEnhMonFormat == null) return 0f;

            const float PadX              = 10f;
            const float PadY              = 8f;
            const float LineHeightMult    = 2.2f;
            const float PanelHeightBuffer = 4f;
            float       lineH  = MonitoringDashboardFontSize * LineHeightMult;
            float       panelW = 480f;
            // U13: pre-compute clamped panel width so WrapTextToLines uses the post-clamp usable width
            float clampedW;
            ClampTableY(0f, 0f, rtH, panelW, rtW, out clampedW);
            float usableW = (clampedW > 0 && clampedW < panelW ? clampedW : panelW) - PadX * 2;

            // B2/B13 fix: use wall-clock-based session so header and monitoring panel always agree.
            bool   monSessionHighParticipation;
            string session = GetCurrentSessionInfo(out monSessionHighParticipation);

            string vwapStatus = "VWAP: N/A";
            if (_cachedVwapValue > 0 && CurrentBar >= 50)
            {
                bool aboveVwap  = _latestClose > _cachedVwapValue;
                bool aboveEma50 = _cachedEma50Value > 0 && _latestClose > _cachedEma50Value;
                string vwapDir  = aboveVwap  ? "Price>VWAP"  : "Price<VWAP";
                string emaDir   = aboveEma50 ? "Price>EMA50" : "Price<EMA50";
                string align    = (aboveVwap == aboveEma50) ? "ALIGNED" : "DIVERGED";
                vwapStatus = string.Format("{0} | {1} [{2}]", vwapDir, emaDir, align);
            }

            string rangeInfo = adrValue > 0
                ? string.Format("ADR {0:F0}p  Today {1:F0}p  ({2:F0}%)",
                    adrValue / TickSize,
                    (dayHigh - dayLow) / TickSize,
                    (dayHigh - dayLow) / adrValue * 100.0)
                : "ADR: loading\u2026";

            string zoneInfo = string.Format("ULZ Active: {0}  Recovered: {1}",
                activeZoneCount, recoveredZoneCount);

            var lines = new List<string>
            {
                string.Format("IQMainUltimate [{0}]  Monitoring", AssetClass.ToString().ToUpper()),
                string.Format("Session: {0}  ({1})", session,
                    monSessionHighParticipation ? "High Participation" : "Low Participation"),
                rangeInfo,
                "Volume:",
                vwapStatus,
                zoneInfo
            };

            // ── TPO Section ───────────────────────────────────────────────────
            bool hasTpoData = tpoCurrentPOC > 0 || tpoCurrentVAH > 0 || tpoCurrentVAL > 0;
            if (hasTpoData)
            {
                lines.Add("");  // visual separator

                // POC / VAH / VAL line
                if (tpoCurrentPOC > 0 && tpoCurrentVAH > 0 && tpoCurrentVAL > 0)
                {
                    double vaRange = (tpoCurrentVAH - tpoCurrentVAL) / TickSize;
                    lines.Add(string.Format("\u25ba POC:{0} VAH:{1} VAL:{2}",
                        Instrument.MasterInstrument.FormatPrice(tpoCurrentPOC),
                        Instrument.MasterInstrument.FormatPrice(tpoCurrentVAH),
                        Instrument.MasterInstrument.FormatPrice(tpoCurrentVAL)));
                    lines.Add(string.Format("  VA range: {0:F0}p", vaRange));

                    // Price location relative to value area
                    double closePx = _latestClose;
                    string priceCtx;
                    if (Math.Abs(closePx - tpoCurrentVAH) <= 3 * TickSize)
                        priceCtx = string.Format("Price at VAH -{0:F0}t (testing upper value)", (tpoCurrentVAH - closePx) / TickSize);
                    else if (Math.Abs(closePx - tpoCurrentVAL) <= 3 * TickSize)
                        priceCtx = string.Format("Price at VAL +{0:F0}t (testing lower value)", (closePx - tpoCurrentVAL) / TickSize);
                    else if (closePx > tpoCurrentVAH)
                        priceCtx = string.Format("Price above VAH +{0:F0}t", (closePx - tpoCurrentVAH) / TickSize);
                    else if (closePx < tpoCurrentVAL)
                        priceCtx = string.Format("Price below VAL -{0:F0}t", (tpoCurrentVAL - closePx) / TickSize);
                    else
                        priceCtx = string.Format("Price inside VA, {0:F0}t from POC", Math.Abs(closePx - tpoCurrentPOC) / TickSize);
                    lines.Add("\u25ba " + priceCtx);
                }

                // Profile shape
                if (ShowProfileShape)
                {
                    string shapeDesc;
                    switch (tpoCurrentShape)
                    {
                        case TPOProfileShape.TrendDay:         shapeDesc = "TrendDay (D-shape)";          break;
                        case TPOProfileShape.DoubleDistribution: shapeDesc = "DoubleDistribution (b-shape)"; break;
                        case TPOProfileShape.Balanced:         shapeDesc = "Balanced (P-shape)";           break;
                        default:                               shapeDesc = "Normal (balanced bell)";       break;
                    }
                    lines.Add("\u25ba Profile: " + shapeDesc);
                }

                // Nearest naked POC above or below price
                if (ShowNakedLevels && tpoNearestNakedLevel != null && !tpoNearestNakedLevel.IsClosed)
                {
                    lines.Add(string.Format("\u25ba Naked {0} {1} @ {2} (unrecovered)",
                        tpoNearestNakedLevel.LevelType,
                        tpoNearestNakedLevel.SessionLabel,
                        Instrument.MasterInstrument.FormatPrice(tpoNearestNakedLevel.Price)));
                }
            }

            if (dashboardConflictDetected && ShowConflictWarnings && !string.IsNullOrEmpty(dashboardConflictText))
            {
                lines.Add(""); // section separator — adds visual gap before alerts
                foreach (string part in dashboardConflictText.Split('\n'))
                {
                    string trimmed = part.TrimEnd('\r');
                    if (string.IsNullOrEmpty(trimmed)) continue;
                    foreach (string wrapped in WrapTextToLines(trimmed, usableW, MonitoringDashboardFontSize))
                        lines.Add(wrapped);
                }
            }

            // Bug 1: deduplicate consecutive identical lines before rendering
            for (int i = lines.Count - 1; i > 0; i--)
                if (lines[i] == lines[i - 1])
                    lines.RemoveAt(i);

            float panelH = PadY * 2 + lines.Count * lineH + PanelHeightBuffer;

            float panelX, panelY;
            GetDashboardPosition(MonitoringDashboardPosition, rtW, rtH, panelW, panelH, out panelX, out panelY);
            // Apply stack offset: top positions stack downward, bottom positions stack upward
            if (IsTopPosition(MonitoringDashboardPosition)) panelY += stackOffset; else panelY -= stackOffset;

            // Clamp to keep panel within render area
            panelY = ClampTableY(panelY, panelH, rtH, panelW, rtW, out clampedW);
            if (clampedW > 0 && clampedW < panelW) panelW = clampedW;

            rt.FillRectangle(new SharpDX.RectangleF(panelX, panelY, panelW, panelH), dxEnhDashBgBrush);

            float ty    = panelY + PadY;
            bool  first = true;
            float volumeBarY = -1f;
            foreach (string line in lines)
            {
                if (line == "Volume:")
                    volumeBarY = ty;

                SharpDX.Direct2D1.SolidColorBrush brush;
                if      (first)                                            brush = dxEnhDashHeaderBrush  ?? dxEnhDashTextBrush;
                else if (line.Contains("\u26a0") || line.Contains("["))   brush = dxEnhDashWarningBrush ?? dxEnhDashTextBrush;
                else                                                       brush = dxEnhDashTextBrush;

                if (brush != null && !string.IsNullOrEmpty(line))
                    rt.DrawText(line, dxEnhMonFormat,
                        new SharpDX.RectangleF(panelX + PadX, ty, panelW - PadX * 2, lineH), brush);

                ty += lineH;
                first = false;
            }

            // Draw graphical volume bar on the "Volume:" line
            if (volumeBarY >= 0)
            {
                const float BarWidth  = 120f;
                const float BarHeight = 12f;
                const int   VolumeLabelChars = 8; // character count of "Volume: "
                float labelW = MonitoringDashboardFontSize * 0.60f * VolumeLabelChars;
                float barX   = panelX + PadX + labelW;
                float barY   = volumeBarY + (lineH - BarHeight) / 2f;
                DrawVolumeBar(rt, barX, barY, BarWidth, BarHeight, sessionBuyVol, sessionSellVol);
            }
            return panelH;
        }

        /// <summary>Draw entry (white solid), stop (red dashed), target (green dashed) lines on chart.</summary>
        private void DrawStopTargetLinesOnChart(ChartControl cc, ChartScale cs)
        {
            var rt = RenderTarget;
            if (rt == null) return;

            // U12: hide lines when there is no actionable signal
            string sig = dashboardPrimarySignal;
            if (sig == "NEUTRAL" || sig == "No Data" || HasSignalExpired() || _dashboardSkipDueToRR) return;
            if (dashboardEntryPrice <= 0 || dashboardStopPrice <= 0 || dashboardTargetPrice <= 0) return;

            float rtW = rt.Size.Width;

            if (dashboardEntryPrice > 0 && dxEntryLineBrush != null)
            {
                float y = cs.GetYByValue(dashboardEntryPrice);
                DrawStyledLine(0, y, rtW, y, dxEntryLineBrush, 1, IQMLineStyle.Solid);
            }
            if (dashboardStopPrice > 0 && dxStopLineBrush != null)
            {
                float y = cs.GetYByValue(dashboardStopPrice);
                DrawStyledLine(0, y, rtW, y, dxStopLineBrush, 1, IQMLineStyle.Dashed);
            }
            if (dashboardTargetPrice > 0 && dxTargetLineBrush != null)
            {
                float y = cs.GetYByValue(dashboardTargetPrice);
                DrawStyledLine(0, y, rtW, y, dxTargetLineBrush, 1, IQMLineStyle.Dashed);
            }
        }

        /// <summary>Render the Main Dashboard — original IQMainGPU stats (buy/sell vol, delta, signals, ADR, session).</summary>
        /// <param name="stackOffset">Y offset applied to stack this panel below/above a same-corner panel.</param>
        /// <returns>The rendered panel height so the caller can advance its stack cursor.</returns>
        private float RenderMainDashboard(ChartControl cc, ChartScale cs, float rtW, float rtH, float stackOffset)
        {
            var rt = RenderTarget;
            if (rt == null || dxEnhDashBgBrush == null || dxMainDashFormat == null) return 0f;

            const float PadX              = 10f;
            const float PadY              = 8f;
            const float LineHeightMult    = 2.2f;
            const float PanelHeightBuffer = 4f;
            float       lineH  = MainDashboardFontSize * LineHeightMult;
            float       panelW = 480f;

            // B2/B13 fix: use wall-clock session so the header never shows "—" on 30m/60m
            // bars whose open time precedes the session window, and so header always matches
            // the monitoring panel (both call the same GetCurrentSessionInfo).
            bool   mainDashHighParticipation;
            string activeSessName = GetCurrentSessionInfo(out mainDashHighParticipation);

            string assetTag = AssetClass.ToString().ToUpper();
            string adrStr    = adrValue > 0 ? string.Format("ADR {0:F0}p", adrValue / TickSize) : "ADR N/A";
            string awrStr    = awrValue > 0 ? string.Format("AWR {0:F0}p", awrValue / TickSize) : "AWR N/A";
            string amrStr    = amrValue > 0 ? string.Format("AMR {0:F0}p", amrValue / TickSize) : "AMR N/A";
            string rangeLine = string.Format("{0}  {1}  {2}", adrStr, awrStr, amrStr);
            string[] lines = {
                string.Format("IQMainUltimate [{0}]  Session: {1}", assetTag, activeSessName),
                dashLine2,
                dashLine3,
                dashLine4,
                dashLine5,
                dashLine6,
                dashLine7,
                rangeLine,
                EnableLevel2 ? l2StatusText : ""
            };

            int   nonEmpty = 0;
            for (int i = 0; i < lines.Length; i++)
                if (!string.IsNullOrEmpty(lines[i])) nonEmpty++;
            float panelH   = PadY * 2 + nonEmpty * lineH + PanelHeightBuffer;

            float panelX, panelY;
            GetDashboardPosition(MainDashboardPosition, rtW, rtH, panelW, panelH, out panelX, out panelY);
            // Apply stack offset: top positions stack downward, bottom positions stack upward
            if (IsTopPosition(MainDashboardPosition)) panelY += stackOffset; else panelY -= stackOffset;

            // Clamp to keep panel within render area
            float clampedW;
            panelY = ClampTableY(panelY, panelH, rtH, panelW, rtW, out clampedW);
            if (clampedW > 0 && clampedW < panelW) panelW = clampedW;

            rt.FillRectangle(new SharpDX.RectangleF(panelX, panelY, panelW, panelH), dxEnhDashBgBrush);

            float ty    = panelY + PadY;
            bool  first = true;
            foreach (string line in lines)
            {
                if (string.IsNullOrEmpty(line)) continue;
                SharpDX.Direct2D1.SolidColorBrush textBrush;
                if (first)
                    textBrush = dxEnhDashHeaderBrush ?? dxEnhDashTextBrush;
                else if (line.Contains("FAKE") || line.Contains("Fake"))
                    textBrush = dxEnhDashWarningBrush ?? dxEnhDashTextBrush;
                else
                    textBrush = dxEnhDashTextBrush;
                if (textBrush != null)
                    rt.DrawText(line, dxMainDashFormat,
                        new SharpDX.RectangleF(panelX + PadX, ty, panelW - PadX * 2, lineH), textBrush);
                ty += lineH;
                first = false;
            }
            return panelH;
        }

        /// <summary>Split a single text line into multiple display lines that each fit within
        /// <paramref name="usableWidth"/> pixels, using Consolas character-width approximation
        /// (charWidth ≈ fontSize * 0.60). Minimum 20 chars per line to prevent degenerate output.</summary>
        private static List<string> WrapTextToLines(string text, float usableWidth, float fontSize)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;

            int charsPerLine = Math.Max(20, (int)(usableWidth / (fontSize * 0.60f))); // 0.60f: Consolas char width ≈ 60% of font size; 20: minimum to prevent degenerate single-word-per-line output
            string[] words   = text.Split(' ');
            var currentLine  = new System.Text.StringBuilder();

            foreach (string word in words)
            {
                if (currentLine.Length == 0)
                {
                    currentLine.Append(word);
                }
                else if (currentLine.Length + 1 + word.Length <= charsPerLine)
                {
                    currentLine.Append(' ');
                    currentLine.Append(word);
                }
                else
                {
                    result.Add(currentLine.ToString());
                    currentLine.Clear();
                    currentLine.Append(word);
                }
            }

            if (currentLine.Length > 0)
                result.Add(currentLine.ToString());

            return result;
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Helper methods — pivots, sessions, DST

        private void ComputePivots()
        {
            if (prevDayHigh == 0 && prevDayLow == 0) return;

            double h = prevDayHigh, l = prevDayLow, c = prevDayClose;
            double pp = (h + l + c) / 3.0;
            double r1 = 2 * pp - l;
            double s1 = 2 * pp - h;
            double r2 = pp - s1 + r1;
            double s2 = pp - r1 + s1;
            double r3 = 2 * pp + h - 2 * l;
            double s3 = 2 * pp - (2 * h - l);

            currentPivot = new PivotSnapshot
            {
                PP = pp, R1 = r1, R2 = r2, R3 = r3,
                S1 = s1, S2 = s2, S3 = s3,
                M0 = (s2 + s3) / 2.0,
                M1 = (s1 + s2) / 2.0,
                M2 = (pp + s1) / 2.0,
                M3 = (pp + r1) / 2.0,
                M4 = (r1 + r2) / 2.0,
                M5 = (r2 + r3) / 2.0,
                StartBarIndex = CurrentBar
            };
        }

        private void GetSessionEtTimes(int sessionId, DateTime barEt, out DateTime start, out DateTime end)
        {
            DateTime today     = barEt.Date;
            DateTime yesterday = today.AddDays(-1);
            switch (sessionId)
            {
                case 0: start = today.AddHours(3);                end = today.AddHours(11).AddMinutes(30); break; // London
                case 1: start = today.AddHours(9).AddMinutes(30); end = today.AddHours(16);                break; // NY
                case 2: start = yesterday.AddHours(19);           end = today.AddHours(4);                 break; // Tokyo
                case 3: start = yesterday.AddHours(21);           end = today.AddHours(4);                 break; // Hong Kong
                case 4: start = yesterday.AddHours(17);           end = today.AddHours(2);                 break; // Sydney
                case 5: start = today.AddHours(3);                end = today.AddHours(4);                 break; // EU Brinks
                case 6: start = today.AddHours(9).AddMinutes(30); end = today.AddHours(10).AddMinutes(30); break; // US Brinks
                case 7: start = today.AddHours(2);                end = today.AddHours(11);                break; // Frankfurt
                default: start = today;                            end = today;                             break;
            }
        }

        private bool IsSessionEnabled(int id)
        {
            switch (id)
            {
                case 0: return ShowLondon;
                case 1: return ShowNewYork;
                case 2: return ShowTokyo;
                case 3: return ShowHongKong;
                case 4: return ShowSydney;
                case 5: return ShowEuBrinks;
                case 6: return ShowUsBrinks;
                case 7: return ShowFrankfurt;
                default: return false;
            }
        }

        private bool SessionShowOpeningRange(int id)
        {
            switch (id)
            {
                case 0: return LondonShowOpeningRange;
                case 1: return NewYorkShowOpeningRange;
                case 2: return TokyoShowOpeningRange;
                case 3: return HongKongShowOpeningRange;
                case 4: return SydneyShowOpeningRange;
                case 5: return EuBrinksShowOpeningRange;
                case 6: return UsBrinksShowOpeningRange;
                case 7: return FrankfurtShowOpeningRange;
                default: return false;
            }
        }

        private string GetSessionLabel(int id)
        {
            switch (id)
            {
                case 0: return LondonLabel;
                case 1: return NewYorkLabel;
                case 2: return TokyoLabel;
                case 3: return HongKongLabel;
                case 4: return SydneyLabel;
                case 5: return EuBrinksLabel;
                case 6: return UsBrinksLabel;
                case 7: return FrankfurtLabel;
                default: return "";
            }
        }

        private bool SessionShowLabel(int id)
        {
            switch (id)
            {
                case 0: return LondonShowLabel;
                case 1: return NewYorkShowLabel;
                case 2: return TokyoShowLabel;
                case 3: return HongKongShowLabel;
                case 4: return SydneyShowLabel;
                case 5: return EuBrinksShowLabel;
                case 6: return UsBrinksShowLabel;
                case 7: return FrankfurtShowLabel;
                default: return false;
            }
        }

        private void UpdateSessions(DateTime barEt)
        {
            for (int id = 0; id < 8; id++)
            {
                if (!IsSessionEnabled(id)) continue;

                DateTime sStart, sEnd;
                GetSessionEtTimes(id, barEt, out sStart, out sEnd);
                bool inSession = barEt >= sStart && barEt < sEnd;

                if (inSession)
                {
                    if (activeSessions[id] == null || activeSessions[id].StartTime != sStart)
                    {
                        var box = new SessionBox
                        {
                            StartTime     = sStart,
                            EndTime       = sEnd,
                            StartBarIndex = CurrentBar,
                            EndBarIndex   = CurrentBar,
                            SessionHigh   = High[0],
                            SessionLow    = Low[0],
                            IsComplete    = false,
                            SessionId     = id
                        };
                        activeSessions[id] = box;
                        lock (_sessionLock)
                        {
                            if (sessionBoxes.Count >= 200) sessionBoxes.RemoveAt(0);
                            sessionBoxes.Add(box);
                        }
                    }
                    else
                    {
                        var box = activeSessions[id];
                        if (High[0] > box.SessionHigh) box.SessionHigh = High[0];
                        if (Low[0]  < box.SessionLow)  box.SessionLow  = Low[0];
                        box.EndBarIndex = CurrentBar;
                    }
                }
                else
                {
                    if (activeSessions[id] != null && !activeSessions[id].IsComplete)
                    {
                        activeSessions[id].IsComplete = true;
                        activeSessions[id] = null;
                    }
                }
            }
        }

        private void UpdateEthAndRthOpens(DateTime barEt)
        {
            DateTime today = barEt.Date;

            // ── ETH Daily Open — 18:00 ET (6 PM ET = CME Globex daily reset) ─
            DateTime ethStart = GetEthSessionStartEt(barEt);
            DateTime ethEnd   = ethStart.AddHours(24);
            if (currentEthOpenEntry == null || currentEthOpenEntry.SessionStart != ethStart)
            {
                if (currentEthOpenEntry != null)
                    currentEthOpenEntry.EndBarIndex = CurrentBar - 1;
                currentEthOpenEntry = new SessionOpenEntry
                {
                    OpenPrice     = Open[0],
                    StartBarIndex = CurrentBar,
                    EndBarIndex   = CurrentBar,
                    SessionStart  = ethStart,
                    SessionEnd    = ethEnd
                };
                lock (_sessionLock)
                {
                    if (ethOpenEntries.Count >= 200) ethOpenEntries.RemoveAt(0);
                    ethOpenEntries.Add(currentEthOpenEntry);
                }
            }
            else
                currentEthOpenEntry.EndBarIndex = CurrentBar;

            // ── Asia Open — 19:00 ET prev → 04:00 ET (Tokyo session anchor) ─
            DateTime asiaStart = today.AddDays(-1).AddHours(19);
            DateTime asiaEnd   = today.AddHours(4);
            bool inAsia = barEt >= asiaStart && barEt < asiaEnd;
            UpdateRthEntry(inAsia, asiaStart, asiaEnd, ref currentRthAsiaEntry, rthAsiaOpenEntries);

            // ── London Open — 03:00 ET → 11:30 ET ────────────────────────────
            DateTime lndStart = today.AddHours(3);
            DateTime lndEnd   = today.AddHours(11).AddMinutes(30);
            bool inLondon = barEt >= lndStart && barEt < lndEnd;
            UpdateRthEntry(inLondon, lndStart, lndEnd, ref currentRthLondonEntry, rthLondonOpenEntries);

            // ── US Open — 09:30 ET → 16:00 ET ────────────────────────────────
            DateTime usStart = today.AddHours(9).AddMinutes(30);
            DateTime usEnd   = today.AddHours(16);
            bool inUs = barEt >= usStart && barEt < usEnd;
            UpdateRthEntry(inUs, usStart, usEnd, ref currentRthUsEntry, rthUsOpenEntries);
        }

        private static DateTime GetEthSessionStartEt(DateTime barEt)
        {
            // ETH session = 18:00 ET (6 PM) to 18:00 ET next day (CME Globex daily reset)
            DateTime today18 = barEt.Date.AddHours(18);
            if (barEt >= today18) return today18;
            return barEt.Date.AddDays(-1).AddHours(18);
        }

        private void UpdateRthEntry(bool inSession, DateTime sessionStart, DateTime sessionEnd,
            ref SessionOpenEntry currentEntry, List<SessionOpenEntry> entryList)
        {
            if (!inSession) return;

            if (currentEntry == null || currentEntry.SessionStart != sessionStart)
            {
                if (currentEntry != null)
                    currentEntry.EndBarIndex = CurrentBar - 1;
                currentEntry = new SessionOpenEntry
                {
                    OpenPrice     = Open[0],
                    StartBarIndex = CurrentBar,
                    EndBarIndex   = CurrentBar,
                    SessionStart  = sessionStart,
                    SessionEnd    = sessionEnd
                };
                lock (_sessionLock)
                {
                    if (entryList.Count >= 200) entryList.RemoveAt(0);
                    entryList.Add(currentEntry);
                }
            }
            else
                currentEntry.EndBarIndex = CurrentBar;
        }

        private void CheckPivotCrossAlert(double prev, double curr, double level, string name)
        {
            if (level == 0) return;
            bool crossed = (prev < level && curr >= level) || (prev > level && curr <= level);
            if (crossed)
                Alert("IQMU_Pivot_" + name, Priority.Medium,
                    "IQMainUltimate: Price crossed " + name + " @ " + level.ToString("F5"),
                    NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10,
                    Brushes.Yellow, Brushes.Black);
        }

        private void UpdateVwap(DateTime barEt)
        {
            if (vwapEthData == null || vwapRthData == null) return;

            double tp  = (High[0] + Low[0] + Close[0]) / 3.0;
            double vol = Volume[0];

            // ── ETH-anchored VWAP (resets at 18:00 ET = CME Globex daily open) ─
            DateTime ethStart = GetEthSessionStartEt(barEt);
            if (vwapEthSessionStart != ethStart)
            {
                vwapEthSessionStart      = ethStart;
                vwapEthCumulativePV      = 0;
                vwapEthCumulativeVolume  = 0;
                vwapEthCumulativeTPVSq   = 0;
            }

            if (vol > 0)
            {
                vwapEthCumulativePV     += tp * vol;
                vwapEthCumulativeVolume += vol;
                vwapEthCumulativeTPVSq  += tp * tp * vol;
            }

            double ethVwap     = vwapEthCumulativeVolume > 0 ? vwapEthCumulativePV / vwapEthCumulativeVolume : tp;
            double ethVariance = vwapEthCumulativeVolume > 0
                ? Math.Max(0, (vwapEthCumulativeTPVSq / vwapEthCumulativeVolume) - (ethVwap * ethVwap))
                : 0;
            double ethStdDev   = Math.Sqrt(ethVariance);

            if (vwapEthData.Count >= 1000) vwapEthData.RemoveAt(0);
            vwapEthData.Add(new VwapBarData
            {
                Vwap       = ethVwap,
                StdDev     = ethStdDev,
                Band1Upper = ethVwap + ethStdDev,
                Band1Lower = ethVwap - ethStdDev,
                Band2Upper = ethVwap + 2 * ethStdDev,
                Band2Lower = ethVwap - 2 * ethStdDev,
                Band3Upper = ethVwap + 3 * ethStdDev,
                Band3Lower = ethVwap - 3 * ethStdDev
            });

            // ── RTH-anchored VWAP (resets at 09:30 ET = US cash open) ────────
            DateTime rthStart = barEt.Date.AddHours(9).AddMinutes(30);
            bool     inRth    = barEt >= rthStart;

            if (inRth)
            {
                if (vwapRthSessionStart != rthStart)
                {
                    vwapRthSessionStart      = rthStart;
                    vwapRthCumulativePV      = 0;
                    vwapRthCumulativeVolume  = 0;
                    vwapRthCumulativeTPVSq   = 0;
                }

                if (vol > 0)
                {
                    vwapRthCumulativePV     += tp * vol;
                    vwapRthCumulativeVolume += vol;
                    vwapRthCumulativeTPVSq  += tp * tp * vol;
                }

                double rthVwap     = vwapRthCumulativeVolume > 0 ? vwapRthCumulativePV / vwapRthCumulativeVolume : tp;
                double rthVariance = vwapRthCumulativeVolume > 0
                    ? Math.Max(0, (vwapRthCumulativeTPVSq / vwapRthCumulativeVolume) - (rthVwap * rthVwap))
                    : 0;
                double rthStdDev   = Math.Sqrt(rthVariance);

                if (vwapRthData.Count >= 1000) vwapRthData.RemoveAt(0);
                vwapRthData.Add(new VwapBarData
                {
                    Vwap       = rthVwap,
                    StdDev     = rthStdDev,
                    Band1Upper = rthVwap + rthStdDev,
                    Band1Lower = rthVwap - rthStdDev,
                    Band2Upper = rthVwap + 2 * rthStdDev,
                    Band2Lower = rthVwap - 2 * rthStdDev,
                    Band3Upper = rthVwap + 3 * rthStdDev,
                    Band3Lower = rthVwap - 3 * rthStdDev
                });
            }
            else
            {
                // Outside RTH — store null placeholder to keep list aligned with CurrentBar
                if (vwapRthData.Count >= 1000) vwapRthData.RemoveAt(0);
                vwapRthData.Add(null);
            }
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region TPO / Market Profile calculation methods (IQMainUltimate additions)

        /// <summary>
        /// Main TPO session update method. Called once per bar (first tick) in OnBarUpdate.
        /// For each active session window (London, NY, Tokyo, etc.) it:
        ///  1. Creates or continues a TPOSession, accumulating volume-at-price
        ///  2. Updates Initial Balance if within the first 60 minutes
        ///  3. On session close: calculates POC, Value Area, profile shape, and creates naked levels
        ///  4. Keeps tpoCurrentPOC/VAH/VAL/IB fields fresh for dashboards and stop/target logic
        ///  5. Runs naked-level touch checks and age cleanup
        /// </summary>
        private void UpdateTPOSession(DateTime barEt)
        {
            if (tpoSessions == null) return;

            double tickSz = TickSize > 0 ? TickSize : 0.25;
            double binSz  = tickSz * Math.Max(1, TPOBinSizeMultiplier);

            double barVol = Volume[0];
            double tp     = (High[0] + Low[0] + Close[0]) / 3.0;  // typical price for volume bucketing
            double roundedTp = Math.Round(tp / binSz) * binSz;

            // Track which session is most relevant for dashboard metrics
            // (prefer NY = id 1, then London = id 0, then others)
            TPOSession bestActive = null;
            int bestPriority = int.MaxValue;
            int[] sessionPriority = { 1, 0, 2, 3, 4, 5, 6, 7 }; // NY first, then London, then others

            for (int id = 0; id < 8; id++)
            {
                if (!IsSessionEnabled(id)) continue;

                DateTime sStart, sEnd;
                GetSessionEtTimes(id, barEt, out sStart, out sEnd);
                bool inSession = barEt >= sStart && barEt < sEnd;

                if (inSession)
                {
                    // Create or continue active TPO session
                    if (activeTPOSessions[id] == null || activeTPOSessions[id].StartTime != sStart)
                    {
                        // New session: archive old one if any
                        if (activeTPOSessions[id] != null && !activeTPOSessions[id].IsComplete)
                        {
                            FinalizeTPOSession(activeTPOSessions[id]);
                        }
                        var tpoSess = new TPOSession(id, sStart, sEnd, CurrentBar);
                        activeTPOSessions[id] = tpoSess;
                        lock (_sessionLock)
                        {
                            if (tpoSessions.Count >= 200) tpoSessions.RemoveAt(0);
                            tpoSessions.Add(tpoSess);
                        }
                    }

                    var sess = activeTPOSessions[id];
                    sess.EndBarIndex = CurrentBar;

                    // Accumulate volume at rounded typical price
                    if (barVol > 0)
                    {
                        double bucket = Math.Round(roundedTp / binSz) * binSz;
                        if (sess.VolumeProfile.ContainsKey(bucket))
                            sess.VolumeProfile[bucket] += barVol;
                        else
                            sess.VolumeProfile[bucket]  = barVol;
                    }

                    // Initial Balance: first 60 minutes of session
                    UpdateInitialBalance(sess, barEt);

                    // Calculate POC/VA from current profile
                    if (sess.VolumeProfile.Count > 0)
                    {
                        CalculatePOC(sess);
                        CalculateValueArea(sess);
                        sess.ProfileShape = ClassifyProfileShape(sess);
                    }

                    // Track for dashboard priority
                    int priority = Array.IndexOf(sessionPriority, id);
                    if (priority < 0) priority = 99;
                    if (priority < bestPriority)
                    {
                        bestPriority = priority;
                        bestActive   = sess;
                    }
                }
                else
                {
                    // Session ended: finalize it
                    if (activeTPOSessions[id] != null && !activeTPOSessions[id].IsComplete)
                    {
                        FinalizeTPOSession(activeTPOSessions[id]);
                        activeTPOSessions[id] = null;
                    }
                }
            }

            // Update cached dashboard metrics from best active session
            if (bestActive != null)
            {
                tpoCurrentPOC = bestActive.POCPrice;
                tpoCurrentVAH = bestActive.ValueAreaHigh;
                tpoCurrentVAL = bestActive.ValueAreaLow;

                // Bug 3: apply IB range sanity guard — suppress IB values if range exceeds 3× ADR
                double ibHigh = bestActive.IBHigh > double.MinValue ? bestActive.IBHigh : 0;
                double ibLow  = bestActive.IBLow  < double.MaxValue ? bestActive.IBLow  : 0;
                double maxIBRange = adrValue > 0 ? adrValue * 3.0 : double.MaxValue;
                if (ibHigh > 0 && ibLow > 0 && (ibHigh - ibLow) > maxIBRange)
                {
                    ibHigh = 0;
                    ibLow  = 0;
                }
                tpoCurrentIBHigh    = ibHigh;
                tpoCurrentIBLow     = ibLow;
                tpoCurrentIBHighExt = (ibHigh > 0 && ibLow > 0) ? bestActive.IBHighExtension : 0;
                tpoCurrentIBLowExt  = (ibHigh > 0 && ibLow > 0) ? bestActive.IBLowExtension  : 0;

                tpoCurrentShape        = bestActive.ProfileShape;
                tpoCurrentSessionLabel = GetSessionLabel(bestActive.SessionId);
            }

            // Check naked level touch and cleanup old levels
            UpdateNakedLevels();
            CleanupOldNakedLevels();

            // Update nearest naked level above price for dashboard
            UpdateNearestNakedLevel();
        }

        /// <summary>
        /// Finalize a TPO session when it closes:
        ///  • Compute final POC, VA, IB, Profile Shape
        ///  • Store as previousDayTPO if it is a London or NY session
        ///  • Create naked levels for POC/VAH/VAL
        /// </summary>
        private void FinalizeTPOSession(TPOSession sess)
        {
            if (sess == null || sess.IsComplete) return;
            sess.IsComplete = true;

            if (sess.VolumeProfile.Count > 0)
            {
                CalculatePOC(sess);
                CalculateValueArea(sess);
                CalculateIBExtensions(sess);
                sess.ProfileShape = ClassifyProfileShape(sess);
            }

            // Store as previous day TPO (only London=0 or NY=1 sessions used as reference)
            if (sess.SessionId == 0 || sess.SessionId == 1)
                previousDayTPO = sess;

            // Create naked levels from completed session
            CreateNakedTPOLevels(sess);
        }

        /// <summary>
        /// Calculate the Point of Control (highest-volume price level) for a session.
        /// Sets sess.POCPrice and sess.POCVolume.
        /// </summary>
        private void CalculatePOC(TPOSession sess)
        {
            if (sess.VolumeProfile == null || sess.VolumeProfile.Count == 0) return;

            double maxVol  = 0;
            double pocPrice = 0;

            foreach (var kv in sess.VolumeProfile)
            {
                if (kv.Value > maxVol)
                {
                    maxVol   = kv.Value;
                    pocPrice = kv.Key;
                }
            }

            sess.POCPrice  = pocPrice;
            sess.POCVolume = maxVol;
        }

        /// <summary>
        /// Calculate Value Area High and Low — the price range containing 70% of session volume.
        /// Algorithm: start from POC, expand outward (higher then lower) until 70% is captured.
        /// Sets sess.ValueAreaHigh and sess.ValueAreaLow.
        /// </summary>
        private void CalculateValueArea(TPOSession sess)
        {
            if (sess.VolumeProfile == null || sess.VolumeProfile.Count == 0 || sess.POCPrice == 0) return;

            double totalVol  = sess.VolumeProfile.Values.Sum();
            double targetVol = totalVol * (sess.ValueAreaPct / 100.0);

            // Sort price levels
            var sorted = sess.VolumeProfile.Keys.OrderBy(p => p).ToList();
            int pocIdx = sorted.IndexOf(sess.POCPrice);
            if (pocIdx < 0) return;

            int lo = pocIdx, hi = pocIdx;
            double accumulated = sess.POCVolume;

            while (accumulated < targetVol)
            {
                bool canGoHi = hi + 1 < sorted.Count;
                bool canGoLo = lo - 1 >= 0;

                if (!canGoHi && !canGoLo) break;

                double hiVol = canGoHi ? sess.VolumeProfile[sorted[hi + 1]] : 0;
                double loVol = canGoLo ? sess.VolumeProfile[sorted[lo - 1]] : 0;

                // Expand toward higher volume side (standard VA methodology)
                if (hiVol >= loVol && canGoHi)
                    accumulated += sess.VolumeProfile[sorted[++hi]];
                else if (canGoLo)
                    accumulated += sess.VolumeProfile[sorted[--lo]];
                else
                    accumulated += sess.VolumeProfile[sorted[++hi]];
            }

            sess.ValueAreaHigh = sorted[hi];
            sess.ValueAreaLow  = sorted[lo];
        }

        /// <summary>
        /// Update the Initial Balance for a session using the first 60 minutes.
        /// Tracks IB High/Low and sets IBComplete when the IB window closes.
        /// </summary>
        private void UpdateInitialBalance(TPOSession sess, DateTime barEt)
        {
            if (sess.IBComplete) return;

            DateTime ibEnd = sess.StartTime.AddMinutes(60);

            if (barEt < ibEnd)
            {
                // Still within the IB window — extend range
                if (High[0] > sess.IBHigh || sess.IBHigh == double.MinValue) sess.IBHigh = High[0];
                if (Low[0]  < sess.IBLow  || sess.IBLow  == double.MaxValue) sess.IBLow  = Low[0];
                sess.IBEndBarIndex = CurrentBar;
            }
            else
            {
                // IB window closed — compute range and extensions
                if (sess.IBHigh > double.MinValue && sess.IBLow < double.MaxValue)
                    sess.IBRange = sess.IBHigh - sess.IBLow;
                sess.IBComplete = true;
            }
        }

        /// <summary>
        /// Compute IB extension targets once IB range is known.
        /// IBHighExtension = IBHigh + IBRange × 1.0
        /// IBLowExtension  = IBLow  - IBRange × 1.0
        /// </summary>
        private void CalculateIBExtensions(TPOSession sess)
        {
            if (sess.IBRange <= 0 || sess.IBHigh <= double.MinValue || sess.IBLow >= double.MaxValue) return;
            sess.IBHighExtension = sess.IBHigh + sess.IBRange * 1.0;
            sess.IBLowExtension  = sess.IBLow  - sess.IBRange * 1.0;
        }

        /// <summary>
        /// Classify the session volume distribution profile shape.
        ///   TrendDay (D-shape)  : single dominant cluster, skewed close (> 60% in upper or lower 40%)
        ///   DoubleDistribution  : two distinct peaks separated by a valley (> 20% vol gap in middle)
        ///   Balanced (P-shape)  : single peak but close near opposite end from most volume
        ///   Normal              : balanced bell curve (default)
        /// </summary>
        private TPOProfileShape ClassifyProfileShape(TPOSession sess)
        {
            if (sess.VolumeProfile == null || sess.VolumeProfile.Count < 5)
                return TPOProfileShape.Normal;

            var sorted = sess.VolumeProfile.OrderBy(kv => kv.Key).ToList();
            int count  = sorted.Count;
            if (count < 3) return TPOProfileShape.Normal;

            double totalVol = sorted.Sum(kv => kv.Value);
            if (totalVol <= 0) return TPOProfileShape.Normal;

            // Split into thirds: bottom third, middle third, top third
            int third = count / 3;
            double botVol = sorted.Take(third).Sum(kv => kv.Value);
            double topVol = sorted.Skip(count - third).Take(third).Sum(kv => kv.Value);
            double midVol = totalVol - botVol - topVol;

            // TrendDay: top or bottom third has > 55% of volume
            if (topVol / totalVol > 0.55 || botVol / totalVol > 0.55)
                return TPOProfileShape.TrendDay;

            // DoubleDistribution: middle third has < 20% of volume (bimodal distribution)
            if (midVol / totalVol < 0.20)
                return TPOProfileShape.DoubleDistribution;

            // Balanced: top and bottom thirds are within 20% of each other, middle dominant
            if (midVol / totalVol > 0.45 && Math.Abs(topVol - botVol) / totalVol < 0.20)
                return TPOProfileShape.Balanced;

            return TPOProfileShape.Normal;
        }

        /// <summary>
        /// Create naked TPO levels from a completed session.
        /// Adds POC, VAH, VAL as naked levels if they are valid and list isn't full.
        /// Oldest levels are pruned when MaxNakedLevels is exceeded.
        /// </summary>
        private void CreateNakedTPOLevels(TPOSession sess)
        {
            if (!ShowNakedLevels || nakedTPOLevels == null) return;
            if (sess.POCPrice <= 0 && sess.ValueAreaHigh <= 0) return;

            string sessLabel = GetSessionLabel(sess.SessionId);
            DateTime sessDate = sess.StartTime;

            // Add POC
            if (sess.POCPrice > 0)
                TryAddNakedLevel(sess.POCPrice, "POC", sessLabel, sessDate);

            // Add VAH
            if (sess.ValueAreaHigh > 0)
                TryAddNakedLevel(sess.ValueAreaHigh, "VAH", sessLabel, sessDate);

            // Add VAL
            if (sess.ValueAreaLow > 0)
                TryAddNakedLevel(sess.ValueAreaLow, "VAL", sessLabel, sessDate);
        }

        /// <summary>Helper: add a naked level if no duplicate price exists.
        /// Evicts the oldest entry first when the list is already at MaxNakedLevels capacity,
        /// ensuring the count never exceeds MaxNakedLevels at any observable point.</summary>
        private void TryAddNakedLevel(double price, string levelType, string sessLabel, DateTime sessDate)
        {
            // Check for duplicate price (within 1 tick)
            foreach (var existing in nakedTPOLevels)
            {
                if (Math.Abs(existing.Price - price) <= TickSize) return;
            }
            // Evict oldest entry before adding so the list never exceeds MaxNakedLevels
            if (nakedTPOLevels.Count >= MaxNakedLevels)
                nakedTPOLevels.RemoveAt(0);
            nakedTPOLevels.Add(new NakedTPOLevel
            {
                Price        = price,
                LevelType    = levelType,
                SessionLabel = sessLabel,
                SessionDate  = sessDate,
                CreatedBar   = CurrentBar,
                IsClosed     = false
            });
        }

        /// <summary>
        /// Check each bar whether price has traded through any naked level.
        /// If High[0] >= naked level and Low[0] <= naked level, mark it closed.
        /// </summary>
        private void UpdateNakedLevels()
        {
            if (nakedTPOLevels == null) return;
            double barHigh = High[0];
            double barLow  = Low[0];
            foreach (var nl in nakedTPOLevels)
            {
                if (nl.IsClosed) continue;
                // Level touched if bar's range includes the level price
                if (barHigh >= nl.Price && barLow <= nl.Price)
                    nl.IsClosed = true;
            }
        }

        /// <summary>Remove naked levels that exceed NakedLevelMaxAgeDays calendar days old.</summary>
        private void CleanupOldNakedLevels()
        {
            if (nakedTPOLevels == null) return;
            DateTime cutoff = Time[0].AddDays(-NakedLevelMaxAgeDays);
            nakedTPOLevels.RemoveAll(nl => nl.SessionDate < cutoff || nl.IsClosed);
        }

        /// <summary>Update tpoNearestNakedLevel: find nearest open naked level above current price.</summary>
        private void UpdateNearestNakedLevel()
        {
            if (nakedTPOLevels == null) { tpoNearestNakedLevel = null; return; }
            double close = Close[0];
            double nearest = double.MaxValue;
            NakedTPOLevel best = null;
            foreach (var nl in nakedTPOLevels)
            {
                if (!nl.IsClosed && nl.Price > close && nl.Price < nearest)
                {
                    nearest = nl.Price;
                    best    = nl;
                }
            }
            tpoNearestNakedLevel = best;
        }

        /// <summary>
        /// Build a descriptive stop line label for the Entry Mode dashboard.
        /// Reads _lastStopSource set by CalculateDynamicStop; shows fallback origin when applicable.
        /// </summary>
        private string BuildStopLabel(double stopPrice, double entryPrice)
        {
            string baseStr = string.Format("Stop:       {0}  ({1:F0}t)",
                Instrument.MasterInstrument.FormatPrice(stopPrice),
                Math.Abs(entryPrice - stopPrice) / TickSize);

            string sourceTag = StopSourceTag(_lastStopSource, _lastStopSourceDetail);
            if (string.IsNullOrEmpty(sourceTag))
                return baseStr;

            if (_lastStopWasFallback)
                return baseStr + "  [" + sourceTag + " \u2190 " + StopMode.ToString() + " fallback]";

            return baseStr + "  [" + sourceTag + "]";
        }

        private static string StopSourceTag(StopSource src, string detail)
        {
            switch (src)
            {
                case StopSource.VAH:           return "VAH";
                case StopSource.VAL:           return "VAL";
                case StopSource.PrevVAH:       return "prev VAH";
                case StopSource.PrevVAL:       return "prev VAL";
                case StopSource.PivotS1:       return "S1";
                case StopSource.PivotR1:       return "R1";
                case StopSource.LiquidityZone: return "Liq Zone";
                case StopSource.SRLevel:       return "SR";
                case StopSource.ADR:           return "ADR";
                case StopSource.Manual:        return "Manual";
                default: return "";
            }
        }

        /// <summary>
        /// Build a descriptive target line label for the Entry Mode dashboard.
        /// Reads _lastTargetSource set by CalculateDynamicTarget; shows fallback origin when applicable.
        /// </summary>
        private string BuildTargetLabel(double targetPrice, double entryPrice)
        {
            string baseStr = string.Format("Target:     {0}  ({1:F0}t)",
                Instrument.MasterInstrument.FormatPrice(targetPrice),
                Math.Abs(targetPrice - entryPrice) / TickSize);

            string sourceTag = TargetSourceTag(_lastTargetSource, _lastTargetSourceDetail);
            if (string.IsNullOrEmpty(sourceTag))
                return baseStr;

            if (_lastTargetWasFallback)
                return baseStr + "  [" + sourceTag + " \u2190 " + TargetMode.ToString() + " fallback]";

            return baseStr + "  [" + sourceTag + "]";
        }

        private static string TargetSourceTag(TargetSource src, string detail)
        {
            switch (src)
            {
                case TargetSource.IBExtHigh: return "IB ext H";
                case TargetSource.IBExtLow:  return "IB ext L";
                case TargetSource.VAH:       return "VAH";
                case TargetSource.VAL:       return "VAL";
                case TargetSource.PivotR1:   return "R1";
                case TargetSource.PivotR2:   return "R2";
                case TargetSource.PivotS1:   return "S1";
                case TargetSource.PivotS2:   return "S2";
                case TargetSource.NakedPOC:  return string.IsNullOrEmpty(detail) ? "Naked POC" : "Naked POC " + detail;
                case TargetSource.SRLevel:   return "SR";
                case TargetSource.ADR:       return "ADR";
                case TargetSource.Manual:    return "Manual";
                default: return "";
            }
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region TPO Rendering methods (IQMainUltimate additions)

        /// <summary>
        /// Master TPO rendering dispatcher. Called from OnRender when any TPO display option is enabled.
        /// Delegates to: RenderValueAreaBox, RenderInitialBalance, RenderPOCLine, RenderNakedLevels.
        /// </summary>
        private void RenderTPOLevels(ChartControl cc, ChartScale cs, float rtW)
        {
            var rt = RenderTarget;
            if (rt == null) return;
            if (tpoSessions == null) return;

            // Render completed sessions (historical) and active sessions
            lock (_sessionLock)
            {
                foreach (var sess in tpoSessions)
                {
                    if (sess.VolumeProfile.Count == 0) continue;
                    if (sess.POCPrice <= 0) continue;

                    // Determine X range for this session
                    int startBar = sess.StartBarIndex;
                    int endBar   = sess.IsComplete ? sess.EndBarIndex : (ChartBars?.ToIndex ?? sess.EndBarIndex);

                    if (startBar > ChartBars.ToIndex || endBar < ChartBars.FromIndex) continue;

                    float xStart;
                    try   { xStart = cc.GetXByBarIndex(ChartBars, Math.Max(startBar, ChartBars.FromIndex)); }
                    catch { continue; }

                    float xEnd;
                    try   { xEnd = cc.GetXByBarIndex(ChartBars, Math.Min(endBar, ChartBars.ToIndex)); }
                    catch { xEnd = rtW; }

                    float lineW = Math.Max(1f, xEnd - xStart);

                    // Value Area box + VAH/VAL lines
                    if (ShowValueArea && sess.ValueAreaHigh > 0 && sess.ValueAreaLow > 0)
                        RenderValueAreaBox(rt, cs, sess, xStart, lineW);

                    // POC line
                    if (ShowPOC && sess.POCPrice > 0 && dxTPOPocBrush != null)
                    {
                        float yPoc = cs.GetYByValue(sess.POCPrice);
                        if (!float.IsNaN(yPoc))
                        {
                            // Draw POC line from session start to end (solid gold, thickness 2)
                            DrawStyledLine(xStart, yPoc, xEnd, yPoc, dxTPOPocBrush, 2, IQMLineStyle.Solid);

                            // Label at left edge of session
                            if (dxTPOLabelFormat != null)
                            {
                                string volStr = sess.POCVolume >= 1_000_000
                                    ? string.Format("{0:F1}M", sess.POCVolume / 1_000_000)
                                    : sess.POCVolume >= 1000
                                    ? string.Format("{0:F0}K", sess.POCVolume / 1000)
                                    : string.Format("{0:F0}", sess.POCVolume);
                                string lbl = string.Format("{0} POC {1} ({2} vol)",
                                    GetSessionLabel(sess.SessionId),
                                    Instrument.MasterInstrument.FormatPrice(sess.POCPrice),
                                    volStr);
                                float labelYPoc = GetNonCollidingLabelY(yPoc - 14f, 14f);
                                rt.DrawText(lbl, dxTPOLabelFormat,
                                    new SharpDX.RectangleF(xStart + 2f, labelYPoc, 220f, 14f),
                                    dxTPOPocBrush);
                            }
                        }
                    }

                    // Initial Balance bracket
                    if (ShowInitialBalance && sess.IBHigh > double.MinValue && sess.IBLow < double.MaxValue)
                        RenderInitialBalance(rt, cc, cs, sess, xStart);
                }
            }

            // Naked levels (extend from creation bar forward to current bar)
            if (ShowNakedLevels)
                RenderNakedLevels(rt, cc, cs, rtW);
        }

        /// <summary>
        /// Returns a Y pixel coordinate for a TPO label that does not collide with already-used
        /// positions. Increments by labelHeight until a clear slot is found.
        /// Thread-safety: called only from OnRender (UI thread), so no lock needed.
        /// </summary>
        private float GetNonCollidingLabelY(float y, float labelHeight = 18f)
        {
            int yInt = (int)y;
            int step = (int)labelHeight;
            bool collision = true;
            while (collision)
            {
                collision = false;
                foreach (int used in _usedLabelYPositions)
                {
                    if (Math.Abs(used - yInt) < step) { collision = true; break; }
                }
                if (collision) yInt += step;
            }
            _usedLabelYPositions.Add(yInt);
            return (float)yInt;
        }

        /// <summary>
        /// Render the Value Area semi-transparent fill rectangle and VAH/VAL cyan dashed lines.
        /// The fill is drawn as a rectangle from session start to end, between VAH and VAL.
        /// </summary>
        private void RenderValueAreaBox(SharpDX.Direct2D1.RenderTarget rt, ChartScale cs,
            TPOSession sess, float xStart, float lineW)
        {
            if (dxTPOVAFillBrush == null || dxTPOVALineBrush == null) return;

            float yVAH = cs.GetYByValue(sess.ValueAreaHigh);
            float yVAL = cs.GetYByValue(sess.ValueAreaLow);

            if (float.IsNaN(yVAH) || float.IsNaN(yVAL)) return;

            float top    = Math.Min(yVAH, yVAL);
            float bottom = Math.Max(yVAH, yVAL);
            float height = bottom - top;

            if (height >= 1f)
                rt.FillRectangle(new SharpDX.RectangleF(xStart, top, lineW, height), dxTPOVAFillBrush);

            // VAH line (cyan dashed)
            DrawStyledLine(xStart, yVAH, xStart + lineW, yVAH, dxTPOVALineBrush, 1, IQMLineStyle.Dashed);
            // VAL line (cyan dashed)
            DrawStyledLine(xStart, yVAL, xStart + lineW, yVAL, dxTPOVALineBrush, 1, IQMLineStyle.Dashed);

            // Labels — use collision avoidance for Y positions (Bug 10)
            if (dxTPOLabelFormat != null)
            {
                float labelYVAH = GetNonCollidingLabelY(yVAH - 13f);
                float labelYVAL = GetNonCollidingLabelY(yVAL + 1f);
                rt.DrawText(string.Format("VAH {0}", Instrument.MasterInstrument.FormatPrice(sess.ValueAreaHigh)),
                    dxTPOLabelFormat, new SharpDX.RectangleF(xStart + 2f, labelYVAH, 100f, 13f), dxTPOVALineBrush);
                rt.DrawText(string.Format("VAL {0}", Instrument.MasterInstrument.FormatPrice(sess.ValueAreaLow)),
                    dxTPOLabelFormat, new SharpDX.RectangleF(xStart + 2f, labelYVAL,  100f, 13f), dxTPOVALineBrush);
            }
        }

        /// <summary>
        /// Render the Initial Balance vertical bracket (white lines connecting IB High to IB Low
        /// at session start) and optional dotted extension target lines.
        /// </summary>
        private void RenderInitialBalance(SharpDX.Direct2D1.RenderTarget rt, ChartControl cc,
            ChartScale cs, TPOSession sess, float xStart)
        {
            if (dxTPOIBBrush == null) return;
            if (sess.IBHigh <= double.MinValue || sess.IBLow >= double.MaxValue) return;

            float yIBH = cs.GetYByValue(sess.IBHigh);
            float yIBL = cs.GetYByValue(sess.IBLow);

            if (float.IsNaN(yIBH) || float.IsNaN(yIBL)) return;

            float bracketX = xStart + 2f;

            // Vertical bracket line (IB High to IB Low)
            rt.DrawLine(new SharpDX.Vector2(bracketX, yIBH), new SharpDX.Vector2(bracketX, yIBL),
                dxTPOIBBrush, 2f);

            // Horizontal tick marks at IB High and IB Low
            rt.DrawLine(new SharpDX.Vector2(bracketX, yIBH), new SharpDX.Vector2(bracketX + 8f, yIBH),
                dxTPOIBBrush, 2f);
            rt.DrawLine(new SharpDX.Vector2(bracketX, yIBL), new SharpDX.Vector2(bracketX + 8f, yIBL),
                dxTPOIBBrush, 2f);

            // Labels — use collision avoidance (Bug 10)
            if (dxTPOLabelFormat != null)
            {
                float labelYIBH = GetNonCollidingLabelY(yIBH - 13f);
                float labelYIBL = GetNonCollidingLabelY(yIBL + 1f);
                rt.DrawText(string.Format("IB H {0}", Instrument.MasterInstrument.FormatPrice(sess.IBHigh)),
                    dxTPOLabelFormat, new SharpDX.RectangleF(bracketX + 10f, labelYIBH, 100f, 13f), dxTPOIBBrush);
                rt.DrawText(string.Format("IB L {0}", Instrument.MasterInstrument.FormatPrice(sess.IBLow)),
                    dxTPOLabelFormat, new SharpDX.RectangleF(bracketX + 10f, labelYIBL,  100f, 13f), dxTPOIBBrush);
            }

            // IB extension dotted lines (if enabled and IB is complete)
            if (ShowIBExtensions && sess.IBComplete && sess.IBHighExtension > 0 && sess.IBLowExtension > 0)
            {
                int endBar = sess.IsComplete ? sess.EndBarIndex : (ChartBars?.ToIndex ?? CurrentBar);
                float xEnd;
                try   { xEnd = cc.GetXByBarIndex(ChartBars, Math.Min(endBar, ChartBars.ToIndex)); }
                catch { xEnd = xStart + 50f; }

                float yExtH = cs.GetYByValue(sess.IBHighExtension);
                float yExtL = cs.GetYByValue(sess.IBLowExtension);

                if (!float.IsNaN(yExtH))
                {
                    DrawStyledLine(xStart, yExtH, xEnd, yExtH, dxTPOIBBrush, 1, IQMLineStyle.Dotted);
                    if (dxTPOLabelFormat != null)
                    {
                        float labelYExtH = GetNonCollidingLabelY(yExtH - 13f);
                        rt.DrawText(string.Format("IB+1R {0}", Instrument.MasterInstrument.FormatPrice(sess.IBHighExtension)),
                            dxTPOLabelFormat, new SharpDX.RectangleF(xStart + 2f, labelYExtH, 120f, 13f), dxTPOIBBrush);
                    }
                }
                if (!float.IsNaN(yExtL))
                {
                    DrawStyledLine(xStart, yExtL, xEnd, yExtL, dxTPOIBBrush, 1, IQMLineStyle.Dotted);
                    if (dxTPOLabelFormat != null)
                    {
                        float labelYExtL = GetNonCollidingLabelY(yExtL + 1f);
                        rt.DrawText(string.Format("IB-1R {0}", Instrument.MasterInstrument.FormatPrice(sess.IBLowExtension)),
                            dxTPOLabelFormat, new SharpDX.RectangleF(xStart + 2f, labelYExtL,  120f, 13f), dxTPOIBBrush);
                    }
                }
            }
        }

        /// <summary>
        /// Render unclosed naked TPO levels as orange dashed horizontal lines that extend
        /// from the bar they were created at through to the current (rightmost) bar.
        /// Labels include the session date, level type, and price.
        /// </summary>
        private void RenderNakedLevels(SharpDX.Direct2D1.RenderTarget rt, ChartControl cc,
            ChartScale cs, float rtW)
        {
            if (dxTPONakedBrush == null || nakedTPOLevels == null) return;

            foreach (var nl in nakedTPOLevels)
            {
                if (nl.IsClosed) continue;
                if (nl.CreatedBar > ChartBars.ToIndex) continue;

                float xStart;
                try   { xStart = cc.GetXByBarIndex(ChartBars, Math.Max(nl.CreatedBar, ChartBars.FromIndex)); }
                catch { continue; }

                float y = cs.GetYByValue(nl.Price);
                if (float.IsNaN(y)) continue;

                // Orange dashed line extending to the right edge
                DrawStyledLine(xStart, y, rtW, y, dxTPONakedBrush, 1, IQMLineStyle.Dashed);

                // Label at left edge of the naked level — use collision avoidance (Bug 10)
                if (dxTPOLabelFormat != null)
                {
                    string dateStr = nl.SessionDate.ToString("M/d");
                    string lbl = string.Format("Naked {0} {1} ({2})",
                        nl.LevelType, dateStr,
                        Instrument.MasterInstrument.FormatPrice(nl.Price));
                    float labelY = GetNonCollidingLabelY(y - 13f);
                    rt.DrawText(lbl, dxTPOLabelFormat,
                        new SharpDX.RectangleF(xStart + 2f, labelY, 180f, 13f), dxTPONakedBrush);
                }
            }
        }

        private double GetAverageVolume(int barIndex, int lookback)
        {
            double sum = 0;
            int limit = Math.Min(barIndex + lookback, CurrentBar - 1);
            for (int i = barIndex + 1; i <= limit; i++)
                sum += Volume[i];
            return lookback > 0 ? sum / lookback : 0;
        }

        private double GetMaxSpreadVolProduct(int barIndex, int lookback)
        {
            double maxProduct = 0;
            int limit = Math.Min(barIndex + lookback, CurrentBar - 1);
            for (int i = barIndex + 1; i <= limit; i++)
            {
                double svp = (High[i] - Low[i]) * Volume[i];
                if (svp > maxProduct) maxProduct = svp;
            }
            return maxProduct;
        }

        private bool IsPVSRAGreen(int barIndex)
        {
            if (!EnablePVSRA || CurrentBar < barIndex + PVSRALookback + 1) return false;
            double avgVol = GetAverageVolume(barIndex, PVSRALookback);
            if (avgVol <= 0) return false;
            double svp    = (High[barIndex] - Low[barIndex]) * Volume[barIndex];
            double maxSvp = GetMaxSpreadVolProduct(barIndex, PVSRALookback);
            return Close[barIndex] > Open[barIndex] &&
                   (Volume[barIndex] >= avgVol * (HighVolumeThreshold / 100.0) ||
                    (maxSvp > 0 && svp >= maxSvp));
        }

        private bool IsPVSRARed(int barIndex)
        {
            if (!EnablePVSRA || CurrentBar < barIndex + PVSRALookback + 1) return false;
            double avgVol = GetAverageVolume(barIndex, PVSRALookback);
            if (avgVol <= 0) return false;
            double svp    = (High[barIndex] - Low[barIndex]) * Volume[barIndex];
            double maxSvp = GetMaxSpreadVolProduct(barIndex, PVSRALookback);
            return Close[barIndex] < Open[barIndex] &&
                   (Volume[barIndex] >= avgVol * (HighVolumeThreshold / 100.0) ||
                    (maxSvp > 0 && svp >= maxSvp));
        }

        private bool IsPVSRABlue(int barIndex)
        {
            if (!EnablePVSRA || CurrentBar < barIndex + PVSRALookback + 1) return false;
            double avgVol = GetAverageVolume(barIndex, PVSRALookback);
            if (avgVol <= 0) return false;
            return Close[barIndex] > Open[barIndex] &&
                   Volume[barIndex] >= avgVol * (MidVolumeThreshold  / 100.0) &&
                   Volume[barIndex] <  avgVol * (HighVolumeThreshold / 100.0);
        }

        private bool IsPVSRAPink(int barIndex)
        {
            if (!EnablePVSRA || CurrentBar < barIndex + PVSRALookback + 1) return false;
            double avgVol = GetAverageVolume(barIndex, PVSRALookback);
            if (avgVol <= 0) return false;
            return Close[barIndex] < Open[barIndex] &&
                   Volume[barIndex] >= avgVol * (MidVolumeThreshold  / 100.0) &&
                   Volume[barIndex] <  avgVol * (HighVolumeThreshold / 100.0);
        }

        private void CreatePVSRAZone(bool isBullish, bool isAbsorption, int barIndex)
        {
            double high  = High[barIndex];
            double low   = Low[barIndex];
            double open  = Open[barIndex];
            double close = Close[barIndex];

            double wickTop, wickBot;
            if (isBullish)
            {
                wickBot = isAbsorption ? open : low;
                wickTop = high;
            }
            else
            {
                wickTop = isAbsorption ? open : high;
                wickBot = low;
            }

            double bodyHigh = Math.Max(open, close);
            double bodyLow  = Math.Min(open, close);
            double bodyTop, bodyBot;
            if (isBullish)
            {
                bodyBot = isAbsorption ? open : bodyLow;
                bodyTop = bodyHigh;
            }
            else
            {
                bodyTop = isAbsorption ? open : bodyHigh;
                bodyBot = bodyLow;
            }

            double zoneHigh     = Math.Max(wickTop, wickBot);
            double zoneLow      = Math.Min(wickTop, wickBot);
            double zoneBodyHigh = Math.Max(bodyTop, bodyBot);
            double zoneBodyLow  = Math.Min(bodyTop, bodyBot);

            if (zoneBodyHigh == zoneBodyLow) return;

            AddLiquidityZone(new LiquidityZone
            {
                HighPrice           = zoneHigh,
                LowPrice            = zoneLow,
                BodyHighPrice       = zoneBodyHigh,
                BodyLowPrice        = zoneBodyLow,
                CreatedBar          = CurrentBar,
                OriginBarIndex      = CurrentBar - barIndex,
                IsBullish           = isBullish,
                IsAbsorption        = isAbsorption,
                IsRecovered         = false,
                PartialRecoveryHigh = zoneBodyHigh,
                PartialRecoveryLow  = zoneBodyLow
            });
        }

        private void UpdateImbalanceThresholds()
        {
            if (deltaHistory.Count < 5) return;
            var sorted = new List<double>(deltaHistory);
            sorted.Sort();
            int lo5  = (int)Math.Floor(sorted.Count * 0.05);
            int hi95 = Math.Min((int)Math.Floor(sorted.Count * 0.95), sorted.Count - 1);
            imbalanceLow  = sorted[lo5];
            imbalanceHigh = sorted[hi95];
        }

        private void TryDetectSRLevel()
        {
            if (CurrentBar < SRSwingStrength * 2 + 1) return;

            bool isSwingHigh = true;
            for (int i = 1; i <= SRSwingStrength; i++)
            {
                if (High[i] >= High[0]) { isSwingHigh = false; break; }
            }
            if (isSwingHigh)
            {
                for (int i = SRSwingStrength + 1; i <= SRSwingStrength * 2; i++)
                {
                    if (i < CurrentBar && High[i] >= High[SRSwingStrength]) { isSwingHigh = false; break; }
                }
            }

            bool isSwingLow = true;
            for (int i = 1; i <= SRSwingStrength; i++)
            {
                if (Low[i] <= Low[0]) { isSwingLow = false; break; }
            }
            if (isSwingLow)
            {
                for (int i = SRSwingStrength + 1; i <= SRSwingStrength * 2; i++)
                {
                    if (i < CurrentBar && Low[i] <= Low[SRSwingStrength]) { isSwingLow = false; break; }
                }
            }

            double level = 0;
            if      (isSwingHigh) level = High[0];
            else if (isSwingLow)  level = Low[0];

            if (level > 0)
            {
                bool duplicate = false;
                double tolerance = TickSize * 5;
                foreach (double existing in srLevels)
                {
                    if (Math.Abs(existing - level) < tolerance) { duplicate = true; break; }
                }
                if (!duplicate)
                {
                    if (srLevels.Count >= 50) srLevels.RemoveAt(0);
                    srLevels.Add(level);
                }
            }
        }

        private void UpdateOTEZones()
        {
            if (CurrentBar < OTESwingStrength * 2 + 1) return;

            // Bug B Fix 1 — Age-based deactivation
            int maxZoneAgeBars = OTEMaxZones * OTESwingStrength * 20;
            foreach (var z in oteZones)
                if (z.IsActive && (CurrentBar - z.CreatedBar) > maxZoneAgeBars)
                    z.IsActive = false;

            // Bug B Fix 2 — Price-based deactivation (price traded through the 79% level)
            foreach (var z in oteZones)
            {
                if (!z.IsActive) continue;
                bool priceThroughZone = z.IsBullish
                    ? Low[0] < z.Level79   // price traded below 79% on a bullish zone
                    : High[0] > z.Level79; // price traded above 79% on a bearish zone
                if (priceThroughZone)
                    z.IsActive = false;
            }

            int swingBar = OTESwingStrength;

            // Detect swing high at swingBar bars ago
            bool isSwingHigh = true;
            for (int i = 0; i < OTESwingStrength; i++)
            {
                if (High[i] >= High[swingBar]) { isSwingHigh = false; break; }
            }
            if (isSwingHigh)
            {
                for (int i = swingBar + 1; i <= swingBar + OTESwingStrength; i++)
                {
                    if (i < CurrentBar && High[i] >= High[swingBar]) { isSwingHigh = false; break; }
                }
            }

            // Detect swing low at swingBar bars ago
            bool isSwingLow = true;
            for (int i = 0; i < OTESwingStrength; i++)
            {
                if (Low[i] <= Low[swingBar]) { isSwingLow = false; break; }
            }
            if (isSwingLow)
            {
                for (int i = swingBar + 1; i <= swingBar + OTESwingStrength; i++)
                {
                    if (i < CurrentBar && Low[i] <= Low[swingBar]) { isSwingLow = false; break; }
                }
            }

            if (isSwingHigh)
            {
                oteLastSwingHigh    = High[swingBar];
                oteLastSwingHighBar = CurrentBar - swingBar;
                TryCreateOTEZone();
            }

            if (isSwingLow)
            {
                oteLastSwingLow    = Low[swingBar];
                oteLastSwingLowBar = CurrentBar - swingBar;
                TryCreateOTEZone();
            }
        }

        private void TryCreateOTEZone()
        {
            if (double.IsNaN(oteLastSwingHigh) || double.IsNaN(oteLastSwingLow)) return;
            if (oteLastSwingHigh <= oteLastSwingLow) return;

            double range = oteLastSwingHigh - oteLastSwingLow;
            // Bullish: swing high formed after swing low (price moved up then retraces back down)
            bool isBullish = oteLastSwingHighBar > oteLastSwingLowBar;

            double level62, level705, level79;
            if (isBullish)
            {
                level62  = oteLastSwingHigh - (range * 0.62);
                level705 = oteLastSwingHigh - (range * 0.705);
                level79  = oteLastSwingHigh - (range * 0.79);
            }
            else
            {
                level62  = oteLastSwingLow + (range * 0.62);
                level705 = oteLastSwingLow + (range * 0.705);
                level79  = oteLastSwingLow + (range * 0.79);
            }

            // Avoid duplicate zones (same swing high and low already tracked)
            foreach (OTEZone existing in oteZones)
            {
                if (Math.Abs(existing.SwingHigh - oteLastSwingHigh) < TickSize &&
                    Math.Abs(existing.SwingLow  - oteLastSwingLow)  < TickSize)
                    return;
            }

            if (oteZones.Count >= OTEMaxZones)
                oteZones.RemoveAt(0);

            oteZones.Add(new OTEZone
            {
                SwingHigh    = oteLastSwingHigh,
                SwingLow     = oteLastSwingLow,
                Level62      = level62,
                Level705     = level705,
                Level79      = level79,
                SwingHighBar = oteLastSwingHighBar,
                SwingLowBar  = oteLastSwingLowBar,
                CreatedBar   = CurrentBar,
                IsBullish    = isBullish,
                IsActive     = true
            });
        }

        private void CheckFakeBreakoutFilters(double barBuy, double barSell, double totalVol,
            ref bool isFake, ref int dir)
        {
            isFake = false;
            dir    = 0;

            if (CurrentBar < MomentumPeriod + SRSwingStrength * 2 + 2) return;

            double nearestBreak   = 0;
            int    breakDirection = 0;
            double breakTolerance = TickSize * 3;

            foreach (double sr in srLevels)
            {
                if (High[0] > sr && Low[1] < sr && Math.Abs(Close[0] - sr) < breakTolerance * 10)
                {
                    nearestBreak = sr; breakDirection = 1; break;
                }
                if (Low[0] < sr && High[1] > sr && Math.Abs(Close[0] - sr) < breakTolerance * 10)
                {
                    nearestBreak = sr; breakDirection = -1; break;
                }
            }

            if (nearestBreak == 0) return;

            bool confirmedBreakout;
            if (breakDirection == 1)
                confirmedBreakout = Close[0] > nearestBreak && Close[1] > nearestBreak;
            else
                confirmedBreakout = Close[0] < nearestBreak && Close[1] < nearestBreak;

            if (confirmedBreakout)
            {
                confirmBarsCount++;
                if (confirmBarsCount >= ConfirmationBars) return;
            }
            else
            {
                confirmBarsCount = 0;
            }

            double avgVol  = 0;
            int    lookback = Math.Min(CurrentBar, DeltaLookback);
            for (int i = 1; i <= lookback; i++) avgVol += Volume[i];
            if (lookback > 0) avgVol /= lookback;

            bool volumeOK = avgVol > 0 && (totalVol / avgVol * 100.0) >= VolumeFollowThrough;
            if (volumeOK) return;

            double momentumNow  = Close[0] - Close[MomentumPeriod];
            double momentumPrev = Close[1] - Close[MomentumPeriod + 1];
            bool   momentumDiverges;
            if (breakDirection == 1) momentumDiverges = momentumNow < momentumPrev;
            else                     momentumDiverges = momentumNow > momentumPrev;

            bool srValid = nearestBreak > 0;

            double stopDist = Math.Abs(Close[0] - nearestBreak) + TickSize;
            double reward   = Math.Abs(High[0] - Low[0]);
            bool   rrOK     = stopDist > 0 && (reward / stopDist) >= MinRiskReward;

            if (!confirmedBreakout && !volumeOK && (momentumDiverges || srValid) && !rrOK)
            {
                isFake = true;
                dir    = breakDirection;
            }
        }

        private void DetectOrderBookWalls(Dictionary<double, BookLevel> book, bool isBid)
        {
            if (book.Count == 0) return;

            double avg = 0;
            foreach (var kv in book) avg += kv.Value.Size;
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

            if (isBid) { wallBidPrice = wallPrice; wallBidSize  = wallSize; }
            else       { wallAskPrice = wallPrice; wallAskSize  = wallSize; }
        }

        private void UpdateDashboardText(double barBuy, double barSell, double delta, double deltaPct,
            bool isAbsorption, bool isImbalance, bool isFake)
        {
            dashLine2 = string.Format("Buy Vol: {0:N0}  Sell Vol: {1:N0}", barBuy, barSell);
            dashLine3 = string.Format("Delta: {0:+0;-0;0}  Δ%: {1:+0.0;-0.0;0.0}%", (long)delta, deltaPct);
            dashLine4 = string.Format("Cum Δ: {0}  Session B/S: {1:N0}/{2:N0}",
                FormatDelta(cumDelta), sessionBuyVol, sessionSellVol);

            var flags = new List<string>();
            if (isAbsorption)     flags.Add("ABS");
            if (isImbalance)      flags.Add("IMB");
            if (isFake)           flags.Add("FAKE-BKT");
            if (wallBidPrice > 0) flags.Add(string.Format("BID WALL@{0:F2}", wallBidPrice));
            if (wallAskPrice > 0) flags.Add(string.Format("ASK WALL@{0:F2}", wallAskPrice));
            dashLine5 = flags.Count > 0 ? "Signals: " + string.Join(" | ", flags) : "Signals: none";

            dashLine6 = string.Format("S/R Levels: {0}  |  Imb Threshold: {1:N0}", srLevels.Count, imbalanceHigh);

            if (ShowZoneCount && EnableLiquidityZones)
            {
                int greenZones = 0, redZones = 0, blueZones = 0, pinkZones = 0;
                if (liquidityZones != null)
                {
                    foreach (LiquidityZone z in liquidityZones)
                    {
                        if (z.IsRecovered) continue;
                        if      ( z.IsBullish && !z.IsAbsorption) greenZones++;
                        else if (!z.IsBullish && !z.IsAbsorption) redZones++;
                        else if ( z.IsBullish &&  z.IsAbsorption) blueZones++;
                        else                                       pinkZones++;
                    }
                }
                dashLine7 = string.Format("ULZ: G:{0} R:{1} B:{2} P:{3}  (Rec:{4})",
                    greenZones, redZones, blueZones, pinkZones, recoveredZoneCount);
            }
            else
            {
                dashLine7 = "";
            }
        }

        private BarSnapshot GetSnapshot(int barIdx)
        {
            int offset = CurrentBar - barIdx;
            int sIdx   = snapshots.Count - 1 - offset;
            if (sIdx < 0 || sIdx >= snapshots.Count) return null;
            return snapshots[sIdx];
        }

        private void CheckLiquidityZoneRecovery()
        {
            if (liquidityZones.Count == 0) return;

            int refBar = (Calculate == Calculate.OnBarClose) ? 0 : 1;
            double curHigh  = High[refBar];
            double curLow   = Low[refBar];

            int active = 0, recovered = 0;

            for (int i = 0; i < liquidityZones.Count; i++)
            {
                LiquidityZone z = liquidityZones[i];
                if (z.IsRecovered) { recovered++; continue; }
                if (z.CreatedBar >= CurrentBar) { active++; continue; }

                bool zoneRecovered = false;

                if (z.IsBullish)
                {
                    double topBoundary = z.PartialRecoveryHigh;
                    double botBoundary = z.PartialRecoveryLow;

                    if (curLow <= botBoundary)
                        zoneRecovered = true;
                    else if (curLow < topBoundary)
                    {
                        z.PartialRecoveryHigh = Math.Min(z.PartialRecoveryHigh, curLow);
                        if (z.PartialRecoveryHigh <= z.PartialRecoveryLow)
                            zoneRecovered = true;
                    }
                }
                else
                {
                    double topBoundary = z.PartialRecoveryHigh;
                    double botBoundary = z.PartialRecoveryLow;

                    if (curHigh >= topBoundary)
                        zoneRecovered = true;
                    else if (curHigh > botBoundary)
                    {
                        z.PartialRecoveryLow = Math.Max(z.PartialRecoveryLow, curHigh);
                        if (z.PartialRecoveryHigh <= z.PartialRecoveryLow)
                            zoneRecovered = true;
                    }
                }

                if (zoneRecovered) { z.IsRecovered = true; recovered++; }
                else active++;
            }

            activeZoneCount    = active;
            recoveredZoneCount = recovered;
        }

        private void AddLiquidityZone(LiquidityZone zone)
        {
            if (zone.HighPrice <= zone.LowPrice) return;

            if (liquidityZones.Count >= MaxActiveZones)
            {
                LiquidityZone recoveredZone = null;
                for (int i = 0; i < liquidityZones.Count; i++)
                {
                    if (liquidityZones[i].IsRecovered) { recoveredZone = liquidityZones[i]; break; }
                }
                if (recoveredZone != null) liquidityZones.Remove(recoveredZone);
                else if (liquidityZones.Count > 0) liquidityZones.RemoveAt(0);
            }

            liquidityZones.Add(zone);
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
        #region Render helpers — VWAP

        private void RenderVwap(ChartControl cc, ChartScale cs, int fromBar, int toBar)
        {
            if (vwapEthData == null || vwapRthData == null) return;
            var rt = RenderTarget;
            if (rt == null) return;

            bool showEth = (VwapAnchor == VwapSessionAnchor.ETH || VwapAnchor == VwapSessionAnchor.Both);
            bool showRth = (VwapAnchor == VwapSessionAnchor.RTH_US || VwapAnchor == VwapSessionAnchor.Both);

            if (showEth) RenderVwapLine(cc, cs, fromBar, toBar, vwapEthData, VwapEthLabel);
            if (showRth) RenderVwapLine(cc, cs, fromBar, toBar, vwapRthData, VwapRthLabel);
        }

        private void RenderVwapLine(ChartControl cc, ChartScale cs, int fromBar, int toBar,
            List<VwapBarData> data, string label)
        {
            var rt = RenderTarget;
            if (rt == null || data == null || data.Count == 0) return;

            // Render bands behind the VWAP line
            if (ShowVwapBand3 && dxVwapBand3Brush != null)
                RenderVwapBandPair(cc, cs, fromBar, toBar, data, 3, dxVwapBand3Brush, VwapBand3Thickness);
            if (ShowVwapBand2 && dxVwapBand2Brush != null)
                RenderVwapBandPair(cc, cs, fromBar, toBar, data, 2, dxVwapBand2Brush, VwapBand2Thickness);
            if (ShowVwapBand1 && dxVwapBand1Brush != null)
                RenderVwapBandPair(cc, cs, fromBar, toBar, data, 1, dxVwapBand1Brush, VwapBand1Thickness);

            // Render VWAP line with dynamic color
            for (int barIdx = fromBar; barIdx < toBar; barIdx++)
            {
                if (barIdx < 0 || barIdx + 1 >= Bars.Count) continue;
                VwapBarData d0 = GetVwapDataAt(data, barIdx);
                VwapBarData d1 = GetVwapDataAt(data, barIdx + 1);
                if (d0 == null || d1 == null) continue;

                float x0 = cc.GetXByBarIndex(ChartBars, barIdx);
                float x1 = cc.GetXByBarIndex(ChartBars, barIdx + 1);
                float y0 = cs.GetYByValue(d0.Vwap);
                float y1 = cs.GetYByValue(d1.Vwap);

                SharpDX.Direct2D1.SolidColorBrush brush;
                if (VwapDynamicColor)
                {
                    double closeNext = Bars.GetClose(barIdx + 1);
                    if (closeNext > d1.Vwap)
                        brush = dxVwapAboveBrush;
                    else if (closeNext < d1.Vwap)
                        brush = dxVwapBelowBrush;
                    else
                        brush = dxVwapNeutralBrush;
                }
                else
                {
                    brush = dxVwapNeutralBrush;
                }

                if (brush == null) continue;
                DrawStyledLine(x0, y0, x1, y1, brush, VwapLineThickness, VwapLineStyle);
            }

            // Label at rightmost visible bar
            if (ShowVwapLabel && dxLabelFormat != null)
            {
                VwapBarData lastData = GetVwapDataAt(data, toBar);
                if (lastData != null)
                {
                    float  y      = cs.GetYByValue(lastData.Vwap);
                    float  labelX = cc.GetXByBarIndex(ChartBars, toBar) + 6f;
                    string txt    = label + " " + Instrument.MasterInstrument.FormatPrice(lastData.Vwap);

                    SharpDX.Direct2D1.SolidColorBrush labelBrush;
                    if (VwapDynamicColor)
                    {
                        double c = Close[0];
                        labelBrush = c > lastData.Vwap ? dxVwapAboveBrush
                                   : c < lastData.Vwap ? dxVwapBelowBrush
                                   : dxVwapNeutralBrush;
                    }
                    else
                    {
                        labelBrush = dxVwapNeutralBrush;
                    }

                    if (labelBrush != null)
                        RenderTarget.DrawText(txt, dxLabelFormat,
                            new SharpDX.RectangleF(labelX, y - 8f, 140f, 16f), labelBrush);
                }
            }
        }

        private void RenderVwapBandPair(ChartControl cc, ChartScale cs, int fromBar, int toBar,
            List<VwapBarData> data, int bandNum,
            SharpDX.Direct2D1.SolidColorBrush brush, int thickness)
        {
            var rt = RenderTarget;
            if (rt == null) return;

            for (int barIdx = fromBar; barIdx < toBar; barIdx++)
            {
                if (barIdx < 0 || barIdx + 1 >= Bars.Count) continue;
                VwapBarData d0 = GetVwapDataAt(data, barIdx);
                VwapBarData d1 = GetVwapDataAt(data, barIdx + 1);
                if (d0 == null || d1 == null) continue;

                double upper0 = 0, lower0 = 0, upper1 = 0, lower1 = 0;
                switch (bandNum)
                {
                    case 1: upper0 = d0.Band1Upper; lower0 = d0.Band1Lower; upper1 = d1.Band1Upper; lower1 = d1.Band1Lower; break;
                    case 2: upper0 = d0.Band2Upper; lower0 = d0.Band2Lower; upper1 = d1.Band2Upper; lower1 = d1.Band2Lower; break;
                    case 3: upper0 = d0.Band3Upper; lower0 = d0.Band3Lower; upper1 = d1.Band3Upper; lower1 = d1.Band3Lower; break;
                    default: continue;
                }

                float yU0 = cs.GetYByValue(upper0);
                float yU1 = cs.GetYByValue(upper1);
                float yL0 = cs.GetYByValue(lower0);
                float yL1 = cs.GetYByValue(lower1);

                float x0 = cc.GetXByBarIndex(ChartBars, barIdx);
                float x1 = cc.GetXByBarIndex(ChartBars, barIdx + 1);

                DrawStyledLine(x0, yU0, x1, yU1, brush, thickness, IQMLineStyle.Dashed);
                DrawStyledLine(x0, yL0, x1, yL1, brush, thickness, IQMLineStyle.Dashed);

                // Optional fill between upper and lower band
                if (VwapFillBands && dxVwapFillBrush != null)
                {
                    float fillTop    = Math.Min(yU0, yU1);   // highest point of upper band
                    float fillBottom = Math.Max(yL0, yL1);   // lowest point of lower band
                    float width      = x1 - x0;
                    if (width > 0 && fillBottom > fillTop)
                        rt.FillRectangle(
                            new SharpDX.RectangleF(x0, fillTop, width, fillBottom - fillTop),
                            dxVwapFillBrush);
                }
            }
        }

        private VwapBarData GetVwapDataAt(List<VwapBarData> data, int barIdx)
        {
            int offset = CurrentBar - barIdx;
            int idx    = data.Count - 1 - offset;
            if (idx < 0 || idx >= data.Count) return null;
            return data[idx];
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region SharpDX resource management

        private void CreateDXResources()
        {
            var rt = RenderTarget;
            if (rt == null) { dxReady = false; return; }

            DisposeDXResources();

            try
            {
                float opacityFrac = Math.Max(0f, Math.Min(1f, DashOpacity / 100f));

                dxWriteFactory = new SharpDX.DirectWrite.Factory();
                dxLabelFormat  = new SharpDX.DirectWrite.TextFormat(dxWriteFactory, "Consolas", 12f);
                dxSmallFormat  = new SharpDX.DirectWrite.TextFormat(dxWriteFactory, "Consolas", 12f);
                dxDashFormat   = new SharpDX.DirectWrite.TextFormat(dxWriteFactory, "Consolas", DashFontSize);

                // Pivot brushes
                dxPPBrush     = MakeBrush(rt, PPColor,     0.85f);
                dxRLevelBrush = MakeBrush(rt, RLevelColor, 0.85f);
                dxSLevelBrush = MakeBrush(rt, SLevelColor, 0.85f);
                dxMLevelBrush = MakeBrush(rt, MLevelColor, MLevelOpacity / 100f);

                // Yesterday / last week
                dxYesterdayBrush = MakeBrush(rt, YesterdayColor, 0.8f);
                dxLastWeekBrush  = MakeBrush(rt, LastWeekColor,  0.8f);

                // ADR / AWR / AMR / RD / RW
                dxAdrBrush = MakeBrush(rt, AdrColor, AdrOpacity / 100f);
                dxAwrBrush = MakeBrush(rt, AwrColor, AwrOpacity / 100f);
                dxAmrBrush = MakeBrush(rt, AmrColor, AmrOpacity / 100f);
                dxRdBrush  = MakeBrush(rt, RdColor,  RdOpacity  / 100f);
                dxRwBrush  = MakeBrush(rt, RwColor,  RwOpacity  / 100f);

                // Daily open
                dxDailyOpenBrush = MakeBrush(rt, DailyOpenColor, 0.9f);

                // ETH Daily Open and RTH Session Open brushes
                dxEthDailyOpenBrush = MakeBrush(rt, EthDailyOpenColor, 0.9f);
                dxAsiaOpenBrush     = MakeBrush(rt, AsiaOpenColor,     0.9f);
                dxLondonOpenBrush   = MakeBrush(rt, LondonOpenColor,   0.9f);
                dxUsOpenBrush       = MakeBrush(rt, UsOpenColor,       0.9f);

                // Session brushes — per session
                dxSessionBoxBrush    = new SharpDX.Direct2D1.SolidColorBrush[8];
                dxSessionBorderBrush = new SharpDX.Direct2D1.SolidColorBrush[8];
                System.Windows.Media.Brush[] sessionColors =
                {
                    LondonColor, NewYorkColor, TokyoColor, HongKongColor,
                    SydneyColor, EuBrinksColor, UsBrinksColor, FrankfurtColor
                };
                int[] sessionOpacities =
                {
                    LondonOpacity, NewYorkOpacity, TokyoOpacity, HongKongOpacity,
                    SydneyOpacity, EuBrinksOpacity, UsBrinksOpacity, FrankfurtOpacity
                };
                for (int i = 0; i < 8; i++)
                {
                    dxSessionBoxBrush[i]    = MakeBrush(rt, sessionColors[i], sessionOpacities[i] / 100f);
                    dxSessionBorderBrush[i] = MakeBrush(rt, sessionColors[i], 0.85f);
                }

                // Psy levels
                dxPsyBrush = MakeBrush(rt, PsyColor, PsyOpacity / 100f);

                // Shared dashboard background and text
                dxDashBgBrush    = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color((byte)10, (byte)10, (byte)20, (byte)(opacityFrac * 220)));
                dxDashTextBrush  = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color((byte)220, (byte)220, (byte)230, (byte)240));
                dxDashHeaderBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(1f, 0.85f, 0.2f, 1f));
                dxDashAccentBrush = MakeBrush(rt, FakeBreakoutColor, 1f);

                // Candle brushes
                dxBullBrush      = MakeBrush(rt, BullishColor,     0.85f);
                dxBearBrush      = MakeBrush(rt, BearishColor,     0.85f);
                dxAbsorbBrush    = MakeBrush(rt, AbsorptionColor,  0.90f);
                dxImbalanceBrush = MakeBrush(rt, ImbalanceColor,   0.85f);
                dxFakeBrush      = MakeBrush(rt, FakeBreakoutColor, 0.90f);
                dxWickBrush      = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color((byte)180, (byte)180, (byte)180, (byte)200));

                // Wall brushes
                dxWallBidBrush = MakeBrush(rt, BullishColor, 0.9f);
                dxWallAskBrush = MakeBrush(rt, BearishColor, 0.9f);

                // PVSRA brushes
                dxPvsraHighBullBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(0f / 255f, 210f / 255f, 80f / 255f, 220f / 255f));
                dxPvsraHighBearBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(220f / 255f, 0f / 255f, 30f / 255f, 220f / 255f));
                dxPvsraMidBullBrush  = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(30f / 255f, 100f / 255f, 255f / 255f, 200f / 255f));
                dxPvsraMidBearBrush  = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(255f / 255f, 105f / 255f, 180f / 255f, 200f / 255f));

                // Composite border brushes
                dxBorderAbsorbBrush    = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(255f / 255f, 215f / 255f, 0f / 255f, 230f / 255f));
                dxBorderImbalanceBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(30f / 255f, 144f / 255f, 255f / 255f, 230f / 255f));
                dxBorderFakeBrush      = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(255f / 255f, 120f / 255f, 0f / 255f, 230f / 255f));

                // Zone fill brushes
                float zoneAlphaF = Math.Max(0.02f, Math.Min(0.80f, (float)ZoneOpacity / 100f));
                dxULZBullishBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(0f / 255f, 210f / 255f, 80f / 255f, zoneAlphaF));
                dxULZBearishBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(220f / 255f, 0f / 255f, 30f / 255f, zoneAlphaF));
                dxULZBlueBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(30f / 255f, 100f / 255f, 255f / 255f, zoneAlphaF));
                dxULZPinkBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(255f / 255f, 105f / 255f, 180f / 255f, zoneAlphaF));

                // VWAP brushes
                dxVwapAboveBrush   = MakeBrush(rt, VwapAboveColor,   VwapOpacity  / 100f);
                dxVwapBelowBrush   = MakeBrush(rt, VwapBelowColor,   VwapOpacity  / 100f);
                dxVwapNeutralBrush = MakeBrush(rt, VwapNeutralColor, VwapOpacity  / 100f);
                dxVwapBand1Brush   = MakeBrush(rt, VwapBand1Color,   VwapBand1Opacity / 100f);
                dxVwapBand2Brush   = MakeBrush(rt, VwapBand2Color,   VwapBand2Opacity / 100f);
                dxVwapBand3Brush   = MakeBrush(rt, VwapBand3Color,   VwapBand3Opacity / 100f);
                dxVwapFillBrush    = MakeBrush(rt, VwapNeutralColor, VwapFillOpacity  / 100f);

                // OTE zone brushes
                float oteAlphaF     = Math.Max(0.02f, Math.Min(0.80f, (float)OTEZoneOpacity / 100f));
                dxOTEBullishBrush   = MakeBrush(rt, OTEBullishColor, oteAlphaF);
                dxOTEBearishBrush   = MakeBrush(rt, OTEBearishColor, oteAlphaF);
                dxOTELineBrush      = MakeBrush(rt, OTELineColor,    0.90f);
                dxOTEOptimalBrush   = MakeBrush(rt, OTEOptimalColor, 0.95f);

                // ── Enhanced dashboard resources (IQMainGPU_Enhanced) ─────────
                if (dxMainDashFormat != null) { dxMainDashFormat.Dispose(); dxMainDashFormat = null; }
                if (dxEnhDashFormat  != null) { dxEnhDashFormat.Dispose();  dxEnhDashFormat  = null; }
                if (dxEnhMonFormat   != null) { dxEnhMonFormat.Dispose();   dxEnhMonFormat   = null; }
                dxMainDashFormat = new SharpDX.DirectWrite.TextFormat(dxWriteFactory, "Consolas", MainDashboardFontSize);
                dxEnhDashFormat  = new SharpDX.DirectWrite.TextFormat(dxWriteFactory, "Consolas", EntryModeDashboardFontSize);
                dxEnhMonFormat   = new SharpDX.DirectWrite.TextFormat(dxWriteFactory, "Consolas", MonitoringDashboardFontSize);

                float dashOpacFrac = Math.Max(0f, Math.Min(1f, DashboardOpacity / 100f));
                dxEnhDashBgBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color((byte)5, (byte)5, (byte)15, (byte)(dashOpacFrac * 230)));
                dxEnhDashTextBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(0.90f, 0.90f, 0.95f, 0.95f));
                dxEnhDashHeaderBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(1f, 0.90f, 0.20f, 1f));
                dxEnhDashWarningBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(1f, 0.50f, 0.10f, 1f));
                dxEnhDashGreenBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(0.20f, 0.90f, 0.30f, 1f));
                dxEnhDashRedBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(0.95f, 0.20f, 0.20f, 1f));
                dxEnhDashNeutralBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(0.50f, 0.50f, 0.50f, 0.80f));
                dxEntryLineBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(1f, 1f, 1f, 0.85f));
                dxStopLineBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(0.95f, 0.20f, 0.20f, 0.75f));
                dxTargetLineBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(0.20f, 0.90f, 0.30f, 0.75f));
                _brushDimmedText = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(0.6f, 0.6f, 0.6f, 0.5f));

                // ── TPO / Market Profile brushes (IQMainUltimate) ─────────────
                // POC: bright gold #FFD700
                dxTPOPocBrush = MakeBrush(rt, POCColor, 0.95f);

                // VAH/VAL lines: cyan #00FFFF
                dxTPOVALineBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(0f, 1f, 1f, 0.90f));

                // Value Area fill: steel-blue semi-transparent
                float vaAlpha = Math.Max(0.05f, Math.Min(0.50f, ValueAreaOpacity / 100f));
                dxTPOVAFillBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(0.27f, 0.51f, 0.71f, vaAlpha));   // SteelBlue #4682B4

                // IB bracket: white/light grey
                dxTPOIBBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(1f, 1f, 1f, 0.85f));

                // Naked levels: dark orange #FF8C00
                dxTPONakedBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(1f, 0.55f, 0f, 0.90f));

                // TPO label format: Consolas 11pt
                if (dxTPOLabelFormat != null) { dxTPOLabelFormat.Dispose(); dxTPOLabelFormat = null; }
                dxTPOLabelFormat = new SharpDX.DirectWrite.TextFormat(dxWriteFactory, "Consolas", 11f);

                dxReady = true;
            }
            catch (Exception ex)
            {
                Print("IQMainUltimate: CreateDXResources failed [" + ex.GetType().Name + "]: " + ex.Message);
                dxReady = false;
                DisposeDXResources();
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
                System.Windows.Media.Color c;
                try   { c = scb.Color; }
                catch (InvalidOperationException) { c = System.Windows.Media.Colors.White; }
                return new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, opacity));
            }
            return new SharpDX.Direct2D1.SolidColorBrush(rt,
                new SharpDX.Color4(1f, 1f, 1f, opacity));
        }

        private void DisposeDXResources()
        {
            DisposeRef(ref dxWriteFactory);
            DisposeRef(ref dxLabelFormat);
            DisposeRef(ref dxSmallFormat);
            DisposeRef(ref dxDashFormat);

            DisposeRef(ref dxPPBrush);
            DisposeRef(ref dxRLevelBrush);
            DisposeRef(ref dxSLevelBrush);
            DisposeRef(ref dxMLevelBrush);
            DisposeRef(ref dxYesterdayBrush);
            DisposeRef(ref dxLastWeekBrush);
            DisposeRef(ref dxAdrBrush);
            DisposeRef(ref dxAwrBrush);
            DisposeRef(ref dxAmrBrush);
            DisposeRef(ref dxRdBrush);
            DisposeRef(ref dxRwBrush);
            DisposeRef(ref dxDailyOpenBrush);
            DisposeRef(ref dxEthDailyOpenBrush);
            DisposeRef(ref dxAsiaOpenBrush);
            DisposeRef(ref dxLondonOpenBrush);
            DisposeRef(ref dxUsOpenBrush);
            DisposeRef(ref dxPsyBrush);
            DisposeRef(ref dxDashBgBrush);
            DisposeRef(ref dxDashTextBrush);
            DisposeRef(ref dxDashHeaderBrush);
            DisposeRef(ref dxDashAccentBrush);
            DisposeRef(ref dxBullBrush);
            DisposeRef(ref dxBearBrush);
            DisposeRef(ref dxAbsorbBrush);
            DisposeRef(ref dxImbalanceBrush);
            DisposeRef(ref dxFakeBrush);
            DisposeRef(ref dxWickBrush);
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
            DisposeRef(ref dxULZBlueBrush);
            DisposeRef(ref dxULZPinkBrush);
            DisposeRef(ref dxVwapAboveBrush);
            DisposeRef(ref dxVwapBelowBrush);
            DisposeRef(ref dxVwapNeutralBrush);
            DisposeRef(ref dxVwapBand1Brush);
            DisposeRef(ref dxVwapBand2Brush);
            DisposeRef(ref dxVwapBand3Brush);
            DisposeRef(ref dxVwapFillBrush);
            DisposeRef(ref dxOTEBullishBrush);
            DisposeRef(ref dxOTEBearishBrush);
            DisposeRef(ref dxOTELineBrush);
            DisposeRef(ref dxOTEOptimalBrush);

            if (dxSessionBoxBrush != null)
            {
                for (int i = 0; i < dxSessionBoxBrush.Length; i++)
                    DisposeRef(ref dxSessionBoxBrush[i]);
                dxSessionBoxBrush = null;
            }
            if (dxSessionBorderBrush != null)
            {
                for (int i = 0; i < dxSessionBorderBrush.Length; i++)
                    DisposeRef(ref dxSessionBorderBrush[i]);
                dxSessionBorderBrush = null;
            }

            // Enhanced dashboard resources (IQMainUltimate)
            DisposeRef(ref dxMainDashFormat);
            DisposeRef(ref dxEnhDashBgBrush);
            DisposeRef(ref dxEnhDashTextBrush);
            DisposeRef(ref dxEnhDashHeaderBrush);
            DisposeRef(ref dxEnhDashWarningBrush);
            DisposeRef(ref dxEnhDashGreenBrush);
            DisposeRef(ref dxEnhDashRedBrush);
            DisposeRef(ref dxEnhDashNeutralBrush);
            DisposeRef(ref dxEntryLineBrush);
            DisposeRef(ref dxStopLineBrush);
            DisposeRef(ref dxTargetLineBrush);
            DisposeRef(ref _brushDimmedText);
            DisposeRef(ref dxEnhDashFormat);
            DisposeRef(ref dxEnhMonFormat);

            // TPO / Market Profile brushes (IQMainUltimate)
            DisposeRef(ref dxTPOPocBrush);
            DisposeRef(ref dxTPOVALineBrush);
            DisposeRef(ref dxTPOVAFillBrush);
            DisposeRef(ref dxTPOIBBrush);
            DisposeRef(ref dxTPONakedBrush);
            DisposeRef(ref dxTPOLabelFormat);
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
