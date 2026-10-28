# LiquidityPulse — Level 2 Liquidity and Reference Levels for NinjaTrader 8

**Version:** v0.1 — Foundation

**Type:** On-chart overlay indicator (SharpDX)
**Related specification:** [Issue #196](https://github.com/Dollars1bySTEVE/NT8-Indicators/issues/196)

---

## Overview

LiquidityPulse combines the standard NinjaTrader Level 2 book and trade feed with session/day/week reference levels. It displays persistent, large resting bid and ask liquidity, tracks aggressive trade volume at those levels, and reports a session delta and adaptive wall threshold in a small HUD.

This release is the foundation only. It does not label pulls, fills, absorption, sweeps, failed runs, or issue alerts. Wall disappearance is recorded as inactive and left for the v0.2 event classifier.

## Requirements

- NinjaTrader 8 and a live or Playback connection supplying Level 2 market depth and trades.
- A chart using **CME US Index Futures ETH** trading hours is recommended.
- `OnMarketDepth` has no historical depth series; book walls, tape totals, and feed status begin when the indicator receives data.
- Wall visibility reflects the provider's aggregated Market-By-Price book, usually the top ~10 levels. Order counts are not available.
- This indicator is not a prediction or backtesting tool.

## Installation

1. Copy `LiquidityPulse.cs` to `Documents\NinjaTrader 8\bin\Custom\Indicators\`.
2. Open **Control Center → New → NinjaScript Editor** and press **F5**.
3. Add **LiquidityPulse** from the chart's Indicators list.
4. Confirm the selected connection and instrument provide Level 2 data.

`SharpEngineAllInOne.cs` remains unchanged and may be run alongside LiquidityPulse for comparison.

## Settings

### Liquidity walls

| Setting | Default | Description |
|---|---:|---|
| Use Adaptive Threshold | On | Uses a rolling median of sampled average book size. |
| Wall Multiplier | 3.0 | Multiplier applied to the adaptive median. |
| Adaptive Window Minutes | 5 | Time covered by the rolling sample window. |
| Minimum Lot Floor | 8 | Threshold during warmup and the minimum threshold. |
| Maximum Walls Per Side | 5 | Largest visible active walls retained per side. |
| Walls From Last Bar | On | Starts wall lines at the last bar and extends them right. |
| Scale Walls By Size | On | Scales line width and opacity by size relative to threshold. |
| Minimum Wall Label Spacing | 12 px | Vertical separation used to avoid label overlap. |
| Wall Minimum Age Seconds | 3 | Suppresses short-lived walls. |
| Feed Stale Seconds | 5 | Marks the HUD stale and greys the walls after this long without depth. |
| Show Walls / Show Wall Labels | On / On | Independent wall and label display switches. |
| Ask / Bid / Stale Wall Color | Crimson / Lime Green / Gray | Independently serialized colors. |
| Ask / Bid / Stale Wall Opacity | 90 / 90 / 65% | Independent alpha settings. |
| Wall Minimum / Maximum Width | 1 / 6 px | Width range for size-scaled lines. |
| Wall Dash Style / Label Font Size | Solid / 10 | Line style and label type size. |

The adaptive baseline samples the average size of available ask and bid rows every two event-time seconds. It keeps the median for the selected window and uses `max(Minimum Lot Floor, ceil(median × Wall Multiplier))`. The baseline and samples reset when the phase changes.

### HUD and bias shading

| Setting | Default | Description |
|---|---:|---|
| Show HUD | On | Displays session phase, baseline, wall threshold, aggressive delta, and feed health. |
| HUD Corner | Bottom Left | Selects one of the four chart corners. |
| HUD Font Size | 12 | HUD text size. |
| HUD Text / Background Color | White Smoke / Black | Independently serialized colors. |
| HUD Text / Background Opacity | 100 / 65% | Independent opacity controls. |
| Enable Bias Shading | Off | Enables optional Swing-based context shading. |
| Bull / Bear Shade Color | Forest Green / Crimson | Independently serialized colors. |
| Shade Opacity | 5% | Opacity for both bias shades. |
| Shading Mode | Full Height | Full panel, top band, or bottom band. |
| HTF Bars Period / Value | Minute / 60 | Higher timeframe used for Swing context. |
| Confirm Bars Period / Value | Tick / 80 | Faster confirming Swing series. |
| Swing Strength | 3 | Swing look-back/look-forward strength. |

If the HTF swing is not formed yet, the shading uses the confirmation series alone. This avoids treating a zero-valued HTF swing as a real price.

### Reference levels and profiles

Day, week, previous-session, and current-session levels preserve individual show switches and colors for Open, High, and Low. Session and weekly Volume Profile retain separate POC, VAH, VAL, and VWAP switches and colors.

| Setting | Default | Description |
|---|---:|---|
| Reference Line Width / Style | 1 / Dash | Appearance of day/week/session OHL lines. |
| Reference Opacity | 100% | Shared opacity for reference levels. |
| Show Level Labels / Label Font Size | On / 10 | Reference/profile labels and text size. |
| Value Area Percent | 70 | Profile value-area coverage. |
| Profile Line Width / Style | 2 / Solid | Appearance of profile and VWAP lines. |
| Profile Opacity | 100% | Shared opacity for profile and VWAP lines. |

Every brush color is stored using NinjaTrader's brush serialization properties. All distances and sizes used by future event logic are in instrument ticks.

## How it works

1. **Book:** market-depth Add/Update/Remove operations modify positional ask and bid row lists using `e.Position`; reset events clear the lists. Book updates are performed under a single lock.
2. **Tape:** bid and ask quotes are cached. Last trades at or above the ask add aggressive-buy volume; trades at or below the bid add aggressive-sell volume. Trades between the quotes are unclassified. Classified volume is credited to active walls at the matching price.
3. **Wall lifecycle:** each threshold-qualified price tracks first-seen time, peak/current size, trade volume, and active state. A wall is rendered only after its minimum age. Inactive classification is intentionally deferred to v0.2.
4. **Clock and phases:** depth/trade event timestamps drive session phase, wall age, sampling, and feed-stale logic. Before the first event, the local clock is used only as a fallback. Eastern time is resolved with the DST-aware `Eastern Standard Time` zone. Phases are ETH Overnight (18:00–03:00), ETH London (03:00–09:30), RTH Open (09:30–10:00), RTH (10:00–16:00), and Post-Close (16:00–18:00). The chart's `SessionIterator` supplies its session end when available, including early closes; Post-Close clears the book.
5. **Levels:** previous/current day, week, and trading session OH/L are updated from primary bars. Session and weekly profile/VWAP calculations replace the current developing-bar contribution before adding the updated value, preventing cumulative-volume overcounting with `Calculate.OnEachTick`.
6. **Rendering:** wall refresh/baseline work is throttled to approximately 150 ms and chart invalidation to at most about 13 times per second. Rendering snapshots wall state under the book lock, filters to the visible price scale, selects the largest walls, and avoids nearby labels. SharpDX brushes, text formats, and stroke styles are reused and disposed on render-target changes and termination.

## Recommended charts

- **Primary:** 1-minute Volumetric chart for levels and context.
- **Scalping:** NQ 500–1000 tick chart.
- Use the **CME US Index Futures ETH** trading-hours template.
- MNQ has a thinner independent book; consider observing NQ liquidity while executing MNQ.

## Known limits

- Only the provider's visible top ~10 depth levels are available; this is aggregated MBP data, not per-order MBO.
- There is no historical depth replay on ordinary chart history. Walls and tape start on load; Market Replay works only when depth data is present.
- Wall size, traded volume, and delta are heuristics/context, not event classifications or trading recommendations.
- Session/day/week bar-derived references depend on the chart's data and trading-hours template.
- v0.2–v0.5 items—event markers, alerts, CSV/outcome tracking, and the heatmap—are not part of this release.

## Troubleshooting

### No walls appear

1. Check that the connection and instrument provide Level 2 depth.
2. Wait for live or Playback depth events; historical bars do not contain the book.
3. Temporarily lower **Minimum Lot Floor** or **Wall Multiplier**.
4. Confirm **Show Walls** is enabled and the price is within the visible chart scale.

### HUD says `BOOK STALE`

Check the connection and instrument's depth subscription. The HUD uses depth-event timestamps, so a continuing trade feed alone cannot make the book healthy.

### No bias shading

Enable **Enable Bias Shading**, allow the confirmation Swing to form, and check its period and strength. HTF-unformed periods fall back to the confirmation series.

### Session phase or early close looks wrong

Use the chart's intended exchange time zone and the **CME US Index Futures ETH** trading-hours template. The phase clock is Eastern; the template's actual session end is used for early close when the chart has provided a session boundary.

### Compile errors

Compile from NinjaScript Editor with **F5** and inspect the Errors tab. The generated NinjaScript region is intentionally omitted from the source file.

---

Free to use and modify.
