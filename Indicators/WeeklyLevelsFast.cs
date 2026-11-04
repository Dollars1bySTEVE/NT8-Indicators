using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

#if !WEEKLY_LEVELS_CORE_ONLY
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
#endif

namespace WeeklyLevelsFastCalculation
{
    public sealed class WeekData
    {
        public DateTime Key;
        public DateTime Begin;
        public double Open;
        public double High = double.NegativeInfinity;
        public double Low = double.PositiveInfinity;
        public double Close;
        public double IbHigh = double.NegativeInfinity;
        public double IbLow = double.PositiveInfinity;
        public double Volume;
        public double PriceVolume;
        public readonly Dictionary<long, double> Profile = new Dictionary<long, double>();
    }

    public sealed class LevelSnapshot
    {
        public readonly double[] Levels = new double[26];
        public DateTime Week;
        public bool IbComplete;

        public LevelSnapshot()
        {
            for (int i = 0; i < Levels.Length; i++)
                Levels[i] = double.NaN;
        }
    }

    public sealed class WeeklyEngine
    {
        public const int MaximumPriceSpan = 1000000;
        private readonly double tickSize;
        private readonly TimeSpan ibDuration;
        private readonly bool buildProfile;
        private long lastId = -1;
        private double lastVolume;
        private double lastPriceVolume;
        private long lastLow;
        private long lastHigh;
        private bool haveLast;
        private DateTime lastTime;
        public WeekData Current { get; private set; }
        public WeekData Previous { get; private set; }

        public WeeklyEngine(double tickSize, TimeSpan ibDuration, bool buildProfile)
        {
            if (tickSize <= 0 || double.IsNaN(tickSize) || double.IsInfinity(tickSize))
                throw new ArgumentOutOfRangeException("tickSize");
            if (ibDuration <= TimeSpan.Zero || ibDuration >= TimeSpan.FromDays(7))
                throw new ArgumentOutOfRangeException("ibDuration");
            this.tickSize = tickSize;
            this.ibDuration = ibDuration;
            this.buildProfile = buildProfile;
        }

        public static DateTime Monday(DateTime tradingDay)
        {
            return tradingDay.Date.AddDays(-(((int)tradingDay.DayOfWeek + 6) % 7));
        }

        public void SeedPrevious(WeekData previous)
        {
            if (Current != null)
                throw new InvalidOperationException("Seed before processing data.");
            Previous = previous;
        }

        public void Update(long id, DateTime week, DateTime begin, DateTime time,
            double open, double high, double low, double close, double volume, bool trade)
        {
            if (id < lastId)
                throw new InvalidOperationException("Source bars must be processed in order.");
            if (volume < 0 || double.IsNaN(volume) || double.IsInfinity(volume))
                throw new InvalidOperationException("Invalid traded volume.");
            if (double.IsNaN(open) || double.IsNaN(high) || double.IsNaN(low)
                || double.IsNaN(close) || double.IsInfinity(open) || double.IsInfinity(high)
                || double.IsInfinity(low) || double.IsInfinity(close)
                || high < low || open < low || open > high || close < low || close > high)
                throw new InvalidOperationException("Invalid source OHLC.");
            bool sameBar = haveLast && id == lastId;
            if (Current == null || Current.Key != week)
            {
                if (sameBar || (Current != null && week < Current.Key))
                    throw new InvalidOperationException("Source week moved backwards.");
                if (Current != null)
                    Previous = Current.Key == week.AddDays(-7) ? Current : null;
                else if (Previous != null && Previous.Key != week.AddDays(-7))
                    Previous = null;
                Current = new WeekData { Key = week, Begin = begin, Open = open };
                haveLast = false;
                sameBar = false;
            }

            long lo = PriceTick(low);
            long hi = PriceTick(high);
            if (hi < lo || (decimal)hi - lo > MaximumPriceSpan)
                throw new InvalidOperationException("Source price range exceeds the safety limit.");
            if (sameBar)
            {
                Current.Volume -= lastVolume;
                Current.PriceVolume -= lastPriceVolume;
                if (buildProfile)
                    Distribute(lastLow, lastHigh, -lastVolume);
            }

            Current.High = Math.Max(Current.High, high);
            Current.Low = Math.Min(Current.Low, low);
            Current.Close = close;
            bool inIb = trade
                ? time >= begin && time < begin.Add(ibDuration)
                : time > begin && time <= begin.Add(ibDuration);
            if (inIb)
            {
                Current.IbHigh = Math.Max(Current.IbHigh, high);
                Current.IbLow = Math.Min(Current.IbLow, low);
            }

            double weightedPrice = trade ? close : (high + low + close) / 3.0;
            lastPriceVolume = weightedPrice * volume;
            Current.Volume += volume;
            Current.PriceVolume += lastPriceVolume;
            if (buildProfile)
                Distribute(lo, hi, volume);
            lastId = id;
            lastVolume = volume;
            lastLow = lo;
            lastHigh = hi;
            haveLast = true;
            lastTime = time;
        }

        private long PriceTick(double price)
        {
            return checked((long)Math.Round(price / tickSize, MidpointRounding.AwayFromZero));
        }

        private void Distribute(long low, long high, double volume)
        {
            if (volume == 0)
                return;
            double each = volume / (high - low + 1);
            for (long price = low; price <= high; price++)
            {
                double old;
                Current.Profile.TryGetValue(price, out old);
                double value = old + each;
                if (Math.Abs(value) < 0.00000001)
                    Current.Profile.Remove(price);
                else
                    Current.Profile[price] = value;
            }
        }

