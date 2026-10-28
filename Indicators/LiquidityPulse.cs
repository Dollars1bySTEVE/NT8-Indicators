#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Threading;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.Core.FloatingPoint;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
#endregion

public enum LiquidityPulseShadeMode
{
    FullHeight,
    TopBand,
    BottomBand
}

public enum LiquidityPulseHudCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

namespace NinjaTrader.NinjaScript.Indicators
{
    public class LiquidityPulse : Indicator
    {
        private class DepthRow
        {
            public double Price;
            public long Size;
        }

        private class WallState
        {
            public double Price;
            public bool IsAsk;
            public DateTime FirstSeen;
            public DateTime LastSeenAboveThreshold;
            public long PeakSize;
            public long LastSize;
            public double TradedBuy;
            public double TradedSell;
            public bool Active;
            public bool LostQualification;
        }

        private class WallSnapshot
        {
            public double Price;
            public bool IsAsk;
            public long Size;
            public long PeakSize;
            public double TradedBuy;
            public double TradedSell;
            public DateTime FirstSeen;
        }

        private enum SessionPhase
        {
            EthOvernight,
            EthLondon,
            RthOpen,
            Rth,
            EthPostRth,
            PostClose
        }

        private sealed class SizeSample
        {
            public DateTime Time;
            public double Average;
        }

        private readonly object bookLock = new object();
        private readonly List<DepthRow> askRows = new List<DepthRow>();
        private readonly List<DepthRow> bidRows = new List<DepthRow>();
        private readonly Dictionary<double, WallState> askWalls = new Dictionary<double, WallState>();
        private readonly Dictionary<double, WallState> bidWalls = new Dictionary<double, WallState>();
        private readonly Queue<SizeSample> sizeSamples = new Queue<SizeSample>();
        private readonly Dictionary<string, SharpDX.Direct2D1.SolidColorBrush> levelBrushes =
            new Dictionary<string, SharpDX.Direct2D1.SolidColorBrush>();
        private static readonly TimeZoneInfo EasternTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        private Swing swingHtf;
        private Swing swingConfirm;
        private Series<int> biasSeries;
        private Series<double> sessionVwapSeries;
        private Series<double> weeklyVwapSeries;
        private SessionIterator sessionIterator;

        private DateTime currentDayDate = DateTime.MinValue;
        private DateTime currentWeekStart = DateTime.MinValue;
        private DateTime cachedSessionEndEastern = DateTime.MinValue;
        private DateTime lastSampleTime = DateTime.MinValue;
        private DateTime lastDetectionTime = DateTime.MinValue;
        private DateTime lastClockTime = DateTime.MinValue;
        private bool pendingDetection;
        private SessionPhase lastPhase;
        private bool phaseInitialized;
        private double adaptiveBaseline = double.NaN;
        private double lastBid = double.NaN;
        private double lastAsk = double.NaN;
        private double sessionAggBuy;
        private double sessionAggSell;
        private int lastSessionBarIndex = -1;
        private int lastSessionIteratorBarIndex = -1;
        private long eventTimeBinary;
        private long lastDepthEventTimeBinary;
        private long lastDepthArrivalUtcBinary;
        private long lastEventArrivalUtcBinary;
        private long lastForceRefreshTimestamp;
        private System.Threading.Timer feedRefreshTimer;
        private System.Threading.Timer trailingDetectionTimer;

        private double currentDayOpen;
        private double currentDayHigh;
        private double currentDayLow;
        private double priorDayOpen;
        private double priorDayHigh;
        private double priorDayLow;
        private double currentWeekOpen;
        private double currentWeekHigh;
        private double currentWeekLow;
        private double priorWeekOpen;
        private double priorWeekHigh;
        private double priorWeekLow;
        private double currentSessionOpen;
        private double currentSessionHigh;
        private double currentSessionLow;
        private double priorSessionOpen;
        private double priorSessionHigh;
        private double priorSessionLow;
        private double sessionCumPv;
        private double sessionCumVol;
        private double weeklyCumPv;
        private double weeklyCumVol;
        private int developingBarIndex = -1;
        private double developingBarPv;
        private double developingBarVolume;
        private double developingBarBucketPrice = double.NaN;
        private readonly SortedDictionary<double, double> sessionProfile = new SortedDictionary<double, double>();
        private readonly SortedDictionary<double, double> weeklyProfile = new SortedDictionary<double, double>();
        private double sessionPoc = double.NaN;
        private double sessionVah = double.NaN;
        private double sessionVal = double.NaN;
        private double weeklyPoc = double.NaN;
        private double weeklyVah = double.NaN;
        private double weeklyVal = double.NaN;

        private SharpDX.Direct2D1.SolidColorBrush dxAskWall;
        private SharpDX.Direct2D1.SolidColorBrush dxBidWall;
        private SharpDX.Direct2D1.SolidColorBrush dxStaleWall;
        private SharpDX.Direct2D1.SolidColorBrush dxHudText;
        private SharpDX.Direct2D1.SolidColorBrush dxHudBackground;
        private SharpDX.Direct2D1.SolidColorBrush dxBullShade;
        private SharpDX.Direct2D1.SolidColorBrush dxBearShade;
        private TextFormat hudFormat;
        private TextFormat wallFormat;
        private TextFormat levelLabelFormat;
        private StrokeStyle wallStrokeStyle;
        private StrokeStyle referenceStrokeStyle;
        private StrokeStyle profileStrokeStyle;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "LiquidityPulse real-time Level 2 order-flow foundation.";
                Name = "LiquidityPulse";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = false;

                UseAdaptiveThreshold = true;
                WallMultiplier = 3.0;
                AdaptiveWindowMinutes = 5;
                MinLotFloor = 8;
                MaxWallsPerSide = 5;
                WallsFromLastBar = true;
                ScaleWallsBySize = true;
                MinWallLabelSpacing = 12;
                WallMinAgeSeconds = 3;
                WallGraceMilliseconds = 1000;
                FeedStaleSeconds = 5;
                ShowWalls = true;
                ShowWallLabels = true;
                WallMinWidth = 1;
                WallMaxWidth = 6;
                WallDashStyle = DashStyleHelper.Solid;
                AskWallColor = System.Windows.Media.Brushes.Crimson;
                BidWallColor = System.Windows.Media.Brushes.LimeGreen;
                StaleWallColor = System.Windows.Media.Brushes.Gray;
                AskWallOpacity = 90;
                BidWallOpacity = 90;
                StaleWallOpacity = 65;
                WallLabelFontSize = 10;

                ShowHud = true;
                HudCorner = LiquidityPulseHudCorner.BottomLeft;
                HudFontSize = 12;
                HudTextColor = System.Windows.Media.Brushes.WhiteSmoke;
                HudBackgroundColor = System.Windows.Media.Brushes.Black;
                HudBackgroundOpacity = 65;
                HudTextOpacity = 100;

                EnableBiasShading = false;
                BullShadeColor = System.Windows.Media.Brushes.ForestGreen;
                BearShadeColor = System.Windows.Media.Brushes.Crimson;
                ShadeOpacity = 5;
                ShadeMode = LiquidityPulseShadeMode.FullHeight;
                HtfBarsPeriodType = BarsPeriodType.Minute;
                HtfBarsValue = 60;
                ConfirmBarsPeriodType = BarsPeriodType.Tick;
                ConfirmBarsValue = 80;
                SwingStrength = 3;

                ShowPreviousDayLevels = true;
                ShowPreviousDayOpenLevel = true;
                ShowPreviousDayHighLevel = true;
                ShowPreviousDayLowLevel = true;
                PreviousDayOpenColor = System.Windows.Media.Brushes.Gold;
                PreviousDayHighColor = System.Windows.Media.Brushes.LimeGreen;
                PreviousDayLowColor = System.Windows.Media.Brushes.OrangeRed;
                ShowPreviousWeekLevels = true;
                ShowPreviousWeekOpenLevel = true;
                ShowPreviousWeekHighLevel = true;
                ShowPreviousWeekLowLevel = true;
                PreviousWeekOpenColor = System.Windows.Media.Brushes.Goldenrod;
                PreviousWeekHighColor = System.Windows.Media.Brushes.DeepSkyBlue;
                PreviousWeekLowColor = System.Windows.Media.Brushes.MediumVioletRed;
                ShowPreviousSessionLevels = true;
                ShowPreviousSessionOpenLevel = true;
                ShowPreviousSessionHighLevel = true;
                ShowPreviousSessionLowLevel = true;
                PreviousSessionOpenColor = System.Windows.Media.Brushes.Khaki;
                PreviousSessionHighColor = System.Windows.Media.Brushes.LightGreen;
                PreviousSessionLowColor = System.Windows.Media.Brushes.LightCoral;
                ShowCurrentSessionLevels = true;
                ShowCurrentSessionOpenLevel = true;
                ShowCurrentSessionHighLevel = true;
                ShowCurrentSessionLowLevel = true;
                CurrentSessionOpenColor = System.Windows.Media.Brushes.WhiteSmoke;
                CurrentSessionHighColor = System.Windows.Media.Brushes.Cyan;
                CurrentSessionLowColor = System.Windows.Media.Brushes.Magenta;
                ReferenceLineWidth = 1;
                ReferenceLineStyle = DashStyleHelper.Dash;
                ReferenceOpacity = 100;
                ShowLevelLabels = true;
                LevelLabelFontSize = 10;

