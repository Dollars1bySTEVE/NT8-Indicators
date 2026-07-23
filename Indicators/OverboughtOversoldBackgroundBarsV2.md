# OverboughtOversoldBackgroundBarsV2 — Settings Guide

> Living document — update this as we learn what works in live trading.

## What It Does

An always-on background **"warning light"** for the price chart:

- 🔴 **Red tint** — price is **overbought** (RSI above threshold) → a reversal down may be looming
- 🟢 **Green tint** — price is **oversold** (RSI below threshold) → a reversal up may be looming
- **Clear** — neutral, no signal

The tint appears the moment the condition starts, stays as long as it holds, and clears the instant it ends. Deeper RSI extremes = more opaque tint (Gradient Intensity). Rendered with SharpDX for smooth performance and zero conflicts with other background-coloring indicators.

**Primary use case:** 6/3 NinZaRenko chart on NQ/MNQ, but works on any futures instrument with threshold tuning.

---

## Quick Start — Current Preferred Settings (NQ 6/3 NinZaRenko)

*As of 2026-07-23, after live tuning:*

| Group | Setting | Value | Notes |
|---|---|---|---|
| Parameters | RSI Period | **14** | Try 9–10 if signals feel late (renko compresses time) |
| Parameters | RSI Smooth | **1** | |
| Parameters | Overbought Threshold | **75** | Widened from 70 — renko trends pin RSI |
| Parameters | Oversold Threshold | **35** | Asymmetric on purpose — see below |
| Visuals | Gradient Intensity | **On** | |
| Visuals | Min Opacity % | **15** | |
| Visuals | Max Opacity % | **50** | Enough range for extremes to pop |
| Visuals | Overbought Color | **Crimson** (#FFDC143C) | |
| Visuals | Oversold Color | **MediumSeaGreen** (#FF3CB371) | |
| Visuals | Show Status Readout | **On while tuning**, off once dialed in | |
| Order Flow | Enable Delta Boost | **On** | |
| Order Flow | Delta Boost Threshold | **100** RTH / **40–50** overnight | See session notes |
| Order Flow | Enable Level 2 Boost | **Off** | Evaluate delta alone first |
| Order Flow | L2 Depth Levels | 5 | (unused while L2 off) |
| Order Flow | L2 Imbalance % Trigger | 70 | (unused while L2 off) |

### Why asymmetric thresholds (75 / 35)?

- **OB 75 (strict):** in an uptrend, renko bricks pin RSI high — a strict overbought filter cuts false "reversal looming" reds during strong runs.
- **OS 35 (sensitive):** the more sensitive oversold side fires green on pullback lows, flagging **dip-buy spots in an uptrend**.

This tunes the tool for *buying pullbacks in an uptrend*. For symmetric extreme-only behavior (range days / no directional bias), use **75 / 25**.

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

### Gradient Intensity
Opacity scales from Min (at zone entry, e.g. RSI 75) to Max (near extremes). Saturates ~80% of the way to 0/100 since RSI rarely hits absolute limits. Faint tint = "extended"; deep tint = "very extended."

### Delta Boost (real-time only)
Watches the tape (`OnMarketData`). While in a zone, if net **aggressive opposing flow** on the current bar exceeds the threshold (selling into overbought / buying into oversold), tint jumps to Max Opacity — *"the reversal isn't just looming, it's starting."*

- Falls back to prior bar's delta early in a new brick (no flicker on renko transitions).
- **Threshold must match session volume** — watch your delta panel: if bars run ±30–50 overnight, a threshold of 100 will never fire.

### Level 2 Boost (experimental, real-time only)
Watches the resting book (`OnMarketDepth`, throttled to 4×/sec). Boosts when one side holds ≥ trigger % of visible size *against* the extreme. **Caution:** book size is spoofable, especially on NQ. Keep off until delta boost is dialed in.

### Status Readout
On-chart corner panel: live RSI + zone, current bar delta, book imbalance % and `** BOOST ACTIVE **` flag. Note: readout collects tape/depth data even when boosts are off, so you can watch flow before enabling them. Historical bars show `n/a (hist)` for delta/book — no historical tape/depth in NT8.

### Strategy Access
`ZoneState` series is public: `1` = overbought, `-1` = oversold, `0` = neutral.

---

## Known Behaviors & Limitations

- **Boosts are real-time only** — historical bars show the pure RSI gradient. Inherent to NT8 (no historical tape/depth data).
- **Calculate = On price change** — background reacts intrabar. Switch to *On bar close* in the indicator dialog for confirmed-bar-only behavior.
- **Strong trends will still tint** — RSI pinning is reduced by wider thresholds but not eliminated. The tint means *extended*, not *enter now*. Wait for confirmation (delta boost, your entry signal, etc).
- **4 AM-style thin sessions** — expect more zone time and adjust delta threshold down.

---

## Changelog

| Date | Change |
|---|---|
| 2026-07-23 | V2 released: SharpDX rendering, gradient intensity, delta boost, experimental L2 boost, status readout |
| 2026-07-23 | Fixed compile errors (escaped `&&`, missing `NinjaTrader.Cbi` using) |
| 2026-07-23 | Fixed status readout showing RSI 0.0 (render-thread indexer issue) |
| 2026-07-23 | Live tuning on NQ 6/3 NinZaRenko: OB 70→75, Max Opacity 30→50, OS kept sensitive at 35 for pullback-buy signals |

## Session Notes (add yours here)

> **2026-07-23 (overnight, ~4 AM ET):** 70/30 tinted red through most of an uptrend — too noisy. 75 OB fixed it. Delta running ±30–50 per bar pre-market, so 100 threshold never fired; use 40–50 overnight. Green bands at pullback lows looked genuinely useful for dip entries.

<!-- Template:
> **DATE (session):** what settings, what worked, what didn't.
-->
