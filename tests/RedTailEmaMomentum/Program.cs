using System;
using System.IO;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;

static class Program
{
    private static int assertions;
    private static string sound;
    static void Check(bool value, string message)
    {
        assertions++;
        if (!value) throw new Exception(message);
    }

    static RedTailEmaMomentum Create(Action<RedTailEmaMomentum> configure = null)
    {
        var indicator = new RedTailEmaMomentum();
        indicator.SetState(State.SetDefaults);
        indicator.FastPeriod = 2;
        indicator.MiddlePeriod = 3;
        indicator.SlowPeriod = 4;
        indicator.RsiPeriod = 2;
        indicator.MacdFastPeriod = 2;
        indicator.MacdSlowPeriod = 3;
        indicator.MacdSignalPeriod = 2;
        indicator.LongSound = indicator.ShortSound = sound;
        configure?.Invoke(indicator);
        indicator.SetState(State.Configure);
        indicator.SetState(State.DataLoaded);
        indicator.SetState(State.Historical);
        return indicator;
    }

    static void Feed(RedTailEmaMomentum i, int bar, double price, double fast,
        double middle = 100, double slow = 90, double rsi = 60,
        double histogram = 1, bool first = false)
    {
        i.CurrentBar = bar;
        i.IsFirstTickOfBar = first;
        i.Input[0] = price;
        i.High[0] = price + 1;
        i.Low[0] = price - 1;
        i.Emas[i.FastPeriod][0] = fast;
        if (i.EmaCount >= 2) i.Emas[i.MiddlePeriod][0] = middle;
        if (i.EmaCount == 3) i.Emas[i.SlowPeriod][0] = slow;
        if (i.Rsi != null) i.Rsi[0] = rsi;
        if (i.Macd != null) i.Macd.Diff[0] = histogram;
        i.Update();
    }

    static void LiveBaseline(RedTailEmaMomentum i)
    {
        i.CurrentBar = 10;
        i.SetState(State.Realtime);
        Feed(i, 10, 110, 99, first: true);
    }

    static void Invalid(Action<RedTailEmaMomentum> configure)
    {
        bool threw = false;
        try { Create(configure); }
        catch (ArgumentException) { threw = true; }
        Check(threw, "Invalid configuration must report an error");
    }

    static void Main()
    {
        sound = Path.GetTempFileName();
        try { Run(); }
        finally { File.Delete(sound); }
        Console.WriteLine(assertions + " assertions passed (NT8 API model; no actual playback/rendering).");
    }