        public LevelSnapshot Snapshot(double percent, bool currentMidPoc, bool previousMidPoc,
            bool includeVolume)
        {
            if (percent <= 0 || percent > 100 || double.IsNaN(percent))
                throw new ArgumentOutOfRangeException("percent");
            LevelSnapshot result = new LevelSnapshot();
            if (Current == null)
                return result;
            result.Week = Current.Key;
            result.IbComplete = lastTime >= Current.Begin.Add(ibDuration);
            double[] p = result.Levels;
            if (!double.IsInfinity(Current.IbHigh) && !double.IsInfinity(Current.IbLow))
            {
                p[0] = Current.IbHigh;
                p[1] = (Current.IbHigh + Current.IbLow) / 2;
                p[2] = Current.IbLow;
                double range = Current.IbHigh - Current.IbLow;
                for (int i = 0; i < 4; i++)
                {
                    p[3 + i * 2] = Current.IbHigh + range * (i + 1) * 0.5;
                    p[4 + i * 2] = Current.IbLow - range * (i + 1) * 0.5;
                }
            }
            p[14] = Current.Open;
            p[25] = (Current.High + Current.Low) / 2;
            if (includeVolume)
            {
                ValueArea(Current, percent, out p[15], out p[16], out p[17]);
                if (currentMidPoc && !double.IsNaN(p[15]))
                    p[16] = (p[15] + p[17]) / 2;
                if (Current.Volume > 0)
                    p[21] = Current.PriceVolume / Current.Volume;
            }
            if (Previous != null)
            {
                p[11] = Previous.High;
                p[12] = Previous.Low;
                p[13] = Previous.Close;
                p[22] = Previous.Open;
                p[24] = (Previous.High + Previous.Low) / 2;
                if (includeVolume)
                {
                    ValueArea(Previous, percent, out p[18], out p[19], out p[20]);
                    if (previousMidPoc && !double.IsNaN(p[18]))
                        p[19] = (p[18] + p[20]) / 2;
                }
            }
            return result;
        }

        public void ValueArea(WeekData week, double percent, out double vah, out double poc,
            out double val)
        {
            if (percent <= 0 || percent > 100 || double.IsNaN(percent))
                throw new ArgumentOutOfRangeException("percent");
            vah = poc = val = double.NaN;
            if (week == null || week.Profile.Count == 0)
                return;
            long min = long.MaxValue, max = long.MinValue, center = 0;
            double total = 0, largest = -1;
            foreach (KeyValuePair<long, double> entry in week.Profile)
            {
                if (entry.Value < -0.00000001 || double.IsNaN(entry.Value)
                    || double.IsInfinity(entry.Value))
                    throw new InvalidOperationException("Invalid volume profile.");
                if (entry.Value <= 0)
                    continue;
                min = Math.Min(min, entry.Key);
                max = Math.Max(max, entry.Key);
                total += entry.Value;
                if (entry.Value > largest || (entry.Value == largest && entry.Key < center))
                {
                    largest = entry.Value;
                    center = entry.Key;
                }
            }
            if (total <= 0)
                return;
            if (max - min > MaximumPriceSpan)
                throw new InvalidOperationException("Weekly price range exceeds the safety limit.");
            long lower = center, upper = center;
            double accumulated = largest;
            double target = total * percent / 100;
            while (accumulated + total * 1e-12 < target && (lower > min || upper < max))
            {
                double below = -1, above = -1;
                if (lower > min && !week.Profile.TryGetValue(lower - 1, out below))
                    below = 0;
                if (upper < max && !week.Profile.TryGetValue(upper + 1, out above))
                    above = 0;
                if (lower > min && (upper == max || below >= above))
                {
                    lower--;
                    accumulated += below;
                }
                else
                {
                    upper++;
                    accumulated += above;
                }
            }
            vah = upper * tickSize;
            poc = center * tickSize;
            val = lower * tickSize;
        }
    }

    public static class WeeklyCache
    {
        private const int Version = 1;