                ShowSessionProfileLevels = true;
                ShowWeeklyProfileLevels = true;
                ShowSessionPoc = true;
                ShowSessionVah = true;
                ShowSessionVal = true;
                ShowWeeklyPoc = true;
                ShowWeeklyVah = true;
                ShowWeeklyVal = true;
                ShowSessionVwap = true;
                ShowWeeklyVwap = true;
                ValueAreaPercent = 70;
                ProfileLineWidth = 2;
                ProfileLineStyle = DashStyleHelper.Solid;
                ProfileOpacity = 100;
                SessionPocColor = System.Windows.Media.Brushes.Gold;
                SessionVahColor = System.Windows.Media.Brushes.LimeGreen;
                SessionValColor = System.Windows.Media.Brushes.OrangeRed;
                SessionVwapColor = System.Windows.Media.Brushes.DodgerBlue;
                WeeklyPocColor = System.Windows.Media.Brushes.DarkGoldenrod;
                WeeklyVahColor = System.Windows.Media.Brushes.GreenYellow;
                WeeklyValColor = System.Windows.Media.Brushes.IndianRed;
                WeeklyVwapColor = System.Windows.Media.Brushes.DeepSkyBlue;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(HtfBarsPeriodType, HtfBarsValue);
                AddDataSeries(ConfirmBarsPeriodType, ConfirmBarsValue);
            }
            else if (State == State.DataLoaded)
            {
                swingHtf = Swing(BarsArray[1], SwingStrength);
                swingConfirm = Swing(BarsArray[2], SwingStrength);
                biasSeries = new Series<int>(this, MaximumBarsLookBack.Infinite);
                sessionVwapSeries = new Series<double>(this, MaximumBarsLookBack.Infinite);
                weeklyVwapSeries = new Series<double>(this, MaximumBarsLookBack.Infinite);
                sessionIterator = new SessionIterator(Bars);
            }
            else if (State == State.Realtime)
            {
                trailingDetectionTimer = new System.Threading.Timer(OnTrailingDetection, null,
                    Timeout.Infinite, Timeout.Infinite);
                feedRefreshTimer = new System.Threading.Timer(OnFeedRefreshTimer, null, 1000, 1000);
            }
            else if (State == State.Terminated)
            {
                if (feedRefreshTimer != null)
                {
                    feedRefreshTimer.Dispose();
                    feedRefreshTimer = null;
                }
                lock (bookLock)
                {
                    if (trailingDetectionTimer != null)
                    {
                        trailingDetectionTimer.Dispose();
                        trailingDetectionTimer = null;
                    }
                }
                DisposeDxResources();
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;

            UpdateReferenceLevelsAndProfiles();
            UpdateBias();

            if ((CurrentBar == 0 && IsFirstTickOfBar)
                || (Bars.IsFirstBarOfSession && IsFirstTickOfBar
                    && CurrentBar != lastSessionIteratorBarIndex))
            {
                sessionIterator.GetNextSession(Time[0], false);
                DateTime end = sessionIterator.ActualSessionEnd;
                lock (bookLock)
                    cachedSessionEndEastern = TimeZoneInfo.ConvertTime(end, TimeZoneInfo.Local, EasternTz);
                lock (bookLock)
                {
                    sessionAggBuy = 0;
                    sessionAggSell = 0;
                }
                lastSessionIteratorBarIndex = CurrentBar;
            }
        }

        private void UpdateBias()
        {
            if (biasSeries == null)
                return;

            if (CurrentBar < 1 || CurrentBars.Length < 3 || CurrentBars[2] < SwingStrength)
            {
                biasSeries[0] = 0;
                return;
            }

            double htfLow = CurrentBars[1] >= SwingStrength ? swingHtf.SwingLow[0] : double.NaN;
            double htfHigh = CurrentBars[1] >= SwingStrength ? swingHtf.SwingHigh[0] : double.NaN;
            double confirmLow = swingConfirm.SwingLow[0];
            double confirmHigh = swingConfirm.SwingHigh[0];
            bool hasHtfLow = !double.IsNaN(htfLow) && htfLow > 0;
            bool hasHtfHigh = !double.IsNaN(htfHigh) && htfHigh > 0;
            bool hasConfirmLow = !double.IsNaN(confirmLow) && confirmLow > 0;
            bool hasConfirmHigh = !double.IsNaN(confirmHigh) && confirmHigh > 0;

            bool bull = hasConfirmLow && Close[0] > confirmLow
                && (!hasHtfLow || Close[0] > htfLow);
            bool bear = hasConfirmHigh && Close[0] < confirmHigh
                && (!hasHtfHigh || Close[0] < htfHigh);
            biasSeries[0] = bull ? 1 : (bear ? -1 : 0);
        }

        private void UpdateReferenceLevelsAndProfiles()
        {
            DateTime barTime = Time[0];
            DateTime barDate = barTime.Date;
            DateTime weekStart = GetWeekStart(barDate);
            bool isFirstBar = CurrentBar == 0 && IsFirstTickOfBar;
            bool isNewSession = isFirstBar
                || (Bars.IsFirstBarOfSession && CurrentBar != lastSessionBarIndex);
            if (isNewSession)
                lastSessionBarIndex = CurrentBar;
            bool isNewDay = isFirstBar || barDate != currentDayDate;
            bool isNewWeek = isFirstBar || weekStart != currentWeekStart;

            if (isNewWeek)
            {
                if (!isFirstBar)
                {
                    priorWeekOpen = currentWeekOpen;
                    priorWeekHigh = currentWeekHigh;
                    priorWeekLow = currentWeekLow;
                }
                currentWeekStart = weekStart;
                currentWeekOpen = Open[0];
                currentWeekHigh = High[0];
                currentWeekLow = Low[0];
                weeklyCumPv = 0;
                weeklyCumVol = 0;
                weeklyProfile.Clear();
                weeklyPoc = weeklyVah = weeklyVal = double.NaN;
            }
            else
            {
                currentWeekHigh = Math.Max(currentWeekHigh, High[0]);
                currentWeekLow = Math.Min(currentWeekLow, Low[0]);
            }

            if (isNewDay)
            {
                if (!isFirstBar)
                {
                    priorDayOpen = currentDayOpen;
                    priorDayHigh = currentDayHigh;
                    priorDayLow = currentDayLow;
                }
                currentDayDate = barDate;
                currentDayOpen = Open[0];
                currentDayHigh = High[0];
                currentDayLow = Low[0];
            }
            else
            {
                currentDayHigh = Math.Max(currentDayHigh, High[0]);
                currentDayLow = Math.Min(currentDayLow, Low[0]);
            }

            if (isNewSession)
            {
                if (!isFirstBar)
                {
                    priorSessionOpen = currentSessionOpen;
                    priorSessionHigh = currentSessionHigh;
                    priorSessionLow = currentSessionLow;
                }
                currentSessionOpen = Open[0];
                currentSessionHigh = High[0];
                currentSessionLow = Low[0];
                sessionCumPv = sessionCumVol = 0;
                sessionProfile.Clear();
                sessionPoc = sessionVah = sessionVal = double.NaN;
            }
            else
            {
                currentSessionHigh = Math.Max(currentSessionHigh, High[0]);
                currentSessionLow = Math.Min(currentSessionLow, Low[0]);
            }

            double barVolume = Math.Max(0d, Volume[0]);
            double typicalPrice = (High[0] + Low[0] + Close[0]) / 3.0;
            double bucketPrice = Instrument.MasterInstrument.RoundToTickSize(typicalPrice);
            double barPv = typicalPrice * barVolume;
            if (CurrentBar != developingBarIndex)
            {
                developingBarIndex = CurrentBar;
                developingBarPv = developingBarVolume = 0;
                developingBarBucketPrice = double.NaN;
            }
            else
            {
                sessionCumPv -= developingBarPv;
                sessionCumVol -= developingBarVolume;
                weeklyCumPv -= developingBarPv;
                weeklyCumVol -= developingBarVolume;
                if (!double.IsNaN(developingBarBucketPrice))
                {
                    RemoveProfileVolume(sessionProfile, developingBarBucketPrice, developingBarVolume);
                    RemoveProfileVolume(weeklyProfile, developingBarBucketPrice, developingBarVolume);
                }
            }

            sessionCumPv += barPv;
            sessionCumVol += barVolume;
            weeklyCumPv += barPv;
            weeklyCumVol += barVolume;
            sessionVwapSeries[0] = sessionCumVol > 0 ? sessionCumPv / sessionCumVol : double.NaN;
            weeklyVwapSeries[0] = weeklyCumVol > 0 ? weeklyCumPv / weeklyCumVol : double.NaN;
            AddProfileVolume(sessionProfile, bucketPrice, barVolume);
            AddProfileVolume(weeklyProfile, bucketPrice, barVolume);
            developingBarPv = barPv;
            developingBarVolume = barVolume;
            developingBarBucketPrice = bucketPrice;

            if (State == State.Historical || IsFirstTickOfBar)
            {
                ComputeValueArea(sessionProfile, out sessionPoc, out sessionVah, out sessionVal);
                ComputeValueArea(weeklyProfile, out weeklyPoc, out weeklyVah, out weeklyVal);
            }
        }

        private void AddProfileVolume(SortedDictionary<double, double> profile, double price, double volume)
        {
            if (volume <= 0)
                return;
            double existing;
            profile.TryGetValue(price, out existing);
            profile[price] = existing + volume;
        }

        private void RemoveProfileVolume(SortedDictionary<double, double> profile, double price, double volume)
        {
            double existing;
            if (volume <= 0 || !profile.TryGetValue(price, out existing))
                return;
            if (existing > volume)
                profile[price] = existing - volume;
            else
                profile.Remove(price);
        }

