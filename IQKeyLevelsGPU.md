# IQKeyLevelsGPU

**GPU-accelerated key-levels overlay indicator for NinjaTrader 8.**

Renders six independently-toggleable feature groups on the price panel using SharpDX Direct2D.
Coexists alongside `IQMainGPU.cs`, `IQMainUltimate.cs`, and `HourlyOpenStats.cs` in the same compiled assembly — all enum names use the `IQKL` prefix to avoid conflicts.

---

## Installation

1. Copy `Indicators/IQKeyLevelsGPU.cs` to `Documents\NinjaTrader 8\bin\Custom\Indicators\`
2. In NinjaTrader, open **NinjaScript Editor** → **Compile** (F5)
3. Add to any chart: **Indicators** → search *IQKeyLevelsGPU*

> Alternatively, import via **Tools → Import NinjaScript** and select the `.cs` file.

---

## Feature Groups & Settings

### 1. Session POCs — Asia / London / New York

Computes a volume-at-price profile for each of the three major sessions and draws a horizontal POC (Point of Control = highest-volume price bucket) line.

**Session windows (all times Eastern)**

| Session | Start  | End            | Note               |
|---------|--------|----------------|--------------------|
| Asia    | 19:00  | 04:00 (+1 day) | Cross-midnight     |
| London  | 03:00  | 11:30          | Same calendar day  |
| New York| 09:30  | 16:00          | RTH cash session   |

**Per-session settings (identical set for each):**

| Setting | Default | Description |
|---------|---------|-------------|
| Show [Session] POC | `true` | Enable/disable this session's POC lines |
| [Session] POC Color | Crimson / SteelBlue / ForestGreen | Line color |
| [Session] POC Opacity % | 80 | Line opacity (1–100) |
| [Session] POC Line Style | Solid | Solid / Dashed / Dotted |
| [Session] POC Thickness | 2 | Line width in pixels (1–5) |
| Show [Session] POC Labels | `true` | Toggle "Asia POC 7/11 21534.25" label |

**General POC settings:**

| Setting | Default | Description |
|---------|---------|-------------|
| POC Extension Days | 7 | Days each POC line extends forward (1–7). After this many calendar days the line stops rendering. |
| POC Bin Multiplier | 1 | Volume bucket width = TickSize × Multiplier. Increase for coarser profiles. |
| Fade Older POCs | `true` | Reduce opacity −12% per calendar day back so today's lines are fullest. |

---

### 2. Range Daily

Rolling-average daily range projected as RD High and RD Low around today's developing range (slack = (avgRange − todayRange) / 2 added above/below).

| Setting | Default | Description |
|---------|---------|-------------|
| Show RD Bands | `true` | Toggle the two RD lines |
| RD Color | DodgerBlue | Line color |
| RD Opacity % | 70 | Opacity (1–100) |
| RD Line Style | Dashed | Solid / Dashed / Dotted |
| RD Thickness | 1 | Line width |
| Show RD Labels | `true` | "RD H / RD L + price" labels |
| RD Lookback Days | 15 | Number of prior days in the rolling average (5–50) |

---

### 3. Psy Levels

Psychological round-number levels computed as `ceil(periodHigh / step) × step` (high) and `floor(periodLow / step) × step` (low), where `step = PsyRoundIncrement × TickSize`.

Available for three independent periods:

| Period | Default Color | Label prefix |
|--------|--------------|--------------|
| Daily | Orange | `DPsy H / DPsy L` |
| Weekly | Gold | `WPsy H / WPsy L` |
| Monthly | MediumPurple | `MPsy H / MPsy L` |

Each period has: Show toggle, Color, Opacity %, Line Style, Show Labels toggle.

| Setting | Default | Description |
|---------|---------|-------------|
| Psy Round Increment (ticks) | 50 | Increment in ticks; e.g. 50 on ES (0.25/tick) = 12.50 pts |

---

### 4. Weekly / Monthly Hi-Lo

Previous completed week's and month's high/low as full-width horizontal lines.

- **Week boundary** uses the Sunday-anchored comparison (robust cross-midnight detection, mirrors IQMainUltimate).
- **Month boundary** triggers when the calendar month changes.

| Setting | Default | Description |
|---------|---------|-------------|
| Show Last Week Hi/Lo | `true` | Toggle LWH / LWL lines |
| Last Week Color | MediumSeaGreen | |
| Last Week Opacity % | 80 | |
| Last Week Line Style | Dashed | |
| Last Week Thickness | 1 | |
| Show Last Week Labels | `true` | "LWH / LWL + price" labels |
| Show Last Month Hi/Lo | `true` | Toggle LMH / LML lines |
| Last Month Color | IndianRed | |
| Last Month Opacity % | 80 | |
| Last Month Line Style | Dashed | |
| Last Month Thickness | 1 | |
| Show Last Month Labels | `true` | "LMH / LML + price" labels |

---

### 5. Hourly Opens

Records `Open[0]` at the start of each new clock hour and draws a horizontal line from that bar to the end of the hour (or to the current bar for the live hour).

| Setting | Default | Description |
|---------|---------|-------------|
| Show Hourly Opens | `true` | Master toggle |
| Hourly Open Color | Yellow | Line color |
| Hourly Open Opacity % | 90 | |
| Hourly Open Line Style | Solid | Solid / Dashed / Dotted |
| Hourly Open Thickness | 1 | |
| Show Hourly Open Labels | `true` | "HO 14:00 21534.25" label |
| Start Hour (0–23) | 0 | Only track hours ≥ this value |
| End Hour (1–24) | 24 | Only track hours < this value (24 = all day) |
| Max Lines to Show | 6 | Number of most-recent hourly lines visible (1–24) |

---

### 6. Level 2 Walls

Maintains bid and ask order-book dictionaries via `OnMarketDepth`. Detects the single largest level exceeding `average × WallMultiplier` as the active wall and renders it as a horizontal line.

> **Requires an L2 data feed.** Enable "Level 2" in your NinjaTrader connection and check *Require Bid/Ask* in the data series settings.

| Setting | Default | Description |
|---------|---------|-------------|
| Enable Level 2 | `false` | Subscribe to OnMarketDepth |
| Wall Multiplier | 5 | Level flagged as wall when size > avg × multiplier (1–100) |
| Show Wall Lines | `true` | Render wall lines |
| Bid Wall Color | LimeGreen | |
| Bid Wall Opacity % | 90 | |
| Ask Wall Color | Crimson | |
| Ask Wall Opacity % | 90 | |
| Wall Line Thickness | 2 | |
| Show Wall Labels | `true` | "BID WALL x{size}" / "ASK WALL x{size}" labels |

---

### 7. General

| Setting | Default | Description |
|---------|---------|-------------|
| Label Font Size | 11 | Consolas font size for all labels (8–16) |
| Global Show Labels | `true` | Master on/off for every label in all feature groups |

---

## Technical Notes

- `Calculate = Calculate.OnPriceChange`; per-bar accumulation guarded by `IsFirstTickOfBar`.
- `IsOverlay = true`, `IsAutoScale = false`, no indicator plots.
- `MaximumBarsLookBack = Infinite` — ensures weekly/monthly high/low tracking works on intraday charts with limited "days to load".
- All SharpDX resources (brushes, text formats) created in `CreateDXResources`, disposed in `DisposeDXResources` and `OnRenderTargetChanged`, with the try/catch SharpDXException recovery pattern.
- Session POC list bounded to 21 entries (3 sessions × 7 days); hourly opens list bounded to 200 entries; collections read in `OnRender` via lock-snapshot pattern (`lock(_sessionLock) { snapshot = list.ToList(); }`).
- Label Y-positions use per-frame collision avoidance (`GetNonCollidingLabelY`) to prevent overlapping text.
- All color properties follow the `[XmlIgnore]` + `...Serializable` string pattern used across this repository.