    static void Run()
    {
        var i = Create();
        Check(i.AlertTiming == RedTailEmaAlertTiming.Intrabar, "Immediate default");
        Check(i.EmaCount == 3 && i.MomentumFilter == RedTailEmaMomentumFilter.RSI, "Default filters");
        LiveBaseline(i);
        Feed(i, 10, 110, 101);
        Check(i.Alerts.Count == 1 && i.Arrows.Count == 1, "Immediate long on crossing tick");
        Check(i.Arrows[0].Price == 109 - 10 * i.TickSize, "Arrow tick offset");
        Feed(i, 10, 110, 102);
        Check(i.Alerts.Count == 1 && i.Arrows.Count == 1, "No repeated alert while condition holds");
        Feed(i, 10, 80, 99, slow: 90, rsi: 40);
        Check(i.Alerts.Count == 2 && i.Arrows[1].Direction == -1, "Opposite direction once per candle");
        Feed(i, 10, 110, 101);
        Check(i.Alerts.Count == 2, "Same-direction recross suppressed within candle");
        Feed(i, 11, 110, 99, first: true);
        Feed(i, 11, 110, 101);
        Check(i.Alerts.Count == 3 && i.Alerts[2] != i.Alerts[0], "New candle rearms unique alert");

        i = Create();
        LiveBaseline(i);
        Feed(i, 10, 110, 101, rsi: 50);
        Check(i.Alerts.Count == 0, "RSI exactly midpoint blocks");
        Feed(i, 10, 110, 102, rsi: 60);
        Check(i.Alerts.Count == 0, "No delayed signal when RSI improves after crossing");
        Feed(i, 10, 110, 99);
        Feed(i, 10, 110, 101);
        Check(i.Alerts.Count == 1, "Fresh crossing after rejected signal works");

        i = Create();
        LiveBaseline(i);
        Feed(i, 10, 85, 101);
        Check(i.Alerts.Count == 0, "Third EMA rejects wrong price side");
        Feed(i, 10, 110, 102);
        Check(i.Alerts.Count == 0, "Third EMA improvement does not announce old crossing");

        i = Create(x => { x.EmaCount = 2; x.MomentumFilter = RedTailEmaMomentumFilter.None; });
        LiveBaseline(i);
        Feed(i, 10, 50, 101, rsi: 0);
        Check(i.Alerts.Count == 1, "Two EMAs ignore slow and momentum");
        Check(double.IsNaN(i.Values[2][0]), "Disabled third EMA hidden");

        i = Create(x => {
            x.EmaCount = 1; x.SignalMode = RedTailEmaSignalMode.PriceCross;
            x.MomentumFilter = RedTailEmaMomentumFilter.None;
        });
        i.SetState(State.Realtime);
        Feed(i, 10, 99, 100, first: true);
        Feed(i, 10, 101, 100);
        Check(i.Alerts.Count == 1, "Single EMA price cross");
        Check(double.IsNaN(i.Values[1][0]) && double.IsNaN(i.Values[2][0]), "Unused plots hidden");
        Check(i.Values[0][0] == 100, "Fast plot follows EMA");

        i = Create(x => { x.SignalMode = RedTailEmaSignalMode.PriceCross; });
        i.SetState(State.Realtime);
        Feed(i, 10, 100, 101, middle: 102, first: true);
        Feed(i, 10, 102, 101, middle: 102);
        Check(i.Alerts.Count == 0, "Price cross rejects misalignment");
        Feed(i, 10, 100, 101, middle: 100);
        Feed(i, 10, 102, 101, middle: 100);
        Check(i.Alerts.Count == 1, "Price cross accepts three aligned EMAs");
        Feed(i, 10, 98, 99, middle: 100, slow: 110, rsi: 40);
        Check(i.Alerts.Count == 2, "Price cross short alignment");

        i = Create(x => { x.MomentumFilter = RedTailEmaMomentumFilter.MACD; });
        LiveBaseline(i);
        Feed(i, 10, 110, 101, histogram: 0);
        Check(i.Alerts.Count == 0, "Zero MACD histogram blocks");
        Feed(i, 10, 110, 99);
        Feed(i, 10, 110, 101, histogram: -1);
        Check(i.Alerts.Count == 0, "Wrong MACD direction blocks");
        Feed(i, 10, 110, 99);
        Feed(i, 10, 110, 101);
        Feed(i, 10, 80, 99, histogram: -1);
        Check(i.Alerts.Count == 2, "MACD both directions");

        i = Create(x => { x.AlertTiming = RedTailEmaAlertTiming.BarClose; });
        Feed(i, 9, 110, 99);
        Feed(i, 10, 110, 101);
        Check(i.Arrows.Count == 1 && i.Arrows[0].Bar == 10 && i.Alerts.Count == 0,
            "Historical close-only arrow without sound");
        i.SetState(State.Realtime);
        Feed(i, 11, 110, 101, first: true);
        Check(i.Alerts.Count == 0, "Historical signal is not reannounced live");

        i = Create(x => { x.AlertTiming = RedTailEmaAlertTiming.BarClose; });
        Feed(i, 9, 110, 99);
        Feed(i, 10, 110, 99);
        i.SetState(State.Realtime);
        Feed(i, 10, 110, 99);
        Feed(i, 10, 110, 101);
        Check(i.Alerts.Count == 0, "Bar-close mode ignores current candle fluctuations");
        Feed(i, 11, 110, 101, first: true);
        Check(i.Alerts.Count == 1 && i.Arrows[0].Bar == 10, "Closed bar evaluated on next first tick");
        Feed(i, 11, 110, 101, first: true);
        Check(i.Alerts.Count == 1, "Completed candle not evaluated twice");

        i = Create(x => { x.AlertTiming = RedTailEmaAlertTiming.BarClose; });
        i.Bars.IsTickReplay = true;
        Feed(i, 9, 110, 99, first: true);
        Feed(i, 10, 110, 101, first: true);
        Check(i.Arrows.Count == 0, "Tick replay waits for completion");
        Feed(i, 10, 110, 101);
        Feed(i, 11, 110, 101, first: true);
        Check(i.Arrows.Count == 1 && i.Arrows[0].Bar == 10, "Tick replay uses previous completed bar");

        i = Create();
        i.Bars.IsTickReplay = true;
        Feed(i, 10, 110, 99, first: true);
        Feed(i, 10, 110, 101);
        Feed(i, 10, 110, 102);
        Check(i.Arrows.Count == 1 && i.Arrows[0].Direction == 1, "Tick replay arrow survives later ticks");
        Check(i.Alerts.Count == 0, "Historical Tick Replay never plays sounds");
        i.SetState(State.Realtime);
        Feed(i, 10, 110, 99);
        Check(i.Alerts.Count == 0, "First realtime tick does not replay a stale crossing");

        i = Create(x => { x.ShowArrows = false; });
        LiveBaseline(i);
        Feed(i, 10, 110, 101);
        Check(i.Alerts.Count == 1 && i.Arrows.Count == 0, "Arrow setting does not disable audio");
        i = Create(x => { x.EnableAlerts = false; });
        LiveBaseline(i);
        Feed(i, 10, 110, 101);
        Check(i.Alerts.Count == 0 && i.Arrows.Count == 1, "Audio setting does not disable arrows");
        i = Create(x => { x.LongSound = null; });
        LiveBaseline(i);
        Feed(i, 10, 110, 101);
        Feed(i, 11, 110, 99, first: true);
        Feed(i, 11, 110, 101);
        Check(i.Errors.Count == 1 && i.Output.Count == 1 && i.Alerts.Count == 0,
            "Missing audio reported explicitly without repetitive log spam");
        i.LongSound = sound;
        Feed(i, 12, 110, 99, first: true);
        Feed(i, 12, 110, 101);
        Check(i.Alerts.Count == 1, "Sound repaired at runtime can play");

        i = Create();
        i.SetState(State.Realtime);
        Feed(i, 1, 110, 99, first: true);
        Feed(i, 1, 110, 101);
        Check(i.Alerts.Count == 0 && i.Arrows.Count == 0, "Warmup blocks signals");
        i.BarsInProgress = 1;
        Feed(i, 10, 110, 99);
        Feed(i, 10, 110, 101);
        Check(i.Alerts.Count == 0, "Secondary series ignored");

        var a = Create();
        var b = Create();
        LiveBaseline(a);
        LiveBaseline(b);
        Feed(a, 10, 110, 101);
        Feed(b, 10, 110, 101);
        Check(a.Alerts[0] != b.Alerts[0], "Indicator instances do not suppress each other's alerts");
        Invalid(x => x.EmaCount = 1);
        Invalid(x => x.FastPeriod = x.MiddlePeriod);
        Invalid(x => x.MiddlePeriod = x.SlowPeriod);
        Invalid(x => x.Calculate = Calculate.OnBarClose);
        Invalid(x => { x.MomentumFilter = RedTailEmaMomentumFilter.MACD; x.MacdFastPeriod = 3; });

        foreach (int count in new[] { 1, 2, 3 })
        foreach (RedTailEmaSignalMode mode in Enum.GetValues<RedTailEmaSignalMode>())
        foreach (RedTailEmaAlertTiming timing in Enum.GetValues<RedTailEmaAlertTiming>())
        foreach (RedTailEmaMomentumFilter filter in Enum.GetValues<RedTailEmaMomentumFilter>())
        {
            if (count == 1 && mode == RedTailEmaSignalMode.EmaCrossover) continue;
            i = Create(x => {
                x.EmaCount = count;
                x.SignalMode = mode;
                x.AlertTiming = timing;
                x.MomentumFilter = filter;
            });
            Feed(i, 9, 98, 99);
            Feed(i, 10, 98, 99);
            i.Arrows.Clear();
            i.SetState(State.Realtime);
            Feed(i, 10, 98, 99);
            Feed(i, 10, 110, 101);
            if (timing == RedTailEmaAlertTiming.BarClose)
            {
                Check(i.Alerts.Count == 0, "Settings matrix: no premature bar-close alert");
                Feed(i, 11, 80, 99, slow: 110, rsi: 40, histogram: -1, first: true);
                Check(i.Alerts.Count == 1 && i.Arrows[0].Bar == 10,
                    "Settings matrix: confirmed long on completed bar");
                Feed(i, 12, 110, 101, first: true);
                Check(i.Alerts.Count == 2 && i.Arrows[1].Direction == -1 && i.Arrows[1].Bar == 11,
                    "Settings matrix: confirmed short on completed bar");
            }
            else
            {
                Check(i.Alerts.Count == 1, "Settings matrix: immediate long");
                Feed(i, 10, 80, 99, slow: 110, rsi: 40, histogram: -1);
                Check(i.Alerts.Count == 2 && i.Arrows[1].Direction == -1,
                    "Settings matrix: immediate short");
            }
        }
    }
}