        public static void Save(string path, string identity, WeekData week)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (BinaryWriter writer = new BinaryWriter(File.Create(temporary)))
                {
                    writer.Write(Version);
                    writer.Write(identity);
                    writer.Write(week.Key.Ticks);
                    writer.Write(week.Begin.Ticks);
                    writer.Write(week.Open);
                    writer.Write(week.High);
                    writer.Write(week.Low);
                    writer.Write(week.Close);
                    writer.Write(week.IbHigh);
                    writer.Write(week.IbLow);
                    writer.Write(week.Volume);
                    writer.Write(week.PriceVolume);
                    writer.Write(week.Profile.Count);
                    foreach (KeyValuePair<long, double> entry in week.Profile)
                    {
                        writer.Write(entry.Key);
                        writer.Write(entry.Value);
                    }
                }
                if (File.Exists(path))
                    File.Replace(temporary, path, null);
                else
                    File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        public static WeekData Load(string path, string identity, DateTime expectedWeek)
        {
            using (BinaryReader reader = new BinaryReader(File.OpenRead(path)))
            {
                if (reader.ReadInt32() != Version || reader.ReadString() != identity)
                    throw new InvalidDataException("Cache identity/version mismatch.");
                WeekData week = new WeekData
                {
                    Key = new DateTime(reader.ReadInt64()),
                    Begin = new DateTime(reader.ReadInt64()),
                    Open = reader.ReadDouble(),
                    High = reader.ReadDouble(),
                    Low = reader.ReadDouble(),
                    Close = reader.ReadDouble(),
                    IbHigh = reader.ReadDouble(),
                    IbLow = reader.ReadDouble(),
                    Volume = reader.ReadDouble(),
                    PriceVolume = reader.ReadDouble()
                };
                if (week.Key != expectedWeek || week.Key != WeeklyEngine.Monday(week.Key)
                    || !Finite(week.Open) || !Finite(week.High) || !Finite(week.Low)
                    || !Finite(week.Close) || !Finite(week.Volume) || !Finite(week.PriceVolume)
                    || week.High < week.Low || week.Volume < 0
                    || week.Open < week.Low || week.Open > week.High
                    || week.Close < week.Low || week.Close > week.High
                    || (!Finite(week.IbHigh) && week.IbHigh != double.NegativeInfinity)
                    || (!Finite(week.IbLow) && week.IbLow != double.PositiveInfinity)
                    || double.IsInfinity(week.IbHigh) != double.IsInfinity(week.IbLow)
                    || (!double.IsInfinity(week.IbHigh) && week.IbHigh < week.IbLow))
                    throw new InvalidDataException("Invalid cached weekly values.");
                int count = reader.ReadInt32();
                if (count < 0 || count > WeeklyEngine.MaximumPriceSpan + 1)
                    throw new InvalidDataException("Invalid cached profile size.");
                double sum = 0;
                long min = long.MaxValue, max = long.MinValue;
                for (int i = 0; i < count; i++)
                {
                    long price = reader.ReadInt64();
                    double volume = reader.ReadDouble();
                    if (!Finite(volume) || volume <= 0 || week.Profile.ContainsKey(price)
                        || price < -1000000000000L || price > 1000000000000L)
                        throw new InvalidDataException("Invalid cached profile entry.");
                    min = Math.Min(min, price);
                    max = Math.Max(max, price);
                    sum += volume;
                    week.Profile.Add(price, volume);
                }
                if ((count > 0 && max - min > WeeklyEngine.MaximumPriceSpan)
                    || Math.Abs(sum - week.Volume) > Math.Max(0.0001, week.Volume * 1e-8)
                    || reader.BaseStream.Position != reader.BaseStream.Length)
                    throw new InvalidDataException("Cached profile totals/range are invalid.");
                return week;
            }
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}

#if !WEEKLY_LEVELS_CORE_ONLY
public enum WeeklyLevelsFastSource
{
    HistoricalTrades,
    MinuteEstimate
}

namespace NinjaTrader.NinjaScript.Indicators
{
    public class WeeklyLevelsFast : Indicator
    {
        private readonly object requestGate = new object();
        private BarsRequest request;
        private SessionIterator previewSessions;
        private SessionIterator sourceSessions;
        private WeeklyLevelsFastCalculation.WeeklyEngine preview;
        private WeeklyLevelsFastCalculation.WeeklyEngine engine;
        private volatile WeeklyLevelsFastCalculation.LevelSnapshot snapshot;
        private volatile WeeklyLevelsFastCalculation.LevelSnapshot previewSnapshot;
        private volatile string status = "Loading chart levels...";
        private volatile string warning = "";
        private volatile bool terminated;
        private volatile bool requestReady;
        private volatile bool sourcePublished;
        private bool started;
        private bool previewSessionReady;
        private bool sourceSessionReady;
        private volatile bool failed;
        private int sourceIndex = -1;
        private long cutoffTicks;
        private int dirty;
        private int workerScheduled;
        private System.Threading.Timer timer;
        private DateTime lastSavedWeek;
        private DateTime requestFrom;
        private DateTime minimumSourceWeek;
        private DateTime sourceWeek;
        private DateTime sourceWeekBegin;
        private DateTime previewWeek;
        private DateTime previewWeekBegin;
        private readonly HashSet<DateTime> cacheableWeeks = new HashSet<DateTime>();
        private string cacheIdentity;
        private string cachePrefix;
        private SharpDX.Direct2D1.Brush[] dxBrushes;
        private System.Windows.Media.Brush[] brushSources;
        private SharpDX.Direct2D1.StrokeStyle[] dxStyles;
        private SharpDX.DirectWrite.TextFormat textFormat;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "WeeklyLevelsFast";
                Description = "Standalone weekly IB, extensions, reference levels, VWAP and asynchronous volume profiles.";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                IsAutoScale = false;
                DisplayInDataBox = false;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = false;
                BarsRequiredToPlot = 0;
                IbDuration = "30:00";
                ValueAreaPercent = 70;
                Source = WeeklyLevelsFastSource.HistoricalTrades;
                UseCompletedWeekCache = true;
                RefreshMilliseconds = 500;
                WeeklyIB = true;
                WeeklyIBHighlight = false;
                Extensions50 = Extensions100 = Extensions150 = true;
                Extensions200 = false;
                CurrentWeekOpen = CurrentWeekMid = CurrentWeekValueArea = true;
                CurrentWeekVWAP = true;
                PriorWeekHLC = PriorWeekOpen = PriorWeekMid = PriorWeekValueArea = true;
                PriorWeekVAHighlight = false;
                CurrentWeekVaMidAsPoc = PriorWeekVaMidAsPoc = false;
                TotalWidth = 100;
                EdgeOffset = 0;
                ShowLabels = true;
                ShowPriceOnLabels = false;
                LabelFontSize = 11;
                AddPlot(new Stroke(Brushes.White, DashStyleHelper.DashDot, 1), PlotStyle.Line, "WIB-Hi");
                AddPlot(new Stroke(Brushes.White, DashStyleHelper.DashDot, 1), PlotStyle.Line, "WIB-Mid");
                AddPlot(new Stroke(Brushes.White, DashStyleHelper.DashDot, 1), PlotStyle.Line, "WIB-Lo");
                for (int i = 1; i <= 4; i++)
                {
                    AddPlot(new Stroke(Brushes.DeepSkyBlue, DashStyleHelper.DashDot, 1),
                        PlotStyle.Line, "WIB " + i * 50 + "%");
                    AddPlot(new Stroke(Brushes.OrangeRed, DashStyleHelper.DashDot, 1),
                        PlotStyle.Line, "WIB -" + i * 50 + "%");
                }
                AddPlot(new Stroke(Brushes.IndianRed, 1), PlotStyle.Line, "PW-Hi");
                AddPlot(new Stroke(Brushes.IndianRed, 1), PlotStyle.Line, "PW-Lo");
                AddPlot(new Stroke(Brushes.Gray, DashStyleHelper.DashDot, 1), PlotStyle.Line, "PW-Cl");
                AddPlot(new Stroke(Brushes.SpringGreen, 1), PlotStyle.Line, "WK-Op");
                AddPlot(new Stroke(Brushes.Silver, DashStyleHelper.DashDot, 1), PlotStyle.Line, "WK-VAH");
                AddPlot(new Stroke(Brushes.Silver, DashStyleHelper.DashDot, 1), PlotStyle.Line, "WK-POC");
                AddPlot(new Stroke(Brushes.Silver, DashStyleHelper.DashDot, 1), PlotStyle.Line, "WK-VAL");
                AddPlot(new Stroke(Brushes.SlateGray, 1), PlotStyle.Line, "PW-VAH");
                AddPlot(new Stroke(Brushes.Gray, DashStyleHelper.DashDot, 1), PlotStyle.Line, "PW-POC");
                AddPlot(new Stroke(Brushes.SlateGray, 1), PlotStyle.Line, "PW-VAL");
                AddPlot(new Stroke(Brushes.SlateGray, DashStyleHelper.DashDot, 1), PlotStyle.Line, "WK-VWAP");
                AddPlot(new Stroke(Brushes.Silver, DashStyleHelper.Dot, 1), PlotStyle.Line, "PW-Op");
                AddPlot(new Stroke(Brushes.Silver, DashStyleHelper.Dash, 1), PlotStyle.Line, "PW-Mid");
                AddPlot(new Stroke(Brushes.Gray, DashStyleHelper.Dash, 1), PlotStyle.Line, "WK-Mid");
            }
            else if (State == State.DataLoaded)
            {
                TimeSpan duration = ParseDuration(IbDuration);
                preview = new WeeklyLevelsFastCalculation.WeeklyEngine(TickSize, duration, false);
                engine = new WeeklyLevelsFastCalculation.WeeklyEngine(TickSize, duration, true);
                previewSessions = new SessionIterator(Bars);
                timer = new System.Threading.Timer(ScheduleWork, null,
                    RefreshMilliseconds, RefreshMilliseconds);
            }
            else if (State == State.Terminated)
            {
                terminated = true;
                if (timer != null)
                    timer.Dispose();
                BarsRequest oldRequest;
                lock (requestGate)
                {
                    oldRequest = request;
                    request = null;
                }
                if (oldRequest != null)
                {
                    oldRequest.Update -= RequestUpdated;
                    oldRequest.Dispose();
                }
                if (ChartControl != null)
                    ChartControl.Dispatcher.BeginInvoke(new Action(DisposeRenderResources));
                else
                    DisposeRenderResources();
            }
        }

