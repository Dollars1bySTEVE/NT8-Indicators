# OverboughtOversoldBackgroundBarsV2 — Settings Guide

> Living document — update this as we learn what works in live trading.

## What It Does

An always-on background **"warning light"** for the price chart:

- 🔴 **Red tint** — price is **overbought** (RSI above threshold) → a reversal down may be looming
- 🟢 **Green tint** — price is **oversold** (RSI below threshold) → a reversal up may be looming
- **Clear** — neutral, no signal

The tint appears once the condition has *persisted* (see Min Bars In Zone), stays as long as it holds, and clears when it ends. Deeper RSI extremes = more opaque tint (Gradient Intensity). Rendered with SharpDX **behind the chart bars** for smooth performance, full bar visibility, and zero conflicts with other background-coloring indicators.

**Primary use case:** 6/3 NinZaRenko chart on NQ/MNQ, but works on any futures instrument with threshold tuning.

---

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
| Visuals | Min Opacity % | **15** | |
| Visuals | Max Opacity % | **40** | 60 is too heavy even behind the bars |
| Visuals | Overbought Color | **Crimson** (#FFDC143C) | |
| Visuals | Oversold Color | **MediumSeaGreen** (#FF3CB371) | |
| Visuals | Show Status Readout | **On while tuning**, off once dialed in | |
| Order Flow | Enable Delta Boost | **On** | |
| Order Flow | Delta Boost Threshold | **100 RTH / 50 overnight** | Match the session's delta panel range |
| Order Flow | Enable Level 2 Boost | **Off** | Evaluate delta alone first |
| Order Flow | L2 Depth Levels | 5 | (unused while L2 off) |
| Order Flow | L2 Imbalance % Trigger | 70 | (unused while L2 off) |

> ⚠️ **Save these as the default template!** In the indicator dialog, click **template** (bottom right) → save as default. Recompiling after a code change that adds a new property **resets the instance to code defaults (70/30/60/100)** — this bit us once already. With a saved template, you're one click from restoring your baseline.

### Threshold profiles — pick per day's bias

| Profile | OB / OS | Personality |
|---|---|---|
| **75 / 25** (baseline) | strict both ways | Extremes only, fewest & highest-quality signals. Best default. |
| **75 / 35** | strict top, sensitive bottom | *Buy-dips* profile — greens fire on pullback lows in an uptrend |
| **70 / 25** | sensitive top, strict bottom | *Fade-rallies* profile — reds fire on swing highs, tuned for shorts |

Asymmetric profiles trade signal frequency on one side for noise. The strict side stays high-quality; the sensitive side fires often — treat those as *areas of interest*, not signals.

---

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

---

## Feature Reference

### Min Bars In Zone (persistence filter)
Tint only paints once RSI has held in the zone for N consecutive bars (default 3). On confirmation, the earlier bars of the run **back-fill retroactively**, so completed bands look whole on chart review. Brief one/two-brick blips never paint. **Visual-only** — the `ZoneState` series stays raw for strategy use. The readout shows pending zones as e.g. `[OVERBOUGHT (2/3)]` so you can watch a band building before it confirms. Set to **1** to restore classic always-on behavior.

### Gradient Intensity
Opacity scales from Min (at zone entry, e.g. RSI 75) to Max (near extremes). Saturates ~80% of the way to 0/100 since RSI rarely hits absolute limits. Faint tint = "extended"; deep tint = "very extended."

### Delta Boost (real-time only)
Watches the tape (`OnMarketData`). While in a zone, if net **aggressive opposing flow** on the current bar exceeds the threshold (selling into overbought / buying into oversold), tint jumps to Max Opacity — *"the reversal isn't just looming, it's starting."*

- Falls back to prior bar's delta early in a new brick (no flicker on renko transitions).
- **Threshold must match session volume** — watch your delta panel: NQ overnight runs ±30–50/bar (use ~50), RTH runs ±60–70+ (use 100–150).

### Level 2 Boost (experimental, real-time only)
Watches the resting book (`OnMarketDepth`, throttled to 4×/sec). Boosts when one side holds ≥ trigger % of visible size *against* the extreme. **Caution:** book size is spoofable, especially on NQ. Keep off until delta boost is dialed in. Readout shows `Book: off` while disabled.

### Status Readout
On-chart corner panel: live RSI + zone (with pending count), current bar delta, book imbalance (or `off`), and `** BOOST ACTIVE **` flag. Historical bars show `n/a (hist)` for delta — no historical tape/depth in NT8.

### Rendering
Drawn behind the chart bars (`ZOrder = ChartBars.ZOrder - 1`) so even max-opacity tint never overpowers the bricks.

### Strategy Access
`ZoneState` series is public: `1` = overbought, `-1` = oversold, `0` = neutral. Unfiltered by Min Bars In Zone.

---

## Known Behaviors & Limitations

- **Recompile resets settings** when a new property is added — re-enter your baseline and keep a saved default template (see Quick Start warning).
- **Boosts are real-time only** — historical bars show the pure RSI gradient. Inherent to NT8 (no historical tape/depth data).
- **Calculate = On price change** — background reacts intrabar. Switch to *On bar close* for confirmed-bar-only behavior.
- **Strong trends will still tint** — RSI pinning is reduced by wider thresholds + persistence filter but not eliminated. The tint means *extended*, not *enter now*. Wait for confirmation (delta boost, your entry signal, etc).
- **Thin sessions** (e.g., 4 AM) — expect more zone time; drop the delta threshold to ~50.

---

## Changelog

| Date | Change |
|---|---|
| 2026-07-23 | V2 released: SharpDX rendering, gradient intensity, delta boost, experimental L2 boost, status readout |
| 2026-07-23 | Fixed compile errors (escaped `&&`, missing `NinjaTrader.Cbi` using) |
| 2026-07-23 | Fixed status readout showing RSI 0.0 (render-thread indexer issue) |
| 2026-07-23 | Fixed readout showing price instead of RSI (dedicated `rsiSeries`); tint now renders **behind** bars (ZOrder); `Book: off` when L2 disabled |
| 2026-07-23 | Added **Min Bars In Zone** persistence filter (visual-only, retroactive fill, default 3); readout shows pending count |
| 2026-07-23 | Baseline validated in RTH: **75/25, opacity 15/40, Min Bars 3, delta 100 RTH / 50 overnight, L2 off** |

## Session Notes (add yours here)

> **2026-07-23 (overnight, ~4 AM ET):** 70/30 tinted red through most of an uptrend — too noisy. 75 OB fixed it. Delta running ±30–50 per bar pre-market, so 100 threshold never fired; use 40–50 overnight. Green bands at pullback lows looked genuinely useful for dip entries.

> **2026-07-23 (~4:54 AM ET):** 75/25 @ max 40 opacity confirmed as best balance in overnight chop — reds only at exhaustion highs, one quality green low before a rip. ZOrder fix verified: bars fully readable behind deepest tint.

> **2026-07-23 (RTH ~10:36 AM ET):** Recompile (Min Bars In Zone added) silently reset settings to 70/30/60 defaults → red wall in a strong uptrend, mirror-image green wall in the earlier downtrend. Lesson: **save the default template after any recompile.** Persistence filter itself worked great — all bands chunky, zero blips.

> **2026-07-23 (RTH ~10:40 AM ET):** Baseline restored (75/25/40, Min Bars 3). Excellent selectivity in heavy tape: reds at 10:40:17 & 10:40:25–29 nailed the 28,772 swing high before a hard leg down (delta +60/+70 buyer exhaustion into the top); greens marked pullback lows and the extended selloff. Validated as the locked-in RTH baseline. Delta 100 appropriate for RTH volume.

<!-- Template:
> **DATE (session):** what settings, what worked, what didn't.
-->
