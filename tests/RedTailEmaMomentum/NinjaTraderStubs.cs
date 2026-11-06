// Minimal API model: tests execute the production indicator, not NT8 rendering/audio.
using System;
using System.Collections.Generic;
using NinjaTrader.Cbi;
using System.Windows.Media;

namespace System.Windows.Media
{
    public class Brush { }
    public static class Brushes
    {
        public static readonly Brush LimeGreen = new Brush(), Red = new Brush(),
            Gold = new Brush(), DodgerBlue = new Brush(), MediumOrchid = new Brush(),
            Transparent = new Brush(), Black = new Brush();
    }
}

namespace NinjaTrader.Cbi
{
    public enum Priority { High }
    public enum LogLevel { Error }
}

namespace NinjaTrader.Core
{
    public static class Globals { public static string InstallDir = "/nt8"; }
}

namespace NinjaTrader.Gui
{
    public static class Serialize
    {
        public static string BrushToString(Brush brush) { return ""; }
        public static Brush StringToBrush(string value) { return new Brush(); }
    }
}

namespace NinjaTrader.NinjaScript
{
    public enum State { SetDefaults, Configure, DataLoaded, Historical, Realtime }
    public enum Calculate { OnEachTick, OnBarClose }
    public class NinjaScriptPropertyAttribute : Attribute { }

    public class Series
    {
        private readonly Func<int> bar;
        private readonly Dictionary<int, double> values = new Dictionary<int, double>();
        public Series(Func<int> bar) { this.bar = bar; }
        public void Reset() { this[0] = double.NaN; }
        public double this[int ago]
        {
            get { return values.TryGetValue(bar() - ago, out double value) ? value : 0; }
            set { values[bar() - ago] = value; }
        }
    }

    public class TestBars { public bool IsTickReplay; }
    public class TestInstrument { public string FullName = "NQ TEST"; }

    public class Indicator
    {
        public State State;
        public Calculate Calculate;
        public int CurrentBar, BarsInProgress;
        public bool IsFirstTickOfBar, IsOverlay, DrawOnPricePanel, IsSuspendedWhileInactive;
        public string Name, Description;
        public double TickSize = 0.25;
        public TestBars Bars = new TestBars();
        public TestInstrument Instrument = new TestInstrument();
        public Series Input, High, Low;
        public List<Series> Values = new List<Series>();
        public Dictionary<int, Indicators.EMA> Emas = new Dictionary<int, Indicators.EMA>();
        public Indicators.RSI Rsi;
        public Indicators.MACD Macd;
        public readonly List<string> Alerts = new List<string>(), Errors = new List<string>();
        public readonly List<string> Output = new List<string>();
        public readonly List<(int Bar, int Direction, double Price)> Arrows
            = new List<(int, int, double)>();
        public Indicator()
        {
            Input = new Series(() => CurrentBar);
            High = new Series(() => CurrentBar);
            Low = new Series(() => CurrentBar);
        }
        protected virtual void OnStateChange() { }
        protected virtual void OnBarUpdate() { }
        public void SetState(State state) { State = state; OnStateChange(); }
        public void Update() { OnBarUpdate(); }
        protected void AddPlot(Brush color, string name) { Values.Add(new Series(() => CurrentBar)); }
        protected Indicators.EMA EMA(Series input, int period)
        {
            if (!Emas.ContainsKey(period)) Emas[period] = new Indicators.EMA(() => CurrentBar);
            return Emas[period];
        }
        protected Indicators.RSI RSI(Series input, int period, int smooth)
        {
            Rsi = new Indicators.RSI(() => CurrentBar);
            return Rsi;
        }
        protected Indicators.MACD MACD(Series input, int fast, int slow, int smooth)
        {
            Macd = new Indicators.MACD(() => CurrentBar);
            return Macd;
        }
        protected void Print(string value) { Output.Add(value); }
        protected void Log(string value, LogLevel level) { Errors.Add(value); }
        protected void Alert(string id, Priority priority, string message, string file,
            int rearm, Brush background, Brush foreground) { Alerts.Add(id); }
    }
}

namespace NinjaTrader.NinjaScript.Indicators
{
    public class EMA : Series { public EMA(Func<int> bar) : base(bar) { } }
    public class RSI : Series { public RSI(Func<int> bar) : base(bar) { } }
    public class MACD
    {
        public Series Diff;
        public MACD(Func<int> bar) { Diff = new Series(bar); }
    }
}

namespace NinjaTrader.NinjaScript.DrawingTools
{
    public static class Draw
    {
        public static void ArrowUp(Indicator owner, string tag, bool auto, int ago, double y, Brush color)
        { owner.Arrows.Add((owner.CurrentBar - ago, 1, y)); }
        public static void ArrowDown(Indicator owner, string tag, bool auto, int ago, double y, Brush color)
        { owner.Arrows.Add((owner.CurrentBar - ago, -1, y)); }
    }
}
