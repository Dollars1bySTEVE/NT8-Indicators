# Session-Open Timing Investigation — 2026-04-28

*Files affected:* `Indicators/IQMainGPU.cs`, `Indicators/IQMainGPU_Enhanced.cs`, `Indicators/IQMainUltimate.cs`
*PR:* PR-C1 (DRAFT — see checklist below)

---

## 1. Reported Symptoms

On NQ 06-26 (10-min bars, CME US Index Futures ETH trading hours, NT chart display timezone = US Eastern, machine clock = US Eastern):

| Label | Expected position (ET) | Observed position | Shift |
|-------|------------------------|-------------------|-------|
| ETH Open | 6:00 PM ET | ~9:30–10 PM ET | ~3.5 h late |
| Asia Open | 7:00 PM ET (Tokyo = midnight UTC) | ~midnight–2:30 AM ET column | ~5 h late |
| London Open | 3:00 AM ET (DST) | Close but not exact 3:00 AM | ~0–1 h off |
| US Open | 9:30 AM ET | ~8:00 AM ET column | ~1.5 h early |

The user confirmed:
- Chart timezone: `(UTC-05:00) Eastern Time`
- Trading hours template: `CME US Index Futures ETH`
- PC clock: US Eastern (correct)

---

## 2. Suspected Root Causes

### Bug 1 — `Time[0]` chart-display-timezone double-conversion

**Affected code (identical in all three indicators):**

```csharp
// OnBarUpdate session tracking block (IQMainGPU.cs ~line 2384, Enhanced ~2602, Ultimate ~2858)
DateTime barUtc = TimeZoneInfo.ConvertTimeToUtc(
    DateTime.SpecifyKind(Time[0], DateTimeKind.Unspecified),
    Bars.TradingHours.TimeZoneInfo);
```

**The bug:** In NinjaTrader 8, when the user sets a chart display timezone in *Tools → Options → General → Time zone*, `Time[0]` returns bar times **in that display timezone** (US Eastern), not in the trading-hours timezone (Central for CME futures). The code above then treats the Eastern-time value as if it were Central-time-unspecified and converts to UTC using the Central offset. This double-dips: it applies a second UTC conversion on a value that was already in the user's local timezone.

**Effect:** `barUtc` ends up shifted by the difference between the display TZ and the trading-hours TZ — typically 1 hour (ET vs CT), sometimes 1.5 hours when one has crossed a DST boundary the other hasn't yet.

**The fix:** Use `Bars.GetTime(barIndex)`, which always returns times in `Bars.TradingHours.TimeZoneInfo` regardless of the chart display timezone:

```csharp
private static DateTime BarTimeToUtc(NinjaTrader.Data.Bars bars, int barIndex)
{
    DateTime t = bars.GetTime(barIndex);
    return TimeZoneInfo.ConvertTimeToUtc(
        DateTime.SpecifyKind(t, DateTimeKind.Unspecified),
        bars.TradingHours.TimeZoneInfo);
}
```

All three indicators now call `BarTimeToUtc(Bars, CurrentBar)` instead of the old pattern.

---

### Bug 2 — DST evaluated at midnight UTC instead of session-start instant

**Affected code (identical in all three indicators):**

```csharp
// GetSessionUtcTimes (IQMainGPU.cs ~line 3662, Enhanced ~5045, Ultimate ~5593)
DateTime day = utcDate.Date;  // midnight UTC
case 0: { int offset = IsUkDst(day) ? -1 : 0; start = day.AddHours(8 + offset); ... }
case 1: { int offset = IsUsDst(day) ? -1 : 0; start = day.AddHours(14 + offset).AddMinutes(30); ... }

// GetEthSessionStart (IQMainGPU.cs ~line 3843, Enhanced ~5226, Ultimate ~5774)
int ethHour = IsUsDst(day) ? 22 : 23;  // day = midnight UTC

// UpdateEthAndRthOpens (IQMainGPU.cs ~line 3829, Enhanced ~5211, Ultimate ~5760)
int lndOffset = IsUkDst(day) ? 0 : 1;  // day = midnight UTC
int usOffset  = IsUsDst(day) ? 0 : 1;  // day = midnight UTC
```