        protected override void OnBarUpdate()
        {
            if (terminated || failed || BarsInProgress != 0)
                return;
            if (!Bars.BarsType.IsIntraday)
            {
                Fail("Use an intraday price chart, not daily/weekly bars.");
                return;
            }
            if (!started)
            {
                started = true;
                DateTime last = Bars.GetTime(Bars.Count - 1);
                Interlocked.Exchange(ref cutoffTicks, last.Ticks);
                StartRequest(last);
            }
            if (Time[0].Ticks > Interlocked.Read(ref cutoffTicks))
            {
                Interlocked.Exchange(ref cutoffTicks, Time[0].Ticks);
                Interlocked.Exchange(ref dirty, 1);
            }
            if (!sourcePublished)
            {
                LocateSession(previewSessions, Time[0], ref previewSessionReady, true);
                DateTime week = WeeklyLevelsFastCalculation.WeeklyEngine.Monday(
                    previewSessions.ActualTradingDayExchange);
                if (week != previewWeek)
                {
                    previewWeek = week;
                    previewWeekBegin = previewSessions.GetTradingDayBeginLocal(week);
                }
                preview.Update(CurrentBar, week, previewWeekBegin,
                    Time[0], Open[0], High[0], Low[0], Close[0], Volume[0], false);
                previewSnapshot = preview.Snapshot(ValueAreaPercent, false, false, false);
            }
            WeeklyLevelsFastCalculation.LevelSnapshot published = snapshot;
            if (sourcePublished && published != null)
            {
                for (int i = 0; i < 26; i++)
                    Values[i][0] = published.Levels[i];
            }
        }

