# IQMainGPU — Traders Reality inspired Main Indicator

**The Ultimate All-in-One NinjaTrader 8 Indicator**

GPU-accelerated • Institutional-Grade • Fully Customizable

> 📸 *[<img width="2200" height="1316" alt="image" src="https://github.com/user-attachments/assets/ed4210db-44b7-43ca-b8bc-b1f88f728cc7" />
]*

---

A comprehensive trading indicator that combines Smart Money Concepts, PVSRA analysis, session tracking, VWAP, and more — all GPU-rendered for maximum performance. What would normally require 10+ separate indicators is consolidated into a single, efficient package.

---

## ⚠️ IMPORTANT: Installation & Performance Settings

**Critical Configuration for Best Performance:**

1. **Calculate Setting**: Set to **"On price change"** (NOT "On bar close" or "On each tick")
   - This balances responsiveness with resource efficiency

2. **DO NOT enable "Auto scale"** — The indicator manages its own scaling

3. **DO NOT tick "Auto render"** — This is **critical** for performance!
   - The indicator uses custom GPU rendering via SharpDX
   - Auto render will cause unnecessary redraws and performance issues
     
4. If you want EMA markers for EMA'S , Click on "Price Marker(s)" 
---

## Table of Contents

- [Features Overview](#-features-overview)
  - [EMAs](#-emas-exponential-moving-averages)
  - [OTE Zones (NEW!)](#-ote-zones-optimal-trade-entry)
  - [Pivot Points](#-pivot-points)
  - [Market Sessions](#-market-sessions-8-sessions)
  - [Daily Open Lines](#-daily-open-lines-5-types)
  - [Range Levels](#-range-levels)
  - [VWAP](#-vwap-volume-weighted-average-price)
  - [PVSRA](#️-pvsra-price-volume-spread-analysis)
  - [Liquidity Zones](#-liquidity-zones)
  - [Volume Delta & Microstructure](#-volume-delta--microstructure)
  - [Fake Breakout Detection](#️-fake-breakout-detection)
  - [Order Book (Level 2)](#-order-book-level-2)
  - [Dashboard](#️-dashboard)
  - [Additional Levels](#-additional-levels)
- [GPU Rendering](#️-gpu-rendering)
- [Installation](#️-installation)
- [Recommended Settings](#-recommended-settings)
- [Parameter Groups](#-parameter-groups)
- [Screenshots](#-screenshots)
- [Troubleshooting](#-troubleshooting)
- [Inspiration & Credits](#-inspiration--credits)
- [License](#-license)
- [Contributing](#-contributing)

---

## 🎯 Features Overview

### 📊 EMAs (Exponential Moving Averages)
- 5 EMAs: 5, 13, 50, 200, 800 periods
- EMA 50 Cloud (standard deviation envelope)
- Customizable colors, thickness, labels
- GPU-rendered for smooth display
- **Smart Timeframe Handling**: Automatically uses extended lookback on higher timeframes (1hr+) to ensure long-period EMAs (50, 200, 800) display correctly

### 🎯 OTE Zones (Optimal Trade Entry)
**NEW!** ICT-style Optimal Trade Entry zones for precision entries during pullbacks.

**What is OTE?**
OTE (Optimal Trade Entry) is an ICT methodology concept that identifies the "sweet spot" for trade entries during retracements. It uses specific Fibonacci levels to pinpoint high-probability reversal zones.

**Features:**
- **Automatic Swing Detection** — Identifies swing highs/lows automatically
- **Three Key Levels**:
  - **62%** — Top of OTE zone
  - **70.5%** — Optimal entry line (the sweet spot)
  - **79%** — Bottom of OTE zone
- **Bullish & Bearish Zones** — Detects both buy and sell setups:
  - Bullish OTE: After swing low → swing high (buy on pullback down)
  - Bearish OTE: After swing high → swing low (sell on pullback up)
- **GPU-Rendered** — Smooth, flicker-free display
- **Fully Customizable**:
  - Zone fill colors and opacity
  - Line colors (62%/79% and 70.5% optimal)
  - Line thickness and style
  - Label prefix customization
  - Max zones to display

**Settings:**
| Parameter | Default | Description |
|-----------|---------|-------------|
| Show OTE | false | Master toggle (OFF by default — user opts in) |
| OTE Swing Strength | 5 | Bars to confirm swing high/low |
| OTE Max Zones | 3 | Maximum zones to display |
| OTE Bullish Color | DodgerBlue | Buy zone fill color |
| OTE Bearish Color | Crimson | Sell zone fill color |
| OTE Zone Opacity | 15 | Zone fill opacity % |
| OTE Line Color | DodgerBlue | 62%/79% line color |
| OTE Optimal Color | Gold | 70.5% optimal line color |
| OTE Line Thickness | 1 | Standard line thickness |
| OTE Optimal Thickness | 2 | 70.5% line thickness (bolder) |
| Show OTE Labels | true | Display labels with price |
| OTE Label Prefix | "OTE" | Customizable label prefix |

**How to Use OTE:**
1. Enable `ShowOTE` in indicator settings
2. Wait for price to pull back into the OTE zone (62%-79%)
3. Look for entries near the 70.5% "optimal" line (gold)
4. Combine with other confluences (order blocks, VWAP, session levels)

### 📍 Pivot Points
- Classic Floor Pivots (PP, R1–R3, S1–S3)
- M-Level midpoints (M0–M5)
- Customizable line styles (solid, dashed, dotted)
- Optional labels with price display

### 🌍 Market Sessions (8 Sessions)
- **London** (08:00–16:30 UTC, UK DST adjusted)
- **New York** (14:30–21:00 UTC, US DST adjusted)
- **Tokyo** (00:00–06:00 UTC)
- **Hong Kong** (01:30–08:00 UTC)
- **Sydney** (22:00–06:00 UTC, AU DST adjusted)
- **EU Brinks** (08:00–09:00 UTC)
- **US Brinks** (14:00–15:00 UTC)
- **Frankfurt** (07:00–16:30 UTC)

Features:
- Session boxes with opening range visualization
- Automatic DST handling for all regions
- Customizable colors, opacity, labels per session

### 📈 Daily Open Lines (5 Types)
- **ETH Daily Open** — Anchored to 6:00 PM ET (futures session start)
- **Asia Session Open** — Tokyo/Asia RTH open
- **London Session Open** — London RTH open
- **US Session Open** — New York RTH open
- **Calendar Daily Open** — Standard midnight reset

Each line:
- Extends RIGHT from session open to session close
- Proper DST handling
- Customizable colors, styles, labels

### 📏 Range Levels
- **ADR** (Average Daily Range) with 50% midline
- **AWR** (Average Weekly Range) with 50% midline
- **AMR** (Average Monthly Range) with 50% midline
- **RD** (Range Daily Hi/Lo)
- **RW** (Range Weekly Hi/Lo)
- Configurable lookback periods
- Alerts when price reaches range extremes

### 📊 VWAP (Volume Weighted Average Price)
- **Dynamic color change**: Green when price > VWAP, Red when price < VWAP
- **Standard Deviation Bands**: ±1σ, ±2σ, ±3σ
- **Session anchoring**: ETH (6PM ET), RTH US, or Both
- Optional band fill
- Fully customizable colors, opacity, thickness

### 🕯️ PVSRA (Price Volume Spread Analysis)
- Volume-based candle classification
- **High Volume Bullish** (Green) — Strong buying
- **High Volume Bearish** (Red) — Strong selling
- **Mid Volume Bullish** (Blue) — Moderate buying
- **Mid Volume Bearish** (Pink) — Moderate selling
- Configurable volume thresholds

### 🟦 Liquidity Zones
- Automatic detection from PVSRA candles
- Unrecovered zones displayed as shaded rectangles
- Partial recovery tracking
- Configurable max zones, opacity, colors

### 📉 Volume Delta & Microstructure
- Real-time buy/sell volume estimation
- Cumulative delta tracking
- Absorption detection (trapped traders)
- Imbalance flagging with rolling percentile thresholds

### ⚠️ Fake Breakout Detection
- 5-filter system:
  1. Confirmation bars check
  2. Volume follow-through analysis
  3. Momentum divergence (RSI-based)
  4. S/R level validation
  5. Risk/Reward ratio filter
- Visual alerts on detected fake breakouts

### 📚 Order Book (Level 2)
- Bid/Ask depth visualization
- Wall detection (large resting orders)
- Anti-spoofing checks
- Requires broker L2 data support

### 🎛️ Dashboard
- Unified on-chart statistics panel
- Session status, delta info, signal flags
- Range statistics (ADR/AWR/AMR in pips)
- Configurable position (corners)
- DST reference table option

### 📅 Additional Levels
- **Yesterday High/Low** — Previous day's range
- **Last Week High/Low** — Previous week's range
- **Psy Levels** — Psychological round numbers (daily & weekly)

---

## 🖥️ GPU Rendering

IQMainGPU uses **SharpDX Direct2D** for GPU-accelerated rendering:

- ✅ Smooth, flicker-free display
- ✅ Efficient resource management
- ✅ Handles complex multi-element charts
- ✅ Automatic resource cleanup

This is why the **"Auto render" setting must remain OFF** — the indicator manages its own render cycle.

---

## ⚙️ Installation

1. Download `IQMainGPU.cs` from the `Indicators` folder
2. In NinjaTrader 8, go to **Tools → Import → NinjaScript...**
3. Select the downloaded file
4. The indicator will compile automatically

**Or manual installation:**
1. Copy `IQMainGPU.cs` to: `Documents\NinjaTrader 8\bin\Custom\Indicators\`
2. In NinjaTrader, open **NinjaScript Editor**
3. Right-click and select **Compile**

---

## ⚡ Recommended Settings

| Setting | Recommended Value | Why |
|---------|-------------------|-----|
| **Calculate** | On price change | Best balance of responsiveness and performance |
| **Auto scale** | OFF | Indicator manages scaling |
| **Auto render** | OFF | ⚠️ CRITICAL — GPU rendering requires this OFF |

---

## 📝 Parameter Groups

The indicator organizes 150+ parameters into logical groups:

1. **Core** — Asset class, candle color mode, L2 toggle
2. **EMAs** — All EMA settings
3. **Pivot Points** — PP, R/S levels, M-levels
4. **Sessions** (per session) — London, NY, Tokyo, etc.
5. **Range Levels** — ADR, AWR, AMR, RD, RW
6. **Daily/Weekly Levels** — Yesterday, Last Week, Daily Open, Psy
7. **RTH Session Opens** — Asia, London, US open lines
8. **PVSRA Vectors** — Volume thresholds
9. **Liquidity Zones** — Zone settings
10. **Volume Delta** — Delta lookback, absorption sensitivity
11. **Fake Breakout** — Filter settings
12. **Order Book** — Wall detection, spoofing checks
13. **Dashboard** — Position, font, opacity
14. **Tables** — Range table, DST table
15. **OTE Zones** — OTE settings (NEW!)
16. **VWAP** — All VWAP and band settings

---

## 📸 Screenshots

> 📸 *[Suggest adding screenshots showing:]*
> 1. Full indicator with all features enabled
> 2. VWAP with bands
> 3. Session boxes
> 4. PVSRA candles with liquidity zones
> 5. Dashboard overlay
> 6. OTE zones with entry levels

---

## 🐛 Troubleshooting

**Indicator not displaying:**
- Ensure "Auto render" is OFF
- Check that Calculate is set to "On price change"
- Verify the indicator compiled without errors

**Performance issues:**
- Disable features you don't need
- Reduce max liquidity zones
- Disable Level 2 if not using order book

**VWAP not showing:**
- Ensure ShowVwap is enabled
- Check that sufficient bars have loaded for calculation

**EMAs not showing on higher timeframes (2hr+):**
- This has been fixed! The indicator now automatically uses extended lookback on higher timeframes
- EMA 50, 200, 800 should display correctly on all timeframes

**OTE zones not appearing:**
- Ensure `ShowOTE` is enabled (it's OFF by default)
- Check that enough bars have loaded for swing detection
- Verify `OTESwingStrength` setting (default: 5)

---

## 🙏 Inspiration & Credits

This indicator was inspired by and builds upon concepts from:

- **PVSRA (Price Volume Spread Analysis)** — Original concept for volume-based candle analysis
- **Smart Money Concepts (SMC)** — Liquidity zones, market structure
- **ICT (Inner Circle Trader)** — Session timing, killzones, institutional order flow concepts, **OTE methodology**
- **Traders Reality** — The namesake inspiration for combining multiple analysis methods https://tradersreality.com/
- **NinjaTrader Community** — Countless forum discussions and shared knowledge https://ninjatrader.com/
- **Mike Swartz** - Mentor and Fellow Trader Watch him live! https://www.youtube.com/@Mike_Swartz/featured
- ** OSHO**- Mentor and Friend. A professional former pit floor Trader with years of experience 

Special thanks to all the traders and educators who have shared their knowledge freely, making tools like this possible.

---

## 📄 License

This indicator is provided free for personal use. Feel free to modify and improve!

---

## 🤝 Contributing

Contributions welcome! If you have improvements or bug fixes:
1. Fork the repository
2. Create a feature branch
3. Submit a pull request
4. LET ME KNOW!
---

## ⭐ If You Find This Useful

Consider starring the repository! It helps others discover these free tools.

---

**Happy Trading!** 📈💰

*Built with ❤️ for the NinjaTrader community*
