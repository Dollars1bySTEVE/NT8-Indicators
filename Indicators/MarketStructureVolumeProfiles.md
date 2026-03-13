# Market Structure Volume Profiles

**Original Concept:** [KioseffTrading](https://www.tradingview.com/u/KioseffTrading/) on TradingView  
**Original Script:** [Market Structure Volume Profiles](https://www.tradingview.com/v/shy8kACw/)  
**NinjaTrader 8 Port:** Dollars1bySTEVE

---

## 📖 Overview

**Market Structure Volume Profiles** merges two powerful analytical frameworks into one indicator:

1. **ICT / Smart Money Concepts (SMC) Market Structure** — Detects Break of Structure (BoS) and Change of Character (CHoCH) events to objectively map trend continuation vs. reversal.
2. **Volume Profile per Swing** — Builds a volume histogram for each structural swing, revealing exactly where market participants traded the most during each move.

The combination lets you answer the critical question: *"Not only did price move here — but how did volume support or oppose that move?"*

---

## ✨ Features

- **Break of Structure (BoS)** — Confirms trend continuation when price breaks a key swing high or low with volume context
- **Change of Character (CHoCH)** — Early warning of trend reversal at key structural levels
- **Volume Profile per Swing** — Histogram of buy/sell volume for each structural move
- **Point of Control (POC)** — Price level with the highest volume in each swing
- **Value Area (VAH / VAL)** — Price range containing a configurable % of swing volume (default 70%)
- **Split / Stacked Profile Modes** — View buy vs. sell volume side-by-side or as a unified bar
- **Cumulative Volume Delta (CVD)** — Tracks net buy/sell pressure, resetting on configurable structure events
- **Fully Customizable Colors & Opacity** — Separate color controls for bullish/bearish structure and buy/sell volume
- **Works on Any Timeframe** — Scalping (1m), day trading (5m–15m), swing trading (1H–4H), or position (Daily)

---

## ⚠️ Important Notes

- **Disable "Auto Scale"** in chart properties for best profile display.  
  Right-click chart → Properties → uncheck *Auto scale*
- The CVD plot appears as a **bar chart below the price panel**. Right-click the CVD panel and select *Move to new panel* if needed.
- For highest CVD accuracy on live data, use **tick replay** enabled sessions.
- Historical CVD uses bar direction (close vs. open) as a buy/sell proxy — this is an approximation.

---

## 📥 Installation

1. Download `MarketStructureVolumeProfiles.cs`
2. Place in: `Documents\NinjaTrader 8\bin\Custom\Indicators\`
3. Open NinjaTrader 8
4. Go to: **Control Center → New → NinjaScript Editor**
5. Press **F5** to compile
6. Add to chart: Right-click chart → **Indicators → MarketStructureVolumeProfiles**

---

## ⚙️ Settings Reference

### 1. Structure Detection

| Setting | Default | Description |
|---------|---------|-------------|
| Swing Strength | 3 | Number of bars on each side required to confirm a swing high/low |
| Confirmation Bars | 1 | Consecutive closes beyond the level needed to confirm the break |
| Show BoS | ✅ ON | Display Break of Structure lines and labels |
| Show CHoCH | ✅ ON | Display Change of Character lines and labels |

**Swing Strength Guide:**
- `2–3` — More signals, faster response (scalping / 1m–5m)
- `4–5` — Balanced (day trading / 5m–15m)
- `6–10` — Fewer, higher-quality signals (swing / 1H+)

### 2. Appearance

| Setting | Default | Description |
|---------|---------|-------------|
| Bullish Color | LimeGreen | Color for bullish BoS/CHoCH lines |
| Bearish Color | Crimson | Color for bearish BoS/CHoCH lines |
| Line Width | 2 | Thickness of structure lines (1–5) |
| Line Style | Dash | Dash style (Solid, Dash, Dot, DashDot) |
| Show Labels | ✅ ON | Display "BoS" / "CHoCH" text on structure lines |
| Label Font Size | 10 | Font size for labels (8–14 px) |

### 3. Volume Profile

| Setting | Default | Description |
|---------|---------|-------------|
| Show Volume Profile | ✅ ON | Render volume profile histograms |
| Profile Levels | 100 | Number of price buckets per profile (5–500) |
| Profile Width % | 25 | Width as a % of visible chart width |
| Profile Type | Split | Split = buy/sell side-by-side; Stacked = single bar |
| Profile Opacity % | 60 | Transparency of volume bars (10–90%) |
| Buy Volume Color | DodgerBlue | Color for buying volume bars |
| Sell Volume Color | Tomato | Color for selling volume bars |
| Max Profiles | 10 | Maximum recent swing profiles shown |

**Profile Type:**
- **Split** — Buy volume anchors to the right, sell volume extends left. Best for seeing delta imbalance at each level.
- **Stacked** — Single bar colored by swing direction. Best for a clean, simple volume shape.

### 4. POC & Value Area

| Setting | Default | Description |
|---------|---------|-------------|
| Show POC | ✅ ON | Draw Point of Control (highest volume level) line |
| POC Color | Yellow | Color of the POC horizontal line |
| Show Value Area | ✅ ON | Draw VAH and VAL boundary lines |
| VA Color | DimGray | Color of Value Area High/Low lines |
| Value Area % | 70 | Percentage of volume defining the value area |

### 5. CVD (Cumulative Volume Delta)

| Setting | Default | Description |
|---------|---------|-------------|
| Show CVD | ✅ ON | Display CVD bar chart |
| CVD Reset Mode | CHoCH | When to reset the CVD cumulative baseline |
| CVD Positive Color | LimeGreen | Color when net delta is positive (buying pressure) |
| CVD Negative Color | Crimson | Color when net delta is negative (selling pressure) |

**CVD Reset Modes:**
- **CHoCH** — Resets only on trend reversals (Change of Character)
- **BoS+CHoCH** — Resets on every structure break
- **Day** — Resets at the start of each trading day
- **Week** — Resets at the start of each trading week

---

## 📊 How Market Structure Is Detected

### Break of Structure (BoS)

A **BoS** occurs when price closes beyond the most recent swing high (bullish) or swing low (bearish) **in the direction of the current trend** — confirming trend continuation.

```
Bullish BoS:  Current trend = Bullish  →  Close > Last Swing High
Bearish BoS:  Current trend = Bearish  →  Close < Last Swing Low
```

### Change of Character (CHoCH)

A **CHoCH** occurs when price closes beyond a swing level **against** the current trend — the first warning sign of a potential reversal.

```
Bullish CHoCH:  Current trend = Bearish  →  Close > Last Swing High  (potential reversal up)
Bearish CHoCH:  Current trend = Bullish  →  Close < Last Swing Low   (potential reversal down)
```

---

## 📈 Volume Profile Construction

For each structure break, the indicator builds a volume profile spanning from the **swing point bar** to the **break bar**:

1. **Price range** is divided into `Profile Levels` equal price buckets
2. Each bar's volume is distributed across the price levels it spans (proportional to bar range overlap)
3. Up-close bars contribute **Buy Volume**; down-close bars contribute **Sell Volume**
4. **POC** = level with the highest total volume
5. **Value Area** = levels around the POC that contain the target % of total volume (expanding outward from POC)

---

## 💡 Trading Applications

### Reading BoS + Volume Profile Together

| Signal | Volume Interpretation |
|--------|-----------------------|
| Bullish BoS | High buy volume at/above POC → strong trend continuation |
| Bullish BoS | High sell volume at POC → potential exhaustion, watch for reversal |
| Bearish CHoCH | POC in upper portion of profile → weak bearish structure |
| Bearish CHoCH | Increasing sell CVD → confirming bearish momentum |

### Futures Index Use Cases (ES, NQ, CL, GC)

**Scalping (1m–5m):**
- Use Swing Strength 2–3, Profile Levels 50
- Look for CHoCH at key HTF levels as entry trigger
- CVD Reset: BoS+CHoCH for frequent resets

**Day Trading (5m–15m):**
- Swing Strength 3–5, Profile Levels 100
- Trade BoS pullbacks into the Value Area of the prior swing profile
- Use POC as a magnetic target level

**Swing Trading (1H–4H):**
- Swing Strength 5–8, Profile Levels 150
- CHoCH on Daily/4H as primary signal
- Volume profile POC as trade target or stop reference

---

## 🎨 Visual Guide

```
  High ─────┐
            │  ┌─────────────────────────┐  ← VAH (gray line)
            │  │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒░░░░░│
  POC ──────│──│█████████████████████████│  ← POC (yellow line, max volume)
            │  │▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒░░░░░░░░│
            │  └─────────────────────────┘  ← VAL (gray line)
            │  ┌─────┐
            │  │░░░░░│
  Low ──────┘  └─────┘
                Blue = Buy Vol    Red = Sell Vol
                ■ BoS ──────────────────────── (green dashed line)
```

---

## 🔧 Recommended Settings

### ES / NQ Futures – 5m Day Trading
```
Swing Strength:    3
Confirmation Bars: 1
Profile Levels:    100
Profile Width %:   20
Value Area %:      70
CVD Reset:         CHoCH
```

### CL Futures – 1m Scalping
```
Swing Strength:    2
Confirmation Bars: 1
Profile Levels:    50
Profile Width %:   15
Value Area %:      68
CVD Reset:         BoS+CHoCH
```

### NQ Futures – 1H Swing
```
Swing Strength:    5
Confirmation Bars: 2
Profile Levels:    150
Profile Width %:   30
Value Area %:      70
CVD Reset:         CHoCH
```

---

## 🐛 Troubleshooting

### Profiles not displaying?
- Confirm **Auto Scale is OFF** in chart properties
- Check `Show Volume Profile` is enabled
- Verify the indicator has at least `SwingStrength * 2 + 2` bars loaded

### Too many or too few signals?
- Increase **Swing Strength** to reduce signal frequency
- Increase **Confirmation Bars** to require stronger breakouts

### Profiles look too wide/narrow?
- Adjust **Profile Width %** (5–50%)
- Reduce **Profile Levels** if bars are very small

### CVD not visible?
- Ensure **Show CVD** is ON
- The CVD plot may be on the price panel — drag it to a separate sub-panel by right-clicking

### Labels overlapping?
- Turn off **Show Labels** or reduce **Label Font Size**

---

## 📝 Changelog

### v1.0.0
- Initial NinjaTrader 8 port
- BoS/CHoCH detection via configurable swing strength
- Volume profile per swing with Split/Stacked modes
- POC and Value Area (VAH/VAL) with configurable % threshold
- Cumulative Volume Delta plot with configurable reset modes
- Full color and opacity customization
- SharpDX rendering for smooth, high-performance profiles

---

## 📜 License & Credits

Free to use and modify for personal trading.  
Credit to **KioseffTrading** for the original TradingView concept.

### Credits
- **Original Concept:** [KioseffTrading](https://www.tradingview.com/u/KioseffTrading/) on TradingView
- **Original Script:** [Market Structure Volume Profiles](https://www.tradingview.com/v/shy8kACw/)
- **NinjaTrader 8 Port:** Dollars1bySTEVE