        private static TimeSpan ParseDuration(string value)
        {
            string[] parts = (value ?? "").Split(':');
            int hours, minutes;
            if (parts.Length != 2 || !int.TryParse(parts[0], out hours)
                || !int.TryParse(parts[1], out minutes) || hours < 0
                || minutes < 0 || minutes > 59 || hours > 167
                || (hours == 0 && minutes == 0))
                throw new ArgumentException("Weekly IB duration must be HH:MM, from 00:01 to 167:59.");
            return TimeSpan.FromMinutes(hours * 60 + minutes);
        }

        private void StartRequest(DateTime last)
        {
            SessionIterator locator = new SessionIterator(Bars);
            locator.GetNextSession(last, true);
            DateTime week = WeeklyLevelsFastCalculation.WeeklyEngine.Monday(
                locator.ActualTradingDayExchange);
            cacheIdentity = BuildCacheIdentity();
            using (SHA256 hash = SHA256.Create())
                cachePrefix = BitConverter.ToString(hash.ComputeHash(
                    Encoding.UTF8.GetBytes(cacheIdentity))).Replace("-", "");

            DateTime previousWeek = week.AddDays(-7);
            WeeklyLevelsFastCalculation.WeekData cached = ReadCache(previousWeek);
            if (cached != null)
            {
                engine.SeedPrevious(cached);
                lastSavedWeek = previousWeek;
            }
            minimumSourceWeek = cached == null ? previousWeek : week;
            requestFrom = minimumSourceWeek.AddDays(-2);
            status = Source == WeeklyLevelsFastSource.HistoricalTrades
                ? "Loading trade profile; chart-only price preview. VA / VWAP pending."
                : "Loading 1-minute estimates; chart-only price preview.";

            BarsRequest newRequest;
            lock (requestGate)
            {
                if (terminated)
                    return;
                newRequest = new BarsRequest(Instrument, requestFrom, last.Date.AddDays(1));
                newRequest.BarsPeriod = new BarsPeriod
                {
                    BarsPeriodType = Source == WeeklyLevelsFastSource.HistoricalTrades
                        ? BarsPeriodType.Tick : BarsPeriodType.Minute,
                    Value = 1,
                    MarketDataType = MarketDataType.Last
                };
                newRequest.TradingHours = Bars.TradingHours;
                newRequest.Update += RequestUpdated;
                request = newRequest;
            }
            newRequest.Request((result, error, message) =>
            {
                lock (requestGate)
                {
                    if (terminated)
                        return;
                    if (error != ErrorCode.NoError)
                    {
                        Fail("Historical data request failed: " + error + ". " + message);
                        return;
                    }
                    if (result.Bars.Count == 0)
                    {
                        Fail("No source data returned. Check the connection and historical-data entitlement.");
                        return;
                    }
                    sourceSessions = new SessionIterator(result.Bars);
                    requestReady = true;
                    Interlocked.Exchange(ref dirty, 1);
                }
                ScheduleWork(null);
            });
        }

