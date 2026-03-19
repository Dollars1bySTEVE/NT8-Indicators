# D1ADRv2 Indicator

## Overview
D1ADRv2 (Daily Average Daily Range v2) is an enhanced version of the D1ADR indicator for NinjaTrader 8. This version includes critical fixes for chart scaling issues that caused the chart to squeeze when pivot levels were far from current price.

## What's New in v2
- **Fixed Chart Squeeze Issue**: All horizontal lines and text labels now have `isAutoScale = false`, preventing the chart from scaling to include distant pivot levels (R3, S3, etc.)
- **Improved Performance**: Optimized drawing routines
- **Same Great Features**: All features from the original D1ADR are preserved

## Features
- **ADR High/Low Lines**: Projection lines showing expected daily range from daily open
- **AWR High/Low Lines**: Weekly average range projection lines
- **Daily Pivot Points**: PP, R1, R2, R3, S1, S2, S3 with optional mid-pivots
- **Weekly Pivot Points**: PP, R1, R2, R3, S1, S2, S3 with optional mid-pivots
- **Session Opens**: Asia, London, and New York session open markers (vertical + horizontal lines)
- **Daily Open Line**: Configurable daily open reference line
- **Info Box**: Real-time ADR/AWR statistics in corner of chart
- **Fill Zones**: Optional SharpDX-rendered fill zones for ADR/AWR ranges
- **Fully Customizable**: Colors, line styles, widths, labels all configurable

## Installation
1. Download `D1ADRv2.cs` from this repository
2. Open NinjaTrader 8
3. Go to `Tools` > `Import` > `NinjaScript...`
4. Select the downloaded file and click Open
5. The indicator will compile automatically

## Configuration

### ADR Parameters
| Parameter | Default | Description |
|-----------|---------|-------------|
| ADR Period | 14 | Number of days to average |
| Daily Open Mode | 0 | 0=Session, 1=Midnight, 2=Custom |
| Custom Open Hour/Minute | 0:00 | Custom open time (when mode=2) |

### ADR Lines
| Parameter | Default | Description |
|-----------|---------|-------------|
| Show ADR Lines | true | Display ADR high/low lines |
| ADR High Color | DodgerBlue | Color of ADR high line |
| ADR Low Color | OrangeRed | Color of ADR low line |
| ADR Line Style | Dash | Line style (Solid, Dash, Dot, etc.) |
| ADR Line Width | 2 | Line thickness |

### Weekly Range (AWR)
| Parameter | Default | Description |
|-----------|---------|-------------|
| Show AWR | true | Display weekly range lines |
| AWR Period | 4 | Number of weeks to average |
| AWR High Color | Cyan | Color of AWR high line |
| AWR Low Color | Magenta | Color of AWR low line |

### Session Opens
| Parameter | Default | Description |
|-----------|---------|-------------|
| Show Asia Open | true | Display Asia session open |
| Show London Open | true | Display London session open |
| Show NY Open | true | Display NY session open |
| Asia Open Hour (UTC) | 0 | Asia session open hour in UTC |
| London Open Hour (UTC) | 8 | London session open hour in UTC |
| NY Open Hour (UTC) | 13 | NY session open hour in UTC |

### Daily/Weekly Pivots
| Parameter | Default | Description |
|-----------|---------|-------------|
| Show Daily Pivots | true | Display daily pivot points |
| Show Weekly Pivots | false | Display weekly pivot points |
| Show R3/S3 | true | Include R3 and S3 levels |
| Show Mid-Pivots | false | Show levels between pivots |

## Usage Examples

### Day Trading
- Use ADR lines to identify potential reversal zones when price approaches ADR high/low
- Monitor the info box to see what % of ADR has been used
- Use session opens (Asia, London, NY) as potential support/resistance levels

### Swing Trading
- Use AWR lines for weekly range expectations
- Weekly pivots can identify multi-day support/resistance zones
- Compare daily range to ADR to gauge if the move is extended

## Troubleshooting

### Chart Still Squeezing
- Ensure you're using D1ADRv2, not the original D1ADR
- Check that other indicators on your chart also have IsAutoScale = false
- Right-click the chart > Properties > ensure "Auto scale" is configured correctly

### Pivots Not Showing
- Pivots require at least one completed day/week of data
- Check that "Show Daily Pivots" or "Show Weekly Pivots" is enabled
- Verify the chart has sufficient historical data loaded

### Session Opens Not Appearing
- Session opens use UTC time - verify your UTC offset
- Opens only plot once per day at the specified time
- Ensure "Show Session Vertical Lines" or "Show Session Open Price Lines" is enabled

## Version History
- **v2.0** - Fixed chart squeeze issue with `isAutoScale = false` on all draw objects
- **v1.0** - Initial release

## Author
**Dollars1bySTEVE** | Free & Open Source

## License
This indicator is free and open source. Use at your own risk.
