# 🎯 NT8-Indicators
### Free, Open-Source NinjaTrader 8 Indicators by [@Dollars1bySTEVE](https://github.com/Dollars1bySTEVE)

A growing collection of custom NinjaTrader 8 indicators built for real traders. Clean code, zero compile errors, fully customizable.

**All indicators tested and verified on MNQ futures, 3-minute charts.**

---

## 📦 Indicators

| # | Indicator | Type | Description |
|---|---|---|---|
| 1 | [SmartMoneyStructure](#1-smartmoneystructure-bos--choch) | Structure | BOS & CHoCH detection with zones and labels |
| 2 | [BreakoutLiquiditySweep](#2-breakoutliquiditysweep) | Breakout | EMA breakout with liquidity sweep & absorption detection |
| 3 | [TimeZoneColors](#3-timezonecolors) | Session | Tokyo / London / New York session background coloring |

---

## 1. SmartMoneyStructure (BOS / CHoCH)
**Smart Money Concepts** structure detection — automatically identifies Break of Structure and Change of Character on any timeframe.

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

**Tip:** Start with Swing Strength = 3, Confirmation Bars = 1 for scalping. Increase both for swing trading.

---

## 2. BreakoutLiquiditySweep
**EMA crossover breakout detection** with built-in **liquidity sweep** and **absorption zone** identification.

*Original concept by Alighten, enhanced by Dollars1bySTEVE.*

#### What It Does
| Feature | Description |
|---|---|
| **EMA Breakout Detection** | Flags when price crosses above the fast EMA (bullish) or below the slow EMA (bearish) |
| **Liquidity Sweep Detection** | When a breakout occurs during high-volume absorption → double arrow = smart money sweep |
| **Absorption Zones** | High volume + tight range = orders being absorbed. Marked with yellow dots |
| **Visual Signal Hierarchy** | Single arrow = normal breakout, Double arrow = liquidity sweep event |
| **Multi-Timeframe Ready** | Configurable HTF data series for broader context |
| **Cached Indicators** | EMA and SMA instances cached in State.DataLoaded for optimal performance |

#### Settings
| Setting | Range | Default | Description |
|---|---|---|---|
| Timeframe Minutes | 1–10000 | 60 | Higher timeframe period for multi-TF context |
| EMA Period 1 (Short) | 1–500 | 14 | Fast EMA — bullish breakout trigger |
| EMA Period 2 (Long) | 1–500 | 21 | Slow EMA — bearish breakout trigger |
| Volume Lookback | 1–100 | 20 | Bars to average for volume comparison |
| Volume Threshold | 1.0–5.0 | 1.5 | Multiplier above average volume to flag high volume |
| Detect Absorption | On/Off | True | Toggle absorption zone detection |

#### Signal Guide
| Signal | Visual | Meaning |
|---|---|---|
| Normal Breakout Up | Single green ▲ | Price crossed above fast EMA |
| Liquidity Sweep Up | Double green ▲▲ | Bullish breakout + absorption (smart money) |
| Normal Breakout Down | Single red ▼ | Price crossed below slow EMA |
| Liquidity Sweep Down | Double red ▼▼ | Bearish breakout + absorption (smart money) |
| Absorption Zone | Yellow dot ● | High volume + tight range (orders being absorbed) |

---

## 3. TimeZoneColors
**Session background coloring** for up to three trading sessions with **adjustable opacity** — perfect for identifying Tokyo, London, and New York sessions at a glance.

#### What It Does
| Feature | Description |
|---|---|
| **3 Custom Time Zones** | Define start/end hours for up to three sessions |
| **Adjustable Opacity** | Slider from 5 (barely visible) to 255 (full solid) — default 40 |
| **Overlap Blending** | When sessions overlap, colors blend automatically |
| **Session Name Labels** | Current session name displayed in top-right corner |
| **Session Alerts** | Optional audio alerts when sessions begin/end |
| **Color All Panels** | Toggle to color just the price panel or all chart panels |
| **Wraps Midnight** | Handles sessions that cross midnight (e.g., Tokyo 19:00–04:00) |

#### Settings

**Configuration**
| Setting | Range | Default | Description |
|---|---|---|---|
| Zone Opacity | 5–255 | 40 | Background transparency |
| Color All Panels | On/Off | Off | Color all panels or just price panel |
| Alert on Begin/End | On/Off | On | Audio alert at session transitions |

**Default Sessions (fully customizable)**
| Session | Start | End | Color |
|---|---|---|---|
| Tokyo | 19:00 | 04:00 | Pink |
| London | 03:00 | 12:00 | Beige |
| New York | 08:00 | 16:00 | LightGreen |

#### Opacity Guide
| Value | Effect |
|---|---|
| 5–15 | Barely visible hint |
| 25–40 | Subtle, great for dark themes ← **default** |
| 60–100 | Clearly visible but still readable |
| 150–200 | Strong presence |
| 255 | Full solid (original behavior) |

---

## 🔧 Installation (All Indicators)

1. **Download** any `.cs` file from the [`Indicators/`](./Indicators/) folder
2. In NinjaTrader 8: **Tools → Edit NinjaScript → Indicator → New**
3. **Replace** all generated code with the downloaded file contents
4. **Compile** (F5) — all indicators compile with zero errors
5. Right-click chart → **Indicators** → find the indicator name → **Add**

---

## ⚠️ NinjaTrader 8 SharpDX Developer Notes

If you're building or modifying NT8 indicators that use custom rendering (OnRender), here are the **golden rules** that prevent the #1 compilation issue:

| Rule | Do This | Not This |
|---|---|---|
| **1. Never import SharpDX namespaces at top** | Fully qualify: `SharpDX.Direct2D1.SolidColorBrush` | ~~`using SharpDX.Direct2D1;`~~ |
| **2. Use the NT8 singleton factory** | `NinjaTrader.Core.Globals.DirectWriteFactory` | ~~`new SharpDX.DirectWrite.Factory()`~~ |
| **3. Explicitly type WPF Brush properties** | `System.Windows.Media.Brush BullishColor` | ~~`Brush BullishColor`~~ |

These rules prevent CS0104 ambiguous reference errors between SharpDX and System.Windows.Media types (Brush, SolidColorBrush, FontWeight, FontStyle, FontStretch, TextAlignment).

---

## 📄 License

Free to use, modify, and share. If you find it useful, give it a ⭐!

---

*Built with frustration, coffee, and eventually — zero compile errors.* ☕