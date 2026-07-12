// IQKeyLevelsGPU — Standalone key-levels overlay indicator for NinjaTrader 8.
// Features:
//   1. Session Daily POCs — Asia, London, New York (per-session color-coded, extendable up to 7 days)
//   2. Range Daily (RD) High / Low bands
//   3. Psychological (Psy) Levels — Daily, Weekly, Monthly round numbers
//   4. Weekly and Monthly High / Low lines (previous completed period)
//   5. Hourly Opening Lines with configurable hour filter and labels
//   6. Level 2 Bid/Ask Wall detection and rendering
//
// Coexists with IQMainGPU.cs, IQMainUltimate.cs, HourlyOpenStats.cs in the same
// NinjaTrader assembly — all enum names are IQKL-prefixed to avoid conflicts.

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

// ── Enums declared OUTSIDE all namespaces per NT8 requirements ────────────────
// Reference: forum.ninjatrader.com threads #1182932, #95909, #1046853
// IQKL prefix avoids conflicts with IQMLineStyle in IQMainGPU.cs / IQMainUltimate.cs.

/// <summary>Line rendering style for IQKeyLevelsGPU level lines.</summary>
public enum IQKLLineStyle { Solid, Dashed, Dotted }

/// <summary>Label anchor position for IQKeyLevelsGPU level labels.</summary>
public enum IQKLLabelAnchor { LineStart, LineEnd }

/// <summary>Action taken when a gap zone has been fully filled by price.</summary>
public enum IQKLGapFilledAction { Remove, Dim, Keep }

