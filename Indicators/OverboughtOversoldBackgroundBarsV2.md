# OverboughtOversoldBackgroundBarsV2 — Settings Guide

> Living document — update this as we learn what works in live trading.

## What It Does

An always-on background **"warning light"** for the price chart:

- 🔴 **Red tint** — price is **overbought** (RSI above threshold) → a reversal down may be looming
- 🟢 **Green tint** — price is **oversold** (RSI below threshold) → a reversal up may be looming
- **Clear** — neutral, no signal

The tint appears once the condition has *persisted* (see Min Bars In Zone), stays as long as it holds, and clears when it ends. Deeper RSI extremes = more opaque tint (Gradient Intensity). Rendered with SharpDX **behind the chart bars** for smooth performance, full bar visibility, and zero conflicts with other background-coloring indicators.

**Primary use case:** 6/3 NinZaRenko chart on NQ/MNQ, but works on any futures instrument with threshold tuning.

## Design Goal

**Catch the bigger oversold and overbought extremes — equally on both sides — as accurately as possible.** Nothing is 100%, but every tuning decision should serve that goal:

- **Symmetric strict thresholds (75/25)** — no directional bias; both sides earn their signal the same way
- **Persistence filter (Min Bars 3)** — kills *brief* touches on both sides equally
- **Wide opacity gradient (5→40)** — mutes *shallow* zones to a whisper; only deep extremes visually pop
- **Delta confluence** — the highest-conviction reads come when a saturated band coincides with an opposing delta spike (see Trade Trigger Pattern below)

## Quick Start — Validated Baseline (NQ 6/3 NinZaRenko)

*Locked in 2026-07-23 after live tuning in both overnight and RTH sessions:*

