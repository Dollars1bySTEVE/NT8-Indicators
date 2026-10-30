using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Compile the actual callback/helper bodies with small feed stubs, without NT8 or SharpDX.
var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../Indicators/LiquidityPulse.cs"));
var source = File.ReadAllText(path);
var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp5));
var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
if (errors.Length != 0)
    throw new Exception(string.Join(Environment.NewLine, errors.Select(d => d.ToString())));
var indicator = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
    .Single(c => c.Identifier.Text == "LiquidityPulse");
var names = new HashSet<string>
{
    "DepthRow", "WallState", "ResidualZone", "ResidualZoneState", "OnMarketData", "OnMarketDepth",
    "HasValidQuotes", "SanitizeDepthLocked", "SanitizeDepthSideLocked", "GetPulledZoneFadeFactor",
    "IsPriceInVisibleWindowLocked", "AddResidualZoneLocked", "IsResidualZoneLive", "PruneEventHistoryLocked",
    "EventMarker", "WallFade", "LiquidityEventType", "InteractionWatch", "LevelInteraction",
    "InteractionLoggingEnabled", "InteractionFeedIssueLocked", "InterruptInteractionsLocked",
    "DiscoverInteractionLevelsLocked", "RegisterInteractionLevelLocked", "UpdateInteractionClockLocked",
    "ProcessInteractionTradeLocked", "UpdateInteractionDepthLocked", "GetInteractionSizeLocked",
    "EndInteractionObservationLocked", "CompleteInteractionHorizonsLocked", "LogInteractionLocked",
    "ResetBookLocked", "AdvanceClockLocked"
};
var members = indicator.Members.Where(m => names.Contains(m switch
{
    MethodDeclarationSyntax method => method.Identifier.Text,
    BaseTypeDeclarationSyntax type => type.Identifier.Text,
    _ => ""
})).ToArray();
if (members.Length != names.Count)
    throw new Exception("A regression-tested member was renamed or removed.");
