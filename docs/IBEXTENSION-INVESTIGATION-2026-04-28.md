# IBExtension Branch Investigation — IQMainUltimate

*Date: 2026-04-28*
*File in scope: `Indicators/IQMainUltimate.cs` (7,415 lines)*

**Trigger:** Entry Mode dashboard rendered `Target: 27339.25 [VAH]` while `TargetMode = IBExtension` was set.

---

## 1. Enum Declarations

### `UltimateTargetMode` — lines 57–59

```csharp
/// <summary>Take-profit algorithm for IQMainUltimate Entry Mode.
/// Extends the base TargetPlacementMode with TPO-aware options.</summary>
public enum UltimateTargetMode { AutoDetected, PivotR1, PivotR2, ManualInput, VAH, IBExtension }
```

### `UltimateStopMode` — lines 53–55

```csharp
/// <summary>Stop-loss algorithm for IQMainUltimate Entry Mode.
/// Extends the base StopPlacementMode with a TPO-aware option.</summary>
public enum UltimateStopMode { AutoDetected, PivotBased, HVNBased, ManualInput, TPOBased }
```

Both enums are declared **outside all namespaces** (lines 46–59, before the `namespace NinjaTrader.NinjaScript.Indicators` block at line 61), per NT8 convention for custom enums.

Mapping to Enhanced's `TargetPlacementMode { AutoDetected, PivotR1, PivotR2, ManualInput }`: the first four values carry over; `VAH` and `IBExtension` are Ultimate-only additions.

---

## 2. Full `case UltimateTargetMode.IBExtension:` Branch

**Lines 4385–4414 verbatim:**

```csharp
case UltimateTargetMode.IBExtension:
    if (bearish)
    {
        // Bearish: target IB Low Extension if price is trending down
        if (tpoCurrentIBLowExt > 0 && tpoCurrentIBLowExt < close)
        {
            if (Math.Abs(close - tpoCurrentIBLowExt) <= maxDist)
                return tpoCurrentIBLowExt;
        }
        // Fallback to VAL
        if (tpoCurrentVAL > 0 && tpoCurrentVAL < close)
        {
            if (Math.Abs(close - tpoCurrentVAL) <= maxDist)
                return tpoCurrentVAL;
        }
        return close - GetAdrBasedTargetDistance();
    }
    // Bullish: Target the IB High Extension if price is trending up
    if (tpoCurrentIBHighExt > close)
    {
        if (Math.Abs(close - tpoCurrentIBHighExt) <= maxDist)
            return tpoCurrentIBHighExt;
    }
    // Fallback to VAH
    if (tpoCurrentVAH > close)
    {
        if (Math.Abs(close - tpoCurrentVAH) <= maxDist)
            return tpoCurrentVAH;
    }
    return close + GetAdrBasedTargetDistance();
```

**Fields read:**

| Field | Direction | Role |
|---|---|---|
| `tpoCurrentIBHighExt` | Bullish | Primary: IB High + 1× IBRange |
| `tpoCurrentIBLowExt` | Bearish | Primary: IB Low − 1× IBRange |
| `tpoCurrentVAH` | Bullish | Fallback: current session Value Area High |
| `tpoCurrentVAL` | Bearish | Fallback: current session Value Area Low |
| `maxDist` | Both | `MaxTPOTargetTicks * TickSize` (cap = 400 ticks, `private const`) |

**Cap logic:** `Math.Abs(close - level) <= maxDist` must pass for each candidate. There is **no `Print()` diagnostic** when the IB extension is skipped due to cap overflow (unlike `AutoDetected` and `PivotBased` branches, which do log skipped levels).

**Bullish fallback chain:**
1. `tpoCurrentIBHighExt` — if above price and within `MaxTPOTargetTicks`
2. `tpoCurrentVAH` — if above price and within `MaxTPOTargetTicks`
3. `close + GetAdrBasedTargetDistance()` — 30% of ADR, floored by `TargetDistanceTicks * TickSize`

