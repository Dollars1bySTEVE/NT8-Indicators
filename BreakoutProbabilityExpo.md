# Breakout Probability (Expo) - NinjaTrader 8

## 📊 Overview

A NinjaTrader 8 port of the popular TradingView indicator **"Breakout Probability (Expo)"** by **Zeiierman**.

This indicator calculates and displays the statistical probability that price will break above or below the previous bar's high/low at various percentage distances. It provides traders with a data-driven approach to understanding breakout likelihood.

**Original TradingView Indicator:** [Breakout Probability (Expo) by Zeiierman](https://www.tradingview.com/script/Qt7fqntR-Breakout-Probability-Expo/)

---

## ✨ Features

### 🎯 Core Functionality
- **Probability Calculation** - Calculates the likelihood that the next bar will break above/below the prior bar's high/low
- **Multiple Levels** - Displays up to 5 stepped probability levels above and below price
- **Prior Candle Direction** - Separates statistics based on whether the prior candle was bullish (green) or bearish (red)
- **Backtest Statistics** - Real-time win/loss tracking with profitability percentage

### 🔄 Auto-Scaling (Enhanced Feature)
- **ATR-Based Step Calculation** - Automatically adjusts the distance between probability levels based on the instrument's volatility
- **Works Across All Instruments** - Futures, stocks, forex - no manual adjustment needed
- **Timeframe Adaptive** - Automatically adjusts for 1-minute to daily charts
- **Manual Override** - Option to use fixed percentage steps if preferred

### 🎨 Visual Customization
- **Color Settings** - Fully customizable bullish/bearish colors
- **Region Fill** - Optional shaded regions between probability levels with adjustable opacity
- **Line Styles** - Configurable line width, dash style (solid, dotted, dashed)
- **Label Formatting** - Adjustable font size for probability labels and stats panel

### 🔔 Alert System
- **Configurable Alerts** - Enable/disable alerts with customizable content
- **Alert Options** - Include ticker ID, high/low prices, bias direction, and percentages
- **Re-arm Timer** - Prevent alert spam with configurable cooldown period
- **Custom Sounds** - Use default or custom alert sounds

---

## 📈 Performance Results

Tested on NQ (Nasdaq Futures) across multiple timeframes:

| Timeframe | ATR | Auto Step | Win Rate |
|-----------|-----|-----------|----------|
| 1 min | 5.89 | 0.020% | 66.34% |
| 5 min | 10.76 | 0.020% | 65.60% |
| 15 min | 20.23 | 0.020% | 71.71% |
| 30 min | 36.93 | 0.022% | 69.59% |
| 60 min | 66.63 | 0.040% | 73.91% |

*Results based on historical data analysis. Past performance does not guarantee future results.*

---

## ⚙️ Settings

### Settings Group
| Setting | Default | Description |
|---------|---------|-------------|
| Step Mode | Auto | Auto or Manual step calculation |
| Manual Percentage Step | 0.1 | Fixed step % (when Manual mode) |
| Number of Lines | 5 | Probability levels to display (1-5) |
| Hide 0% Lines | True | Hide levels with 0% probability |
| Show Statistics Panel | True | Display win/loss stats |
| Calculate On Each Tick | False | Update frequency |
| Use Bid/Ask Break Logic | False | Use bid/ask for break detection |
| Line Length (bars) | 25 | How far back lines extend |
| Show Region Fill | True | Shaded areas between levels |

### Auto-Scale Settings
| Setting | Default | Description |
|---------|---------|-------------|
| ATR Period | 14 | Lookback period for ATR |
| ATR Multiplier | 0.15 | Fraction of ATR per level |
| Min Step % | 0.02 | Minimum allowed step |
| Max Step % | 1.0 | Maximum allowed step |
| Show Calculated Step | True | Display step in stats panel |

### Colors
| Setting | Default | Description |
|---------|---------|-------------|
| Bullish Color | LimeGreen | Color for upper levels |
| Bearish Color | Red | Color for lower levels |
| Region Opacity % | 12 | Transparency of filled regions |
| Label Color Up | LimeGreen | Text color for bullish labels |
| Label Color Down | Red | Text color for bearish labels |

### Style
| Setting | Default | Description |
|---------|---------|-------------|
| Line Width | 1 | Thickness of probability lines |
| Line Dash Style | Dot | Solid, Dash, Dot, etc. |
| Label Font Size | 10 | Size of percentage labels |
| Stats Font Size | 11 | Size of statistics panel text |

### Alerts
| Setting | Default | Description |
|---------|---------|-------------|
| Enable Alerts | False | Master alert switch |
| Alert Ticker | True | Include instrument name |
| Alert High/Low | True | Include price levels |
| Alert Bias | True | Include bullish/bearish bias |
| Alert Percentage | True | Include probability % |
| Alert Sound | (empty) | Custom sound file path |
| Alert Rearm Seconds | 0 | Cooldown between alerts |

---

## 🚀 Installation

1. Download `BreakoutProbabilityExpo.cs`
2. Open NinjaTrader 8
3. Go to **Tools → Import → NinjaScript Add-On**
4. Select the downloaded file
5. Restart NinjaTrader if prompted
6. Add to chart via **Indicators** menu

---

## 💡 Usage Tips

### Setting Trading Bias
Use the higher probability direction (up vs down) from higher timeframes to set your trading bias on lower timeframes.

### Stop Loss Placement
Place stop losses beyond levels that show very low breakout probability (< 10%).

### Breakout Confirmation
Combine with other indicators to confirm or filter breakout signals.

### Recommended Starting Settings
- **Scalping (1-5 min):** ATR Multiplier 0.10-0.12
- **Day Trading (15-30 min):** ATR Multiplier 0.15 (default)
- **Swing Trading (60 min+):** ATR Multiplier 0.20-0.25

---

## 📜 Credits

- **Original TradingView Indicator:** [Zeiierman](https://www.tradingview.com/u/Zeiierman/)
- **TradingView Script:** [Breakout Probability (Expo)](https://www.tradingview.com/script/Qt7fqntR-Breakout-Probability-Expo/)
- **NinjaTrader 8 Port:** Dollars1bySTEVE

---

## ⚠️ Disclaimer

This indicator is provided for educational and informational purposes only. It is not financial advice. Trading involves substantial risk of loss and is not suitable for all investors. Past performance is not indicative of future results. Always do your own research and consult with a qualified financial advisor before making trading decisions.

---

## 📝 Version History

- **v1.0** - Initial NinjaTrader 8 port with auto-scaling feature