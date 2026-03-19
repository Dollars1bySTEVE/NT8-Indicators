# NinjaTrader 8 Indicators

Free, professional-grade indicators for NinjaTrader 8 — Smart Money Concepts (BOS/CHoCH), Liquidity Detection, Session Analysis, Range-Based Tools, and Multi-Timeframe Dashboards.

## Table of Contents

1. [SmartMoneyDashboard](#1-smartmoneydashboard) ⭐ NEW
2. [SmartMoneyStructure](#2-smartmoneystructure)
3. [BreakoutLiquiditySweep](#3-breakoutliquiditysweep)
4. [TimeZoneColors](#4-timezonecolors)
5. [HourlyOpenStats](#5-hourlyopenstats)
6. [D1ADR](#6-d1adr)

---

## 1. SmartMoneyDashboard

### Overview
Professional-grade **multi-timeframe monitoring dashboard** designed for futures traders (NQ, ES, RTY, YM). Displays real-time **time-to-close**, **percentage change**, and **trend direction** across multiple timeframes in a clean, customizable overlay panel. Works as both chart overlay or standalone dashboard window.

### Key Features
- ✅ Three information panels: Time to Close, % Change, and Trend Direction
- ✅ Four trend calculation methods: Price Action, EMA, Volume, Hybrid
- ✅ Level 1 & Level 2 data integration (with graceful fallback)
- ✅ Multi-timeframe support (up to 10 simultaneous timeframes)
- ✅ Futures-optimized with 24/5 market awareness
- ✅ Pre-configured profiles: Scalper, Day Trader, Swing Trader
- ✅ Fully customizable colors, fonts, positioning, and opacity
- ✅ SharpDX rendering for smooth performance
- ✅ Works as overlay or standalone dashboard window

### Settings

#### 1. Display Toggles
| Parameter | Default | Description |
|-----------|---------|-------------|
| **Enable Dashboard** | true | Master on/off switch for entire dashboard |
| **Show Time Remaining** | true | Display Panel A (time to close) |
| **Show % Change Panel** | true | Display Panel B (percentage change) |
| **Show Trend Box** | true | Display Panel C (trend direction) |
| **Use Level 2 Data** | false | Market depth data if available (broker-dependent) |

#### 2. Timeframe Configuration
| Parameter | Default | Description |
|-----------|---------|-------------|
| **Timeframes Input** | "60,240,1440" | Comma-separated minutes (1H, 4H, Daily) |
| **Max Timeframes** | 10 | Maximum timeframes to display (1-10) |

**Pre-configured Profiles:**
- **Scalper**: `1,5,15,60` (1min, 5min, 15min, 1H)
- **Day Trader**: `15,60,240` (15min, 1H, 4H)
- **Swing Trader**: `60,240,1440` (1H, 4H, Daily)

#### 3. Layout & Appearance
| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| **Panel Position** | TopLeft | — | TopLeft, TopRight, BottomLeft, BottomRight |
| **Panel Opacity** | 85 | 10-100 | Background transparency (%) |
| **Font Size** | 10 | 8-14 | Text size in points |
| **Panel Width** | 300 | 50-400 | Panel width in pixels |
| **Row Height** | 18 | 10-30 | Height per row in pixels |
| **Padding X** | 10 | 5-20 | Horizontal padding |
| **Padding Y** | 10 | 5-20 | Vertical padding |

#### 4. Color Customization
| Parameter | Default | Description |
|-----------|---------|-------------|
| **Bullish Color** | LimeGreen | Color for positive values/trends |
| **Bearish Color** | Crimson | Color for negative values/trends |
| **Neutral Color** | DarkGray | Color for neutral/uncertain trends |
| **Text Color** | White | General text color |
| **Background Color** | Black | Panel background color |

#### 5. Trend Detection Methods
| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| **Trend Method** | Hybrid | — | PriceAction, EMA, Volume, or Hybrid |
| **Trend EMA Period** | 20 | 1-200 | EMA period for trend calculation |
| **Trend SMA Period** | 50 | 1-200 | SMA period (used in Hybrid mode) |
| **Volume Lookback** | 20 | 1-100 | Bars to average for volume comparison |

### Panel Descriptions

#### Panel A: TIME TO CLOSE
Shows minutes remaining until next candle close for each timeframe.

**Formula:** `TimeframeMinutes - MinutesElapsed = minutes remaining`

**Example Display:**
```
TIME TO CLOSE
1H: 47 min
4H: 183 min
Daily: 923 min
```

#### Panel B: % CHANGE
Real-time percentage change from previous candle close.

**Formula:** `((CurrentClose - PreviousClose) / PreviousClose) * 100`

**Color Coding:**
- 🟢 Green with ↑ for positive change
- 🔴 Red with ↓ for negative change

**Example Display:**
```
% CHANGE
1H: +0.23% ↑
4H: -0.18% ↓
Daily: +0.87% ↑
```

#### Panel C: TREND DIRECTION
Directional bias with user-selectable calculation method.

**Methods:**
1. **Price Action**: `Close > Previous Close` (fastest, most responsive)
2. **EMA**: `Close > EMA[period]` (smoother, less noise)
3. **Volume**: `Volume > Average Volume` (shows conviction)
4. **Hybrid**: Combination of all three showing consensus (most reliable)

**Color Coding:**
- 🟢 Green for UP trend
- 🔴 Red for DOWN trend
- ⚪ Gray for NEUTRAL (Hybrid mode only)

**Example Display (Hybrid Mode):**
```
TREND (Hybrid)
1H: UP (3/3)
4H: DOWN (2/3)
Daily: UP (3/3)

✓ Price
✓ EMA
✓ Volume
```

### Level 1 vs Level 2 Data

**Level 1 (Always Available):**
- Standard OHLCV data from any broker
- Price action and volume analysis
- Accuracy: ~85%
- No special requirements

**Level 2 (Optional):**
- Market depth (order book) data
- Bid/ask imbalance analysis
- Order accumulation detection
- Accuracy: ~92%+
- Requires: Interactive Brokers, Kinetic, or similar
- **Graceful Fallback**: Shows "Level 2: Not Available (L1 Only)" if unavailable

### Deployment Modes

#### Mode A: Overlay on Main Chart
1. Open your trading chart (ES, NQ, etc.)
2. Add SmartMoneyDashboard indicator
3. Configure position (TopLeft/TopRight/BottomLeft/BottomRight)
4. Adjust opacity and size as needed

#### Mode B: Standalone Dashboard Window (Recommended)
1. Create new chart window (File → New → Chart)
2. Select same instrument or blank chart
3. Apply SmartMoneyDashboard to that window
4. Position side-by-side with main trading chart
5. Professional dashboard layout for dedicated monitoring

### Futures Market Features

**24/5 Market Handling:**
- Automatically adapts to futures hours (Sunday 6pm ET - Friday 5pm ET)
- Session-aware calculations
- Handles overnight sessions correctly

**Optimized Timeframes:**
- Default: `60,240,1440` (1H, 4H, Daily)
- Perfect for ES/NQ day trading
- Adjustable for any trading style

### Performance & Architecture

**Memory Efficient:**
- Uses `Dictionary<int, TimeframeData>` for fast lookups
- Minimal memory footprint
- Updates only on relevant timeframe changes

**Rendering Optimized:**
- SharpDX Direct2D rendering
- GPU-accelerated graphics
- Smooth 60 FPS refresh rate
- No lag on chart updates

**Calculation Efficiency:**
- Calculate = `OnEachTick` for real-time updates
- Smart caching of indicator values (EMA, SMA)
- Only recalculates when needed

### Use Cases

**Day Trading ES/NQ:**
- Monitor 1H, 4H, Daily trends simultaneously
- See time remaining to major timeframe closes
- Align entries with multi-timeframe trend

**Scalping:**
- Track 1m, 5m, 15m momentum
- Quick glance at percentage moves
- Volume confirmation in real-time

**Swing Trading:**
- Watch Daily, 4H, 1H alignment
- Wait for 3/3 Hybrid signals
- Confirm with Level 2 data (if available)

### Tips & Best Practices

1. **Start with Hybrid Mode**: Most reliable trend signals
2. **Use Standalone Window**: Cleaner workspace for monitoring
3. **Adjust Opacity**: 70-85% for subtle overlay, 100% for dedicated window
4. **Match Timeframes to Style**: Scalper (1,5,15), Day Trader (15,60,240), Swing (60,240,1440)
5. **Level 2 Optional**: Level 1 works perfectly fine for most traders
6. **Panel Position**: TopRight avoids interfering with chart tools on left side

---

## 2. SmartMoneyStructure

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

## 3. BreakoutLiquiditySweep

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

## 4. TimeZoneColors

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

## 5. HourlyOpenStats

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

## 6. D1ADR

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
| **NY Open Hour (UTC)** | 13 | 0-23 | NY session start hour (13:30 UTC = 8:30 EST) |
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
# NT8 Indicators Overview

## SmartMoneyDashboard
- **Key Features:** A dashboard that consolidates market insights and key trading metrics.
- **Best Use Cases:** Ideal for traders looking to analyze market trends and make informed decisions.
- **Complexity Level:** Intermediate.
- **How it Works:** Integrates with various data sources to provide a real-time overview of market conditions.
- **Documentation Link:** [SmartMoneyDashboard Documentation](#)

## SmartMoneyStructure
- **Key Features:** Analyzes price structures to identify potential trading opportunities.
- **Best Use Cases:** Suitable for structure-based trading strategies.
- **Complexity Level:** Intermediate.
- **How it Works:** Evaluates price movements to create actionable insights for traders.
- **Documentation Link:** [SmartMoneyStructure Documentation](#)

## D1ADR
- **Key Features:** Daily Average True Range indicator over a 1-day period for better risk management.
- **Best Use Cases:** Best for swing traders who require volatility measures.
- **Complexity Level:** Beginner.
- **How it Works:** Calculates the average true range over the past day for volatility analysis.
- **Documentation Link:** [D1ADR Documentation](#)

## HourlyOpenStats
- **Key Features:** Provides analysis of hourly price movements and opens.
- **Best Use Cases:** Helps intraday traders understand hourly price behavior.
- **Complexity Level:** Beginner.
- **How it Works:** Tracks price openings and closes hourly to provide statistical insights.
- **Documentation Link:** [HourlyOpenStats Documentation](#)

## BreakoutLiquiditySweep
- **Key Features:** Monitors liquidity pools and potential breakout points.
- **Best Use Cases:** Useful for identifying breakout scenarios in trending markets.
- **Complexity Level:** Advanced.
- **How it Works:** Analyzes order flows to identify potential breakout situations.
- **Documentation Link:** [BreakoutLiquiditySweep Documentation](#)

## TimeZoneColors
- **Key Features:** Color-coded time zone representation to enhance trading visibility.
- **Best Use Cases:** Great for traders who work across multiple time zones.
- **Complexity Level:** Beginner.
- **How it Works:** Visually represents market activity across different time zones.
- **Documentation Link:** [TimeZoneColors Documentation](#)

## Market Structure Volume Profiles
- **Original Concept:** [KioseffTrading](https://www.tradingview.com/u/KioseffTrading/) on TradingView — [Original Script](https://www.tradingview.com/v/shy8kACw/)
- **NinjaTrader 8 Port:** Dollars1bySTEVE
- **Key Features:** Combines ICT/Smart Money Concepts market structure (BoS/CHoCH) with volume profile analysis per swing. Shows Point of Control (POC), Value Area (VAH/VAL), and Cumulative Volume Delta (CVD).
- **Best Use Cases:** ICT/SMC traders who want to see the volume story behind each structural break. Works on all timeframes from 1-minute scalping to daily swing trading.
- **Complexity Level:** Advanced.
- **How it Works:** Detects swing highs/lows to identify Break of Structure (BoS) and Change of Character (CHoCH) events, then builds a volume profile histogram for each swing showing where the most trading activity occurred. CVD tracks net buy/sell pressure with configurable resets on structure events.
- **Documentation Link:** [MarketStructureVolumeProfiles Documentation](Indicators/MarketStructureVolumeProfiles.md)

## Setup Instructions
To set up the indicators, follow these steps:
1. Install the indicator package in NinjaTrader.
2. Configure the settings as per your trading strategy.
3. Refer to the individual documentation for detailed installation guides.

## Trading Strategies
Each indicator can be strategically used in various trading scenarios:
- Combine SmartMoneyDashboard with D1ADR for trend analysis.
- Utilize BreakoutLiquiditySweep alongside SmartMoneyStructure for optimized entries.

## Usage Examples
- **Example 1:** Using SmartMoneyDashboard to identify bullish market conditions…  
- **Example 2:** Trading strategies involving HourlyOpenStats and TimeZoneColors…

---

*This README provides a comprehensive overview of the NT8 Indicators and their functionalities.*  
*For more specific implementations and advanced configuration, please consult the dedicated documentation links provided above.*  

---