var fixture = """
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Linq;
enum MarketDataType { Bid, Ask, Last }
enum Operation { Add, Update, Remove }
enum State { Realtime, Historical }
enum SessionPhase { Rth, PostClose }
class MarketDataEventArgs
{
    public MarketDataType MarketDataType;
    public DateTime Time;
    public double Price, Bid, Ask;
    public long Volume;
}
class MarketDepthEventArgs : MarketDataEventArgs
{
    public bool IsReset;
    public int Position;
    public Operation Operation;
}
class MasterInstrument
{
    public double TickSize = 0.25;
    public double RoundToTickSize(double value) { return Math.Round(value / TickSize) * TickSize; }
}
class InstrumentStub { public MasterInstrument MasterInstrument = new MasterInstrument(); }
class IndicatorStub
{
    protected virtual void OnMarketData(MarketDataEventArgs e) {}
    protected virtual void OnMarketDepth(MarketDepthEventArgs e) {}
}
static class FloatingPoint
{
    public static int ApproxCompare(this double a, double b) { return a.CompareTo(b); }
}
class LiquidityPulse : IndicatorStub
{
    int BarsInProgress;
    object bookLock = new object();
    InstrumentStub Instrument = new InstrumentStub();
    double lastBid = double.NaN, lastAsk = double.NaN, lastTradePrice = double.NaN;
    double sessionAggBuy, sessionAggSell;
    long processedTradeUpdates, ignoredSecondaryTradeUpdates;
    bool tradeQuotesInvalid, stale;
    State State = State.Realtime;
    SessionPhase phase = SessionPhase.Rth;
    bool EnableSessionLog, EnableInteractionLog = true, sessionLogFailed;
    string interactionRunId = Guid.NewGuid().ToString("N");
    Dictionary<string, InteractionWatch> interactionWatches = new Dictionary<string, InteractionWatch>();
    List<LevelInteraction> interactions = new List<LevelInteraction>();
    static int[] interactionHorizons = { 30, 120, 600, 3600 };
    static int[] interactionBarriers = { 8, 16, 32 };
    long interactionSequence, interactionLevelSequence;
    DateTime lastInteractionTrade, lastInteractionDepth, lastClockTime;
    bool pendingDetection, crossClose;
    List<int> sweepCandidates = new List<int>();
    int KeyLevelProximityTicks = 2;
    double depthAgeSeconds;
    DateTime lastBidUpdate, lastAskUpdate, lastTradeUpdate, clock;
    List<DepthRow> askRows = new List<DepthRow>(), bidRows = new List<DepthRow>();
    Dictionary<double, WallState> askWalls = new Dictionary<double, WallState>();
    Dictionary<double, WallState> bidWalls = new Dictionary<double, WallState>();
    List<WallState> retiredAskWalls = new List<WallState>(), retiredBidWalls = new List<WallState>();
    int PulledZoneFadeMinutes = 15, LargePulledZoneFadeMinutes = 45;
    int WallMinAgeSeconds = 3, EventMarkerRetentionMinutes = 30, WallFadeDurationSeconds = 10;
    int ResidualZoneMaxMinutes = 120, TakenZoneFadeMinutes = 5;
    List<ResidualZone> residualZones = new List<ResidualZone>();
    List<EventMarker> eventMarkers = new List<EventMarker>();
    List<WallFade> wallFades = new List<WallFade>();
    int detectionCalls, processCalls, throughCalls, neutralCalls;
    List<string> logs = new List<string>();
    List<string> details = new List<string>();
    List<DateTime> logTimes = new List<DateTime>();
    void SetEventTime(DateTime time, bool depth) { clock = time; }
    DateTime FeedNow() { return clock; }
    bool CrossedSessionCloseLocked(DateTime previous, DateTime time) { return crossClose; }
    void ClearRecentTradesLocked() {}
    SessionPhase GetSessionPhase(DateTime time) { return phase; }
    string FindNearestKeyLevelLocked(double price, int ticks) { return "S-POC"; }
    bool IsBookStale() { return stale; }
    bool IsDepthOlderThan(double seconds) { return depthAgeSeconds > seconds; }
    void RunDetectionLocked(DateTime time, bool force) { detectionCalls++; }
    void ThrottledForceRefresh() {}
    void RecordTradeThroughLocked(double price) { throughCalls++; }
    void UpdateResidualZoneTradesLocked(DateTime time, double price, double volume, int side) {}
    void ProcessTradeLocked(DateTime time, double price, double volume, int side)
    { processCalls++; if (side == 0) neutralCalls++; }
    void LogSessionLocked(DateTime time, string kind, string side, double price, long size, string detail)
    { logs.Add(kind); details.Add(detail); logTimes.Add(time); }
    static int assertions;
    static void Check(bool value, string message)
    { assertions++; if (!value) throw new Exception(message); }
    void Fresh(DateTime time)
    { lastBidUpdate = lastAskUpdate = lastTradeUpdate = time; }
    static LiquidityPulse InteractionFixture(DateTime now, bool isAsk)
    {
        var lp = new LiquidityPulse { EnableSessionLog = true, lastBid = 100, lastAsk = 100.25 };
        var rows = isAsk ? lp.askRows : lp.bidRows;
        rows.Add(new DepthRow { Price = 100, Size = 30 });
        rows.Add(new DepthRow { Price = isAsk ? 102 : 98, Size = 10 });
        lp.RegisterInteractionLevelLocked(100, isAsk, now.AddMinutes(-1), 30, 10, "WALL");
        return lp;
    }
    public static void Run()
    {
        DateTime now = new DateTime(2026, 10, 29, 22, 0, 0);
        var lp = new LiquidityPulse();
        var print = new MarketDataEventArgs { Time = now, MarketDataType = MarketDataType.Last,
            Price = 100.25, Bid = 100, Ask = 100.25, Volume = 25 };
        for (int series = 0; series < 3; series++)
        { lp.BarsInProgress = series; lp.OnMarketData(print); }
        Check(lp.sessionAggBuy == 25 && lp.processedTradeUpdates == 1
            && lp.ignoredSecondaryTradeUpdates == 2, "Primary trade must count once across three series");
        Check(lp.processCalls == 1 && lp.throughCalls == 1 && lp.detectionCalls == 1,
            "Secondary callbacks must not affect detection or trade-through tracking");
        lp.BarsInProgress = 0;
        lp.OnMarketData(print);
        Check(lp.sessionAggBuy == 50, "Genuine identical primary prints must not be deduplicated");
        print.Bid = 101; print.Ask = 99;
        lp.OnMarketData(print); lp.OnMarketData(print);
        Check(lp.sessionAggBuy == 50 && lp.sessionAggSell == 0 && lp.neutralCalls == 2,
            "Crossed quotes must not create classified volume");
        Check(lp.logs.Count == 1 && lp.logs[0] == "QUOTE_INVALID", "Invalid quote logging is transition-only");
        print.Bid = 100; print.Ask = 100.25;
        lp.OnMarketData(print);
        Check(lp.logs[1] == "QUOTE_RECOVERED", "Quote recovery must be logged");
        lp.lastBid = 100; lp.lastAsk = 100.25;
        print.Bid = 0; print.Ask = double.NaN;
        lp.OnMarketData(print);
        Check(lp.sessionAggBuy == 75 && lp.logs[2] == "QUOTE_INVALID",
            "Missing trade quotes must not fall back to cached quotes");
        print.Bid = 100; print.Ask = 100.25;
        Check(!HasValidQuotes(double.NaN, 101) && !HasValidQuotes(100, 100)
            && !HasValidQuotes(100, double.PositiveInfinity), "Reject invalid and locked quotes");

        lp = new LiquidityPulse();
        var depth = new MarketDepthEventArgs { Time = now, MarketDataType = MarketDataType.Ask,
            Operation = Operation.Add, Position = 0, Price = 101, Volume = 12 };
        for (int series = 0; series < 3; series++)
        { lp.BarsInProgress = series; lp.OnMarketDepth(depth); }
        Check(lp.askRows.Count == 1 && lp.detectionCalls == 1, "Depth positions must not be triplicated");
        lp.BarsInProgress = 0;
        lp.askRows[0].Price = 97;
        lp.askWalls[97] = new WallState { Price = 97, Active = true };
        lp.retiredAskWalls.Add(new WallState { Price = 97 });
        lp.askRows.Add(new DepthRow { Price = 102, Size = 20 });
        lp.bidRows.Add(new DepthRow { Price = 103, Size = 15 });
        lp.bidRows.Add(new DepthRow { Price = 99.5, Size = 15 });
        lp.lastBid = 100; lp.lastAsk = 100.25; lp.lastTradePrice = 100;
        lp.Fresh(now);
        lp.SanitizeDepthLocked(now);
        lp.Fresh(now.AddMilliseconds(999));
        lp.SanitizeDepthLocked(now.AddMilliseconds(999));
        Check(lp.askRows[0].Size == 12, "Transient crossings must survive the one-second grace");
        lp.Fresh(now.AddSeconds(1));
        lp.SanitizeDepthLocked(now.AddSeconds(1));
        Check(lp.askRows.Count == 2 && lp.askRows[0].Size == 0 && lp.askRows[1].Size == 20,
            "Invalidation must preserve positional indices");
        Check(lp.bidRows[0].Size == 0 && lp.bidRows[1].Size == 15, "Bid and ask checks must be symmetric");
        Check(lp.askWalls.Count == 0 && lp.retiredAskWalls.Count == 0,
            "Invalid walls must not emit disappearance events or become residual zones");
        Check(!lp.IsPriceInVisibleWindowLocked(lp.askRows, 97, true),
            "Invalidated rows must not count as visible zone tests");
        Check(lp.logs.Contains("DEPTH_INVALIDATED"), "Invalidation must be recorded");
        depth.Operation = Operation.Update; depth.Position = 1; depth.Price = 102.25; depth.Volume = 30;
        lp.OnMarketDepth(depth);
        Check(lp.askRows[0].Size == 0 && lp.askRows[1].Price == 102.25 && lp.askRows[1].Size == 30,
            "A later positional update must target the original row");
        depth.Position = 0; depth.Price = 101; depth.Volume = 25;
        lp.OnMarketDepth(depth);
        Check(!lp.askRows[0].Invalidated && lp.askRows[0].CrossedSince == DateTime.MinValue,
            "A replacement price must recover a quarantined slot");

        foreach (bool crossedQuotes in new[] { false, true })
        {
            lp = new LiquidityPulse();
            lp.lastBid = crossedQuotes ? 102 : 100; lp.lastAsk = 100.25; lp.lastTradePrice = 100;
            lp.askRows.Add(new DepthRow { Price = 97, Size = 10, CrossedSince = now });
            lp.Fresh(now);
            lp.SanitizeDepthLocked(now.AddSeconds(3));
            Check(lp.askRows[0].Size == 10 && lp.askRows[0].CrossedSince == DateTime.MinValue,
                "Stale or crossed quotes must not authorize depth invalidation");
        }
        lp = new LiquidityPulse();
        lp.lastBid = 100; lp.lastAsk = 100.25; lp.lastTradePrice = 100; lp.Fresh(now);
        lp.askRows.Add(new DepthRow { Price = 99, Size = 10 });
        lp.SanitizeDepthLocked(now); lp.Fresh(now.AddSeconds(1)); lp.SanitizeDepthLocked(now.AddSeconds(1));
        Check(lp.askRows[0].Size == 10, "Exactly four ticks must remain inside the tolerance");
        lp.askRows[0].Price = 97;
        lp.stale = true;
        lp.SanitizeDepthLocked(now.AddSeconds(1));
        Check(lp.askRows[0].CrossedSince == DateTime.MinValue, "Stale depth must suspend validation");
        lp.stale = false; lp.depthAgeSeconds = 3;
        lp.askRows[0].CrossedSince = now; lp.Fresh(now.AddSeconds(1));
        lp.SanitizeDepthLocked(now.AddSeconds(1));
        Check(lp.askRows[0].Size == 10 && lp.askRows[0].CrossedSince == DateTime.MinValue,
            "Depth older than two seconds must not authorize invalidation");
        lp.askRows.Add(new DepthRow { Price = 98, Size = 0 });
        lp.bidRows.Add(new DepthRow { Price = 96, Size = 0 });
        Check(lp.IsPriceInVisibleWindowLocked(lp.askRows, 98, true)
            && lp.IsPriceInVisibleWindowLocked(lp.askRows, 96.5, true)
            && !lp.IsPriceInVisibleWindowLocked(lp.askRows, 95.5, true),
            "Ordinary zero-size rows remain visible depth levels and opposite edges");

        lp = new LiquidityPulse();
        var zone = new ResidualZone { Resolved = now, PeakSize = 29, QualifyingThreshold = 10 };
        Check(lp.GetPulledZoneFadeFactor(zone, now) == 1, "Ordinary fade starts at full relative opacity");
        Check(lp.GetPulledZoneFadeFactor(zone, now.AddMinutes(7.5)) == 0.5, "Ordinary half-life");
        Check(lp.GetPulledZoneFadeFactor(zone, now.AddMinutes(15)) == 0, "Ordinary expiration boundary");
        zone.PeakSize = 30;
        Check(lp.GetPulledZoneFadeFactor(zone, now.AddMinutes(22.5)) == 0.5, "3x boundary uses large tier");
        zone.PeakSize = 99;
        Check(lp.GetPulledZoneFadeFactor(zone, now.AddMinutes(45)) == 0, "Below 10x still expires");
        zone.PeakSize = 100;
        Check(lp.GetPulledZoneFadeFactor(zone, now.AddMinutes(120)) == 1, "10x tier retains opacity");
        zone.PeakSize = 12; zone.QualifyingThreshold = 9; lp.PulledZoneFadeMinutes = 0;
        Check(lp.GetPulledZoneFadeFactor(zone, now) == 0, "Zero ordinary duration hides immediately");
        zone.PeakSize = 27; lp.LargePulledZoneFadeMinutes = 0;
        Check(lp.GetPulledZoneFadeFactor(zone, now) == 0, "Zero large duration hides immediately");
        Check(lp.GetPulledZoneFadeFactor(zone, now.AddMinutes(-1)) >= 0, "Clock jitter must not create negative opacity");
        lp = new LiquidityPulse();
        var wall = new WallState { Price = 100, IsAsk = true, FirstSeen = now.AddMinutes(-1),
            LastSeenAboveThreshold = now, PeakSize = 100, QualifyingThreshold = 10 };
        lp.AddResidualZoneLocked(wall, now);
        Check(lp.residualZones.Count == 1 && lp.residualZones[0].QualifyingThreshold == 10,
            "Zone must inherit the wall's original qualifying threshold");
        wall.QualifyingThreshold = 20; wall.PeakSize = 120;
        lp.AddResidualZoneLocked(wall, now);
        Check(lp.residualZones.Count == 1 && lp.residualZones[0].QualifyingThreshold == 10
            && lp.residualZones[0].PeakSize == 120, "Live-zone merges preserve the original threshold");
        lp.residualZones[0].State = ResidualZoneState.Gone;
        lp.residualZones[0].Created = now.AddMinutes(-121);
        lp.residualZones[0].Resolved = now;
        lp.residualZones.Add(new ResidualZone { Created = now, Resolved = now.AddMinutes(-15),
            PeakSize = 20, QualifyingThreshold = 10, State = ResidualZoneState.Gone });
        lp.residualZones.Add(new ResidualZone { Created = now, Resolved = now.AddMinutes(-45),
            PeakSize = 30, QualifyingThreshold = 10, State = ResidualZoneState.Gone });
        lp.residualZones.Add(new ResidualZone { Created = now, Resolved = now.AddMinutes(-5),
            PeakSize = 20, QualifyingThreshold = 10, State = ResidualZoneState.Taken });
        lp.PruneEventHistoryLocked(now);
        Check(lp.residualZones.Count == 0, "Prune expires each fade tier and caps exceptional zones by original age");

        foreach (bool isAsk in new[] { false, true })
        {
            double direction = isAsk ? -1 : 1;
            lp = InteractionFixture(now, isAsk);
            lp.ProcessInteractionTradeLocked(now, 100 + direction, 5, 0, false);
            var interaction = lp.interactions[0];
            Check(lp.logs.Contains("INTERACTION_START") && interaction.LastSize == 30,
                "Approach starts with the currently visible size");
            Check(!lp.logs.Contains("INTERACTION_CROSS"), "Starting beyond a level must not invent a crossing");
            lp.ProcessInteractionTradeLocked(now.AddMilliseconds(10), 100, 7, isAsk ? 1 : -1, false);
            lp.ProcessInteractionTradeLocked(now.AddMilliseconds(20), 100, 7, isAsk ? 1 : -1, false);
            Check(interaction.Tests == 1 && (isAsk ? interaction.BuyVolume : interaction.SellVolume) == 14,
                "Continuous contact is one test; genuine identical prints both count");
            lp.ProcessInteractionTradeLocked(now.AddMilliseconds(30), 100, 3, 0, false);
            Check(interaction.NeutralVolume == 3, "Unclassified executions remain explicitly separate");
            lp.ProcessInteractionTradeLocked(now.AddMilliseconds(40), 100 - direction * 0.25, 1, 0, false);
            Check(lp.logs.Contains("INTERACTION_CROSS"), "Crossing through either side must be symmetric");
            lp.ProcessInteractionTradeLocked(now.AddMilliseconds(50), 100 + direction * 3, 1, 0, false);
            Check(lp.logs.Contains("INTERACTION_LEAVE") && lp.logs.Contains("INTERACTION_RECROSS"),
                "Departure and recross are recorded independently");
            lp.ProcessInteractionTradeLocked(now.AddMilliseconds(100), 100 + direction, 1, 0, false);
            Check(lp.logs.Contains("INTERACTION_RETURN") && lp.interactions.Count == 1,
                "Brief departure and return retain the same approach episode");
            Check(lp.logs.Count(k => k == "INTERACTION_SAMPLE") == 1,
                "Samples are throttled without suppressing contact and crossing events");

            var rows = isAsk ? lp.askRows : lp.bidRows;
            rows[0].Size = 10; lp.UpdateInteractionDepthLocked(now.AddMilliseconds(110));
            rows[0].Size = 25; lp.UpdateInteractionDepthLocked(now.AddMilliseconds(120));
            Check(interaction.RemovedSize == 20 && interaction.AddedSize == 15 && interaction.Refills == 1,
                "All observed depth decreases and subsequent increases are accumulated");
            rows.Clear(); lp.UpdateInteractionDepthLocked(now.AddMilliseconds(130));
            Check(!interaction.LastSize.HasValue && interaction.RemovedSize == 20,
                "A level outside visible depth is unknown, not zero or a withdrawal");
            rows.Add(new DepthRow { Price = 100, Size = 50 });
            lp.UpdateInteractionDepthLocked(now.AddMilliseconds(140));
            Check(interaction.AddedSize == 15 && interaction.Refills == 1,
                "Returning visibility must not manufacture replenishment");

            lp.ProcessInteractionTradeLocked(now.AddSeconds(1), 100 + direction * 3, 1, 0, false);
            lp.ProcessInteractionTradeLocked(now.AddSeconds(4), 100 + direction * 3, 1, 0, false);
            Check(!interaction.Observing && lp.logs.Contains("INTERACTION_END"),
                "Three seconds away ends observation but not price outcome tracking");
            lp.ProcessInteractionTradeLocked(now.AddSeconds(5), 100, 1, 0, false);
            Check(lp.interactions.Count == 2 && lp.interactionWatches[isAsk ? "A:100" : "B:100"].Visits == 2,
                "A subsequent approach gets its own ID and visit number");
        }

        lp = InteractionFixture(now, false);
        lp.ProcessInteractionTradeLocked(now, 100, 1, 0, false);
        var first = lp.interactions[0];
        for (int seconds = 5; seconds <= 30; seconds += 5)
            lp.ProcessInteractionTradeLocked(now.AddSeconds(seconds), seconds == 30 ? 102 : 100, 1, 0, false);
        int outcomeIndex = lp.logs.IndexOf("INTERACTION_OUTCOME");
        Check(outcomeIndex >= 0 && lp.details[outcomeIndex].Contains("horizon_seconds=30")
            && lp.details[outcomeIndex].Contains("mfe_ticks=8.0") && lp.logTimes[outcomeIndex] == now.AddSeconds(30),
            "Exact-boundary print is included in its horizon");
        lp.ProcessInteractionTradeLocked(now.AddSeconds(31), 110, 1, 0, false);
        Check(!lp.details[outcomeIndex].Contains("mfe_ticks=40.0"),
            "Later price progress cannot alter an earlier horizon");
        Check(lp.logs.Count(k => k == "INTERACTION_BARRIER") == 3,
            "Each favorable 8/16/32 tick barrier is emitted once");
        for (int seconds = 36; seconds < 3600; seconds += 5)
            lp.ProcessInteractionTradeLocked(now.AddSeconds(seconds), 110, 1, 0, false);
        lp.ProcessInteractionTradeLocked(now.AddSeconds(3600), 110, 1, 0, false);
        Check(lp.interactions.Count == 0 && lp.logs.Count(k => k == "INTERACTION_OUTCOME") == 4,
            "All four horizons finish once, including the hourly observation");
        Check(first.BuyVolume == 0 && !first.Observing, "Flow recording ends independently of hourly price tracking");

        lp = InteractionFixture(now, false);
        lp.ProcessInteractionTradeLocked(now, 100, 1, 0, false);
        for (int seconds = 5; seconds < 30; seconds += 5)
            lp.ProcessInteractionTradeLocked(now.AddSeconds(seconds), 100, 1, 0, false);
        lp.ProcessInteractionTradeLocked(now.AddSeconds(31), 110, 1, 0, false);
        outcomeIndex = lp.logs.IndexOf("INTERACTION_OUTCOME");
        Check(lp.details[outcomeIndex].Contains("mfe_ticks=0.0")
            && lp.details[outcomeIndex].Contains("observed_price=100 "),
            "First print after a horizon must not leak into its outcome");

        lp = InteractionFixture(now, false);
        lp.ProcessInteractionTradeLocked(now, 100, 1, 0, false);
        for (int seconds = 5; seconds < 30; seconds += 5)
            lp.ProcessInteractionTradeLocked(now.AddSeconds(seconds), 100, 1, 0, false);
        lp.UpdateInteractionDepthLocked(now.AddSeconds(29));
        lp.bidRows[0].Size = 50;
        lp.UpdateInteractionDepthLocked(now.AddSeconds(30.1));
        outcomeIndex = lp.logs.IndexOf("INTERACTION_OUTCOME");
        Check(lp.details[outcomeIndex].Contains("displayed_added=0 ")
            && lp.interactions[0].AddedSize == 20,
            "Post-cutoff depth changes must not leak into earlier horizon features");

        foreach (string issue in new[] { "quotes", "depth", "gap", "reset", "rewind", "close", "termination", "invalidated" })
        {
            lp = InteractionFixture(now, false);
            lp.ProcessInteractionTradeLocked(now, 100, 1, 0, false);
            if (issue == "quotes") lp.ProcessInteractionTradeLocked(now.AddSeconds(1), 100, 1, 0, true);
            if (issue == "depth") { lp.depthAgeSeconds = 3; lp.UpdateInteractionClockLocked(now.AddSeconds(1)); }
            if (issue == "gap") lp.UpdateInteractionClockLocked(now.AddSeconds(11));
            if (issue == "reset") lp.ResetBookLocked();
            if (issue == "rewind") { lp.lastClockTime = now; lp.AdvanceClockLocked(now.AddSeconds(-3)); }
            if (issue == "close") { lp.phase = SessionPhase.PostClose; lp.UpdateInteractionClockLocked(now); }
            if (issue == "termination") lp.InterruptInteractionsLocked(now, "indicator_terminated");
            if (issue == "invalidated")
            {
                lp.lastBid = 104; lp.lastAsk = 104.25; lp.lastTradePrice = 104;
                lp.bidRows[0].Price = 108; lp.bidRows[0].CrossedSince = now.AddSeconds(-2);
                lp.Fresh(now); lp.SanitizeDepthLocked(now);
            }
            Check(lp.interactions.Count == 0 && lp.logs.Contains("INTERACTION_INTERRUPTED")
                && !lp.logs.Contains("INTERACTION_OUTCOME"), "Unreliable observations are censored: " + issue);
        }

        lp = InteractionFixture(now, false);
        lp.EnableSessionLog = false;
        lp.ProcessInteractionTradeLocked(now, 100, 1, 0, false);
        lp.EnableSessionLog = true; lp.EnableInteractionLog = false;
        lp.ProcessInteractionTradeLocked(now, 100, 1, 0, false);
        lp.EnableInteractionLog = true; lp.State = State.Historical;
        lp.ProcessInteractionTradeLocked(now, 100, 1, 0, false);
        lp.State = State.Realtime; lp.sessionLogFailed = true;
        lp.ProcessInteractionTradeLocked(now, 100, 1, 0, false);
        Check(lp.logs.Count == 0 && lp.interactions.Count == 0,
            "Disabled, historical and failed logging must not collect interaction state");

        lp = InteractionFixture(now, false);
        for (int i = 0; i < 512; i++)
        {
            var watch = new InteractionWatch { Id = "capacity" + i, Price = 100, Available = true };
            lp.interactions.Add(new LevelInteraction { Watch = watch, Id = "old" + i,
                Started = now, LastTrade = now, StartPrice = 100, LastPrice = 100, Observing = false });
        }
        lp.ProcessInteractionTradeLocked(now, 100, 1, 0, false);
        Check(lp.interactions.Count == 512 && lp.details.Any(d => d.Contains("reason=capacity")),
            "Capacity limits explicitly interrupt the oldest outcome instead of silently dropping it");

        lp = InteractionFixture(now, false);
        lp.interactionWatches.Clear();
        lp.bidWalls[100] = new WallState { Price = 100, FirstSeen = now.AddSeconds(-3),
            PeakSize = 30, QualifyingThreshold = 10, Active = true };
        lp.askWalls[101] = new WallState { Price = 101, FirstSeen = now.AddSeconds(-2),
            PeakSize = 30, QualifyingThreshold = 10, Active = true };
        lp.residualZones.Add(new ResidualZone { Price = 100, Created = now.AddMinutes(-1),
            PeakSize = 50, QualifyingThreshold = 12, State = ResidualZoneState.Gone });
        lp.DiscoverInteractionLevelsLocked(now);
        Check(lp.interactionWatches.Count == 1 && lp.interactionWatches["B:100"].Source == "WALL",
            "Discovery respects minimum wall age and coalesces a wall and zone at the same side/price");
        lp.bidWalls.Clear();
        lp.DiscoverInteractionLevelsLocked(now);
        Check(lp.interactionWatches["B:100"].Source == "GONE",
            "Pulled historical areas remain eligible while they remain in retained zones");
        lp.residualZones.Clear(); lp.DiscoverInteractionLevelsLocked(now);
        Check(lp.interactionWatches.Count == 0, "Expired sources do not retain idle watches");

        lp = InteractionFixture(now, false);
        lp.ProcessInteractionTradeLocked(now, 100, 1, 0, false);
        lp.UpdateInteractionDepthLocked(now);
        lp.UpdateInteractionDepthLocked(now.AddSeconds(3));
        Check(lp.interactions.Count == 0 && lp.details.Any(d => d.Contains("reason=depth_gap")),
            "A returning depth update must not conceal the preceding feed gap");
        lp.ProcessInteractionTradeLocked(now.AddSeconds(4), 100, 1, 0, false);
        Check(lp.interactions.Count == 1 && lp.interactionSequence == 2,
            "Recovery starts an independent interaction rather than resuming censored data");

        lp = InteractionFixture(now, false);
        lp.ProcessInteractionTradeLocked(now, 100, 1, 0, false);
        lp.BarsInProgress = 1;
        lp.OnMarketData(new MarketDataEventArgs { Time = now, MarketDataType = MarketDataType.Last,
            Price = 100, Bid = 99.75, Ask = 100, Volume = 25 });
        Check(lp.interactions[0].BuyVolume == 0 && lp.interactionSequence == 1,
            "Secondary callbacks cannot add interaction trades or new episodes");
        Console.WriteLine(assertions + " behavioral assertions passed.");
    }
}
""";
var fixtureTree = CSharpSyntaxTree.ParseText(fixture);
var root = fixtureTree.GetCompilationUnitRoot();
var fixtureClass = root.Members.OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.Text == "LiquidityPulse");
root = root.ReplaceNode(fixtureClass, fixtureClass.AddMembers(members));
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
    .Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
var compilation = CSharpCompilation.Create("LiquidityPulseBehavior",
    [CSharpSyntaxTree.Create(root)], references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var assemblyStream = new MemoryStream();
var result = compilation.Emit(assemblyStream);
if (!result.Success)
    throw new Exception(string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
var assembly = Assembly.Load(assemblyStream.ToArray());
assembly.GetType("LiquidityPulse")!.GetMethod("Run")!.Invoke(null, null);
Console.WriteLine("Complete indicator parses as C# 5. NT8/SharpDX compilation still requires NinjaTrader.");
