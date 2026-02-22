# Fair Value Gaps (FVG) Indicator for NinjaTrader 8

**Original Concept:** DGT (dgtrd) — TradingView "Fair Value Gaps by DGT"  
**NinjaTrader 8 Port:** Dollars1bySTEVE

---

## 📖 Overview

Fair Value Gaps (FVGs) are price imbalances created when there's a significant move in one direction, leaving a gap between the wicks of surrounding candles. These gaps often act as support/resistance zones and tend to get "filled" as price returns to rebalance.

This indicator automatically detects and displays FVGs across multiple timeframes with full customization.

---

## ✨ Features

- **Multi-Timeframe Detection** — Current, Higher (HTF), and Lower (LTF) timeframes
- **Fully Customizable Colors** — Separate color schemes per timeframe
- **Zone Expiration** — Age-based, count-based, and session-based expiration
- **Dynamic Zones** — Shows remaining unfilled portion as price fills the gap
- **DGT-Style Labels** — Format: `TF FVG ▲ | XX%` with fill percentage
- **Overlapping Zone Highlighting** — Gold highlight where multiple TF zones overlap
- **Fill Logic Options** — Any Touch, Midpoint, Wick Sweep, Body Beyond
- **Filled Zone Display** — Option to show filled zones with dimmed appearance
- **Debug Mode** — Output window logging for troubleshooting

---

## ⚠️ Important

**Disable "Auto Scale" in chart properties for proper display.**

Right-click chart → Properties → uncheck "Auto scale"

---

## 📥 Installation