/// <summary>Width/size measurement mode for cluster zones and gap minimum size.</summary>
public enum IQKLWidthMode { FixedPoints, PercentOfADR, Ticks }

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// IQKeyLevelsGPU — GPU-accelerated key levels indicator (IsOverlay = true, no plots).
    /// Provides Session POCs with multi-day extension and optional opacity fade,
    /// Range Daily Hi/Lo, Psy Levels (Daily/Weekly/Monthly), Weekly and Monthly Hi/Lo,
    /// Hourly Opening Lines, and Level 2 Bid/Ask Walls.
    /// </summary>
    public class IQKeyLevelsGPU : Indicator
    {
        // ════════════════════════════════════════════════════════════════════════
        #region Inner types

        /// <summary>One completed or in-progress session POC entry.</summary>
        private class KLPocEntry
        {
            public int      SessionId;        // 0=Asia, 1=London, 2=NY
            public string   SessionName;
            public DateTime SessionDate;      // Calendar date of session START (ET)
            public DateTime SessionStart;     // Full ET DateTime of session start
            public DateTime SessionEnd;       // Full ET DateTime of session end
            public int      StartBarIndex;
            public double   POCPrice;
            public bool     IsComplete;
            public double   CurrentBinSize;   // effective volume-bucket width (auto-coarsens under LimitPocBins)
            public Dictionary<double, double> VolumeProfile = new Dictionary<double, double>();
        }

        /// <summary>One detected POC cluster zone (group of nearby session POCs).</summary>
        private class KLClusterZone
        {
            public double MinPrice;
            public double MaxPrice;
            public int    Count;
            public bool   AlertFired;
        }

        /// <summary>One session Open/Close entry (one per session per day).</summary>
        private class KLSessionOC
        {
            public int      SessionId;        // 0=Asia, 1=London, 2=NY
            public string   SessionName;
            public DateTime SessionDate;      // ET date of session start
            public DateTime SessionStart;     // Full ET DateTime of session start (for window change detection)
            public int      OpenBarIndex;     // bar index where Open was recorded
            public double   OpenPrice;        // first in-session bar Open
            public int      CloseBarIndex;    // bar index of last in-session bar
            public double   ClosePrice;       // last in-session bar Close (updated live)
            public bool     IsComplete;       // session ended, ClosePrice finalized
        }

        /// <summary>One hourly open record.</summary>
        private class KLHourlyOpen
        {
            public int      Hour;
            public double   OpenPrice;
            public int      StartBarIndex;
            public int      EndBarIndex;
            public DateTime BarDate;
        }

        /// <summary>One order-book price level for L2 tracking.</summary>
        private class KLBookLevel
        {
            public double Price;
            public long   Size;
        }

        /// <summary>One detected session gap zone (Globex open vs prior session close).</summary>
        private class KLGapZone
        {
            public DateTime OpenDate;       // ET date of the gap open bar (for label)
            public int      OpenBarIndex;   // bar index of the gap open bar
            public double   ZoneLow;        // fixed lower bound (min of prevClose, openPrice)
            public double   ZoneHigh;       // fixed upper bound (max of prevClose, openPrice)
            public bool     IsGapUp;        // true = open > prevClose
            // NearEdge tracks how far price has penetrated into the zone from the open side:
            //   gap-up: starts = ZoneHigh, decreases toward ZoneLow as price fills from above
            //   gap-down: starts = ZoneLow, increases toward ZoneHigh as price fills from below
            public double   NearEdge;
            public bool     IsFilled;       // true once fully traded through
            public bool     AlertFired;     // one-shot entry-alert flag
            public DateTime CreationDate;   // ET date at creation (for age-out)
        }

        /// <summary>Queued label for the per-frame level-label merge system (Psy, Hi-Lo, RD).</summary>
        private struct KLLevelLabel
        {
            public double   Price;
            public string   Prefix;       // "DPsy","WPsy","MPsy","LWH","LWL","LMH","LML","RD"
            public string   Side;         // "H","L", or "" when side is already in Prefix
            public float    LabelXHint;   // desired X before collision
            public float    LabelWidth;
            public bool     UseRightBucket;
            public float    LineY;        // Y from chartScale (for collision seed)
            public SharpDX.Direct2D1.SolidColorBrush Brush;
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Private fields — ET timezone helper

        private static readonly TimeZoneInfo EtZone = SafeFindEtZone();

        private static TimeZoneInfo SafeFindEtZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch (Exception)
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
                catch (Exception) { return TimeZoneInfo.CreateCustomTimeZone("ET-Fallback", TimeSpan.FromHours(-5), "ET-Fallback", "ET-Fallback"); }
            }
        }

        private DateTime BarTimeEt()
        {
            DateTime t = Bars.GetTime(CurrentBar);
            DateTime tUnspec = DateTime.SpecifyKind(t, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTime(tUnspec, Bars.TradingHours.TimeZoneInfo, EtZone);
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Private fields — day/week/month tracking

        private double   _dayHigh, _dayLow;
        private DateTime _currentDay = DateTime.MinValue;

        private double   _weekHigh, _weekLow;
        private double   _lastWeekHigh, _lastWeekLow;
        private bool     _weekLoaded;
        private DateTime _currentWeekStart = DateTime.MinValue;

        private double   _monthHigh, _monthLow;
        private double   _prevMonthHigh, _prevMonthLow;
        private bool     _monthLoaded;
        private int      _currentMonth;

        private double   _psyDayHigh, _psyDayLow;
        private double   _psyWeekHigh, _psyWeekLow;
        private double   _psyMonthHigh, _psyMonthLow;

        // Cached rounded Psy levels — recomputed once per bar in OnBarUpdate instead of every
        // OnRender frame (these values are invariant for the life of the current bar).
        private double   _cachedDPsyH, _cachedDPsyL;
        private double   _cachedWPsyH, _cachedWPsyL;
        private double   _cachedMPsyH, _cachedMPsyL;

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Private fields — RD Range Daily

        private Queue<double> _rdRanges;
        private double        _rdValue, _rdHigh, _rdLow;
        private double        _rdDayOpen;       // today's Open[0], anchor used when UseOnlyCompletedDaysForRD is true

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Private fields — Session POCs

        private KLPocEntry[] _activePocSessions; // index: 0=Asia,1=London,2=NY
        private List<KLPocEntry> _pocList;
        private readonly object  _sessionLock = new object();

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Private fields — POC Clusters

        private List<KLClusterZone> _clusterZones = new List<KLClusterZone>();

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Private fields — Session Open/Close

        // Max OC entries = 3 sessions × 7 days
        private const int MaxOcListSize = 21;

        private KLSessionOC[] _activeOcSessions; // index: 0=Asia,1=London,2=NY
        private List<KLSessionOC> _ocList;

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Private fields — Hourly Opens

        private List<KLHourlyOpen> _hourlyOpens;
        private KLHourlyOpen        _currentHourlyOpen;
        private int                 _lastHour = -1;

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Private fields — Gap Zones

        private const int MaxGapZones = 14;

        private List<KLGapZone>  _gapZones;
        private DateTime         _lastGapCheckSessionStart = DateTime.MinValue;
        private double           _gapScanPrevClose;  // Close[1] captured at IsFirstTickOfBar

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Private fields — L2 Order Book

        private Dictionary<double, KLBookLevel> _bidBook;
        private Dictionary<double, KLBookLevel> _askBook;
        private double _wallBidPrice;
        private long   _wallBidSize;
        private double _wallAskPrice;
        private long   _wallAskSize;
        private bool   _level2Available;

        // Running size totals — updated incrementally in OnMarketDepth so DetectOrderBookWalls
        // no longer needs a full summation pass over the book on every event.
        private long   _bidSizeSum;
        private long   _askSizeSum;

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Private fields — SharpDX resources

        // Max POC entries = 3 sessions × 7 days (matches PocExtensionDays max)
        private const int MaxPocListSize = 21;

        // Label render widths — must match the RectangleF width arg passed to DrawText
        private const float PocLabelWidth      = 220f;  // POC/OC labels  "Asia Open MM/DD 99999.99"
        private const float LevelLabelWidth     = 200f;  // General labels "RD H / Psy H / LWH …"
        private const float ClusterLabelWidth   = 300f;  // Cluster labels "POC Cluster ×4  29800.75–29829.25  (A,L,N)"
        private const float GapLabelWidth       = 310f;  // Gap zone labels "7/12 Gap  29980.25–30038.50"
        private const double ClusterMatchToleranceFactor = 0.001;
        private const double ClusterZonePaddingTickFactor = 2.0;
        private const double ClusterZonePaddingMaxPoints = 2.0;
        private const double ClusterZonePaddingPercent = 0.10;

        // Cached ET date of the most-recently-processed bar (used in OnRender for age checks)
        private DateTime _latestBarEtDate = DateTime.MinValue;

        // Cached Y of the current price line (set each OnRender frame; used for keep-out band)
        private float _currentPriceY = float.NaN;

        private bool dxReady;

        private SharpDX.DirectWrite.Factory      _dxWriteFactory;
        private SharpDX.DirectWrite.TextFormat   _dxLabelFormat;

        // Session POC brushes (one per session)
        private SharpDX.Direct2D1.SolidColorBrush _dxAsiaPocBrush;
        private SharpDX.Direct2D1.SolidColorBrush _dxLondonPocBrush;
        private SharpDX.Direct2D1.SolidColorBrush _dxNyPocBrush;

        // RD Range
        private SharpDX.Direct2D1.SolidColorBrush _dxRdBrush;

        // Psy levels
        private SharpDX.Direct2D1.SolidColorBrush _dxDailyPsyBrush;
        private SharpDX.Direct2D1.SolidColorBrush _dxWeeklyPsyBrush;
        private SharpDX.Direct2D1.SolidColorBrush _dxMonthlyPsyBrush;

        // Weekly / Monthly Hi-Lo
        private SharpDX.Direct2D1.SolidColorBrush _dxWeekHlBrush;
        private SharpDX.Direct2D1.SolidColorBrush _dxMonthHlBrush;

        // Hourly opens
        private SharpDX.Direct2D1.SolidColorBrush _dxHourlyOpenBrush;

        // L2 walls
        private SharpDX.Direct2D1.SolidColorBrush _dxWallBidBrush;
        private SharpDX.Direct2D1.SolidColorBrush _dxWallAskBrush;

        // Session Open/Close brushes (one per session; Open=Solid, Close=Dashed)
        private SharpDX.Direct2D1.SolidColorBrush _dxAsiaOcBrush;
        private SharpDX.Direct2D1.SolidColorBrush _dxLondonOcBrush;
        private SharpDX.Direct2D1.SolidColorBrush _dxNyOcBrush;

        // POC Cluster zone shading brush
        private SharpDX.Direct2D1.SolidColorBrush _dxClusterBrush;

        // Gap zone brushes
        private SharpDX.Direct2D1.SolidColorBrush _dxGapUpBrush;
        private SharpDX.Direct2D1.SolidColorBrush _dxGapDownBrush;

        // Label collision avoidance — cleared per OnRender frame, bucketed by screen region so
        // left-anchored and right-anchored labels only collide against their own bucket.
        private readonly HashSet<int>    _usedLabelYLeft       = new HashSet<int>();
        private readonly HashSet<int>    _usedLabelYRight      = new HashSet<int>();

        // Per-frame set of POC prices that belong to at least one rendered cluster zone.
        // Populated by RenderPocClusters (called first in OnRender) and read by
        // RenderSessionPocs to suppress individual labels when SuppressPocLabelsInClusters = true.
        private readonly HashSet<double> _pocPricesInClusters  = new HashSet<double>();

        // Per-frame pending level labels for the coincident-merge system (Psy, Hi-Lo, RD).
        // Lines are drawn inline; labels are queued here and flushed merged after all helpers.
        private readonly List<KLLevelLabel> _pendingLevelLabels = new List<KLLevelLabel>();

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region OnStateChange

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                // 1a. Session Windows (ET) — Asia 18:00→03:00, London 03:00→11:30, NY 08:00→16:00
                AsiaStartHour   = 18; AsiaStartMin   = 0;
                AsiaEndHour     = 3;  AsiaEndMin     = 0;
                LondonStartHour = 3;  LondonStartMin = 0;
                LondonEndHour   = 11; LondonEndMin   = 30;
                NyStartHour     = 8;  NyStartMin     = 0;
                NyEndHour       = 16; NyEndMin       = 0;

                // 1a. Session POCs — Asia
                ShowAsiaPoc         = true;
                AsiaPocColor        = Brushes.Crimson;
                AsiaPocOpacity      = 80;
                AsiaPocLineStyle    = IQKLLineStyle.Solid;
                AsiaPocThickness    = 2;
                ShowAsiaPocLabels   = true;

                // 1b. Session POCs — London
                ShowLondonPoc       = true;
                LondonPocColor      = Brushes.SteelBlue;
                LondonPocOpacity    = 80;
                LondonPocLineStyle  = IQKLLineStyle.Solid;
                LondonPocThickness  = 2;
                ShowLondonPocLabels = true;

                // 1c. Session POCs — New York
                ShowNyPoc           = true;
                NyPocColor          = Brushes.ForestGreen;
                NyPocOpacity        = 80;
                NyPocLineStyle      = IQKLLineStyle.Solid;
                NyPocThickness      = 2;
                ShowNyPocLabels     = true;

                // 1d. Session POCs — General
                PocExtensionDays  = 7;
                PocBinMultiplier  = 1;
                FadeOlderPocs     = true;
                LimitPocBins      = 500;

                // 1c. POC Clusters
                ShowPocClusters        = true;
                ClusterZoneWidthPoints = 35.0;
                ClusterWidthMode       = IQKLWidthMode.FixedPoints;
                ClusterWidthPctADR     = 2.0;
                ClusterWidthTicks      = 140;
                ClusterMinPocCount     = 2;
                ClusterColor           = Brushes.Gold;
                ClusterOpacity         = 15;
                ShowClusterLabels      = true;
                ClusterAudioAlert      = false;
                SuppressPocLabelsInClusters = true;

                // 1b. Session Opens/Closes — Asia (kept for serialization compatibility only;
                // Asia no longer renders Open/Close — see IsOcSessionEnabled)
                ShowAsiaOC          = true;
                ShowAsiaOCOpen      = true;
                ShowAsiaOCClose     = true;
                AsiaOcColor         = Brushes.Crimson;
                AsiaOcOpacity       = 80;
                AsiaOcOpenStyle     = IQKLLineStyle.Solid;
                AsiaOcCloseStyle    = IQKLLineStyle.Dashed;
                AsiaOcThickness     = 1;
                ShowAsiaOCLabels    = true;

                // 1b. Session Opens/Closes — London
                ShowLondonOC         = true;
                ShowLondonOCOpen     = true;
                ShowLondonOCClose    = true;
                LondonOcColor        = Brushes.SteelBlue;
                LondonOcOpacity      = 80;
                LondonOcOpenStyle    = IQKLLineStyle.Solid;
                LondonOcCloseStyle   = IQKLLineStyle.Dashed;
                LondonOcThickness    = 1;
                ShowLondonOCLabels   = true;
                EndLondonAtNyOpen    = false;

                // 1b. Session Opens/Closes — New York
                ShowNyOC             = true;
                ShowNyOCOpen         = true;
                ShowNyOCClose        = true;
                NyOcColor            = Brushes.ForestGreen;
                NyOcOpacity          = 80;
                NyOcOpenStyle        = IQKLLineStyle.Solid;
                NyOcCloseStyle       = IQKLLineStyle.Dashed;
                NyOcThickness        = 1;
                ShowNyOCLabels       = true;

                // 1b. Session Opens/Closes — General
                OcExtensionDays     = 7;
                FadeOlderOC         = true;

                // 2. Range Daily
                ShowRd          = true;
                RdColor         = Brushes.DodgerBlue;
                RdOpacity       = 70;
                RdLineStyle     = IQKLLineStyle.Dashed;
                RdThickness     = 1;
                ShowRdLabels    = true;
                RdLength        = 15;
                UseOnlyCompletedDaysForRD = false;

                // 3. Psy Levels
                ShowDailyPsy          = true;
                DailyPsyColor         = Brushes.Orange;
                DailyPsyOpacity       = 80;
                DailyPsyLineStyle     = IQKLLineStyle.Dotted;
                ShowDailyPsyLabels    = true;
                ShowWeeklyPsy         = true;
                WeeklyPsyColor        = Brushes.Gold;
                WeeklyPsyOpacity      = 80;
                WeeklyPsyLineStyle    = IQKLLineStyle.Dotted;
                ShowWeeklyPsyLabels   = true;
                ShowMonthlyPsy        = true;
                MonthlyPsyColor       = Brushes.MediumPurple;
                MonthlyPsyOpacity     = 80;
                MonthlyPsyLineStyle   = IQKLLineStyle.Dotted;
                ShowMonthlyPsyLabels  = true;
                PsyRoundIncrement     = 50;

                // 4. Weekly / Monthly Hi-Lo
                ShowLastWeek        = true;
                LastWeekColor       = Brushes.MediumSeaGreen;
                LastWeekOpacity     = 80;
                LastWeekLineStyle   = IQKLLineStyle.Dashed;
                LastWeekThickness   = 1;
                ShowLastWeekLabels  = true;
                ShowLastMonth       = true;
                LastMonthColor      = Brushes.IndianRed;
                LastMonthOpacity    = 80;
                LastMonthLineStyle  = IQKLLineStyle.Dashed;
                LastMonthThickness  = 1;
                ShowLastMonthLabels = true;

                // 5. Hourly Opens
                ShowHourlyOpens        = true;
                HourlyOpenColor        = Brushes.Yellow;
                HourlyOpenOpacity      = 90;
                HourlyOpenLineStyle    = IQKLLineStyle.Solid;
                HourlyOpenThickness    = 1;
                ShowHourlyOpenLabels   = true;
                HourlyStartHour        = 0;
                HourlyEndHour          = 24;
                MaxHourlyLinesToShow   = 6;

                // 6. Level 2 Walls
                EnableLevel2      = false;
                WallMultiplier    = 5;
                ShowWallLines     = true;
                BidWallColor      = Brushes.LimeGreen;
                BidWallOpacity    = 90;
                AskWallColor      = Brushes.Crimson;
                AskWallOpacity    = 90;
                WallLineThickness = 2;
                ShowWallLabels    = true;

                // 7. General
                LabelFontSize    = 11;
                GlobalShowLabels = true;
                LabelAnchor      = IQKLLabelAnchor.LineStart;
                ExtendLinesToRightEdge = true;
                MergeCoincidentLabels  = true;

                // 1d. Gap Zones
                ShowGapZones       = true;
                GapUpColor         = Brushes.MediumPurple;
                GapDownColor       = Brushes.Teal;
                GapOpacity         = 20;
                GapMaxAgeDays      = 7;
                ShowGapLabels      = true;
                GapFilledAction    = IQKLGapFilledAction.Remove;
                GapDimOpacity      = 8;
                GapAudioAlert      = false;
                GapMinSizeMode     = IQKLWidthMode.PercentOfADR;
                GapMinSizePoints   = 10.0;
                GapMinSizePctADR   = 1.0;
                GapMinSizeTicks    = 40;

                // Indicator meta
                Name               = "IQKeyLevelsGPU";
                Description        = "GPU-accelerated key levels: Session POCs, Session Opens/Closes, RD Hi/Lo, Psy Levels, Weekly/Monthly Hi-Lo, Hourly Opens, L2 Walls.";
                IsOverlay          = true;
                IsAutoScale        = false;
                DisplayInDataBox   = false;
                DrawOnPricePanel   = true;
                PaintPriceMarkers  = false;
            }
            else if (State == State.Configure)
            {
                Calculate                = Calculate.OnPriceChange;
                IsSuspendedWhileInactive = false;
                MaximumBarsLookBack      = MaximumBarsLookBack.Infinite;
            }
            else if (State == State.DataLoaded)
            {
                _rdRanges          = new Queue<double>();
                _hourlyOpens       = new List<KLHourlyOpen>();
                _pocList           = new List<KLPocEntry>();
                _activePocSessions = new KLPocEntry[3];
                _ocList            = new List<KLSessionOC>();
                _activeOcSessions  = new KLSessionOC[3];
                _bidBook           = new Dictionary<double, KLBookLevel>();
                _askBook           = new Dictionary<double, KLBookLevel>();
                _clusterZones      = new List<KLClusterZone>();
                _gapZones          = new List<KLGapZone>();
                _bidSizeSum        = 0;
                _askSizeSum        = 0;

                _currentMonth = -1;
            }
            else if (State == State.Terminated)
            {
                DisposeDXResources();
            }
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region OnBarUpdate

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 1) return;

            // ── Per-bar accumulation guarded by IsFirstTickOfBar ──────────────
            if (IsFirstTickOfBar)
            {
                // Capture previous bar's close BEFORE any session processing (used by gap detection).
                _gapScanPrevClose = Close[1];

                DateTime barTime = Time[0];
                DateTime barEt   = BarTimeEt();
                _latestBarEtDate = barEt.Date;  // cache ET date for use in OnRender

                // ── Day rollover ──────────────────────────────────────────────
                if (barTime.Date != _currentDay)
                {
                    if (_currentDay != DateTime.MinValue)
                    {
                        // Store completed day range for RD calculation
                        double dRange = _dayHigh - _dayLow;
                        if (_rdRanges.Count >= RdLength) _rdRanges.Dequeue();
                        _rdRanges.Enqueue(dRange);
                    }
                    _dayHigh    = High[0];
                    _dayLow     = Low[0];
                    _currentDay = barTime.Date;
                    _rdDayOpen  = Open[0];

                    _psyDayHigh = High[0];
                    _psyDayLow  = Low[0];
                }
                else
                {
                    if (High[0] > _dayHigh) _dayHigh = High[0];
                    if (Low[0]  < _dayLow)  _dayLow  = Low[0];

                    if (High[0] > _psyDayHigh) _psyDayHigh = High[0];
                    if (Low[0]  < _psyDayLow)  _psyDayLow  = Low[0];
                }

                // ── Week rollover — Sunday-anchored (mirrors IQMainUltimate) ─
                DateTime weekStart = barTime.Date.AddDays(-(int)barTime.DayOfWeek);
                if (weekStart != _currentWeekStart)
                {
                    if (_weekLoaded)
                    {
                        _lastWeekHigh = _weekHigh;
                        _lastWeekLow  = _weekLow;
                    }
                    _weekHigh         = High[0];
                    _weekLow          = Low[0];
                    _psyWeekHigh      = High[0];
                    _psyWeekLow       = Low[0];
                    _currentWeekStart = weekStart;
                    _weekLoaded       = true;
                }
                else if (_weekLoaded)
                {
                    if (High[0] > _weekHigh) _weekHigh = High[0];
                    if (Low[0]  < _weekLow)  _weekLow  = Low[0];
                    if (High[0] > _psyWeekHigh) _psyWeekHigh = High[0];
                    if (Low[0]  < _psyWeekLow)  _psyWeekLow  = Low[0];
                }

                // ── Month rollover ────────────────────────────────────────────
                if (barTime.Month != _currentMonth)
                {
                    if (_monthLoaded)
                    {
                        _prevMonthHigh = _monthHigh;
                        _prevMonthLow  = _monthLow;
                    }
                    _monthHigh     = High[0];
                    _monthLow      = Low[0];
                    _psyMonthHigh  = High[0];
                    _psyMonthLow   = Low[0];
                    _currentMonth  = barTime.Month;
                    _monthLoaded   = true;
                }
                else if (_monthLoaded)
                {
                    if (High[0] > _monthHigh)  _monthHigh  = High[0];
                    if (Low[0]  < _monthLow)   _monthLow   = Low[0];
                    if (High[0] > _psyMonthHigh) _psyMonthHigh = High[0];
                    if (Low[0]  < _psyMonthLow)  _psyMonthLow  = Low[0];
                }

                // ── Session POC accumulation ──────────────────────────────────
                UpdateSessionPocs(barEt);

                // ── Gap zone detection (runs after session start is known) ────
                DetectGapZone(barEt);

                // ── Session Open/Close tracking ───────────────────────────────
                UpdateSessionOC(barEt);

                // ── Hourly open tracking ──────────────────────────────────────
                UpdateHourlyOpens(barEt);

                // ── Cache rounded Psy levels once per bar (invariant until next bar) ──
                double psyStep = PsyRoundIncrement * TickSize;
                if (psyStep > 0)
                {
                    _cachedDPsyH = Math.Ceiling(_psyDayHigh   / psyStep) * psyStep;
                    _cachedDPsyL = Math.Floor(_psyDayLow      / psyStep) * psyStep;
                    _cachedWPsyH = Math.Ceiling(_psyWeekHigh  / psyStep) * psyStep;
                    _cachedWPsyL = Math.Floor(_psyWeekLow     / psyStep) * psyStep;
                    _cachedMPsyH = Math.Ceiling(_psyMonthHigh / psyStep) * psyStep;
                    _cachedMPsyL = Math.Floor(_psyMonthLow    / psyStep) * psyStep;
                }

                // ── POC cluster zones — recomputed once per bar (first tick only) ──
                RecomputePocClusters(barEt);
            }

            // ── RD high/low recalculate every tick (uses current day stats) ──
            if (_rdRanges.Count > 0)
            {
                _rdValue = _rdRanges.Average();
                if (UseOnlyCompletedDaysForRD)
                {
                    double half = _rdValue / 2.0;
                    _rdHigh = _rdDayOpen + half;
                    _rdLow  = _rdDayOpen - half;
                }
                else
                {
                    double rdSlack = (_rdValue - (_dayHigh - _dayLow)) / 2.0;
                    _rdHigh = _dayHigh + rdSlack;
                    _rdLow  = _dayLow  - rdSlack;
                }
            }

            // Update live hourly open end bar
            if (_currentHourlyOpen != null)
                _currentHourlyOpen.EndBarIndex = CurrentBar;

            // ── Gap zone fill tracking — checked every tick for responsiveness ──
            UpdateGapZoneFill();

            // ── POC cluster audio alert — checked every tick for responsiveness ──
            CheckClusterAlerts(Close[0]);

            // ── Gap zone audio alert — checked every tick ──────────────────────
            CheckGapAlerts(Close[0]);
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Session POC helpers

        private void UpdateSessionPocs(DateTime barEt)
        {
            for (int id = 0; id < 3; id++)
            {
                if (!IsPocSessionEnabled(id)) continue;

                DateTime sStart, sEnd;
                GetPocSessionWindow(id, barEt, out sStart, out sEnd);
                bool inSession = barEt >= sStart && barEt < sEnd;

                if (inSession)
                {
                    // Create new entry if session changed or no active entry
                    if (_activePocSessions[id] == null || _activePocSessions[id].SessionStart != sStart)
                    {
                        if (_activePocSessions[id] != null && !_activePocSessions[id].IsComplete)
                            FinalizePocSession(_activePocSessions[id]);

                        var entry = new KLPocEntry
                        {
                            SessionId    = id,
                            SessionName  = GetPocSessionName(id),
                            SessionDate  = sStart.Date,
                            SessionStart = sStart,
                            SessionEnd   = sEnd,
                            StartBarIndex = CurrentBar,
                            IsComplete   = false,
                            CurrentBinSize = TickSize * Math.Max(1, PocBinMultiplier)
                        };
                        _activePocSessions[id] = entry;
                        lock (_sessionLock)
                        {
                            // Prune entries outside the PocExtensionDays window using the bar's ET date
                            // (keep exactly PocExtensionDays calendar dates: today plus the previous PocExtensionDays-1)
                            DateTime cutoff = barEt.Date.AddDays(-(PocExtensionDays - 1));
                            for (int i = _pocList.Count - 1; i >= 0; i--)
                                if (_pocList[i].SessionDate < cutoff) _pocList.RemoveAt(i);
                            if (_pocList.Count >= MaxPocListSize) _pocList.RemoveAt(0);
                            _pocList.Add(entry);
                        }
                    }

                    var sess = _activePocSessions[id];

                    // Accumulate volume at typical price bucketed to the session's current bin size
                    double barVol = Volume[0];
                    if (barVol > 0)
                    {
                        double tp     = (High[0] + Low[0] + Close[0]) / 3.0;
                        double bucket = Math.Round(tp / sess.CurrentBinSize) * sess.CurrentBinSize;

                        if (sess.VolumeProfile.ContainsKey(bucket))
                            sess.VolumeProfile[bucket] += barVol;
                        else
                            sess.VolumeProfile[bucket]  = barVol;

                        // Performance guard: if the profile grows past the configured bin cap,
                        // auto-coarsen by doubling the bin size and re-bucketing existing data
                        // instead of dropping it.
                        if (sess.VolumeProfile.Count > LimitPocBins)
                            CoarsenVolumeProfile(sess);
                    }

                    // Recalculate POC
                    CalcPoc(sess);
                }
                else
                {
                    // Session has ended — finalize if still open
                    if (_activePocSessions[id] != null && !_activePocSessions[id].IsComplete)
                    {
                        FinalizePocSession(_activePocSessions[id]);
                        _activePocSessions[id] = null;
                    }
                }
            }
        }

        /// <summary>Doubles a session's volume-profile bin size and re-buckets existing entries,
        /// keeping data intact while bounding dictionary growth (LimitPocBins).</summary>
        private void CoarsenVolumeProfile(KLPocEntry sess)
        {
            sess.CurrentBinSize *= 2.0;
            var coarsened = new Dictionary<double, double>();
            foreach (var kv in sess.VolumeProfile)
            {
                double bucket = Math.Round(kv.Key / sess.CurrentBinSize) * sess.CurrentBinSize;
                if (coarsened.ContainsKey(bucket))
                    coarsened[bucket] += kv.Value;
                else
                    coarsened[bucket]  = kv.Value;
            }
            sess.VolumeProfile = coarsened;
        }

        private void FinalizePocSession(KLPocEntry entry)
        {
            CalcPoc(entry);
            entry.IsComplete = true;
        }

        private void CalcPoc(KLPocEntry entry)
        {
            if (entry.VolumeProfile == null || entry.VolumeProfile.Count == 0) return;
            double maxVol = 0;
            double poc    = 0;
            foreach (var kv in entry.VolumeProfile)
            {
                if (kv.Value > maxVol) { maxVol = kv.Value; poc = kv.Key; }
            }
            entry.POCPrice = poc;
        }

        private void GetPocSessionWindow(int sessionId, DateTime barEt, out DateTime start, out DateTime end)
        {
            switch (sessionId)
            {
                case 0:
                    GetConfigurableWindow(barEt, AsiaStartHour, AsiaStartMin, AsiaEndHour, AsiaEndMin, out start, out end);
                    break;
                case 1:
                    GetConfigurableWindow(barEt, LondonStartHour, LondonStartMin, LondonEndHour, LondonEndMin, out start, out end);
                    // When EndLondonAtNyOpen is enabled, London's POC profile accumulates only until
                    // the configured NY session start instead of the configured London session end.
                    // Anchor the NY open to the session start's date and roll forward a day when it
                    // lands at/before the start (cross-midnight London windows), so end > start.
                    if (EndLondonAtNyOpen)
                    {
                        end = start.Date.AddHours(NyStartHour).AddMinutes(NyStartMin);
                        if (end <= start) end = end.AddDays(1);
                    }
                    break;
                case 2:
                    GetConfigurableWindow(barEt, NyStartHour, NyStartMin, NyEndHour, NyEndMin, out start, out end);
                    break;
                default:
                    start = barEt.Date; end = barEt.Date; break;
            }
        }

        // Session OC window always uses the canonical configured session times (London always ends
        // at LondonEndHour/Min, regardless of EndLondonAtNyOpen which only affects the POC profile).
        private void GetOcSessionWindow(int sessionId, DateTime barEt, out DateTime start, out DateTime end)
        {
            switch (sessionId)
            {
                case 0: GetConfigurableWindow(barEt, AsiaStartHour, AsiaStartMin, AsiaEndHour, AsiaEndMin, out start, out end); break;
                case 1: GetConfigurableWindow(barEt, LondonStartHour, LondonStartMin, LondonEndHour, LondonEndMin, out start, out end); break;
                case 2: GetConfigurableWindow(barEt, NyStartHour, NyStartMin, NyEndHour, NyEndMin, out start, out end); break;
                default: start = barEt.Date; end = barEt.Date; break;
            }
        }

        /// <summary>
        /// Resolves a configurable ET session window for the bar's calendar date. Handles both
        /// same-day windows (start &lt;= end, e.g. London 03:00-11:30) and cross-midnight windows
        /// (end &lt;= start, e.g. Asia 18:00-03:00) generically, including the Sunday 18:00 ET
        /// weekend rollover — the window is always anchored to the bar's own ET calendar date, so
        /// a Sunday-evening bar correctly opens a session that spans into Monday.
        /// </summary>
        private static void GetConfigurableWindow(DateTime barEt, int startHour, int startMin, int endHour, int endMin,
            out DateTime start, out DateTime end)
        {
            DateTime today   = barEt.Date;
            TimeSpan startTod = new TimeSpan(startHour, startMin, 0);
            TimeSpan endTod   = new TimeSpan(endHour, endMin, 0);

            if (startTod < endTod)
            {
                // Same calendar day window
                start = today.Add(startTod);
                end   = today.Add(endTod);
            }
            else
            {
                // Cross-midnight window (covers startTod == endTod as a full 24h wrap too)
                if (barEt.TimeOfDay >= startTod)
                { start = today.Add(startTod);             end = today.AddDays(1).Add(endTod); }
                else
                { start = today.AddDays(-1).Add(startTod); end = today.Add(endTod); }
            }
        }

        private bool IsPocSessionEnabled(int sessionId)
        {
            switch (sessionId) { case 0: return ShowAsiaPoc; case 1: return ShowLondonPoc; case 2: return ShowNyPoc; default: return false; }
        }

        private bool IsOcSessionEnabled(int sessionId)
        {
            // Asia keeps ONLY its POC — Open/Close is intentionally disabled here regardless of
            // the legacy ShowAsiaOC* properties (retained solely for workspace/template deserialization).
            switch (sessionId) { case 0: return false; case 1: return ShowLondonOC; case 2: return ShowNyOC; default: return false; }
        }

        private string GetPocSessionName(int sessionId)
        {
            switch (sessionId) { case 0: return "Asia"; case 1: return "London"; case 2: return "NY"; default: return ""; }
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region POC Cluster helpers

        private void RecomputePocClusters(DateTime barEt)
        {
            if (!ShowPocClusters)
            {
                if (_clusterZones.Count > 0) _clusterZones = new List<KLClusterZone>();
                return;
            }

            List<KLPocEntry> snapshot;
            lock (_sessionLock) { snapshot = _pocList.ToList(); }

            DateTime etToday = barEt.Date;
            var prices = new List<double>();
            foreach (KLPocEntry e in snapshot)
            {
                if (e.POCPrice == 0) continue;
                int daysOld = (etToday - e.SessionDate).Days;
                if (daysOld < 0 || daysOld >= PocExtensionDays) continue;
                prices.Add(e.POCPrice);
            }
            prices.Sort();

            var newZones = new List<KLClusterZone>();
            int idx = 0;
            while (idx < prices.Count)
            {
                int    j        = idx;
                double groupMin = prices[idx];
                double groupMax = prices[idx];
                while (j + 1 < prices.Count && (prices[j + 1] - groupMin) <= ComputeEffectiveClusterWidth())
                {
                    j++;
                    groupMax = prices[j];
                }

                int count = j - idx + 1;
                if (count >= ClusterMinPocCount)
                {
                    // Preserve the alert-fired flag from the previous frame's matching zone so a
                    // resting price doesn't re-trigger the alert on every recompute.
                    bool fired = false;
                    double tol = Math.Max(TickSize / 2.0, Math.Max(1e-9, ClusterZoneWidthPoints * ClusterMatchToleranceFactor));
                    foreach (KLClusterZone oldZone in _clusterZones)
                    {
                        if (Math.Abs(oldZone.MinPrice - groupMin) < tol &&
                            Math.Abs(oldZone.MaxPrice - groupMax) < tol)
                        { fired = oldZone.AlertFired; break; }
                    }
                    newZones.Add(new KLClusterZone { MinPrice = groupMin, MaxPrice = groupMax, Count = count, AlertFired = fired });
                }
                idx = j + 1;
            }

            _clusterZones = newZones;
        }

        /// <summary>Fires a one-shot audio alert the first time price enters a cluster zone, and
        /// resets the fired flag once price leaves it. Alerts only during State.Realtime to avoid
        /// spamming during historical load/replay.</summary>
        private void CheckClusterAlerts(double price)
        {
            // Snapshot the list reference: RecomputePocClusters can replace _clusterZones between
            // the count check and the index access, which could otherwise throw or drop updates.
            List<KLClusterZone> zones = _clusterZones;
            if (!ClusterAudioAlert || zones.Count == 0) return;

            for (int i = 0; i < zones.Count; i++)
            {
                KLClusterZone z = zones[i];
                bool inside = price >= z.MinPrice && price <= z.MaxPrice;
                if (inside)
                {
                    if (!z.AlertFired)
                    {
                        z.AlertFired = true;
                        if (State == State.Realtime)
                            Alert("IQKL_Cluster_" + i, Priority.Low,
                                "IQKeyLevelsGPU: Price entered POC Cluster ×" + z.Count,
                                NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10,
                                ClusterColor, Brushes.Black);
                    }
                }
                else
                {
                    z.AlertFired = false;
                }
            }
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Gap Zone helpers

        /// <summary>Detects a session gap when the Globex session (Asia) opens. Called once per
        /// new session start from OnBarUpdate / IsFirstTickOfBar.</summary>
        private void DetectGapZone(DateTime barEt)
        {
            if (!ShowGapZones) return;

            // Only fires at the start of the Asia (Globex) session
            DateTime sStart, sEnd;
            GetConfigurableWindow(barEt, AsiaStartHour, AsiaStartMin, AsiaEndHour, AsiaEndMin, out sStart, out sEnd);
            bool inAsia = barEt >= sStart && barEt < sEnd;
            if (!inAsia) return;

            // Only fire once per session start
            if (sStart == _lastGapCheckSessionStart) return;
            _lastGapCheckSessionStart = sStart;

            if (CurrentBar < 2) return;

            double prevClose = _gapScanPrevClose;
            double openPrice = Open[0];
            if (prevClose == 0 || openPrice == 0) return;

            double gapSize = Math.Abs(openPrice - prevClose);
            if (gapSize < ComputeEffectiveGapMinSize()) return;

            bool   isGapUp  = openPrice > prevClose;
            double zoneLow  = Math.Min(prevClose, openPrice);
            double zoneHigh = Math.Max(prevClose, openPrice);

            var zone = new KLGapZone
            {
                OpenDate     = barEt.Date,
                OpenBarIndex = CurrentBar,
                ZoneLow      = zoneLow,
                ZoneHigh     = zoneHigh,
                IsGapUp      = isGapUp,
                // NearEdge starts at the "open side" — gap-up: starts at ZoneHigh (=open),
                // gap-down: starts at ZoneLow (=open). Both move toward the unfilled edge.
                NearEdge     = isGapUp ? zoneHigh : zoneLow,
                IsFilled     = false,
                AlertFired   = false,
                CreationDate = barEt.Date
            };

            lock (_sessionLock)
            {
                DateTime cutoff = barEt.Date.AddDays(-Math.Max(1, GapMaxAgeDays));
                for (int i = _gapZones.Count - 1; i >= 0; i--)
                {
                    KLGapZone gz = _gapZones[i];
                    if (gz.CreationDate < cutoff) { _gapZones.RemoveAt(i); continue; }
                    if (gz.IsFilled && GapFilledAction == IQKLGapFilledAction.Remove) _gapZones.RemoveAt(i);
                }
                if (_gapZones.Count >= MaxGapZones) _gapZones.RemoveAt(0);
                _gapZones.Add(zone);
            }
        }

        /// <summary>Updates the partial-fill NearEdge for every unfilled gap zone using the
        /// current bar's High/Low. Called every tick for responsiveness.</summary>
        private void UpdateGapZoneFill()
        {
            List<KLGapZone> zones = _gapZones;
            if (zones == null || zones.Count == 0) return;

            double lo = Low[0];
            double hi = High[0];

            for (int i = 0; i < zones.Count; i++)
            {
                KLGapZone z = zones[i];
                if (z.IsFilled) continue;

                if (z.IsGapUp)
                {
                    // Track lowest price seen (fills zone from the open/top side downward)
                    if (lo < z.NearEdge) z.NearEdge = lo;
                    if (z.NearEdge <= z.ZoneLow) z.IsFilled = true;
                }
                else
                {
                    // Track highest price seen (fills zone from the open/bottom side upward)
                    if (hi > z.NearEdge) z.NearEdge = hi;
                    if (z.NearEdge >= z.ZoneHigh) z.IsFilled = true;
                }
            }
        }

        /// <summary>Fires a one-shot audio alert the first time price enters an unfilled gap zone.
        /// Alerts only during State.Realtime to avoid spamming during historical load/replay.</summary>
        private void CheckGapAlerts(double price)
        {
            if (!GapAudioAlert || State != State.Realtime) return;
            List<KLGapZone> zones = _gapZones;
            if (zones == null || zones.Count == 0) return;

            for (int i = 0; i < zones.Count; i++)
            {
                KLGapZone z = zones[i];
                if (z.IsFilled || z.AlertFired) continue;

                // Unfilled portion: gap-up → [ZoneLow, NearEdge]; gap-down → [NearEdge, ZoneHigh]
                double dispLow  = z.IsGapUp ? z.ZoneLow  : z.NearEdge;
                double dispHigh = z.IsGapUp ? z.NearEdge : z.ZoneHigh;
                bool inside = price >= dispLow && price <= dispHigh;
                if (inside)
                {
                    z.AlertFired = true;
                    Alert("IQKL_Gap_" + i, Priority.Low,
                        "IQKeyLevelsGPU: Price entered Gap zone " + z.OpenDate.ToString("M/d"),
                        NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10,
                        z.IsGapUp ? GapUpColor : GapDownColor, Brushes.Black);
                }
            }
        }

        /// <summary>Effective cluster zone width in points, determined by ClusterWidthMode.
        /// Falls back to FixedPoints value when ADR data is insufficient (&lt;3 days).</summary>
        private double ComputeEffectiveClusterWidth()
        {
            switch (ClusterWidthMode)
            {
                case IQKLWidthMode.Ticks:
                    return Math.Max(1, ClusterWidthTicks) * TickSize;
                case IQKLWidthMode.PercentOfADR:
                    if (_rdRanges.Count >= 3)
                        return _rdRanges.Average() * (ClusterWidthPctADR / 100.0);
                    return ClusterZoneWidthPoints; // fall back when insufficient ADR data
                case IQKLWidthMode.FixedPoints:
                default:
                    return ClusterZoneWidthPoints;
            }
        }

        /// <summary>Effective gap minimum size in points, determined by GapMinSizeMode.
        /// Falls back to FixedPoints value when ADR data is insufficient (&lt;3 days).</summary>
        private double ComputeEffectiveGapMinSize()
        {
            switch (GapMinSizeMode)
            {
                case IQKLWidthMode.Ticks:
                    return Math.Max(1, GapMinSizeTicks) * TickSize;
                case IQKLWidthMode.PercentOfADR:
                    if (_rdRanges.Count >= 3)
                        return _rdRanges.Average() * (GapMinSizePctADR / 100.0);
                    return GapMinSizePoints; // fall back
                case IQKLWidthMode.FixedPoints:
                default:
                    return GapMinSizePoints;
            }
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Session Open/Close helpers

        private void UpdateSessionOC(DateTime barEt)
        {
            for (int id = 0; id < 3; id++)
            {
                if (!IsOcSessionEnabled(id)) continue;

                DateTime sStart, sEnd;
                GetOcSessionWindow(id, barEt, out sStart, out sEnd);
                bool inSession = barEt >= sStart && barEt < sEnd;

                if (inSession)
                {
                    // Create new OC entry when the session window changes (mirrors KLPocEntry detection)
                    if (_activeOcSessions[id] == null || _activeOcSessions[id].SessionStart != sStart)
                    {
                        // Finalize previous entry if still open
                        if (_activeOcSessions[id] != null && !_activeOcSessions[id].IsComplete)
                            _activeOcSessions[id].IsComplete = true;

                        var entry = new KLSessionOC
                        {
                            SessionId     = id,
                            SessionName   = GetPocSessionName(id),
                            SessionDate   = sStart.Date,
                            SessionStart  = sStart,
                            OpenBarIndex  = CurrentBar,
                            OpenPrice     = Open[0],
                            CloseBarIndex = CurrentBar,
                            ClosePrice    = Close[0],
                            IsComplete    = false
                        };
                        _activeOcSessions[id] = entry;

                        lock (_sessionLock)
                        {
                            // Prune entries outside the OcExtensionDays window
                            DateTime cutoff = barEt.Date.AddDays(-(OcExtensionDays - 1));
                            for (int i = _ocList.Count - 1; i >= 0; i--)
                                if (_ocList[i].SessionDate < cutoff) _ocList.RemoveAt(i);
                            if (_ocList.Count >= MaxOcListSize) _ocList.RemoveAt(0);
                            _ocList.Add(entry);
                        }
                    }
                    else
                    {
                        // Update live close price each bar
                        var sess = _activeOcSessions[id];
                        sess.CloseBarIndex = CurrentBar;
                        sess.ClosePrice    = Close[0];
                    }
                }
                else
                {
                    // Session ended — finalize
                    if (_activeOcSessions[id] != null && !_activeOcSessions[id].IsComplete)
                    {
                        _activeOcSessions[id].IsComplete = true;
                        _activeOcSessions[id] = null;
                    }
                }
            }
        }

        private SharpDX.Direct2D1.SolidColorBrush GetOcBrush(int sessionId)
        {
            switch (sessionId) { case 0: return _dxAsiaOcBrush; case 1: return _dxLondonOcBrush; case 2: return _dxNyOcBrush; default: return null; }
        }

        private float GetOcThickness(int sessionId)
        {
            switch (sessionId) { case 0: return (float)AsiaOcThickness; case 1: return (float)LondonOcThickness; case 2: return (float)NyOcThickness; default: return 1f; }
        }

        private bool GetOcShowLabels(int sessionId)
        {
            switch (sessionId) { case 0: return ShowAsiaOCLabels; case 1: return ShowLondonOCLabels; case 2: return ShowNyOCLabels; default: return false; }
        }

        private IQKLLineStyle GetOcOpenStyle(int sessionId)
        {
            switch (sessionId) { case 0: return AsiaOcOpenStyle; case 1: return LondonOcOpenStyle; case 2: return NyOcOpenStyle; default: return IQKLLineStyle.Solid; }
        }

        private IQKLLineStyle GetOcCloseStyle(int sessionId)
        {
            switch (sessionId) { case 0: return AsiaOcCloseStyle; case 1: return LondonOcCloseStyle; case 2: return NyOcCloseStyle; default: return IQKLLineStyle.Dashed; }
        }

        private bool GetOcShowOpen(int sessionId)
        {
            switch (sessionId) { case 0: return ShowAsiaOCOpen; case 1: return ShowLondonOCOpen; case 2: return ShowNyOCOpen; default: return false; }
        }

        private bool GetOcShowClose(int sessionId)
        {
            switch (sessionId) { case 0: return ShowAsiaOCClose; case 1: return ShowLondonOCClose; case 2: return ShowNyOCClose; default: return false; }
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Hourly open helpers

        private void UpdateHourlyOpens(DateTime barEt)
        {
            if (!ShowHourlyOpens) return;

            int hour = barEt.Hour;
            if (!IsHourInRange(hour)) return;

            if (hour != _lastHour)
            {
                // Finalize previous hour
                if (_currentHourlyOpen != null)
                    _currentHourlyOpen.EndBarIndex = CurrentBar - 1;

                // Create new hourly open entry
                var ho = new KLHourlyOpen
                {
                    Hour          = hour,
                    OpenPrice     = Open[0],
                    StartBarIndex = CurrentBar,
                    EndBarIndex   = CurrentBar,
                    BarDate       = barEt.Date
                };
                _currentHourlyOpen = ho;
                lock (_sessionLock)
                {
                    if (_hourlyOpens.Count >= 200) _hourlyOpens.RemoveAt(0);
                    _hourlyOpens.Add(ho);
                }
                _lastHour = hour;
            }
        }

        private bool IsHourInRange(int hour)
        {
            if (HourlyEndHour >= 24) return hour >= HourlyStartHour;
            if (HourlyStartHour <= HourlyEndHour)
                return hour >= HourlyStartHour && hour < HourlyEndHour;
            return hour >= HourlyStartHour || hour < HourlyEndHour;
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region OnMarketDepth — Level 2 order book

        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (!EnableLevel2) return;

            _level2Available = true;

            bool isBid = e.MarketDataType == MarketDataType.Bid;
            bool isAsk = e.MarketDataType == MarketDataType.Ask;
            if (!isBid && !isAsk) return;

            Dictionary<double, KLBookLevel> book = isBid ? _bidBook : _askBook;
            double price = e.Price;
            long   size  = e.Volume;

            // Guard book mutation + wall detection so RenderWallLines (render thread)
            // never observes torn/inconsistent wall state
            lock (_sessionLock)
            {
                long oldSize = 0;
                KLBookLevel existing;
                book.TryGetValue(price, out existing);
                if (existing != null) oldSize = existing.Size;

                switch (e.Operation)
                {
                    case Operation.Add:
                    case Operation.Update:
                        if (existing == null) { existing = new KLBookLevel { Price = price }; book[price] = existing; }
                        existing.Size = size;
                        // Running sum updated incrementally instead of re-summing the whole book.
                        if (isBid) _bidSizeSum += size - oldSize; else _askSizeSum += size - oldSize;
                        break;
                    case Operation.Remove:
                        if (existing != null)
                        {
                            book.Remove(price);
                            if (isBid) _bidSizeSum -= oldSize; else _askSizeSum -= oldSize;
                        }
                        break;
                }

                DetectOrderBookWalls(book, isBid);
            }
        }

        private void DetectOrderBookWalls(Dictionary<double, KLBookLevel> book, bool isBid)
        {
            if (book.Count == 0) return;

            long   sizeSum = isBid ? _bidSizeSum : _askSizeSum;
            double avg     = (double)sizeSum / book.Count;

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

            if (isBid) { _wallBidPrice = wallPrice; _wallBidSize  = wallSize; }
            else       { _wallAskPrice = wallPrice; _wallAskSize  = wallSize; }
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

            if (Bars == null || ChartBars == null || RenderTarget == null) return;

            if (!dxReady)
            {
                try { CreateDXResources(); }
                catch (Exception ex)
                {
                    Print("IQKeyLevelsGPU: Unexpected exception from CreateDXResources: " + ex.Message);
                    return;
                }
            }
            if (!dxReady) return;

            // Clear label collision buckets for this frame (left-anchored / right-anchored)
            _usedLabelYLeft.Clear();
            _usedLabelYRight.Clear();
            // Clear per-frame cluster membership set (populated by RenderPocClusters below)
            _pocPricesInClusters.Clear();
            // Clear per-frame pending level label list (for Psy/HiLo/RD coincident merging)
            _pendingLevelLabels.Clear();

            var rt   = RenderTarget;
            // Bug 1 fix: use RenderTarget.Size.Width (the actual GPU drawing-surface width)
            // instead of chartControl.ActualWidth (the full WPF element, which may include the
            // price axis and differ from the renderable area at non-100% DPI).
            // This matches IQMainGPU / IQMainUltimate which also use rt.Size.Width.
            float rtW = rt.Size.Width;
            float rtH = (float)chartControl.ActualHeight;

            // Cache current-price Y for the label keep-out band (Part D).
            try
            {
                _currentPriceY = CurrentBar >= 0 ? chartScale.GetYByValue(Close[0]) : float.NaN;
            }
            catch { _currentPriceY = float.NaN; }

            // ── -1. Gap zones (shaded background, drawn before clusters) ─────
            try { if (ShowGapZones) RenderGapZones(chartControl, chartScale, rtW); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQKeyLevelsGPU: SharpDX error RenderGapZones: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQKeyLevelsGPU: RenderGapZones [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 0. POC Cluster zones (shaded background, drawn first) ────────
            try { if (ShowPocClusters) RenderPocClusters(chartControl, chartScale, rtW); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQKeyLevelsGPU: SharpDX error RenderPocClusters: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQKeyLevelsGPU: RenderPocClusters [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 1. Session POCs ───────────────────────────────────────────────
            try { RenderSessionPocs(chartControl, chartScale, rtW); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQKeyLevelsGPU: SharpDX error RenderSessionPocs: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQKeyLevelsGPU: RenderSessionPocs [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 1b. Session Opens/Closes ──────────────────────────────────────
            try { RenderSessionOC(chartControl, chartScale, rtW); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQKeyLevelsGPU: SharpDX error RenderSessionOC: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQKeyLevelsGPU: RenderSessionOC [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 2. RD Range Daily Hi/Lo ───────────────────────────────────────
            try { if (ShowRd && _rdValue > 0) RenderRdBands(chartControl, chartScale, rtW); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQKeyLevelsGPU: SharpDX error RenderRdBands: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQKeyLevelsGPU: RenderRdBands [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 3. Psy Levels ─────────────────────────────────────────────────
            try { RenderPsyLevels(chartControl, chartScale, rtW); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQKeyLevelsGPU: SharpDX error RenderPsyLevels: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQKeyLevelsGPU: RenderPsyLevels [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 4. Weekly / Monthly Hi-Lo ─────────────────────────────────────
            try { RenderWeeklyMonthlyHiLo(chartControl, chartScale, rtW); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQKeyLevelsGPU: SharpDX error RenderWeeklyMonthlyHiLo: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQKeyLevelsGPU: RenderWeeklyMonthlyHiLo [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 5. Hourly Opening Lines ───────────────────────────────────────
            try { if (ShowHourlyOpens) RenderHourlyOpens(chartControl, chartScale, rtW); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQKeyLevelsGPU: SharpDX error RenderHourlyOpens: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQKeyLevelsGPU: RenderHourlyOpens [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── 6. L2 Wall Lines ──────────────────────────────────────────────
            try { if (ShowWallLines && EnableLevel2 && _level2Available) RenderWallLines(chartControl, chartScale, rtW); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQKeyLevelsGPU: SharpDX error RenderWallLines: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQKeyLevelsGPU: RenderWallLines [" + ex.GetType().Name + "]: " + ex.Message); }

            // ── Flush merged level labels (Psy, Hi-Lo, RD — collected above) ─
            try { if (_pendingLevelLabels.Count > 0) FlushLevelLabels(chartScale, rtW); }
            catch (Exception ex) { Print("IQKeyLevelsGPU: FlushLevelLabels [" + ex.GetType().Name + "]: " + ex.Message); }
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Render methods

        private void RenderSessionPocs(ChartControl cc, ChartScale cs, float rtW)
        {
            if (!RenderPrereqsOk()) return;

            List<KLPocEntry> snapshot;
            lock (_sessionLock) { snapshot = _pocList.ToList(); }

            if (snapshot.Count == 0) return;

            // Use cached ET date for age calculations (avoids system-local DateTime.Today timezone issue)
            DateTime etToday = _latestBarEtDate != DateTime.MinValue
                ? _latestBarEtDate
                : TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, EtZone).Date;

            float lastBarX = cc.GetXByBarIndex(ChartBars, ChartBars.ToIndex);

            // Forward iteration: oldest entries first (rendered below), newest last (on top)
            for (int i = 0; i < snapshot.Count; i++)
            {
                KLPocEntry entry = snapshot[i];
                if (entry.POCPrice == 0) continue;

                // Extension days check — skip entries outside the window (using ET date).
                // A value of N keeps N calendar dates: daysOld 0 through N-1.
                int daysOld = (etToday - entry.SessionDate).Days;
                if (daysOld >= PocExtensionDays) continue;

                // C1 fix: skip entries whose StartBarIndex is beyond the visible right edge
                // (the line wouldn't be visible, and xStart > xEnd causes zero-length draw)
                if (entry.StartBarIndex > ChartBars.ToIndex) continue;

                SharpDX.Direct2D1.SolidColorBrush pocBrush = GetPocBrush(entry.SessionId);
                if (pocBrush == null) continue;

                bool showLabel = GlobalShowLabels && GetPocShowLabels(entry.SessionId);
                IQKLLineStyle style     = GetPocLineStyle(entry.SessionId);
                float         thickness = GetPocThickness(entry.SessionId);

                // Apply fade based on age (Fade Older POCs feature)
                float savedOpacity = pocBrush.Opacity;
                if (FadeOlderPocs && daysOld > 0)
                {
                    float fadeOpacity = savedOpacity * Math.Max(0.15f, 1f - daysOld * 0.12f);
                    pocBrush.Opacity = Math.Min(savedOpacity, Math.Max(0.08f, fadeOpacity));
                }

                try
                {
                    // C1 fix: clamp xStart to visible area; extend xEnd to right edge unless disabled
                    int clampedStart = Math.Max(ChartBars.FromIndex, entry.StartBarIndex);
                    float xStart = cc.GetXByBarIndex(ChartBars, clampedStart);
                    float xEnd   = ExtendLinesToRightEdge ? rtW : Math.Max(xStart, lastBarX);
                    float yPoc   = cs.GetYByValue(entry.POCPrice);

                    if (!float.IsNaN(yPoc) && !float.IsInfinity(yPoc) && xStart <= xEnd)
                    {
                        DrawStyledLine(xStart, yPoc, xEnd, yPoc, pocBrush, thickness, style);

                        // C2 fix: only draw label when line is actually visible
                        // Feature: suppress individual label when this POC is a member of a
                        // rendered cluster zone and SuppressPocLabelsInClusters is enabled —
                        // the cluster label (drawn first) conveys all relevant info.
                        bool drawLabel = showLabel && _dxLabelFormat != null
                            && !(SuppressPocLabelsInClusters && _pocPricesInClusters.Contains(entry.POCPrice));
                        if (drawLabel)
                        {
                            string label = string.Format("{0} POC {1} {2}",
                                entry.SessionName,
                                GetSessionDateLabel(entry.SessionDate, etToday),
                                Instrument.MasterInstrument.FormatPrice(entry.POCPrice));

                            // D: label anchor support
                            float labelX = LabelAnchor == IQKLLabelAnchor.LineEnd
                                ? xEnd - (PocLabelWidth + 4f)
                                : xStart + 4f;
                            labelX = ClampLabelX(labelX, rtW, PocLabelWidth);
                            float labelY = GetNonCollidingLabelY(yPoc - 14f, LabelAnchor == IQKLLabelAnchor.LineEnd);
                            RenderTarget.DrawText(label, _dxLabelFormat,
                                new SharpDX.RectangleF(labelX, labelY, PocLabelWidth, 16f), pocBrush);
                        }
                    }
                }
                finally
                {
                    pocBrush.Opacity = savedOpacity; // always restore opacity
                }
            }
        }

        /// <summary>Renders semi-transparent shaded rectangles for detected POC cluster zones,
        /// optional enhanced cluster labels (count + price range + session composition), and
        /// populates <see cref="_pocPricesInClusters"/> so RenderSessionPocs can suppress
        /// individual labels for clustered POCs when SuppressPocLabelsInClusters is true.</summary>
        private void RenderPocClusters(ChartControl cc, ChartScale cs, float rtW)
        {
            if (!RenderPrereqsOk() || _dxClusterBrush == null) return;

            List<KLClusterZone> zones = _clusterZones;
            if (zones == null || zones.Count == 0) return;

            // Take POC snapshot for session composition and label-suppression population.
            List<KLPocEntry> pocSnapshot;
            lock (_sessionLock) { pocSnapshot = _pocList.ToList(); }

            DateTime etToday = _latestBarEtDate != DateTime.MinValue
                ? _latestBarEtDate
                : TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, EtZone).Date;

            // Tolerance for matching POC prices to zone bounds (half a tick, never zero).
            double tol = Math.Max(TickSize / 2.0, 1e-9);

            double pad = Math.Max(TickSize * ClusterZonePaddingTickFactor,
                Math.Min(ClusterZonePaddingMaxPoints, ClusterZoneWidthPoints * ClusterZonePaddingPercent));

            // Bug 2/3 fix: use LabelAnchor to select the same bucket as RenderSessionPocs /
            // RenderSessionOC use — so cluster labels and POC/OC labels collide against each other.
            bool useRight = LabelAnchor == IQKLLabelAnchor.LineEnd;

            for (int i = 0; i < zones.Count; i++)
            {
                KLClusterZone z = zones[i];
                float yTop    = cs.GetYByValue(z.MaxPrice + pad);
                float yBottom = cs.GetYByValue(z.MinPrice - pad);
                if (float.IsNaN(yTop) || float.IsNaN(yBottom) || float.IsInfinity(yTop) || float.IsInfinity(yBottom))
                    continue;

                var rect = new SharpDX.RectangleF(0f, Math.Min(yTop, yBottom), rtW, Math.Abs(yBottom - yTop));
                RenderTarget.FillRectangle(rect, _dxClusterBrush);

                // ── Collect POC members in this zone for label text and label-suppression ──
                // Always runs (not gated on ShowClusterLabels) so SuppressPocLabelsInClusters
                // works even when the cluster label itself is hidden.
                var sessionFlags = new bool[3]; // index: 0=Asia, 1=London, 2=NY
                for (int p = 0; p < pocSnapshot.Count; p++)
                {
                    KLPocEntry e = pocSnapshot[p];
                    if (e.POCPrice == 0) continue;
                    int daysOld = (etToday - e.SessionDate).Days;
                    if (daysOld < 0 || daysOld >= PocExtensionDays) continue;
                    if (e.POCPrice < z.MinPrice - tol || e.POCPrice > z.MaxPrice + tol) continue;

                    // This POC is a member of the zone.
                    if (e.SessionId >= 0 && e.SessionId < 3) sessionFlags[e.SessionId] = true;
                    _pocPricesInClusters.Add(e.POCPrice);
                }

                if (GlobalShowLabels && ShowClusterLabels && _dxLabelFormat != null)
                {
                    // Build enhanced label: "POC Cluster ×N  minPoc–maxPoc  (sessions)"
                    // z.MinPrice / z.MaxPrice are the exact min/max POC prices in the zone.
                    string priceRange = Instrument.MasterInstrument.FormatPrice(z.MinPrice)
                        + "–" + Instrument.MasterInstrument.FormatPrice(z.MaxPrice);

                    string sessionComp = "";
                    var sessionParts = new System.Text.StringBuilder();
                    if (sessionFlags[0]) { if (sessionParts.Length > 0) sessionParts.Append(","); sessionParts.Append("A"); }
                    if (sessionFlags[1]) { if (sessionParts.Length > 0) sessionParts.Append(","); sessionParts.Append("L"); }
                    if (sessionFlags[2]) { if (sessionParts.Length > 0) sessionParts.Append(","); sessionParts.Append("N"); }
                    if (sessionParts.Length > 0)
                        sessionComp = "  (" + sessionParts + ")";

                    string label = string.Format("POC Cluster ×{0}  {1}{2}", z.Count, priceRange, sessionComp);

                    // Bug 4 fix: use ClampLabelX with correct rtW so label stays within the
                    // render surface and doesn't bleed into the price axis.
                    float labelX = useRight ? rtW - (ClusterLabelWidth + 4f) : 4f;
                    labelX = ClampLabelX(labelX, rtW, ClusterLabelWidth);
                    float labelY = GetNonCollidingLabelY(Math.Min(yTop, yBottom) + 2f, useRight);
                    RenderTarget.DrawText(label, _dxLabelFormat,
                        new SharpDX.RectangleF(labelX, labelY, ClusterLabelWidth, 16f), _dxClusterBrush);
                }
            }
        }

        private void RenderRdBands(ChartControl cc, ChartScale cs, float rtW)
        {
            if (!RenderPrereqsOk()) return;
            if (_dxRdBrush == null || _rdHigh == 0 || _rdLow == 0) return;

            bool useRight = LabelAnchor == IQKLLabelAnchor.LineEnd;
            float labelX  = useRight ? rtW - (LevelLabelWidth + 4f) : 4f;
            labelX = ClampLabelX(labelX, rtW, LevelLabelWidth);

            float yH = cs.GetYByValue(_rdHigh);
            if (!float.IsNaN(yH) && !float.IsInfinity(yH))
            {
                DrawStyledLine(0f, yH, rtW, yH, _dxRdBrush, RdThickness, RdLineStyle);
                if (GlobalShowLabels && ShowRdLabels && _dxLabelFormat != null)
                {
                    var lbl = new KLLevelLabel { Price = _rdHigh, Prefix = "RD", Side = "H",
                        LabelXHint = labelX, LabelWidth = LevelLabelWidth,
                        UseRightBucket = useRight, LineY = yH, Brush = _dxRdBrush };
                    _pendingLevelLabels.Add(lbl);
                }
            }

            float yL = cs.GetYByValue(_rdLow);
            if (!float.IsNaN(yL) && !float.IsInfinity(yL))
            {
                DrawStyledLine(0f, yL, rtW, yL, _dxRdBrush, RdThickness, RdLineStyle);
                if (GlobalShowLabels && ShowRdLabels && _dxLabelFormat != null)
                {
                    var lbl = new KLLevelLabel { Price = _rdLow, Prefix = "RD", Side = "L",
                        LabelXHint = labelX, LabelWidth = LevelLabelWidth,
                        UseRightBucket = useRight, LineY = yL, Brush = _dxRdBrush };
                    _pendingLevelLabels.Add(lbl);
                }
            }
        }

        private void RenderPsyLevels(ChartControl cc, ChartScale cs, float rtW)
        {
            if (!RenderPrereqsOk()) return;

            bool showLabels = GlobalShowLabels && _dxLabelFormat != null;
            bool useRight   = LabelAnchor == IQKLLabelAnchor.LineEnd;
            float labelX    = useRight ? rtW - (LevelLabelWidth + 4f) : 4f;
            labelX = ClampLabelX(labelX, rtW, LevelLabelWidth);

            // Draw all psy lines; queue labels into the merge system.
            if (ShowDailyPsy && _dxDailyPsyBrush != null && _psyDayHigh > 0)
            {
                float yDH = cs.GetYByValue(_cachedDPsyH);
                float yDL = cs.GetYByValue(_cachedDPsyL);
                if (!float.IsNaN(yDH) && !float.IsInfinity(yDH))
                {
                    DrawStyledLine(0f, yDH, rtW, yDH, _dxDailyPsyBrush, 1f, DailyPsyLineStyle);
                    if (showLabels && ShowDailyPsyLabels)
                        _pendingLevelLabels.Add(new KLLevelLabel { Price = _cachedDPsyH, Prefix = "DPsy", Side = "H",
                            LabelXHint = labelX, LabelWidth = LevelLabelWidth, UseRightBucket = useRight,
                            LineY = yDH, Brush = _dxDailyPsyBrush });
                }
                if (!float.IsNaN(yDL) && !float.IsInfinity(yDL))
                {
                    DrawStyledLine(0f, yDL, rtW, yDL, _dxDailyPsyBrush, 1f, DailyPsyLineStyle);
                    if (showLabels && ShowDailyPsyLabels)
                        _pendingLevelLabels.Add(new KLLevelLabel { Price = _cachedDPsyL, Prefix = "DPsy", Side = "L",
                            LabelXHint = labelX, LabelWidth = LevelLabelWidth, UseRightBucket = useRight,
                            LineY = yDL, Brush = _dxDailyPsyBrush });
                }
            }

            if (ShowWeeklyPsy && _dxWeeklyPsyBrush != null && _psyWeekHigh > 0)
            {
                float yWH = cs.GetYByValue(_cachedWPsyH);
                float yWL = cs.GetYByValue(_cachedWPsyL);
                if (!float.IsNaN(yWH) && !float.IsInfinity(yWH))
                {
                    DrawStyledLine(0f, yWH, rtW, yWH, _dxWeeklyPsyBrush, 1f, WeeklyPsyLineStyle);
                    if (showLabels && ShowWeeklyPsyLabels)
                        _pendingLevelLabels.Add(new KLLevelLabel { Price = _cachedWPsyH, Prefix = "WPsy", Side = "H",
                            LabelXHint = labelX, LabelWidth = LevelLabelWidth, UseRightBucket = useRight,
                            LineY = yWH, Brush = _dxWeeklyPsyBrush });
                }
                if (!float.IsNaN(yWL) && !float.IsInfinity(yWL))
                {
                    DrawStyledLine(0f, yWL, rtW, yWL, _dxWeeklyPsyBrush, 1f, WeeklyPsyLineStyle);
                    if (showLabels && ShowWeeklyPsyLabels)
                        _pendingLevelLabels.Add(new KLLevelLabel { Price = _cachedWPsyL, Prefix = "WPsy", Side = "L",
                            LabelXHint = labelX, LabelWidth = LevelLabelWidth, UseRightBucket = useRight,
                            LineY = yWL, Brush = _dxWeeklyPsyBrush });
                }
            }

            if (ShowMonthlyPsy && _dxMonthlyPsyBrush != null && _psyMonthHigh > 0)
            {
                float yMH = cs.GetYByValue(_cachedMPsyH);
                float yML = cs.GetYByValue(_cachedMPsyL);
                if (!float.IsNaN(yMH) && !float.IsInfinity(yMH))
                {
                    DrawStyledLine(0f, yMH, rtW, yMH, _dxMonthlyPsyBrush, 1f, MonthlyPsyLineStyle);
                    if (showLabels && ShowMonthlyPsyLabels)
                        _pendingLevelLabels.Add(new KLLevelLabel { Price = _cachedMPsyH, Prefix = "MPsy", Side = "H",
                            LabelXHint = labelX, LabelWidth = LevelLabelWidth, UseRightBucket = useRight,
                            LineY = yMH, Brush = _dxMonthlyPsyBrush });
                }
                if (!float.IsNaN(yML) && !float.IsInfinity(yML))
                {
                    DrawStyledLine(0f, yML, rtW, yML, _dxMonthlyPsyBrush, 1f, MonthlyPsyLineStyle);
                    if (showLabels && ShowMonthlyPsyLabels)
                        _pendingLevelLabels.Add(new KLLevelLabel { Price = _cachedMPsyL, Prefix = "MPsy", Side = "L",
                            LabelXHint = labelX, LabelWidth = LevelLabelWidth, UseRightBucket = useRight,
                            LineY = yML, Brush = _dxMonthlyPsyBrush });
                }
            }
        }

        private void RenderWeeklyMonthlyHiLo(ChartControl cc, ChartScale cs, float rtW)
        {
            if (!RenderPrereqsOk()) return;

            bool showLabels = GlobalShowLabels && _dxLabelFormat != null;
            bool useRight   = LabelAnchor == IQKLLabelAnchor.LineEnd;
            float labelX    = useRight ? rtW - (LevelLabelWidth + 4f) : 4f;
            labelX = ClampLabelX(labelX, rtW, LevelLabelWidth);

            if (ShowLastWeek && _dxWeekHlBrush != null && _lastWeekHigh > 0)
            {
                float yH = cs.GetYByValue(_lastWeekHigh);
                float yL = cs.GetYByValue(_lastWeekLow);
                if (!float.IsNaN(yH) && !float.IsInfinity(yH))
                {
                    DrawStyledLine(0f, yH, rtW, yH, _dxWeekHlBrush, LastWeekThickness, LastWeekLineStyle);
                    if (showLabels && ShowLastWeekLabels)
                        _pendingLevelLabels.Add(new KLLevelLabel { Price = _lastWeekHigh, Prefix = "LWH", Side = "",
                            LabelXHint = labelX, LabelWidth = LevelLabelWidth, UseRightBucket = useRight,
                            LineY = yH, Brush = _dxWeekHlBrush });
                }
                if (!float.IsNaN(yL) && !float.IsInfinity(yL))
                {
                    DrawStyledLine(0f, yL, rtW, yL, _dxWeekHlBrush, LastWeekThickness, LastWeekLineStyle);
                    if (showLabels && ShowLastWeekLabels)
                        _pendingLevelLabels.Add(new KLLevelLabel { Price = _lastWeekLow, Prefix = "LWL", Side = "",
                            LabelXHint = labelX, LabelWidth = LevelLabelWidth, UseRightBucket = useRight,
                            LineY = yL, Brush = _dxWeekHlBrush });
                }
            }

            if (ShowLastMonth && _dxMonthHlBrush != null && _prevMonthHigh > 0)
            {
                float yH = cs.GetYByValue(_prevMonthHigh);
                float yL = cs.GetYByValue(_prevMonthLow);
                if (!float.IsNaN(yH) && !float.IsInfinity(yH))
                {
                    DrawStyledLine(0f, yH, rtW, yH, _dxMonthHlBrush, LastMonthThickness, LastMonthLineStyle);
                    if (showLabels && ShowLastMonthLabels)
                        _pendingLevelLabels.Add(new KLLevelLabel { Price = _prevMonthHigh, Prefix = "LMH", Side = "",
                            LabelXHint = labelX, LabelWidth = LevelLabelWidth, UseRightBucket = useRight,
                            LineY = yH, Brush = _dxMonthHlBrush });
                }
                if (!float.IsNaN(yL) && !float.IsInfinity(yL))
                {
                    DrawStyledLine(0f, yL, rtW, yL, _dxMonthHlBrush, LastMonthThickness, LastMonthLineStyle);
                    if (showLabels && ShowLastMonthLabels)
                        _pendingLevelLabels.Add(new KLLevelLabel { Price = _prevMonthLow, Prefix = "LML", Side = "",
                            LabelXHint = labelX, LabelWidth = LevelLabelWidth, UseRightBucket = useRight,
                            LineY = yL, Brush = _dxMonthHlBrush });
                }
            }
        }

        private void RenderHourlyOpens(ChartControl cc, ChartScale cs, float rtW)
        {
            if (!RenderPrereqsOk() || _dxHourlyOpenBrush == null) return;

            List<KLHourlyOpen> snapshot;
            lock (_sessionLock) { snapshot = _hourlyOpens.ToList(); }

            if (snapshot.Count == 0) return;

            int firstBar = ChartBars.FromIndex;
            int lastBar  = ChartBars.ToIndex;

            // Show only the most recent MaxHourlyLinesToShow in-range entries
            // Collect eligible entries and take the last N
            var eligible = new List<KLHourlyOpen>();
            foreach (KLHourlyOpen ho in snapshot)
            {
                if (!IsHourInRange(ho.Hour)) continue;
                eligible.Add(ho);
            }
            int startIdx = Math.Max(0, eligible.Count - MaxHourlyLinesToShow);

            for (int i = startIdx; i < eligible.Count; i++)
            {
                KLHourlyOpen ho = eligible[i];
                int endBar = (ho == _currentHourlyOpen) ? CurrentBar : ho.EndBarIndex;

                // C2 fix: skip if entirely outside visible area
                if (endBar < firstBar || ho.StartBarIndex > lastBar) continue;

                float xStart = cc.GetXByBarIndex(ChartBars, Math.Max(firstBar, ho.StartBarIndex));
                // C2 fix: for the live (current) hour, extend line to chart right edge so it
                // renders alongside its label; for completed hours clamp to last visible bar.
                // Note: label is anchored at xEnd - (LevelLabelWidth + 2f), so extending to rtW
                // ensures the label always has a corresponding visible line segment.
                float xEnd = (ho == _currentHourlyOpen && ExtendLinesToRightEdge)
                    ? rtW
                    : cc.GetXByBarIndex(ChartBars, Math.Min(lastBar, endBar));
                float yOpen  = cs.GetYByValue(ho.OpenPrice);

                if (float.IsNaN(yOpen) || float.IsInfinity(yOpen)) continue;
                // C2 fix: only draw label when the line has non-zero visible width
                if (xEnd <= xStart) continue;

                DrawStyledLine(xStart, yOpen, xEnd, yOpen, _dxHourlyOpenBrush, HourlyOpenThickness, HourlyOpenLineStyle);

                if (GlobalShowLabels && ShowHourlyOpenLabels && _dxLabelFormat != null)
                {
                    string label = string.Format("HO {0:D2}:00 {1}", ho.Hour,
                        Instrument.MasterInstrument.FormatPrice(ho.OpenPrice));
                    // Hourly open labels always at line end (natural position near current time)
                    float labelX = ClampLabelX(xEnd - (LevelLabelWidth + 2f), rtW, LevelLabelWidth);
                    float labelY = GetNonCollidingLabelY(yOpen - 14f, true);
                    RenderTarget.DrawText(label, _dxLabelFormat,
                        new SharpDX.RectangleF(labelX, labelY, LevelLabelWidth, 16f), _dxHourlyOpenBrush);
                }
            }
        }

        private void RenderWallLines(ChartControl cc, ChartScale cs, float rtW)
        {
            if (!RenderPrereqsOk()) return;

            // Snapshot wall state under lock — OnMarketDepth mutates these on the data thread
            double wallBidPrice, wallAskPrice;
            long   wallBidSize, wallAskSize;
            lock (_sessionLock)
            {
                wallBidPrice = _wallBidPrice;
                wallBidSize  = _wallBidSize;
                wallAskPrice = _wallAskPrice;
                wallAskSize  = _wallAskSize;
            }

            if (wallBidPrice > 0 && _dxWallBidBrush != null)
            {
                float yBid = cs.GetYByValue(wallBidPrice);
                if (!float.IsNaN(yBid) && !float.IsInfinity(yBid))
                {
                    DrawStyledLine(0f, yBid, rtW, yBid, _dxWallBidBrush, WallLineThickness, IQKLLineStyle.Solid);
                    if (GlobalShowLabels && ShowWallLabels && _dxLabelFormat != null)
                    {
                        string label = string.Format("BID WALL x{0}", wallBidSize);
                        float labelX = ClampLabelX(4f, rtW, LevelLabelWidth);
                        float labelY = GetNonCollidingLabelY(yBid - 14f, false);
                        RenderTarget.DrawText(label, _dxLabelFormat,
                            new SharpDX.RectangleF(labelX, labelY, LevelLabelWidth, 16f), _dxWallBidBrush);
                    }
                }
            }

            if (wallAskPrice > 0 && _dxWallAskBrush != null)
            {
                float yAsk = cs.GetYByValue(wallAskPrice);
                if (!float.IsNaN(yAsk) && !float.IsInfinity(yAsk))
                {
                    DrawStyledLine(0f, yAsk, rtW, yAsk, _dxWallAskBrush, WallLineThickness, IQKLLineStyle.Solid);
                    if (GlobalShowLabels && ShowWallLabels && _dxLabelFormat != null)
                    {
                        string label = string.Format("ASK WALL x{0}", wallAskSize);
                        float labelX = ClampLabelX(4f, rtW, LevelLabelWidth);
                        float labelY = GetNonCollidingLabelY(yAsk - 14f, false);
                        RenderTarget.DrawText(label, _dxLabelFormat,
                            new SharpDX.RectangleF(labelX, labelY, LevelLabelWidth, 16f), _dxWallAskBrush);
                    }
                }
            }
        }

        /// <summary>Renders gap zones (shaded rectangles + labels) with partial-fill shrinking.
        /// Must be called before RenderPocClusters so clusters render on top.</summary>
        private void RenderGapZones(ChartControl cc, ChartScale cs, float rtW)
        {
            if (!RenderPrereqsOk()) return;

            List<KLGapZone> snapshot;
            lock (_sessionLock) { snapshot = _gapZones.ToList(); }
            if (snapshot.Count == 0) return;

            DateTime etToday = _latestBarEtDate != DateTime.MinValue
                ? _latestBarEtDate
                : TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, EtZone).Date;

            bool useRight = LabelAnchor == IQKLLabelAnchor.LineEnd;

            for (int i = 0; i < snapshot.Count; i++)
            {
                KLGapZone z = snapshot[i];

                int daysOld = (etToday - z.CreationDate).Days;
                if (daysOld > GapMaxAgeDays) continue;

                if (z.IsFilled && GapFilledAction == IQKLGapFilledAction.Remove) continue;

                // Unfilled display bounds:
                //   gap-up:   displayed = [ZoneLow, NearEdge]  (NearEdge shrinks from ZoneHigh toward ZoneLow)
                //   gap-down: displayed = [NearEdge, ZoneHigh] (NearEdge grows from ZoneLow toward ZoneHigh)
                double dispLow  = z.IsGapUp ? z.ZoneLow  : z.NearEdge;
                double dispHigh = z.IsGapUp ? z.NearEdge : z.ZoneHigh;

                // Show full original bounds when filled + Keep action
                if (z.IsFilled && GapFilledAction == IQKLGapFilledAction.Keep)
                { dispLow = z.ZoneLow; dispHigh = z.ZoneHigh; }

                if (dispHigh <= dispLow) continue; // fully collapsed (filled)

                float yTop    = cs.GetYByValue(dispHigh);
                float yBottom = cs.GetYByValue(dispLow);
                if (float.IsNaN(yTop) || float.IsInfinity(yTop) ||
                    float.IsNaN(yBottom) || float.IsInfinity(yBottom)) continue;

                SharpDX.Direct2D1.SolidColorBrush brush = z.IsGapUp ? _dxGapUpBrush : _dxGapDownBrush;
                if (brush == null) continue;

                float savedOpacity = brush.Opacity;
                if (z.IsFilled && GapFilledAction == IQKLGapFilledAction.Dim)
                    brush.Opacity = Math.Max(0.01f, GapDimOpacity / 100f);

                try
                {
                    float rectTop    = Math.Min(yTop, yBottom);
                    float rectHeight = Math.Abs(yBottom - yTop);
                    RenderTarget.FillRectangle(
                        new SharpDX.RectangleF(0f, rectTop, rtW, rectHeight), brush);

                    if (GlobalShowLabels && ShowGapLabels && _dxLabelFormat != null)
                    {
                        string filledSuffix = (z.IsFilled && GapFilledAction == IQKLGapFilledAction.Dim)
                            ? " (filled)" : "";
                        string label = string.Format("{0} Gap  {1}–{2}{3}",
                            z.OpenDate.ToString("M/d"),
                            Instrument.MasterInstrument.FormatPrice(z.ZoneLow),
                            Instrument.MasterInstrument.FormatPrice(z.ZoneHigh),
                            filledSuffix);
                        float labelX = useRight ? rtW - (GapLabelWidth + 4f) : 4f;
                        labelX = ClampLabelX(labelX, rtW, GapLabelWidth);
                        float labelY = GetNonCollidingLabelY(rectTop + 2f, useRight);
                        RenderTarget.DrawText(label, _dxLabelFormat,
                            new SharpDX.RectangleF(labelX, labelY, GapLabelWidth, 16f), brush);
                    }
                }
                finally { brush.Opacity = savedOpacity; }
            }
        }

        /// <summary>Flushes all queued level labels (Psy, Hi-Lo, RD) with optional coincident
        /// merging. When MergeCoincidentLabels is true, labels within ±1 tick of each other
        /// are collapsed into a single combined label. All underlying lines are already drawn;
        /// only labels are affected here.</summary>
        private void FlushLevelLabels(ChartScale cs, float rtW)
        {
            if (_pendingLevelLabels.Count == 0 || _dxLabelFormat == null) return;

            int n = _pendingLevelLabels.Count;
            var rendered = new bool[n];

            if (!MergeCoincidentLabels)
            {
                // No merging — draw each label individually
                for (int i = 0; i < n; i++)
                {
                    KLLevelLabel e = _pendingLevelLabels[i];
                    string text = BuildSingleLevelLabelText(e);
                    float labelY = GetNonCollidingLabelY(e.LineY - LabelFontSize - 2f, e.UseRightBucket);
                    RenderTarget.DrawText(text, _dxLabelFormat,
                        new SharpDX.RectangleF(e.LabelXHint, labelY, e.LabelWidth, 16f), e.Brush);
                }
                return;
            }

            double tick = Math.Max(TickSize, 1e-9);

            for (int i = 0; i < n; i++)
            {
                if (rendered[i]) continue;
                rendered[i] = true;

                KLLevelLabel baseEntry = _pendingLevelLabels[i];
                var group = new System.Collections.Generic.List<KLLevelLabel> { baseEntry };

                for (int j = i + 1; j < n; j++)
                {
                    if (rendered[j]) continue;
                    KLLevelLabel other = _pendingLevelLabels[j];
                    if (Math.Abs(other.Price - baseEntry.Price) <= tick)
                    {
                        group.Add(other);
                        rendered[j] = true;
                    }
                }

                string mergedText = group.Count > 1
                    ? BuildMergedLevelLabelText(group, baseEntry.Price)
                    : BuildSingleLevelLabelText(baseEntry);

                float labelY = GetNonCollidingLabelY(baseEntry.LineY - LabelFontSize - 2f, baseEntry.UseRightBucket);
                RenderTarget.DrawText(mergedText, _dxLabelFormat,
                    new SharpDX.RectangleF(baseEntry.LabelXHint, labelY, baseEntry.LabelWidth, 16f),
                    baseEntry.Brush);
            }
        }

        private string BuildSingleLevelLabelText(KLLevelLabel e)
        {
            string priceStr = Instrument.MasterInstrument.FormatPrice(e.Price);
            // HiLo prefixes already include H/L in the prefix ("LWH", "LWL", "LMH", "LML")
            if (e.Side.Length == 0)
                return string.Format("{0} {1}", e.Prefix, priceStr);
            return string.Format("{0} {1} {2}", e.Prefix, e.Side, priceStr);
        }

        private string BuildMergedLevelLabelText(
            System.Collections.Generic.List<KLLevelLabel> group, double price)
        {
            string priceStr = Instrument.MasterInstrument.FormatPrice(price);

            // Separate Psy entries (prefix ends with "Psy") from HiLo/RD entries
            bool hasD = false, hasW = false, hasM = false;
            string psySide = "";
            var nonPsyParts = new System.Text.StringBuilder();

            for (int k = 0; k < group.Count; k++)
            {
                KLLevelLabel e = group[k];
                if (e.Prefix == "DPsy") { hasD = true; psySide = e.Side; }
                else if (e.Prefix == "WPsy") { hasW = true; psySide = e.Side; }
                else if (e.Prefix == "MPsy") { hasM = true; psySide = e.Side; }
                else
                {
                    // HiLo (LWH/LWL/LMH/LML) or RD (with Side)
                    if (nonPsyParts.Length > 0) nonPsyParts.Append(" + ");
                    if (e.Side.Length == 0) nonPsyParts.Append(e.Prefix);
                    else nonPsyParts.Append(e.Prefix + " " + e.Side);
                }
            }

            // Build Psy prefix (D, W, M combined)
            string psyPart = "";
            if (hasD || hasW || hasM)
            {
                var psyCode = new System.Text.StringBuilder();
                if (hasD) psyCode.Append("D");
                if (hasW) { if (psyCode.Length > 0) psyCode.Append("+"); psyCode.Append("W"); }
                if (hasM) { if (psyCode.Length > 0) psyCode.Append("+"); psyCode.Append("M"); }
                psyPart = psyCode + "Psy " + psySide;
            }

            // Combine parts
            var result = new System.Text.StringBuilder();
            if (nonPsyParts.Length > 0)
            {
                result.Append(nonPsyParts);
                if (psyPart.Length > 0) { result.Append(" + "); result.Append(psyPart); }
            }
            else { result.Append(psyPart); }
            result.Append(" "); result.Append(priceStr);
            return result.ToString();
        }

        /// <summary>Render a single full-width horizontal line with optional label.</summary>
        private void RenderSingleLine(ChartControl cc, ChartScale cs, float rtW, double price,
            SharpDX.Direct2D1.SolidColorBrush brush, string label, bool showLabel,
            float xStart = 0f, IQKLLineStyle style = IQKLLineStyle.Solid, float thickness = 1.5f)
        {
            if (brush == null || price == 0) return;
            float y = cs.GetYByValue(price);
            if (float.IsNaN(y) || float.IsInfinity(y)) return;

            DrawStyledLine(xStart, y, rtW, y, brush, thickness, style);

            if (showLabel && _dxLabelFormat != null && label.Length > 0)
            {
                string text = string.Format("{0} {1}", label, Instrument.MasterInstrument.FormatPrice(price));
                // D: label anchor — LineEnd places label at right edge, LineStart at left
                float labelX = LabelAnchor == IQKLLabelAnchor.LineEnd
                    ? rtW - (LevelLabelWidth + 4f)
                    : xStart + 4f;
                labelX = ClampLabelX(labelX, rtW, LevelLabelWidth);
                float labelY = GetNonCollidingLabelY(y - 14f, LabelAnchor == IQKLLabelAnchor.LineEnd);
                RenderTarget.DrawText(text, _dxLabelFormat,
                    new SharpDX.RectangleF(labelX, labelY, LevelLabelWidth, 16f), brush);
            }
        }

        /// <summary>Render the Session Open/Close lines and their labels.</summary>
        private void RenderSessionOC(ChartControl cc, ChartScale cs, float rtW)
        {
            if (!RenderPrereqsOk()) return;

            List<KLSessionOC> snapshot;
            lock (_sessionLock) { snapshot = _ocList.ToList(); }

            if (snapshot.Count == 0) return;

            DateTime etToday = _latestBarEtDate != DateTime.MinValue
                ? _latestBarEtDate
                : TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, EtZone).Date;

            float lastBarX = cc.GetXByBarIndex(ChartBars, ChartBars.ToIndex);

            for (int i = 0; i < snapshot.Count; i++)
            {
                KLSessionOC entry = snapshot[i];

                int daysOld = (etToday - entry.SessionDate).Days;
                if (daysOld >= OcExtensionDays) continue;

                if (!IsOcSessionEnabled(entry.SessionId)) continue;

                // Skip if the entry's open bar is beyond the visible right edge
                if (entry.OpenBarIndex > ChartBars.ToIndex) continue;

                SharpDX.Direct2D1.SolidColorBrush ocBrush = GetOcBrush(entry.SessionId);
                if (ocBrush == null) continue;

                float thickness  = GetOcThickness(entry.SessionId);
                bool  showLabel  = GlobalShowLabels && GetOcShowLabels(entry.SessionId);
                bool  showOpen   = GetOcShowOpen(entry.SessionId);
                bool  showClose  = GetOcShowClose(entry.SessionId);
                string dateLabel = GetSessionDateLabel(entry.SessionDate, etToday);

                float savedOpacity = ocBrush.Opacity;
                if (FadeOlderOC && daysOld > 0)
                {
                    float fadeOpacity = savedOpacity * Math.Max(0.15f, 1f - daysOld * 0.12f);
                    ocBrush.Opacity = Math.Min(savedOpacity, Math.Max(0.08f, fadeOpacity));
                }

                try
                {
                    // ── Open line (always visible while in extension window) ──
                    if (showOpen && entry.OpenPrice != 0)
                    {
                        int clampedOpenStart = Math.Max(ChartBars.FromIndex, entry.OpenBarIndex);
                        float xOpenStart = cc.GetXByBarIndex(ChartBars, clampedOpenStart);
                        float xOpenEnd   = ExtendLinesToRightEdge ? rtW : Math.Max(xOpenStart, lastBarX);
                        float yOpen      = cs.GetYByValue(entry.OpenPrice);

                        if (!float.IsNaN(yOpen) && !float.IsInfinity(yOpen) && xOpenStart <= xOpenEnd)
                        {
                            DrawStyledLine(xOpenStart, yOpen, xOpenEnd, yOpen, ocBrush, thickness, GetOcOpenStyle(entry.SessionId));

                            if (showLabel && _dxLabelFormat != null)
                            {
                                string label = string.Format("{0} Open {1} {2}",
                                    entry.SessionName,
                                    dateLabel,
                                    Instrument.MasterInstrument.FormatPrice(entry.OpenPrice));
                                float labelX = LabelAnchor == IQKLLabelAnchor.LineEnd
                                    ? xOpenEnd - (PocLabelWidth + 4f)
                                    : xOpenStart + 4f;
                                labelX = ClampLabelX(labelX, rtW, PocLabelWidth);
                                float labelY = GetNonCollidingLabelY(yOpen - 14f, LabelAnchor == IQKLLabelAnchor.LineEnd);
                                RenderTarget.DrawText(label, _dxLabelFormat,
                                    new SharpDX.RectangleF(labelX, labelY, PocLabelWidth, 16f), ocBrush);
                            }
                        }
                    }

                    // ── Close line (only when session is complete) ──
                    if (showClose && entry.IsComplete && entry.ClosePrice != 0)
                    {
                        // Close line starts at the close bar index
                        if (entry.CloseBarIndex <= ChartBars.ToIndex)
                        {
                            int clampedCloseStart = Math.Max(ChartBars.FromIndex, entry.CloseBarIndex);
                            float xCloseStart = cc.GetXByBarIndex(ChartBars, clampedCloseStart);
                            float xCloseEnd   = ExtendLinesToRightEdge ? rtW : Math.Max(xCloseStart, lastBarX);
                            float yClose      = cs.GetYByValue(entry.ClosePrice);

                            if (!float.IsNaN(yClose) && !float.IsInfinity(yClose) && xCloseStart <= xCloseEnd)
                            {
                                DrawStyledLine(xCloseStart, yClose, xCloseEnd, yClose, ocBrush, thickness, GetOcCloseStyle(entry.SessionId));

                                if (showLabel && _dxLabelFormat != null)
                                {
                                    string label = string.Format("{0} Close {1} {2}",
                                        entry.SessionName,
                                        dateLabel,
                                        Instrument.MasterInstrument.FormatPrice(entry.ClosePrice));
                                    float labelX = LabelAnchor == IQKLLabelAnchor.LineEnd
                                        ? xCloseEnd - (PocLabelWidth + 4f)
                                        : xCloseStart + 4f;
                                    labelX = ClampLabelX(labelX, rtW, PocLabelWidth);
                                    float labelY = GetNonCollidingLabelY(yClose - 14f, LabelAnchor == IQKLLabelAnchor.LineEnd);
                                    RenderTarget.DrawText(label, _dxLabelFormat,
                                        new SharpDX.RectangleF(labelX, labelY, PocLabelWidth, 16f), ocBrush);
                                }
                            }
                        }
                    }
                }
                finally
                {
                    ocBrush.Opacity = savedOpacity;
                }
            }
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Render helpers — DrawStyledLine, GetNonCollidingLabelY, POC brush lookup

        private void DrawStyledLine(float x1, float y1, float x2, float y2,
            SharpDX.Direct2D1.SolidColorBrush brush, float strokeWidth, IQKLLineStyle style)
        {
            if (brush == null) return;
            var rt = RenderTarget;

            if (style == IQKLLineStyle.Solid)
            {
                rt.DrawLine(new SharpDX.Vector2(x1, y1), new SharpDX.Vector2(x2, y2), brush, strokeWidth);
                return;
            }

            float dashLen  = style == IQKLLineStyle.Dashed ? 8f : 3f;
            float gapLen   = style == IQKLLineStyle.Dashed ? 4f : 3f;
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

        private float GetNonCollidingLabelY(float y, bool useRightBucket, float labelHeight = 18f)
        {
            // Bucket by anchor region so left-anchored labels only collide with left-anchored
            // labels, and right-edge / line-end labels only collide with their own bucket.
            HashSet<int> bucket = useRightBucket ? _usedLabelYRight : _usedLabelYLeft;

            // Part D: use font-sized step + margin to guarantee readable separation.
            int step      = Math.Max((int)labelHeight, LabelFontSize + 4);
            int threshold = LabelFontSize + 3;

            int yInt = (int)y;
            bool collision = true;
            int maxIter = 80; // guard against infinite loop
            while (collision && maxIter-- > 0)
            {
                collision = false;
                // Part D: keep-out band — never overprint the current-price marker box
                if (!float.IsNaN(_currentPriceY) && Math.Abs(yInt - (int)_currentPriceY) < LabelFontSize)
                { collision = true; }

                if (!collision)
                {
                    foreach (int used in bucket)
                    {
                        if (Math.Abs(used - yInt) < threshold) { collision = true; break; }
                    }
                }
                if (collision) yInt += step;
            }
            bucket.Add(yInt);
            return (float)yInt;
        }

        /// <summary>Clamps a label's X position so its full width stays within the visible chart
        /// area — prevents LineEnd-anchored labels from running off-screen near the right edge.</summary>
        private static float ClampLabelX(float labelX, float rtW, float labelWidth)
        {
            const float margin = 2f;
            float minX = margin;
            float maxX = rtW - labelWidth - margin;
            if (maxX < minX) maxX = minX; // chart narrower than the label itself
            if (labelX < minX) return minX;
            if (labelX > maxX) return maxX;
            return labelX;
        }

        /// <summary>True when Bars/ChartBars/RenderTarget are all available — guards render
        /// helpers against null references during replay/teardown edge cases.</summary>
        private bool RenderPrereqsOk()
        {
            return Bars != null && ChartBars != null && RenderTarget != null;
        }

        /// <summary>"Mon.", "Tues.", "Wed.", "Thur.", "Fri.", "Sat.", "Sun."</summary>
        private static string GetWeekdayAbbrev(DayOfWeek dow)
        {
            switch (dow)
            {
                case DayOfWeek.Monday:    return "Mon.";
                case DayOfWeek.Tuesday:   return "Tues.";
                case DayOfWeek.Wednesday: return "Wed.";
                case DayOfWeek.Thursday:  return "Thur.";
                case DayOfWeek.Friday:    return "Fri.";
                case DayOfWeek.Saturday:  return "Sat.";
                case DayOfWeek.Sunday:    return "Sun.";
                default:                  return "";
            }
        }

        /// <summary>Weekday-abbreviation label for a session date, prefixed "Pr" (e.g. "PrMon.")
        /// when the session date falls in a prior week relative to the current bar's week
        /// (Sunday-anchored week start, matching the week-rollover convention used elsewhere).</summary>
        private static string GetSessionDateLabel(DateTime sessionDate, DateTime currentBarDate)
        {
            DateTime entryWeekStart   = GetSundayAnchoredWeekStart(sessionDate);
            DateTime currentWeekStart = GetSundayAnchoredWeekStart(currentBarDate);
            string   dayAbbrev        = GetWeekdayAbbrev(sessionDate.DayOfWeek);
            return entryWeekStart < currentWeekStart ? "Pr" + dayAbbrev : dayAbbrev;
        }

        private static DateTime GetSundayAnchoredWeekStart(DateTime date)
        {
            return date.Date.AddDays(-(int)date.DayOfWeek);
        }

        private SharpDX.Direct2D1.SolidColorBrush GetPocBrush(int sessionId)
        {
            switch (sessionId) { case 0: return _dxAsiaPocBrush; case 1: return _dxLondonPocBrush; case 2: return _dxNyPocBrush; default: return null; }
        }

        private bool GetPocShowLabels(int sessionId)
        {
            switch (sessionId) { case 0: return ShowAsiaPocLabels; case 1: return ShowLondonPocLabels; case 2: return ShowNyPocLabels; default: return false; }
        }

        private IQKLLineStyle GetPocLineStyle(int sessionId)
        {
            switch (sessionId) { case 0: return AsiaPocLineStyle; case 1: return LondonPocLineStyle; case 2: return NyPocLineStyle; default: return IQKLLineStyle.Solid; }
        }

        private float GetPocThickness(int sessionId)
        {
            switch (sessionId) { case 0: return (float)AsiaPocThickness; case 1: return (float)LondonPocThickness; case 2: return (float)NyPocThickness; default: return 1.5f; }
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
                _dxWriteFactory = new SharpDX.DirectWrite.Factory();
                _dxLabelFormat  = new SharpDX.DirectWrite.TextFormat(_dxWriteFactory, "Consolas",
                    Math.Max(8f, Math.Min(16f, (float)LabelFontSize)));

                // Session POC brushes
                _dxAsiaPocBrush   = MakeBrush(rt, AsiaPocColor,    AsiaPocOpacity   / 100f);
                _dxLondonPocBrush = MakeBrush(rt, LondonPocColor,  LondonPocOpacity / 100f);
                _dxNyPocBrush     = MakeBrush(rt, NyPocColor,      NyPocOpacity     / 100f);

                // RD Range
                _dxRdBrush = MakeBrush(rt, RdColor, RdOpacity / 100f);

                // Psy Levels
                _dxDailyPsyBrush   = MakeBrush(rt, DailyPsyColor,   DailyPsyOpacity   / 100f);
                _dxWeeklyPsyBrush  = MakeBrush(rt, WeeklyPsyColor,  WeeklyPsyOpacity  / 100f);
                _dxMonthlyPsyBrush = MakeBrush(rt, MonthlyPsyColor, MonthlyPsyOpacity / 100f);

                // Weekly / Monthly Hi-Lo
                _dxWeekHlBrush  = MakeBrush(rt, LastWeekColor,  LastWeekOpacity  / 100f);
                _dxMonthHlBrush = MakeBrush(rt, LastMonthColor, LastMonthOpacity / 100f);

                // Hourly Opens
                _dxHourlyOpenBrush = MakeBrush(rt, HourlyOpenColor, HourlyOpenOpacity / 100f);

                // L2 Walls
                _dxWallBidBrush = MakeBrush(rt, BidWallColor, BidWallOpacity / 100f);
                _dxWallAskBrush = MakeBrush(rt, AskWallColor, AskWallOpacity / 100f);

                // Session Open/Close brushes (one per session)
                _dxAsiaOcBrush   = MakeBrush(rt, AsiaOcColor,   AsiaOcOpacity   / 100f);
                _dxLondonOcBrush = MakeBrush(rt, LondonOcColor, LondonOcOpacity / 100f);
                _dxNyOcBrush     = MakeBrush(rt, NyOcColor,     NyOcOpacity     / 100f);

                // POC Cluster zone shading
                _dxClusterBrush = MakeBrush(rt, ClusterColor, ClusterOpacity / 100f);

                // Gap zone brushes
                _dxGapUpBrush   = MakeBrush(rt, GapUpColor,   GapOpacity / 100f);
                _dxGapDownBrush = MakeBrush(rt, GapDownColor, GapOpacity / 100f);

                dxReady = true;
            }
            catch (Exception ex)
            {
                Print("IQKeyLevelsGPU: CreateDXResources failed [" + ex.GetType().Name + "]: " + ex.Message);
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
            return new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(1f, 1f, 1f, opacity));
        }

        private void DisposeDXResources()
        {
            DisposeRef(ref _dxWriteFactory);
            DisposeRef(ref _dxLabelFormat);
            DisposeRef(ref _dxAsiaPocBrush);
            DisposeRef(ref _dxLondonPocBrush);
            DisposeRef(ref _dxNyPocBrush);
            DisposeRef(ref _dxRdBrush);
            DisposeRef(ref _dxDailyPsyBrush);
            DisposeRef(ref _dxWeeklyPsyBrush);
            DisposeRef(ref _dxMonthlyPsyBrush);
            DisposeRef(ref _dxWeekHlBrush);
            DisposeRef(ref _dxMonthHlBrush);
            DisposeRef(ref _dxHourlyOpenBrush);
            DisposeRef(ref _dxWallBidBrush);
            DisposeRef(ref _dxWallAskBrush);
            DisposeRef(ref _dxAsiaOcBrush);
            DisposeRef(ref _dxLondonOcBrush);
            DisposeRef(ref _dxNyOcBrush);
            DisposeRef(ref _dxClusterBrush);
            DisposeRef(ref _dxGapUpBrush);
            DisposeRef(ref _dxGapDownBrush);
        }

        private static void DisposeRef<T>(ref T resource) where T : class, IDisposable
        {
            if (resource != null) { resource.Dispose(); resource = null; }
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Properties — 1a. Session Windows (ET)

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Asia Start Hour (ET)", Order = 1, GroupName = "1a. Session Windows (ET)",
            Description = "Hour (0-23) the Asia session/POC window starts, Eastern Time.")]
        public int AsiaStartHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Asia Start Min (ET)", Order = 2, GroupName = "1a. Session Windows (ET)")]
        public int AsiaStartMin { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Asia End Hour (ET)", Order = 3, GroupName = "1a. Session Windows (ET)",
            Description = "Hour (0-23) the Asia session/POC window ends, Eastern Time. End <= Start wraps past midnight.")]
        public int AsiaEndHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Asia End Min (ET)", Order = 4, GroupName = "1a. Session Windows (ET)")]
        public int AsiaEndMin { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "London Start Hour (ET)", Order = 5, GroupName = "1a. Session Windows (ET)")]
        public int LondonStartHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "London Start Min (ET)", Order = 6, GroupName = "1a. Session Windows (ET)")]
        public int LondonStartMin { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "London End Hour (ET)", Order = 7, GroupName = "1a. Session Windows (ET)")]
        public int LondonEndHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "London End Min (ET)", Order = 8, GroupName = "1a. Session Windows (ET)")]
        public int LondonEndMin { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "NY Start Hour (ET)", Order = 9, GroupName = "1a. Session Windows (ET)")]
        public int NyStartHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "NY Start Min (ET)", Order = 10, GroupName = "1a. Session Windows (ET)")]
        public int NyStartMin { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "NY End Hour (ET)", Order = 11, GroupName = "1a. Session Windows (ET)")]
        public int NyEndHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "NY End Min (ET)", Order = 12, GroupName = "1a. Session Windows (ET)")]
        public int NyEndMin { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Properties — 1. Session POCs — Asia

        [NinjaScriptProperty]
        [Display(Name = "Show Asia POC", Order = 1, GroupName = "1. Session POCs — Asia")]
        public bool ShowAsiaPoc { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Asia POC Color", Order = 2, GroupName = "1. Session POCs — Asia")]
        [XmlIgnore]
        public System.Windows.Media.Brush AsiaPocColor { get; set; }
        [Browsable(false)]
        public string AsiaPocColorSerializable
        { get { return Serialize.BrushToString(AsiaPocColor); } set { AsiaPocColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Asia POC Opacity %", Order = 3, GroupName = "1. Session POCs — Asia")]
        public int AsiaPocOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Asia POC Line Style", Order = 4, GroupName = "1. Session POCs — Asia")]
        public IQKLLineStyle AsiaPocLineStyle { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Asia POC Thickness", Order = 5, GroupName = "1. Session POCs — Asia")]
        public int AsiaPocThickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Asia POC Labels", Order = 6, GroupName = "1. Session POCs — Asia")]
        public bool ShowAsiaPocLabels { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Properties — 1. Session POCs — London

        [NinjaScriptProperty]
        [Display(Name = "Show London POC", Order = 1, GroupName = "1. Session POCs — London")]
        public bool ShowLondonPoc { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "London POC Color", Order = 2, GroupName = "1. Session POCs — London")]
        [XmlIgnore]
        public System.Windows.Media.Brush LondonPocColor { get; set; }
        [Browsable(false)]
        public string LondonPocColorSerializable
        { get { return Serialize.BrushToString(LondonPocColor); } set { LondonPocColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "London POC Opacity %", Order = 3, GroupName = "1. Session POCs — London")]
        public int LondonPocOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "London POC Line Style", Order = 4, GroupName = "1. Session POCs — London")]
        public IQKLLineStyle LondonPocLineStyle { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "London POC Thickness", Order = 5, GroupName = "1. Session POCs — London")]
        public int LondonPocThickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show London POC Labels", Order = 6, GroupName = "1. Session POCs — London")]
        public bool ShowLondonPocLabels { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Properties — 1. Session POCs — New York

        [NinjaScriptProperty]
        [Display(Name = "Show NY POC", Order = 1, GroupName = "1. Session POCs — New York")]
        public bool ShowNyPoc { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "NY POC Color", Order = 2, GroupName = "1. Session POCs — New York")]
        [XmlIgnore]
        public System.Windows.Media.Brush NyPocColor { get; set; }
        [Browsable(false)]
        public string NyPocColorSerializable
        { get { return Serialize.BrushToString(NyPocColor); } set { NyPocColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "NY POC Opacity %", Order = 3, GroupName = "1. Session POCs — New York")]
        public int NyPocOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "NY POC Line Style", Order = 4, GroupName = "1. Session POCs — New York")]
        public IQKLLineStyle NyPocLineStyle { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "NY POC Thickness", Order = 5, GroupName = "1. Session POCs — New York")]
        public int NyPocThickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show NY POC Labels", Order = 6, GroupName = "1. Session POCs — New York")]
        public bool ShowNyPocLabels { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Properties — 1. Session POCs — General

        [NinjaScriptProperty]
        [Range(1, 7)]
        [Display(Name = "POC Extension Days", Order = 1, GroupName = "1. Session POCs — General",
            Description = "How many calendar days each POC line extends forward (1–7).")]
        public int PocExtensionDays { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "POC Bin Multiplier", Order = 2, GroupName = "1. Session POCs — General",
            Description = "Volume bucket size = TickSize × BinMultiplier. Larger values create coarser profiles.")]
        public int PocBinMultiplier { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Fade Older POCs", Order = 3, GroupName = "1. Session POCs — General",
            Description = "Progressively reduce opacity of older POC lines (−12% per day back).")]
        public bool FadeOlderPocs { get; set; }

        [NinjaScriptProperty]
        [Range(50, 5000)]
        [Display(Name = "Limit POC Bins", Order = 4, GroupName = "1. Session POCs — General",
            Description = "Max volume-profile buckets per session before auto-coarsening (doubling bin size and re-bucketing) to bound memory/CPU use.")]
        public int LimitPocBins { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Properties — 1c. POC Clusters

        [NinjaScriptProperty]
        [Display(Name = "Show POC Clusters", Order = 1, GroupName = "1c. POC Clusters",
            Description = "Highlight zones where multiple session POCs cluster within a narrow price range.")]
        public bool ShowPocClusters { get; set; }

        [NinjaScriptProperty]
        [Range(5.0, 200.0)]
        [Display(Name = "Cluster Zone Width (points)", Order = 2, GroupName = "1c. POC Clusters",
            Description = "Max price spread (in points) between the lowest and highest POC in a cluster group. Used when Cluster Width Mode = FixedPoints.")]
        public double ClusterZoneWidthPoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Cluster Width Mode", Order = 3, GroupName = "1c. POC Clusters",
            Description = "FixedPoints: use Cluster Zone Width (points). PercentOfADR: % of average daily range (auto-scales across NQ/ES/RTY/YM/CL/GC). Ticks: fixed tick count.")]
        public IQKLWidthMode ClusterWidthMode { get; set; }

        [NinjaScriptProperty]
        [Range(0.25, 10.0)]
        [Display(Name = "Cluster Width % of ADR", Order = 4, GroupName = "1c. POC Clusters",
            Description = "Used when Cluster Width Mode = PercentOfADR. Zone width = this % of the rolling average daily range. Falls back to FixedPoints if fewer than 3 completed days are available.")]
        public double ClusterWidthPctADR { get; set; }

        [NinjaScriptProperty]
        [Range(4, 2000)]
        [Display(Name = "Cluster Width (ticks)", Order = 5, GroupName = "1c. POC Clusters",
            Description = "Used when Cluster Width Mode = Ticks. Zone width = ticks × TickSize.")]
        public int ClusterWidthTicks { get; set; }

        [NinjaScriptProperty]
        [Range(2, 5)]
        [Display(Name = "Cluster Min POC Count", Order = 6, GroupName = "1c. POC Clusters",
            Description = "Minimum number of POCs grouped within the zone width required to form a visible cluster zone.")]
        public int ClusterMinPocCount { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Cluster Color", Order = 7, GroupName = "1c. POC Clusters")]
        [XmlIgnore]
        public System.Windows.Media.Brush ClusterColor { get; set; }
        [Browsable(false)]
        public string ClusterColorSerializable
        { get { return Serialize.BrushToString(ClusterColor); } set { ClusterColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "Cluster Opacity %", Order = 8, GroupName = "1c. POC Clusters")]
        public int ClusterOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Cluster Labels", Order = 9, GroupName = "1c. POC Clusters",
            Description = "Toggle the \"POC Cluster ×N\" label drawn on each zone.")]
        public bool ShowClusterLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Cluster Audio Alert", Order = 10, GroupName = "1c. POC Clusters",
            Description = "Play an alert sound the first time price enters a cluster zone (real-time only).")]
        public bool ClusterAudioAlert { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Suppress POC Labels In Clusters", Order = 11, GroupName = "1c. POC Clusters",
            Description = "When enabled (default), individual session POC labels are hidden for any POC that belongs to a rendered cluster zone. The cluster label (e.g. \"POC Cluster ×4  29800.75–29829.25  (L,N)\") carries all relevant information, eliminating label pile-up inside the zone. Disable to restore individual POC labels regardless of cluster membership.")]
        public bool SuppressPocLabelsInClusters { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Properties — 1b. Session Opens/Closes — Asia
        // NOTE: Asia keeps ONLY its POC — these legacy properties are retained purely so existing
        // saved workspaces/templates deserialize without error. They are hidden from the property
        // grid ([Browsable(false)]) and ignored by all Open/Close logic and rendering
        // (see IsOcSessionEnabled, which always returns false for Asia).

        [NinjaScriptProperty]
        [Browsable(false)]
        [Display(Name = "Show Asia Open/Close", Order = 1, GroupName = "1b. Session Opens/Closes — Asia",
            Description = "Master toggle for Asia session Open and Close lines.")]
        public bool ShowAsiaOC { get; set; }

        [NinjaScriptProperty]
        [Browsable(false)]
        [Display(Name = "Show Asia Open Lines", Order = 2, GroupName = "1b. Session Opens/Closes — Asia")]
        public bool ShowAsiaOCOpen { get; set; }

        [NinjaScriptProperty]
        [Browsable(false)]
        [Display(Name = "Show Asia Close Lines", Order = 3, GroupName = "1b. Session Opens/Closes — Asia")]
        public bool ShowAsiaOCClose { get; set; }

        [NinjaScriptProperty]
        [Browsable(false)]
        [Display(Name = "Asia OC Color", Order = 4, GroupName = "1b. Session Opens/Closes — Asia")]
        [XmlIgnore]
        public System.Windows.Media.Brush AsiaOcColor { get; set; }
        [Browsable(false)]
        public string AsiaOcColorSerializable
        { get { return Serialize.BrushToString(AsiaOcColor); } set { AsiaOcColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Browsable(false)]
        [Display(Name = "Asia OC Opacity %", Order = 5, GroupName = "1b. Session Opens/Closes — Asia")]
        public int AsiaOcOpacity { get; set; }

        [NinjaScriptProperty]
        [Browsable(false)]
        [Display(Name = "Asia Open Line Style", Order = 6, GroupName = "1b. Session Opens/Closes — Asia")]
        public IQKLLineStyle AsiaOcOpenStyle { get; set; }

        [NinjaScriptProperty]
        [Browsable(false)]
        [Display(Name = "Asia Close Line Style", Order = 7, GroupName = "1b. Session Opens/Closes — Asia")]
        public IQKLLineStyle AsiaOcCloseStyle { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Browsable(false)]
        [Display(Name = "Asia OC Thickness", Order = 8, GroupName = "1b. Session Opens/Closes — Asia")]
        public int AsiaOcThickness { get; set; }

        [NinjaScriptProperty]
        [Browsable(false)]
        [Display(Name = "Show Asia OC Labels", Order = 9, GroupName = "1b. Session Opens/Closes — Asia")]
        public bool ShowAsiaOCLabels { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Properties — 1b. Session Opens/Closes — London

        [NinjaScriptProperty]
        [Display(Name = "Show London Open/Close", Order = 1, GroupName = "1b. Session Opens/Closes — London",
            Description = "Master toggle for London session Open and Close lines.")]
        public bool ShowLondonOC { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show London Open Lines", Order = 2, GroupName = "1b. Session Opens/Closes — London")]
        public bool ShowLondonOCOpen { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show London Close Lines", Order = 3, GroupName = "1b. Session Opens/Closes — London")]
        public bool ShowLondonOCClose { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "London OC Color", Order = 4, GroupName = "1b. Session Opens/Closes — London")]
        [XmlIgnore]
        public System.Windows.Media.Brush LondonOcColor { get; set; }
        [Browsable(false)]
        public string LondonOcColorSerializable
        { get { return Serialize.BrushToString(LondonOcColor); } set { LondonOcColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "London OC Opacity %", Order = 5, GroupName = "1b. Session Opens/Closes — London")]
        public int LondonOcOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "London Open Line Style", Order = 6, GroupName = "1b. Session Opens/Closes — London")]
        public IQKLLineStyle LondonOcOpenStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "London Close Line Style", Order = 7, GroupName = "1b. Session Opens/Closes — London")]
        public IQKLLineStyle LondonOcCloseStyle { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "London OC Thickness", Order = 8, GroupName = "1b. Session Opens/Closes — London")]
        public int LondonOcThickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show London OC Labels", Order = 9, GroupName = "1b. Session Opens/Closes — London")]
        public bool ShowLondonOCLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "End London Profile at NY Open", Order = 10, GroupName = "1b. Session Opens/Closes — London",
            Description = "When enabled, London's volume-at-price profile stops accumulating at the configured NY session start instead of the configured London end. London Close price still uses the configured London end time.")]
        public bool EndLondonAtNyOpen { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Properties — 1b. Session Opens/Closes — New York

        [NinjaScriptProperty]
        [Display(Name = "Show NY Open/Close", Order = 1, GroupName = "1b. Session Opens/Closes — New York",
            Description = "Master toggle for New York session Open and Close lines.")]
        public bool ShowNyOC { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show NY Open Lines", Order = 2, GroupName = "1b. Session Opens/Closes — New York")]
        public bool ShowNyOCOpen { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show NY Close Lines", Order = 3, GroupName = "1b. Session Opens/Closes — New York")]
        public bool ShowNyOCClose { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "NY OC Color", Order = 4, GroupName = "1b. Session Opens/Closes — New York")]
        [XmlIgnore]
        public System.Windows.Media.Brush NyOcColor { get; set; }
        [Browsable(false)]
        public string NyOcColorSerializable
        { get { return Serialize.BrushToString(NyOcColor); } set { NyOcColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "NY OC Opacity %", Order = 5, GroupName = "1b. Session Opens/Closes — New York")]
        public int NyOcOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "NY Open Line Style", Order = 6, GroupName = "1b. Session Opens/Closes — New York")]
        public IQKLLineStyle NyOcOpenStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "NY Close Line Style", Order = 7, GroupName = "1b. Session Opens/Closes — New York")]
        public IQKLLineStyle NyOcCloseStyle { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "NY OC Thickness", Order = 8, GroupName = "1b. Session Opens/Closes — New York")]
        public int NyOcThickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show NY OC Labels", Order = 9, GroupName = "1b. Session Opens/Closes — New York")]
        public bool ShowNyOCLabels { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Properties — 1b. Session Opens/Closes — General

        [NinjaScriptProperty]
        [Range(1, 7)]
        [Display(Name = "OC Extension Days", Order = 1, GroupName = "1b. Session Opens/Closes — General",
            Description = "How many calendar days each Open/Close line extends forward (1–7).")]
        public int OcExtensionDays { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Fade Older Open/Close", Order = 2, GroupName = "1b. Session Opens/Closes — General",
            Description = "Progressively reduce opacity of older Open/Close lines (−12% per day back).")]
        public bool FadeOlderOC { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Properties — 1d. Gap Zones

        [NinjaScriptProperty]
        [Display(Name = "Show Gap Zones", Order = 1, GroupName = "1d. Gap Zones",
            Description = "Master toggle for session gap zones (Globex open vs prior close). Applies to daily and weekend gaps.")]
        public bool ShowGapZones { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Gap Up Color", Order = 2, GroupName = "1d. Gap Zones",
            Description = "Fill color for bullish gaps (open > prevClose).")]
        [XmlIgnore]
        public System.Windows.Media.Brush GapUpColor { get; set; }
        [Browsable(false)]
        public string GapUpColorSerializable
        { get { return Serialize.BrushToString(GapUpColor); } set { GapUpColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Display(Name = "Gap Down Color", Order = 3, GroupName = "1d. Gap Zones",
            Description = "Fill color for bearish gaps (open < prevClose).")]
        [XmlIgnore]
        public System.Windows.Media.Brush GapDownColor { get; set; }
        [Browsable(false)]
        public string GapDownColorSerializable
        { get { return Serialize.BrushToString(GapDownColor); } set { GapDownColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "Gap Opacity %", Order = 4, GroupName = "1d. Gap Zones",
            Description = "Fill opacity for unfilled gap zone rectangles (5–50).")]
        public int GapOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 14)]
        [Display(Name = "Gap Max Age (days)", Order = 5, GroupName = "1d. Gap Zones",
            Description = "Number of calendar days a gap zone is displayed before being removed (1–14).")]
        public int GapMaxAgeDays { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Gap Labels", Order = 6, GroupName = "1d. Gap Zones",
            Description = "Toggle the gap zone label (e.g. \"7/12 Gap  29980.25–30038.50\").")]
        public bool ShowGapLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Gap Filled Action", Order = 7, GroupName = "1d. Gap Zones",
            Description = "What happens when price fully trades through a gap zone: Remove (stop rendering), Dim (keep at reduced opacity with \" (filled)\" label suffix), Keep (unchanged).")]
        public IQKLGapFilledAction GapFilledAction { get; set; }

        [NinjaScriptProperty]
        [Range(1, 30)]
        [Display(Name = "Gap Dim Opacity %", Order = 8, GroupName = "1d. Gap Zones",
            Description = "Opacity used when Gap Filled Action = Dim (1–30). Default 8%.")]
        public int GapDimOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Gap Audio Alert", Order = 9, GroupName = "1d. Gap Zones",
            Description = "Play a one-shot alert the first time price enters an unfilled gap zone (real-time only).")]
        public bool GapAudioAlert { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Gap Min Size Mode", Order = 10, GroupName = "1d. Gap Zones",
            Description = "How the minimum gap size is measured: FixedPoints (GapMinSizePoints), PercentOfADR (% of rolling avg daily range — self-scales across instruments), or Ticks.")]
        public IQKLWidthMode GapMinSizeMode { get; set; }

        [NinjaScriptProperty]
        [Range(0.25, 1000.0)]
        [Display(Name = "Gap Min Size (points)", Order = 11, GroupName = "1d. Gap Zones",
            Description = "Minimum gap size in points. Used when Gap Min Size Mode = FixedPoints; also used as fallback when ADR data is insufficient.")]
        public double GapMinSizePoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "Gap Min Size % of ADR", Order = 12, GroupName = "1d. Gap Zones",
            Description = "Minimum gap size as % of rolling average daily range. Used when Gap Min Size Mode = PercentOfADR. Self-scales across NQ, ES, RTY, YM, CL, GC.")]
        public double GapMinSizePctADR { get; set; }

        [NinjaScriptProperty]
        [Range(1, 2000)]
        [Display(Name = "Gap Min Size (ticks)", Order = 13, GroupName = "1d. Gap Zones",
            Description = "Minimum gap size in ticks. Used when Gap Min Size Mode = Ticks.")]
        public int GapMinSizeTicks { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Properties — 2. Range Daily

        [NinjaScriptProperty]
        [Display(Name = "Show RD Bands", Order = 1, GroupName = "2. Range Daily")]
        public bool ShowRd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RD Color", Order = 2, GroupName = "2. Range Daily")]
        [XmlIgnore]
        public System.Windows.Media.Brush RdColor { get; set; }
        [Browsable(false)]
        public string RdColorSerializable
        { get { return Serialize.BrushToString(RdColor); } set { RdColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "RD Opacity %", Order = 3, GroupName = "2. Range Daily")]
        public int RdOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RD Line Style", Order = 4, GroupName = "2. Range Daily")]
        public IQKLLineStyle RdLineStyle { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "RD Thickness", Order = 5, GroupName = "2. Range Daily")]
        public int RdThickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show RD Labels", Order = 6, GroupName = "2. Range Daily")]
        public bool ShowRdLabels { get; set; }

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "RD Lookback Days", Order = 7, GroupName = "2. Range Daily",
            Description = "Number of prior trading days used to compute the average daily range.")]
        public int RdLength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Only Completed Days for RD", Order = 8, GroupName = "2. Range Daily",
            Description = "When enabled, RD High/Low are anchored to today's Open using only the completed-day average range, ignoring today's still-developing high/low so the bands stay fixed intraday.")]
        public bool UseOnlyCompletedDaysForRD { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Properties — 3. Psy Levels

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Psy Round Increment (ticks)", Order = 1, GroupName = "3. Psy Levels",
            Description = "Round-number increment in ticks (e.g. 50 = 50-tick round numbers).")]
        public int PsyRoundIncrement { get; set; }

        // ── Daily Psy ──
        [NinjaScriptProperty]
        [Display(Name = "Show Daily Psy", Order = 2, GroupName = "3. Psy Levels")]
        public bool ShowDailyPsy { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Daily Psy Color", Order = 3, GroupName = "3. Psy Levels")]
        [XmlIgnore]
        public System.Windows.Media.Brush DailyPsyColor { get; set; }
        [Browsable(false)]
        public string DailyPsyColorSerializable
        { get { return Serialize.BrushToString(DailyPsyColor); } set { DailyPsyColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Daily Psy Opacity %", Order = 4, GroupName = "3. Psy Levels")]
        public int DailyPsyOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Daily Psy Line Style", Order = 5, GroupName = "3. Psy Levels")]
        public IQKLLineStyle DailyPsyLineStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Daily Psy Labels", Order = 6, GroupName = "3. Psy Levels")]
        public bool ShowDailyPsyLabels { get; set; }

        // ── Weekly Psy ──
        [NinjaScriptProperty]
        [Display(Name = "Show Weekly Psy", Order = 7, GroupName = "3. Psy Levels")]
        public bool ShowWeeklyPsy { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Weekly Psy Color", Order = 8, GroupName = "3. Psy Levels")]
        [XmlIgnore]
        public System.Windows.Media.Brush WeeklyPsyColor { get; set; }
        [Browsable(false)]
        public string WeeklyPsyColorSerializable
        { get { return Serialize.BrushToString(WeeklyPsyColor); } set { WeeklyPsyColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Weekly Psy Opacity %", Order = 9, GroupName = "3. Psy Levels")]
        public int WeeklyPsyOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Weekly Psy Line Style", Order = 10, GroupName = "3. Psy Levels")]
        public IQKLLineStyle WeeklyPsyLineStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Weekly Psy Labels", Order = 11, GroupName = "3. Psy Levels")]
        public bool ShowWeeklyPsyLabels { get; set; }

        // ── Monthly Psy ──
        [NinjaScriptProperty]
        [Display(Name = "Show Monthly Psy", Order = 12, GroupName = "3. Psy Levels")]
        public bool ShowMonthlyPsy { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Monthly Psy Color", Order = 13, GroupName = "3. Psy Levels")]
        [XmlIgnore]
        public System.Windows.Media.Brush MonthlyPsyColor { get; set; }
        [Browsable(false)]
        public string MonthlyPsyColorSerializable
        { get { return Serialize.BrushToString(MonthlyPsyColor); } set { MonthlyPsyColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Monthly Psy Opacity %", Order = 14, GroupName = "3. Psy Levels")]
        public int MonthlyPsyOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Monthly Psy Line Style", Order = 15, GroupName = "3. Psy Levels")]
        public IQKLLineStyle MonthlyPsyLineStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Monthly Psy Labels", Order = 16, GroupName = "3. Psy Levels")]
        public bool ShowMonthlyPsyLabels { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Properties — 4. Weekly/Monthly Hi-Lo

        // ── Last Week ──
        [NinjaScriptProperty]
        [Display(Name = "Show Last Week Hi/Lo", Order = 1, GroupName = "4. Weekly/Monthly Hi-Lo")]
        public bool ShowLastWeek { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Last Week Color", Order = 2, GroupName = "4. Weekly/Monthly Hi-Lo")]
        [XmlIgnore]
        public System.Windows.Media.Brush LastWeekColor { get; set; }
        [Browsable(false)]
        public string LastWeekColorSerializable
        { get { return Serialize.BrushToString(LastWeekColor); } set { LastWeekColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Last Week Opacity %", Order = 3, GroupName = "4. Weekly/Monthly Hi-Lo")]
        public int LastWeekOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Last Week Line Style", Order = 4, GroupName = "4. Weekly/Monthly Hi-Lo")]
        public IQKLLineStyle LastWeekLineStyle { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Last Week Thickness", Order = 5, GroupName = "4. Weekly/Monthly Hi-Lo")]
        public int LastWeekThickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Last Week Labels", Order = 6, GroupName = "4. Weekly/Monthly Hi-Lo")]
        public bool ShowLastWeekLabels { get; set; }

        // ── Last Month ──
        [NinjaScriptProperty]
        [Display(Name = "Show Last Month Hi/Lo", Order = 7, GroupName = "4. Weekly/Monthly Hi-Lo")]
        public bool ShowLastMonth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Last Month Color", Order = 8, GroupName = "4. Weekly/Monthly Hi-Lo")]
        [XmlIgnore]
        public System.Windows.Media.Brush LastMonthColor { get; set; }
        [Browsable(false)]
        public string LastMonthColorSerializable
        { get { return Serialize.BrushToString(LastMonthColor); } set { LastMonthColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Last Month Opacity %", Order = 9, GroupName = "4. Weekly/Monthly Hi-Lo")]
        public int LastMonthOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Last Month Line Style", Order = 10, GroupName = "4. Weekly/Monthly Hi-Lo")]
        public IQKLLineStyle LastMonthLineStyle { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Last Month Thickness", Order = 11, GroupName = "4. Weekly/Monthly Hi-Lo")]
        public int LastMonthThickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Last Month Labels", Order = 12, GroupName = "4. Weekly/Monthly Hi-Lo")]
        public bool ShowLastMonthLabels { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Properties — 5. Hourly Opens

        [NinjaScriptProperty]
        [Display(Name = "Show Hourly Opens", Order = 1, GroupName = "5. Hourly Opens")]
        public bool ShowHourlyOpens { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Hourly Open Color", Order = 2, GroupName = "5. Hourly Opens")]
        [XmlIgnore]
        public System.Windows.Media.Brush HourlyOpenColor { get; set; }
        [Browsable(false)]
        public string HourlyOpenColorSerializable
        { get { return Serialize.BrushToString(HourlyOpenColor); } set { HourlyOpenColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Hourly Open Opacity %", Order = 3, GroupName = "5. Hourly Opens")]
        public int HourlyOpenOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Hourly Open Line Style", Order = 4, GroupName = "5. Hourly Opens")]
        public IQKLLineStyle HourlyOpenLineStyle { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Hourly Open Thickness", Order = 5, GroupName = "5. Hourly Opens")]
        public int HourlyOpenThickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Hourly Open Labels", Order = 6, GroupName = "5. Hourly Opens")]
        public bool ShowHourlyOpenLabels { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Start Hour (0–23)", Order = 7, GroupName = "5. Hourly Opens",
            Description = "Only track hourly opens at or after this hour (0 = midnight).")]
        public int HourlyStartHour { get; set; }

        [NinjaScriptProperty]
        [Range(1, 24)]
        [Display(Name = "End Hour (1–24)", Order = 8, GroupName = "5. Hourly Opens",
            Description = "Only track hourly opens before this hour (24 = all day).")]
        public int HourlyEndHour { get; set; }

        [NinjaScriptProperty]
        [Range(1, 24)]
        [Display(Name = "Max Lines to Show", Order = 9, GroupName = "5. Hourly Opens",
            Description = "Maximum number of hourly open lines visible at once.")]
        public int MaxHourlyLinesToShow { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Properties — 6. Level 2 Walls

        [NinjaScriptProperty]
        [Display(Name = "Enable Level 2", Order = 1, GroupName = "6. Level 2 Walls",
            Description = "Subscribe to OnMarketDepth for L2 order book wall detection. Requires L2 data feed.")]
        public bool EnableLevel2 { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Wall Multiplier", Order = 2, GroupName = "6. Level 2 Walls",
            Description = "A level is flagged as a wall when its size exceeds average × WallMultiplier.")]
        public int WallMultiplier { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Wall Lines", Order = 3, GroupName = "6. Level 2 Walls")]
        public bool ShowWallLines { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Bid Wall Color", Order = 4, GroupName = "6. Level 2 Walls")]
        [XmlIgnore]
        public System.Windows.Media.Brush BidWallColor { get; set; }
        [Browsable(false)]
        public string BidWallColorSerializable
        { get { return Serialize.BrushToString(BidWallColor); } set { BidWallColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Bid Wall Opacity %", Order = 5, GroupName = "6. Level 2 Walls")]
        public int BidWallOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Ask Wall Color", Order = 6, GroupName = "6. Level 2 Walls")]
        [XmlIgnore]
        public System.Windows.Media.Brush AskWallColor { get; set; }
        [Browsable(false)]
        public string AskWallColorSerializable
        { get { return Serialize.BrushToString(AskWallColor); } set { AskWallColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Ask Wall Opacity %", Order = 7, GroupName = "6. Level 2 Walls")]
        public int AskWallOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Wall Line Thickness", Order = 8, GroupName = "6. Level 2 Walls")]
        public int WallLineThickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Wall Labels", Order = 9, GroupName = "6. Level 2 Walls")]
        public bool ShowWallLabels { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Properties — 7. General

        [NinjaScriptProperty]
        [Range(8, 16)]
        [Display(Name = "Label Font Size", Order = 1, GroupName = "7. General")]
        public int LabelFontSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Global Show Labels", Order = 2, GroupName = "7. General",
            Description = "Master on/off for all labels on all features.")]
        public bool GlobalShowLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Label Anchor", Order = 3, GroupName = "7. General",
            Description = "LineStart: labels at line start (left edge). LineEnd: labels at line end (right edge, near current price).")]
        public IQKLLabelAnchor LabelAnchor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Extend Lines to Right Edge", Order = 4, GroupName = "7. General",
            Description = "When enabled (default), POC and Open/Close lines extend to the chart's right edge. When disabled, they render only out to the last printed bar.")]
        public bool ExtendLinesToRightEdge { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Merge Coincident Labels", Order = 5, GroupName = "7. General",
            Description = "When enabled (default), level labels (Psy, Hi-Lo, RD) at the same price (within ±1 tick) are merged into a single label, e.g. \"D+WPsy H 29962.50\" or \"LWH + MPsy H 30094.00\".")]
        public bool MergeCoincidentLabels { get; set; }

        #endregion
    }
}