        private void ComputeValueArea(SortedDictionary<double, double> profile, out double poc, out double vah, out double val)
        {
            poc = vah = val = double.NaN;
            if (profile == null || profile.Count == 0)
                return;
            List<double> prices = new List<double>(profile.Keys);
            List<double> volumes = new List<double>(profile.Values);
            int pocIndex = 0;
            double maxVolume = 0;
            double totalVolume = 0;
            for (int i = 0; i < volumes.Count; i++)
            {
                totalVolume += volumes[i];
                if (volumes[i] > maxVolume)
                {
                    maxVolume = volumes[i];
                    pocIndex = i;
                }
            }
            if (totalVolume <= 0)
                return;
            double target = totalVolume * (ValueAreaPercent / 100.0);
            double accumulated = volumes[pocIndex];
            int lowIndex = pocIndex;
            int highIndex = pocIndex;
            while (accumulated < target && (lowIndex > 0 || highIndex < prices.Count - 1))
            {
                double lower = lowIndex > 0 ? volumes[lowIndex - 1] : -1;
                double upper = highIndex < prices.Count - 1 ? volumes[highIndex + 1] : -1;
                if (upper >= lower && highIndex < prices.Count - 1)
                    accumulated += volumes[++highIndex];
                else if (lowIndex > 0)
                    accumulated += volumes[--lowIndex];
                else
                    accumulated += volumes[++highIndex];
            }
            poc = prices[pocIndex];
            val = prices[lowIndex];
            vah = prices[highIndex];
        }

        private DateTime GetWeekStart(DateTime date)
        {
            int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
            return date.AddDays(-diff).Date;
        }

        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (e == null)
                return;

