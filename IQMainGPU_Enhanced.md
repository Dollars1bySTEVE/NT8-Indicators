# IQMainGPU_Enhanced — Standalone Enhanced Indicator

**The Ultimate All-in-One NinjaTrader 8 Indicator — Enhanced Edition**

GPU-accelerated • Institutional-Grade • Smart Money Concepts • 3 Independent Dashboards

> 📂 File location: `Indicators/IQMainGPU_Enhanced.cs`

---

## What Is IQMainGPU_Enhanced?

`IQMainGPU_Enhanced` is a **standalone, self-contained** enhanced version of `IQMainGPU`. It contains **every single feature** from the original IQMainGPU indicator — plus three fully independent, GPU-rendered dashboard panels designed for live trading analysis:

- **Main Dashboard** — Original IQMainGPU stats
- **Monitoring Dashboard** — Compact real-time market health panel
- **Entry Mode Dashboard** — Live trade setup with entry, stop, target, and R/R

Both files (`IQMainGPU.cs` and `IQMainGPU_Enhanced.cs`) can coexist in your NinjaTrader 8 `Indicators` folder. They compile together into a single assembly. The original `IQMainGPU.cs` is **never modified**.

---

## ⚠️ IMPORTANT: Dashboard Positioning

> **⚠️ Place all dashboards on the LEFT side or CENTER — NOT on the right side.**

NinjaTrader 8 renders a price axis (Y-axis) on the right side of every chart panel. Dashboards positioned `TopRight` or `BottomRight` will **overlap the price axis**, making both the dashboard and the price labels unreadable.

**Recommended positions:**
| Dashboard | Recommended Position |
|-----------|----------------------|
| Main Dashboard | `TopLeft` |
| Monitoring Dashboard | `BottomLeft` |
| Entry Mode Dashboard | `CenterBottom` |

Available positions: `TopLeft`, `TopRight`, `BottomLeft`, `BottomRight`, `CenterTop`, `CenterBottom`, `Hidden`

---

## ⚠️ IMPORTANT: Installation & Performance Settings

**Critical Configuration for Best Performance:**

1. **Calculate Setting**: Set to **"On price change"** (NOT "On bar close" or "On each tick")
   - This balances responsiveness with resource efficiency

2. **DO NOT enable "Auto scale"** — The indicator manages its own scaling

3. **DO NOT tick "Auto render"** — This is **critical** for performance!
   - The indicator uses custom GPU rendering via SharpDX
   - Auto render will cause unnecessary redraws and performance issues

4. If you want EMA price markers, click on **"Price Marker(s)"** in the indicator properties

---

## Table of Contents

