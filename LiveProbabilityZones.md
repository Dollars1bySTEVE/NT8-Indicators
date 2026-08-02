# LiveProbabilityZones — Design Document & Build Plan

> **Status:** Phase 1 complete (base version). Phase 2 (L2/Order Flow enhanced) planned.

---

## 📌 What This Indicator Is

Auto-drawn forward-probability zones that appear **ahead of price** and update their **% probability live every tick** as the market moves. Zones change colour based on probability strength and can shift from Grey → Gold → Green (high probability target) or Red (low probability resistance).

This is **different** from `BreakoutProbabilityExpo` which looks backward at historical bar data. This indicator looks **forward** — answering:

> *"What is the mathematical probability that price REACHES this level before the session ends?"*

---

## 🔍 What The Zones Represent

### Zone Placement
Zones are drawn at **ATR-based multiples from the session open**, projected both above and below current price:

```
Session Open = anchor point

Zone Above 1 = Open + (0.5 × ATR)
Zone Above 2 = Open + (1.0 × ATR)
Zone Above 3 = Open + (1.5 × ATR)
Zone Above 4 = Open + (2.0 × ATR)

Zone Below 1 = Open - (0.5 × ATR)
Zone Below 2 = Open - (1.0 × ATR)
Zone Below 3 = Open - (1.5 × ATR)
Zone Below 4 = Open - (2.0 × ATR)
```

Zone **thickness** is proportional to ATR magnitude — bigger volatility = thicker zone.

### Why These Levels?
- ATR multiples represent statistically meaningful move targets for the instrument
- Session open is the most natural intraday anchor point
- Prior session H/L/Close can be added as additional zone sources (Phase 2)
- Options strike clusters and DOM walls can reinforce zones (Phase 2)

---

## 🧮 The Core Probability Formula

This uses the **Reflection Principle / First Passage Time** from stochastic process theory:

```
P(price touches level X before session end) =

  2 × N( -|ln(X/S)| / (σ × √T) )

Where:
  S = current live price (updates every tick)
  X = zone level price (fixed when drawn at session open)
  σ = rolling volatility = ATR(14) / Close[0]
  T = fraction of session remaining (0.0 at close → 1.0 at open)
  N = cumulative normal distribution function
```

### Why the % Changes Live

| Event | Effect on % |
|---|---|
| Price moves CLOSER to zone | % goes UP |
| Price moves AWAY from zone | % goes DOWN |
| Session time passes | % adjusts (closer zones up, far zones down) |
| Volatility (ATR) increases | % goes UP on all zones |
| Volatility (ATR) decreases | % goes DOWN on all zones |

---

## 🎨 Colour Logic

```
Grey        →  Zone just drawn / probability < 35%
             Low confluence, price far away

Gold/Orange →  35% to 75% probability
             Medium confidence, price approaching

Green       →  > 75% probability
             High confidence, price likely to reach

Red         →  Zone is ABOVE current price with low %
             Acting as resistance, low chance of breaking through
```

### Colour Transition Examples
```
Price rallies toward upper zone:
  Grey (far) → Gold (approaching) → Green (high probability)

Price fails and drops away from upper zone:
  Green → Gold → Grey or Red (resistance confirmed)

Price drops toward lower zone:
  Grey → Gold → Green (downside target)
```

---

## 📐 Zone Size / Thickness Logic

```
Base thickness = ATR value (in points)
Zone half-width = ATR × 0.05 (5% of ATR per side)

Small ATR session  → thin zones
High volatility    → thicker zones

Phase 2 addition:
  DOM order size at level → multiplies thickness
  Large wall = thicker zone
  No orders  = thinner zone
```

---

## 🕐 Timeframe Guidance

| Timeframe | Behaviour | Verdict |
|---|---|---|
| 1m | Very reactive, fast % shifts | Scalping only |
| 3m | Good balance | Scalping/day trading |
| **5m** | **Sweet spot — smooth, readable** | **Recommended ✅** |
| 15m | Stable, slow % movement | Swing intraday |
| 30m+ | Near-static zones intraday | Not ideal |

The indicator is **timeframe-agnostic** — σ uses ATR from whatever TF is loaded, and T always uses the SESSION clock not bar count. Works on all timeframes but 5m is optimal.

---

## 🏗️ Build Phases

### ✅ Phase 1 — Base Version (COMPLETE)
**File:** `Indicators/LiveProbabilityZones.cs`

Features:
- ATR-based zone placement from session open
- Live touch probability calculation every tick
- Grey / Gold / Green / Red colour coding
- Variable zone thickness based on ATR
- % label updates every tick
- Works on any timeframe
- No external data dependencies

