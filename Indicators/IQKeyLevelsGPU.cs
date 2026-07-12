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
            public Dictionary<double, double> VolumeProfile = new Dictionary<double, double>();
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

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Private fields — ET timezone helper

        private static readonly TimeZoneInfo EtZone = SafeFindEtZone();

        private static TimeZoneInfo SafeFindEtZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch (Exception) { return TimeZoneInfo.CreateCustomTimeZone("ET-Fallback", TimeSpan.FromHours(-5), "ET-Fallback", "ET-Fallback"); }
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

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Private fields — RD Range Daily

        private Queue<double> _rdRanges;
        private double        _rdValue, _rdHigh, _rdLow;

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Private fields — Session POCs

        private KLPocEntry[] _activePocSessions; // index: 0=Asia,1=London,2=NY
        private List<KLPocEntry> _pocList;
        private readonly object  _sessionLock = new object();

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
        #region Private fields — L2 Order Book

        private Dictionary<double, KLBookLevel> _bidBook;
        private Dictionary<double, KLBookLevel> _askBook;
        private double _wallBidPrice;
        private long   _wallBidSize;
        private double _wallAskPrice;
        private long   _wallAskSize;
        private bool   _level2Available;

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Private fields — SharpDX resources

        // Max POC entries = 3 sessions × 7 days (matches PocExtensionDays max)
        private const int MaxPocListSize = 21;

        // Cached ET date of the most-recently-processed bar (used in OnRender for age checks)
        private DateTime _latestBarEtDate = DateTime.MinValue;

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

        // Label collision avoidance — cleared per OnRender frame
        private readonly HashSet<int> _usedLabelYPositions = new HashSet<int>();

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region OnStateChange

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
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

                // 1b. Session Opens/Closes — Asia
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

                // ── Session Open/Close tracking ───────────────────────────────
                UpdateSessionOC(barEt);

                // ── Hourly open tracking ──────────────────────────────────────
                UpdateHourlyOpens(barEt);
            }

            // ── RD high/low recalculate every tick (uses current day stats) ──
            if (_rdRanges.Count > 0)
            {
                _rdValue = _rdRanges.Average();
                double rdSlack = (_rdValue - (_dayHigh - _dayLow)) / 2.0;
                _rdHigh = _dayHigh + rdSlack;
                _rdLow  = _dayLow  - rdSlack;
            }

            // Update live hourly open end bar
            if (_currentHourlyOpen != null)
                _currentHourlyOpen.EndBarIndex = CurrentBar;
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
                            IsComplete   = false
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

                    // Accumulate volume at typical price bucketed to bin size
                    double barVol = Volume[0];
                    if (barVol > 0)
                    {
                        double tp     = (High[0] + Low[0] + Close[0]) / 3.0;
                        double binSz  = TickSize * Math.Max(1, PocBinMultiplier);
                        double bucket = Math.Round(tp / binSz) * binSz;

                        if (sess.VolumeProfile.ContainsKey(bucket))
                            sess.VolumeProfile[bucket] += barVol;
                        else
                            sess.VolumeProfile[bucket]  = barVol;
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
            DateTime today = barEt.Date;
            switch (sessionId)
            {
                case 0: GetCrossMidnightWindow(barEt, 19, 4, out start, out end); break;   // Asia   19:00→04:00 ET
                case 1:
                    start = today.AddHours(3);
                    // When EndLondonAtNyOpen is enabled, London POC profile accumulates only until NY open (09:30)
                    end = EndLondonAtNyOpen
                        ? today.AddHours(9).AddMinutes(30)
                        : today.AddHours(11).AddMinutes(30);
                    break;  // London 03:00→11:30 ET (or 09:30)
                case 2: start = today.AddHours(9).AddMinutes(30); end = today.AddHours(16); break;  // NY     09:30→16:00 ET
                default: start = today; end = today; break;
            }
        }

        // Session OC window always uses the canonical session times (London always ends at 11:30,
        // regardless of EndLondonAtNyOpen which only affects the POC profile accumulation).
        private static void GetOcSessionWindow(int sessionId, DateTime barEt, out DateTime start, out DateTime end)
        {
            DateTime today = barEt.Date;
            switch (sessionId)
            {
                case 0: GetCrossMidnightWindow(barEt, 19, 4, out start, out end); break;   // Asia   19:00→04:00 ET
                case 1: start = today.AddHours(3); end = today.AddHours(11).AddMinutes(30); break;  // London 03:00→11:30 ET
                case 2: start = today.AddHours(9).AddMinutes(30); end = today.AddHours(16); break;  // NY     09:30→16:00 ET
                default: start = today; end = today; break;
            }
        }

        private static void GetCrossMidnightWindow(DateTime barEt, int startHour, int endHour,
            out DateTime start, out DateTime end)
        {
            DateTime today = barEt.Date;
            if (barEt.TimeOfDay >= TimeSpan.FromHours(startHour))
            { start = today.AddHours(startHour);             end = today.AddDays(1).AddHours(endHour); }
            else
            { start = today.AddDays(-1).AddHours(startHour); end = today.AddHours(endHour); }
        }

        private bool IsPocSessionEnabled(int sessionId)
        {
            switch (sessionId) { case 0: return ShowAsiaPoc; case 1: return ShowLondonPoc; case 2: return ShowNyPoc; default: return false; }
        }

        private bool IsOcSessionEnabled(int sessionId)
        {
            switch (sessionId) { case 0: return ShowAsiaOC; case 1: return ShowLondonOC; case 2: return ShowNyOC; default: return false; }
        }

        private string GetPocSessionName(int sessionId)
        {
            switch (sessionId) { case 0: return "Asia"; case 1: return "London"; case 2: return "NY"; default: return ""; }
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
                switch (e.Operation)
                {
                    case Operation.Add:
                    case Operation.Update:
                        if (!book.ContainsKey(price)) book[price] = new KLBookLevel { Price = price };
                        book[price].Size = size;
                        break;
                    case Operation.Remove:
                        if (book.ContainsKey(price)) book.Remove(price);
                        break;
                }

                DetectOrderBookWalls(book, isBid);
            }
        }

        private void DetectOrderBookWalls(Dictionary<double, KLBookLevel> book, bool isBid)
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

            // Clear label collision set for this frame
            _usedLabelYPositions.Clear();

            var rt   = RenderTarget;
            float rtW = (float)chartControl.ActualWidth;
            float rtH = (float)chartControl.ActualHeight;

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
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Render methods

        private void RenderSessionPocs(ChartControl cc, ChartScale cs, float rtW)
        {
            List<KLPocEntry> snapshot;
            lock (_sessionLock) { snapshot = _pocList.ToList(); }

            if (snapshot.Count == 0) return;

            // Use cached ET date for age calculations (avoids system-local DateTime.Today timezone issue)
            DateTime etToday = _latestBarEtDate != DateTime.MinValue
                ? _latestBarEtDate
                : TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, EtZone).Date;

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
                    // C1 fix: clamp xStart to visible area and extend xEnd to right edge
                    int clampedStart = Math.Max(ChartBars.FromIndex, entry.StartBarIndex);
                    float xStart = cc.GetXByBarIndex(ChartBars, clampedStart);
                    float xEnd   = rtW; // always extend to chart right edge
                    float yPoc   = cs.GetYByValue(entry.POCPrice);

                    if (!float.IsNaN(yPoc) && !float.IsInfinity(yPoc) && xStart <= xEnd)
                    {
                        DrawStyledLine(xStart, yPoc, xEnd, yPoc, pocBrush, thickness, style);

                        // C2 fix: only draw label when line is actually visible
                        if (showLabel && _dxLabelFormat != null)
                        {
                            string label = string.Format("{0} POC {1}/{2} {3}",
                                entry.SessionName,
                                entry.SessionDate.Month,
                                entry.SessionDate.Day,
                                Instrument.MasterInstrument.FormatPrice(entry.POCPrice));

                            // D: label anchor support
                            float labelX = LabelAnchor == IQKLLabelAnchor.LineEnd
                                ? xEnd - 224f
                                : xStart + 4f;
                            float labelY = GetNonCollidingLabelY(yPoc - 14f);
                            RenderTarget.DrawText(label, _dxLabelFormat,
                                new SharpDX.RectangleF(labelX, labelY, 220f, 16f), pocBrush);
                        }
                    }
                }
                finally
                {
                    pocBrush.Opacity = savedOpacity; // always restore opacity
                }
            }
        }

        private void RenderRdBands(ChartControl cc, ChartScale cs, float rtW)
        {
            if (_dxRdBrush == null || _rdHigh == 0 || _rdLow == 0) return;

            float yH = cs.GetYByValue(_rdHigh);
            float yL = cs.GetYByValue(_rdLow);

            if (!float.IsNaN(yH) && !float.IsInfinity(yH))
            {
                DrawStyledLine(0f, yH, rtW, yH, _dxRdBrush, RdThickness, RdLineStyle);
                if (GlobalShowLabels && ShowRdLabels && _dxLabelFormat != null)
                {
                    string label = string.Format("RD H {0}", Instrument.MasterInstrument.FormatPrice(_rdHigh));
                    float labelX = LabelAnchor == IQKLLabelAnchor.LineEnd ? rtW - 204f : 4f;
                    float labelY = GetNonCollidingLabelY(yH - 14f);
                    RenderTarget.DrawText(label, _dxLabelFormat,
                        new SharpDX.RectangleF(labelX, labelY, 200f, 16f), _dxRdBrush);
                }
            }

            if (!float.IsNaN(yL) && !float.IsInfinity(yL))
            {
                DrawStyledLine(0f, yL, rtW, yL, _dxRdBrush, RdThickness, RdLineStyle);
                if (GlobalShowLabels && ShowRdLabels && _dxLabelFormat != null)
                {
                    string label = string.Format("RD L {0}", Instrument.MasterInstrument.FormatPrice(_rdLow));
                    float labelX = LabelAnchor == IQKLLabelAnchor.LineEnd ? rtW - 204f : 4f;
                    float labelY = GetNonCollidingLabelY(yL - 14f);
                    RenderTarget.DrawText(label, _dxLabelFormat,
                        new SharpDX.RectangleF(labelX, labelY, 200f, 16f), _dxRdBrush);
                }
            }
        }

        private void RenderPsyLevels(ChartControl cc, ChartScale cs, float rtW)
        {
            double psyStep = PsyRoundIncrement * TickSize;
            if (psyStep <= 0) return;

            // Daily Psy
            if (ShowDailyPsy && _dxDailyPsyBrush != null && _psyDayHigh > 0)
            {
                double dPsyH = Math.Ceiling(_psyDayHigh / psyStep) * psyStep;
                double dPsyL = Math.Floor(_psyDayLow   / psyStep) * psyStep;
                RenderSingleLine(cc, cs, rtW, dPsyH, _dxDailyPsyBrush,
                    GlobalShowLabels && ShowDailyPsyLabels ? "DPsy H" : "", GlobalShowLabels && ShowDailyPsyLabels,
                    0f, DailyPsyLineStyle, 1f);
                RenderSingleLine(cc, cs, rtW, dPsyL, _dxDailyPsyBrush,
                    GlobalShowLabels && ShowDailyPsyLabels ? "DPsy L" : "", GlobalShowLabels && ShowDailyPsyLabels,
                    0f, DailyPsyLineStyle, 1f);
            }

            // Weekly Psy
            if (ShowWeeklyPsy && _dxWeeklyPsyBrush != null && _psyWeekHigh > 0)
            {
                double wPsyH = Math.Ceiling(_psyWeekHigh / psyStep) * psyStep;
                double wPsyL = Math.Floor(_psyWeekLow   / psyStep) * psyStep;
                RenderSingleLine(cc, cs, rtW, wPsyH, _dxWeeklyPsyBrush,
                    GlobalShowLabels && ShowWeeklyPsyLabels ? "WPsy H" : "", GlobalShowLabels && ShowWeeklyPsyLabels,
                    0f, WeeklyPsyLineStyle, 1f);
                RenderSingleLine(cc, cs, rtW, wPsyL, _dxWeeklyPsyBrush,
                    GlobalShowLabels && ShowWeeklyPsyLabels ? "WPsy L" : "", GlobalShowLabels && ShowWeeklyPsyLabels,
                    0f, WeeklyPsyLineStyle, 1f);
            }

            // Monthly Psy
            if (ShowMonthlyPsy && _dxMonthlyPsyBrush != null && _psyMonthHigh > 0)
            {
                double mPsyH = Math.Ceiling(_psyMonthHigh / psyStep) * psyStep;
                double mPsyL = Math.Floor(_psyMonthLow   / psyStep) * psyStep;
                RenderSingleLine(cc, cs, rtW, mPsyH, _dxMonthlyPsyBrush,
                    GlobalShowLabels && ShowMonthlyPsyLabels ? "MPsy H" : "", GlobalShowLabels && ShowMonthlyPsyLabels,
                    0f, MonthlyPsyLineStyle, 1f);
                RenderSingleLine(cc, cs, rtW, mPsyL, _dxMonthlyPsyBrush,
                    GlobalShowLabels && ShowMonthlyPsyLabels ? "MPsy L" : "", GlobalShowLabels && ShowMonthlyPsyLabels,
                    0f, MonthlyPsyLineStyle, 1f);
            }
        }

        private void RenderWeeklyMonthlyHiLo(ChartControl cc, ChartScale cs, float rtW)
        {
            // Last Week Hi/Lo
            if (ShowLastWeek && _dxWeekHlBrush != null && _lastWeekHigh > 0)
            {
                RenderSingleLine(cc, cs, rtW, _lastWeekHigh, _dxWeekHlBrush,
                    GlobalShowLabels && ShowLastWeekLabels ? "LWH" : "", GlobalShowLabels && ShowLastWeekLabels,
                    0f, LastWeekLineStyle, LastWeekThickness);
                RenderSingleLine(cc, cs, rtW, _lastWeekLow, _dxWeekHlBrush,
                    GlobalShowLabels && ShowLastWeekLabels ? "LWL" : "", GlobalShowLabels && ShowLastWeekLabels,
                    0f, LastWeekLineStyle, LastWeekThickness);
            }

            // Last Month Hi/Lo
            if (ShowLastMonth && _dxMonthHlBrush != null && _prevMonthHigh > 0)
            {
                RenderSingleLine(cc, cs, rtW, _prevMonthHigh, _dxMonthHlBrush,
                    GlobalShowLabels && ShowLastMonthLabels ? "LMH" : "", GlobalShowLabels && ShowLastMonthLabels,
                    0f, LastMonthLineStyle, LastMonthThickness);
                RenderSingleLine(cc, cs, rtW, _prevMonthLow, _dxMonthHlBrush,
                    GlobalShowLabels && ShowLastMonthLabels ? "LML" : "", GlobalShowLabels && ShowLastMonthLabels,
                    0f, LastMonthLineStyle, LastMonthThickness);
            }
        }

        private void RenderHourlyOpens(ChartControl cc, ChartScale cs, float rtW)
        {
            if (_dxHourlyOpenBrush == null) return;

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
                float xEnd = (ho == _currentHourlyOpen)
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
                    float labelY = GetNonCollidingLabelY(yOpen - 14f);
                    RenderTarget.DrawText(label, _dxLabelFormat,
                        new SharpDX.RectangleF(xEnd - 202f, labelY, 200f, 16f), _dxHourlyOpenBrush);
                }
            }
        }

        private void RenderWallLines(ChartControl cc, ChartScale cs, float rtW)
        {
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
                        float labelY = GetNonCollidingLabelY(yBid - 14f);
                        RenderTarget.DrawText(label, _dxLabelFormat,
                            new SharpDX.RectangleF(4f, labelY, 200f, 16f), _dxWallBidBrush);
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
                        float labelY = GetNonCollidingLabelY(yAsk - 14f);
                        RenderTarget.DrawText(label, _dxLabelFormat,
                            new SharpDX.RectangleF(4f, labelY, 200f, 16f), _dxWallAskBrush);
                    }
                }
            }
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
                    ? rtW - 204f
                    : xStart + 4f;
                float labelY = GetNonCollidingLabelY(y - 14f);
                RenderTarget.DrawText(text, _dxLabelFormat,
                    new SharpDX.RectangleF(labelX, labelY, 200f, 16f), brush);
            }
        }

        /// <summary>Render the Session Open/Close lines and their labels.</summary>
        private void RenderSessionOC(ChartControl cc, ChartScale cs, float rtW)
        {
            List<KLSessionOC> snapshot;
            lock (_sessionLock) { snapshot = _ocList.ToList(); }

            if (snapshot.Count == 0) return;

            DateTime etToday = _latestBarEtDate != DateTime.MinValue
                ? _latestBarEtDate
                : TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, EtZone).Date;

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
                        float xOpenEnd   = rtW;
                        float yOpen      = cs.GetYByValue(entry.OpenPrice);

                        if (!float.IsNaN(yOpen) && !float.IsInfinity(yOpen) && xOpenStart <= xOpenEnd)
                        {
                            DrawStyledLine(xOpenStart, yOpen, xOpenEnd, yOpen, ocBrush, thickness, GetOcOpenStyle(entry.SessionId));

                            if (showLabel && _dxLabelFormat != null)
                            {
                                string label = string.Format("{0} Open {1}/{2} {3}",
                                    entry.SessionName,
                                    entry.SessionDate.Month,
                                    entry.SessionDate.Day,
                                    Instrument.MasterInstrument.FormatPrice(entry.OpenPrice));
                                float labelX = LabelAnchor == IQKLLabelAnchor.LineEnd
                                    ? xOpenEnd - 224f
                                    : xOpenStart + 4f;
                                float labelY = GetNonCollidingLabelY(yOpen - 14f);
                                RenderTarget.DrawText(label, _dxLabelFormat,
                                    new SharpDX.RectangleF(labelX, labelY, 220f, 16f), ocBrush);
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
                            float xCloseEnd   = rtW;
                            float yClose      = cs.GetYByValue(entry.ClosePrice);

                            if (!float.IsNaN(yClose) && !float.IsInfinity(yClose) && xCloseStart <= xCloseEnd)
                            {
                                DrawStyledLine(xCloseStart, yClose, xCloseEnd, yClose, ocBrush, thickness, GetOcCloseStyle(entry.SessionId));

                                if (showLabel && _dxLabelFormat != null)
                                {
                                    string label = string.Format("{0} Close {1}/{2} {3}",
                                        entry.SessionName,
                                        entry.SessionDate.Month,
                                        entry.SessionDate.Day,
                                        Instrument.MasterInstrument.FormatPrice(entry.ClosePrice));
                                    float labelX = LabelAnchor == IQKLLabelAnchor.LineEnd
                                        ? xCloseEnd - 224f
                                        : xCloseStart + 4f;
                                    float labelY = GetNonCollidingLabelY(yClose - 14f);
                                    RenderTarget.DrawText(label, _dxLabelFormat,
                                        new SharpDX.RectangleF(labelX, labelY, 220f, 16f), ocBrush);
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
        }

        private static void DisposeRef<T>(ref T resource) where T : class, IDisposable
        {
            if (resource != null) { resource.Dispose(); resource = null; }
        }

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

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Properties — 1b. Session Opens/Closes — Asia

        [NinjaScriptProperty]
        [Display(Name = "Show Asia Open/Close", Order = 1, GroupName = "1b. Session Opens/Closes — Asia",
            Description = "Master toggle for Asia session Open and Close lines.")]
        public bool ShowAsiaOC { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Asia Open Lines", Order = 2, GroupName = "1b. Session Opens/Closes — Asia")]
        public bool ShowAsiaOCOpen { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Asia Close Lines", Order = 3, GroupName = "1b. Session Opens/Closes — Asia")]
        public bool ShowAsiaOCClose { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Asia OC Color", Order = 4, GroupName = "1b. Session Opens/Closes — Asia")]
        [XmlIgnore]
        public System.Windows.Media.Brush AsiaOcColor { get; set; }
        [Browsable(false)]
        public string AsiaOcColorSerializable
        { get { return Serialize.BrushToString(AsiaOcColor); } set { AsiaOcColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Asia OC Opacity %", Order = 5, GroupName = "1b. Session Opens/Closes — Asia")]
        public int AsiaOcOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Asia Open Line Style", Order = 6, GroupName = "1b. Session Opens/Closes — Asia")]
        public IQKLLineStyle AsiaOcOpenStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Asia Close Line Style", Order = 7, GroupName = "1b. Session Opens/Closes — Asia")]
        public IQKLLineStyle AsiaOcCloseStyle { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Asia OC Thickness", Order = 8, GroupName = "1b. Session Opens/Closes — Asia")]
        public int AsiaOcThickness { get; set; }

        [NinjaScriptProperty]
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
            Description = "When enabled, London's volume-at-price profile stops accumulating at 09:30 ET (NY open) instead of 11:30 ET. London Close price is still stamped at 11:30 either way.")]
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

        #endregion
    }
}