**The bug:** `IsUsDst` and `IsUkDst` are evaluated on `day` (midnight UTC, 00:00), not on the actual session-start instant. Around DST transition Sundays, midnight UTC may be in the old DST state while the session opens in the new state (or vice versa). This can produce a 1-hour shift on sessions that start after the clock change.

**Combined with Bug 1**, the compounding of a ~1 h base error (Bug 1) with sporadic additional 1 h flips (Bug 2) explains the 3–5 hour shifts seen on Asia and ETH labels.

**The fix:** Evaluate `IsUsDst`/`IsUkDst` at the *tentative session-start UTC instant*, not at midnight:

```csharp
// GetSessionUtcTimes — London (case 0)
offset = IsUkDst(day.AddHours(8)) ? -1 : 0;   // check at 08:00 UTC

// GetSessionUtcTimes — New York (case 1)
offset = IsUsDst(day.AddHours(14).AddMinutes(30)) ? -1 : 0;   // check at 14:30 UTC

// GetEthSessionStart
int ethHour = IsUsDst(day.AddHours(22)) ? 22 : 23;   // check at 22:00 UTC

// UpdateEthAndRthOpens — London
int lndOffset = IsUkDst(day.AddHours(7)) ? 0 : 1;   // check at 07:00 UTC

// UpdateEthAndRthOpens — US
int usOffset = IsUsDst(day.AddHours(13).AddMinutes(30)) ? 0 : 1;   // check at 13:30 UTC
```

---

## 3. Phase 1 — TZ Diagnostic (IQMainGPU_Enhanced.cs only)

A one-shot diagnostic block is present in `IQMainGPU_Enhanced.cs` at the top of `OnBarUpdate`. It fires once on the first bar and prints to the NT8 Output window:

```
=== IQMainGPU_Enhanced TZ DIAGNOSTIC ===
Time[0] kind=Unspecified value=2026-04-28 09:30:00
Time[0].ToUniversalTime() = 2026-04-28 14:30:00Z
Bars.GetTime(CurrentBar) kind=Unspecified value=2026-04-28 09:30:00
Bars.GetTime(CurrentBar).ToUniversalTime() = 2026-04-28 14:30:00Z
Bars.TradingHours.TimeZoneInfo.Id = Central Standard Time
ConvertTimeToUtc(Time[0], TradingHoursTz) = 2026-04-28 15:30:00Z
=== END TZ DIAGNOSTIC ===
```

**How to read each line:**

| Line | What to look for |
|------|-----------------|
| `Time[0] kind=...` | Should say `Unspecified`. If it says `Local`, NT8 is auto-converting — unusual. |
| `Time[0].ToUniversalTime()` | If this differs from `ConvertTimeToUtc(Time[0], TradingHoursTz)` by exactly 1 hour, Bug 1 is confirmed. |
| `Bars.GetTime(CurrentBar)` | Value should equal `Time[0]` when chart TZ = trading-hours TZ; may differ by 1 h if display TZ ≠ trading-hours TZ. |
| `Bars.GetTime(CurrentBar).ToUniversalTime()` | This is the correct UTC bar time. Compare to `ConvertTimeToUtc(Time[0], TradingHoursTz)`. If they differ, Bug 1 is confirmed and `BarTimeToUtc` (using `Bars.GetTime`) is the correct fix. |
| `Bars.TradingHours.TimeZoneInfo.Id` | Should be `"Central Standard Time"` for CME futures. If it says `"Eastern Standard Time"`, Bug 1 may be smaller than expected (only DST-gap periods would be affected). |
| `ConvertTimeToUtc(Time[0], TradingHoursTz)` | The *old* (buggy) computation. Compare to `Bars.GetTime(CurrentBar).ToUniversalTime()` — any difference is the magnitude of Bug 1. |

> **TODO for maintainer:** After user pastes the diagnostic output, verify that  
> `Bars.GetTime(CurrentBar).ToUniversalTime()` matches the known correct UTC time for that bar.  
> If confirmed correct, the `BarTimeToUtc` fix in Phase 2 is validated and this diagnostic block  
> should be **removed** before merging to main.

