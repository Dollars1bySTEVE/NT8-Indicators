# Liquidity Heatmap (NinjaTrader 8)

`LiquidityHeatmap` is a NinjaTrader 8 order-book overlay indicator that renders a Bookmap-style depth heatmap from live DOM updates, with wall highlighting/labels, bid/ask step lines, aggressive trade print dots + hover tooltip, and a right-side DOM ladder text view.

## Screenshot Placeholder

Add chart screenshots here after loading the indicator on ES/NQ in live data or Market Replay.

## Install Instructions

1. Copy `LiquidityHeatmap.cs` to:
   - `Documents\NinjaTrader 8\bin\Custom\Indicators\`
2. Open NinjaTrader 8.
3. Open **New → NinjaScript Editor**.
4. Press **F5** to compile (or right-click the indicator and choose **Compile**).
5. Add **Liquidity Heatmap** to your chart from the Indicators dialog.

## Requirements

- NinjaTrader 8
- Level 2 / Market Depth data subscription
- Instruments that publish depth (futures such as ES/NQ usually do; many retail stock feeds do not)

## Usage Notes

- The heatmap starts filling from the right edge when the indicator is loaded.
- For historical depth study, use NinjaTrader 8 **Market Replay** sessions that include depth.
- Works on any timeframe/instrument with available depth data.

## Property Groups

- **Heatmap**: master toggle, visible price levels, snapshot timing/history, thresholds, background.
- **Walls**: wall detection toggle, size threshold, wall color.
- **Trade Prints (Dots)**: min size filter, radius range, buy/sell colors.
- **Bid/Ask Line**: step-line display, forward extension, and right-edge bid/ask labels.
- **DOM Ladder**: right-side bid/ask ladder text colors.
- **Display**: branding watermark toggle.

## Independence / Attribution

This is an independent open-source clone inspired by Bookmap-style heatmap workflows, and is not affiliated with or endorsed by Tholvi/TholviTrader, Bookmap, or NinjaTrader LLC.

## License Note

No root `LICENSE` file was found in this repository at implementation time; per issue request, this indicator documentation is marked as MIT-style.
