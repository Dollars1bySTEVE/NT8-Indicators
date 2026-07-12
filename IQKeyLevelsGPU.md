# IQKeyLevelsGPU

**GPU-accelerated key-levels overlay indicator for NinjaTrader 8.**

Renders independently-toggleable feature groups on the price panel using SharpDX Direct2D.
Coexists alongside `IQMainGPU.cs`, `IQMainUltimate.cs`, and `HourlyOpenStats.cs` in the same compiled assembly — all enum names use the `IQKL` prefix to avoid conflicts.

---

## Installation

1. Copy `Indicators/IQKeyLevelsGPU.cs` to `Documents\NinjaTrader 8\bin\Custom\Indicators\`
2. In NinjaTrader, open **NinjaScript Editor** → **Compile** (F5)
3. Add to any chart: **Indicators** → search *IQKeyLevelsGPU*

> Alternatively, import via **Tools → Import NinjaScript** and select the `.cs` file.

---

## Feature Groups & Settings

### 1a. Session Windows (ET)

All three session windows are fully configurable (hour/minute, Eastern Time) and drive **both** the POC volume profiles and the London/New York Open/Close lines.

| Session | Default Start | Default End | Note |
|---------|---------------|-------------|------|
| Asia    | 18:00 | 03:00 (+1 day) | Cross-midnight; correctly rolls over across the Sunday 18:00 ET weekend open |
| London  | 03:00 | 11:30 | Same calendar day |
| New York| 08:00 | 16:00 | Same calendar day |

Each session exposes `*StartHour`, `*StartMin`, `*EndHour`, `*EndMin` properties (0–23 / 0–59, same style as `IQMainUltimate`'s custom time properties). A window is treated as cross-midnight automatically whenever End ≤ Start.

`EndLondonAtNyOpen` now cuts London's **POC profile only** at the configured NY start time (not a hard-coded 09:30) — London's Open/Close lines are unaffected and still use the configured London end time.

---

### 1. Session POCs — Asia / London / New York

Computes a volume-at-price profile for each of the three sessions above and draws a horizontal POC (Point of Control = highest-volume price bucket) line.

> **Asia keeps ONLY its POC.** Asia no longer renders Open/Close lines — see section 1b below.

**Per-session settings (identical set for each):**

| Setting | Default | Description |
|---------|---------|-------------|
| Show [Session] POC | `true` | Enable/disable this session's POC lines |
| [Session] POC Color | Crimson / SteelBlue / ForestGreen | Line color |
| [Session] POC Opacity % | 80 | Line opacity (1–100) |
| [Session] POC Line Style | Solid | Solid / Dashed / Dotted |
| [Session] POC Thickness | 2 | Line width in pixels (1–5) |
| Show [Session] POC Labels | `true` | Toggle label, e.g. `"Asia POC Mon. 21534.25"` |

**General POC settings:**

| Setting | Default | Description |
|---------|---------|-------------|
| POC Extension Days | 7 | Days each POC line extends forward (1–7). After this many calendar days the line stops rendering. |
| POC Bin Multiplier | 1 | Volume bucket width = TickSize × Multiplier. Increase for coarser profiles. |
| Fade Older POCs | `true` | Reduce opacity −12% per calendar day back so today's lines are fullest. |
| Limit POC Bins | 500 | Max volume-profile buckets per session (50–5000). If a session's profile grows past this cap, the bin size is auto-doubled and existing data is re-bucketed (never dropped) — bounds memory/CPU growth on very active sessions. |

---

### 1c. POC Clusters

Automatically detects zones where several session POCs (across sessions/days) land within a narrow price range, and shades that zone as a potential high-confluence area.

**Algorithm:** all active, non-expired POC prices are sorted, then greedily grouped so each group's max−min spread stays within `Cluster Zone Width`; any group with at least `Cluster Min POC Count` members becomes a visible cluster zone. Recomputed once per bar (first tick only).

| Setting | Default | Description |
|---------|---------|-------------|
| Show POC Clusters | `true` | Master toggle |
| Cluster Zone Width (points) | 35.0 | Max price spread between the lowest/highest POC in a group (5–200) |
| Cluster Min POC Count | 2 | Minimum POCs required to form a zone (2–5) |
| Cluster Color | Gold | Shading + label color |
| Cluster Opacity % | 15 | Rectangle fill opacity (5–50) |
| Show Cluster Labels | `true` | Toggle `"POC Cluster ×3"` label |
| Cluster Audio Alert | `false` | Play a one-shot alert sound the first time price enters a zone; resets when price leaves. Fires only in `State.Realtime` (never during historical load/replay). |

---

### 1b. Session Opens/Closes — London / New York

For London and New York, records the first in-session bar's **Open price** and the last in-session bar's **Close price** per day, then renders them as horizontal lines extending forward for an adjustable number of days.

> **Asia's legacy Open/Close properties are retained but hidden** (`[Browsable(false)]`) purely so previously-saved workspaces/templates deserialize without error. They are fully ignored by the indicator's logic and rendering — Asia only ever draws its POC (section 1 above).

- **Open lines** appear immediately when the session begins (live); style defaults to **Solid**.
- **Close lines** only appear once the session is complete; style defaults to **Dashed**.
- One color per session (shared by both Open and Close), matching the POC color defaults.
- Labels format: `"London Open Mon. 29850.00"` / `"NY Close Mon. 29901.25"` (see weekday-label format below).

**Per-session settings (identical set for London and NY):**

| Setting | Default | Description |
|---------|---------|-------------|
| Show [Session] Open/Close | `true` | Master toggle for this session's Open/Close lines |
| Show [Session] Open Lines | `true` | Sub-toggle for Open lines only |
| Show [Session] Close Lines | `true` | Sub-toggle for Close lines only |
| [Session] OC Color | SteelBlue / ForestGreen | Color for both Open and Close lines |
| [Session] OC Opacity % | 80 | Opacity (1–100) |
| [Session] Open Line Style | Solid | |
| [Session] Close Line Style | Dashed | |
| [Session] OC Thickness | 1 | Line width in pixels (1–5) |
| Show [Session] OC Labels | `true` | Toggle Open/Close price labels |

**London-specific:**

| Setting | Default | Description |
|---------|---------|-------------|
| End London Profile at NY Open | `false` | When enabled, London's **volume-at-price profile** stops accumulating at the configured NY start time instead of the configured London end time. London **Open/Close price lines** are NOT affected — London Close still uses the configured London end time. |

**General Open/Close settings:**

| Setting | Default | Description |
|---------|---------|-------------|
| OC Extension Days | 7 | Days each Open/Close line extends forward (1–7) |
| Fade Older Open/Close | `true` | Progressively reduce opacity of older lines (−12% per day back) |

---

### Weekday label format

All POC and Open/Close labels use weekday abbreviations instead of `M/d` dates: `Mon.`, `Tues.`, `Wed.`, `Thur.`, `Fri.`, `Sat.`, `Sun.`

If a session's date falls in a **previous week** relative to the current bar's week (Sunday-anchored week start, matching the week-rollover convention used elsewhere in the file), the label is prefixed `Pr` — e.g. `PrMon.`, `PrFri.` — so last week's Monday is visually distinct from this week's Monday.

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
| Use Only Completed Days for RD | `false` | When enabled, today's developing range is excluded from the **average** used for RD projection; the bands still project from today's current high/low, preserving the usual RD behavior while using only completed prior days in the average. |

---

### 3. Psy Levels

Psychological round-number levels computed as `ceil(periodHigh / step) × step` (high) and `floor(periodLow / step) × step` (low), where `step = PsyRoundIncrement × TickSize`. Values are cached once per bar in `OnBarUpdate` (not recomputed every render frame).

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

Records `Open[0]` at the start of each new clock hour and draws a horizontal line from that bar to the end of the hour (or to the chart right edge for the live hour, subject to `Extend Lines to Right Edge`).

| Setting | Default | Description |
|---------|---------|-------------|
| Show Hourly Opens | `true` | Master toggle |
| Hourly Open Color | Yellow | Line color |
| Hourly Open Opacity % | 90 | |
| Hourly Open Line Style | Solid | Solid / Dashed / Dotted |
| Hourly Open Thickness | 1 | |
| Show Hourly Open Labels | `true` | "HO 14:00 21534.25" label at line end |
| Start Hour (0–23) | 0 | Only track hours ≥ this value |
| End Hour (1–24) | 24 | Only track hours < this value (24 = all day) |
| Max Lines to Show | 6 | Number of most-recent hourly lines visible (1–24) |

---

### 6. Level 2 Walls

Maintains bid and ask order-book dictionaries via `OnMarketDepth`, with a running size sum/count updated incrementally on every book event (no full re-summation pass). Detects the single largest level exceeding `average × WallMultiplier` as the active wall and renders it as a horizontal line.

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
| Label Anchor | LineStart | **LineStart**: labels at line origin (left/start edge). **LineEnd**: labels at line right end (near current price / right edge of chart). Label X positions are clamped so LineEnd labels never render off-screen near the right edge. |
| Extend Lines to Right Edge | `true` | When enabled, POC and Open/Close lines extend to the chart's right edge. When disabled, they render only out to the last printed bar. |

---

## Technical Notes

- `Calculate = Calculate.OnPriceChange`; per-bar accumulation guarded by `IsFirstTickOfBar`.
- `IsOverlay = true`, `IsAutoScale = false`, no indicator plots.
- `MaximumBarsLookBack = Infinite` — ensures weekly/monthly high/low tracking works on intraday charts with limited "days to load".
- All SharpDX resources (brushes, text formats) created in `CreateDXResources`, disposed in `DisposeDXResources` and `OnRenderTargetChanged`, with the try/catch SharpDXException recovery pattern.
- Session POC list bounded to 21 entries (3 sessions × 7 days); Session OC list also bounded to 21 entries; hourly opens list bounded to 200 entries; collections read in `OnRender` via lock-snapshot pattern (`lock(_sessionLock) { snapshot = list.ToList(); }`).
- Render helpers guard against `Bars`/`ChartBars`/`RenderTarget` being null (e.g. during replay/teardown) before touching chart geometry.
- Label Y-positions use per-frame collision avoidance (`GetNonCollidingLabelY`), now bucketed into **two independent HashSets** — one for left-anchored labels, one for right-anchored — so labels only collide against others in the same screen region; both buckets are cleared once per `OnRender` frame.
- Each session POC volume profile tracks its own effective bin size (`CurrentBinSize`), which auto-doubles and re-buckets existing data whenever the profile exceeds `LimitPocBins`, instead of dropping data or growing unbounded.
- `DetectOrderBookWalls` uses a running bid/ask size sum maintained incrementally in `OnMarketDepth`, avoiding a full re-summation of the book on every event.
- All color properties follow the `[XmlIgnore]` + `...Serializable` string pattern used across this repository.
- All new enums (`IQKLLineStyle`, `IQKLLabelAnchor`) are declared outside all namespaces per NT8 requirements.
- All existing public `[NinjaScriptProperty]` names are preserved for workspace/template serialization compatibility, including the now-hidden legacy Asia Open/Close properties.