---

## 4. Phase 2 — `BarTimeToUtc` Helper (all three indicators)

Added to `IQMainGPU.cs`, `IQMainGPU_Enhanced.cs`, and `IQMainUltimate.cs` just before `GetSessionUtcTimes`:

```csharp
private static DateTime BarTimeToUtc(NinjaTrader.Data.Bars bars, int barIndex)
{
    DateTime t = bars.GetTime(barIndex);
    return TimeZoneInfo.ConvertTimeToUtc(
        DateTime.SpecifyKind(t, DateTimeKind.Unspecified),
        bars.TradingHours.TimeZoneInfo);
}
```

Every `barUtc` computation in `OnBarUpdate` now calls:

```csharp
DateTime barUtc = BarTimeToUtc(Bars, CurrentBar);
```

---

## 5. Phase 3 — DST at Session-Start Instant (all three indicators)

`GetSessionUtcTimes`, `GetEthSessionStart`, and `UpdateEthAndRthOpens` all updated to evaluate DST helpers at the session-start instant rather than at midnight UTC. See §2 above for exact before/after code.

---

## 6. Phase 4 — Startup Smoke Check (all three indicators, permanent)

A one-shot smoke check runs on the first bar of each data load. Example output for a US-DST-active (EDT) day:

```
=== IQMainGPU_Enhanced session anchors for 2026-04-28 ===
  ETH Open:    22:00Z = 18:00 ET
  Asia Open:   00:00Z = 20:00 ET
  London Open: 07:00Z = 03:00 ET
  US Open:     13:30Z = 09:30 ET
=== END IQMainGPU_Enhanced session anchors ===
```

Expected anchor times (US EDT active, i.e. UTC-4):

| Session | UTC anchor | ET anchor |
|---------|------------|-----------|
| ETH Open | 22:00Z | 18:00 ET (6 PM) |
| Asia (Tokyo) Open | 00:00Z | 20:00 ET (8 PM prev. day) |
| London Open | 07:00Z | 03:00 ET (3 AM) |
| US Open | 13:30Z | 09:30 ET (9:30 AM) |

Expected anchor times (US EST active, i.e. UTC-5):

| Session | UTC anchor | ET anchor |
|---------|------------|-----------|
| ETH Open | 23:00Z | 18:00 ET (6 PM) |
| Asia (Tokyo) Open | 00:00Z | 19:00 ET (7 PM prev. day) |
| London Open | 08:00Z | 03:00 ET (3 AM) |
| US Open | 14:30Z | 09:30 ET (9:30 AM) |

If the ET anchor columns do not match these expected values, re-investigate the DST boundaries in `IsUsDst` / `IsUkDst`.

---

## 7. PR-C1 Checklist

- [x] `BarTimeToUtc(Bars, CurrentBar)` helper added to all three indicators
- [x] All `barUtc` call sites in `OnBarUpdate` updated to use `BarTimeToUtc`
- [x] `GetSessionUtcTimes` — DST evaluated at session-start instant (all three)
- [x] `GetEthSessionStart` — DST evaluated at session-start instant (all three)
- [x] `UpdateEthAndRthOpens` — London/US DST evaluated at session-start instant (all three)
- [x] Phase 1 diagnostic present in `IQMainGPU_Enhanced.cs` only
- [x] Phase 4 smoke check present in all three indicators (permanent)
- [x] `docs/AUDIT-2026-04-28.md` updated with PR-C1 row
- [ ] **Awaiting user TZ diagnostic output** — user must run `IQMainGPU_Enhanced` and paste  
      the `=== IQMainGPU_Enhanced TZ DIAGNOSTIC ===` block from the NT8 Output window.  
      Compare `Bars.GetTime(CurrentBar).ToUniversalTime()` vs `ConvertTimeToUtc(Time[0], TradingHoursTz)`  
      to confirm Bug 1 magnitude before merging to Main.
- [ ] After diagnostic confirmed: remove `_tzDiagPrinted` block from `IQMainGPU_Enhanced.cs`
- [ ] Live verification: smoke check ET times match expected table in §6 above
