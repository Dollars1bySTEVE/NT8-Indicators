# TAHelperEnhanced Indicator

## Overview
TAHelperEnhanced is a comprehensive technical analysis dashboard for NinjaTrader 8. It aggregates signals from 32 different technical indicators across three categories (Oscillators, Moving Averages, and Pivot Points) to provide an overall market bias reading.

## Features

### Signal Categories

#### Oscillators (11 Indicators)
| Indicator | Sell Signal | Buy Signal |
|-----------|-------------|------------|
| RSI (14) | < 30 | > 70 |
| Stochastics (14,3,3) | K < 20 | K > 80 |
| CCI (20) | < -100 | > 100 |
| ADX/DMI (14) | DI- > DI+ & ADX > 25 | DI+ > DI- & ADX > 25 |
| Awesome Oscillator | < 0 | > 0 |
| Momentum (10) | < 0 | > 0 |
| MACD Histogram | Declining | Rising |
| Stoch RSI (14) | < 20 | > 80 |
| Williams %R (14) | < -80 | > -20 |
| Bull/Bear Power | SMA < 0 | SMA > 0 |
| Ultimate Oscillator | < 30 | > 70 |

#### Moving Averages (17 Indicators)
| Indicator | Sell Signal | Buy Signal |
|-----------|-------------|------------|
| EMA 5 | Close < EMA | Close > EMA |
| SMA 5 | Close < SMA | Close > SMA |
| EMA 10 | Close < EMA | Close > EMA |
| SMA 10 | Close < SMA | Close > SMA |
| EMA 20 | Close < EMA | Close > EMA |
| SMA 20 | Close < SMA | Close > SMA |
| EMA 30 | Close < EMA | Close > EMA |
| SMA 30 | Close < SMA | Close > SMA |
| EMA 50 | Close < EMA | Close > EMA |
| SMA 50 | Close < SMA | Close > SMA |
| EMA 100 | Close < EMA | Close > EMA |
| SMA 100 | Close < SMA | Close > SMA |
| EMA 200 | Close < EMA | Close > EMA |
| SMA 200 | Close < SMA | Close > SMA |
| Ichimoku Base | Close < Base | Close > Base |
| VWMA 20 | Close < VWMA | Close > VWMA |
| HMA 9 | Close < HMA | Close > HMA |

#### Pivot Points (4 Types)
- **Traditional**: Classic pivot point calculation
- **Fibonacci**: Fibonacci-based levels
- **Woodie**: Open-weighted pivots
- **Camarilla**: Close-based levels

### Market Signal Logic

| Signal | Condition |
|--------|-----------|
| STRONG BUY | All 3 categories show more buys AND buy_point > 50% |
| BUY | Total buys > total sells AND buy_point > 50% |
| NEUTRAL | Neither buy nor sell conditions met |
| SELL | Total sells > total buys AND sell_point > 50% |
| STRONG SELL | All 3 categories show more sells AND sell_point > 50% |

### Visual Dashboard
- Color-coded signal header (Red=Sell, Gray=Neutral, Green=Buy)
- Grid showing signal counts per category
- Percentage bar showing sell/neutral/buy distribution
- Fully customizable position, scale, and opacity

## Installation

1. Download `TAHelperEnhanced.cs` from this repository
2. Open NinjaTrader 8
3. Go to `Tools` > `Import` > `NinjaScript Add-On...`
4. Select the downloaded file and click Open
5. The indicator will compile automatically

## Configuration

### Position Settings
| Parameter | Default | Description |
|-----------|---------|-------------|
| Horizontal Position | 2 (Right) | 0=Left, 1=Center, 2=Right |
| Vertical Position | 2 (Bottom) | 0=Top, 1=Middle, 2=Bottom |
| Table Scale | 1.0 | Size multiplier (0.5 to 3.0) |
| Table Opacity | 90% | Transparency (10% to 100%) |
| Table Margin | 10 | Distance from chart edge |

### Color Settings
| Parameter | Default | Description |
|-----------|---------|-------------|
| Strong Sell Color | Red | STRONG SELL header |
| Sell Color | Salmon | SELL header |
| Neutral Color | Gray | NEUTRAL header |
| Buy Color | LightGreen | BUY header |
| Strong Buy Color | Green | STRONG BUY header |
| Panel Background | Black | Dashboard background |
| Cell Background 1/2 | DimGray/DarkSlateGray | Alternating cell colors |

## Usage Tips

### Day Trading
- Use the summary signal for quick market bias assessment
- Watch for transitions between signal states
- Combine with price action for entry timing

### Swing Trading
- Focus on STRONG BUY/SELL signals for higher conviction
- Use oscillator readings to identify overbought/oversold conditions
- MA alignment indicates trend strength

### Scalping
- Position dashboard in corner for quick reference
- Monitor percentage bar for momentum shifts
- React to signal changes near key levels

## Requirements
- NinjaTrader 8
- Minimum 200 bars of historical data for accurate calculations

## Technical Notes
- Uses SharpDX for GPU-accelerated rendering
- Proper resource management (create/dispose pattern)
- `IsAutoScale = false` prevents chart distortion
- Calculates on bar close for stability

## Version History
- **v5.0** - Enhanced dashboard with SharpDX rendering, added pivot point analysis

## Author
**Dollars1bySTEVE** | Free & Open Source

## License
This indicator is free and open source. Use at your own risk.
