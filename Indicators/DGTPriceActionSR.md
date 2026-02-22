# DGT Price Action S/R - NinjaTrader 8

A comprehensive multi-feature technical analysis indicator for NinjaTrader 8, combining volume profile, support/resistance levels, spike detection, high volatility zones, supply/demand zones, and volume-based bar coloring.

---

## Credits & Attribution

| Role | Credit |
|------|--------|
| **Original Indicator** | [dgtrd](https://www.tradingview.com/u/dgtrd/) on TradingView |
| **Original Script** | [Price Action - Support & Resistance by DGT](https://www.tradingview.com/script/Z1byay68-Price-Action-Support-Resistance-by-DGT/) |
| **NinjaTrader 8 Port** | [Dollars1bySTEVE](https://github.com/Dollars1bySTEVE) |

> This indicator is a port of dgtrd's open-source TradingView indicator. All credit for the original concept and logic goes to dgtrd. This port adapts the indicator for use in NinjaTrader 8 using Level 1 data.

---

## Overview

This indicator combines **6 powerful features** into a single overlay:

1. **S&R Targets** - Dynamic support/resistance levels
2. **Volume Spike Detection** - Extreme volume identification
3. **High Volatility Detection** - ATR-based volatility zones
4. **Volume Profile** - POC, VAH, VAL with HVN/LVN visualization
5. **Supply & Demand Zones** - Based on low volume nodes
6. **Bar Coloring** - Volume-weighted candle coloring

---

## Features

### 1. S&R Targets (Support & Resistance)

Identifies support and resistance levels using either **Volume** or **Price** patterns.

**Volume Mode:**
- Requires 3 consecutive bullish/bearish bars
- Volume must be above the SMA
- Volume must be increasing

**Price Mode:**
- Requires 3 consecutive bullish/bearish bars
- Requires 3 consecutive closes in the same direction

| Parameter | Default | Description |
|-----------|---------|-------------|
| Enable | `true` | Toggle S&R detection |
| Source | `Volume` | Volume or Price based detection |
| Lookback | `360` | Number of bars to analyze |
| Max Lines | `10` | Maximum S/R lines displayed |
| Dedup Ticks | `10` | Minimum tick distance between levels |
| Extend Full | `true` | Extend lines to chart edge |
| Labels | `true` | Show price labels |

---

### 2. Volume Spike Detection

Identifies bars where volume exceeds a threshold multiplied by the volume SMA.

| Parameter | Default | Description |
|-----------|---------|-------------|
| Enable | `true` | Toggle spike detection |
| Threshold | `4.669` | Multiplier for volume SMA |
| Display | `Lines` | Lines, Zone, or Both |
| Max Zones | `8` | Maximum zones displayed |
| Dedup Ticks | `8` | Minimum distance between zones |
| Markers | `true` | Show diamond markers |
| Labels | `true` | Show price labels |

---

### 3. High Volatility Detection

Identifies bars where the price range exceeds ATR × multiplier.

| Parameter | Default | Description |
|-----------|---------|-------------|
| Enable | `true` | Toggle HV detection |
| ATR Length | `11` | ATR calculation period |
| ATR Mult | `2.718` | ATR multiplier (Euler's number) |
| Display | `Both` | Lines, Zone, or Both |
| Max Zones | `8` | Maximum zones displayed |
| Dedup Ticks | `8` | Minimum distance between zones |
| Markers | `true` | Show diamond markers |
| Labels | `true` | Show price labels |

---

### 4. Volume Profile

Displays a volume-at-price histogram with key levels.

| Parameter | Default | Description |
|-----------|---------|-------------|
| Enable | `true` | Toggle volume profile |
| Mode | `VisibleRange` | VisibleRange or FixedRange |
| Fixed Bars | `360` | Bars for fixed range mode |
| Rows | `100` | Number of price levels |
| VA Pct | `68` | Value Area percentage |
| Show POC | `true` | Point of Control line |
| Show VAH | `true` | Value Area High line |
| Show VAL | `true` | Value Area Low line |
| Width % | `30` | Profile width as % of margin |
| Labels | `true` | Show price labels |

**Volume Node Colors:**
| Node Type | Condition | Default Color |
|-----------|-----------|---------------|
| HVN (High Volume) | >80% of max | Dark Orange |
| AVN (Average Volume) | 20-80% of max | Gray |
| LVN (Low Volume) | <20% of max | Dim Gray |

---

### 5. Supply & Demand Zones

Highlights low volume nodes as potential supply/demand zones.

| Parameter | Default | Description |
|-----------|---------|-------------|
| Enable | `true` | Toggle S/D zones |
| Threshold | `10` | % of max volume for LVN |
| Supply Color | `Red` | Zones above POC |
| Demand Color | `Teal` | Zones below POC |
| Opacity | `10` | Zone transparency |

---

### 6. Bar Coloring

Colors bars based on volume relative to the SMA using Fibonacci ratios.

| Parameter | Default | Description |
|-----------|---------|-------------|
| Enable | `true` | Toggle bar coloring |
| Hi Threshold | `1.618` | High volume multiplier |
| Lo Threshold | `0.618` | Low volume multiplier |
| Vol SMA | `89` | Volume SMA period |

**Color Scheme:**
| Volume Level | Bullish Bar | Bearish Bar |
|--------------|-------------|-------------|
| High (>1.618×) | Dark Green | Dark Red |
| Low (<0.618×) | Aquamarine | Orange |
| Normal | Default | Default |

---

## Alerts

The indicator provides real-time alerts for:
- **Volume Spikes** - Plays `Alert1.wav`
- **High Volatility** - Plays `Alert2.wav`

Alerts only fire in real-time mode (not on historical data).

---

## Installation

1. **Download** the `DGTPriceActionSR.cs` file

2. **Import into NinjaTrader 8:**
   - Open NinjaTrader 8
   - Go to `Tools` → `Import` → `NinjaScript Add-On...`
   - Select the downloaded `.cs` file
   - Click `Import`

3. **Alternative Manual Installation:**
   - Navigate to: `Documents\NinjaTrader 8\bin\Custom\Indicators\`
   - Copy `DGTPriceActionSR.cs` into this folder
   - In NinjaTrader, open the NinjaScript Editor
   - Press `F5` to compile

4. **Add to Chart:**
   - Right-click on your chart
   - Select `Indicators...`
   - Find `DGT Price Action SR` in the list
   - Configure parameters and click `OK`

---

## Requirements

| Requirement | Details |
|-------------|---------|
| Platform | NinjaTrader 8 |
| Data | Level 1 (standard market data) |
| Volume | Requires actual volume data |
| Best For | Futures, Stocks (instruments with real volume) |

> **Note:** This indicator works best with instruments that have real volume data. Forex pairs using tick volume may produce less reliable results.

---

## Performance Tips

- **Disable unused modules** to reduce CPU load
- **Increase Dedup values** to reduce visual clutter
- **Use Fixed Range** for volume profile on slower systems
- **Reduce VP Rows** (e.g., 50 instead of 100) for faster rendering

---

## Default Settings Summary

| Module | Key Defaults |
|--------|--------------|
| S&R Targets | Volume source, 360 lookback, 10 max lines |
| Volume Spike | 4.669× threshold, Lines display |
| High Volatility | ATR(11) × 2.718 |
| Volume Profile | Visible Range, 100 rows, 68% VA |
| Supply/Demand | 10% threshold |
| Bar Coloring | 1.618/0.618 Fibonacci thresholds, 89 SMA |

---

## License

This indicator is provided free for personal use. 

**Please maintain attribution to the original author (dgtrd) if sharing or modifying this code.**

---

## Links

- [Original TradingView Indicator](https://www.tradingview.com/script/Z1byay68-Price-Action-Support-Resistance-by-DGT/)
- [dgtrd on TradingView](https://www.tradingview.com/u/dgtrd/)
- [NT8-Indicators Repository](https://github.com/Dollars1bySTEVE/NT8-Indicators)
