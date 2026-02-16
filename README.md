# 🎯 NT8-Indicators
### Free, Open-Source NinjaTrader 8 Indicators by [@Dollars1bySTEVE](https://github.com/Dollars1bySTEVE)

A growing collection of custom NinjaTrader 8 indicators built for real traders. Clean code, zero errors, fully customizable.

---

## 📦 Indicators

### SmartMoneyStructure (BOS / CHoCH)
**Smart Money Concepts** structure detection — automatically identifies Break of Structure and Change of Character on any timeframe.

![SmartMoneyStructure on MNQ 3-min](https://github.com/Dollars1bySTEVE/NT8-Indicators/blob/main/Screenshots/SmartMoneyStructure_MNQ_3min.png?raw=true)

#### What It Does
| Feature | Description |
|---|---|
| **Break of Structure (BOS)** | Detects when price breaks a previous swing high/low in the direction of the trend — trend continuation |
| **Change of Character (CHoCH)** | Detects when price breaks structure against the trend — potential reversal |
| **Directional Color Coding** | Green = bullish, Red = bearish (fully customizable) |
| **Zone Shading** | Semi-transparent background zones at each structure level |
| **Text Labels** | Clean BOS/CHoCH labels (max 12px, configurable) |
| **Line Stops at Break Candle** | Lines don't project forward — they stop at the candle that caused the break |
| **Adjustable Sensitivity** | Control swing strength and confirmation bars |
| **Real-Time Updates** | Recalculates on each bar close |

#### Settings

**Structure Detection**
| Setting | Range | Default | Description |
|---|---|---|---|
| Swing Strength | 1–20 | 3 | Bars on each side to qualify a swing high/low |
| Confirmation Bars | 1–10 | 1 | Consecutive closes beyond level to confirm break |

**Appearance**
| Setting | Default | Description |
|---|---|---|
| Bullish Color | LimeGreen | Color for bullish BOS/CHoCH |
| Bearish Color | Crimson | Color for bearish BOS/CHoCH |
| Line Width | 2 | Line thickness (1–5) |
| Line Style | Dash | Solid, Dash, Dot, DashDot, DashDotDot |
| Show Labels | True | Toggle BOS/CHoCH text |
| Label Font Size | 11 | Font size 8–12px |

**Zones**
| Setting | Default | Description |
|---|---|---|
| Show Zones | True | Toggle background shading |
| Zone Opacity % | 15 | Transparency (5–50%) |
| Zone Extend Bars | 8 | How far past break candle the zone extends |

---

## 🔧 Installation

1. **Download** `SmartMoneyStructure.cs` from the [`Indicators/`](./Indicators/) folder
2. In NinjaTrader 8: **Tools → Edit NinjaScript → Indicator → New**
3. **Replace** all generated code with the downloaded file contents
4. **Compile** (F5) — should compile with zero errors
5. Right-click chart → **Indicators** → find **SmartMoneyStructure** → **Add**

**Tip:** Start with Swing Strength = 3 and Confirmation Bars = 1 for scalping. Increase both for swing trading on higher timeframes.

---

## ⚠️ NinjaTrader 8 SharpDX Developer Notes

If you're building or modifying NT8 indicators that use custom rendering (`OnRender`), read [`SHARPDX_NOTES.md`](./SHARPDX_NOTES.md) — it covers the #1 compilation issue that trips up almost every NT8 developer.

---

## 📄 License

Free to use, modify, and share. If you find it useful, give it a ⭐!

---

*Built with frustration, coffee, and eventually — zero compile errors.* ☕