### 🔲 Phase 2 — Enhanced Version (PLANNED)
**File:** `Indicators/LiveProbabilityZonesEnhanced.cs`

Additional features:
- **Level 2 DOM integration** via `OnMarketDepth`
  - Large resting orders → zone reinforced, % adjusted down (harder to reach)
  - Thin DOM at level → % adjusted up (easier to pass through)
  - Order pulled from DOM → zone weakens
- **Order flow / delta integration**
  - Aggressive buying toward zone → % boosted
  - Absorption at zone → colour confirmed
- **Volume Profile integration**
  - High Volume Nodes → additional zone draw triggers
  - Low Volume Nodes → zones marked as fast-move areas
- **Prior session structure**
  - Prior H/L/Close as additional zone anchors
  - Overnight high/low zones
- **Confluence scoring**
  - Each zone gets a 0–5 confluence score
  - Score drives both thickness AND colour intensity
  - 1 reason = thin/grey | 3+ reasons = thick/strong colour
- **GPU rendering** via SharpDX Direct2D (same as IQMainGPU / IQKeyLevelsGPU)

---

## 📊 Phase 2 — Full Probability Score Formula

Combining all factors (Phase 2 target):

```
FinalProbability =
  (DistanceProb  × 0.35)   // 35% weight — how close is price
+ (VolumeScore   × 0.25)   // 25% weight — volume node strength
+ (DOMScore      × 0.25)   // 25% weight — live order flow / DOM
+ (TimeScore     × 0.10)   // 10% weight — session time remaining
+ (VolatilityAdj × 0.05)   //  5% weight — volatility adjustment

Each score normalised 0.0 → 1.0
Final output: 0% → 99%
```

### Phase 2 L2 Modifier (DOM)
```csharp
// Large order at level = harder to reach = probability DOWN
double domSize   = GetDOMSizeAtLevel(X);
double avgSize   = GetAverageDOMSize();
double wallRatio = domSize / avgSize;
double l2Modifier = 1.0 - Math.Min(wallRatio * 0.05, 0.30); // max -30%
double adjustedProb = baseProb * l2Modifier;
```

### Phase 2 Zone Thickness With DOM
```csharp
// Thickness scales with order size sitting at level
double baseThickness = dailyATR * 0.05;
double domWeight     = Math.Min(domSize / avgSize, 3.0); // cap at 3×
double finalThickness = baseThickness * (1.0 + (domWeight * 0.5));
```

---

## 📡 NT8 API Hooks Required

### Phase 1
```csharp
OnMarketData(MarketDataEventArgs e)   // live price tick by tick
OnBarUpdate()                          // session open detection, ATR
SessionIterator                        // session start/end times
Draw.Rectangle()                       // zone rendering
Draw.Text()                            // % label rendering
ATR(period)                            // volatility input
```

### Phase 2 (additional)
```csharp
OnMarketDepth(MarketDepthEventArgs e)  // Level 2 DOM data
// Requires: Enable Level 2 in NT8 connection
// Requires: Require Bid/Ask in data series settings
MarketDepthRow                         // individual DOM rows
_bidBook / _askBook Dictionary         // order book tracking
```

---

## 🔗 Integration With Existing Indicators

| Indicator | Role | Relationship |
|---|---|---|
| `IQMainGPU.cs` | Candles, sessions, VWAP, order flow | Use alongside |
| `IQKeyLevelsGPU.cs` | Static key levels, POC, L2 walls | L2 code reference for Phase 2 |
| `BreakoutProbabilityExpo.cs` | Historical bar-close probabilities | Complementary (backward-looking) |
| `LiveProbabilityZones.cs` | **Forward probability live zones** | **This indicator** |
| `LiveProbabilityZonesEnhanced.cs` | Phase 2 full version | Planned |

---

## 💡 Usage Tips

- **Green zone below price** = high probability downside target → look for shorts
- **Green zone above price** = high probability upside target → look for longs
- **Red zone above price** = strong resistance, low % break → fade or wait
- **% dropping fast on a zone** = session running out of time, don't chase
- **Zone turns Gold mid-session** = price approaching, prepare for reaction
- **Combine with IQKeyLevelsGPU** POC clusters — zone + POC confluence = highest quality setups

---

## ⚠️ Disclaimer

This indicator is provided for educational and informational purposes only. It is not financial advice. Trading involves substantial risk of loss and is not suitable for all investors. Past performance does not guarantee future results.

---

## 📝 Version History

| Version | Date | Changes |
|---|---|---|
| **1.0** | 2026-08-02 | Phase 1 — base ATR + probability math, no L2 dependency |
| **2.0** | Planned | Phase 2 — L2 DOM + order flow + volume profile + GPU rendering |
