# SessionProfileSuite — Standalone Session Volume Profile Suite

**Author:** Dollars1bySTEVE  
**Type:** On-chart overlay indicator (SharpDX / GPU-rendered)

---

## What Is SessionProfileSuite?

`SessionProfileSuite` is a **fully standalone** NinjaTrader 8 indicator built for **ES, NQ, MES, and MNQ on a 5-minute chart**.

It tracks three fixed **US Eastern** sessions:

- **Asia** — `18:00 → 03:00 ET`
- **London** — dynamic London open (`08:00 London local`) with ET fallback defaults of `03:00 → 09:30 ET`
- **New York** — `09:30 → 16:00 ET`

Everything needed for session detection, DST-aware ET conversion, volume profile math, VWAP, value area, opening balance, and midnight open handling is implemented **inside this file**. It does **not** depend on `IQMainGPU.cs`, `IQMainGPU_Enhanced.cs`, or any other repository file.

---

## ✨ Features

### 1. Session Volume Profile
- Per-session reset for **Asia / London / NY**
- Live **POC / VAH / VAL** during the active session
- **70% value area** by default, configurable from `50–90%`
- Classic left-anchored **histogram** rendered with SharpDX
- `TickBased` and `BarDistributed` profile-source modes
- Closed-session **POC / VAH / VAL** can project forward as dashed reference levels

### 2. Session VWAP + 1σ Bands
- Resets at each session open
- Tick-accurate VWAP when `Profile Source = TickBased`
- Typical-price VWAP when `Profile Source = BarDistributed`
- **+1σ / -1σ** cloud with configurable opacity
- VWAP line flips **green above / red below**

### 3. Opening Balance
- Per-session **High / Mid / Low** from the first `OpeningBalanceMinutes`
- Default `60` minutes, configurable from `15–180`
- Optional forward projection after session close

### 4. Midnight Open
- Captures the **00:00 ET** opening price
- Draws a horizontal reference line across the ET day

### 5. Standalone GPU Rendering
- Uses **SharpDX / Direct2D** in `OnRender`
- Safe resource lifecycle with `CreateResources`, `DisposeResources`, and `OnRenderTargetChanged`
- Label and line rendering stays on the price panel as an overlay

### 6. Confluence Fade Signal
- Detects high-probability **mean-reversion / fade** opportunities on the primary bar series
- Fires a **bearish arrow** (↓) when the bar's High touches the **+1σ upper VWAP band** AND coincides (within the configured tolerance) with at least one resistance level: **VAH**, **POC** (if above VWAP), or **OB High** (when the Opening Balance window is complete)
- Fires a **bullish arrow** (↑) when the bar's Low touches the **−1σ lower VWAP band** AND coincides with at least one support level: **VAL**, **POC** (if below VWAP), or **OB Low** (when the Opening Balance window is complete)
- Fully reuses the live `currentSession` state — no recomputation of VWAP, bands, or profile data
- Configurable **tolerance** (0–20 ticks) and independent **bullish / bearish arrow colors**
- Arrows are drawn via NinjaTrader's native `Draw.ArrowDown` / `Draw.ArrowUp`; one per bar, identified by bar index so they update cleanly on developing bars

---

## ⚙️ Parameter Groups

| Group | Parameters |
|---|---|
| **1. General** | Master Enable, Profile Source |
| **2. Sessions** | Enable Asia/London/NY, Asia/London/NY ET start/end inputs, Dynamic London Open toggle |
| **3. Volume Profile** | Show Histogram, Histogram Max Width, Ticks Per Row, Value Area Percent, Show POC/VAH/VAL, Project Closed Sessions |
| **4. VWAP** | Show VWAP, Show Bands, Band Opacity, VWAP Line Width, VWAP Line Style |
| **5. Opening Balance** | Show Opening Balance, Opening Balance Minutes, Project Closed OB, OB Line Width, OB Line Style |
| **6. Midnight Open** | Show Midnight Open, Midnight Line Width, Midnight Line Style |
| **7. Colors & Style** | Profile colors, VWAP above/below colors, band colors, OB colors, midnight-open color, label size, profile line width |
| **8. Confluence Signal** | Enable Confluence Signal, Confluence Tolerance (ticks), Bullish Signal Color, Bearish Signal Color |

---

## 📥 Installation

### Import via NinjaScript
1. Open **NinjaTrader 8**
2. Go to **Tools → Import → NinjaScript Add-On**
3. Import the file package if you exported it as a zip

