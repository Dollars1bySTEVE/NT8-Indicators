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
            public DateTime DisappearanceStarted;
            public long PeakSize;
            public long LastSize;
            public long LastObservedSize;
            public double TradedBuy;
            public double TradedSell;
            public int RefillCount;
            public bool HadSizeReduction;
            public bool AbsorptionEmitted;
            public bool TradedThrough;
            public bool DisappearanceEventEmitted;
            public bool Active;
            public bool LostQualification;
        }

        private enum LiquidityEventType
        {
            Pull,
            Filled,
            Absorption,
            Sweep,
            FailedSweep,
            HeldRun,
            LargeTrade
        }

        private class EventMarker
        {
            public LiquidityEventType Type;
            public DateTime Time;
            public double Price;
            public string Label;
            public string KeyLevel;
            public bool IsAsk;
        }

        private class WallFade
        {
            public LiquidityEventType Type;
            public DateTime Time;
            public double Price;
        }

        private enum ResidualZoneState
        {
            Untested,
            Held,
            Taken,
            Gone
        }

        private class ResidualZone
        {
            public double Price;
            public bool IsAsk;
            public DateTime Created;
            public DateTime Resolved;
            public DateTime LowSince;
            public long PeakSize;
            public double Traded;
            public ResidualZoneState State;
        }

        private class HeatmapLevel
        {
            public double Price;
            public long Size;
            public bool IsAsk;
        }

        private class HeatmapSnapshot
        {
            public DateTime Time;
            public List<HeatmapLevel> Levels;
        }

        private class TradeSample
        {
            public DateTime Time;
            public double Price;
            public double Volume;
            public bool IsBuy;
        }

        private class SweepCandidate
        {
            public DateTime Time;
            public DateTime LastUpdated;
            public double LevelPrice;
            public string LevelName;
            public bool IsUp;
            public double Volume;
            public double OpposingVolume;
            public bool Failed;
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
        private readonly List<WallState> retiredAskWalls = new List<WallState>();
        private readonly List<WallState> retiredBidWalls = new List<WallState>();
        private readonly Queue<TradeSample> recentBuyTrades = new Queue<TradeSample>();
        private readonly Queue<TradeSample> recentSellTrades = new Queue<TradeSample>();
        private readonly LinkedList<TradeSample> recentBuyMinPrices = new LinkedList<TradeSample>();
        private readonly LinkedList<TradeSample> recentSellMaxPrices = new LinkedList<TradeSample>();
        private double recentBuyVolume;
        private double recentSellVolume;
        private bool buySweepEpisodeActive;
        private bool sellSweepEpisodeActive;
        private readonly List<EventMarker> eventMarkers = new List<EventMarker>();
        private readonly List<WallFade> wallFades = new List<WallFade>();
        private readonly List<ResidualZone> residualZones = new List<ResidualZone>();
        private readonly HashSet<double> drawnAskWallLabels = new HashSet<double>();
        private readonly HashSet<double> drawnBidWallLabels = new HashSet<double>();
        private readonly Queue<HeatmapSnapshot> heatmapSnapshots = new Queue<HeatmapSnapshot>();
        private readonly List<SweepCandidate> sweepCandidates = new List<SweepCandidate>();
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
        private double lastTradePrice = double.NaN;
        private DateTime lastSweepBuyTime = DateTime.MinValue;
        private DateTime lastSweepSellTime = DateTime.MinValue;
        private DateTime lastBookImbalanceUpdate = DateTime.MinValue;
        private DateTime lastHeatmapSampleTime = DateTime.MinValue;
        private double smoothedBookImbalance;
        private TextFormat eventFormat;
        private TextFormat heatmapFormat;
        private SharpDX.Direct2D1.SolidColorBrush dxHeatmapBid;
        private SharpDX.Direct2D1.SolidColorBrush dxHeatmapAsk;
        private SharpDX.Direct2D1.SolidColorBrush dxHeatmapBackground;
        private SharpDX.Direct2D1.SolidColorBrush dxPullEvent;
        private SharpDX.Direct2D1.SolidColorBrush dxFilledEvent;
        private SharpDX.Direct2D1.SolidColorBrush dxAbsorptionEvent;
        private SharpDX.Direct2D1.SolidColorBrush dxSweepEvent;
        private SharpDX.Direct2D1.SolidColorBrush dxFailedSweepEvent;
        private SharpDX.Direct2D1.SolidColorBrush dxHeldRunEvent;
        private SharpDX.Direct2D1.SolidColorBrush dxLargeTradeEvent;
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
        private StrokeStyle pullFadeStrokeStyle;
        private StrokeStyle pulledZoneStrokeStyle;
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

                ShowEventMarkers = true;
                ShowPullEvents = true;
                ShowFilledEvents = true;
                ShowAbsorptionEvents = true;
                ShowSweepEvents = true;
                ShowFailedSweepEvents = true;
                ShowHeldRunEvents = true;
                ShowLargeTradeEvents = true;
                ShowHeatmap = true;
                ShowResidualZones = true;
                ShowResolvedResidualZones = true;
                ResidualZoneMaxMinutes = 120;
                ResidualZoneOpacity = 70;
                TakenZoneFadeMinutes = 5;
                ShowPulledZones = true;
                HeatmapHistorySeconds = 60;
                HeatmapWidth = 160;
                HeatmapOpacity = 70;
                HeatmapBidColor = System.Windows.Media.Brushes.LimeGreen;
                HeatmapAskColor = System.Windows.Media.Brushes.Crimson;
                EventMarkerFontSize = 9;
                EventMarkerOpacity = 90;
                EventMarkerRetentionMinutes = 60;
                WallFadeDurationSeconds = 10;
                EventGraceMilliseconds = 300;
                PullTradeRatio = 0.3;
                AbsorptionTradeMultiple = 1.5;
                SweepWindowSeconds = 3;
                SweepMinimumTicks = 3;
                SweepMinimumVolume = 20;
                FailedRunWindowSeconds = 10;
                LargeTradeMinimumContracts = 20;
                KeyLevelProximityTicks = 2;
                EventCooldownSeconds = 5;
                PullEventColor = System.Windows.Media.Brushes.Crimson;
                FilledEventColor = System.Windows.Media.Brushes.Orange;
                AbsorptionEventColor = System.Windows.Media.Brushes.Gold;
                SweepEventColor = System.Windows.Media.Brushes.DodgerBlue;
                FailedSweepEventColor = System.Windows.Media.Brushes.Magenta;
                HeldRunEventColor = System.Windows.Media.Brushes.DeepSkyBlue;
                LargeTradeEventColor = System.Windows.Media.Brushes.Orchid;
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
                    lastTradePrice = price;
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
                    RecordTradeThroughLocked(price);
                    UpdateResidualZoneTradesLocked(now, price, e.Volume,
                        aggressiveBuy ? 1 : (aggressiveSell ? -1 : 0));
                    if (!IsBookStale())
                        ProcessTradeLocked(now, price, e.Volume,
                            aggressiveBuy ? 1 : (aggressiveSell ? -1 : 0));
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
            retiredAskWalls.Clear();
            retiredBidWalls.Clear();
            ClearRecentTradesLocked();
            sweepCandidates.Clear();
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
                smoothedBookImbalance = 0;
                lastBookImbalanceUpdate = DateTime.MinValue;
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
                retiredAskWalls.Clear();
                retiredBidWalls.Clear();
                pendingDetection = false;
                PruneEventHistoryLocked(now);
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
                bool bookFresh = !IsBookStale();
                CaptureHeatmapSnapshotLocked(now, bookFresh);
                RefreshWallsLocked(askRows, askWalls, retiredAskWalls, true, now, bookFresh);
                RefreshWallsLocked(bidRows, bidWalls, retiredBidWalls, false, now, bookFresh);
                UpdateBookImbalanceLocked(now);
                if (bookFresh)
                {
                    ProcessPendingWallEventsLocked(askRows, askWalls, retiredAskWalls, true, now);
                    ProcessPendingWallEventsLocked(bidRows, bidWalls, retiredBidWalls, false, now);
                    ProcessSweepCandidatesLocked(now);
                    UpdateResidualZonesLocked(now);
                }
                PruneEventHistoryLocked(now);
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

        private void CaptureHeatmapSnapshotLocked(DateTime now, bool bookFresh)
        {
            if (lastHeatmapSampleTime != DateTime.MinValue && now < lastHeatmapSampleTime)
            {
                heatmapSnapshots.Clear();
                lastHeatmapSampleTime = DateTime.MinValue;
            }

            while (heatmapSnapshots.Count > 0
                && (now - heatmapSnapshots.Peek().Time).TotalSeconds > HeatmapHistorySeconds)
                heatmapSnapshots.Dequeue();

            if (!ShowHeatmap || !bookFresh
                || lastHeatmapSampleTime != DateTime.MinValue
                    && (now - lastHeatmapSampleTime).TotalMilliseconds < 250)
                return;

            List<HeatmapLevel> levels = new List<HeatmapLevel>();
            for (int i = 0; i < Math.Min(10, bidRows.Count); i++)
                if (bidRows[i].Size > 0)
                    levels.Add(new HeatmapLevel
                    {
                        Price = bidRows[i].Price,
                        Size = bidRows[i].Size,
                        IsAsk = false
                    });
            for (int i = 0; i < Math.Min(10, askRows.Count); i++)
                if (askRows[i].Size > 0)
                    levels.Add(new HeatmapLevel
                    {
                        Price = askRows[i].Price,
                        Size = askRows[i].Size,
                        IsAsk = true
                    });

            if (levels.Count == 0)
                return;

            while (heatmapSnapshots.Count >= 240)
                heatmapSnapshots.Dequeue();
            heatmapSnapshots.Enqueue(new HeatmapSnapshot { Time = now, Levels = levels });
            lastHeatmapSampleTime = now;
            while (heatmapSnapshots.Count > 240)
                heatmapSnapshots.Dequeue();
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
            if (lastSampleTime != DateTime.MinValue
                && (now - lastSampleTime).TotalSeconds < 2)
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
            List<WallState> retiredWalls, bool isAsk, DateTime now, bool bookFresh)
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
                long previousSize = active ? wall.LastObservedSize : 0;
                if (active && row.Size < previousSize)
                    wall.HadSizeReduction = true;
                else if (active && wall.HadSizeReduction && row.Size > previousSize)
                {
                    wall.RefillCount++;
                    wall.HadSizeReduction = false;
                }
                if (active)
                    wall.LastObservedSize = row.Size;
                if (row.Size < (active ? 0.8 * threshold : threshold))
                    continue;
                seen.Add(row.Price);
                if (!active)
                {
                    if (wall != null && wall.LostQualification && !wall.DisappearanceEventEmitted
                        && wall.DisappearanceStarted != DateTime.MinValue)
                    {
                        // Keep the expired lifecycle until its disappearance is classified.
                        retiredWalls.Add(wall);
                        while (retiredWalls.Count > 200)
                            retiredWalls.RemoveAt(0);
                    }
                    wall = new WallState
                    {
                        Price = row.Price,
                        IsAsk = isAsk,
                        FirstSeen = now,
                        LastObservedSize = row.Size,
                        Active = true
                    };
                    walls[row.Price] = wall;
                }
                wall.LostQualification = false;
                wall.DisappearanceStarted = DateTime.MinValue;
                wall.LastSeenAboveThreshold = now;
                wall.LastSize = row.Size;
                wall.PeakSize = Math.Max(wall.PeakSize, row.Size);
                double tradedAtWall = isAsk ? wall.TradedBuy : wall.TradedSell;
                bool priceHeld = double.IsNaN(lastTradePrice)
                    || (isAsk ? lastTradePrice <= wall.Price : lastTradePrice >= wall.Price);
                if (bookFresh && !wall.AbsorptionEmitted && wall.RefillCount > 0 && priceHeld
                    && wall.PeakSize > 0
                    && tradedAtWall >= wall.PeakSize * AbsorptionTradeMultiple)
                {
                    wall.AbsorptionEmitted = true;
                    AddEventLocked(LiquidityEventType.Absorption, wall.Price, isAsk, now,
                        string.Format("ABSORB {0} {1:F0}", isAsk ? "ASK" : "BID", tradedAtWall),
                        FindNearestKeyLevelLocked(wall.Price, KeyLevelProximityTicks));
                }
            }

            foreach (WallState wall in walls.Values)
            {
                if (wall.Active && !seen.Contains(wall.Price))
                {
                    wall.LostQualification = true;
                    if (wall.DisappearanceStarted == DateTime.MinValue)
                        wall.DisappearanceStarted = now;
                    if ((now - wall.LastSeenAboveThreshold).TotalMilliseconds >= WallGraceMilliseconds)
                    {
                        wall.Active = false;
                    }
                }
            }
        }

        private void ProcessTradeLocked(DateTime now, double price, double volume, int aggressor)
        {
            if (aggressor != 0)
            {
                PruneRecentTradesLocked(now);
                RearmSweepEpisodesLocked();
                AddRecentTradeLocked(new TradeSample
                {
                    Time = now,
                    Price = price,
                    Volume = volume,
                    IsBuy = aggressor > 0
                });

                double largeTradeThreshold = Math.Max(LargeTradeMinimumContracts,
                    GetWallThresholdLocked() * 2.0);
                if (volume >= largeTradeThreshold)
                {
                    AddEventLocked(LiquidityEventType.LargeTrade, price, aggressor < 0, now,
                        string.Format("LARGE {0} {1:F0}", aggressor > 0 ? "BUY" : "SELL", volume),
                        FindNearestKeyLevelLocked(price, KeyLevelProximityTicks));
                }
                DetectSweepLocked(now, aggressor, price);
            }

            for (int i = sweepCandidates.Count - 1; i >= 0; i--)
            {
                SweepCandidate candidate = sweepCandidates[i];
                candidate.LastUpdated = now;
                if (aggressor != 0 && ((candidate.IsUp && aggressor < 0)
                    || (!candidate.IsUp && aggressor > 0)))
                    candidate.OpposingVolume += volume;

                bool returnedInside = candidate.IsUp
                    ? price < candidate.LevelPrice
                    : price > candidate.LevelPrice;
                double requiredOpposingVolume = Math.Max(1, candidate.Volume * 0.2);
                if (!candidate.Failed && (now - candidate.Time).TotalSeconds <= FailedRunWindowSeconds
                    && returnedInside && candidate.OpposingVolume >= requiredOpposingVolume)
                {
                    candidate.Failed = true;
                    AddEventLocked(LiquidityEventType.FailedSweep, candidate.LevelPrice,
                        candidate.IsUp, now,
                        "FAILED SWEEP " + candidate.LevelName,
                        candidate.LevelName);
                }
            }
        }

        private bool SweepWindowQualifiesLocked(bool isBuy, double currentPrice,
            out double minPrice, out double maxPrice)
        {
            double tickSize = Instrument.MasterInstrument.TickSize;
            double volume = isBuy ? recentBuyVolume : recentSellVolume;
            LinkedList<TradeSample> extremes = isBuy ? recentBuyMinPrices : recentSellMaxPrices;
            minPrice = currentPrice;
            maxPrice = currentPrice;
            if (extremes.Count == 0 || tickSize <= 0 || volume < SweepMinimumVolume)
                return false;
            if (isBuy)
                minPrice = extremes.First.Value.Price;
            else
                maxPrice = extremes.First.Value.Price;
            return (maxPrice - minPrice) / tickSize >= SweepMinimumTicks;
        }

        private void RearmSweepEpisodesLocked()
        {
            // The newest same-side print is always the tail of its monotonic extreme deque.
            double minPrice, maxPrice;
            if (buySweepEpisodeActive && (recentBuyMinPrices.Count == 0
                || !SweepWindowQualifiesLocked(true, recentBuyMinPrices.Last.Value.Price,
                    out minPrice, out maxPrice)))
                buySweepEpisodeActive = false;
            if (sellSweepEpisodeActive && (recentSellMaxPrices.Count == 0
                || !SweepWindowQualifiesLocked(false, recentSellMaxPrices.Last.Value.Price,
                    out minPrice, out maxPrice)))
                sellSweepEpisodeActive = false;
        }

        private void DetectSweepLocked(DateTime now, int aggressor, double price)
        {
            // Rolling per-side aggregates: progress is measured from the earlier extreme opposite the
            // aggressor direction to the current print, so only time-ordered movement counts.
            double volume = aggressor > 0 ? recentBuyVolume : recentSellVolume;
            double minPrice, maxPrice;
            if (!SweepWindowQualifiesLocked(aggressor > 0, price, out minPrice, out maxPrice))
            {
                if (aggressor > 0)
                    buySweepEpisodeActive = false;
                else
                    sellSweepEpisodeActive = false;
                return;
            }

            bool alreadyEmitted = aggressor > 0
                ? buySweepEpisodeActive : sellSweepEpisodeActive;
            if (alreadyEmitted)
                return;
            if (aggressor > 0)
                buySweepEpisodeActive = true;
            else
                sellSweepEpisodeActive = true;

            DateTime lastSweep = aggressor > 0 ? lastSweepBuyTime : lastSweepSellTime;
            if (lastSweep != DateTime.MinValue
                && (now - lastSweep).TotalSeconds < EventCooldownSeconds)
                return;

            bool isUp = aggressor > 0;
            string keyLevel = FindCrossedKeyLevelLocked(minPrice, maxPrice, isUp);
            string label = string.Format("SWEEP {0} {1:F0}", isUp ? "UP" : "DOWN", volume);
            if (!string.IsNullOrEmpty(keyLevel))
                label += " " + keyLevel;
            AddEventLocked(LiquidityEventType.Sweep, lastTradePrice, !isUp, now, label, keyLevel);
            if (aggressor > 0)
                lastSweepBuyTime = now;
            else
                lastSweepSellTime = now;

            if (!string.IsNullOrEmpty(keyLevel))
            {
                double levelPrice = GetKeyLevelPriceLocked(keyLevel);
                if (!double.IsNaN(levelPrice))
                {
                    sweepCandidates.Add(new SweepCandidate
                    {
                        Time = now,
                        LastUpdated = now,
                        LevelPrice = levelPrice,
                        LevelName = keyLevel,
                        IsUp = isUp,
                        Volume = volume
                    });
                }
            }
        }

        private void ProcessPendingWallEventsLocked(List<DepthRow> rows,
            Dictionary<double, WallState> walls, List<WallState> retiredWalls, bool isAsk, DateTime now)
        {
            List<WallState> pending = new List<WallState>(retiredWalls);
            pending.AddRange(walls.Values);
            foreach (WallState wall in pending)
            {
                if (!wall.LostQualification || wall.DisappearanceEventEmitted
                    || wall.DisappearanceStarted == DateTime.MinValue
                    || (now - wall.DisappearanceStarted).TotalMilliseconds
                        < Math.Max(EventGraceMilliseconds, WallGraceMilliseconds))
                    continue;
                if (!IsPriceInVisibleWindowLocked(rows, wall.Price, isAsk))
                {
                    AddResidualZoneLocked(wall);
                    wall.DisappearanceEventEmitted = true;
                    continue;
                }

                double traded = isAsk ? wall.TradedBuy : wall.TradedSell;
                bool tradedEnough = wall.PeakSize > 0
                    && traded >= wall.PeakSize * PullTradeRatio;
                bool tradedThrough = wall.TradedThrough;
                if (tradedEnough && tradedThrough)
                {
                    AddEventLocked(LiquidityEventType.Filled, wall.Price, isAsk, now,
                        string.Format("BROKEN {0} {1:F0}", isAsk ? "ASK" : "BID", traded),
                        FindNearestKeyLevelLocked(wall.Price, KeyLevelProximityTicks));
                    AddWallFadeLocked(LiquidityEventType.Filled, wall.Price, now);
                    wall.DisappearanceEventEmitted = true;
                }
                else if (!tradedEnough)
                {
                    string keyLevel = FindNearestKeyLevelLocked(wall.Price, KeyLevelProximityTicks);
                    AddEventLocked(LiquidityEventType.Pull, wall.Price, isAsk, now,
                        "PULL " + (isAsk ? "ASK" : "BID"), keyLevel);
                    AddWallFadeLocked(LiquidityEventType.Pull, wall.Price, now);
                    wall.DisappearanceEventEmitted = true;
                    int recentPullCount = CountRecentPullsLocked(isAsk, now);
                    if (recentPullCount == 3)
                        AddEventLocked(LiquidityEventType.Pull, wall.Price, isAsk, now,
                            string.Format("{0}× {1} PULLS", recentPullCount, isAsk ? "ASK" : "BID"),
                            keyLevel);
                }
                else
                {
                    wall.DisappearanceEventEmitted = true;
                }
            }

            retiredWalls.RemoveAll(delegate(WallState retired) { return retired.DisappearanceEventEmitted; });
            List<double> finished = new List<double>();
            foreach (KeyValuePair<double, WallState> entry in walls)
                if (!entry.Value.Active && entry.Value.DisappearanceEventEmitted)
                    finished.Add(entry.Key);
            for (int i = 0; i < finished.Count; i++)
                walls.Remove(finished[i]);
        }

        private void RecordTradeThroughLocked(double price)
        {
            double tickSize = Instrument.MasterInstrument.TickSize;
            MarkTradeThroughLocked(askWalls.Values, price, tickSize, true);
            MarkTradeThroughLocked(retiredAskWalls, price, tickSize, true);
            MarkTradeThroughLocked(bidWalls.Values, price, tickSize, false);
            MarkTradeThroughLocked(retiredBidWalls, price, tickSize, false);
        }

        private void MarkTradeThroughLocked(IEnumerable<WallState> walls, double price, double tickSize, bool isAsk)
        {
            foreach (WallState wall in walls)
            {
                if (wall.TradedThrough || wall.DisappearanceEventEmitted)
                    continue;
                if (isAsk ? price >= wall.Price + tickSize : price <= wall.Price - tickSize)
                    wall.TradedThrough = true;
            }
        }

        private bool IsPriceInVisibleWindowLocked(List<DepthRow> rows, double price, bool isAsk)
        {
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Price.ApproxCompare(price) == 0)
                    return true;
            if (rows.Count == 0)
                return false;
            double edge = rows[rows.Count - 1].Price;
            if (isAsk ? price > edge : price < edge)
                return false;
            // A consumed level may move onto the opposite side, but must stay inside its outer edge.
            List<DepthRow> opposite = isAsk ? bidRows : askRows;
            if (opposite.Count == 0)
                return true;
            double oppositeEdge = opposite[opposite.Count - 1].Price;
            return isAsk ? price >= oppositeEdge : price <= oppositeEdge;
        }

        private int CountRecentPullsLocked(bool isAsk, DateTime now)
        {
            int count = 0;
            for (int i = 0; i < eventMarkers.Count; i++)
                if (eventMarkers[i].Type == LiquidityEventType.Pull
                    && eventMarkers[i].IsAsk == isAsk
                    && eventMarkers[i].Label.StartsWith("PULL ", StringComparison.OrdinalIgnoreCase)
                    && (now - eventMarkers[i].Time).TotalSeconds <= 10)
                    count++;
            return count;
        }

        private void UpdateBookImbalanceLocked(DateTime now)
        {
            if (lastBookImbalanceUpdate != DateTime.MinValue
                && (now - lastBookImbalanceUpdate).TotalMilliseconds < 500)
                return;
            lastBookImbalanceUpdate = now;
            double bidSize = 0;
            double askSize = 0;
            for (int i = 0; i < bidRows.Count; i++)
                bidSize += bidRows[i].Size;
            for (int i = 0; i < askRows.Count; i++)
                askSize += askRows[i].Size;
            double total = bidSize + askSize;
            if (total <= 0)
                return;
            double current = (bidSize - askSize) / total * 100.0;
            smoothedBookImbalance = smoothedBookImbalance == 0
                ? current
                : smoothedBookImbalance * 0.75 + current * 0.25;
        }

        private void ProcessSweepCandidatesLocked(DateTime now)
        {
            for (int i = sweepCandidates.Count - 1; i >= 0; i--)
            {
                SweepCandidate candidate = sweepCandidates[i];
                if ((now - candidate.Time).TotalSeconds <= FailedRunWindowSeconds)
                    continue;
                if (!candidate.Failed && IsBeyondLevel(candidate) && IsLiquidityVacuumLocked(candidate))
                    AddEventLocked(LiquidityEventType.HeldRun, candidate.LevelPrice,
                        candidate.IsUp, now, "HELD RUN " + candidate.LevelName,
                        candidate.LevelName);
                sweepCandidates.RemoveAt(i);
            }
        }

        private bool IsBeyondLevel(SweepCandidate candidate)
        {
            return !double.IsNaN(lastTradePrice)
                && (candidate.IsUp ? lastTradePrice >= candidate.LevelPrice
                    : lastTradePrice <= candidate.LevelPrice);
        }

        private bool IsLiquidityVacuumLocked(SweepCandidate candidate)
        {
            List<DepthRow> rows = candidate.IsUp ? askRows : bidRows;
            if (rows.Count == 0 || double.IsNaN(adaptiveBaseline))
                return false;
            double aheadSize = 0;
            double current = lastTradePrice;
            for (int i = 0; i < rows.Count; i++)
                if (candidate.IsUp ? rows[i].Price > current : rows[i].Price < current)
                    aheadSize += rows[i].Size;
            return aheadSize < adaptiveBaseline * Math.Max(1, rows.Count) * 0.5;
        }

        private void AddRecentTradeLocked(TradeSample sample)
        {
            if (sample.IsBuy)
            {
                recentBuyTrades.Enqueue(sample);
                recentBuyVolume += sample.Volume;
                while (recentBuyMinPrices.Count > 0 && recentBuyMinPrices.Last.Value.Price >= sample.Price)
                    recentBuyMinPrices.RemoveLast();
                recentBuyMinPrices.AddLast(sample);
            }
            else
            {
                recentSellTrades.Enqueue(sample);
                recentSellVolume += sample.Volume;
                while (recentSellMaxPrices.Count > 0 && recentSellMaxPrices.Last.Value.Price <= sample.Price)
                    recentSellMaxPrices.RemoveLast();
                recentSellMaxPrices.AddLast(sample);
            }
        }

        private void PruneRecentTradesLocked(DateTime now)
        {
            while (recentBuyTrades.Count > 0
                && (now - recentBuyTrades.Peek().Time).TotalSeconds > SweepWindowSeconds)
            {
                TradeSample expired = recentBuyTrades.Dequeue();
                recentBuyVolume -= expired.Volume;
                if (recentBuyMinPrices.Count > 0 && ReferenceEquals(recentBuyMinPrices.First.Value, expired))
                    recentBuyMinPrices.RemoveFirst();
            }
            while (recentSellTrades.Count > 0
                && (now - recentSellTrades.Peek().Time).TotalSeconds > SweepWindowSeconds)
            {
                TradeSample expired = recentSellTrades.Dequeue();
                recentSellVolume -= expired.Volume;
                if (recentSellMaxPrices.Count > 0 && ReferenceEquals(recentSellMaxPrices.First.Value, expired))
                    recentSellMaxPrices.RemoveFirst();
            }
            if (recentBuyTrades.Count == 0)
                recentBuyVolume = 0;
            if (recentSellTrades.Count == 0)
                recentSellVolume = 0;
        }

        private void ClearRecentTradesLocked()
        {
            recentBuyTrades.Clear();
            recentSellTrades.Clear();
            recentBuyMinPrices.Clear();
            recentSellMaxPrices.Clear();
            recentBuyVolume = 0;
            recentSellVolume = 0;
        }

        private void PruneEventHistoryLocked(DateTime now)
        {
            double retention = Math.Max(1, EventMarkerRetentionMinutes);
            eventMarkers.RemoveAll(delegate(EventMarker marker)
            {
                return (now - marker.Time).TotalMinutes > retention;
            });
            while (eventMarkers.Count > 500)
                eventMarkers.RemoveAt(0);
            wallFades.RemoveAll(delegate(WallFade fade)
            {
                return (now - fade.Time).TotalSeconds > WallFadeDurationSeconds;
            });
            double zoneRetention = Math.Max(1, ResidualZoneMaxMinutes);
            residualZones.RemoveAll(delegate(ResidualZone zone)
            {
                return (now - zone.Created).TotalMinutes > zoneRetention
                    || (zone.State == ResidualZoneState.Taken
                        && (now - zone.Resolved).TotalMinutes >= TakenZoneFadeMinutes);
            });
            while (residualZones.Count > 150)
                residualZones.RemoveAt(0);
        }

        private static bool IsResidualZoneLive(ResidualZone zone)
        {
            return zone.State == ResidualZoneState.Untested || zone.State == ResidualZoneState.Held;
        }

        private void AddResidualZoneLocked(WallState wall)
        {
            // Only walls that left visible depth without being traded through or mostly filled.
            double traded = wall.IsAsk ? wall.TradedBuy : wall.TradedSell;
            if (wall.TradedThrough || wall.PeakSize <= 0 || traded >= wall.PeakSize
                || (wall.LastSeenAboveThreshold - wall.FirstSeen).TotalSeconds < WallMinAgeSeconds)
                return;

            for (int i = 0; i < residualZones.Count; i++)
            {
                ResidualZone existing = residualZones[i];
                if (IsResidualZoneLive(existing) && existing.IsAsk == wall.IsAsk
                    && existing.Price.ApproxCompare(wall.Price) == 0)
                {
                    existing.PeakSize = Math.Max(existing.PeakSize, wall.PeakSize);
                    existing.LowSince = DateTime.MinValue;
                    return;
                }
            }
            residualZones.RemoveAll(delegate(ResidualZone old)
            {
                return old.State == ResidualZoneState.Gone && old.IsAsk == wall.IsAsk
                    && old.Price.ApproxCompare(wall.Price) == 0;
            });

            residualZones.Add(new ResidualZone
            {
                Price = wall.Price,
                IsAsk = wall.IsAsk,
                Created = wall.FirstSeen,
                Resolved = DateTime.MinValue,
                LowSince = DateTime.MinValue,
                PeakSize = wall.PeakSize,
                State = ResidualZoneState.Untested
            });
        }

        private void UpdateResidualZonesLocked(DateTime now)
        {
            for (int i = 0; i < residualZones.Count; i++)
            {
                ResidualZone zone = residualZones[i];
                if (!IsResidualZoneLive(zone))
                    continue;
                List<DepthRow> rows = zone.IsAsk ? askRows : bidRows;
                if (!IsPriceInVisibleWindowLocked(rows, zone.Price, zone.IsAsk))
                {
                    zone.LowSince = DateTime.MinValue;
                    continue;
                }

                long size = 0;
                for (int j = 0; j < rows.Count; j++)
                    if (rows[j].Price.ApproxCompare(zone.Price) == 0)
                    {
                        size = rows[j].Size;
                        break;
                    }

                if (size >= Math.Max(1, (zone.PeakSize + 1) / 2))
                {
                    zone.State = ResidualZoneState.Held;
                    zone.LowSince = DateTime.MinValue;
                    continue;
                }

                if (zone.LowSince == DateTime.MinValue)
                {
                    zone.LowSince = now;
                    continue;
                }
                if ((now - zone.LowSince).TotalMilliseconds < WallGraceMilliseconds)
                    continue;

                zone.State = zone.Traded >= zone.PeakSize * PullTradeRatio
                    ? ResidualZoneState.Taken : ResidualZoneState.Gone;
                zone.Resolved = now;
            }
        }

        private void UpdateResidualZoneTradesLocked(DateTime now, double price, double volume,
            int aggressor)
        {
            double tickSize = Instrument.MasterInstrument.TickSize;
            for (int i = 0; i < residualZones.Count; i++)
            {
                ResidualZone zone = residualZones[i];
                if (!IsResidualZoneLive(zone))
                    continue;
                if (price.ApproxCompare(zone.Price) == 0
                    && (zone.IsAsk ? aggressor > 0 : aggressor < 0))
                    zone.Traded += volume;
                if (zone.IsAsk ? price >= zone.Price + tickSize : price <= zone.Price - tickSize)
                {
                    zone.State = ResidualZoneState.Taken;
                    zone.Resolved = now;
                }
            }
        }

        private void AddWallFadeLocked(LiquidityEventType type, double price, DateTime time)
        {
            wallFades.Add(new WallFade { Type = type, Price = price, Time = time });
            while (wallFades.Count > 100)
                wallFades.RemoveAt(0);
        }

        private void AddEventLocked(LiquidityEventType type, double price, bool isAsk,
            DateTime time, string label, string keyLevel)
        {
            if (!string.IsNullOrEmpty(keyLevel)
                && label.IndexOf(keyLevel, StringComparison.OrdinalIgnoreCase) < 0)
                label += " @" + keyLevel;
            eventMarkers.Add(new EventMarker
            {
                Type = type,
                Time = time,
                Price = Instrument.MasterInstrument.RoundToTickSize(price),
                IsAsk = isAsk,
                Label = label,
                KeyLevel = keyLevel
            });
        }

        private string FindNearestKeyLevelLocked(double price, int maxTicks)
        {
            string bestName = string.Empty;
            double bestDistance = double.MaxValue;
            double maxDistance = Math.Max(0, maxTicks) * Instrument.MasterInstrument.TickSize;
            AddKeyLevelCandidate(price, priorDayHigh, "PDH", maxDistance, ref bestName, ref bestDistance);
            AddKeyLevelCandidate(price, priorDayLow, "PDL", maxDistance, ref bestName, ref bestDistance);
            AddKeyLevelCandidate(price, priorWeekHigh, "PWH", maxDistance, ref bestName, ref bestDistance);
            AddKeyLevelCandidate(price, priorWeekLow, "PWL", maxDistance, ref bestName, ref bestDistance);
            AddKeyLevelCandidate(price, priorSessionHigh, "PSH", maxDistance, ref bestName, ref bestDistance);
            AddKeyLevelCandidate(price, priorSessionLow, "PSL", maxDistance, ref bestName, ref bestDistance);
            AddKeyLevelCandidate(price, currentSessionHigh, "CSH", maxDistance, ref bestName, ref bestDistance);
            AddKeyLevelCandidate(price, currentSessionLow, "CSL", maxDistance, ref bestName, ref bestDistance);
            AddKeyLevelCandidate(price, sessionPoc, "S-POC", maxDistance, ref bestName, ref bestDistance);
            AddKeyLevelCandidate(price, sessionVah, "S-VAH", maxDistance, ref bestName, ref bestDistance);
            AddKeyLevelCandidate(price, sessionVal, "S-VAL", maxDistance, ref bestName, ref bestDistance);
            AddKeyLevelCandidate(price, weeklyPoc, "W-POC", maxDistance, ref bestName, ref bestDistance);
            AddKeyLevelCandidate(price, weeklyVah, "W-VAH", maxDistance, ref bestName, ref bestDistance);
            AddKeyLevelCandidate(price, weeklyVal, "W-VAL", maxDistance, ref bestName, ref bestDistance);
            AddKeyLevelCandidate(price, sessionCumVol > 0 ? sessionCumPv / sessionCumVol : double.NaN,
                "S-VWAP", maxDistance, ref bestName, ref bestDistance);
            AddKeyLevelCandidate(price, weeklyCumVol > 0 ? weeklyCumPv / weeklyCumVol : double.NaN,
                "W-VWAP", maxDistance, ref bestName, ref bestDistance);
            return bestName;
        }

        private void AddKeyLevelCandidate(double price, double candidate, string name, double maxDistance,
            ref string bestName, ref double bestDistance)
        {
            if (double.IsNaN(candidate) || candidate <= 0)
                return;
            double distance = Math.Abs(price - candidate);
            if (distance <= maxDistance && distance < bestDistance)
            {
                bestDistance = distance;
                bestName = name;
            }
        }

        private string FindCrossedKeyLevelLocked(double minPrice, double maxPrice, bool isUp)
        {
            double midpoint = (minPrice + maxPrice) / 2.0;
            double tickSize = Instrument.MasterInstrument.TickSize;
            int halfSpanTicks = tickSize > 0
                ? (int)Math.Ceiling((maxPrice - minPrice) / 2.0 / tickSize - 1e-9) : 0;
            string nearest = FindNearestKeyLevelLocked(midpoint, halfSpanTicks + KeyLevelProximityTicks);
            if (string.IsNullOrEmpty(nearest))
                return string.Empty;
            double level = GetKeyLevelPriceLocked(nearest);
            bool endedBeyond = !double.IsNaN(lastTradePrice)
                && (isUp ? lastTradePrice >= level : lastTradePrice <= level);
            return !double.IsNaN(level) && level >= minPrice && level <= maxPrice && endedBeyond
                ? nearest : string.Empty;
        }

        private double GetKeyLevelPriceLocked(string name)
        {
            switch (name)
            {
                case "PDH": return priorDayHigh;
                case "PDL": return priorDayLow;
                case "PWH": return priorWeekHigh;
                case "PWL": return priorWeekLow;
                case "PSH": return priorSessionHigh;
                case "PSL": return priorSessionLow;
                case "CSH": return currentSessionHigh;
                case "CSL": return currentSessionLow;
                case "S-POC": return sessionPoc;
                case "S-VAH": return sessionVah;
                case "S-VAL": return sessionVal;
                case "W-POC": return weeklyPoc;
                case "W-VAH": return weeklyVah;
                case "W-VAL": return weeklyVal;
                case "S-VWAP": return sessionCumVol > 0 ? sessionCumPv / sessionCumVol : double.NaN;
                case "W-VWAP": return weeklyCumVol > 0 ? weeklyCumPv / weeklyCumVol : double.NaN;
                default: return double.NaN;
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
                dxPullEvent = CreateDxBrush(PullEventColor, EventMarkerOpacity);
                dxFilledEvent = CreateDxBrush(FilledEventColor, EventMarkerOpacity);
                dxAbsorptionEvent = CreateDxBrush(AbsorptionEventColor, EventMarkerOpacity);
                dxSweepEvent = CreateDxBrush(SweepEventColor, EventMarkerOpacity);
                dxFailedSweepEvent = CreateDxBrush(FailedSweepEventColor, EventMarkerOpacity);
                dxHeldRunEvent = CreateDxBrush(HeldRunEventColor, EventMarkerOpacity);
                dxLargeTradeEvent = CreateDxBrush(LargeTradeEventColor, EventMarkerOpacity);
                dxHeatmapBid = CreateDxBrush(HeatmapBidColor, 100);
                dxHeatmapAsk = CreateDxBrush(HeatmapAskColor, 100);
                dxHeatmapBackground = CreateDxBrush(System.Windows.Media.Brushes.Black, 75);

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
                eventFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI",
                    FontWeight.Bold, FontStyle.Normal, EventMarkerFontSize);
                heatmapFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI",
                    FontWeight.SemiBold, FontStyle.Normal, 9f);
                wallStrokeStyle = CreateStrokeStyle(WallDashStyle);
                pullFadeStrokeStyle = CreateStrokeStyle(DashStyleHelper.Dash);
                pulledZoneStrokeStyle = CreateStrokeStyle(DashStyleHelper.Dot);
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
            if (ShowHeatmap)
                RenderHeatmap(chartScale);
            RenderReferenceLevels(chartControl, chartScale);
            RenderProfileLevels(chartControl, chartScale, firstBar, renderLastBar);
            List<float> wallLabelYs = new List<float>();
            drawnAskWallLabels.Clear();
            drawnBidWallLabels.Clear();
            if (ShowWalls)
            {
                RenderWalls(chartScale, wallLabelYs);
                RenderWallFades(chartScale);
            }
            if (ShowResidualZones)
                RenderResidualZones(chartControl, chartScale, wallLabelYs);
            if (ShowEventMarkers)
                RenderEvents(chartControl, chartScale);
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

        private void RenderWalls(ChartScale chartScale, List<float> labelYs)
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
                    if (!float.IsNaN(labelY))
                    {
                        labelYs.Add(labelY);
                        using (TextLayout layout = new TextLayout(Core.Globals.DirectWriteFactory,
                            label, wallFormat, Math.Max(150f, right - start), WallLabelFontSize + 4f))
                        {
                            RenderTarget.DrawTextLayout(new Vector2(start + 3f, labelY), layout, brush);
                        }
                        (wall.IsAsk ? drawnAskWallLabels : drawnBidWallLabels).Add(wall.Price);
                    }
                }
                brush.Opacity = 1f;
            }
        }

        private void RenderResidualZones(ChartControl chartControl, ChartScale chartScale, List<float> labelYs)
        {
            if (chartControl == null || chartScale == null || dxAskWall == null || dxBidWall == null)
                return;
            DateTime now = FeedNow();
            List<ResidualZone> zones = new List<ResidualZone>();
            lock (bookLock)
            {
                now = DetectionNowLocked(now);
                for (int i = 0; i < residualZones.Count; i++)
                {
                    ResidualZone z = residualZones[i];
                    zones.Add(new ResidualZone
                    {
                        Price = z.Price,
                        IsAsk = z.IsAsk,
                        Created = z.Created,
                        Resolved = z.Resolved,
                        PeakSize = z.PeakSize,
                        State = z.State
                    });
                }
            }

            float left = ChartPanel.X;
            float right = ChartPanel.X + ChartPanel.W;
            float labelRight = ShowHeatmap
                ? right - Math.Min(Math.Max(40, HeatmapWidth), ChartPanel.W) - 4f
                : right - 4f;
            float baseOpacity = Math.Max(0, Math.Min(100, ResidualZoneOpacity)) / 100f;
            for (int i = zones.Count - 1; i >= 0; i--)
            {
                ResidualZone zone = zones[i];
                bool live = IsResidualZoneLive(zone);
                bool pulled = zone.State == ResidualZoneState.Gone;
                bool taken = zone.State == ResidualZoneState.Taken;
                if ((taken && !ShowResolvedResidualZones) || (pulled && !ShowPulledZones)
                    || zone.Price < chartScale.MinValue || zone.Price > chartScale.MaxValue)
                    continue;

                float opacity = baseOpacity;
                if (taken)
                {
                    // Taken zones fade linearly to nothing over TakenZoneFadeMinutes.
                    double fadeMinutes = Math.Max(0, TakenZoneFadeMinutes);
                    double elapsed = (now - zone.Resolved).TotalMinutes;
                    if (fadeMinutes <= 0 || elapsed >= fadeMinutes)
                        continue;
                    opacity = baseOpacity * 0.35f * (float)(1.0 - Math.Max(0, elapsed) / fadeMinutes);
                }
                else if (pulled)
                    opacity = baseOpacity * 0.6f;

                float x0 = Math.Max(left, chartControl.GetXByTime(zone.Created));
                float x1 = taken ? Math.Min(right, chartControl.GetXByTime(zone.Resolved)) : right;
                if (x1 <= left || x0 >= right || x1 - x0 < 1f)
                    continue;

                float y = chartScale.GetYByValue(zone.Price);
                SharpDX.Direct2D1.SolidColorBrush brush = zone.IsAsk ? dxAskWall : dxBidWall;
                brush.Opacity = opacity;
                float width = zone.State == ResidualZoneState.Held ? 2f : 1.5f;
                StrokeStyle style = zone.State == ResidualZoneState.Held ? null
                    : (pulled ? pulledZoneStrokeStyle : pullFadeStrokeStyle);
                RenderTarget.DrawLine(new Vector2(x0, y), new Vector2(x1, y), brush, width, style);

                bool coveredByWall = zone.IsAsk ? drawnAskWallLabels.Contains(zone.Price)
                    : drawnBidWallLabels.Contains(zone.Price);
                if (wallFormat != null && !coveredByWall)
                {
                    string label;
                    float labelX;
                    if (live || pulled)
                    {
                        label = string.Format("{0} {1} {2}{3}", zone.IsAsk ? "ASK" : "BID",
                            zone.PeakSize, FormatAge(now - zone.Created),
                            zone.State == ResidualZoneState.Held ? " HELD" : (pulled ? " PULLED" : string.Empty));
                        labelX = float.NaN;
                    }
                    else
                    {
                        label = "TAKEN";
                        labelX = x1 + 3f;
                    }

                    using (TextLayout layout = new TextLayout(Core.Globals.DirectWriteFactory,
                        label, wallFormat, 200f, WallLabelFontSize + 4f))
                    {
                        if (float.IsNaN(labelX))
                            labelX = labelRight - layout.Metrics.Width;
                        if (labelX >= left && labelX + layout.Metrics.Width <= right)
                        {
                            float labelY = FindWallLabelY(y, labelYs, MinWallLabelSpacing);
                            if (!float.IsNaN(labelY))
                            {
                                labelYs.Add(labelY);
                                RenderTarget.DrawTextLayout(new Vector2(labelX, labelY), layout, brush);
                            }
                        }
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

        // Returns NaN when no free slot exists near the line, so crowded labels are skipped
        // instead of piling up at the panel edge.
        private float FindWallLabelY(float y, List<float> usedYs, int spacing)
        {
            float preferred = y - WallLabelFontSize - 2f;
            float top = ChartPanel.Y;
            float bottom = ChartPanel.Y + ChartPanel.H - WallLabelFontSize;
            float step = Math.Max(1, spacing);
            for (int k = 0; k <= 6; k++)
            {
                float offset = ((k + 1) / 2) * step * (k % 2 == 1 ? 1f : -1f);
                float candidate = preferred + offset;
                if (candidate < top || candidate > bottom)
                    continue;
                bool free = true;
                for (int i = 0; i < usedYs.Count; i++)
                    if (Math.Abs(candidate - usedYs[i]) < step)
                    {
                        free = false;
                        break;
                    }
                if (free)
                    return candidate;
            }
            return float.NaN;
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
            double imbalance;
            bool stale;
            lock (bookLock)
            {
                now = DetectionNowLocked(now);
                baseline = adaptiveBaseline;
                threshold = GetWallThresholdLocked();
                delta = sessionAggBuy - sessionAggSell;
                imbalance = smoothedBookImbalance;
                stale = IsBookStale();
            }
            SessionPhase phase = GetSessionPhase(now);
            string hud = PhaseLabel(phase) + " | baseline "
                + (double.IsNaN(baseline) ? "--" : baseline.ToString("F1"))
                + " | wall ≥ " + threshold
                + " | book " + (imbalance >= 0 ? "+" : string.Empty) + imbalance.ToString("F0") + "%"
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

        private void RenderHeatmap(ChartScale chartScale)
        {
            if (chartScale == null || dxHeatmapBid == null || dxHeatmapAsk == null
                || dxHeatmapBackground == null || heatmapFormat == null)
                return;

            List<HeatmapSnapshot> snapshots;
            DateTime now;
            lock (bookLock)
            {
                now = DetectionNowLocked(FeedNow());
                snapshots = new List<HeatmapSnapshot>(heatmapSnapshots);
            }

            float right = ChartPanel.X + ChartPanel.W;
            float width = Math.Min(Math.Max(40, HeatmapWidth), ChartPanel.W);
            float left = right - width;
            float historySeconds = Math.Max(1, HeatmapHistorySeconds);
            RenderTarget.FillRectangle(new RectangleF(left, ChartPanel.Y, width, ChartPanel.H),
                dxHeatmapBackground);

            string title = "L2 " + HeatmapHistorySeconds + "s";
            using (TextLayout layout = new TextLayout(Core.Globals.DirectWriteFactory,
                title, heatmapFormat, width - 8f, 14f))
                RenderTarget.DrawTextLayout(new Vector2(left + 4f, ChartPanel.Y + 3f),
                    layout, dxHeatmapBid);

            double tick = Math.Max(0.0000001, TickSize);
            // Normalize to the largest size in the retained window so thin books still show contrast.
            long sizeReference = 1;
            for (int i = 0; i < snapshots.Count; i++)
                for (int j = 0; j < snapshots[i].Levels.Count; j++)
                    sizeReference = Math.Max(sizeReference, snapshots[i].Levels[j].Size);
            double maxCoverageSeconds = Math.Max(1, FeedStaleSeconds);
            int minColumn = (int)Math.Ceiling(left);
            int maxColumn = (int)Math.Floor(right);
            int groupStart = int.MinValue;
            int groupEnd = int.MinValue;
            double groupAge = 0;
            Dictionary<double, long> groupBids = new Dictionary<double, long>();
            Dictionary<double, long> groupAsks = new Dictionary<double, long>();
            for (int i = 0; i < snapshots.Count; i++)
            {
                HeatmapSnapshot snapshot = snapshots[i];
                double age = (now - snapshot.Time).TotalSeconds;
                if (age < 0 || age > historySeconds)
                    continue;

                DateTime endTime = i + 1 < snapshots.Count ? snapshots[i + 1].Time : now;
                if (endTime > now)
                    endTime = now;
                double coverage = Math.Max(0, Math.Min(maxCoverageSeconds,
                    (endTime - snapshot.Time).TotalSeconds));
                double endAge = Math.Max(0, age - coverage);
                int colStart = Math.Min(maxColumn - 1, Math.Max(minColumn,
                    (int)Math.Floor(right - age / historySeconds * width)));
                int colEnd = Math.Min(maxColumn,
                    (int)Math.Floor(right - endAge / historySeconds * width));
                if (colEnd <= colStart)
                    colEnd = colStart + 1;

                if (colStart != groupStart)
                {
                    if (groupStart != int.MinValue)
                        RenderHeatmapColumn(chartScale, groupBids, groupAsks, groupStart,
                            Math.Min(groupEnd, colStart), groupAge, historySeconds, tick, sizeReference);
                    groupStart = colStart;
                    groupEnd = colEnd;
                    groupBids.Clear();
                    groupAsks.Clear();
                }
                else
                    groupEnd = Math.Max(groupEnd, colEnd);

                groupAge = age;
                for (int j = 0; j < snapshot.Levels.Count; j++)
                {
                    HeatmapLevel level = snapshot.Levels[j];
                    Dictionary<double, long> sizes = level.IsAsk ? groupAsks : groupBids;
                    long existing;
                    if (!sizes.TryGetValue(level.Price, out existing) || level.Size > existing)
                        sizes[level.Price] = level.Size;
                }
            }
            if (groupStart != int.MinValue)
                RenderHeatmapColumn(chartScale, groupBids, groupAsks, groupStart, groupEnd,
                    groupAge, historySeconds, tick, sizeReference);
        }

        private void RenderHeatmapColumn(ChartScale chartScale, Dictionary<double, long> bids,
            Dictionary<double, long> asks, int colStart, int colEnd, double age,
            double historySeconds, double tick, long sizeReference)
        {
            float x = colStart;
            float cellWidth = Math.Max(1, colEnd - colStart);
            float ageOpacity = (float)Math.Max(0.25, 1.0 - age / historySeconds);
            RenderHeatmapLevels(chartScale, bids, dxHeatmapBid, x, cellWidth, ageOpacity,
                tick, sizeReference);
            RenderHeatmapLevels(chartScale, asks, dxHeatmapAsk, x, cellWidth, ageOpacity,
                tick, sizeReference);
        }

        private void RenderHeatmapLevels(ChartScale chartScale, Dictionary<double, long> sizes,
            SharpDX.Direct2D1.SolidColorBrush brush, float x, float cellWidth, float ageOpacity,
            double tick, long sizeReference)
        {
            foreach (KeyValuePair<double, long> level in sizes)
            {
                float centerY = chartScale.GetYByValue(level.Key);
                if (centerY < ChartPanel.Y || centerY > ChartPanel.Y + ChartPanel.H)
                    continue;

                float halfTick = (float)Math.Abs(
                    chartScale.GetYByValue(level.Key + tick / 2.0)
                    - chartScale.GetYByValue(level.Key - tick / 2.0)) / 2f;
                float cellHeight = Math.Max(2f, halfTick * 2f);
                float intensity = (float)Math.Sqrt(Math.Min(1.0, level.Value / (double)sizeReference));
                float opacity = (HeatmapOpacity / 100f)
                    * ageOpacity * (0.2f + 0.8f * intensity);
                brush.Opacity = opacity;
                RenderTarget.FillRectangle(new RectangleF(x, centerY - cellHeight / 2f,
                    cellWidth, cellHeight), brush);
                brush.Opacity = 1f;
            }
        }

        private void RenderEvents(ChartControl chartControl, ChartScale chartScale)
        {
            if (eventFormat == null || chartControl == null || chartScale == null)
                return;
            List<EventMarker> snapshot;
            lock (bookLock)
                snapshot = new List<EventMarker>(eventMarkers);

            List<RectangleF> usedLabels = new List<RectangleF>();
            int drawn = 0;
            for (int i = snapshot.Count - 1; i >= 0 && drawn < 12; i--)
            {
                EventMarker marker = snapshot[i];
                if (!IsEventVisible(marker.Type) || marker.Price < chartScale.MinValue
                    || marker.Price > chartScale.MaxValue)
                    continue;
                float x = chartControl.GetXByTime(marker.Time);
                if (x < ChartPanel.X || x > ChartPanel.X + ChartPanel.W)
                    continue;
                SharpDX.Direct2D1.SolidColorBrush brush = GetEventBrush(marker.Type);
                if (brush == null)
                    continue;
                float y = chartScale.GetYByValue(marker.Price);
                float labelX;
                float labelY;
                float labelWidth = Math.Min(128f, ChartPanel.W);
                float labelHeight = EventMarkerFontSize + 5f;
                if (!TryFindEventLabelPosition(x, y, labelWidth, labelHeight,
                    usedLabels, out labelX, out labelY))
                    continue;
                RectangleF labelBounds = new RectangleF(labelX, labelY, labelWidth, labelHeight);
                usedLabels.Add(labelBounds);
                RenderTarget.DrawLine(new Vector2(x - 4f, y), new Vector2(x + 4f, y),
                    brush, 2f, null);
                using (TextLayout layout = new TextLayout(Core.Globals.DirectWriteFactory,
                    GetCompactEventLabel(marker), eventFormat, labelWidth, labelHeight))
                {
                    if (dxHeatmapBackground != null)
                    {
                        float backgroundOpacity = dxHeatmapBackground.Opacity;
                        dxHeatmapBackground.Opacity = backgroundOpacity * (EventMarkerOpacity / 100f);
                        RenderTarget.FillRectangle(new RectangleF(labelX - 2f, labelY,
                            layout.Metrics.Width + 4f, labelHeight), dxHeatmapBackground);
                        dxHeatmapBackground.Opacity = backgroundOpacity;
                    }
                    RenderTarget.DrawTextLayout(new Vector2(labelX, labelY), layout, brush);
                }
                drawn++;
            }
        }

        private string GetCompactEventLabel(EventMarker marker)
        {
            string label;
            switch (marker.Type)
            {
                case LiquidityEventType.Pull:
                    if (!string.IsNullOrEmpty(marker.Label)
                        && marker.Label.IndexOf("PULLS", StringComparison.Ordinal) >= 0)
                        return marker.Label;
                    label = "PULL " + (marker.IsAsk ? "ASK" : "BID");
                    break;
                case LiquidityEventType.Filled:
                    label = "BROKEN " + (marker.IsAsk ? "ASK" : "BID");
                    break;
                case LiquidityEventType.Absorption:
                    label = "ABSORB " + (marker.IsAsk ? "ASK" : "BID");
                    break;
                case LiquidityEventType.Sweep:
                    label = "SWEEP " + (marker.IsAsk ? "DOWN" : "UP");
                    break;
                case LiquidityEventType.FailedSweep:
                    label = "FAILED " + (marker.IsAsk ? "UP" : "DOWN");
                    break;
                case LiquidityEventType.HeldRun:
                    label = "HELD " + (marker.IsAsk ? "UP" : "DOWN");
                    break;
                case LiquidityEventType.LargeTrade:
                    label = "LARGE " + (marker.IsAsk ? "SELL" : "BUY");
                    break;
                default:
                    return marker.Label;
            }

            if (!string.IsNullOrEmpty(marker.KeyLevel))
                label += " " + marker.KeyLevel;
            return label;
        }

        private bool TryFindEventLabelPosition(float markerX, float markerY, float width,
            float height, List<RectangleF> usedLabels, out float labelX, out float labelY)
        {
            float top = ChartPanel.Y;
            float bottom = ChartPanel.Y + ChartPanel.H - height;
            float left = ChartPanel.X;
            float right = ChartPanel.X + ChartPanel.W;
            labelX = markerX + 6f;
            if (labelX + width > right)
                labelX = markerX - width - 6f;
            labelX = Math.Max(left, Math.Min(right - width, labelX));
            float initial = markerY - height - 2f;
            int spacing = Math.Max(MinWallLabelSpacing, EventMarkerFontSize + 2);
            int maxSteps = Math.Max(1, (int)((bottom - top) / spacing));
            for (int step = 0; step <= maxSteps; step++)
            {
                float above = initial - step * spacing;
                if (IsEventLabelPositionFree(labelX, above, width, height,
                    top, bottom, usedLabels))
                {
                    labelY = above;
                    return true;
                }
                if (step > 0)
                {
                    float below = initial + step * spacing;
                    if (IsEventLabelPositionFree(labelX, below, width, height,
                        top, bottom, usedLabels))
                    {
                        labelY = below;
                        return true;
                    }
                }
            }
            labelY = 0;
            return false;
        }

        private bool IsEventLabelPositionFree(float x, float y, float width, float height,
            float top, float bottom, List<RectangleF> usedLabels)
        {
            if (y < top || y > bottom)
                return false;
            for (int i = 0; i < usedLabels.Count; i++)
            {
                RectangleF used = usedLabels[i];
                if (x < used.Right && x + width > used.Left
                    && y < used.Bottom && y + height > used.Top)
                    return false;
            }
            return true;
        }

        private void RenderWallFades(ChartScale chartScale)
        {
            if (dxStaleWall == null || dxFilledEvent == null || chartScale == null)
                return;
            DateTime now = FeedNow();
            List<WallFade> snapshot;
            lock (bookLock)
            {
                now = DetectionNowLocked(now);
                snapshot = new List<WallFade>(wallFades);
            }

            float right = ChartPanel.X + ChartPanel.W;
            float start = WallsFromLastBar && ChartBars.ToIndex >= 0
                ? chartControlXByLastBar(right)
                : ChartPanel.X;
            if (right - start < 16f)
                start = ChartPanel.X;
            for (int i = 0; i < snapshot.Count; i++)
            {
                WallFade fade = snapshot[i];
                double age = (now - fade.Time).TotalSeconds;
                if (age < 0 || age > WallFadeDurationSeconds
                    || fade.Price < chartScale.MinValue || fade.Price > chartScale.MaxValue)
                    continue;
                float fadeOpacity = (float)Math.Max(0, 1.0 - age / WallFadeDurationSeconds);
                SharpDX.Direct2D1.SolidColorBrush brush = fade.Type == LiquidityEventType.Pull
                    ? dxStaleWall : dxFilledEvent;
                brush.Opacity = fadeOpacity;
                RenderTarget.DrawLine(new Vector2(start, chartScale.GetYByValue(fade.Price)),
                    new Vector2(right, chartScale.GetYByValue(fade.Price)), brush, WallMinWidth,
                    fade.Type == LiquidityEventType.Pull ? pullFadeStrokeStyle : null);
                brush.Opacity = 1f;
            }
        }

        private bool IsEventVisible(LiquidityEventType type)
        {
            switch (type)
            {
                case LiquidityEventType.Pull: return ShowPullEvents;
                case LiquidityEventType.Filled: return ShowFilledEvents;
                case LiquidityEventType.Absorption: return ShowAbsorptionEvents;
                case LiquidityEventType.Sweep: return ShowSweepEvents;
                case LiquidityEventType.FailedSweep: return ShowFailedSweepEvents;
                case LiquidityEventType.HeldRun: return ShowHeldRunEvents;
                case LiquidityEventType.LargeTrade: return ShowLargeTradeEvents;
                default: return false;
            }
        }

        private SharpDX.Direct2D1.SolidColorBrush GetEventBrush(LiquidityEventType type)
        {
            switch (type)
            {
                case LiquidityEventType.Pull: return dxPullEvent;
                case LiquidityEventType.Filled: return dxFilledEvent;
                case LiquidityEventType.Absorption: return dxAbsorptionEvent;
                case LiquidityEventType.Sweep: return dxSweepEvent;
                case LiquidityEventType.FailedSweep: return dxFailedSweepEvent;
                case LiquidityEventType.HeldRun: return dxHeldRunEvent;
                case LiquidityEventType.LargeTrade: return dxLargeTradeEvent;
                default: return null;
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
            SafeDispose(ref pullFadeStrokeStyle);
            SafeDispose(ref pulledZoneStrokeStyle);
            SafeDispose(ref referenceStrokeStyle);
            SafeDispose(ref profileStrokeStyle);
            SafeDispose(ref hudFormat);
            SafeDispose(ref wallFormat);
            SafeDispose(ref levelLabelFormat);
            SafeDispose(ref eventFormat);
            SafeDispose(ref heatmapFormat);
            SafeDispose(ref dxAskWall);
            SafeDispose(ref dxBidWall);
            SafeDispose(ref dxStaleWall);
            SafeDispose(ref dxHudText);
            SafeDispose(ref dxHudBackground);
            SafeDispose(ref dxBullShade);
            SafeDispose(ref dxBearShade);
            SafeDispose(ref dxPullEvent);
            SafeDispose(ref dxFilledEvent);
            SafeDispose(ref dxAbsorptionEvent);
            SafeDispose(ref dxSweepEvent);
            SafeDispose(ref dxFailedSweepEvent);
            SafeDispose(ref dxHeldRunEvent);
            SafeDispose(ref dxLargeTradeEvent);
            SafeDispose(ref dxHeatmapBid);
            SafeDispose(ref dxHeatmapAsk);
            SafeDispose(ref dxHeatmapBackground);
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
        [Display(Name = "Show Heatmap", Order = 1, GroupName = "2. Visualization")]
        public bool ShowHeatmap { get; set; }

        [NinjaScriptProperty]
        [Range(10, 60)]
        [Display(Name = "Heatmap History Seconds", Order = 2, GroupName = "2. Visualization")]
        public int HeatmapHistorySeconds { get; set; }

        [NinjaScriptProperty]
        [Range(40, 400)]
        [Display(Name = "Heatmap Width", Order = 3, GroupName = "2. Visualization")]
        public int HeatmapWidth { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Heatmap Opacity", Order = 4, GroupName = "2. Visualization")]
        public int HeatmapOpacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Heatmap Bid Color", Order = 5, GroupName = "2. Visualization")]
        public System.Windows.Media.Brush HeatmapBidColor { get; set; }
        [Browsable(false)]
        public string HeatmapBidColorSerializable
        {
            get { return Serialize.BrushToString(HeatmapBidColor); }
            set { HeatmapBidColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Heatmap Ask Color", Order = 6, GroupName = "2. Visualization")]
        public System.Windows.Media.Brush HeatmapAskColor { get; set; }
        [Browsable(false)]
        public string HeatmapAskColorSerializable
        {
            get { return Serialize.BrushToString(HeatmapAskColor); }
            set { HeatmapAskColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Show Residual Liquidity Zones", Order = 7, GroupName = "2. Visualization")]
        public bool ShowResidualZones { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Taken Zones", Order = 8, GroupName = "2. Visualization")]
        public bool ShowResolvedResidualZones { get; set; }

        [NinjaScriptProperty]
        [Range(5, 1440)]
        [Display(Name = "Residual Zone Max Minutes", Order = 9, GroupName = "2. Visualization")]
        public int ResidualZoneMaxMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Residual Zone Opacity", Order = 10, GroupName = "2. Visualization")]
        public int ResidualZoneOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(0, 120)]
        [Display(Name = "Taken Zone Fade Minutes", Order = 11, GroupName = "2. Visualization")]
        public int TakenZoneFadeMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Pulled Zones", Order = 12, GroupName = "2. Visualization")]
        public bool ShowPulledZones { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Event Markers", Order = 1, GroupName = "2. Event Detection")]
        public bool ShowEventMarkers { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Pull Events", Order = 2, GroupName = "2. Event Detection")]
        public bool ShowPullEvents { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Filled / Broken Events", Order = 3, GroupName = "2. Event Detection")]
        public bool ShowFilledEvents { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Absorption Events", Order = 4, GroupName = "2. Event Detection")]
        public bool ShowAbsorptionEvents { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Sweep Events", Order = 5, GroupName = "2. Event Detection")]
        public bool ShowSweepEvents { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Failed Sweep Events", Order = 6, GroupName = "2. Event Detection")]
        public bool ShowFailedSweepEvents { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Held Run Events", Order = 7, GroupName = "2. Event Detection")]
        public bool ShowHeldRunEvents { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Large Trade Events", Order = 8, GroupName = "2. Event Detection")]
        public bool ShowLargeTradeEvents { get; set; }

        [NinjaScriptProperty]
        [Range(6, 20)]
        [Display(Name = "Event Marker Font Size", Order = 9, GroupName = "2. Event Detection")]
        public int EventMarkerFontSize { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Event Marker Opacity", Order = 10, GroupName = "2. Event Detection")]
        public int EventMarkerOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1440)]
        [Display(Name = "Event Marker Retention Minutes", Order = 11, GroupName = "2. Event Detection")]
        public int EventMarkerRetentionMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "Wall Fade Duration Seconds", Order = 12, GroupName = "2. Event Detection")]
        public int WallFadeDurationSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10000)]
        [Display(Name = "Event Grace Milliseconds", Order = 13, GroupName = "2. Event Detection")]
        public int EventGraceMilliseconds { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, 1.0)]
        [Display(Name = "Pull Trade Ratio", Order = 14, GroupName = "2. Event Detection")]
        public double PullTradeRatio { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 100.0)]
        [Display(Name = "Absorption Trade Multiple", Order = 15, GroupName = "2. Event Detection")]
        public double AbsorptionTradeMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(1, 30)]
        [Display(Name = "Sweep Window Seconds", Order = 16, GroupName = "2. Event Detection")]
        public int SweepWindowSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Sweep Minimum Ticks", Order = 17, GroupName = "2. Event Detection")]
        public int SweepMinimumTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Sweep Minimum Volume", Order = 18, GroupName = "2. Event Detection")]
        public int SweepMinimumVolume { get; set; }

        [NinjaScriptProperty]
        [Range(1, 120)]
        [Display(Name = "Failed Run Window Seconds", Order = 19, GroupName = "2. Event Detection")]
        public int FailedRunWindowSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Large Trade Minimum Contracts", Order = 20, GroupName = "2. Event Detection")]
        public int LargeTradeMinimumContracts { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "Key Level Proximity Ticks", Order = 21, GroupName = "2. Event Detection")]
        public int KeyLevelProximityTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 3600)]
        [Display(Name = "Event Cooldown Seconds", Order = 22, GroupName = "2. Event Detection")]
        public int EventCooldownSeconds { get; set; }

        [XmlIgnore]
        [Display(Name = "Pull Event Color", Order = 23, GroupName = "2. Event Detection")]
        public System.Windows.Media.Brush PullEventColor { get; set; }
        [Browsable(false)]
        public string PullEventColorSerializable
        {
            get { return Serialize.BrushToString(PullEventColor); }
            set { PullEventColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Filled Event Color", Order = 24, GroupName = "2. Event Detection")]
        public System.Windows.Media.Brush FilledEventColor { get; set; }
        [Browsable(false)]
        public string FilledEventColorSerializable
        {
            get { return Serialize.BrushToString(FilledEventColor); }
            set { FilledEventColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Absorption Event Color", Order = 25, GroupName = "2. Event Detection")]
        public System.Windows.Media.Brush AbsorptionEventColor { get; set; }
        [Browsable(false)]
        public string AbsorptionEventColorSerializable
        {
            get { return Serialize.BrushToString(AbsorptionEventColor); }
            set { AbsorptionEventColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Sweep Event Color", Order = 26, GroupName = "2. Event Detection")]
        public System.Windows.Media.Brush SweepEventColor { get; set; }
        [Browsable(false)]
        public string SweepEventColorSerializable
        {
            get { return Serialize.BrushToString(SweepEventColor); }
            set { SweepEventColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Failed Sweep Event Color", Order = 27, GroupName = "2. Event Detection")]
        public System.Windows.Media.Brush FailedSweepEventColor { get; set; }
        [Browsable(false)]
        public string FailedSweepEventColorSerializable
        {
            get { return Serialize.BrushToString(FailedSweepEventColor); }
            set { FailedSweepEventColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Held Run Event Color", Order = 28, GroupName = "2. Event Detection")]
        public System.Windows.Media.Brush HeldRunEventColor { get; set; }
        [Browsable(false)]
        public string HeldRunEventColorSerializable
        {
            get { return Serialize.BrushToString(HeldRunEventColor); }
            set { HeldRunEventColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Large Trade Event Color", Order = 29, GroupName = "2. Event Detection")]
        public System.Windows.Media.Brush LargeTradeEventColor { get; set; }
        [Browsable(false)]
        public string LargeTradeEventColorSerializable
        {
            get { return Serialize.BrushToString(LargeTradeEventColor); }
            set { LargeTradeEventColor = Serialize.StringToBrush(value); }
        }

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