---

## 3. IB Extension Field Assignments

### Field declarations — lines 495–496

```csharp
private double tpoCurrentIBHighExt;
private double tpoCurrentIBLowExt;
```

### Assignment in active session update block — lines 5819–5831

```csharp
// Bug 3: apply IB range sanity guard — suppress IB values if range exceeds 3× ADR
double ibHigh = bestActive.IBHigh > double.MinValue ? bestActive.IBHigh : 0;
double ibLow  = bestActive.IBLow  < double.MaxValue ? bestActive.IBLow  : 0;
double maxIBRange = adrValue > 0 ? adrValue * 3.0 : double.MaxValue;
if (ibHigh > 0 && ibLow > 0 && (ibHigh - ibLow) > maxIBRange)
{
    ibHigh = 0;
    ibLow  = 0;
}
tpoCurrentIBHigh    = ibHigh;
tpoCurrentIBLow     = ibLow;
tpoCurrentIBHighExt = (ibHigh > 0 && ibLow > 0) ? bestActive.IBHighExtension : 0;
tpoCurrentIBLowExt  = (ibHigh > 0 && ibLow > 0) ? bestActive.IBLowExtension  : 0;
```

Note the **IB range sanity guard**: if `ibHigh − ibLow > 3× ADR`, both `ibHigh`/`ibLow` are zeroed, which also zeroes both extension fields. This means a session with an anomalously wide Initial Balance produces `tpoCurrentIBHighExt = 0`, causing the IBExtension branch to skip straight to the VAH fallback silently.

### `CalculateIBExtensions` method — lines 5970–5975

```csharp
private void CalculateIBExtensions(TPOSession sess)
{
    if (sess.IBRange <= 0 || sess.IBHigh <= double.MinValue || sess.IBLow >= double.MaxValue) return;
    sess.IBHighExtension = sess.IBHigh + sess.IBRange * 1.0;
    sess.IBLowExtension  = sess.IBLow  - sess.IBRange * 1.0;
}
```

The extension is exactly **1× IBRange beyond the IB boundary** (a 100% extension). This method is called only at session finalization (line 5860 inside `FinalizeTPOSession`). For the live/active session the cached `bestActive.IBHighExtension` / `bestActive.IBLowExtension` are read directly from the session object.

---

## 4. Bullish Fallback When IB Extension Is Null or Beyond Cap

The bullish path (lines 4402–4414):

```csharp
// Bullish: Target the IB High Extension if price is trending up
if (tpoCurrentIBHighExt > close)                              // is it above price at all?
{
    if (Math.Abs(close - tpoCurrentIBHighExt) <= maxDist)     // within cap?
        return tpoCurrentIBHighExt;
}
// Fallback to VAH  ← NO Print(), NO log
if (tpoCurrentVAH > close)
{
    if (Math.Abs(close - tpoCurrentVAH) <= maxDist)
        return tpoCurrentVAH;                                  // silently returns VAH
}
return close + GetAdrBasedTargetDistance();
```

**Answer: the fallback is a silent delegation to `tpoCurrentVAH` (current session VAH).** It is not:
- (a) `TargetDistanceTicks * TickSize` manual distance
- (c) the `AutoDetected` branch (pivots/SR levels)

If VAH is also out of range the code falls further to `GetAdrBasedTargetDistance()` (30% ADR floored by `TargetDistanceTicks * TickSize`). No log or diagnostic is emitted at any fallback step in this branch.

---

## 5. The `[VAH]` / `[VAL]` / `[POC]` Tag — Where It Comes From

Tags are built in two dedicated methods called from the Entry Mode dashboard render path.

### Call site — lines 4870–4871

```csharp
string stopLabel   = BuildStopLabel(dashboardStopPrice, dashboardEntryPrice);
string targetLabel = BuildTargetLabel(dashboardTargetPrice, dashboardEntryPrice);
```