### Manual copy
1. Copy `SessionProfileSuite.cs` to:
   `Documents\NinjaTrader 8\bin\Custom\Indicators\`
2. Open **Control Center → New → NinjaScript Editor**
3. Press **F5** to compile
4. Add the indicator from:
   **Right-click chart → Indicators → SessionProfileSuite**

> This repository also includes a copy at `Indicators/SessionProfileSuite.cs` to match the repo's dual-location pattern.

---

## 📊 Recommended Settings

### ES / NQ / MES / MNQ — 5 Minute
- **Calculate:** `On each tick`
- **Profile Source:** `TickBased`
- **Ticks Per Row:** `1`
- **Value Area Percent:** `70`
- **Opening Balance Minutes:** `60`
- **Auto scale:** **OFF**

### When to use BarDistributed
Use `BarDistributed` when:
- historical tick data is missing
- you want a lighter profile mode
- you do not want the extra 1-tick secondary series

### TickBased notes
- `TickBased` is the most accurate mode for live session profile and VWAP
- Best results come with a feed that provides reliable historical and real-time tick data

---

## 🧠 How It Works

### ET / DST handling
The indicator converts bar timestamps into **US Eastern time** with `TimeZoneInfo("Eastern Standard Time")`, so the session engine is keyed to ET rather than the user's local PC timezone. London open can also be derived from **`GMT Standard Time`** so DST shifts are handled automatically.

### Value area expansion
The profile always stores raw **1-tick bins** internally. The value-area engine:
1. Finds the **POC** (highest-volume price)
2. Starts from that bin
3. Compares the next adjacent bin above and below
4. Expands toward the larger adjacent side
5. Stops once the configured percentage of total volume is covered

`TicksPerRow` only changes the **rendered histogram grouping** — it does **not** change POC / VAH / VAL math.

### Confluence Fade Signal engine
The signal engine runs inside `ProcessPrimarySeriesBar()` on every primary bar update and reads directly from the live `currentSession` object — no separate accumulators.

**Bearish fade check (each bar):**
1. Compute `upperBand = Vwap + StdDev` and `tolerance = ConfluenceTolerance × TickSize`
2. If `High[0] >= upperBand − tolerance` → bar High probed the +1σ band
3. Check for a confluent resistance level within the same tolerance window:
   - `Vah` (Value Area High)
   - `Poc` when `Poc ≥ Vwap`
   - `OpeningBalanceHigh` when `OpeningBalanceComplete = true`
4. If any level matches → draw a bearish arrow `↓` just above `High[0]`

**Bullish fade check (each bar):**
1. Compute `lowerBand = Vwap − StdDev`
2. If `Low[0] <= lowerBand + tolerance` → bar Low probed the −1σ band
3. Check for a confluent support level:
   - `Val` (Value Area Low)
   - `Poc` when `Poc ≤ Vwap`
   - `OpeningBalanceLow` when `OpeningBalanceComplete = true`
4. If any level matches → draw a bullish arrow `↑` just below `Low[0]`

Arrow tags are keyed to `CurrentBar` so there is **at most one arrow per bar**; if a developing bar's profile shifts and the condition becomes false the arrow silently becomes stale but is not erased (the last valid state is kept until the next bar begins).

---

## 🐛 Troubleshooting

### No profile appears
1. Confirm **Master Enable** is on
2. Confirm the current time is inside an enabled session
3. If using `TickBased`, verify the instrument/feed provides tick data
4. Switch to `BarDistributed` if you want a fallback profile mode

### VWAP or bands look incomplete
1. Set **Calculate = On each tick**
2. Make sure the session has started and enough bars/ticks have accumulated
3. Disable **Auto scale** on the chart for a cleaner overlay

### London start looks different than 03:00 ET
That is expected when **Dynamic London Open** is enabled during DST mismatch periods between the US and UK calendars. Turn it off if you want to force the ET fallback values manually.

### Indicator will not compile
1. Press **F5** in the NinjaScript Editor and inspect the error list
2. Confirm SharpDX is available in your NT8 install (standard for custom GPU-rendered indicators)
3. Make sure there is only one active working copy in your NT8 `Indicators` folder if you are manually testing edits

### No Confluence Fade arrows appear
1. Confirm **Enable Confluence Signal** is on
2. The signal requires a valid session VWAP with a non-zero standard deviation — it will not fire on the very first bar of a session or when `Profile Source = BarDistributed` and only one bar has been processed
3. Try increasing **Confluence Tolerance (ticks)** if the band and level are close but not quite overlapping
4. For bearish signals, the Opening Balance must have completed its full window (`Opening Balance Minutes`) before the OB High is eligible as a confluence level

---

## 🙏 Credits

- **NinjaTrader 8 indicator:** Dollars1bySTEVE
- **SessionProfileSuite concept:** repo issue specification for standalone session profile, VWAP, OB, and midnight-open workflow
- **Platform:** NinjaTrader 8 / SharpDX / Direct2D