            SetEventTime(e.Time, true);
            lock (bookLock)
            {
                DateTime now = AdvanceClockLocked(FeedNow());
                if (e.IsReset)
                    ResetBookLocked();

                bool isAsk = e.MarketDataType == MarketDataType.Ask;
                if (!e.IsReset && (isAsk || e.MarketDataType == MarketDataType.Bid))
                {
                    List<DepthRow> rows = isAsk ? askRows : bidRows;
                    int position = e.Position;
                    if (e.Operation == Operation.Add)
                    {
                        if (position >= 0 && position <= rows.Count)
                        {
                            rows.Insert(position, new DepthRow
                            {
                                Price = Instrument.MasterInstrument.RoundToTickSize(e.Price),
                                Size = Math.Max(0L, e.Volume)
                            });
                        }
                    }
                    else if (e.Operation == Operation.Update)
                    {
                        if (position >= 0 && position < rows.Count)
                        {
                            rows[position].Price = Instrument.MasterInstrument.RoundToTickSize(e.Price);
                            rows[position].Size = Math.Max(0L, e.Volume);
                        }
                    }
                    else if (e.Operation == Operation.Remove && position >= 0 && position < rows.Count)
                    {
                        rows.RemoveAt(position);
                    }
                }
                RunDetectionLocked(now, false);
            }
            ThrottledForceRefresh();
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e == null)
                return;

            SetEventTime(e.Time, false);
            lock (bookLock)
            {
                DateTime now = AdvanceClockLocked(FeedNow());
                if (e.MarketDataType == MarketDataType.Bid)
                    lastBid = e.Price;
                else if (e.MarketDataType == MarketDataType.Ask)
                    lastAsk = e.Price;
                else if (e.MarketDataType == MarketDataType.Last && e.Volume > 0)
                {
                    double price = Instrument.MasterInstrument.RoundToTickSize(e.Price);
                    double bid = e.Bid > 0 ? e.Bid : lastBid;
                    double ask = e.Ask > 0 ? e.Ask : lastAsk;
                    bool aggressiveBuy = !double.IsNaN(ask) && price >= ask;
                    bool aggressiveSell = !double.IsNaN(bid) && price <= bid;
                    if (aggressiveBuy)
                    {
                        sessionAggBuy += e.Volume;
                        WallState wall;
                        if (askWalls.TryGetValue(price, out wall) && wall.Active)
                            wall.TradedBuy += e.Volume;
                    }
                    else if (aggressiveSell)
                    {
                        sessionAggSell += e.Volume;
                        WallState wall;
                        if (bidWalls.TryGetValue(price, out wall) && wall.Active)
                            wall.TradedSell += e.Volume;
                    }
                }

                RunDetectionLocked(now, false);
            }
            ThrottledForceRefresh();
        }

        private void OnFeedRefreshTimer(object state)
        {
            if (State != State.Realtime)
                return;
            // Live feeds advance the event clock by elapsed wall time so grace expiry and
            // maintenance/early-close boundaries are handled without new events; Playback stays on event time.
            if (Connection.PlaybackConnection == null && Interlocked.Read(ref eventTimeBinary) != 0)
            {
                lock (bookLock)
                    RunDetectionLocked(AdvanceClockLocked(TimerNow()), false);
            }
            ThrottledForceRefresh();
        }

        private void OnTrailingDetection(object state)
        {
            if (State != State.Realtime)
                return;
            lock (bookLock)
            {
                if (!pendingDetection)
                    return;
                RunDetectionLocked(AdvanceClockLocked(TimerNow()), true);
            }
            ThrottledForceRefresh();
        }

        private DateTime TimerNow()
        {
            DateTime now = FeedNow();
            long arrival = Interlocked.Read(ref lastEventArrivalUtcBinary);
            if (Connection.PlaybackConnection != null || arrival == 0)
                return now;
            TimeSpan elapsed = DateTime.UtcNow - DateTime.FromBinary(arrival);
            return elapsed > TimeSpan.Zero ? now + elapsed : now;
        }

        private DateTime AdvanceClockLocked(DateTime now)
        {
            if (lastClockTime != DateTime.MinValue)
            {
                if (now < lastClockTime)
                {
                    // Absorb small jitter between extrapolated timer time and event timestamps.
                    if ((lastClockTime - now).TotalSeconds <= 2)
                        now = lastClockTime;
                }
                else if (CrossedSessionCloseLocked(lastClockTime, now))
                {
                    ResetBookLocked();
                }
            }
            lastClockTime = now;
            return now;
        }

        private bool CrossedSessionCloseLocked(DateTime previous, DateTime now)
        {
            DateTime previousEt = TimeZoneInfo.ConvertTime(previous, TimeZoneInfo.Local, EasternTz);
            DateTime nowEt = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local, EasternTz);
            if ((nowEt - previousEt).TotalDays > 7)
                return true;
            for (DateTime day = previousEt.Date; day <= nowEt.Date; day = day.AddDays(1))
            {
                DateTime closeStart = day.AddHours(17);
                if (cachedSessionEndEastern != DateTime.MinValue
                    && cachedSessionEndEastern.Date == day
                    && cachedSessionEndEastern < closeStart)
                    closeStart = cachedSessionEndEastern;
                if (previousEt < closeStart && nowEt >= closeStart)
                    return true;
            }
            return false;
        }

        private void ResetBookLocked()
        {
            askRows.Clear();
            bidRows.Clear();
            askWalls.Clear();
            bidWalls.Clear();
            pendingDetection = false;
        }

        private void RunDetectionLocked(DateTime now, bool force)
        {
            SessionPhase phase = GetSessionPhase(now);
            if (!phaseInitialized || phase != lastPhase)
            {
                phaseInitialized = true;
                lastPhase = phase;
                sizeSamples.Clear();
                adaptiveBaseline = double.NaN;
                lastSampleTime = DateTime.MinValue;
                lastDetectionTime = DateTime.MinValue;
                if (phase == SessionPhase.EthOvernight)
                {
                    sessionAggBuy = 0;
                    sessionAggSell = 0;
                }
            }

            if (phase == SessionPhase.PostClose)
            {
                // The book is reset once on entering the close; depth received during maintenance
                // is kept so positional updates at reopening still apply, but walls are suppressed.
                askWalls.Clear();
                bidWalls.Clear();
                pendingDetection = false;
                return;
            }

            double sinceLast = lastDetectionTime == DateTime.MinValue
                ? double.MaxValue
                : (now - lastDetectionTime).TotalMilliseconds;
            if (force || sinceLast >= 150)
            {
                lastDetectionTime = now;
                pendingDetection = false;
                UpdateAdaptiveBaselineLocked(now);
                RefreshWallsLocked(askRows, askWalls, true, now);
                RefreshWallsLocked(bidRows, bidWalls, false, now);
            }
            else if (!pendingDetection)
            {
                pendingDetection = true;
                if (trailingDetectionTimer != null)
                {
                    int delay = (int)Math.Max(1, Math.Min(150, 150 - Math.Max(0, sinceLast)));
                    trailingDetectionTimer.Change(delay, Timeout.Infinite);
                }
            }
        }

        private void SetEventTime(DateTime time, bool isDepth)
        {
            Interlocked.Exchange(ref eventTimeBinary, time.ToBinary());
            Interlocked.Exchange(ref lastEventArrivalUtcBinary, DateTime.UtcNow.ToBinary());
            if (isDepth)
            {
                Interlocked.Exchange(ref lastDepthEventTimeBinary, time.ToBinary());
                Interlocked.Exchange(ref lastDepthArrivalUtcBinary, DateTime.UtcNow.ToBinary());
            }
        }

        private DateTime FeedNow()
        {
            long binary = Interlocked.Read(ref eventTimeBinary);
            return binary == 0 ? DateTime.Now : DateTime.FromBinary(binary);
        }

        private DateTime DetectionNowLocked(DateTime fallback)
        {
            return lastClockTime == DateTime.MinValue ? fallback : lastClockTime;
        }

        private DateTime GetLastDepthTime()
        {
            long binary = Interlocked.Read(ref lastDepthEventTimeBinary);
            return binary == 0 ? DateTime.MinValue : DateTime.FromBinary(binary);
        }

        private bool IsBookStale()
        {
            if (State == State.Realtime && Connection.PlaybackConnection == null)
            {
                long binary = Interlocked.Read(ref lastDepthArrivalUtcBinary);
                return binary == 0
                    || (DateTime.UtcNow - DateTime.FromBinary(binary)).TotalSeconds > FeedStaleSeconds;
            }
            DateTime lastDepth = GetLastDepthTime();
            return lastDepth == DateTime.MinValue
                || (FeedNow() - lastDepth).TotalSeconds > FeedStaleSeconds;
        }

        private SessionPhase GetSessionPhase(DateTime eventTime)
        {
            DateTime et = TimeZoneInfo.ConvertTime(eventTime, TimeZoneInfo.Local, EasternTz);
            TimeSpan time = et.TimeOfDay;
            lock (bookLock)
            {
                if (cachedSessionEndEastern != DateTime.MinValue
                    && cachedSessionEndEastern.Date == et.Date
                    && cachedSessionEndEastern.TimeOfDay < new TimeSpan(17, 0, 0)
                    && et >= cachedSessionEndEastern
                    && time < new TimeSpan(18, 0, 0))
                    return SessionPhase.PostClose;
            }
            if (time >= new TimeSpan(9, 30, 0) && time < new TimeSpan(10, 0, 0))
                return SessionPhase.RthOpen;
            if (time >= new TimeSpan(10, 0, 0) && time < new TimeSpan(16, 0, 0))
                return SessionPhase.Rth;
            if (time >= new TimeSpan(16, 0, 0) && time < new TimeSpan(17, 0, 0))
                return SessionPhase.EthPostRth;
            if (time >= new TimeSpan(17, 0, 0) && time < new TimeSpan(18, 0, 0))
                return SessionPhase.PostClose;
            if (time >= new TimeSpan(3, 0, 0) && time < new TimeSpan(9, 30, 0))
                return SessionPhase.EthLondon;
            return SessionPhase.EthOvernight;
        }

        private void UpdateAdaptiveBaselineLocked(DateTime now)
        {
            if (!UseAdaptiveThreshold || (lastSampleTime != DateTime.MinValue
                && (now - lastSampleTime).TotalSeconds < 2))
                return;
            lastSampleTime = now;
            double total = 0;
            int count = 0;
            for (int i = 0; i < askRows.Count; i++)
            {
                total += askRows[i].Size;
                count++;
            }
            for (int i = 0; i < bidRows.Count; i++)
            {
                total += bidRows[i].Size;
                count++;
            }
            if (count > 0)
                sizeSamples.Enqueue(new SizeSample { Time = now, Average = total / count });
            while (sizeSamples.Count > 0
                && (now - sizeSamples.Peek().Time).TotalMinutes > AdaptiveWindowMinutes)
                sizeSamples.Dequeue();
            if (sizeSamples.Count == 0)
            {
                adaptiveBaseline = double.NaN;
                return;
            }
            List<double> values = new List<double>();
            foreach (SizeSample sample in sizeSamples)
                values.Add(sample.Average);
            values.Sort();
            int middle = values.Count / 2;
            adaptiveBaseline = values.Count % 2 == 0
                ? (values[middle - 1] + values[middle]) / 2.0
                : values[middle];
        }

        private long GetWallThresholdLocked()
        {
            if (!UseAdaptiveThreshold || double.IsNaN(adaptiveBaseline))
                return MinLotFloor;
            return Math.Max(MinLotFloor, (long)Math.Ceiling(adaptiveBaseline * WallMultiplier));
        }

        private void RefreshWallsLocked(List<DepthRow> rows, Dictionary<double, WallState> walls,
            bool isAsk, DateTime now)
        {
            long threshold = GetWallThresholdLocked();
            HashSet<double> seen = new HashSet<double>();
            for (int i = 0; i < rows.Count; i++)
            {
                DepthRow row = rows[i];
                WallState wall;
                bool active = walls.TryGetValue(row.Price, out wall) && wall.Active;
                if (active && wall.LostQualification
                    && (now - wall.LastSeenAboveThreshold).TotalMilliseconds >= WallGraceMilliseconds)
                {
                    // Qualification was observed lost and grace elapsed before it returned: end that lifecycle.
                    wall.Active = false;
                    active = false;
                }
                if (active)
                    wall.LastSize = row.Size;
                if (row.Size < (active ? 0.8 * threshold : threshold))
                    continue;
                seen.Add(row.Price);
                if (!active)
                {
                    wall = new WallState
                    {
                        Price = row.Price,
                        IsAsk = isAsk,
                        FirstSeen = now,
                        Active = true
                    };
                    walls[row.Price] = wall;
                }
                wall.LostQualification = false;
                wall.LastSeenAboveThreshold = now;
                wall.LastSize = row.Size;
                wall.PeakSize = Math.Max(wall.PeakSize, row.Size);
            }

            foreach (WallState wall in walls.Values)
            {
                if (wall.Active && !seen.Contains(wall.Price))
                {
                    wall.LostQualification = true;
                    if ((now - wall.LastSeenAboveThreshold).TotalMilliseconds >= WallGraceMilliseconds)
                    {
                        wall.Active = false;
                        // v0.2 hook: classify inactive walls as pulled or filled after the grace window.
                    }
                }
            }
        }

        private void ThrottledForceRefresh()
        {
            long now = Stopwatch.GetTimestamp();
            long previous = Interlocked.Read(ref lastForceRefreshTimestamp);
            if (previous != 0 && (now - previous) * 1000.0 / Stopwatch.Frequency < 75)
                return;
            if (Interlocked.CompareExchange(ref lastForceRefreshTimestamp, now, previous) == previous)
                ForceRefresh();
        }

        public override void OnRenderTargetChanged()
        {
            DisposeDxResources();
            if (RenderTarget == null)
                return;
            try
            {
                dxAskWall = CreateDxBrush(AskWallColor, AskWallOpacity);
                dxBidWall = CreateDxBrush(BidWallColor, BidWallOpacity);
                dxStaleWall = CreateDxBrush(StaleWallColor, StaleWallOpacity);
                dxHudText = CreateDxBrush(HudTextColor, HudTextOpacity);
                dxHudBackground = CreateDxBrush(HudBackgroundColor, HudBackgroundOpacity);
                dxBullShade = CreateDxBrush(BullShadeColor, ShadeOpacity);
                dxBearShade = CreateDxBrush(BearShadeColor, ShadeOpacity);

                AddLevelBrush("pdOpen", PreviousDayOpenColor, ReferenceOpacity);
                AddLevelBrush("pdHigh", PreviousDayHighColor, ReferenceOpacity);
                AddLevelBrush("pdLow", PreviousDayLowColor, ReferenceOpacity);
                AddLevelBrush("pwOpen", PreviousWeekOpenColor, ReferenceOpacity);
                AddLevelBrush("pwHigh", PreviousWeekHighColor, ReferenceOpacity);
                AddLevelBrush("pwLow", PreviousWeekLowColor, ReferenceOpacity);
                AddLevelBrush("psOpen", PreviousSessionOpenColor, ReferenceOpacity);
                AddLevelBrush("psHigh", PreviousSessionHighColor, ReferenceOpacity);
                AddLevelBrush("psLow", PreviousSessionLowColor, ReferenceOpacity);
                AddLevelBrush("csOpen", CurrentSessionOpenColor, ReferenceOpacity);
                AddLevelBrush("csHigh", CurrentSessionHighColor, ReferenceOpacity);
                AddLevelBrush("csLow", CurrentSessionLowColor, ReferenceOpacity);
                AddLevelBrush("sessionPoc", SessionPocColor, ProfileOpacity);
                AddLevelBrush("sessionVah", SessionVahColor, ProfileOpacity);
                AddLevelBrush("sessionVal", SessionValColor, ProfileOpacity);
                AddLevelBrush("sessionVwap", SessionVwapColor, ProfileOpacity);
                AddLevelBrush("weeklyPoc", WeeklyPocColor, ProfileOpacity);
                AddLevelBrush("weeklyVah", WeeklyVahColor, ProfileOpacity);
                AddLevelBrush("weeklyVal", WeeklyValColor, ProfileOpacity);
                AddLevelBrush("weeklyVwap", WeeklyVwapColor, ProfileOpacity);

                hudFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI",
                    FontWeight.SemiBold, FontStyle.Normal, HudFontSize);
                wallFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI",
                    FontWeight.Bold, FontStyle.Normal, WallLabelFontSize);
                levelLabelFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI",
                    FontWeight.Normal, FontStyle.Normal, LevelLabelFontSize);
                wallStrokeStyle = CreateStrokeStyle(WallDashStyle);
                referenceStrokeStyle = CreateStrokeStyle(ReferenceLineStyle);
                profileStrokeStyle = CreateStrokeStyle(ProfileLineStyle);
            }
            catch
            {
                DisposeDxResources();
            }
        }

        private void AddLevelBrush(string key, System.Windows.Media.Brush color, int opacity)
        {
            levelBrushes[key] = CreateDxBrush(color, opacity);
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (chartControl == null || chartScale == null || ChartBars == null
                || RenderTarget == null || CurrentBar < 1)
                return;

            int firstBar = ChartBars.FromIndex;
            int lastBar = ChartBars.ToIndex;
            if (firstBar < 0 || lastBar < firstBar)
                return;
            int renderLastBar = Math.Min(lastBar, CurrentBar);

            if (EnableBiasShading && biasSeries != null)
                RenderBias(chartControl, firstBar, renderLastBar);
            RenderReferenceLevels(chartControl, chartScale);
            RenderProfileLevels(chartControl, chartScale, firstBar, renderLastBar);
            if (ShowWalls)
                RenderWalls(chartScale);
            if (ShowHud)
                RenderHud();
        }

        private void RenderBias(ChartControl chartControl, int firstBar, int lastBar)
        {
            if (dxBullShade == null || dxBearShade == null)
                return;
            float bandHeight = Math.Max(12f, ChartPanel.H * 0.08f);
            for (int bar = firstBar; bar <= lastBar; bar++)
            {
                int bias = biasSeries.GetValueAt(bar);
                if (bias == 0)
                    continue;
                float x = chartControl.GetXByBarIndex(ChartBars, bar);
                float y = ChartPanel.Y;
                float height = ChartPanel.H;
                if (ShadeMode == LiquidityPulseShadeMode.TopBand)
                    height = bandHeight;
                else if (ShadeMode == LiquidityPulseShadeMode.BottomBand)
                {
                    y += ChartPanel.H - bandHeight;
                    height = bandHeight;
                }
                RenderTarget.FillRectangle(new RectangleF(x - (float)chartControl.BarWidth * 0.5f,
                    y, (float)chartControl.BarWidth, height), bias > 0 ? dxBullShade : dxBearShade);
            }
        }

        private void RenderWalls(ChartScale chartScale)
        {
            if (dxAskWall == null || dxBidWall == null)
                return;
            DateTime now = FeedNow();
            bool stale;
            List<WallSnapshot> snapshot = new List<WallSnapshot>();
            long threshold;
            lock (bookLock)
            {
                now = DetectionNowLocked(now);
                stale = IsBookStale();
                threshold = GetWallThresholdLocked();
                CopyActiveWalls(askWalls, snapshot);
                CopyActiveWalls(bidWalls, snapshot);
            }

            List<WallSnapshot> visible = new List<WallSnapshot>();
            for (int i = 0; i < snapshot.Count; i++)
            {
                WallSnapshot wall = snapshot[i];
                if (wall.Price < chartScale.MinValue || wall.Price > chartScale.MaxValue
                    || (now - wall.FirstSeen).TotalSeconds < WallMinAgeSeconds)
                    continue;
                visible.Add(wall);
            }
            visible.Sort(delegate(WallSnapshot a, WallSnapshot b) { return b.Size.CompareTo(a.Size); });

            float right = ChartPanel.X + ChartPanel.W;
            float start = WallsFromLastBar && ChartBars.ToIndex >= 0
                ? chartControlXByLastBar(right)
                : ChartPanel.X;
            if (right - start < 16f)
                start = ChartPanel.X;

            List<float> labelYs = new List<float>();
            int askCount = 0;
            int bidCount = 0;
            for (int i = 0; i < visible.Count; i++)
            {
                WallSnapshot wall = visible[i];
                if (wall.IsAsk ? askCount >= MaxWallsPerSide : bidCount >= MaxWallsPerSide)
                    continue;
                if (wall.IsAsk) askCount++; else bidCount++;

                float y = chartScale.GetYByValue(wall.Price);
                float width = WallMinWidth;
                double sizeScale = Math.Min(1.0, (double)wall.Size / Math.Max(1, threshold * 3.0));
                if (ScaleWallsBySize)
                {
                    width += (float)(sizeScale * (WallMaxWidth - WallMinWidth));
                }
                width = Math.Max(WallMinWidth, Math.Min(WallMaxWidth, width));
                SharpDX.Direct2D1.SolidColorBrush brush = stale ? dxStaleWall
                    : (wall.IsAsk ? dxAskWall : dxBidWall);
                if (brush == null)
                    continue;
                brush.Opacity = ScaleWallsBySize
                    ? Math.Max(0f, Math.Min(1f, 0.4f + 0.6f * (float)sizeScale)) : 1f;
                RenderTarget.DrawLine(new Vector2(start, y), new Vector2(right, y), brush,
                    width, wallStrokeStyle);

                if (ShowWallLabels && wallFormat != null)
                {
                    string label = string.Format("{0} {1} ({2:F1}×) {3}",
                        wall.IsAsk ? "ASK" : "BID",
                        wall.Size,
                        GetWallMultiple(wall.Size),
                        FormatAge(now - wall.FirstSeen));
                    float labelY = FindWallLabelY(y, labelYs, MinWallLabelSpacing);
                    labelYs.Add(labelY);
                    using (TextLayout layout = new TextLayout(Core.Globals.DirectWriteFactory,
                        label, wallFormat, Math.Max(150f, right - start), WallLabelFontSize + 4f))
                    {
                        RenderTarget.DrawTextLayout(new Vector2(start + 3f, labelY), layout, brush);
                    }
                }
                brush.Opacity = 1f;
            }
        }

        private double GetWallMultiple(long size)
        {
            lock (bookLock)
                return adaptiveBaseline > 0 ? size / adaptiveBaseline : 0;
        }

        private void CopyActiveWalls(Dictionary<double, WallState> walls, List<WallSnapshot> result)
        {
            foreach (WallState wall in walls.Values)
            {
                if (!wall.Active)
                    continue;
                result.Add(new WallSnapshot
                {
                    Price = wall.Price,
                    IsAsk = wall.IsAsk,
                    Size = wall.LastSize,
                    PeakSize = wall.PeakSize,
                    TradedBuy = wall.TradedBuy,
                    TradedSell = wall.TradedSell,
                    FirstSeen = wall.FirstSeen
                });
            }
        }

        private float FindWallLabelY(float y, List<float> usedYs, int spacing)
        {
            float result = y - WallLabelFontSize - 2f;
            for (int i = 0; i < usedYs.Count; i++)
            {
                if (Math.Abs(result - usedYs[i]) < spacing)
                {
                    result = usedYs[i] + spacing;
                    i = -1;
                }
            }
            return Math.Max(ChartPanel.Y, Math.Min(ChartPanel.Y + ChartPanel.H - WallLabelFontSize, result));
        }

        private string FormatAge(TimeSpan age)
        {
            int seconds = Math.Max(0, (int)age.TotalSeconds);
            return string.Format("{0}m{1:00}s", seconds / 60, seconds % 60);
        }

        private float chartControlXByLastBar(float fallback)
        {
            if (ChartControl == null || ChartBars == null || ChartBars.ToIndex < 0)
                return fallback;
            return ChartControl.GetXByBarIndex(ChartBars, ChartBars.ToIndex);
        }

        private void RenderHud()
        {
            if (hudFormat == null || dxHudText == null || dxHudBackground == null)
                return;
            DateTime now = FeedNow();
            double baseline;
            long threshold;
            double delta;
            bool stale;
            lock (bookLock)
            {
                now = DetectionNowLocked(now);
                baseline = adaptiveBaseline;
                threshold = GetWallThresholdLocked();
                delta = sessionAggBuy - sessionAggSell;
                stale = IsBookStale();
            }
            SessionPhase phase = GetSessionPhase(now);
            string hud = PhaseLabel(phase) + " | baseline "
                + (double.IsNaN(baseline) ? "--" : baseline.ToString("F1"))
                + " | wall ≥ " + threshold
                + " | Δ " + (delta >= 0 ? "+" : string.Empty) + delta.ToString("F0")
                + (stale ? " | BOOK STALE" : " | FEED OK");
            float width = Math.Min(ChartPanel.W - 12f, Math.Max(250f, hud.Length * HudFontSize * 0.58f));
            float height = HudFontSize + 10f;
            float x = ChartPanel.X + 6f;
            float y = ChartPanel.Y + 6f;
            if (HudCorner == LiquidityPulseHudCorner.TopRight || HudCorner == LiquidityPulseHudCorner.BottomRight)
                x = ChartPanel.X + ChartPanel.W - width - 6f;
            if (HudCorner == LiquidityPulseHudCorner.BottomLeft || HudCorner == LiquidityPulseHudCorner.BottomRight)
                y = ChartPanel.Y + ChartPanel.H - height - 6f;
            RenderTarget.FillRectangle(new RectangleF(x, y, width, height), dxHudBackground);
            using (TextLayout layout = new TextLayout(Core.Globals.DirectWriteFactory,
                hud, hudFormat, width - 8f, height))
            {
                RenderTarget.DrawTextLayout(new Vector2(x + 4f, y + 3f), layout, dxHudText);
            }
        }

        private string PhaseLabel(SessionPhase phase)
        {
            switch (phase)
            {
                case SessionPhase.EthOvernight: return "ETH-OVERNIGHT";
                case SessionPhase.EthLondon: return "ETH-LONDON";
                case SessionPhase.RthOpen: return "RTH-OPEN";
                case SessionPhase.Rth: return "RTH";
                case SessionPhase.EthPostRth: return "ETH-POST-RTH";
                default: return "POST-CLOSE";
            }
        }

        private void RenderReferenceLevels(ChartControl chartControl, ChartScale chartScale)
        {
            if (ShowPreviousDayLevels)
            {
                if (ShowPreviousDayOpenLevel) RenderLevel(chartControl, chartScale, priorDayOpen, "PD Open", "pdOpen", ReferenceLineWidth, referenceStrokeStyle);
                if (ShowPreviousDayHighLevel) RenderLevel(chartControl, chartScale, priorDayHigh, "PD High", "pdHigh", ReferenceLineWidth, referenceStrokeStyle);
                if (ShowPreviousDayLowLevel) RenderLevel(chartControl, chartScale, priorDayLow, "PD Low", "pdLow", ReferenceLineWidth, referenceStrokeStyle);
            }
            if (ShowPreviousWeekLevels)
            {
                if (ShowPreviousWeekOpenLevel) RenderLevel(chartControl, chartScale, priorWeekOpen, "PW Open", "pwOpen", ReferenceLineWidth, referenceStrokeStyle);
                if (ShowPreviousWeekHighLevel) RenderLevel(chartControl, chartScale, priorWeekHigh, "PW High", "pwHigh", ReferenceLineWidth, referenceStrokeStyle);
                if (ShowPreviousWeekLowLevel) RenderLevel(chartControl, chartScale, priorWeekLow, "PW Low", "pwLow", ReferenceLineWidth, referenceStrokeStyle);
            }
            if (ShowPreviousSessionLevels)
            {
                if (ShowPreviousSessionOpenLevel) RenderLevel(chartControl, chartScale, priorSessionOpen, "PS Open", "psOpen", ReferenceLineWidth, referenceStrokeStyle);
                if (ShowPreviousSessionHighLevel) RenderLevel(chartControl, chartScale, priorSessionHigh, "PS High", "psHigh", ReferenceLineWidth, referenceStrokeStyle);
                if (ShowPreviousSessionLowLevel) RenderLevel(chartControl, chartScale, priorSessionLow, "PS Low", "psLow", ReferenceLineWidth, referenceStrokeStyle);
            }
            if (ShowCurrentSessionLevels)
            {
                if (ShowCurrentSessionOpenLevel) RenderLevel(chartControl, chartScale, currentSessionOpen, "Session Open", "csOpen", ReferenceLineWidth, referenceStrokeStyle);
                if (ShowCurrentSessionHighLevel) RenderLevel(chartControl, chartScale, currentSessionHigh, "Session High", "csHigh", ReferenceLineWidth, referenceStrokeStyle);
                if (ShowCurrentSessionLowLevel) RenderLevel(chartControl, chartScale, currentSessionLow, "Session Low", "csLow", ReferenceLineWidth, referenceStrokeStyle);
            }
        }

        private void RenderProfileLevels(ChartControl chartControl, ChartScale chartScale, int firstBar, int lastBar)
        {
            if (ShowSessionProfileLevels)
            {
                if (ShowSessionPoc) RenderLevel(chartControl, chartScale, sessionPoc, "Session POC", "sessionPoc", ProfileLineWidth, profileStrokeStyle);
                if (ShowSessionVah) RenderLevel(chartControl, chartScale, sessionVah, "Session VAH", "sessionVah", ProfileLineWidth, profileStrokeStyle);
                if (ShowSessionVal) RenderLevel(chartControl, chartScale, sessionVal, "Session VAL", "sessionVal", ProfileLineWidth, profileStrokeStyle);
            }
            if (ShowWeeklyProfileLevels)
            {
                if (ShowWeeklyPoc) RenderLevel(chartControl, chartScale, weeklyPoc, "Weekly POC", "weeklyPoc", ProfileLineWidth, profileStrokeStyle);
                if (ShowWeeklyVah) RenderLevel(chartControl, chartScale, weeklyVah, "Weekly VAH", "weeklyVah", ProfileLineWidth, profileStrokeStyle);
                if (ShowWeeklyVal) RenderLevel(chartControl, chartScale, weeklyVal, "Weekly VAL", "weeklyVal", ProfileLineWidth, profileStrokeStyle);
            }
            if (ShowSessionVwap) RenderSeriesLine(chartControl, chartScale, sessionVwapSeries, "sessionVwap", ProfileLineWidth, profileStrokeStyle, firstBar, lastBar, "Session VWAP");
            if (ShowWeeklyVwap) RenderSeriesLine(chartControl, chartScale, weeklyVwapSeries, "weeklyVwap", ProfileLineWidth, profileStrokeStyle, firstBar, lastBar, "Weekly VWAP");
        }

        private void RenderLevel(ChartControl chartControl, ChartScale chartScale, double price,
            string label, string brushKey, float width, StrokeStyle style)
        {
            SharpDX.Direct2D1.SolidColorBrush brush;
            if (!IsRenderablePrice(price) || !levelBrushes.TryGetValue(brushKey, out brush) || brush == null)
                return;
            float y = chartScale.GetYByValue(price);
            float left = chartControl.GetXByBarIndex(ChartBars, ChartBars.FromIndex);
            float right = chartControl.GetXByBarIndex(ChartBars, ChartBars.ToIndex);
            RenderTarget.DrawLine(new Vector2(left, y), new Vector2(right, y), brush, width, style);
            if (ShowLevelLabels)
                RenderRightLabel(chartControl, chartScale, price, label, brush);
        }

        private void RenderSeriesLine(ChartControl chartControl, ChartScale chartScale,
            Series<double> series, string brushKey, float width, StrokeStyle style,
            int firstBar, int lastBar, string label)
        {
            SharpDX.Direct2D1.SolidColorBrush brush;
            if (series == null || !levelBrushes.TryGetValue(brushKey, out brush) || brush == null)
                return;
            bool hasSegment = false;
            float lastX = 0;
            float lastY = 0;
            double lastValue = double.NaN;
            for (int bar = firstBar; bar <= lastBar; bar++)
            {
                double value = series.GetValueAt(bar);
                if (!IsRenderablePrice(value))
                {
                    hasSegment = false;
                    continue;
                }
                float x = chartControl.GetXByBarIndex(ChartBars, bar);
                float y = chartScale.GetYByValue(value);
                if (hasSegment)
                    RenderTarget.DrawLine(new Vector2(lastX, lastY), new Vector2(x, y), brush, width, style);
                hasSegment = true;
                lastX = x;
                lastY = y;
                lastValue = value;
            }
            if (ShowLevelLabels && IsRenderablePrice(lastValue))
                RenderRightLabel(chartControl, chartScale, lastValue, label, brush);
        }

        private void RenderRightLabel(ChartControl chartControl, ChartScale chartScale,
            double price, string text, SharpDX.Direct2D1.SolidColorBrush brush)
        {
            if (levelLabelFormat == null || brush == null)
                return;
            float x = chartControl.GetXByBarIndex(ChartBars, ChartBars.ToIndex);
            float y = chartScale.GetYByValue(price) - LevelLabelFontSize - 2f;
            using (TextLayout layout = new TextLayout(Core.Globals.DirectWriteFactory,
                text, levelLabelFormat, 220f, LevelLabelFontSize + 4f))
                RenderTarget.DrawTextLayout(new Vector2(x - layout.Metrics.Width - 4f, y), layout, brush);
        }

        private bool IsRenderablePrice(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value.ApproxCompare(0) > 0;
        }

        private SharpDX.Direct2D1.SolidColorBrush CreateDxBrush(System.Windows.Media.Brush brush, int opacity)
        {
            System.Windows.Media.SolidColorBrush solid = brush as System.Windows.Media.SolidColorBrush;
            System.Windows.Media.Color color = solid == null
                ? System.Windows.Media.Colors.White
                : solid.Color;
            return new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                new Color4(color.R / 255f, color.G / 255f, color.B / 255f,
                    Math.Max(0, Math.Min(100, opacity)) / 100f));
        }

        private StrokeStyle CreateStrokeStyle(DashStyleHelper dashStyle)
        {
            if (dashStyle == DashStyleHelper.Solid)
                return null;
            SharpDX.Direct2D1.DashStyle style = SharpDX.Direct2D1.DashStyle.Solid;
            switch (dashStyle)
            {
                case DashStyleHelper.Dash: style = SharpDX.Direct2D1.DashStyle.Dash; break;
                case DashStyleHelper.Dot: style = SharpDX.Direct2D1.DashStyle.Dot; break;
                case DashStyleHelper.DashDot: style = SharpDX.Direct2D1.DashStyle.DashDot; break;
                case DashStyleHelper.DashDotDot: style = SharpDX.Direct2D1.DashStyle.DashDotDot; break;
            }
            return new StrokeStyle(Core.Globals.D2DFactory, new StrokeStyleProperties
            {
                DashStyle = style,
                StartCap = CapStyle.Flat,
                EndCap = CapStyle.Flat,
                LineJoin = LineJoin.Miter
            });
        }

        private void DisposeDxResources()
        {
            SafeDispose(ref wallStrokeStyle);
            SafeDispose(ref referenceStrokeStyle);
            SafeDispose(ref profileStrokeStyle);
            SafeDispose(ref hudFormat);
            SafeDispose(ref wallFormat);
            SafeDispose(ref levelLabelFormat);
            SafeDispose(ref dxAskWall);
            SafeDispose(ref dxBidWall);
            SafeDispose(ref dxStaleWall);
            SafeDispose(ref dxHudText);
            SafeDispose(ref dxHudBackground);
            SafeDispose(ref dxBullShade);
            SafeDispose(ref dxBearShade);
            foreach (SharpDX.Direct2D1.SolidColorBrush brush in levelBrushes.Values)
                if (brush != null)
                    brush.Dispose();
            levelBrushes.Clear();
        }

        private void SafeDispose<T>(ref T resource) where T : class, IDisposable
        {
            if (resource == null)
                return;
            resource.Dispose();
            resource = null;
        }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Use Adaptive Threshold", Order = 1, GroupName = "1. Liquidity Walls")]
        public bool UseAdaptiveThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 100.0)]
        [Display(Name = "Wall Multiplier", Order = 2, GroupName = "1. Liquidity Walls")]
        public double WallMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "Adaptive Window Minutes", Order = 3, GroupName = "1. Liquidity Walls")]
        public int AdaptiveWindowMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Minimum Lot Floor", Order = 4, GroupName = "1. Liquidity Walls")]
        public int MinLotFloor { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Maximum Walls Per Side", Order = 5, GroupName = "1. Liquidity Walls")]
        public int MaxWallsPerSide { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Walls From Last Bar", Order = 6, GroupName = "1. Liquidity Walls")]
        public bool WallsFromLastBar { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Scale Walls By Size", Order = 7, GroupName = "1. Liquidity Walls")]
        public bool ScaleWallsBySize { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Minimum Wall Label Spacing", Order = 8, GroupName = "1. Liquidity Walls")]
        public int MinWallLabelSpacing { get; set; }

        [NinjaScriptProperty]
        [Range(0, 3600)]
        [Display(Name = "Wall Minimum Age Seconds", Order = 9, GroupName = "1. Liquidity Walls")]
        public int WallMinAgeSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10000)]
        [Display(Name = "Wall Grace Milliseconds", Order = 24, GroupName = "1. Liquidity Walls")]
        public int WallGraceMilliseconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 300)]
        [Display(Name = "Feed Stale Seconds", Order = 10, GroupName = "1. Liquidity Walls")]
        public int FeedStaleSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Walls", Order = 11, GroupName = "1. Liquidity Walls")]
        public bool ShowWalls { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Wall Labels", Order = 12, GroupName = "1. Liquidity Walls")]
        public bool ShowWallLabels { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Wall Minimum Width", Order = 13, GroupName = "1. Liquidity Walls")]
        public int WallMinWidth { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Wall Maximum Width", Order = 14, GroupName = "1. Liquidity Walls")]
        public int WallMaxWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Wall Dash Style", Order = 15, GroupName = "1. Liquidity Walls")]
        public DashStyleHelper WallDashStyle { get; set; }

        [XmlIgnore]
        [Display(Name = "Ask Wall Color", Order = 16, GroupName = "1. Liquidity Walls")]
        public System.Windows.Media.Brush AskWallColor { get; set; }
        [Browsable(false)]
        public string AskWallColorSerializable
        {
            get { return Serialize.BrushToString(AskWallColor); }
            set { AskWallColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Bid Wall Color", Order = 17, GroupName = "1. Liquidity Walls")]
        public System.Windows.Media.Brush BidWallColor { get; set; }
        [Browsable(false)]
        public string BidWallColorSerializable
        {
            get { return Serialize.BrushToString(BidWallColor); }
            set { BidWallColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Stale Wall Color", Order = 18, GroupName = "1. Liquidity Walls")]
        public System.Windows.Media.Brush StaleWallColor { get; set; }
        [Browsable(false)]
        public string StaleWallColorSerializable
        {
            get { return Serialize.BrushToString(StaleWallColor); }
            set { StaleWallColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Ask Wall Opacity", Order = 19, GroupName = "1. Liquidity Walls")]
        public int AskWallOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Bid Wall Opacity", Order = 20, GroupName = "1. Liquidity Walls")]
        public int BidWallOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Stale Wall Opacity", Order = 21, GroupName = "1. Liquidity Walls")]
        public int StaleWallOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(6, 24)]
        [Display(Name = "Wall Label Font Size", Order = 22, GroupName = "1. Liquidity Walls")]
        public int WallLabelFontSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show HUD", Order = 1, GroupName = "2. HUD")]
        public bool ShowHud { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "HUD Corner", Order = 2, GroupName = "2. HUD")]
        public LiquidityPulseHudCorner HudCorner { get; set; }

        [NinjaScriptProperty]
        [Range(8, 24)]
        [Display(Name = "HUD Font Size", Order = 3, GroupName = "2. HUD")]
        public int HudFontSize { get; set; }

        [XmlIgnore]
        [Display(Name = "HUD Text Color", Order = 4, GroupName = "2. HUD")]
        public System.Windows.Media.Brush HudTextColor { get; set; }
        [Browsable(false)]
        public string HudTextColorSerializable
        {
            get { return Serialize.BrushToString(HudTextColor); }
            set { HudTextColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "HUD Background Color", Order = 5, GroupName = "2. HUD")]
        public System.Windows.Media.Brush HudBackgroundColor { get; set; }
        [Browsable(false)]
        public string HudBackgroundColorSerializable
        {
            get { return Serialize.BrushToString(HudBackgroundColor); }
            set { HudBackgroundColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "HUD Background Opacity", Order = 6, GroupName = "2. HUD")]
        public int HudBackgroundOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "HUD Text Opacity", Order = 7, GroupName = "2. HUD")]
        public int HudTextOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Bias Shading", Order = 1, GroupName = "3. Bias Shading")]
        public bool EnableBiasShading { get; set; }

        [XmlIgnore]
        [Display(Name = "Bull Shade Color", Order = 2, GroupName = "3. Bias Shading")]
        public System.Windows.Media.Brush BullShadeColor { get; set; }
        [Browsable(false)]
        public string BullShadeColorSerializable
        {
            get { return Serialize.BrushToString(BullShadeColor); }
            set { BullShadeColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Bear Shade Color", Order = 3, GroupName = "3. Bias Shading")]
        public System.Windows.Media.Brush BearShadeColor { get; set; }
        [Browsable(false)]
        public string BearShadeColorSerializable
        {
            get { return Serialize.BrushToString(BearShadeColor); }
            set { BearShadeColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Shade Opacity", Order = 4, GroupName = "3. Bias Shading")]
        public int ShadeOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Shading Mode", Order = 5, GroupName = "3. Bias Shading")]
        public LiquidityPulseShadeMode ShadeMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "HTF Bars Period Type", Order = 6, GroupName = "3. Bias Shading")]
        public BarsPeriodType HtfBarsPeriodType { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "HTF Bars Value", Order = 7, GroupName = "3. Bias Shading")]
        public int HtfBarsValue { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Confirm Bars Period Type", Order = 8, GroupName = "3. Bias Shading")]
        public BarsPeriodType ConfirmBarsPeriodType { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Confirm Bars Value", Order = 9, GroupName = "3. Bias Shading")]
        public int ConfirmBarsValue { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Swing Strength", Order = 10, GroupName = "3. Bias Shading")]
        public int SwingStrength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Previous Day Levels", Order = 1, GroupName = "4. Reference Levels")]
        public bool ShowPreviousDayLevels { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Previous Day Open", Order = 2, GroupName = "4. Reference Levels")]
        public bool ShowPreviousDayOpenLevel { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Previous Day High", Order = 3, GroupName = "4. Reference Levels")]
        public bool ShowPreviousDayHighLevel { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Previous Day Low", Order = 4, GroupName = "4. Reference Levels")]
        public bool ShowPreviousDayLowLevel { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Previous Week Levels", Order = 5, GroupName = "4. Reference Levels")]
        public bool ShowPreviousWeekLevels { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Previous Week Open", Order = 6, GroupName = "4. Reference Levels")]
        public bool ShowPreviousWeekOpenLevel { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Previous Week High", Order = 7, GroupName = "4. Reference Levels")]
        public bool ShowPreviousWeekHighLevel { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Previous Week Low", Order = 8, GroupName = "4. Reference Levels")]
        public bool ShowPreviousWeekLowLevel { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Previous Session Levels", Order = 9, GroupName = "4. Reference Levels")]
        public bool ShowPreviousSessionLevels { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Previous Session Open", Order = 10, GroupName = "4. Reference Levels")]
        public bool ShowPreviousSessionOpenLevel { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Previous Session High", Order = 11, GroupName = "4. Reference Levels")]
        public bool ShowPreviousSessionHighLevel { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Previous Session Low", Order = 12, GroupName = "4. Reference Levels")]
        public bool ShowPreviousSessionLowLevel { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Current Session Levels", Order = 13, GroupName = "4. Reference Levels")]
        public bool ShowCurrentSessionLevels { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Current Session Open", Order = 14, GroupName = "4. Reference Levels")]
        public bool ShowCurrentSessionOpenLevel { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Current Session High", Order = 15, GroupName = "4. Reference Levels")]
        public bool ShowCurrentSessionHighLevel { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Current Session Low", Order = 16, GroupName = "4. Reference Levels")]
        public bool ShowCurrentSessionLowLevel { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Reference Line Width", Order = 17, GroupName = "4. Reference Levels")]
        public int ReferenceLineWidth { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Reference Line Style", Order = 18, GroupName = "4. Reference Levels")]
        public DashStyleHelper ReferenceLineStyle { get; set; }
        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Reference Opacity", Order = 19, GroupName = "4. Reference Levels")]
        public int ReferenceOpacity { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Level Labels", Order = 20, GroupName = "4. Reference Levels")]
        public bool ShowLevelLabels { get; set; }
        [NinjaScriptProperty]
        [Range(8, 18)]
        [Display(Name = "Level Label Font Size", Order = 21, GroupName = "4. Reference Levels")]
        public int LevelLabelFontSize { get; set; }

        [XmlIgnore]
        [Display(Name = "Previous Day Open Color", Order = 22, GroupName = "4. Reference Levels")]
        public System.Windows.Media.Brush PreviousDayOpenColor { get; set; }
        [Browsable(false)]
        public string PreviousDayOpenColorSerializable { get { return Serialize.BrushToString(PreviousDayOpenColor); } set { PreviousDayOpenColor = Serialize.StringToBrush(value); } }
        [XmlIgnore]
        [Display(Name = "Previous Day High Color", Order = 23, GroupName = "4. Reference Levels")]
        public System.Windows.Media.Brush PreviousDayHighColor { get; set; }
        [Browsable(false)]
        public string PreviousDayHighColorSerializable { get { return Serialize.BrushToString(PreviousDayHighColor); } set { PreviousDayHighColor = Serialize.StringToBrush(value); } }
        [XmlIgnore]
        [Display(Name = "Previous Day Low Color", Order = 24, GroupName = "4. Reference Levels")]
        public System.Windows.Media.Brush PreviousDayLowColor { get; set; }
        [Browsable(false)]
        public string PreviousDayLowColorSerializable { get { return Serialize.BrushToString(PreviousDayLowColor); } set { PreviousDayLowColor = Serialize.StringToBrush(value); } }
        [XmlIgnore]
        [Display(Name = "Previous Week Open Color", Order = 25, GroupName = "4. Reference Levels")]
        public System.Windows.Media.Brush PreviousWeekOpenColor { get; set; }
        [Browsable(false)]
        public string PreviousWeekOpenColorSerializable { get { return Serialize.BrushToString(PreviousWeekOpenColor); } set { PreviousWeekOpenColor = Serialize.StringToBrush(value); } }
        [XmlIgnore]
        [Display(Name = "Previous Week High Color", Order = 26, GroupName = "4. Reference Levels")]
        public System.Windows.Media.Brush PreviousWeekHighColor { get; set; }
        [Browsable(false)]
        public string PreviousWeekHighColorSerializable { get { return Serialize.BrushToString(PreviousWeekHighColor); } set { PreviousWeekHighColor = Serialize.StringToBrush(value); } }
        [XmlIgnore]
        [Display(Name = "Previous Week Low Color", Order = 27, GroupName = "4. Reference Levels")]
        public System.Windows.Media.Brush PreviousWeekLowColor { get; set; }
        [Browsable(false)]
        public string PreviousWeekLowColorSerializable { get { return Serialize.BrushToString(PreviousWeekLowColor); } set { PreviousWeekLowColor = Serialize.StringToBrush(value); } }
        [XmlIgnore]
        [Display(Name = "Previous Session Open Color", Order = 28, GroupName = "4. Reference Levels")]
        public System.Windows.Media.Brush PreviousSessionOpenColor { get; set; }
        [Browsable(false)]
        public string PreviousSessionOpenColorSerializable { get { return Serialize.BrushToString(PreviousSessionOpenColor); } set { PreviousSessionOpenColor = Serialize.StringToBrush(value); } }
        [XmlIgnore]
        [Display(Name = "Previous Session High Color", Order = 29, GroupName = "4. Reference Levels")]
        public System.Windows.Media.Brush PreviousSessionHighColor { get; set; }
        [Browsable(false)]
        public string PreviousSessionHighColorSerializable { get { return Serialize.BrushToString(PreviousSessionHighColor); } set { PreviousSessionHighColor = Serialize.StringToBrush(value); } }
        [XmlIgnore]
        [Display(Name = "Previous Session Low Color", Order = 30, GroupName = "4. Reference Levels")]
        public System.Windows.Media.Brush PreviousSessionLowColor { get; set; }
        [Browsable(false)]
        public string PreviousSessionLowColorSerializable { get { return Serialize.BrushToString(PreviousSessionLowColor); } set { PreviousSessionLowColor = Serialize.StringToBrush(value); } }
        [XmlIgnore]
        [Display(Name = "Current Session Open Color", Order = 31, GroupName = "4. Reference Levels")]
        public System.Windows.Media.Brush CurrentSessionOpenColor { get; set; }
        [Browsable(false)]
        public string CurrentSessionOpenColorSerializable { get { return Serialize.BrushToString(CurrentSessionOpenColor); } set { CurrentSessionOpenColor = Serialize.StringToBrush(value); } }
        [XmlIgnore]
        [Display(Name = "Current Session High Color", Order = 32, GroupName = "4. Reference Levels")]
        public System.Windows.Media.Brush CurrentSessionHighColor { get; set; }
        [Browsable(false)]
        public string CurrentSessionHighColorSerializable { get { return Serialize.BrushToString(CurrentSessionHighColor); } set { CurrentSessionHighColor = Serialize.StringToBrush(value); } }
        [XmlIgnore]
        [Display(Name = "Current Session Low Color", Order = 33, GroupName = "4. Reference Levels")]
        public System.Windows.Media.Brush CurrentSessionLowColor { get; set; }
        [Browsable(false)]
        public string CurrentSessionLowColorSerializable { get { return Serialize.BrushToString(CurrentSessionLowColor); } set { CurrentSessionLowColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Display(Name = "Show Session Profile Levels", Order = 1, GroupName = "5. Profile and VWAP")]
        public bool ShowSessionProfileLevels { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Session POC", Order = 2, GroupName = "5. Profile and VWAP")]
        public bool ShowSessionPoc { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Session VAH", Order = 3, GroupName = "5. Profile and VWAP")]
        public bool ShowSessionVah { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Session VAL", Order = 4, GroupName = "5. Profile and VWAP")]
        public bool ShowSessionVal { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Weekly Profile Levels", Order = 5, GroupName = "5. Profile and VWAP")]
        public bool ShowWeeklyProfileLevels { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Weekly POC", Order = 6, GroupName = "5. Profile and VWAP")]
        public bool ShowWeeklyPoc { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Weekly VAH", Order = 7, GroupName = "5. Profile and VWAP")]
        public bool ShowWeeklyVah { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Weekly VAL", Order = 8, GroupName = "5. Profile and VWAP")]
        public bool ShowWeeklyVal { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Session VWAP", Order = 9, GroupName = "5. Profile and VWAP")]
        public bool ShowSessionVwap { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show Weekly VWAP", Order = 10, GroupName = "5. Profile and VWAP")]
        public bool ShowWeeklyVwap { get; set; }
        [NinjaScriptProperty]
        [Range(50, 99)]
        [Display(Name = "Value Area Percent", Order = 11, GroupName = "5. Profile and VWAP")]
        public int ValueAreaPercent { get; set; }
        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Profile Line Width", Order = 12, GroupName = "5. Profile and VWAP")]
        public int ProfileLineWidth { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Profile Line Style", Order = 13, GroupName = "5. Profile and VWAP")]
        public DashStyleHelper ProfileLineStyle { get; set; }
        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Profile Opacity", Order = 14, GroupName = "5. Profile and VWAP")]
        public int ProfileOpacity { get; set; }
        [XmlIgnore]
        [Display(Name = "Session POC Color", Order = 15, GroupName = "5. Profile and VWAP")]
        public System.Windows.Media.Brush SessionPocColor { get; set; }
        [Browsable(false)]
        public string SessionPocColorSerializable { get { return Serialize.BrushToString(SessionPocColor); } set { SessionPocColor = Serialize.StringToBrush(value); } }
        [XmlIgnore]
        [Display(Name = "Session VAH Color", Order = 16, GroupName = "5. Profile and VWAP")]
        public System.Windows.Media.Brush SessionVahColor { get; set; }
        [Browsable(false)]
        public string SessionVahColorSerializable { get { return Serialize.BrushToString(SessionVahColor); } set { SessionVahColor = Serialize.StringToBrush(value); } }
        [XmlIgnore]
        [Display(Name = "Session VAL Color", Order = 17, GroupName = "5. Profile and VWAP")]
        public System.Windows.Media.Brush SessionValColor { get; set; }
        [Browsable(false)]
        public string SessionValColorSerializable { get { return Serialize.BrushToString(SessionValColor); } set { SessionValColor = Serialize.StringToBrush(value); } }
        [XmlIgnore]
        [Display(Name = "Session VWAP Color", Order = 18, GroupName = "5. Profile and VWAP")]
        public System.Windows.Media.Brush SessionVwapColor { get; set; }
        [Browsable(false)]
        public string SessionVwapColorSerializable { get { return Serialize.BrushToString(SessionVwapColor); } set { SessionVwapColor = Serialize.StringToBrush(value); } }
        [XmlIgnore]
        [Display(Name = "Weekly POC Color", Order = 19, GroupName = "5. Profile and VWAP")]
        public System.Windows.Media.Brush WeeklyPocColor { get; set; }
        [Browsable(false)]
        public string WeeklyPocColorSerializable { get { return Serialize.BrushToString(WeeklyPocColor); } set { WeeklyPocColor = Serialize.StringToBrush(value); } }
        [XmlIgnore]
        [Display(Name = "Weekly VAH Color", Order = 20, GroupName = "5. Profile and VWAP")]
        public System.Windows.Media.Brush WeeklyVahColor { get; set; }
        [Browsable(false)]
        public string WeeklyVahColorSerializable { get { return Serialize.BrushToString(WeeklyVahColor); } set { WeeklyVahColor = Serialize.StringToBrush(value); } }
        [XmlIgnore]
        [Display(Name = "Weekly VAL Color", Order = 21, GroupName = "5. Profile and VWAP")]
        public System.Windows.Media.Brush WeeklyValColor { get; set; }
        [Browsable(false)]
        public string WeeklyValColorSerializable { get { return Serialize.BrushToString(WeeklyValColor); } set { WeeklyValColor = Serialize.StringToBrush(value); } }
        [XmlIgnore]
        [Display(Name = "Weekly VWAP Color", Order = 22, GroupName = "5. Profile and VWAP")]
        public System.Windows.Media.Brush WeeklyVwapColor { get; set; }
        [Browsable(false)]
        public string WeeklyVwapColorSerializable { get { return Serialize.BrushToString(WeeklyVwapColor); } set { WeeklyVwapColor = Serialize.StringToBrush(value); } }
        #endregion
    }
}