1. Download `FairValueGaps.cs`
2. Place in: `Documents\NinjaTrader 8\bin\Custom\Indicators\`
3. Open NinjaTrader 8
4. Go to: Control Center → New → NinjaScript Editor
5. Press **F5** to compile
6. Add to chart: Right-click chart → Indicators → FairValueGaps

---

## ⚙️ Settings

### 1. Detection

| Setting | Default | Description |
|---------|---------|-------------|
| Min Gap Size (Ticks) | 0 | Minimum gap size to detect (0 = any size) |
| Max Historical Gaps | 50 | Maximum gaps to track in memory |
| Fill Logic | Any Touch | When a zone is considered "filled" |
| Auto-Remove Filled | ✅ ON | Remove zones once filled |
| Show Partially Filled | ✅ ON | Show zones that are partially filled |

**Fill Logic Options:**
- **Any Touch** — Zone filled when price touches opposite boundary
- **Midpoint (50%)** — Zone filled when price reaches midpoint
- **Wick Sweep** — Zone filled when wick sweeps through
- **Body Beyond** — Zone filled when candle body closes beyond

### 2. Zone Expiration

| Setting | Default | Description |
|---------|---------|-------------|
| Max Zone Age (Bars) | 0 | Remove zones older than X bars (0 = never) |
| Max Zones Per TF | 50 | Maximum zones to display per timeframe |
| Clear On New Session | ❌ OFF | Clear all zones at session start |

### 3. Multi-Timeframe

| Setting | Default | Description |
|---------|---------|-------------|
| Timeframe Mode | Current Only | Which timeframes to detect |
| Lower TF Selection | Auto | Auto-select or manual LTF |
| Higher TF (Minutes) | 60 | Manual HTF setting |
| Lower TF (Minutes) | 1 | Manual LTF setting |

**Timeframe Modes:**
- **Current Only** — Only detect on chart timeframe
- **With Higher TF** — Current + Higher timeframe
- **With Lower TF** — Current + Lower timeframe  
- **All Timeframes** — Current + HTF + LTF

**Auto Timeframe Selection:**

| Chart TF | Auto HTF | Auto LTF |
|----------|----------|----------|
| 1m | 5m | 1m |
| 5m | 15m | 1m |
| 15m | 60m | 5m |
| 60m | 240m | 15m |
| 240m | Daily | 60m |

### 4. Current TF Colors

| Setting | Default |
|---------|---------|
| Bullish Fill | DodgerBlue |
| Bearish Fill | Tomato |
| Bullish Border | RoyalBlue |
| Bearish Border | Crimson |
| Zone Opacity % | 25 |
| Border Width | 1 |

### 5. Higher TF Colors

| Setting | Default |
|---------|---------|
| HTF Bullish Fill | MediumPurple |
| HTF Bearish Fill | Maroon |
| HTF Bullish Border | BlueViolet |
| HTF Bearish Border | DarkRed |
| HTF Opacity % | 40 |
| HTF Border Width | 2 |

### 6. Lower TF Colors

| Setting | Default |
|---------|---------|
| LTF Bullish Fill | Cyan |
| LTF Bearish Fill | Orange |
| LTF Bullish Border | DarkCyan |
| LTF Bearish Border | DarkOrange |
| LTF Opacity % | 20 |
| LTF Border Width | 1 |

### 7. Midline

| Setting | Default |
|---------|---------|
| Show Midline | ✅ ON |
| Midline Color | Gray |
| Midline Style | Dot |
| Midline Width | 1 |

### 8. Overlaps

| Setting | Default |
|---------|---------|
| Show Overlaps | ✅ ON |
| Overlap Color | Gold |
| Overlap Opacity % | 40 |

### 9. Labels

| Setting | Default |
|---------|---------|
| Show Labels | ✅ ON |
| Show Fill % | ✅ ON |
| Font Size | 9 |
| Label Position | Inside Top |

**Label Positions:**
- Inside Top
- Inside Bottom
- Above Zone
- Below Zone

### 10. Filled Zones

| Setting | Default |
|---------|---------|
| Filled Zone Opacity % | 15 |

### 11. Debug

| Setting | Default |
|---------|---------|
| Enable Debug | ❌ OFF |

---

## 🎨 Visual Guide

### Zone Colors (Default)

| Timeframe | Bullish | Bearish |
|-----------|---------|---------|
| Current (5m) | Blue | Red/Salmon |
| Higher (1H) | Purple | Maroon |
| Lower (1m) | Cyan | Orange |
| Filled | Gray (dimmed) | Gray (dimmed) |
| Overlap | Gold | Gold |

### Label Format

```
5m FVG ▲           ← Active bullish 5-minute FVG
1H FVG ▼ | 45%     ← Bearish 1-hour FVG, 45% filled
5m FVG ▲ | 100% ✓  ← Filled bullish FVG with checkmark
```

---

## 📊 How FVGs Form

### Bullish FVG (Gap Up)

**Detection:** `Low[0] > High[2]`

The low of the current candle is higher than the high of the candle 2 bars ago, creating an unfilled gap.

### Bearish FVG (Gap Down)

**Detection:** `High[0] < Low[2]`

The high of the current candle is lower than the low of the candle 2 bars ago, creating an unfilled gap.

---

## 💡 Trading Tips

1. **HTF Zones are Stronger** — 1H and 4H FVGs tend to be more significant than 1m/5m
2. **Overlapping Zones** — Gold highlighted areas where multiple TF zones overlap are high-probability
3. **Unfilled Zones** — Active (unfilled) zones are more relevant than historical filled zones
4. **Use with Context** — FVGs work best when combined with market structure and trend analysis
5. **Filter Small Gaps** — Set Min Gap Size to 5-10 ticks to reduce noise

---

## 🔧 Recommended Settings

### Scalping (1m-5m charts)
- Timeframe Mode: All Timeframes
- Min Gap Size: 3-5 ticks
- Auto-Remove Filled: ON
- Max Zones Per TF: 20

### Day Trading (5m-15m charts)
- Timeframe Mode: With Higher TF
- Higher TF: 60 minutes
- Min Gap Size: 5-10 ticks
- Auto-Remove Filled: ON
- Max Zones Per TF: 30

### Swing Trading (1H-4H charts)
- Timeframe Mode: Current Only or With Higher TF
- Min Gap Size: 10+ ticks
- Auto-Remove Filled: OFF
- Max Zones Per TF: 50

---

## 🐛 Troubleshooting

### Zones not displaying?
1. Check "Auto Scale" is **disabled** in chart properties
2. Verify Timeframe Mode is set correctly
3. Check Max Zone Age isn't filtering zones
4. Try increasing Max Zones Per TF

### Too many zones?
1. Increase Min Gap Size (Ticks)
2. Enable Auto-Remove Filled
3. Reduce Max Zones Per TF
4. Set Max Zone Age to limit old zones

### Labels cluttered?
1. Set Show Labels to OFF
2. Reduce Max Zones Per TF
3. Increase Min Gap Size

### Debug mode
Enable "Enable Debug" to see zone detection info in the Output window (Ctrl+O)

---

## 📝 Changelog

### v1.0.0
- Initial release
- Multi-timeframe FVG detection
- Customizable colors per timeframe
- Zone expiration settings
- Fill logic options
- Overlap highlighting
- DGT-style labels
- Filled zone rendering with original boundaries

---

## 📜 License

Free to use and modify. Credit to DGT for the original TradingView concept.

---

## 🙏 Credits

- **Original Concept:** DGT (dgtrd) — TradingView
- **NinjaTrader 8 Port:** Dollars1bySTEVE