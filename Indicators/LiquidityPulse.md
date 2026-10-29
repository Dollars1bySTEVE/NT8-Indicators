# LiquidityPulse — Level 2 Liquidity and Reference Levels for NinjaTrader 8

**Version:** v0.2 — Event detection and live visualization

**Type:** On-chart overlay indicator (SharpDX)
**Related specification:** [Issue #196](https://github.com/Dollars1bySTEVE/NT8-Indicators/issues/196)

---

## Overview

LiquidityPulse combines the standard NinjaTrader Level 2 book and trade feed with session/day/week reference levels. It displays persistent, large resting bid and ask liquidity, tracks aggressive trade volume at those levels, and marks heuristic pull, break, absorption, sweep, failed-sweep, held-run, and large-trade events.

Event markers and the depth heatmap are live/Playback context, not predictions or trade recommendations. This release does not issue alerts or retain event history across indicator reloads.

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
| Scale Walls By Size | On | Scales line width and opacity by size relative to threshold; brightness scaling retains a 40% minimum before the configured opacity. |
| Minimum Wall Label Spacing | 12 px | Vertical separation used to avoid label overlap. |
| Wall Minimum Age Seconds | 3 | Suppresses short-lived walls. |
| Wall Grace Milliseconds | 1000 | Event-time grace (0–10000 ms) before a wall below its retention threshold becomes inactive. |
| Feed Stale Seconds | 5 | Marks the HUD stale and greys the walls after this long without depth. |
| Show Walls / Show Wall Labels | On / On | Independent wall and label display switches. |
| Ask / Bid / Stale Wall Color | Crimson / Lime Green / Gray | Independently serialized colors. |
| Ask / Bid / Stale Wall Opacity | 90 / 90 / 65% | Independent alpha settings. |
| Wall Minimum / Maximum Width | 1 / 6 px | Width range for size-scaled lines. |
| Wall Dash Style / Label Font Size | Solid / 10 | Line style and label type size. |

The adaptive baseline samples the average size of available ask and bid rows every two event-time seconds. It keeps the median for the selected window and uses `max(Minimum Lot Floor, ceil(median × Wall Multiplier))`. The baseline and samples reset when the phase changes. Sampling continues when Use Adaptive Threshold is off (walls then use the fixed floor) so held-run detection keeps its liquidity-vacuum baseline.

### Event detection

| Setting | Default | Description |
|---|---:|---|
| Show Event Markers | On | Master switch for all event annotations. |
| Show Pull / Filled / Absorption / Sweep / Failed Sweep / Held Run / Large Trade | On | Independent event visibility switches. |
| Event Marker Font Size / Opacity | 9 / 90% | Marker label styling. |
| Event Marker Retention Minutes | 60 | Maximum age retained in the current indicator instance. |
| Wall Fade Duration Seconds | 10 | Pulled walls fade as dashed gray lines; broken walls fade as solid lines. |
| Event Grace Milliseconds | 300 | Initial event grace; final pull/break classification waits for the greater of this value and Wall Grace. |
| Pull Trade Ratio | 0.3 | A disappearance with traded volume below this fraction of peak wall size is labeled a likely pull. |
| Absorption Trade Multiple | 1.5 | Requires aggressive volume above this multiple of peak wall size, a detected refill, and price holding at the wall. |
| Sweep Window / Minimum Ticks / Minimum Volume | 3 sec / 3 / 20 | Same-side aggressive trade burst needed for a sweep marker. |
| Failed Run Window Seconds | 10 | Time to return across a crossed key level with sufficient opposite-side aggressive volume. |
| Large Trade Minimum Contracts | 20 | Minimum print size; the effective cutoff is also at least 2× the current wall threshold. |
| Key Level Proximity Ticks / Event Cooldown Seconds | 2 / 5 | Level tagging distance and minimum interval between distinct directional sweep episodes. |
| Event Colors | Per-event | Independently configurable pull, break, absorption, sweep, failed-sweep, held-run, and large-trade colors. |

The book-imbalance readout in the HUD is a smoothed bid-versus-ask size comparison. It is context only and does not trigger a directional signal.

### Heatmap and event placement

| Setting | Default | Description |
|---|---:|---|
| Show Heatmap | On | Displays recent bid/ask depth in a narrow strip at the chart's right edge. |
| Heatmap History Seconds | 60 | Visible rolling window, configurable from 10 to 60 seconds. |
| Heatmap Width | 160 px | Width of the strip, configurable from 40 to 400 px. |
| Heatmap Opacity | 45% | Maximum cell opacity; stronger displayed size is brighter and older samples fade. |
| Heatmap Bid / Ask Color | Lime Green / Crimson | Independently configurable depth colors. |

Depth is sampled at most every 250 ms, retaining at most 240 snapshots and the top 10 rows per side. The heatmap begins only after live/Playback depth arrives and does not backfill historical depth. It uses aggregated displayed size, not individual order identity. The strip occupies chart space at the right edge; a chart right margin can help keep it clear of the latest bars.

Event labels are anchored to their event time and price instead of collecting at the latest bar. Labels outside the visible chart range are skipped, and nearby labels are repositioned to avoid overlap.

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
3. **Wall lifecycle:** each threshold-qualified price tracks first-seen time, last-qualified time, peak/current size, trade volume, refills, and active state. Existing walls stay qualified at 80% of the current threshold (hysteresis). Below that level or absent from the book, they remain active until **Wall Grace Milliseconds** elapses since last qualification. Returning within grace preserves age, peak size, and traded volumes. Once a detection pass has observed the loss of qualification, a return after grace has elapsed ends the old lifecycle and must meet the full entry threshold again; walls that stay continuously qualified during quiet periods are never reset. Only active walls past their minimum age are rendered.
4. **Clock and phases:** depth/trade event timestamps drive session phase, wall age, and sampling. Before the first event, the local clock is used only as a fallback. Live realtime feed health instead uses UTC wall-clock time since the last depth arrival, so a complete feed drop still becomes stale; Playback retains event-time feed health, including when paused. Eastern time is resolved with the DST-aware `Eastern Standard Time` zone. Phases are ETH Overnight (18:00–03:00), ETH London (03:00–09:30), RTH Open (09:30–10:00), RTH (10:00–16:00), ETH Post-RTH (16:00–17:00, HUD `ETH-POST-RTH`), and Post-Close (17:00–18:00 CME maintenance). The chart's `SessionIterator` supplies its session end when available, including early closes; the book and wall lifecycles are reset once whenever the clock crosses the 17:00 close or an earlier actual session end, even if no event arrived inside the closed interval. Walls stay suppressed until 18:00, but depth received during maintenance is retained so positional updates at reopening still apply. In live realtime, the timer advances the event clock by elapsed wall-clock time since the last event so grace expiry and close boundaries are processed without new data; Playback stays on event time.
5. **Levels:** previous/current day, week, and trading session OH/L are updated from primary bars. Session and weekly profile/VWAP calculations replace the current developing-bar contribution before adding the updated value, preventing cumulative-volume overcounting with `Calculate.OnEachTick`.
6. **Rendering:** both depth and trade/quote events run shared wall refresh/baseline work throttled to approximately 150 ms; book changes skipped by the throttle schedule a trailing detection pass so the final change in a burst is always reconciled. Chart invalidation is limited to at most about 13 times per second. A realtime timer also requests a repaint about once per second without new data so stale status and grey walls remain visible; it is disposed at termination. Rendering snapshots active wall state under the book lock, filters to the visible price scale, selects the largest walls, and avoids nearby labels. SharpDX brushes, text formats, and stroke styles are reused and disposed on render-target changes and termination.
7. **Events:** trades are kept in short rolling per-side windows with running volume and price extrema for sweep detection. Each directional rolling burst can trigger once and rearms only after its qualifying volume/price span subsides, preventing repeated labels for the same burst. Wall disappearance is classified only after the grace window and only while its price remains between the outer edges of the visible bid and ask depth: low traded volume yields a likely-pull marker; sufficient traded volume plus a trade through the price recorded at any time during the wall lifecycle yields a broken-wall marker. A wall that requalifies before its earlier disappearance is classified keeps that earlier lifecycle pending until classification completes. Absorption requires a fresh book, a size reduction/refill, aggressive traded volume above the configured multiple of peak size, and price holding at the wall. Sweeps require same-side aggressive volume and time-ordered progress in the aggressor direction (from the earlier window extreme to the current print); key-level crossings are searched across the full sweep span; a key-level sweep becomes a failed sweep only after price returns across the level with opposite aggressive volume, or a held run when price remains beyond the level and depth ahead is thin. Large-trade and book-imbalance measures are contextual heuristics. Event markers are retained in memory only, expire by age (including during maintenance/early-close suppression), and render at both event time and price with separate colors and switches. Up to 12 recent visible labels are rendered with overlap avoidance.
8. **Heatmap:** a fresh-book snapshot is sampled at most every 250 ms, with a bounded in-memory history of up to 240 snapshots and 10 levels per side. Cell position represents snapshot age within the configured recent-history window and each cell spans until the next sample (capped at Feed Stale Seconds); samples sharing a pixel column are merged using the largest size per level; color represents bid/ask and opacity scales with displayed size and fades with age. It is a rolling live/Playback visualization and has no historical-depth backfill.

## Recommended charts

- **Primary:** 1-minute Volumetric chart for levels and context.
- **Scalping:** NQ 500–1000 tick chart.
- Use the **CME US Index Futures ETH** trading-hours template.
- MNQ has a thinner independent book; consider observing NQ liquidity while executing MNQ.

## Known limits

- Only the provider's visible top ~10 depth levels are available; this is aggregated MBP data, not per-order MBO.
- There is no historical depth replay on ordinary chart history. Walls, tape, and heatmap snapshots start on load; Market Replay works only when depth data is present.
- Pull/break, absorption, sweep, failed-run, held-run, and large-trade markers are heuristics, not proof of spoofing, iceberg orders, stop hunts, or future price direction.
- A disappearing level that falls outside the currently visible depth window is not classified as a pull or break. Aggregated Level 2 cannot identify individual order cancellations or fills.
- Failed sweeps require a level crossing, return across it, and enough opposite aggressive volume; held-run detection uses visible depth as a rough liquidity-vacuum proxy.
- Event markers exist only in memory and disappear when the indicator is reloaded. Alerts and CSV/outcome tracking are not part of this release.
- Session/day/week bar-derived references depend on the chart's data and trading-hours template.
- Audio alerts and CSV/outcome tracking are not part of this release.

## Troubleshooting

### No walls appear

1. Check that the connection and instrument provide Level 2 depth.
2. Wait for live or Playback depth events; historical bars do not contain the book.
3. Temporarily lower **Minimum Lot Floor** or **Wall Multiplier**.
4. Confirm **Show Walls** is enabled and the price is within the visible chart scale.

### HUD says `BOOK STALE`

Check the connection and instrument's depth subscription. Live realtime uses elapsed UTC time since the last depth arrival, even if all feeds stop; Playback uses elapsed feed-event time since the last depth update. A continuing trade feed alone cannot make the book healthy.

### No bias shading

Enable **Enable Bias Shading**, allow the confirmation Swing to form, and check its period and strength. HTF-unformed periods fall back to the confirmation series.

### Session phase or early close looks wrong

Use the chart's intended exchange time zone and the **CME US Index Futures ETH** trading-hours template. The phase clock is Eastern; the template's actual session end is used for early close when the chart has provided a session boundary.

### Compile errors

Compile from NinjaScript Editor with **F5** and inspect the Errors tab. The generated NinjaScript region is intentionally omitted from the source file.

---

Free to use and modify.
