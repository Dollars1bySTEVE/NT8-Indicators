# Breakout Probability (Expo)

**NinjaTrader 8 Port of TradingView Indicator by Zeiierman**

GPU-accelerated • Probability-Based • Snapshot Architecture

---

## Overview

Breakout Probability (Expo) calculates the **historical probability** of price breaking above or below the prior bar's high/low. It answers the question: *"Based on history, what are the odds of price reaching these levels?"*

The indicator displays probability percentages at multiple price levels extending from the prior bar, helping traders understand the statistical likelihood of price movement in either direction.

---

## How It Works

### The Concept

After each bar closes, the indicator looks at historical data to determine:

1. **After a GREEN (bullish) bar:**
   - What % of the time did price break above the prior high?
   - What % of the time did price break above the prior high + 1%?
   - What % of the time did price break above the prior high + 2%?
   - (Same analysis for downside breaks below the low)

2. **After a RED (bearish) bar:**
   - Same analysis, but in bearish context

### Visual Display

```
                              ─── 0.20%  (green - upside)
                              ─── 0.59%
                              ─── 3.74%
                              ─── 21.77%
    ┌───────┐                 ─── 79.75% ← High probability at prior high
    │       │
    │ Prior │ ════════════════════════════════════════►
    │  Bar  │
    │       │                 ─── 23.85% ← Probability at prior low
    └───────┘                 ─── 7.77%
                              ─── 2.32%  (red - downside)
                              ─── 1.08%
                              ─── 0.54%
```

- **Green lines/labels**: Upside probability levels (from prior HIGH)
- **Red lines/labels**: Downside probability levels (from prior LOW)
- Lines extend RIGHT from the current bar into empty space
- Probabilities are **LOCKED** until the next bar closes

---

## Key Features

- ✅ **Snapshot Architecture** — Calculates ONCE at bar close, results locked until next bar (TradingView-style)
- ✅ **No Mid-Bar Flickering** — Probabilities don't change while current bar is forming
- ✅ **Auto or Manual Step Mode** — ATR-based adaptive stepping or fixed percentage
- ✅ **Up to 5 Probability Levels** — Configurable number of lines
- ✅ **Region Fill** — Optional shaded zones between levels
- ✅ **Win/Loss Tracking** — Built-in backtest statistics panel
- ✅ **GPU-Rendered** — SharpDX Direct2D for smooth performance
- ✅ **Customizable Alerts** — Notifications on probability conditions

---

## Settings

### Settings Group

| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| **Step Mode** | Auto | Auto/Manual | Auto uses ATR-based step, Manual uses fixed percentage |
| **Manual Percentage Step** | 0.1 | 0.001+ | Fixed step % when in Manual mode |
| **Number of Lines** | 5 | 1-5 | How many probability levels to display |
| **Hide 0% Lines** | false | — | Don't show levels with 0% probability |
| **Show Statistics Panel** | true | — | Display WIN/LOSS/Profitability panel |
| **Calculate On Each Tick** | false | — | Not recommended — use bar close |
| **Use Bid/Ask Break Logic** | false | — | Use bid/ask for break detection (L2) |
| **Line Length (bars)** | 25 | 1-200 | How far right the lines extend |
| **Show Region Fill** | true | — | Shaded fill between levels |

### Auto-Scale Settings (ATR Mode)

| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| **ATR Period** | 14 | 5-100 | ATR lookback period |
| **ATR Multiplier** | 0.15 | 0.01-2.0 | Multiplier for ATR-based step |
| **Min Step %** | 0.02 | 0.001-1.0 | Minimum step percentage |
| **Max Step %** | 1.0 | 0.1-5.0 | Maximum step percentage |
| **Show Calculated Step** | true | — | Display current step in stats panel |

### Colors

| Parameter | Default | Description |
|-----------|---------|-------------|
| **Bullish Color** | LimeGreen | Upside lines and labels |
| **Bearish Color** | Red | Downside lines and labels |
| **Region Opacity %** | 12 | Fill zone transparency |
| **Label Color Up** | LimeGreen | Upside label text |
| **Label Color Down** | Red | Downside label text |

### Style

| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| **Line Width** | 1 | 1-5 | Thickness of probability lines |
| **Line Dash Style** | Dot | — | Solid, Dash, Dot, DashDot |
| **Label Font Size** | 10 | 6-24 | Font size for probability labels |
| **Stats Font Size** | 11 | 8-24 | Font size for statistics panel |

### Alerts

