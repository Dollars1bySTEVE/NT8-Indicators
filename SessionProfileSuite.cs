#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
#endregion

// NinjaTrader 8 requires custom enums declared OUTSIDE all namespaces
// so the auto-generated partial class code can resolve them cleanly.
// See: forum.ninjatrader.com threads #1182932, #95909, #1046853
public enum SessionProfileSource
{
    TickBased,
    BarDistributed
}

public enum SessionProfileKind
{
    None,
    Asia,
    London,
    NewYork
}

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SessionProfileSuite : Indicator
    {
        private const float LabelHorizontalPadding = 4f;
        private const float LabelVerticalOffset = 8f;
        private const float LabelLayoutWidth = 220f;
        private const int MaxClosedSessions = 250;
        private const int MaxMidnightOpenRecords = 60;

        private sealed class SessionWindowInfo
        {
            public SessionProfileKind Kind;
            public string Name;
            public DateTime StartEt;
            public DateTime EndEt;
            public DateTime StartChart;
            public DateTime EndChart;
        }

        private sealed class SessionContribution
        {
            public long Volume;
            public double PriceVolume;
            public double PriceSquaredVolume;
            public Dictionary<double, long> PriceBuckets = new Dictionary<double, long>();
        }

        private sealed class SessionState
        {
            public SessionWindowInfo Window;
            public Dictionary<double, long> Profile = new Dictionary<double, long>();
            public long TotalProfileVolume;
            public double CumPriceVolume;
            public double CumVolume;
            public double CumPriceSquaredVolume;
            public double Poc = double.NaN;
            public double Vah = double.NaN;
            public double Val = double.NaN;
            public double Vwap = double.NaN;
            public double StdDev;
            public double LastPrice = double.NaN;
            public DateTime LastUpdateChart;
            public DateTime LastUpdateEt;
            public int StartBarIndex = -1;
            public int LastBarIndex = -1;
            public bool OpeningBalanceInitialized;
            public bool OpeningBalanceComplete;
            public double OpeningBalanceHigh = double.NaN;
            public double OpeningBalanceLow = double.NaN;
            public DateTime OpeningBalanceEndEt;
            public int DevelopingPrimaryBarIndex = -1;
            public SessionContribution DevelopingPrimaryContribution;
        }

        private sealed class ClosedSessionRecord
        {
            public SessionProfileKind Kind;
            public string Name;
            public DateTime StartChart;
            public DateTime EndChart;
            public double Poc = double.NaN;
            public double Vah = double.NaN;
            public double Val = double.NaN;
            public bool HasOpeningBalance;
            public double OpeningBalanceHigh = double.NaN;
            public double OpeningBalanceLow = double.NaN;
        }

        private sealed class MidnightOpenRecord
        {
            public DateTime DateEt;
            public DateTime StartChart;
            public DateTime EndChart;
            public double Price;
        }

        private TimeZoneInfo easternTimeZone;
        private TimeZoneInfo londonTimeZone;
        private TimeZoneInfo sourceTimeZone;

        private SessionState currentSession;
        private readonly List<ClosedSessionRecord> closedSessions = new List<ClosedSessionRecord>();
        private readonly List<MidnightOpenRecord> midnightOpenRecords = new List<MidnightOpenRecord>();

        private Series<double> sessionVwapSeries;
        private Series<double> sessionUpperBandSeries;
        private Series<double> sessionLowerBandSeries;

        private SharpDX.Direct2D1.SolidColorBrush dxHistogram;
        private SharpDX.Direct2D1.SolidColorBrush dxPocRow;
        private SharpDX.Direct2D1.SolidColorBrush dxPoc;
        private SharpDX.Direct2D1.SolidColorBrush dxVah;
        private SharpDX.Direct2D1.SolidColorBrush dxVal;
        private SharpDX.Direct2D1.SolidColorBrush dxVwapAbove;
        private SharpDX.Direct2D1.SolidColorBrush dxVwapBelow;
        private SharpDX.Direct2D1.SolidColorBrush dxBandUpper;
        private SharpDX.Direct2D1.SolidColorBrush dxBandLower;
        private SharpDX.Direct2D1.SolidColorBrush dxBandUpperFill;
        private SharpDX.Direct2D1.SolidColorBrush dxBandLowerFill;
        private SharpDX.Direct2D1.SolidColorBrush dxObHigh;
        private SharpDX.Direct2D1.SolidColorBrush dxObMid;
        private SharpDX.Direct2D1.SolidColorBrush dxObLow;
        private SharpDX.Direct2D1.SolidColorBrush dxMidnightOpen;
        private TextFormat labelFormat;
        private StrokeStyle dashedStrokeStyle;
        private StrokeStyle vwapStrokeStyle;
        private StrokeStyle openingBalanceStrokeStyle;
        private StrokeStyle midnightOpenStrokeStyle;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Standalone ET session profile, VWAP, opening balance, and midnight open suite for index futures.";
                Name = "SessionProfileSuite";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = true;

                MasterEnable = true;
                ProfileSource = SessionProfileSource.TickBased;

                EnableAsia = true;
                AsiaStartHour = 18;
                AsiaStartMinute = 0;
                AsiaEndHour = 3;
                AsiaEndMinute = 0;

                EnableLondon = true;
                UseDynamicLondonOpen = true;
                LondonStartHour = 3;
                LondonStartMinute = 0;
                LondonEndHour = 9;
                LondonEndMinute = 30;

                EnableNewYork = true;
                NewYorkStartHour = 9;
                NewYorkStartMinute = 30;
                NewYorkEndHour = 16;
                NewYorkEndMinute = 0;

                ShowHistogram = true;
                HistogramMaxWidth = 140;
                TicksPerRow = 1;
                ValueAreaPercent = 70;
                ShowPoc = true;
                ShowVah = true;
                ShowVal = true;
                ProjectClosedSessions = true;

                ShowVwap = true;
                ShowBands = true;
                BandOpacity = 20;
                VwapLineWidth = 2;
                VwapLineStyle = DashStyleHelper.Solid;

                ShowOpeningBalance = true;
                OpeningBalanceMinutes = 60;
                ProjectClosedOpeningBalance = true;
                OpeningBalanceLineWidth = 1;
                OpeningBalanceLineStyle = DashStyleHelper.Solid;

                ShowMidnightOpen = true;
                MidnightOpenLineWidth = 1;
                MidnightOpenLineStyle = DashStyleHelper.Dot;

                ProfileLineWidth = 2;
                LabelFontSize = 10;
                PocColor = System.Windows.Media.Brushes.Gold;
                VahColor = System.Windows.Media.Brushes.LimeGreen;
                ValColor = System.Windows.Media.Brushes.OrangeRed;
                HistogramColor = System.Windows.Media.Brushes.SlateGray;
                PocRowColor = System.Windows.Media.Brushes.Goldenrod;
                VwapAboveColor = System.Windows.Media.Brushes.LimeGreen;
                VwapBelowColor = System.Windows.Media.Brushes.Crimson;
                BandUpperColor = System.Windows.Media.Brushes.DodgerBlue;
                BandLowerColor = System.Windows.Media.Brushes.DodgerBlue;
                OpeningBalanceHighColor = System.Windows.Media.Brushes.DeepSkyBlue;
                OpeningBalanceMidColor = System.Windows.Media.Brushes.LightSkyBlue;
                OpeningBalanceLowColor = System.Windows.Media.Brushes.MediumPurple;
                MidnightOpenColor = System.Windows.Media.Brushes.WhiteSmoke;
            }
            else if (State == State.Configure)
            {
                if (ProfileSource == SessionProfileSource.TickBased)
                    AddDataSeries(BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                easternTimeZone = FindTimeZone("Eastern Standard Time", TimeZoneInfo.Local);
                londonTimeZone = FindTimeZone("GMT Standard Time", easternTimeZone ?? TimeZoneInfo.Local);
                sourceTimeZone = Bars != null && Bars.TradingHours != null && Bars.TradingHours.TimeZoneInfo != null
                    ? Bars.TradingHours.TimeZoneInfo
                    : TimeZoneInfo.Local;

                sessionVwapSeries = new Series<double>(this, MaximumBarsLookBack.Infinite);
                sessionUpperBandSeries = new Series<double>(this, MaximumBarsLookBack.Infinite);
                sessionLowerBandSeries = new Series<double>(this, MaximumBarsLookBack.Infinite);
            }
            else if (State == State.Terminated)
            {
                DisposeResources();
            }
        }

        protected override void OnBarUpdate()
        {
            if (!MasterEnable || State < State.DataLoaded)
                return;

            if (BarsInProgress == 0)
            {
                if (CurrentBar < 0)
                    return;

                ProcessPrimarySeriesBar();
                return;
            }

            if (ProfileSource == SessionProfileSource.TickBased && BarsInProgress == 1)
            {
                if (CurrentBars.Length <= 1 || CurrentBars[1] < 0)
                    return;

                ProcessTickSeriesBar();
            }
        }

        private void ProcessPrimarySeriesBar()
        {
            DateTime chartTime = Times[0][0];
            SessionWindowInfo window = GetSessionWindow(chartTime);
            EnsureSession(window, chartTime);

            CaptureMidnightOpen(chartTime);

            if (currentSession != null)
            {
                currentSession.LastUpdateChart = chartTime;
                currentSession.LastUpdateEt = ToEasternTime(chartTime);

                if (currentSession.StartBarIndex < 0)
                    currentSession.StartBarIndex = CurrentBar;

                currentSession.LastBarIndex = CurrentBar;

                UpdateOpeningBalance(currentSession, High[0], Low[0], currentSession.LastUpdateEt);

                if (ProfileSource == SessionProfileSource.BarDistributed)
                    UpdateBarDistributedProfile(currentSession);

                sessionVwapSeries[0] = currentSession.Vwap;
                sessionUpperBandSeries[0] = currentSession.Vwap + currentSession.StdDev;
                sessionLowerBandSeries[0] = currentSession.Vwap - currentSession.StdDev;
            }
            else
            {
                sessionVwapSeries[0] = double.NaN;
                sessionUpperBandSeries[0] = double.NaN;
                sessionLowerBandSeries[0] = double.NaN;
            }
        }

        private void ProcessTickSeriesBar()
        {
            DateTime chartTime = Times[1][0];
            SessionWindowInfo window = GetSessionWindow(chartTime);
            EnsureSession(window, chartTime);

            if (currentSession == null)
                return;

            double price = Instrument.MasterInstrument.RoundToTickSize(Closes[1][0]);
            long volume = Math.Max(0L, Convert.ToInt64(Volumes[1][0]));
            if (volume <= 0)
                return;

            SessionContribution contribution = new SessionContribution();
            contribution.Volume = volume;
            contribution.PriceVolume = price * volume;
            contribution.PriceSquaredVolume = price * price * volume;
            contribution.PriceBuckets[price] = volume;

            ApplyContribution(currentSession, contribution);
            currentSession.LastPrice = price;
            currentSession.LastUpdateChart = chartTime;
            currentSession.LastUpdateEt = ToEasternTime(chartTime);
        }

        private void UpdateBarDistributedProfile(SessionState session)
        {
            SessionContribution priorContribution = session.DevelopingPrimaryContribution;
            if (session.DevelopingPrimaryBarIndex == CurrentBar && priorContribution != null)
                RemoveContribution(session, priorContribution);

            SessionContribution contribution = BuildBarDistributedContribution();
            ApplyContribution(session, contribution);
            session.LastPrice = Close[0];
            session.DevelopingPrimaryBarIndex = CurrentBar;
            session.DevelopingPrimaryContribution = contribution;
        }

        private SessionContribution BuildBarDistributedContribution()
        {
            SessionContribution contribution = new SessionContribution();

            long volume = Math.Max(0L, Convert.ToInt64(Math.Round(Volume[0])));
            if (volume <= 0)
                return contribution;

            double typicalPrice = (High[0] + Low[0] + Close[0]) / 3.0;
            contribution.Volume = volume;
            contribution.PriceVolume = typicalPrice * volume;
            contribution.PriceSquaredVolume = typicalPrice * typicalPrice * volume;

            double low = Instrument.MasterInstrument.RoundToTickSize(Math.Min(Low[0], High[0]));
            double high = Instrument.MasterInstrument.RoundToTickSize(Math.Max(High[0], Low[0]));
            int tickCount = Math.Max(1, (int)Math.Round((high - low) / TickSize) + 1);
            long baseVolume = volume / tickCount;
            long remainder = volume % tickCount;

            for (int i = 0; i < tickCount; i++)
            {
                long allocated = baseVolume + (i < remainder ? 1L : 0L);
                if (allocated <= 0)
                    continue;

                double price = Instrument.MasterInstrument.RoundToTickSize(low + (i * TickSize));
                if (contribution.PriceBuckets.ContainsKey(price))
                    contribution.PriceBuckets[price] += allocated;
                else
                    contribution.PriceBuckets[price] = allocated;
            }

            return contribution;
        }

        private void ApplyContribution(SessionState session, SessionContribution contribution)
        {
            if (session == null || contribution == null || contribution.Volume <= 0)
                return;

            session.CumPriceVolume += contribution.PriceVolume;
            session.CumVolume += contribution.Volume;
            session.CumPriceSquaredVolume += contribution.PriceSquaredVolume;
            session.TotalProfileVolume += contribution.Volume;

            foreach (KeyValuePair<double, long> bucket in contribution.PriceBuckets)
                AddProfileVolume(session.Profile, bucket.Key, bucket.Value);

            UpdateDerivedSessionLevels(session);
        }

        private void RemoveContribution(SessionState session, SessionContribution contribution)
        {
            if (session == null || contribution == null || contribution.Volume <= 0)
                return;

            session.CumPriceVolume -= contribution.PriceVolume;
            session.CumVolume -= contribution.Volume;
            session.CumPriceSquaredVolume -= contribution.PriceSquaredVolume;
            session.TotalProfileVolume -= contribution.Volume;

            foreach (KeyValuePair<double, long> bucket in contribution.PriceBuckets)
                RemoveProfileVolume(session.Profile, bucket.Key, bucket.Value);

            UpdateDerivedSessionLevels(session);
        }

        private void AddProfileVolume(Dictionary<double, long> profile, double price, long volume)
        {
            if (profile == null || volume <= 0)
                return;

            double key = Instrument.MasterInstrument.RoundToTickSize(price);
            long existing;
            if (profile.TryGetValue(key, out existing))
                profile[key] = existing + volume;
            else
                profile[key] = volume;
        }

        private void RemoveProfileVolume(Dictionary<double, long> profile, double price, long volume)
        {
            if (profile == null || volume <= 0)
                return;

            double key = Instrument.MasterInstrument.RoundToTickSize(price);
            long existing;
            if (!profile.TryGetValue(key, out existing))
                return;

            long remaining = existing - volume;
            if (remaining > 0)
                profile[key] = remaining;
            else
                profile.Remove(key);
        }

        private void UpdateDerivedSessionLevels(SessionState session)
        {
            if (session == null)
                return;

            session.Vwap = session.CumVolume > 0 ? session.CumPriceVolume / session.CumVolume : double.NaN;

            if (session.CumVolume > 0)
            {
                double variance = (session.CumPriceSquaredVolume / session.CumVolume) - (session.Vwap * session.Vwap);
                session.StdDev = Math.Sqrt(Math.Max(0d, variance));
            }
            else
            {
                session.StdDev = 0d;
            }

            ComputeValueArea(session.Profile, out session.Poc, out session.Vah, out session.Val);
        }

        private void ComputeValueArea(Dictionary<double, long> profile, out double poc, out double vah, out double val)
        {
            poc = double.NaN;
            vah = double.NaN;
            val = double.NaN;

            if (profile == null || profile.Count == 0)
                return;

            List<double> prices = profile.Keys.OrderBy(p => p).ToList();
            List<long> volumes = new List<long>(prices.Count);
            long totalVolume = 0L;
            int pocIndex = 0;
            long maxVolume = long.MinValue;

            for (int i = 0; i < prices.Count; i++)
            {
                long volume = profile[prices[i]];
                volumes.Add(volume);
                totalVolume += volume;
                if (volume > maxVolume)
                {
                    maxVolume = volume;
                    pocIndex = i;
                }
            }

            if (totalVolume <= 0)
                return;

            // Standard value-area expansion:
            // 1) Start at the POC (the highest-volume price).
            // 2) Compare the next untouched bin above and below the current range.
            // 3) Add whichever side has the larger adjacent volume.
            // 4) Repeat until the accumulated volume reaches the configured percentage.
            double targetVolume = totalVolume * (ValueAreaPercent / 100.0);
            double accumulated = volumes[pocIndex];
            int lowIndex = pocIndex;
            int highIndex = pocIndex;

            while (accumulated < targetVolume && (lowIndex > 0 || highIndex < prices.Count - 1))
            {
                long downVolume = lowIndex > 0 ? volumes[lowIndex - 1] : -1L;
                long upVolume = highIndex < prices.Count - 1 ? volumes[highIndex + 1] : -1L;

                if (upVolume >= downVolume && highIndex < prices.Count - 1)
                {
                    highIndex++;
                    accumulated += volumes[highIndex];
                }
                else if (lowIndex > 0)
                {
                    lowIndex--;
                    accumulated += volumes[lowIndex];
                }
                else if (highIndex < prices.Count - 1)
                {
                    highIndex++;
                    accumulated += volumes[highIndex];
                }
                else
                {
                    break;
                }
            }

            poc = prices[pocIndex];
            val = prices[lowIndex];
            vah = prices[highIndex];
        }

        private void EnsureSession(SessionWindowInfo nextWindow, DateTime chartTime)
        {
            if (currentSession != null)
            {
                bool sameWindow = nextWindow != null
                    && currentSession.Window.Kind == nextWindow.Kind
                    && currentSession.Window.StartEt == nextWindow.StartEt
                    && currentSession.Window.EndEt == nextWindow.EndEt;

                if (!sameWindow)
                {
                    FinalizeSession(currentSession);
                    currentSession = null;
                }
            }

            if (nextWindow == null)
                return;

            if (currentSession == null)
            {
                currentSession = new SessionState();
                currentSession.Window = nextWindow;
                currentSession.LastUpdateChart = chartTime;
                currentSession.LastUpdateEt = ToEasternTime(chartTime);
                currentSession.OpeningBalanceEndEt = nextWindow.StartEt.AddMinutes(OpeningBalanceMinutes);
            }
        }

        private void FinalizeSession(SessionState session)
        {
            if (session == null)
                return;

            UpdateDerivedSessionLevels(session);

            ClosedSessionRecord record = new ClosedSessionRecord();
            record.Kind = session.Window.Kind;
            record.Name = session.Window.Name;
            record.StartChart = session.Window.StartChart;
            record.EndChart = session.Window.EndChart;
            record.Poc = session.Poc;
            record.Vah = session.Vah;
            record.Val = session.Val;
            record.HasOpeningBalance = session.OpeningBalanceInitialized;
            record.OpeningBalanceHigh = session.OpeningBalanceHigh;
            record.OpeningBalanceLow = session.OpeningBalanceLow;

            closedSessions.Add(record);
            if (closedSessions.Count > MaxClosedSessions)
                closedSessions.RemoveAt(0);
        }

        private void UpdateOpeningBalance(SessionState session, double barHigh, double barLow, DateTime barTimeEt)
        {
            if (!ShowOpeningBalance || session == null)
                return;

            if (!session.OpeningBalanceInitialized)
            {
                session.OpeningBalanceInitialized = true;
                session.OpeningBalanceHigh = barHigh;
                session.OpeningBalanceLow = barLow;
            }

            if (!session.OpeningBalanceComplete && barTimeEt < session.OpeningBalanceEndEt)
            {
                session.OpeningBalanceHigh = Math.Max(session.OpeningBalanceHigh, barHigh);
                session.OpeningBalanceLow = Math.Min(session.OpeningBalanceLow, barLow);
            }
            else
            {
                session.OpeningBalanceComplete = true;
            }
        }

        private void CaptureMidnightOpen(DateTime chartTime)
        {
            DateTime etTime = ToEasternTime(chartTime);
            DateTime etDate = etTime.Date;

            double captureWindowMinutes = BarsPeriod != null && BarsPeriod.BarsPeriodType == BarsPeriodType.Minute
                ? Math.Max(1d, BarsPeriod.Value)
                : 5d;

            if (etTime.TimeOfDay >= TimeSpan.FromMinutes(captureWindowMinutes))
                return;

            MidnightOpenRecord lastRecord = midnightOpenRecords.Count > 0 ? midnightOpenRecords[midnightOpenRecords.Count - 1] : null;
            if (lastRecord != null && lastRecord.DateEt == etDate)
                return;

            MidnightOpenRecord record = new MidnightOpenRecord();
            record.DateEt = etDate;
            record.StartChart = ToChartTime(etDate);
            record.EndChart = ToChartTime(etDate.AddDays(1));
            record.Price = Open[0];

            midnightOpenRecords.Add(record);
            if (midnightOpenRecords.Count > MaxMidnightOpenRecords)
                midnightOpenRecords.RemoveAt(0);
        }

        private SessionWindowInfo GetSessionWindow(DateTime chartTime)
        {
            DateTime etTime = ToEasternTime(chartTime);
            SessionWindowInfo asia = EnableAsia ? BuildWindow(SessionProfileKind.Asia, "Asia", etTime, GetTimeSpan(AsiaStartHour, AsiaStartMinute), GetTimeSpan(AsiaEndHour, AsiaEndMinute)) : null;
            if (asia != null)
                return asia;

            TimeSpan londonStart = UseDynamicLondonOpen ? GetDynamicLondonStartTime(etTime.Date) : GetTimeSpan(LondonStartHour, LondonStartMinute);
            SessionWindowInfo london = EnableLondon ? BuildWindow(SessionProfileKind.London, "London", etTime, londonStart, GetTimeSpan(LondonEndHour, LondonEndMinute)) : null;
            if (london != null)
                return london;

            SessionWindowInfo newYork = EnableNewYork ? BuildWindow(SessionProfileKind.NewYork, "NY", etTime, GetTimeSpan(NewYorkStartHour, NewYorkStartMinute), GetTimeSpan(NewYorkEndHour, NewYorkEndMinute)) : null;
            return newYork;
        }

        private SessionWindowInfo BuildWindow(SessionProfileKind kind, string name, DateTime etTime, TimeSpan startTime, TimeSpan endTime)
        {
            bool spansMidnight = endTime <= startTime;
            DateTime startEt;
            DateTime endEt;

            if (spansMidnight)
            {
                bool inWindow = etTime.TimeOfDay >= startTime || etTime.TimeOfDay < endTime;
                if (!inWindow)
                    return null;

                DateTime anchorDate = etTime.TimeOfDay >= startTime ? etTime.Date : etTime.Date.AddDays(-1);
                startEt = anchorDate.Date.Add(startTime);
                endEt = anchorDate.Date.AddDays(1).Add(endTime);
            }
            else
            {
                if (etTime.TimeOfDay < startTime || etTime.TimeOfDay >= endTime)
                    return null;

                startEt = etTime.Date.Add(startTime);
                endEt = etTime.Date.Add(endTime);
            }

            SessionWindowInfo window = new SessionWindowInfo();
            window.Kind = kind;
            window.Name = name;
            window.StartEt = startEt;
            window.EndEt = endEt;
            window.StartChart = ToChartTime(startEt);
            window.EndChart = ToChartTime(endEt);
            return window;
        }

        private TimeSpan GetDynamicLondonStartTime(DateTime etDate)
        {
            try
            {
                DateTime londonOpenLocal = new DateTime(etDate.Year, etDate.Month, etDate.Day, 8, 0, 0, DateTimeKind.Unspecified);
                DateTime londonOpenEt = TimeZoneInfo.ConvertTime(londonOpenLocal, londonTimeZone, easternTimeZone);
                return londonOpenEt.TimeOfDay;
            }
            catch
            {
                return GetTimeSpan(LondonStartHour, LondonStartMinute);
            }
        }

        private DateTime ToEasternTime(DateTime chartTime)
        {
            // Session detection is always done in US Eastern time so the indicator behaves
            // the same regardless of the user's local PC/chart timezone or the data series timezone.
            DateTime normalized = DateTime.SpecifyKind(chartTime, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTime(normalized, sourceTimeZone, easternTimeZone);
        }

        private DateTime ToChartTime(DateTime etTime)
        {
            DateTime normalized = DateTime.SpecifyKind(etTime, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTime(normalized, easternTimeZone, sourceTimeZone);
        }

        private TimeSpan GetTimeSpan(int hour, int minute)
        {
            return new TimeSpan(Math.Max(0, Math.Min(23, hour)), Math.Max(0, Math.Min(59, minute)), 0);
        }

        private TimeZoneInfo FindTimeZone(string id, TimeZoneInfo fallback)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch
            {
                return fallback;
            }
        }

        public override void OnRenderTargetChanged()
        {
            DisposeResources();

            if (RenderTarget == null)
                return;

            CreateResources();
        }

        private void CreateResources()
        {
            try
            {
                dxHistogram = CreateDxBrush(HistogramColor, 1f);
                dxPocRow = CreateDxBrush(PocRowColor, 1f);
                dxPoc = CreateDxBrush(PocColor, 1f);
                dxVah = CreateDxBrush(VahColor, 1f);
                dxVal = CreateDxBrush(ValColor, 1f);
                dxVwapAbove = CreateDxBrush(VwapAboveColor, 1f);
                dxVwapBelow = CreateDxBrush(VwapBelowColor, 1f);
                dxBandUpper = CreateDxBrush(BandUpperColor, 1f);
                dxBandLower = CreateDxBrush(BandLowerColor, 1f);
                dxBandUpperFill = CreateDxBrush(BandUpperColor, BandOpacity / 100f);
                dxBandLowerFill = CreateDxBrush(BandLowerColor, BandOpacity / 100f);
                dxObHigh = CreateDxBrush(OpeningBalanceHighColor, 1f);
                dxObMid = CreateDxBrush(OpeningBalanceMidColor, 1f);
                dxObLow = CreateDxBrush(OpeningBalanceLowColor, 1f);
                dxMidnightOpen = CreateDxBrush(MidnightOpenColor, 1f);

                labelFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI", FontWeight.Normal, FontStyle.Normal, LabelFontSize);
                dashedStrokeStyle = CreateStrokeStyle(DashStyleHelper.Dash);
                vwapStrokeStyle = CreateStrokeStyle(VwapLineStyle);
                openingBalanceStrokeStyle = CreateStrokeStyle(OpeningBalanceLineStyle);
                midnightOpenStrokeStyle = CreateStrokeStyle(MidnightOpenLineStyle);
            }
            catch
            {
                DisposeResources();
            }
        }

        private void DisposeResources()
        {
            SafeDispose(ref dxHistogram);
            SafeDispose(ref dxPocRow);
            SafeDispose(ref dxPoc);
            SafeDispose(ref dxVah);
            SafeDispose(ref dxVal);
            SafeDispose(ref dxVwapAbove);
            SafeDispose(ref dxVwapBelow);
            SafeDispose(ref dxBandUpper);
            SafeDispose(ref dxBandLower);
            SafeDispose(ref dxBandUpperFill);
            SafeDispose(ref dxBandLowerFill);
            SafeDispose(ref dxObHigh);
            SafeDispose(ref dxObMid);
            SafeDispose(ref dxObLow);
            SafeDispose(ref dxMidnightOpen);
            SafeDispose(ref labelFormat);
            SafeDispose(ref dashedStrokeStyle);
            SafeDispose(ref vwapStrokeStyle);
            SafeDispose(ref openingBalanceStrokeStyle);
            SafeDispose(ref midnightOpenStrokeStyle);
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (!MasterEnable || RenderTarget == null || chartControl == null || chartScale == null || ChartBars == null)
                return;

            try
            {
                RenderMidnightOpenLevels(chartControl, chartScale);
                RenderClosedSessionLevels(chartControl, chartScale);
                RenderCurrentSession(chartControl, chartScale);
            }
            catch
            {
            }
        }

        private void RenderMidnightOpenLevels(ChartControl chartControl, ChartScale chartScale)
        {
            if (!ShowMidnightOpen || dxMidnightOpen == null)
                return;

            foreach (MidnightOpenRecord record in midnightOpenRecords)
                RenderHorizontalSegment(chartControl, chartScale, record.StartChart, record.EndChart, record.Price, "Midnight Open", dxMidnightOpen, MidnightOpenLineWidth, midnightOpenStrokeStyle, false);
        }

        private void RenderClosedSessionLevels(ChartControl chartControl, ChartScale chartScale)
        {
            float chartRight = chartControl.GetXByBarIndex(ChartBars, ChartBars.ToIndex);

            foreach (ClosedSessionRecord record in closedSessions)
            {
                string prefix = record.Name + " ";

                if (ShowPoc)
                {
                    RenderHorizontalSegment(chartControl, chartScale, record.StartChart, record.EndChart, record.Poc, prefix + "POC", dxPoc, ProfileLineWidth, null, false);
                    if (ProjectClosedSessions)
                        RenderProjectedSegment(chartScale, record.Poc, prefix + "POC", dxPoc, chartControl.GetXByTime(record.EndChart), chartRight, ProfileLineWidth);
                }

                if (ShowVah)
                {
                    RenderHorizontalSegment(chartControl, chartScale, record.StartChart, record.EndChart, record.Vah, prefix + "VAH", dxVah, ProfileLineWidth, null, false);
                    if (ProjectClosedSessions)
                        RenderProjectedSegment(chartScale, record.Vah, prefix + "VAH", dxVah, chartControl.GetXByTime(record.EndChart), chartRight, ProfileLineWidth);
                }

                if (ShowVal)
                {
                    RenderHorizontalSegment(chartControl, chartScale, record.StartChart, record.EndChart, record.Val, prefix + "VAL", dxVal, ProfileLineWidth, null, false);
                    if (ProjectClosedSessions)
                        RenderProjectedSegment(chartScale, record.Val, prefix + "VAL", dxVal, chartControl.GetXByTime(record.EndChart), chartRight, ProfileLineWidth);
                }

                if (ShowOpeningBalance && record.HasOpeningBalance)
                {
                    double mid = (record.OpeningBalanceHigh + record.OpeningBalanceLow) / 2.0;
                    RenderHorizontalSegment(chartControl, chartScale, record.StartChart, record.EndChart, record.OpeningBalanceHigh, prefix + "OB High", dxObHigh, OpeningBalanceLineWidth, openingBalanceStrokeStyle, false);
                    RenderHorizontalSegment(chartControl, chartScale, record.StartChart, record.EndChart, mid, prefix + "OB Mid", dxObMid, OpeningBalanceLineWidth, openingBalanceStrokeStyle, false);
                    RenderHorizontalSegment(chartControl, chartScale, record.StartChart, record.EndChart, record.OpeningBalanceLow, prefix + "OB Low", dxObLow, OpeningBalanceLineWidth, openingBalanceStrokeStyle, false);

                    if (ProjectClosedOpeningBalance)
                    {
                        float endX = chartControl.GetXByTime(record.EndChart);
                        RenderProjectedSegment(chartScale, record.OpeningBalanceHigh, prefix + "OB High", dxObHigh, endX, chartRight, OpeningBalanceLineWidth);
                        RenderProjectedSegment(chartScale, mid, prefix + "OB Mid", dxObMid, endX, chartRight, OpeningBalanceLineWidth);
                        RenderProjectedSegment(chartScale, record.OpeningBalanceLow, prefix + "OB Low", dxObLow, endX, chartRight, OpeningBalanceLineWidth);
                    }
                }
            }
        }

        private void RenderCurrentSession(ChartControl chartControl, ChartScale chartScale)
        {
            if (currentSession == null)
                return;

            float startX = chartControl.GetXByTime(currentSession.Window.StartChart);
            float endX = chartControl.GetXByTime(currentSession.LastUpdateChart);
            string prefix = currentSession.Window.Name + " ";

            if (ShowHistogram)
                RenderHistogram(chartScale, startX, currentSession);

            if (ShowPoc)
                RenderLineByX(chartScale, startX, endX, currentSession.Poc, prefix + "POC", dxPoc, ProfileLineWidth, null);

            if (ShowVah)
                RenderLineByX(chartScale, startX, endX, currentSession.Vah, prefix + "VAH", dxVah, ProfileLineWidth, null);

            if (ShowVal)
                RenderLineByX(chartScale, startX, endX, currentSession.Val, prefix + "VAL", dxVal, ProfileLineWidth, null);

            if (ShowOpeningBalance && currentSession.OpeningBalanceInitialized)
            {
                double mid = (currentSession.OpeningBalanceHigh + currentSession.OpeningBalanceLow) / 2.0;
                RenderLineByX(chartScale, startX, endX, currentSession.OpeningBalanceHigh, prefix + "OB High", dxObHigh, OpeningBalanceLineWidth, openingBalanceStrokeStyle);
                RenderLineByX(chartScale, startX, endX, mid, prefix + "OB Mid", dxObMid, OpeningBalanceLineWidth, openingBalanceStrokeStyle);
                RenderLineByX(chartScale, startX, endX, currentSession.OpeningBalanceLow, prefix + "OB Low", dxObLow, OpeningBalanceLineWidth, openingBalanceStrokeStyle);
            }

            if (ShowVwap && currentSession.StartBarIndex >= 0 && currentSession.LastBarIndex >= currentSession.StartBarIndex)
            {
                SharpDX.Direct2D1.SolidColorBrush vwapBrush = !double.IsNaN(currentSession.LastPrice) && currentSession.LastPrice >= currentSession.Vwap
                    ? dxVwapAbove
                    : dxVwapBelow;

                if (ShowBands)
                {
                    RenderCloud(chartControl, chartScale, currentSession.StartBarIndex, currentSession.LastBarIndex, sessionVwapSeries, sessionUpperBandSeries, dxBandUpperFill);
                    RenderCloud(chartControl, chartScale, currentSession.StartBarIndex, currentSession.LastBarIndex, sessionLowerBandSeries, sessionVwapSeries, dxBandLowerFill);
                    RenderSeriesLine(chartControl, chartScale, currentSession.StartBarIndex, currentSession.LastBarIndex, sessionUpperBandSeries, dxBandUpper, 1f, null, prefix + "+1σ");
                    RenderSeriesLine(chartControl, chartScale, currentSession.StartBarIndex, currentSession.LastBarIndex, sessionLowerBandSeries, dxBandLower, 1f, null, prefix + "-1σ");
                }

                RenderSeriesLine(chartControl, chartScale, currentSession.StartBarIndex, currentSession.LastBarIndex, sessionVwapSeries, vwapBrush, VwapLineWidth, vwapStrokeStyle, prefix + "VWAP");
            }
        }

        private void RenderHistogram(ChartScale chartScale, float startX, SessionState session)
        {
            if (session == null || session.Profile == null || session.Profile.Count == 0 || dxHistogram == null)
                return;

            int rowSize = Math.Max(1, TicksPerRow);
            long maxRowVolume = 0L;
            Dictionary<long, long> rows = new Dictionary<long, long>();
            long pocTick = PriceToTick(session.Poc);

            foreach (KeyValuePair<double, long> bucket in session.Profile)
            {
                long tick = PriceToTick(bucket.Key);
                long rowKey = (long)Math.Floor(tick / (double)rowSize) * rowSize;
                long existing;
                if (rows.TryGetValue(rowKey, out existing))
                    rows[rowKey] = existing + bucket.Value;
                else
                    rows[rowKey] = bucket.Value;

                if (rows[rowKey] > maxRowVolume)
                    maxRowVolume = rows[rowKey];
            }

            if (maxRowVolume <= 0)
                return;

            foreach (KeyValuePair<long, long> row in rows.OrderBy(k => k.Key))
            {
                double lowPrice = TickToPrice(row.Key);
                double highPrice = TickToPrice(row.Key + rowSize);
                float y1 = chartScale.GetYByValue(lowPrice);
                float y2 = chartScale.GetYByValue(highPrice);
                float top = Math.Min(y1, y2);
                float height = Math.Max(1f, Math.Abs(y2 - y1));
                float width = (float)(HistogramMaxWidth * (row.Value / (double)maxRowVolume));
                RectangleF rect = new RectangleF(startX, top, width, height);

                bool isPocRow = pocTick >= row.Key && pocTick < row.Key + rowSize;
                RenderTarget.FillRectangle(rect, isPocRow ? dxPocRow : dxHistogram);
            }
        }

        private void RenderCloud(ChartControl chartControl, ChartScale chartScale, int startBar, int endBar, Series<double> lowerSeries, Series<double> upperSeries, SharpDX.Direct2D1.SolidColorBrush fillBrush)
        {
            if (fillBrush == null || lowerSeries == null || upperSeries == null)
                return;

            int fromBar = Math.Max(startBar, ChartBars.FromIndex);
            int toBar = Math.Min(endBar, ChartBars.ToIndex);
            if (toBar <= fromBar)
                return;

            for (int bar = fromBar + 1; bar <= toBar; bar++)
            {
                double lower1 = lowerSeries.GetValueAt(bar - 1);
                double upper1 = upperSeries.GetValueAt(bar - 1);
                double lower2 = lowerSeries.GetValueAt(bar);
                double upper2 = upperSeries.GetValueAt(bar);

                if (!IsRenderablePrice(lower1) || !IsRenderablePrice(upper1) || !IsRenderablePrice(lower2) || !IsRenderablePrice(upper2))
                    continue;

                float x1 = chartControl.GetXByBarIndex(ChartBars, bar - 1);
                float x2 = chartControl.GetXByBarIndex(ChartBars, bar);
                float yLower1 = chartScale.GetYByValue(lower1);
                float yUpper1 = chartScale.GetYByValue(upper1);
                float yLower2 = chartScale.GetYByValue(lower2);
                float yUpper2 = chartScale.GetYByValue(upper2);

                using (PathGeometry geometry = new PathGeometry(Core.Globals.D2DFactory))
                using (GeometrySink sink = geometry.Open())
                {
                    sink.BeginFigure(new Vector2(x1, yLower1), FigureBegin.Filled);
                    sink.AddLine(new Vector2(x2, yLower2));
                    sink.AddLine(new Vector2(x2, yUpper2));
                    sink.AddLine(new Vector2(x1, yUpper1));
                    sink.EndFigure(FigureEnd.Closed);
                    sink.Close();
                    RenderTarget.FillGeometry(geometry, fillBrush);
                }
            }
        }

        private void RenderSeriesLine(ChartControl chartControl, ChartScale chartScale, int startBar, int endBar, Series<double> series, SharpDX.Direct2D1.SolidColorBrush brush, float width, StrokeStyle strokeStyle, string label)
        {
            if (series == null || brush == null)
                return;

            int fromBar = Math.Max(startBar, ChartBars.FromIndex);
            int toBar = Math.Min(endBar, ChartBars.ToIndex);
            if (toBar < fromBar)
                return;

            bool hasPoint = false;
            float lastX = 0f;
            float lastY = 0f;
            double lastValue = double.NaN;
            float labelX = 0f;

            for (int bar = fromBar; bar <= toBar; bar++)
            {
                double value = series.GetValueAt(bar);
                if (!IsRenderablePrice(value))
                {
                    hasPoint = false;
                    continue;
                }

                float x = chartControl.GetXByBarIndex(ChartBars, bar);
                float y = chartScale.GetYByValue(value);

                if (hasPoint)
                    DrawLine(new Vector2(lastX, lastY), new Vector2(x, y), brush, width, strokeStyle);

                hasPoint = true;
                lastX = x;
                lastY = y;
                lastValue = value;
                labelX = x;
            }

            if (IsRenderablePrice(lastValue))
                DrawLabel(label, brush, labelX, chartScale.GetYByValue(lastValue));
        }

        private void RenderHorizontalSegment(ChartControl chartControl, ChartScale chartScale, DateTime startTime, DateTime endTime, double price, string label, SharpDX.Direct2D1.SolidColorBrush brush, float width, StrokeStyle strokeStyle, bool labelAtRightEdge)
        {
            if (!IsRenderablePrice(price) || brush == null)
                return;

            float startX = chartControl.GetXByTime(startTime);
            float endX = chartControl.GetXByTime(endTime);
            if (labelAtRightEdge)
                endX = chartControl.GetXByBarIndex(ChartBars, ChartBars.ToIndex);

            RenderLineByX(chartScale, startX, endX, price, label, brush, width, strokeStyle);
        }

        private void RenderProjectedSegment(ChartScale chartScale, double price, string label, SharpDX.Direct2D1.SolidColorBrush brush, float startX, float endX, float width)
        {
            if (!IsRenderablePrice(price) || brush == null || endX <= startX)
                return;

            RenderLineByX(chartScale, startX, endX, price, label, brush, width, dashedStrokeStyle);
        }

        private void RenderLineByX(ChartScale chartScale, float startX, float endX, double price, string label, SharpDX.Direct2D1.SolidColorBrush brush, float width, StrokeStyle strokeStyle)
        {
            if (!IsRenderablePrice(price) || brush == null || float.IsNaN(startX) || float.IsNaN(endX) || endX <= startX)
                return;

            float y = chartScale.GetYByValue(price);
            DrawLine(new Vector2(startX, y), new Vector2(endX, y), brush, width, strokeStyle);
            DrawLabel(label, brush, endX, y);
        }

        private void DrawLabel(string text, SharpDX.Direct2D1.SolidColorBrush brush, float x, float y)
        {
            if (labelFormat == null || brush == null || string.IsNullOrEmpty(text))
                return;

            using (TextLayout layout = new TextLayout(Core.Globals.DirectWriteFactory, text, labelFormat, LabelLayoutWidth, labelFormat.FontSize + 6f))
                RenderTarget.DrawTextLayout(new Vector2(x - layout.Metrics.Width - LabelHorizontalPadding, y - labelFormat.FontSize - LabelVerticalOffset), layout, brush);
        }

        private void DrawLine(Vector2 start, Vector2 end, SharpDX.Direct2D1.SolidColorBrush brush, float width, StrokeStyle strokeStyle)
        {
            if (brush == null)
                return;

            if (strokeStyle != null)
                RenderTarget.DrawLine(start, end, brush, width, strokeStyle);
            else
                RenderTarget.DrawLine(start, end, brush, width);
        }

        private SharpDX.Direct2D1.SolidColorBrush CreateDxBrush(System.Windows.Media.Brush brush, float alpha)
        {
            return new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, ToColor4(brush, alpha));
        }

        private Color4 ToColor4(System.Windows.Media.Brush brush, float alpha)
        {
            if (brush == null)
                return new Color4(1f, 1f, 1f, alpha);

            System.Windows.Media.SolidColorBrush solid = brush as System.Windows.Media.SolidColorBrush;
            if (solid == null)
                return new Color4(1f, 1f, 1f, alpha);

            System.Windows.Media.Color color = solid.Color;
            float finalAlpha = Math.Max(0f, Math.Min(1f, (float)(solid.Opacity * alpha)));
            return new Color4(color.R / 255f, color.G / 255f, color.B / 255f, finalAlpha);
        }

        private StrokeStyle CreateStrokeStyle(DashStyleHelper dashStyle)
        {
            DashStyle style = DashStyle.Solid;
            switch (dashStyle)
            {
                case DashStyleHelper.Dash:
                    style = DashStyle.Dash;
                    break;
                case DashStyleHelper.Dot:
                    style = DashStyle.Dot;
                    break;
                case DashStyleHelper.DashDot:
                    style = DashStyle.DashDot;
                    break;
                case DashStyleHelper.DashDotDot:
                    style = DashStyle.DashDotDot;
                    break;
                default:
                    style = DashStyle.Solid;
                    break;
            }

            return new StrokeStyle(Core.Globals.D2DFactory, new StrokeStyleProperties { DashStyle = style });
        }

        private void SafeDispose<T>(ref T resource) where T : class, IDisposable
        {
            if (resource == null)
                return;

            resource.Dispose();
            resource = null;
        }

        private bool IsRenderablePrice(double price)
        {
            return !double.IsNaN(price) && !double.IsInfinity(price) && price.ApproxCompare(0) > 0;
        }

        private long PriceToTick(double price)
        {
            if (double.IsNaN(price) || double.IsInfinity(price))
                return 0L;

            return Convert.ToInt64(Math.Round(Instrument.MasterInstrument.RoundToTickSize(price) / TickSize));
        }

        private double TickToPrice(long tick)
        {
            return Instrument.MasterInstrument.RoundToTickSize(tick * TickSize);
        }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Master Enable", Order = 1, GroupName = "1. General")]
        public bool MasterEnable { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Profile Source", Order = 2, GroupName = "1. General")]
        public SessionProfileSource ProfileSource { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Asia", Order = 1, GroupName = "2. Sessions")]
        public bool EnableAsia { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Asia Start Hour (ET)", Order = 2, GroupName = "2. Sessions")]
        public int AsiaStartHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Asia Start Minute (ET)", Order = 3, GroupName = "2. Sessions")]
        public int AsiaStartMinute { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Asia End Hour (ET)", Order = 4, GroupName = "2. Sessions")]
        public int AsiaEndHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Asia End Minute (ET)", Order = 5, GroupName = "2. Sessions")]
        public int AsiaEndMinute { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable London", Order = 6, GroupName = "2. Sessions")]
        public bool EnableLondon { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Dynamic London Open", Order = 7, GroupName = "2. Sessions")]
        public bool UseDynamicLondonOpen { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "London Start Hour (ET Fallback)", Order = 8, GroupName = "2. Sessions")]
        public int LondonStartHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "London Start Minute (ET Fallback)", Order = 9, GroupName = "2. Sessions")]
        public int LondonStartMinute { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "London End Hour (ET)", Order = 10, GroupName = "2. Sessions")]
        public int LondonEndHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "London End Minute (ET)", Order = 11, GroupName = "2. Sessions")]
        public int LondonEndMinute { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable NY", Order = 12, GroupName = "2. Sessions")]
        public bool EnableNewYork { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "NY Start Hour (ET)", Order = 13, GroupName = "2. Sessions")]
        public int NewYorkStartHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "NY Start Minute (ET)", Order = 14, GroupName = "2. Sessions")]
        public int NewYorkStartMinute { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "NY End Hour (ET)", Order = 15, GroupName = "2. Sessions")]
        public int NewYorkEndHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "NY End Minute (ET)", Order = 16, GroupName = "2. Sessions")]
        public int NewYorkEndMinute { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Histogram", Order = 1, GroupName = "3. Volume Profile")]
        public bool ShowHistogram { get; set; }

        [NinjaScriptProperty]
        [Range(20, 400)]
        [Display(Name = "Histogram Max Width", Order = 2, GroupName = "3. Volume Profile")]
        public int HistogramMaxWidth { get; set; }

        [NinjaScriptProperty]
        [Range(1, 32)]
        [Display(Name = "Ticks Per Row", Order = 3, GroupName = "3. Volume Profile")]
        public int TicksPerRow { get; set; }

        [NinjaScriptProperty]
        [Range(50, 90)]
        [Display(Name = "Value Area Percent", Order = 4, GroupName = "3. Volume Profile")]
        public int ValueAreaPercent { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show POC", Order = 5, GroupName = "3. Volume Profile")]
        public bool ShowPoc { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show VAH", Order = 6, GroupName = "3. Volume Profile")]
        public bool ShowVah { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show VAL", Order = 7, GroupName = "3. Volume Profile")]
        public bool ShowVal { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Project Closed Sessions", Order = 8, GroupName = "3. Volume Profile")]
        public bool ProjectClosedSessions { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show VWAP", Order = 1, GroupName = "4. VWAP")]
        public bool ShowVwap { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Bands", Order = 2, GroupName = "4. VWAP")]
        public bool ShowBands { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Band Opacity %", Order = 3, GroupName = "4. VWAP")]
        public int BandOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "VWAP Line Width", Order = 4, GroupName = "4. VWAP")]
        public int VwapLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "VWAP Line Style", Order = 5, GroupName = "4. VWAP")]
        public DashStyleHelper VwapLineStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Opening Balance", Order = 1, GroupName = "5. Opening Balance")]
        public bool ShowOpeningBalance { get; set; }

        [NinjaScriptProperty]
        [Range(15, 180)]
        [Display(Name = "Opening Balance Minutes", Order = 2, GroupName = "5. Opening Balance")]
        public int OpeningBalanceMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Project Closed OB", Order = 3, GroupName = "5. Opening Balance")]
        public bool ProjectClosedOpeningBalance { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "OB Line Width", Order = 4, GroupName = "5. Opening Balance")]
        public int OpeningBalanceLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "OB Line Style", Order = 5, GroupName = "5. Opening Balance")]
        public DashStyleHelper OpeningBalanceLineStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Midnight Open", Order = 1, GroupName = "6. Midnight Open")]
        public bool ShowMidnightOpen { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Midnight Line Width", Order = 2, GroupName = "6. Midnight Open")]
        public int MidnightOpenLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Midnight Line Style", Order = 3, GroupName = "6. Midnight Open")]
        public DashStyleHelper MidnightOpenLineStyle { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Profile Line Width", Order = 1, GroupName = "7. Colors & Style")]
        public int ProfileLineWidth { get; set; }

        [NinjaScriptProperty]
        [Range(8, 18)]
        [Display(Name = "Label Font Size", Order = 2, GroupName = "7. Colors & Style")]
        public int LabelFontSize { get; set; }

        [XmlIgnore]
        [Display(Name = "POC Color", Order = 3, GroupName = "7. Colors & Style")]
        public System.Windows.Media.Brush PocColor { get; set; }

        [Browsable(false)]
        public string PocColorSerializable
        {
            get { return Serialize.BrushToString(PocColor); }
            set { PocColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "VAH Color", Order = 4, GroupName = "7. Colors & Style")]
        public System.Windows.Media.Brush VahColor { get; set; }

        [Browsable(false)]
        public string VahColorSerializable
        {
            get { return Serialize.BrushToString(VahColor); }
            set { VahColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "VAL Color", Order = 5, GroupName = "7. Colors & Style")]
        public System.Windows.Media.Brush ValColor { get; set; }

        [Browsable(false)]
        public string ValColorSerializable
        {
            get { return Serialize.BrushToString(ValColor); }
            set { ValColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Histogram Color", Order = 6, GroupName = "7. Colors & Style")]
        public System.Windows.Media.Brush HistogramColor { get; set; }

        [Browsable(false)]
        public string HistogramColorSerializable
        {
            get { return Serialize.BrushToString(HistogramColor); }
            set { HistogramColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "POC Row Color", Order = 7, GroupName = "7. Colors & Style")]
        public System.Windows.Media.Brush PocRowColor { get; set; }

        [Browsable(false)]
        public string PocRowColorSerializable
        {
            get { return Serialize.BrushToString(PocRowColor); }
            set { PocRowColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "VWAP Above Color", Order = 8, GroupName = "7. Colors & Style")]
        public System.Windows.Media.Brush VwapAboveColor { get; set; }

        [Browsable(false)]
        public string VwapAboveColorSerializable
        {
            get { return Serialize.BrushToString(VwapAboveColor); }
            set { VwapAboveColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "VWAP Below Color", Order = 9, GroupName = "7. Colors & Style")]
        public System.Windows.Media.Brush VwapBelowColor { get; set; }

        [Browsable(false)]
        public string VwapBelowColorSerializable
        {
            get { return Serialize.BrushToString(VwapBelowColor); }
            set { VwapBelowColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Upper Band Color", Order = 10, GroupName = "7. Colors & Style")]
        public System.Windows.Media.Brush BandUpperColor { get; set; }

        [Browsable(false)]
        public string BandUpperColorSerializable
        {
            get { return Serialize.BrushToString(BandUpperColor); }
            set { BandUpperColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Lower Band Color", Order = 11, GroupName = "7. Colors & Style")]
        public System.Windows.Media.Brush BandLowerColor { get; set; }

        [Browsable(false)]
        public string BandLowerColorSerializable
        {
            get { return Serialize.BrushToString(BandLowerColor); }
            set { BandLowerColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "OB High Color", Order = 12, GroupName = "7. Colors & Style")]
        public System.Windows.Media.Brush OpeningBalanceHighColor { get; set; }

        [Browsable(false)]
        public string OpeningBalanceHighColorSerializable
        {
            get { return Serialize.BrushToString(OpeningBalanceHighColor); }
            set { OpeningBalanceHighColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "OB Mid Color", Order = 13, GroupName = "7. Colors & Style")]
        public System.Windows.Media.Brush OpeningBalanceMidColor { get; set; }

        [Browsable(false)]
        public string OpeningBalanceMidColorSerializable
        {
            get { return Serialize.BrushToString(OpeningBalanceMidColor); }
            set { OpeningBalanceMidColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "OB Low Color", Order = 14, GroupName = "7. Colors & Style")]
        public System.Windows.Media.Brush OpeningBalanceLowColor { get; set; }

        [Browsable(false)]
        public string OpeningBalanceLowColorSerializable
        {
            get { return Serialize.BrushToString(OpeningBalanceLowColor); }
            set { OpeningBalanceLowColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Midnight Open Color", Order = 15, GroupName = "7. Colors & Style")]
        public System.Windows.Media.Brush MidnightOpenColor { get; set; }

        [Browsable(false)]
        public string MidnightOpenColorSerializable
        {
            get { return Serialize.BrushToString(MidnightOpenColor); }
            set { MidnightOpenColor = Serialize.StringToBrush(value); }
        }
        #endregion
    }
}