### `BuildTargetLabel` — lines 6134–6154

```csharp
private string BuildTargetLabel(double targetPrice, double entryPrice)
{
    string baseStr = string.Format("Target:     {0}  ({1:F0}t)",
        Instrument.MasterInstrument.FormatPrice(targetPrice),
        Math.Abs(targetPrice - entryPrice) / TickSize);

    if (tpoCurrentVAH > 0 && Math.Abs(targetPrice - tpoCurrentVAH) <= TickSize)
        return baseStr + "  [VAH]";
    if (tpoCurrentIBHighExt > 0 && Math.Abs(targetPrice - tpoCurrentIBHighExt) <= TickSize)
        return baseStr + "  [IB ext]";
    // Check if it matches a naked POC
    if (nakedTPOLevels != null)
    {
        foreach (var nl in nakedTPOLevels)
        {
            if (!nl.IsClosed && nl.LevelType == "POC" && Math.Abs(targetPrice - nl.Price) <= TickSize)
                return baseStr + string.Format("  [Naked POC {0}]", nl.SessionLabel);
        }
    }
    return baseStr;
}
```

### `BuildStopLabel` — lines 6115–6128

```csharp
private string BuildStopLabel(double stopPrice, double entryPrice)
{
    string baseStr = string.Format("Stop:       {0}  ({1:F0}t)",
        Instrument.MasterInstrument.FormatPrice(stopPrice),
        Math.Abs(entryPrice - stopPrice) / TickSize);

    if (tpoCurrentVAL > 0 && Math.Abs(stopPrice - tpoCurrentVAL) <= TickSize)
        return baseStr + "  [VAL]";
    if (previousDayTPO != null && previousDayTPO.ValueAreaLow > 0 &&
        Math.Abs(stopPrice - previousDayTPO.ValueAreaLow) <= TickSize)
        return baseStr + "  [prev VAL]";
    return baseStr;
}
```

**Key architectural point:** `BuildTargetLabel` does **not** inspect `TargetMode`. It receives only the returned `targetPrice` and pattern-matches it against known TPO levels by proximity (within 1 tick). The `[VAH]` check at line 6140 runs **before** the `[IB ext]` check at line 6142.

---

## 6. Verdict on `Target: 27339.25 [VAH]` with `TargetMode = IBExtension`

**Verdict: (a) — Silent Fallback**

The `IBExtension` branch ran and found the IB extension level unusable (zero, below price, or beyond `MaxTPOTargetTicks`), then silently delegated to `tpoCurrentVAH`.

### Exact execution trace

1. `CalculateDynamicTarget()` enters the `IBExtension` case (line 4385).
2. Bullish path: checks `tpoCurrentIBHighExt > close` (line 4403). This condition failed — either:
   - `tpoCurrentIBHighExt == 0` (IB range sanity guard zeroed it, or active session IB window not yet closed), **or**
   - `Math.Abs(close - tpoCurrentIBHighExt) > maxDist` (beyond 400-tick cap)
3. **No `Print()` is emitted.** The branch silently falls through.
4. `tpoCurrentVAH > close` (line 4409) — true. `Math.Abs(close - tpoCurrentVAH) <= maxDist` — true. Returns `tpoCurrentVAH = 27339.25`.
5. `BuildTargetLabel(27339.25, entryPrice)` runs (line 4871).
6. Line 6140: `Math.Abs(27339.25 - tpoCurrentVAH) <= TickSize` → true. Returns `"Target: 27339.25  [VAH]"`.
7. The `[IB ext]` check at line 6142 is never reached.

### The silent-fallback lines

```csharp
// Line 4408-4413 — no log, no flag, no indication to caller that IB ext was skipped
// Fallback to VAH
if (tpoCurrentVAH > close)
{
    if (Math.Abs(close - tpoCurrentVAH) <= maxDist)
        return tpoCurrentVAH;
}
```

