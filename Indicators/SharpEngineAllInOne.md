# SharpEngine All-In-One — Multi-Timeframe Order Flow & L2 Engine for NinjaTrader 8

**Author:** Dollars1bySTEVE
**Type:** On-chart overlay indicator (SharpDX / GPU-rendered)

---

## 📖 Overview

**SharpEngine All-In-One** is a hardware-accelerated overlay that combines three trading tools into a single indicator:

1. **Higher-Timeframe (HTF) directional bias** — subtle green/red background shading tells you which way the bigger picture is leaning.
2. **Level 2 (order book / DOM) liquidity walls** — horizontal lines mark large resting orders sitting in the market, so you can see where price may stall or reverse.
3. **Order-flow entry arrows** — simple reversal-bar triggers that only fire *in the direction of the HTF bias*.

It's built for **discretionary intraday futures traders** (scalping / day trading) who want context, liquidity, and timing in one glance instead of stacking three separate indicators.

Everything is drawn with **SharpDX (Direct2D)** for smooth, low-lag rendering, and all GPU resources are disposed cleanly to avoid memory leaks.

---

## ✨ Features

- **HTF Background Shading** — faint green (bullish) or red (bearish) wash based on two configurable higher-resolution swing series
- **Level 2 Liquidity Walls** — auto-detects resting bid/ask size above a threshold and draws labeled lines (`ASK x50`, `BID x120`)
- **Bias-Aligned Entry Arrows** — up/down triangles on reversal bars, filtered by HTF direction
- **Heads-Up Display (HUD)** — bottom-left status line showing engine state and wall threshold
- **Fully Configurable Data Series** — pick the bar type (Tick / Minute / Second / Renko, etc.) and value for both confirmation series from the settings panel — **no coding, no third-party bar-type add-ons required**
- **Clean Resource Management** — proper SharpDX disposal on device change and shutdown

---

## ⚠️ Important — Requirements

- **Level 2 / market-depth data is required** for the liquidity walls. Use a data feed and instrument that provide DOM data (e.g. **ES, NQ, CL** futures). Without depth data the walls simply won't appear — the other features still work.
- **Calculate = OnEachTick** — run this on **live real-time data** (or **Tick Replay** for historical study). It is not a bar-close backtesting tool.
- Native NinjaTrader bar types only. Selecting **Renko** uses NT8's built-in Renko. Custom add-on bar types (NinjaRenko / UniRenko) are intentionally **not** supported to keep the indicator dependency-free and portable.

---

## 📥 Installation