- [What Is IQMainGPU_Enhanced?](#what-is-iqmaingpu_enhanced)
- [Dashboard Positioning Warning](#️-important-dashboard-positioning)
- [New Features (Enhanced Only)](#-new-features-enhanced-only)
  - [Main Dashboard](#1️⃣-main-dashboard)
  - [Monitoring Dashboard](#2️⃣-monitoring-dashboard)
  - [Entry Mode Dashboard](#3️⃣-entry-mode-dashboard)
  - [Stop & Target Lines](#-stoptarget-chart-lines)
  - [Conflict Detection](#️-conflict-detection)
  - [Dynamic Stop Calculation](#-dynamic-stop-calculation)
  - [Dynamic Target Calculation](#-dynamic-target-calculation)
  - [Signal Staleness Tracking](#-signal-staleness-tracking)
- [All Inherited Features](#-all-inherited-features-from-iqmaingpu)
- [Parameter Groups](#-parameter-groups)
- [Group 16 — Dashboards (Enhanced Only)](#-group-16--dashboards-enhanced-only)
- [Enums Reference](#-enums-reference)
- [Installation](#️-installation)
- [Recommended Settings](#-recommended-settings)
- [Troubleshooting](#-troubleshooting)
- [Credits](#-credits)

---

## 🆕 New Features (Enhanced Only)

### 1️⃣ Main Dashboard

The **Main Dashboard** replicates the original IQMainGPU statistics panel with a refreshed GPU-rendered design.

**Displays:**
- Asset class tag and currently active market session
- Per-bar buy volume / sell volume
- Bar delta and delta percentage
- Cumulative delta and session buy/sell totals
- Active signal flags: `ABS` (absorption), `IMB` (imbalance), `FAKE-BKT` (fake breakout), bid/ask wall levels
- S/R level count and imbalance threshold
- Liquidity zone breakdown (Green / Red / Blue / Pink / Recovered)
- ADR / AWR / AMR in pips
- Level 2 status line (if enabled)

**Settings:**
| Parameter | Default | Description |
|-----------|---------|-------------|
| Show Main Dashboard | `true` | Master toggle |
| Main Dashboard Position | `TopLeft` | ⚠️ Use Left or Center |
| Main Dashboard Font Size | `11` | 10–16pt |

---

### 2️⃣ Monitoring Dashboard

The **Monitoring Dashboard** gives a compact, real-time view of overall market health and conditions.

**Displays:**
- Asset class tag
- Active session name and participation level (`High Participation` / `Low Participation`)
- ADR value, today's range, and % of ADR consumed
- A **graphical volume bar** showing buy vs sell volume ratio with color-coded bias (`BULLISH` / `BEARISH` / `NEUTRAL`)
- VWAP vs EMA 50 alignment status (`ALIGNED` / `DIVERGED`)
- Unrecovered liquidity zone counts (active vs recovered)
- Conflict warnings (if enabled)

**Settings:**
| Parameter | Default | Description |
|-----------|---------|-------------|
| Show Monitoring Dashboard | `true` | Master toggle |
| Monitoring Dashboard Position | `BottomLeft` | ⚠️ Use Left or Center |
| Monitoring Dashboard Font Size | `11` | 10–16pt |

---

### 3️⃣ Entry Mode Dashboard

The **Entry Mode Dashboard** is a focused live trade setup panel. It shows everything a trader needs to evaluate a potential entry at a glance.

**Displays:**
- Asset class tag
- Primary signal label with elapsed time since detection
- Confidence score (0–100%)
- Entry price (current close)
- Stop price with distance in ticks
- Target price with distance in ticks
- Risk/Reward ratio (e.g., `2.45:1`)
- Stale signal warning (⚠️ shown after 15 minutes)
- Conflict warnings (if enabled)

**No Active Signal state:**
When the signal is `NEUTRAL`, `No Data`, or has expired (>30 min), the panel displays:
```
No Active Signal
Waiting for signal detection...
```

**Settings:**
| Parameter | Default | Description |
|-----------|---------|-------------|
| Show Entry Mode Dashboard | `true` | Master toggle |
| Entry Mode Dashboard Position | `BottomRight` | ⚠️ Recommended: change to BottomLeft or CenterBottom |
| Entry Mode Dashboard Font Size | `14` | 12–20pt |
| Show Stop/Target Lines | `true` | Draw lines on chart |

---

### 📐 Stop/Target Chart Lines

When **Show Stop/Target Lines** is enabled in the Entry Mode Dashboard, three horizontal lines are drawn directly on the chart:

| Line | Color | Style | Meaning |
|------|-------|-------|---------|
| Entry | White | Solid | Current close (entry price) |
| Stop | Red | Dashed | Calculated stop-loss price |
| Target | Green | Dashed | Calculated take-profit price |

These lines update on every bar and reflect the current stop/target calculation mode.

---

### ⚠️ Conflict Detection

The conflict detection engine runs on every render frame and evaluates three conditions:

| Conflict Type | Trigger Condition |
|---------------|-------------------|
| **Volume Bullish but Price Below VWAP** | Delta > 0 AND close < ETH VWAP → exhaustion or accumulation risk |
| **Volume Bearish but Price Above VWAP** | Delta < 0 AND close > ETH VWAP → distribution or reversal risk |
| **Fake Breakout** | Fake breakout filter fires → high-risk entry condition |
| **Low Participation Session** | No London / New York / EU Brinks / US Brinks session active |

**Conflict Description Levels:**

| Level | Example Output |
|-------|---------------|
| `Brief` | `⚠ Conflict detected` |
| `Detailed` | `⚠ VOLUME BULLISH BUT PRICE BELOW VWAP (Exhaustion Risk)` |
| `VeryDetailed` | `⚠ [HIGH] VOLUME BULLISH BUT PRICE BELOW VWAP` + sub-description |

VeryDetailed severity tags: `[CRITICAL]`, `[HIGH]`, `[MODERATE]`

**Settings:**
| Parameter | Default | Description |
|-----------|---------|-------------|
| Show Conflict Warnings | `true` | Master toggle for all conflict warnings |
| Conflict Description Level | `Detailed` | Brief / Detailed / VeryDetailed |

---

### 🛑 Dynamic Stop Calculation

The stop price shown in the Entry Mode Dashboard is calculated based on the **Stop Placement Mode**:

| Mode | Logic |
|------|-------|
| `AutoDetected` | Checks unrecovered bullish liquidity zones → Pivot S1 → manual fallback |
| `PivotBased` | Always uses Pivot S1; falls back to manual distance |
| `HVNBased` | Finds the nearest S/R level below current price |
| `ManualInput` | `Close − (StopDistanceTicks × TickSize)` |

**Settings:**
| Parameter | Default | Description |
|-----------|---------|-------------|
| Stop Placement Mode | `AutoDetected` | Algorithm for stop calculation |
| Stop Distance (ticks) | `10` | Used by ManualInput (and as fallback) |

---

### 🎯 Dynamic Target Calculation

The target price is calculated based on the **Target Placement Mode**:

| Mode | Logic |
|------|-------|
| `AutoDetected` | Uses Pivot R1 if above price; otherwise R2; otherwise manual fallback |
| `PivotR1` | Always uses Pivot R1 |
| `PivotR2` | Always uses Pivot R2 |
| `ManualInput` | `Close + (TargetDistanceTicks × TickSize)` |

**Settings:**
| Parameter | Default | Description |
|-----------|---------|-------------|
| Target Placement Mode | `AutoDetected` | Algorithm for target calculation |
| Target Distance (ticks) | `20` | Used by ManualInput (and as fallback) |

---

### ⏱️ Signal Staleness Tracking

The indicator tracks **wall-clock time** (real elapsed minutes) since a signal was first detected:

| Threshold | Behaviour |
|-----------|-----------|
| 0–15 min | Signal displayed normally with elapsed time label (e.g., `3m ago`) |
| 15–30 min | `⚠ [STALE]` warning added below signal line |
| 30+ min | Signal is considered **expired** — panel shows "No Active Signal" |

This prevents traders from acting on old signals that may no longer be valid.

---

## 📦 All Inherited Features (from IQMainGPU)

IQMainGPU_Enhanced includes **100% of the original IQMainGPU features** unchanged:

### 📊 EMAs
- EMA 5, 13, 50, 200, 800 — GPU-rendered with labels
- EMA 50 Cloud (standard deviation envelope)
- Smart extended lookback on higher timeframes (1hr+)

### 📍 Pivot Points
- Classic Floor Pivots: PP, R1–R3, S1–S3
- M-Level midpoints: M0–M5
- Customizable line styles (solid, dashed, dotted)

### 🌍 Market Sessions (8 Sessions)
- London, New York, Tokyo, Hong Kong, Sydney, EU Brinks, US Brinks, Frankfurt
- Session boxes with opening range visualisation
- Automatic DST handling for all regions (UK, US, AU)

### 📈 Daily Open Lines (5 Types)
- ETH Daily Open (6 PM ET)
- Asia, London, US RTH Session Opens
- Calendar Daily Open

### 📏 Range Levels
- ADR, AWR, AMR with 50% midlines and configurable lookback
- RD / RW rolling daily and weekly ranges
- Alerts when price reaches ADR / AWR / AMR extremes

### 📊 VWAP
- ETH-anchored and RTH US-anchored VWAP
- ±1σ, ±2σ, ±3σ standard deviation bands with optional fill
- Dynamic colour (green above / red below / yellow neutral)

### 🕯️ PVSRA (Price Volume Spread Analysis)
- High Volume Bullish (Green), High Volume Bearish (Red)
- Mid Volume Bullish (Blue), Mid Volume Bearish (Pink)

### 🟦 Liquidity Zones
- Auto-detected from PVSRA candles
- Partial recovery tracking
- Max active zones configurable

### 📉 Volume Delta & Microstructure
- Real-time buy/sell volume estimation
- Cumulative delta, absorption detection, imbalance flagging

### ⚠️ Fake Breakout Detection
- 5-filter system: confirmation bars, volume follow-through, momentum divergence, S/R validation, R/R ratio

### 📚 Order Book (Level 2)
- Bid/Ask depth, wall detection, anti-spoofing checks
- Requires broker L2 data support

### 🎯 OTE Zones (Optimal Trade Entry)
- ICT Fibonacci retracement zones (62% / 70.5% / 79%)
- Bullish and bearish zone detection
- GPU-rendered with labels

### 📅 Additional Levels
- Yesterday High/Low, Last Week High/Low
- Psychological round number levels (daily & weekly)
- ADR / AWR / AMR / RD / RW range lines

---

## 📝 Parameter Groups

IQMainGPU_Enhanced uses **16 parameter groups** (groups 1–15 are identical to IQMainGPU; group 16 is new):

| Group | Name | Contents |
|-------|------|----------|
| 1 | Core | Asset class, candle color mode, L2 toggle |
| 2 | EMAs | All EMA settings |
| 3 | Pivot Points | PP, R/S levels, M-levels |
| 4 | Sessions | London, NY, Tokyo, HK, Sydney, EU Brinks, US Brinks, Frankfurt |
| 5 | Range Levels | ADR, AWR, AMR, RD, RW |
| 6 | Daily/Weekly Levels | Yesterday, Last Week, Daily Open, Psy, RTH opens |
| 7 | PVSRA Vectors | Volume thresholds |
| 8 | Liquidity Zones | Zone detection settings |
| 9 | Volume Delta | Delta lookback, absorption sensitivity |
| 10 | Fake Breakout | Filter parameters |
| 11 | Order Book | Wall detection, spoofing checks |
| 12 | Dashboard | Original dashboard position/font/opacity |
| 13 | Tables | Range table, DST reference table |
| 14 | VWAP | All VWAP and band settings |
| 15 | OTE Zones | OTE detection and display |
| **16** | **Dashboards** | **Main / Monitoring / Entry Mode dashboards (Enhanced only)** |

---

## 🆕 Group 16 — Dashboards (Enhanced Only)

### Main Dashboard
| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| Show Main Dashboard | `true` | — | Toggle main stats panel |
| Main Dashboard Position | `TopLeft` | 7 options | ⚠️ Avoid TopRight/BottomRight |
| Main Dashboard Font Size | `11` | 10–16 | Text size |

### Monitoring Dashboard
| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| Show Monitoring Dashboard | `true` | — | Toggle market health panel |
| Monitoring Dashboard Position | `BottomLeft` | 7 options | ⚠️ Avoid TopRight/BottomRight |
| Monitoring Dashboard Font Size | `11` | 10–16 | Text size |

### Entry Mode Dashboard
| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| Show Entry Mode Dashboard | `true` | — | Toggle trade setup panel |
| Entry Mode Dashboard Position | `BottomRight` | 7 options | ⚠️ Recommended: BottomLeft or CenterBottom |
| Entry Mode Dashboard Font Size | `14` | 12–20 | Text size |
| Show Stop/Target Lines | `true` | — | Draw entry/stop/target lines on chart |

### Shared Settings
| Parameter | Default | Range | Description |
|-----------|---------|-------------|
| Show Conflict Warnings | `true` | — | Enable conflict detection overlay |
| Conflict Description Level | `Detailed` | Brief/Detailed/VeryDetailed | Warning verbosity |
| Dashboard Opacity % | `80` | 1–100 | Background transparency for all 3 panels |
| Stop Placement Mode | `AutoDetected` | 4 modes | Stop calculation algorithm |
| Stop Distance (ticks) | `10` | 1–100 | Manual stop fallback distance |
| Target Placement Mode | `AutoDetected` | 4 modes | Target calculation algorithm |
| Target Distance (ticks) | `20` | 1–100 | Manual target fallback distance |

---

## 🔢 Enums Reference

New enums introduced in IQMainGPU_Enhanced (declared outside all namespaces per NT8 requirements):

### `DashboardPositionType`
| Value | Description |
|-------|-------------|
| `Hidden` | Panel disabled / not shown |
| `TopLeft` | ✅ Recommended |
| `TopRight` | ⚠️ Overlaps price axis — avoid |
| `BottomLeft` | ✅ Recommended |
| `BottomRight` | ⚠️ Overlaps price axis — avoid |
| `CenterTop` | ✅ Horizontally centred at top |
| `CenterBottom` | ✅ Horizontally centred at bottom |

### `StopPlacementMode`
| Value | Description |
|-------|-------------|
| `AutoDetected` | Liquidity zones → Pivot S1 → manual fallback |
| `PivotBased` | Always Pivot S1 |
| `HVNBased` | Nearest S/R level below price |
| `ManualInput` | Fixed tick distance |

### `TargetPlacementMode`
| Value | Description |
|-------|-------------|
| `AutoDetected` | Pivot R1 → R2 → manual fallback |
| `PivotR1` | Always Pivot R1 |
| `PivotR2` | Always Pivot R2 |
| `ManualInput` | Fixed tick distance |

### `ConflictDescriptionLevel`
| Value | Description |
|-------|-------------|
| `Brief` | Single-line alert only |
| `Detailed` | Full conflict description |
| `VeryDetailed` | Description with `[CRITICAL]` / `[HIGH]` / `[MODERATE]` severity tags |

---

## ⚙️ Installation

1. Download both files from the `Indicators/` folder:
   - `IQMainGPU.cs` (required — contains shared enums)
   - `IQMainGPU_Enhanced.cs`

2. In NinjaTrader 8, go to **Tools → Import → NinjaScript...**

3. Import **both files** — they must compile together in the same assembly

4. The indicator will appear as **IQMainGPU_Enhanced** in your indicator list

**Or manual installation:**
1. Copy both `.cs` files to: `Documents\NinjaTrader 8\bin\Custom\Indicators\`
2. In NinjaTrader, open **NinjaScript Editor**
3. Right-click → **Compile**

> ⚠️ `IQMainGPU_Enhanced.cs` **requires** `IQMainGPU.cs` to be present.
> Shared enums (`IQMDashboardPosition`, `IQMLineStyle`, `IQMAssetClass`, `IQMCandleColorMode`, `VwapSessionAnchor`) are declared in `IQMainGPU.cs` and used by both files.

---

## ⚡ Recommended Settings

| Setting | Recommended Value | Why |
|---------|-------------------|-----|
| **Calculate** | On price change | Best balance of responsiveness and performance |
| **Auto scale** | OFF | Indicator manages scaling |
| **Auto render** | OFF | ⚠️ CRITICAL — GPU rendering requires this OFF |
| **Main Dashboard Position** | TopLeft | Avoids price axis overlap |
| **Monitoring Dashboard Position** | BottomLeft | Avoids price axis overlap |
| **Entry Mode Dashboard Position** | CenterBottom | Centred, avoids all axes |
| **Conflict Level** | Detailed | Good balance of information vs clutter |
| **Stop Mode** | AutoDetected | Uses available context (zones, pivots) |
| **Target Mode** | AutoDetected | Targets R1/R2 automatically |

---

## 🐛 Troubleshooting

**Dashboards overlapping the price axis:**
- Change dashboard positions to `TopLeft`, `BottomLeft`, or `CenterBottom`
- Avoid `TopRight` and `BottomRight` — NinjaTrader's price axis occupies this space

**Indicator not compiling:**
- Ensure `IQMainGPU.cs` is present in the same `Indicators/` folder
- Shared enums are declared in `IQMainGPU.cs` — both files must compile together

**Dashboard panels not displaying:**
- Ensure `ShowMainDashboard` / `ShowMonitoringDashboard` / `ShowEntryModeDashboard` are `true`
- Verify that the position is not set to `Hidden`
- Check that "Auto render" is OFF

**Stop/Target lines not showing:**
- Ensure `ShowStopTargetLines` is enabled
- Ensure `ShowEntryModeDashboard` is enabled (lines are tied to Entry Mode)
- Verify pivot data has loaded (requires at least one completed trading day)

**Entry Mode shows "No Active Signal":**
- This is normal when the signal is NEUTRAL or has been active for >30 minutes
- Wait for a new microstructure signal (absorption, imbalance, fake breakout, or delta pressure)

**Conflict warnings always firing "Low Participation":**
- This fires outside London, NY, EU Brinks, and US Brinks sessions
- Expected behaviour during off-hours (Tokyo, Sydney, Frankfurt, pre-market)
- Set `ShowConflictWarnings = false` or `ConflictLevel = Brief` to reduce noise

**Performance issues:**
- Disable dashboards you don't use (set position to `Hidden`)
- Reduce `MaxActiveZones`
- Disable Level 2 if not using order book data

---

## 🙏 Credits

- **IQMainGPU_Enhanced** builds on the full foundation of **IQMainGPU**
- **Smart Money Concepts (SMC)** — Liquidity zones, market structure
- **ICT (Inner Circle Trader)** — Session timing, OTE methodology, institutional order flow
- **PVSRA** — Volume-based candle classification
- **Traders Reality** — Inspiration for combining multiple analysis methods — https://tradersreality.com/
- **NinjaTrader Community** — Forum discussions and shared knowledge — https://ninjatrader.com/
- **Mike Swartz** — Mentor and Fellow Trader — https://www.youtube.com/@Mike_Swartz/featured
- **OSHO** — Mentor and Friend. A professional former pit floor trader with years of experience

---

## 📄 License

This indicator is provided free for personal use. Feel free to modify and improve!

---

## ⭐ If You Find This Useful

Consider starring the repository — it helps other traders discover these free tools.

---

**Happy Trading!** 📈💰

*Built with ❤️ for the NinjaTrader community*