| Parameter | Default | Description |
|-----------|---------|-------------|
| **Enable Alerts** | false | Master alert toggle |
| **Alert Ticker** | true | Include ticker in alert |
| **Alert High/Low** | true | Include prior H/L in alert |
| **Alert Bias** | true | Include BULL/BEAR bias |
| **Alert Percentage** | true | Include probability % |
| **Alert Sound** | (default) | Custom sound file path |
| **Alert Rearm Seconds** | 0 | Cooldown between alerts |

---

## Recommended Settings

### For NQ/ES Futures (5-minute chart):

| Setting | Value | Why |
|---------|-------|-----|
| **Step Mode** | Manual | Consistent levels |
| **Manual Percentage Step** | 0.1% - 0.2% | Good spacing for NQ volatility |
| **Number of Lines** | 5 | Full probability ladder |
| **Hide 0% Lines** | true | Cleaner display |
| **Line Length** | 25 | Extends into future space |

### For Forex (Daily chart):

| Setting | Value | Why |
|---------|-------|-----|
| **Step Mode** | Auto | Adapts to pair volatility |
| **ATR Period** | 14 | Standard ATR |
| **Number of Lines** | 5 | Full ladder |

---

## Understanding the Statistics Panel

```
┌─────────────────────┐
│ WIN: 733            │
│ LOSS: 277           │
│ Profitability: 72.57%│
│ ─────────────       │
│ Mode: Auto          │
│ ATR(14): 125.50     │
│ Step: 0.1523%       │
└─────────────────────┘
```

- **WIN**: Times the bias direction hit target (prior high or low)
- **LOSS**: Times the opposite direction hit first
- **Profitability**: Win rate percentage
- **Mode**: Current step calculation mode
- **ATR**: Current ATR value (Auto mode)
- **Step**: Current percentage step being used

---

## How to Use for Trading

### 1. Identify Bias
Look at the **highest probability** on each side:
- If upside shows 72% and downside shows 28% → **Bullish bias**
- If downside shows 65% and upside shows 35% → **Bearish bias**

### 2. Set Targets
Use the probability levels as **realistic targets**:
- 70%+ probability = High confidence target
- 50-70% = Moderate target
- Below 50% = Extended target (less likely)

### 3. Combine with Other Analysis
Best used alongside:
- **OTE Zones** — Enter in OTE when probability favors direction
- **Session Levels** — Probability + session high/low confluence
- **VWAP** — Probability + VWAP direction alignment
- **Liquidity Zones** — High probability toward liquidity

### Example Setup:
```
Prior bar: GREEN (bullish)
Upside probability at prior high: 78%
Downside probability at prior low: 22%

→ Bias: BULLISH
→ Look for long entries
→ Target: Prior high (78% chance of reaching)
→ Stop: Below prior low
```

---

## Snapshot Architecture (Fixed in v2.0)

### The Problem (Before)
- Results would flicker/change mid-bar
- Calculations lagged into next bar
- Did not match TradingView behavior

### The Solution (Now)
- **Snapshot at bar close**: All values (prior high, low, step, probabilities) are captured
- **Locked until next bar**: Results don't change while current bar forms
- **TradingView-style**: Matches original indicator behavior exactly

```
Bar closes → Snapshot values → LOCK → Display
                                 ↓
            No changes until next bar closes!
```

---

## Installation

1. Download `BreakoutProbabilityExpo.cs` from the `Indicators` folder
2. In NinjaTrader 8: **Tools → Import → NinjaScript...**
3. Select the downloaded file
4. The indicator will compile automatically

**Manual installation:**
1. Copy to: `Documents\NinjaTrader 8\bin\Custom\Indicators\`
2. Open NinjaScript Editor
3. Right-click → Compile

---

## Credits

- **Original Concept**: [Zeiierman](https://www.tradingview.com/u/Zeiierman/) on TradingView
- **NinjaTrader Port**: Dollars1bySTEVE
- **Snapshot Fix**: Copilot-assisted architecture update

---

## Troubleshooting

**Probabilities not showing:**
- Ensure enough historical bars have loaded (ATR Period + 2 minimum)
- Check that the indicator compiled without errors

**Results seem wrong:**
- Verify Step Mode and percentage settings
- In Auto mode, check ATR period and multiplier

**Lines extending too far/short:**
- Adjust "Line Length (bars)" parameter

**Performance issues:**
- Disable "Show Region Fill" if chart is laggy
- Reduce "Number of Lines" to 3

---

## Version History

| Version | Changes |
|---------|---------|
| **2.0** | Snapshot architecture — TradingView-style bar-close locking |
| **1.0** | Initial NT8 port from TradingView |

---

**Happy Trading!** 📈

*Ported with ❤️ for the NinjaTrader community*