        private string BuildCacheIdentity()
        {
            StringBuilder key = new StringBuilder("WeeklyLevelsFast-v1|");
            key.Append(Instrument.FullName).Append('|')
                .Append(TickSize.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(Bars.TradingHours.Name).Append('|')
                .Append(Bars.TradingHours.TimeZoneInfo.Id).Append('|')
                .Append(Core.Globals.GeneralOptions.TimeZoneInfo.Id).Append('|')
                .Append(Source).Append('|').Append(IbDuration);
            foreach (var session in Bars.TradingHours.Sessions)
                key.Append('|').Append(session.BeginDay).Append(':').Append(session.BeginTime)
                    .Append(':').Append(session.EndDay).Append(':').Append(session.EndTime)
                    .Append(':').Append(session.TradingDay);
            return key.ToString();
        }

        private string CachePath(DateTime week)
        {
            return Path.Combine(Core.Globals.UserDataDir, "cache", "WeeklyLevelsFast",
                cachePrefix + "-" + week.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".bin");
        }

        private WeeklyLevelsFastCalculation.WeekData ReadCache(DateTime week)
        {
            if (!UseCompletedWeekCache || !File.Exists(CachePath(week)))
                return null;
            try
            {
                return WeeklyLevelsFastCalculation.WeeklyCache.Load(CachePath(week), cacheIdentity, week);
            }
            catch (InvalidDataException error) { Warn("Invalid cache ignored: " + error.Message); }
            catch (IOException error) { Warn("Cache ignored: " + error.Message); }
            catch (UnauthorizedAccessException error) { Warn("Cache unreadable: " + error.Message); }
            catch (ArgumentException error) { Warn("Invalid cache: " + error.Message); }
            return null;
        }

        private void SavePrevious()
        {
            WeeklyLevelsFastCalculation.WeekData prior = engine.Previous;
            if (!UseCompletedWeekCache || prior == null || prior.Key == lastSavedWeek
                || !cacheableWeeks.Contains(prior.Key)
                || requestFrom > prior.Begin.Date.AddDays(-1))
                return;
            try
            {
                WeeklyLevelsFastCalculation.WeeklyCache.Save(CachePath(prior.Key), cacheIdentity, prior);
                lastSavedWeek = prior.Key;
            }
            catch (IOException error) { Warn("Cache not saved: " + error.Message); }
            catch (UnauthorizedAccessException error) { Warn("Cache not saved: " + error.Message); }
        }

        private static void LocateSession(SessionIterator iterator, DateTime time,
            ref bool initialized, bool includeEnd)
        {
            if (!initialized || time > iterator.ActualSessionEnd
                || (!includeEnd && time == iterator.ActualSessionEnd)
                || time < iterator.ActualSessionBegin)
            {
                iterator.GetNextSession(time, includeEnd);
                initialized = true;
            }
        }

        private void RequestUpdated(object sender, BarsUpdateEventArgs args)
        {
            Interlocked.Exchange(ref dirty, 1);
        }

        private void ScheduleWork(object unused)
        {
            if (terminated || failed || !requestReady || Interlocked.CompareExchange(ref dirty, 0, 1) != 1)
                return;
            if (Interlocked.CompareExchange(ref workerScheduled, 1, 0) != 0)
            {
                Interlocked.Exchange(ref dirty, 1);
                return;
            }
            ThreadPool.QueueUserWorkItem(ProcessSource);
        }

        private void ProcessSource(object unused)
        {
            try
            {
                bool finished = false;
                bool revisitLast = true;
                while (!finished && !terminated)
                {
                    lock (requestGate)
                    {
                        if (terminated || request == null || failed)
                            return;
                        Bars bars = request.Bars;
                        int target = bars.Count - 1;
                        int first = Math.Max(0, sourceIndex + (revisitLast ? 0 : 1));
                        int stop = Math.Min(target, first + 9999);
                        bool trade = Source == WeeklyLevelsFastSource.HistoricalTrades;
                        for (int i = first; i <= stop; i++)
                        {
                            DateTime time = bars.GetTime(i);
                            if (time.Ticks > Interlocked.Read(ref cutoffTicks))
                            {
                                finished = true;
                                break;
                            }
                            LocateSession(sourceSessions, time, ref sourceSessionReady, !trade);
                            DateTime week = WeeklyLevelsFastCalculation.WeeklyEngine.Monday(
                                sourceSessions.ActualTradingDayExchange);
                            sourceIndex = i;
                            if (week < minimumSourceWeek)
                                continue;
                            if (week != sourceWeek)
                            {
                                bool firstWeek = sourceWeek == DateTime.MinValue;
                                sourceWeek = week;
                                sourceWeekBegin = sourceSessions.GetTradingDayBeginLocal(week);
                                if (time <= sourceWeekBegin.AddMinutes(1))
                                    cacheableWeeks.Add(week);
                                else
                                    Warn("Source begins after the scheduled weekly open for "
                                        + week.ToString("yyyy-MM-dd") + "; levels may be partial.");
                                if (firstWeek && week > minimumSourceWeek)
                                    Warn("Requested earlier-week history was not returned; check data coverage.");
                            }
                            engine.Update(i, week, sourceWeekBegin,
                                time, bars.GetOpen(i), bars.GetHigh(i), bars.GetLow(i),
                                bars.GetClose(i), bars.GetVolume(i), trade);
                        }
                        revisitLast = false;
                        finished = finished || sourceIndex >= target;
                        if (!sourcePublished && !finished)
                            status = "Building profile: " + (sourceIndex + 1).ToString("N0")
                                + " / " + bars.Count.ToString("N0") + " source bars. VA / VWAP pending.";
                    }
                    if (!finished)
                        Thread.Yield();
                }
                if (terminated)
                    return;
                if (engine.Current == null)
                {
                    Fail("No source trades/bars were returned within the chart's time range.");
                    return;
                }
                snapshot = engine.Snapshot(ValueAreaPercent, CurrentWeekVaMidAsPoc,
                    PriorWeekVaMidAsPoc, true);
                sourcePublished = true;
                status = Source == WeeklyLevelsFastSource.HistoricalTrades
                    ? "Trade-volume profile" : "ESTIMATED: 1-minute profile / typical-price VWAP";
                if (!snapshot.IbComplete)
                    status += " | IB developing";
                if (engine.Previous == null)
                    status += " | Prior week unavailable";
                SavePrevious();
            }
            catch (InvalidOperationException error) { Fail("Calculation stopped: " + error.Message); }
            catch (OverflowException error) { Fail("Calculation stopped: " + error.Message); }
            finally
            {
                Interlocked.Exchange(ref workerScheduled, 0);
                if (!terminated && ChartControl != null)
                    ChartControl.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!terminated && ChartControl != null)
                            ChartControl.InvalidateVisual();
                    }));
            }
        }

        private void Warn(string message)
        {
            warning = message;
            Log(Name + ": " + message, LogLevel.Warning);
        }

        private void Fail(string message)
        {
            failed = true;
            requestReady = false;
            sourcePublished = false;
            snapshot = null;
            previewSnapshot = null;
            status = "ERROR: " + message;
            Log(Name + ": " + message, LogLevel.Error);
        }

        private bool Enabled(int index)
        {
            if (index <= 2) return WeeklyIB;
            if (index <= 4) return Extensions50;
            if (index <= 6) return Extensions100;
            if (index <= 8) return Extensions150;
            if (index <= 10) return Extensions200;
            if (index <= 13) return PriorWeekHLC;
            if (index == 14) return CurrentWeekOpen;
            if (index <= 17) return CurrentWeekValueArea;
            if (index <= 20) return PriorWeekValueArea;
            if (index == 21) return CurrentWeekVWAP;
            if (index == 22) return PriorWeekOpen;
            if (index == 24) return PriorWeekMid;
            return CurrentWeekMid;
        }

        public override void OnRenderTargetChanged()
        {
            DisposeRenderResources();
        }

        private void DisposeRenderResources()
        {
            if (dxBrushes != null)
                foreach (SharpDX.Direct2D1.Brush brush in dxBrushes)
                    if (brush != null) brush.Dispose();
            if (dxStyles != null)
                foreach (SharpDX.Direct2D1.StrokeStyle style in dxStyles)
                    if (style != null) style.Dispose();
            if (textFormat != null) textFormat.Dispose();
            dxBrushes = null;
            dxStyles = null;
            brushSources = null;
            textFormat = null;
        }

        private void EnsureRenderResources()
        {
            if (dxBrushes == null)
            {
                dxBrushes = new SharpDX.Direct2D1.Brush[26];
                brushSources = new System.Windows.Media.Brush[26];
                dxStyles = new SharpDX.Direct2D1.StrokeStyle[5];
                SharpDX.Direct2D1.DashStyle[] dashes = {
                    SharpDX.Direct2D1.DashStyle.Solid, SharpDX.Direct2D1.DashStyle.Dash,
                    SharpDX.Direct2D1.DashStyle.Dot, SharpDX.Direct2D1.DashStyle.DashDot,
                    SharpDX.Direct2D1.DashStyle.DashDotDot
                };
                for (int i = 0; i < dashes.Length; i++)
                    dxStyles[i] = new SharpDX.Direct2D1.StrokeStyle(Core.Globals.D2DFactory,
                        new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = dashes[i], MiterLimit = 10 });
                textFormat = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory,
                    "Arial", LabelFontSize);
            }
            for (int i = 0; i < 26; i++)
            {
                if (dxBrushes[i] == null || !ReferenceEquals(brushSources[i], Plots[i].Brush))
                {
                    if (dxBrushes[i] != null) dxBrushes[i].Dispose();
                    brushSources[i] = Plots[i].Brush;
                    dxBrushes[i] = Plots[i].Brush.ToDxBrush(RenderTarget);
                }
            }
        }

        private int StyleIndex(DashStyleHelper dash)
        {
            if (dash == DashStyleHelper.Dash) return 1;
            if (dash == DashStyleHelper.Dot) return 2;
            if (dash == DashStyleHelper.DashDot) return 3;
            if (dash == DashStyleHelper.DashDotDot) return 4;
            return 0;
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (RenderTarget == null || ChartPanel == null || terminated)
                return;
            EnsureRenderResources();
            float left = ChartPanel.X;
            float right = ChartPanel.X + ChartPanel.W;
            float x2 = Math.Max(left, right - EdgeOffset - 2);
            float x1 = Math.Max(left, x2 - TotalWidth);
            WeeklyLevelsFastCalculation.LevelSnapshot view =
                failed ? null : (sourcePublished ? snapshot : previewSnapshot);
            if (view != null)
            {
                if (WeeklyIBHighlight && WeeklyIB)
                    FillBand(view.Levels[0], view.Levels[2], x1, x2, chartScale, dxBrushes[0]);
                if (PriorWeekVAHighlight && PriorWeekValueArea)
                    FillBand(view.Levels[18], view.Levels[20], x1, x2, chartScale, dxBrushes[18]);
                List<float> labelPositions = new List<float>();
                for (int i = 0; i < 26; i++)
                {
                    double price = view.Levels[i];
                    if (!Enabled(i) || double.IsNaN(price) || double.IsInfinity(price))
                        continue;
                    float y = chartScale.GetYByValue(price);
                    if (y < ChartPanel.Y || y > ChartPanel.Y + ChartPanel.H)
                        continue;
                    RenderTarget.DrawLine(new SharpDX.Vector2(x1, y), new SharpDX.Vector2(x2, y),
                        dxBrushes[i], (float)Plots[i].Width, dxStyles[StyleIndex(Plots[i].DashStyleHelper)]);
                    if (!ShowLabels)
                        continue;
                    string label = Plots[i].Name;
                    if (ShowPriceOnLabels)
                        label += " " + Instrument.MasterInstrument.FormatPrice(price);
                    using (SharpDX.DirectWrite.TextLayout layout = new SharpDX.DirectWrite.TextLayout(
                        Core.Globals.DirectWriteFactory, label, textFormat, ChartPanel.W, LabelFontSize * 2))
                    {
                        float labelY = y - layout.Metrics.Height - 1;
                        bool moved;
                        do
                        {
                            moved = false;
                            foreach (float existing in labelPositions)
                                if (Math.Abs(labelY - existing) < LabelFontSize + 2)
                                {
                                    labelY = existing - LabelFontSize - 2;
                                    moved = true;
                                    break;
                                }
                        } while (moved);
                        if (labelY >= ChartPanel.Y)
                        {
                            labelPositions.Add(labelY);
                            float labelX = Math.Max(left, x2 - layout.Metrics.Width);
                            RenderTarget.DrawTextLayout(new SharpDX.Vector2(labelX, labelY),
                                layout, dxBrushes[i]);
                        }
                    }
                }
            }
            string text = "WeeklyLevelsFast | " + status;
            if (!string.IsNullOrEmpty(warning))
                text += "\nWARNING: " + warning;
            using (SharpDX.DirectWrite.TextLayout layout = new SharpDX.DirectWrite.TextLayout(
                Core.Globals.DirectWriteFactory, text, textFormat, Math.Max(50, ChartPanel.W - 16), 70))
                RenderTarget.DrawTextLayout(new SharpDX.Vector2(left + 8, ChartPanel.Y + 8),
                    layout, dxBrushes[failed ? 4 : 0]);
        }

        private void FillBand(double high, double low, float x1, float x2, ChartScale scale,
            SharpDX.Direct2D1.Brush brush)
        {
            if (double.IsNaN(high) || double.IsNaN(low))
                return;
            float top = Math.Max(ChartPanel.Y, scale.GetYByValue(high));
            float bottom = Math.Min(ChartPanel.Y + ChartPanel.H, scale.GetYByValue(low));
            if (bottom <= top)
                return;
            float opacity = brush.Opacity;
            brush.Opacity = 0.10f;
            RenderTarget.FillRectangle(new SharpDX.RectangleF(x1, top, x2 - x1, bottom - top), brush);
            brush.Opacity = opacity;
        }

        [NinjaScriptProperty]
        [Display(Name = "Weekly IB duration (HH:MM)", GroupName = "1. Parameters", Order = 0)]
        public string IbDuration { get; set; }

        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Value Area (%)", GroupName = "1. Parameters", Order = 1)]
        public double ValueAreaPercent { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Profile / VWAP source", GroupName = "1. Parameters", Order = 2)]
        public WeeklyLevelsFastSource Source { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use completed-week cache", GroupName = "1. Parameters", Order = 3)]
        public bool UseCompletedWeekCache { get; set; }

        [NinjaScriptProperty, Range(100, 5000)]
        [Display(Name = "Refresh interval (ms)", GroupName = "1. Parameters", Order = 4)]
        public int RefreshMilliseconds { get; set; }

        [Display(Name = "Weekly IB", GroupName = "2. Plots to Show", Order = 0)]
        public bool WeeklyIB { get; set; }
        [Display(Name = "Weekly IB Highlight", GroupName = "2. Plots to Show", Order = 1)]
        public bool WeeklyIBHighlight { get; set; }
        [Display(Name = "WIB 50% Extensions", GroupName = "2. Plots to Show", Order = 2)]
        public bool Extensions50 { get; set; }
        [Display(Name = "WIB 100% Extensions", GroupName = "2. Plots to Show", Order = 3)]
        public bool Extensions100 { get; set; }
        [Display(Name = "WIB 150% Extensions", GroupName = "2. Plots to Show", Order = 4)]
        public bool Extensions150 { get; set; }
        [Display(Name = "WIB 200% Extensions", GroupName = "2. Plots to Show", Order = 5)]
        public bool Extensions200 { get; set; }
        [Display(Name = "Current Week Open", GroupName = "2. Plots to Show", Order = 6)]
        public bool CurrentWeekOpen { get; set; }
        [Display(Name = "Current Week Mid", GroupName = "2. Plots to Show", Order = 7)]
        public bool CurrentWeekMid { get; set; }
        [Display(Name = "Current Week VAH / VAL / VPOC", GroupName = "2. Plots to Show", Order = 8)]
        public bool CurrentWeekValueArea { get; set; }
        [Display(Name = "Use Current Week VA MID as POC", GroupName = "2. Plots to Show", Order = 9)]
        public bool CurrentWeekVaMidAsPoc { get; set; }
        [Display(Name = "Current Week VWAP", GroupName = "2. Plots to Show", Order = 10)]
        public bool CurrentWeekVWAP { get; set; }
        [Display(Name = "Prior Week HLC", GroupName = "2. Plots to Show", Order = 11)]
        public bool PriorWeekHLC { get; set; }
        [Display(Name = "Prior Week Open", GroupName = "2. Plots to Show", Order = 12)]
        public bool PriorWeekOpen { get; set; }
        [Display(Name = "Prior Week Mid", GroupName = "2. Plots to Show", Order = 13)]
        public bool PriorWeekMid { get; set; }
        [Display(Name = "Prior Week VAH / VAL / VPOC", GroupName = "2. Plots to Show", Order = 14)]
        public bool PriorWeekValueArea { get; set; }
        [Display(Name = "Prior Week VA Highlight", GroupName = "2. Plots to Show", Order = 15)]
        public bool PriorWeekVAHighlight { get; set; }
        [Display(Name = "Use Prior Week VA MID as POC", GroupName = "2. Plots to Show", Order = 16)]
        public bool PriorWeekVaMidAsPoc { get; set; }
        [Range(20, 2000)]
        [Display(Name = "Total width (pixels)", GroupName = "3. Plot Appearance", Order = 0)]
        public int TotalWidth { get; set; }
        [Range(0, 2000)]
        [Display(Name = "Edge offset (pixels)", GroupName = "3. Plot Appearance", Order = 1)]
        public int EdgeOffset { get; set; }
        [Display(Name = "Show plot labels", GroupName = "3. Plot Appearance", Order = 2)]
        public bool ShowLabels { get; set; }
        [Display(Name = "Show price on labels", GroupName = "3. Plot Appearance", Order = 3)]
        public bool ShowPriceOnLabels { get; set; }
        [Range(8, 24)]
        [Display(Name = "Label font size", GroupName = "3. Plot Appearance", Order = 4)]
        public int LabelFontSize { get; set; }
    }
}
#endif
