# NinjaTrader 8 Indicators

Free, professional-grade indicators for NinjaTrader 8 — Smart Money Concepts (BOS/CHoCH), Liquidity Detection, Session Analysis, and Range-Based Tools.

## Table of Contents

1. [SmartMoneyStructure](#1-smartmoneystructure)
2. [BreakoutLiquiditySweep](#2-breakoutliquiditysweep)
3. [TimeZoneColors](#3-timezonecolors)
4. [HourlyOpenStats](#4-hourlyopenstats)
5. [D1ADR](#5-d1adr)

---

## 1. SmartMoneyStructure

### Overview
Detects **Break of Structure (BOS)** and **Change of Character (CHoCH)** for Smart Money Concepts trading. Identifies when price breaks through swing highs/lows while tracking trend direction to determine whether the break is a continuation (BOS) or reversal (CHoCH) signal.

### Key Features
- ✅ Dual structure detection (BOS and CHoCH)
- ✅ Configurable swing strength and confirmation requirements
- ✅ Visual zones with adjustable opacity around break levels
- ✅ Dynamic labels positioned based on direction
- ✅ SharpDX-accelerated rendering for smooth performance
- ✅ Works on any timeframe and instrument

### Settings

#### Structure Detection
| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| **Swing Strength** | 3 | 1-20 | Bars on each side required to confirm a swing high/low |
| **Confirmation Bars** | 1 | 1-10 | Consecutive closes beyond swing level to confirm break |

#### Appearance
| Parameter | Default | Description |
|-----------|---------|-------------|
| **Bullish Color** | LimeGreen | Color for bullish BOS/CHoCH signals |
| **Bearish Color** | Crimson | Color for bearish BOS/CHoCH signals |
| **Line Width** | 2 | Thickness of structure lines (1-5) |
| **Line Style** | Dash | Line pattern (Solid, Dash, DashDot, Dot, etc.) |
| **Show Labels** | true | Display "BOS" or "CHoCH" text on chart |
| **Label Font Size** | 11 | Font size for labels (8-12px) |

#### Zones
| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| **Show Zones** | true | — | Draw shaded zones at break levels |
| **Zone Opacity** | 15 | 5-50 | Zone transparency (5=faint, 50=visible) |
| **Zone Extend Bars** | 8 | 1-50 | Bars beyond break point to extend zones |

### How It Works
1. **Swing Detection**: Identifies swing highs/lows using configurable strength
2. **Trend Establishment**: Determines initial trend by comparing consecutive swings
3. **Break Detection**: Monitors price for confirmed breaks of swing levels
4. **Event Classification**:
   - **BOS**: Break in direction of current trend (continuation)
   - **CHoCH**: Break against current trend (potential reversal)

---

## 2. BreakoutLiquiditySweep

### Overview
Multi-timeframe indicator combining **breakout detection**, **liquidity sweep identification**, and **absorption zone analysis**. Uses dual EMAs with volume analysis to detect high-probability trade setups where institutional players may be absorbing supply/demand.

### Key Features
- ✅ Dual EMA trend confirmation (14/21 periods)
- ✅ Multi-timeframe context analysis
- ✅ Volume-based absorption detection
- ✅ Visual breakout signals with arrows
- ✅ Liquidity sweep markers (double arrows)
- ✅ Efficient cached indicator calculations

### Settings

| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| **Timeframe Minutes** | 60 | 1-10,000 | Higher timeframe for context |
| **EMA Period 1 (Fast)** | 14 | 1-500 | Short EMA for bullish signals |
| **EMA Period 2 (Slow)** | 21 | 1-500 | Long EMA for bearish signals |
| **Volume Lookback** | 20 | 1-100 | Bars to average for volume comparison |
| **Volume Threshold** | 1.5 | 1.0-5.0 | Multiple of avg volume to flag as "high" |
| **Detect Absorption** | true | — | Enable/disable absorption zone detection |

### Signal Types
- **🟢 Green Arrow Up**: Bullish breakout (Close crosses above EMA14)
- **🔴 Red Arrow Down**: Bearish breakout (Close crosses below EMA21)
- **🟢🟢 Double Green**: Bullish liquidity sweep (breakout + absorption)
- **🔴🔴 Double Red**: Bearish liquidity sweep (breakout + absorption)
- **🟡 Yellow Dot**: Absorption zone (high volume + tight range)

### Detection Logic
- **Breakout**: Price crosses EMA with momentum
- **Absorption**: High volume (>1.5x avg) + tight range (<70% of 3-bar avg)
- **Liquidity Sweep**: Breakout coinciding with absorption

---

## 3. TimeZoneColors

### Overview
Colors chart background based on configurable time windows representing different trading sessions (Tokyo, London, New York). Displays active session name in top-right corner and supports up to 3 simultaneous time regions with customizable colors and opacity.

### Key Features
- ✅ Up to 3 customizable time zones
- ✅ Intelligent color blending for overlapping sessions
- ✅ Timezone-crossing support (e.g., 19:00-04:00)
- ✅ Dynamic session labels with auto-contrast
- ✅ Optional audio/visual alerts on session changes
- ✅ Adjustable opacity (5-255)

### Settings

#### General Configuration
| Parameter | Default | Description |
|-----------|---------|-------------|
| **Zone Opacity** | 40 | Background transparency (5=faint, 255=solid) |
| **Color All Panels** | false | Apply coloring to all panels vs. price panel only |
| **Alert on Begin/End** | true | Audio/visual alerts at session transitions |
| **Text Font** | Arial 16pt | Font for zone label display |

#### Default Time Regions
| Region | Name | Time Range | Color |
|--------|------|------------|-------|
| **Region 1** | Tokyo | 19:00-04:00 | Pink |
| **Region 2** | London | 03:00-12:00 | Beige |
| **Region 3** | New York | 08:00-16:00 | Light Green |

Each region has fully customizable:
- Start Hour/Minute (24-hour clock)
- End Hour/Minute (24-hour clock)
- Background Color
- Display Name

### How It Works
- Detects current time zone based on bar timestamp
- Blends colors when multiple zones overlap (35% blend algorithm)
- Applies opacity via alpha channel for subtle backgrounds
- Updates zone label display in real-time

---

## 4. HourlyOpenStats

### Overview
Institutional-grade indicator tracking hourly market statistics including open/high/low prices, volume breakdown (bullish/bearish), and directional skew. Compares current hourly performance against N-day historical averages to identify patterns and breakout strength.

### Key Features
- ✅ Hourly open price tracking with visual markers
- ✅ High/low range boxes colored by direction
- ✅ Bullish/bearish volume separation and skew analysis
- ✅ Historical comparison (10-day rolling average)
- ✅ Real-time stats panel with comprehensive metrics
- ✅ Flexible hour filtering for custom trading sessions
- ✅ Range distribution percentage (current vs. historical)

### Settings

#### Display Options
| Parameter | Default | Description |
|-----------|---------|-------------|
| **Show Hour Open Lines** | true | Horizontal lines at each hour's open |
| **Show Hour Range Boxes** | true | Colored rectangles (high/low per hour) |
| **Show Hour Labels** | true | Multi-line stats annotations |
| **Show Stats Panel** | true | Fixed overlay with detailed analytics |
| **Show Skew Data** | true | Bullish/bearish volume percentages |
| **Show Pct Distributed** | true | Current range vs. avg percentage |
| **Labels Current Only** | false | Show only active hour label |
| **Max Label Hours** | 6 | Recent hours to display labels for (1-24) |
| **Stats Panel Position** | TopLeft | Panel placement (TopLeft, TopRight, etc.) |

#### Analysis Settings
| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| **Lookback Days** | 10 | 1-50 | Historical sample size for averaging |
| **Start Hour** | 0 | 0-23 | Analysis start hour (24hr format) |
| **End Hour** | 24 | 1-24 | Analysis end hour (supports overnight) |

#### Appearance Settings
| Parameter | Default | Description |
|-----------|---------|-------------|
| **Hour Open Brush** | Yellow | Opening line color |
| **Bullish Box Brush** | LimeGreen | Range box when close > open |
| **Bearish Box Brush** | Crimson | Range box when close < open |
| **Label Brush** | White | Text color for labels |
| **Bullish Box Opacity** | 10% | Green box transparency (3-50%) |
| **Bearish Box Opacity** | 8% | Red box transparency (3-50%) |
| **Open Line Width** | 2 | Line thickness (1-5) |
| **Open Line Dash** | DashDot | Line style |
| **Label Font** | Arial 9pt | Label font settings |
| **Label Y Offset** | 18px | Vertical label position (0-60) |

### Data Tracked Per Hour
- Open, High, Low prices
- Total volume, Up-volume, Down-volume
- Bar count and start time
- Historical range and volume averages
- Directional skew (Bullish >55% up, Bearish >55% down, Balanced)
- Range distribution percentage

### Stats Panel Information
- N-day average range
- Largest/smallest historical range
- Current hour metrics (volume, range, skew)
- High/Low prices
- Percentage of historical average used

---

## 5. D1ADR

### Overview
**Daily Average Range (ADR)** and **Weekly Average Range (AWR)** indicator with **Pivot Points**, **Session Opens**, and **Real-Time Info Box**. Displays expected daily/weekly movement ranges using historical averages, marks key session times, and optionally shows classical pivot levels. Includes visual fill zones with SharpDX rendering for lightweight performance.

### Key Features
- ✅ ADR calculation with configurable period (default 14 days)
- ✅ AWR calculation with configurable period (default 4 weeks)
- ✅ Daily and Weekly Pivot Points (PP, R1-R3, S1-S3, mid-pivots)
- ✅ Session Open markers (Asia, London, New York) with UTC time config
- ✅ Daily Open lines with three modes (Session/Midnight/Custom)
- ✅ SharpDX fill zones for ADR/AWR ranges
- ✅ Real-time info box showing current range vs ADR/AWR with percentage
- ✅ All properties fully customizable (colors, styles, widths, opacity)
- ✅ Works on any chart, symbol, and intraday timeframe
- ✅ Lightweight design with no multi-series overhead

### Settings

#### 1. ADR Settings
| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| **ADR Period (Days)** | 14 | 1-100 | Number of days to calculate average |
| **ADR Multiplier** | 1.0 | 0.1-5.0 | Multiplier for calculated ADR |
| **Show ADR** | true | — | Display ADR lines and zones |
| **ADR Color** | DodgerBlue | — | Color for ADR elements |
| **ADR Line Width** | 2 | 1-5 | Thickness of ADR boundary lines |
| **ADR Line Style** | Solid | — | Line pattern |
| **Show ADR Fill** | true | — | Fill zone between ADR boundaries |
| **ADR Fill Opacity** | 10 | 1-50 | Fill transparency percentage |

#### 2. AWR Settings
| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| **AWR Period (Weeks)** | 4 | 1-52 | Number of weeks to calculate average |
| **AWR Multiplier** | 1.0 | 0.1-5.0 | Multiplier for calculated AWR |
| **Show AWR** | true | — | Display AWR lines and zones |
| **AWR Color** | Purple | — | Color for AWR elements |
| **AWR Line Width** | 2 | 1-5 | Thickness of AWR boundary lines |
| **AWR Line Style** | Dash | — | Line pattern |
| **Show AWR Fill** | true | — | Fill zone between AWR boundaries |
| **AWR Fill Opacity** | 8 | 1-50 | Fill transparency percentage |

#### 3. Daily Open Settings
| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| **Show Daily Open** | true | — | Display daily opening price line |
| **Daily Open Color** | Yellow | — | Line color |
| **Daily Open Width** | 2 | 1-5 | Line thickness |
| **Daily Open Style** | Dot | — | Line pattern |
| **Daily Open Mode** | Session | — | Session / Midnight / Custom |
| **Custom Open Hour** | 9 | 0-23 | Hour for Custom mode |
| **Custom Open Minute** | 30 | 0-59 | Minute for Custom mode |

#### 4. Session Opens
| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| **Show Asia Session** | true | — | Display Asia session marker |
| **Asia Open Hour (UTC)** | 19 | 0-23 | Asia session start hour |
| **Asia Open Minute** | 0 | 0-59 | Asia session start minute |
| **Asia Session Color** | Pink | — | Marker color |
| **Show London Session** | true | — | Display London session marker |
| **London Open Hour (UTC)** | 3 | 0-23 | London session start hour |
| **London Open Minute** | 0 | 0-59 | London session start minute |
| **London Session Color** | Beige | — | Marker color |
| **Show NY Session** | true | — | Display NY session marker |
| **NY Open Hour (UTC)** | 9 | 0-23 | NY session start hour |
| **NY Open Minute** | 30 | 0-59 | NY session start minute |
| **NY Session Color** | LightGreen | — | Marker color |
| **Session Line Width** | 1 | 1-5 | Vertical marker thickness |
| **Session Line Style** | Dot | — | Line pattern |

#### 5. Pivot Points
| Parameter | Default | Description |
|-----------|---------|-------------|
| **Show Daily Pivots** | true | Display daily pivot levels (PP, R1-R3, S1-S3) |
| **Show Weekly Pivots** | false | Display weekly pivot levels |
| **Show Mid Pivots** | false | Display mid-level pivots (M1-M5) |
| **Pivot Color** | Gray | Color for all pivot lines |
| **Pivot Line Width** | 1 | Thickness (1-3) |
| **Pivot Line Style** | Dash | Line pattern |

#### 6. Labels & Info
| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| **Show Labels** | true | — | Display text labels on markers |
| **Label Color** | White | — | Text color |
| **Label Font Size** | 10 | 8-16 | Font size for labels |
| **Show Info Box** | true | — | Display real-time stats overlay |
| **Info Box Position** | TopLeft | — | Panel placement (TopLeft/TopRight) |

### Info Box Display
Real-time overlay showing:
- **ADR (Nd)**: Current N-day average daily range
- **Current**: Today's range so far
- **Percentage**: Current range as % of ADR (e.g., 78.5%)
- **AWR (Nw)**: Current N-week average weekly range
- **AWR Percentage**: Current range as % of AWR

### Calculation Methods
- **ADR**: Average of last N daily high-low ranges × multiplier
- **AWR**: Average of last N weekly high-low ranges × multiplier
- **Pivots**: Classical pivot formula (H+L+C)/3, R1 = 2P-L, S1 = 2P-H, etc.
- **Ranges**: Centered around daily/weekly open prices

### Use Cases
- Identify when daily range is extended vs. compressed
- Set profit targets based on average movement
- Recognize when price approaches ADR/AWR boundaries
- Mark key session times for entry/exit planning
- Use pivots as support/resistance levels

---

## Installation

1. Download the `.cs` files from the `Indicators` folder
2. Copy to: `Documents\NinjaTrader 8\bin\Custom\Indicators\`
3. Open NinjaTrader 8
4. Compile (Tools → Compile)
5. Apply to any chart

## License

Free and open-source. Use at your own risk. No warranty provided.

## Author

**Dollars1bySTEVE**  
Repository: [github.com/Dollars1bySTEVE/NT8-Indicators](https://github.com/Dollars1bySTEVE/NT8-Indicators)