The `[VAH]` label is **technically correct** for the price returned — it is not a labeling bug (option c). The price genuinely is the VAH. The problem is that the user's chosen mode `IBExtension` was bypassed without any indication, and the dashboard renders identically to what `TargetMode = VAH` would show.

### Why this is not option (b) or (c)

- **(b) smart override** would require the branch to compare `tpoCurrentIBHighExt` and `tpoCurrentVAH` and pick the closer one. The code has no such comparison — it is a strict priority waterfall.
- **(c) labeling bug** would require `BuildTargetLabel` to mislabel an IB extension price as VAH. The labeler correctly identified the price as VAH because VAH is what was actually returned.

---

## 7. `UltimateStopMode` Summary and `TPOBased` Stop Branch

### Enum — line 55

```csharp
public enum UltimateStopMode { AutoDetected, PivotBased, HVNBased, ManualInput, TPOBased }
```

### `TPOBased` stop branch — lines 4187–4214

```csharp
case UltimateStopMode.TPOBased:
    if (bearish)
    {
        // Bearish: stop above price — use VAH
        if (tpoCurrentVAH > 0 && tpoCurrentVAH > close)
        {
            if (Math.Abs(close - tpoCurrentVAH) <= maxDist)
                return tpoCurrentVAH;
        }
        if (previousDayTPO != null && previousDayTPO.ValueAreaHigh > 0 && previousDayTPO.ValueAreaHigh > close)
        {
            if (Math.Abs(close - previousDayTPO.ValueAreaHigh) <= maxDist)
                return previousDayTPO.ValueAreaHigh;
        }
        return close + GetAdrBasedStopDistance();
    }
    // Bullish: stop below price — use VAL
    if (tpoCurrentVAL > 0 && tpoCurrentVAL < close)
    {
        if (Math.Abs(close - tpoCurrentVAL) <= maxDist)
            return tpoCurrentVAL;
    }
    if (previousDayTPO != null && previousDayTPO.ValueAreaLow > 0 && previousDayTPO.ValueAreaLow < close)
    {
        if (Math.Abs(close - previousDayTPO.ValueAreaLow) <= maxDist)
            return previousDayTPO.ValueAreaLow;
    }
    return close - GetAdrBasedStopDistance();
```

**Behavior summary:**

| Direction | Priority 1 | Priority 2 | Final fallback |
|---|---|---|---|
| Bearish (stop above) | `tpoCurrentVAH` | `previousDayTPO.ValueAreaHigh` | `close + GetAdrBasedStopDistance()` (15% ADR floored by `StopDistanceTicks * TickSize`) |
| Bullish (stop below) | `tpoCurrentVAL` | `previousDayTPO.ValueAreaLow` | `close − GetAdrBasedStopDistance()` |

- No `Print()` diagnostic on cap overflow (same silent-fallback pattern as the `IBExtension` target branch).
- Unlike `AutoDetected`, this branch is VAH/VAL only — no liquidity zones, no pivot S1/R1. It is a deliberately narrow, TPO-pure mode.
- `maxDist` here is `MaxTPOStopTicks * TickSize` (`private const int MaxTPOStopTicks = 200`).

---

## 8. Related Known Issues (cross-reference to AUDIT-2026-04-28)

| Issue | Ref | Summary |
|---|---|---|
| `MaxTPOTargetTicks` not user-configurable | U5 | Hardcoded 400-tick cap silently rejects valid IB extensions on small-tick instruments |
| `IBExtension` fallback has no `Print()` diagnostic | — | User cannot distinguish "IB ext used" from "IB ext skipped, VAH used" without reading source |
| `previousDayTPO == null` silent degradation | U6 | All TPO-based stop/target modes fall back to ADR without warning when London+NY both disabled |
| `CalculateIBExtensions` called twice | U8 | Called in both `UpdateInitialBalance` and `FinalizeTPOSession`; redundant but harmless |

---

*Investigation completed 2026-04-28. All line numbers verified against the 7,415-line file at the time of this writing.*