1. Download `SharpEngineAllInOne.cs`
2. Place it in: `Documents\NinjaTrader 8\bin\Custom\Indicators\`
3. Open NinjaTrader 8
4. Go to: **Control Center → New → NinjaScript Editor**
5. Press **F5** to compile
6. Add to chart: **Right-click chart → Indicators → SharpEngineAllInOne**

---

## ⚙️ Settings

### 1. Main Settings

| Setting | Default | Description |
|---------|---------|-------------|
| Enable L2 Walls | ✅ ON | Draw horizontal lines at large resting bid/ask orders |
| Min Lot Threshold | 35 | Minimum resting contracts before a wall is drawn |
| Wall Dash Style | Dash | Line style for walls (Solid / Dash / Dot / DashDot / DashDotDot) |
| Enable HTF Shading | ✅ ON | Paint the background green/red based on HTF bias |
| Enable Order Flow Signals | ✅ ON | Show bias-aligned reversal-bar entry arrows |

### 3. Data Series Settings

These control the two secondary series used to compute the HTF bias. Both use **native `BarsPeriodType` values only**.

| Setting | Default | Description |
|---------|---------|-------------|
| HTF Bars Period Type | Minute | Bar type for the higher-timeframe series |
| HTF Bars Value | 240 | Value for the HTF series (e.g. 240 = 4-hour) |
| Confirm Bars Period Type | Tick | Bar type for the faster confirmation series |
| Confirm Bars Value | 80 | Value for the confirmation series (e.g. 80 = 80-tick) |
| Swing Strength | 5 | Swing look-left/right strength for both series |

> 💡 **Renko option:** set either "Period Type" to **Renko** and put your brick size in the matching "Value" field to run the bias off native Renko bricks.

---

## 🧠 How It Works

### 1. HTF Bias (Background Shading)
The indicator runs a `Swing()` on both the HTF and confirmation series. Then it compares the current close to those swing levels:

- Close **above both swing lows** → **Bullish bias** → faint green background
- Close **below both swing highs** → **Bearish bias** → faint red background
- Otherwise → neutral (no shading)

### 2. Liquidity Walls (Level 2)
As DOM updates stream in, resting bid/ask size is tracked per price. Any price level with size **≥ Min Lot Threshold** gets a horizontal line and a label:

- **Ask walls** → red line (`ASK x{size}`) — potential resistance / supply
- **Bid walls** → green line (`BID x{size}`) — potential support / demand

### 3. Entry Arrows (Bias-Aligned Reversals)
Signals only fire *with* the HTF bias:

| Bias | Pattern | Result |
|------|---------|--------|
| Bullish | previous bar red → current bar green | 🔼 up arrow below the bar |
| Bearish | previous bar green → current bar red | 🔽 down arrow above the bar |

---

## 📊 Recommended Chart Setups

Because the indicator adds two configurable series, run it on a **fast primary chart** and let the two secondary series supply the higher-timeframe context.

### Scalping — ES / NQ (fast primary chart)
- **Primary chart:** Tick (e.g. 500-tick) or 1-minute
- HTF Bars: **Minute / 60** (1-hour bias)
- Confirm Bars: **Tick / 80** (default) or **Tick / 200**
- Swing Strength: **5**
- Min Lot Threshold: **35–50** (ES), lower for thinner markets

### Day Trading — ES / NQ / CL
- **Primary chart:** 1m–5m
- HTF Bars: **Minute / 240** (4-hour bias — the default)
- Confirm Bars: **Minute / 15** or **Tick / 500**
- Swing Strength: **5**
- Min Lot Threshold: tune per instrument (ES ~35, CL ~10–20)

### Renko Style
- **Primary chart:** your preferred Renko/tick chart
- HTF Bars: **Minute / 240**
- Confirm Bars: **Renko / (your brick size)**
- Swing Strength: **3–5**

---

## 🧱 Renko Brick Sizing (Index Futures)

When you set a **Period Type** to **Renko**, the matching **Value** field is the **brick size in ticks**. Because the indicator uses native NT8 Renko here — which drives the *bias shading*, not your primary chart — a **slightly larger brick keeps the shading from flickering.**

> ⚠️ **Note on native Renko:** NT8's built-in Renko "cleans" wicks and can hide gaps, so swing levels read a little differently than raw price. If the bias feels off, try a **Minute or Tick** confirmation series instead of Renko.

### Quick reference

| Instrument | Tick | Scalp brick | **Balanced (recommended)** | Smooth brick |
|---|---|---|---|---|
| **ES** (S&P 500) | 0.25 | 4 (1.0 pt) | **8 (2.0 pt)** | 12 (3.0 pt) |
| **NQ** (Nasdaq 100) | 0.25 | 8 (2.0 pt) | **12 (3.0 pt)** | 16–20 (4–5 pt) |
| **RTY** (Russell 2000) | 0.10 | 5 (0.5 pt) | **10 (1.0 pt)** | 15 (1.5 pt) |
| **YM** (Dow 30) | 1.0 | 5 (5 pts) | **10 (10 pts)** | 20 (20 pts) |
| **MES / MNQ / M2K / MYM** (micros) | same as full-size | mirror parent | mirror parent | mirror parent |

> 💡 **NQ tip:** Start at **Confirm Bars → Renko / 12** (a 3-point brick) with **Swing Strength 4–5**. Bump to **16** if the shading whipsaws; drop to **8** if it reacts too slowly.

**How to think about brick size for the bias:**
- **Smaller brick** → bias flips more often, more responsive, more noise
- **Larger brick** → bias is smoother and stickier, fewer whipsaws in the shading

> Reminder: RTY tick = 0.10 and YM tick = 1.0, so their brick *tick counts* differ from ES/NQ even though the concept is identical. Micros mirror their full-size parent exactly.

---

## 💡 Trading Tips

1. **Trade with the shade** — take longs during green shading, shorts during red. Skip signals when the background is neutral.
2. **Walls are magnets and barriers** — price often gravitates toward large walls and reacts at them. A signal firing *into* fresh liquidity is lower quality than one firing *away* from a wall it just rejected.
3. **Tune the threshold per instrument** — 35 contracts is meaningful on ES but too high on thin products. Lower it until walls appear on the levels you actually see the tape defend.
4. **Signals are a starting framework** — the reversal-bar trigger is intentionally simple. Combine it with your own read of structure, volume, or delta before committing.
5. **Use Tick Replay to study** — replay a session to see how the walls and bias behaved around real turning points.

---

## 🐛 Troubleshooting

### No liquidity walls appear
1. Confirm your instrument/feed provides **Level 2 / market depth** (ES, NQ, CL, etc.)
2. Lower **Min Lot Threshold** — your market may rarely rest 35+ contracts at a price
3. Make sure **Enable L2 Walls** is ON
4. Depth only populates on **live/real-time** data

### No background shading
1. Ensure **Enable HTF Shading** is ON
2. The market may be neutral (neither clearly above swing lows nor below swing highs)
3. Give it a few bars — the swing series need enough history to form

### No entry arrows
1. Ensure **Enable Order Flow Signals** is ON
2. Arrows only fire *with* an active HTF bias — none appear during neutral shading
3. They require a clean red→green (or green→red) reversal on the current bar

### Indicator won't compile
1. This is standard NinjaScript using native `BarsPeriodType` only — press **F5** and check the Errors tab
2. Do **not** try to point the series at NinjaRenko/UniRenko — only built-in bar types are supported

---

## 📜 License

Free to use and modify.

---

## 🙏 Credits

- **NinjaTrader 8 Indicator:** Dollars1bySTEVE