| Group | Setting | Value | Notes |
|---|---|---|---|
| Parameters | RSI Period | **14** | |
| Parameters | RSI Smooth | **1** | |
| Parameters | Overbought Threshold | **75** | 70 paints a "red wall" on trend days — validated twice |
| Parameters | Oversold Threshold | **25** | 30 mirrors the same problem in downtrends |
| Parameters | Min Bars In Zone | **3** | Kills one/two-brick blip signals; set 1 for classic always-on |
| Visuals | Gradient Intensity | **On** | |
| Visuals | Min Opacity % | **5** | "Conviction dial": shallow zones whisper, deep extremes pop |
| Visuals | Max Opacity % | **40** | 60 is too heavy even behind the bars |
| Visuals | Overbought Color | **Crimson** (#FFDC143C) | |
| Visuals | Oversold Color | **MediumSeaGreen** (#FF3CB371) | |
| Visuals | Show Status Readout | **On while tuning**, off once dialed in | |
| Order Flow | Enable Delta Boost | **On** | |
| Order Flow | Delta Boost Threshold | **100 RTH / 50 overnight** | Match the session's delta panel range |
| Order Flow | Require Delta Confluence | **Off** | Soft gate (real-time only): whisper until a with-move opposing flush occurs |
| Order Flow | Confluence Threshold | **100** | Flush threshold; can be lower than Delta Boost Threshold |
| Order Flow | Unconfirmed Opacity % | **15** | Fixed whisper opacity while pending flush |
| Order Flow | Enable Level 2 Boost | **Off** | Evaluate delta alone first |
| Order Flow | L2 Depth Levels | 5 | (unused while L2 off) |
| Order Flow | L2 Imbalance % Trigger | 70 | (unused while L2 off) |

> ⚠️ **Save these as the default template!** In the indicator dialog, click **template** (bottom right) → save as default. Recompiling after a code change that adds a new property **resets the instance to code defaults (70/30/60/100)** — this bit us once already. With a saved template, you're one click from restoring your baseline.

### Two-layer noise filtering

The two filters are independent and catch different noise:

| Filter | Kills | Passes |
|---|---|---|
| **Min Bars In Zone (3)** | *Brief* zone touches (1–2 brick blips) | Persistent zones |
| **Min Opacity 5 + gradient** | Visual weight of *shallow* zones | Deep extremes stand out at full saturation |

Result: faint tint = "stretched, be aware"; saturated tint = "genuine extreme, judgment call."

### Trade Trigger Pattern (highest-conviction read)

The A+ setup observed live: **a saturated band paints while the delta panel spikes against the extreme** — e.g. deep green oversold band + the session's largest negative delta bar (sellers flushing into the low). That's exhaustion: aggressive participants capitulating into an extreme. Validated 2026-07-23 10:45:54 (see session notes). The Delta Boost automates a version of this (band jumps to max opacity), but the visual confluence with your delta subgraph is the primary read.

### Threshold profiles — pick per day's bias

| Profile | OB / OS | Personality |
|---|---|---|
| **75 / 25** (baseline) | strict both ways | Extremes only, equal treatment both sides. **The default — matches the design goal.** |
| **75 / 35** | strict top, sensitive bottom | *Buy-dips* profile — greens fire on pullback lows in an uptrend |
| **70 / 25** | sensitive top, strict bottom | *Fade-rallies* profile — reds fire on swing highs, tuned for shorts |

Asymmetric profiles trade signal frequency on one side for noise, and break the both-sides-equal goal — use them deliberately, only when the day has a clear directional bias.

## Per-Instrument Threshold Cheat Sheet

RSI is normalized 0–100, so only thresholds need tuning per instrument:

| Instrument | Character | OB / OS | Delta Boost |
|---|---|---|---|
| **NQ** | Fast, momentum-heavy | 75 / 25 (78/22 strong trend days) | 100–150 RTH, 40–50 overnight |
| **MNQ** | Same, micro sizing | Same as NQ | ~10× NQ values (1000+) |
| **ES / MES** | Steadier, mean-reverting | 70 / 30 classic | 300–500 RTH (ES trades bigger size) |
| **CL** | Violent, headline-driven | 75 / 25 or 80 / 20 | 30–60 |
| **GC** | Grinding, longer cycles | 70 / 30, RSI period 18–21 | 50–100 |
| **ZB / ZN** | Slow | 65 / 35 | 200+ (large resting size) |

*These are starting points — log what actually works in the session notes below.*

## Feature Reference

### Min Bars In Zone (persistence filter)
Tint only paints once RSI has held in the zone for N consecutive bars (default 3). On confirmation, the earlier bars of the run **back-fill retroactively**, so completed bands look whole on chart review. Brief one/two-brick blips never paint. **Visual-only** — the `ZoneState` series stays raw for strategy use. The readout shows pending zones as e.g. `[OVERBOUGHT (2/3)]` so you can watch a band building before it confirms. Set to **1** to restore classic always-on behavior.

### Gradient Intensity
Opacity scales from Min (at zone entry, e.g. RSI 75) to Max (near extremes). Saturates ~80% of the way to 0/100 since RSI rarely hits absolute limits. With Min at 5, shallow zones are a near-invisible whisper and deep extremes carry all the visual weight.

### Delta Boost (real-time only)
Watches the tape (`OnMarketData`). While in a zone, if net **aggressive opposing flow** on the current bar exceeds the threshold (selling into overbought / buying into oversold), tint jumps to Max Opacity — *"the reversal isn't just looming, it's starting."*

- Falls back to prior bar's delta early in a new brick (no flicker on renko transitions).
- **Threshold must match session volume** — watch your delta panel: NQ overnight runs ±30–50/bar (use ~50), RTH runs ±60–70+ (use 100–150).

### Delta-Confluence Requirement (soft gate, real-time only)
Optional three-state behavior (default **Off**) that promotes delta confluence from "intensifier" to "gatekeeper":

1. **Whisper (RSI-only):** run passed Min Bars + Min RSI Depth, but no with-move opposing flush yet → paints at fixed **Unconfirmed Opacity %** (default 15).
2. **Full paint (RSI + capitulation):** once a flush appears during the run, the run graduates to normal full gradient opacity (including retro-upgrade of earlier whisper bars) and becomes follow-latch eligible.
3. **Neutral/clear:** unchanged.

Flush definition (same effective-delta fallback as boost logic: current bar delta, else prior bar delta):
- Overbought run: flush = net selling delta ≤ **-Confluence Threshold**
- Oversold run: flush = net buying delta ≥ **+Confluence Threshold**

Readout appends **`(pending flush)`** when bars+depth are met but the run is still whisper-only.

> NT8 limitation: no historical tape. With this gate enabled, historical bars remain RSI-only (no gating), while live bars use the whisper/full confluence grammar.

### Level 2 Boost (experimental, real-time only)
Watches the resting book (`OnMarketDepth`, throttled to 4×/sec). Boosts when one side holds ≥ trigger % of visible size *against* the extreme. **Caution:** book size is spoofable, especially on NQ. Keep off until delta boost is dialed in. Readout shows `Book: off` while disabled.

### Status Readout
On-chart corner panel: live RSI + zone (with pending count), current bar delta, book imbalance (or `off`), and `** BOOST ACTIVE **` flag. Historical bars show `n/a (hist)` for delta — no historical tape/depth in NT8.

### Rendering
Drawn behind the chart bars (`ZOrder = ChartBars.ZOrder - 1`) so even max-opacity tint never overpowers the bricks.

### Strategy Access
`ZoneState` series is public: `1` = overbought, `-1` = oversold, `0` = neutral. Unfiltered by Min Bars In Zone.

## Known Behaviors & Limitations

- **Recompile resets settings** when a new property is added — re-enter your baseline and keep a saved default template (see Quick Start warning).
- **Boosts are real-time only** — historical bars show the pure RSI gradient. Inherent to NT8 (no historical tape/depth data).
- **Delta-confluence gate is real-time only** — with the gate on, historical bands are still RSI-only while live bands can remain whisper until a flush arrives.
- **Calculate = On price change** — background reacts intrabar. Switch to *On bar close* for confirmed-bar-only behavior.
- **Strong trends will still tint** — RSI pinning is reduced by wider thresholds + persistence filter but not eliminated. The tint means *extended*, not *enter now*. Wait for confirmation (delta confluence, your entry signal, etc).
- **Thin sessions** (e.g., 4 AM) — expect more zone time; drop the delta threshold to ~50.
- **Confluence threshold sensitivity matters more with the gate on** — too high in a quiet session can yield few/no full confirmations.

## Ideas / Backlog (accuracy work)

- **Depth-based confirmation** — optionally require RSI to reach N points past the threshold before painting (filters shallow zones entirely rather than just dimming them)
- **L2 boost evaluation session** — one live session with L2 on to see if the book adds anything the tape doesn't
- **RTH-open delta calibration** — verify 100–150 threshold behavior in the 9:30–10:00 burst

## Changelog

| Date | Change |
|---|---|
| 2026-07-23 | V2 released: SharpDX rendering, gradient intensity, delta boost, experimental L2 boost, status readout |
| 2026-07-23 | Fixed compile errors (escaped `&&`, missing `NinjaTrader.Cbi` using) |
| 2026-07-23 | Fixed status readout showing RSI 0.0 (render-thread indexer issue) |
| 2026-07-23 | Fixed readout showing price instead of RSI (dedicated `rsiSeries`); tint now renders **behind** bars (ZOrder); `Book: off` when L2 disabled |
| 2026-07-23 | Added **Min Bars In Zone** persistence filter (visual-only, retroactive fill, default 3); readout shows pending count |
| 2026-07-23 | Baseline validated in RTH: **75/25, opacity 15/40, Min Bars 3, delta 100 RTH / 50 overnight, L2 off** |
| 2026-07-23 | Min Opacity refined 15 → **5** ("conviction dial"); documented design goal (equal both-sides accuracy) and delta-confluence trade trigger |
| 2026-07-23 | Added optional **Delta-Confluence** soft gate (real-time only, default off): runs that pass bars+depth but lack a with-move flush paint at fixed unconfirmed opacity (whisper) and do not latch follow; a flush graduates to full gradient + follow. Readout shows `(pending flush)`. New properties added — re-save default template after recompile. |

## Session Notes (add yours here)

> **2026-07-23 (overnight, ~4 AM ET):** 70/30 tinted red through most of an uptrend — too noisy. 75 OB fixed it. Delta running ±30–50 per bar pre-market, so 100 threshold never fired; use 40–50 overnight. Green bands at pullback lows looked genuinely useful for dip entries.

> **2026-07-23 (~4:54 AM ET):** 75/25 @ max 40 opacity confirmed as best balance in overnight chop — reds only at exhaustion highs, one quality green low before a rip. ZOrder fix verified: bars fully readable behind deepest tint.

> **2026-07-23 (RTH ~10:36 AM ET):** Recompile (Min Bars In Zone added) silently reset settings to 70/30/60 defaults → red wall in a strong uptrend, mirror-image green wall in the earlier downtrend. Lesson: **save the default template after any recompile.** Persistence filter itself worked great — all bands chunky, zero blips.

> **2026-07-23 (RTH ~10:40 AM ET):** Baseline restored (75/25/40, Min Bars 3). Excellent selectivity in heavy tape: reds at 10:40:17 & 10:40:25–29 nailed the 28,772 swing high before a hard leg down (delta +60/+70 buyer exhaustion into the top); greens marked pullback lows and the extended selloff. Validated as the locked-in RTH baseline. Delta 100 appropriate for RTH volume.

> **2026-07-23 (RTH ~10:45 AM ET):** Dropped Min Opacity 15 → 5. Big improvement in visual hierarchy: shallow zones now whisper (easy to ignore), deep extremes pop. **A+ signal captured at 10:45:54:** saturated green oversold band into the 28,628 low *while* the delta panel printed −60 (largest sell flush on the chart) — sellers capitulating into the extreme; price based and reversed ~30 points. This band+opposing-delta-spike confluence is the primary trade trigger pattern going forward.

<!-- Template:
> **DATE (session):** what settings, what worked, what didn't.
-->
