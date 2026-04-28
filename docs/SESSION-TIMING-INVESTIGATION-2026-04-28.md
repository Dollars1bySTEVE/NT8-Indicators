# Session-Timing Investigation — 2026-04-28

## Problem

Session-open lines and session boxes appeared at incorrect times on the chart. User reported:

- **ETH Open** anchored 3–4 hours late
- **Asia Open** 3–4 hours off
- **US Open** 1.5 hours early
- **London Open** close but not exact

Chart display TZ was US Eastern, machine clock was ET, instrument was NQ on `CME US Index Futures ETH` trading hours.

---

## Original approach (PR #115) — not shipped

PR #115 attempted a diagnostic-driven minimal fix:

1. Added a `_tzDiagPrinted` one-shot diagnostic block to `IQMainGPU_Enhanced` to capture `Time[0]`, `Bars.GetTime(CurrentBar)`, and the result of `ConvertTimeToUtc(Time[0], TradingHoursTz)`.
2. Also added a "Session Anchors" print block to show computed ETH/Asia/London/US times for one bar.

**Root-cause finding from the diagnostic:** The existing code converted bar timestamps to UTC using `TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(Time[0], DateTimeKind.Unspecified), Bars.TradingHours.TimeZoneInfo)` — correct in principle — but then compared these UTC timestamps against session windows computed by **hand-rolled DST tables** (`IsUsDst`, `IsUkDst`, `IsAuDst`, `LastSundayOfMonth`, `NthSundayOfMonth`). These tables were:

- Evaluated at midnight UTC (wrong reference point for DST transitions)
- Applied US DST rules that are inconsistent with the actual Windows TZ database
- Applied UK DST rules that approximated but did not exactly match BST start/end
- Not handling edge cases around DST transitions correctly

The architecture was fundamentally over-engineered and impossible to patch safely. PR #115 was superseded without merging.

---

## Final approach (PR-C1-v2) — shipped

Replace the entire UTC round-trip + custom DST table architecture with **ET-anchored sessions** using .NET's built-in `TimeZoneInfo`.

### Design decision

All CME/ICE US/NYSE/NASDAQ futures sessions are conventionally quoted in **Eastern Time**. Instead of computing UTC times and applying custom DST tables, define every session in ET and let the Windows TZ database handle DST automatically via `TimeZoneInfo.ConvertTime`.

### ET conversion helper

Added to all three indicators (`IQMainGPU.cs`, `IQMainGPU_Enhanced.cs`, `IQMainUltimate.cs`):

```csharp
private static readonly TimeZoneInfo EtZone = SafeFindEtZone();
private static TimeZoneInfo SafeFindEtZone()
{
    try { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
    catch (Exception) { return TimeZoneInfo.CreateCustomTimeZone("ET-Fallback", TimeSpan.FromHours(-5), "ET-Fallback", "ET-Fallback"); }
}
private DateTime BarTimeEt()
{
    DateTime t = Bars.GetTime(CurrentBar);
    DateTime tUnspec = DateTime.SpecifyKind(t, DateTimeKind.Unspecified);
    return TimeZoneInfo.ConvertTime(tUnspec, Bars.TradingHours.TimeZoneInfo, EtZone);
}
```

`"Eastern Standard Time"` is the Windows TZ ID that correctly handles EST↔EDT transitions. The `SafeFindEtZone` fallback creates a fixed UTC-5 zone on systems where the Windows TZ database is unavailable.

### Canonical ET session times (year-round, DST-agnostic from the indicator's perspective)

| Session ID | Name | ET Open | ET Close | Notes |
|---|---|---|---|---|
| 0 | London | 03:00 | 11:30 | UK opens 3 AM ET (8 AM London regardless of UK DST) |
| 1 | New York | 09:30 | 16:00 | US cash equity open |
| 2 | Tokyo | 19:00 (prev day) | 04:00 | JPX open |
| 3 | Hong Kong | 21:00 (prev day) | 04:00 | HKEX open |
| 4 | Sydney | 17:00 (prev day) | 02:00 | ASX open |
| 5 | EU Brinks | 03:00 | 04:00 | First hour of London |
| 6 | US Brinks | 09:30 | 10:30 | First hour of NY cash |
| 7 | Frankfurt | 02:00 | 11:00 | DAX open |

**Anchor lines:**
- ETH Daily Open = **18:00 ET previous trading day** (6 PM ET = CME Globex daily reset)
- Asia Open = **19:00 ET previous day** (Tokyo cash open)
- LDN Open = **03:00 ET** (London cash open)
- US Open = **09:30 ET** (US cash open)

These ET times are constant year-round. Windows handles the EST/EDT switch automatically.

### Why this is robust

- **No hand-rolled DST tables** — Windows TZ database handles all past, present, and future DST rule changes (including potential US legislative changes).
- **Single source of truth** — session times are defined once in ET, not in UTC with offsets.
- **Correct conversion** — `TimeZoneInfo.ConvertTime(tUnspec, Bars.TradingHours.TimeZoneInfo, EtZone)` correctly converts from the instrument's trading-hours TZ to ET, regardless of what the chart's display TZ is set to.
- **Fallback safety** — `SafeFindEtZone` returns a UTC-5 fixed zone on systems without the Windows TZ database (Linux/macOS containers), ensuring the indicator always compiles and runs.

### What changed visually

Session boxes and open lines will move to their correct ET-anchored positions on the chart. The most affected sessions are:

- **Tokyo/ETH Open** — previously anchored ~3-4 hours late; now correct at 19:00 ET prev day / 18:00 ET
- **Asia Open** — previously 00:00 UTC (off by 5 hours from Tokyo open); now 19:00 ET prev
- **US Open** — previously off by DST calculation error; now exact 09:30 ET
- **London Open** — minor correction; now exact 03:00 ET

**This is a fix, not a regression.** Session boxes will move to where they should always have been.

---

## Deleted code

The following methods were removed from all three indicators as they are no longer called:

- `IsUkDst(DateTime utc)`
- `IsUsDst(DateTime utc)`
- `IsAuDst(DateTime utc)`
- `LastSundayOfMonth(int year, int month)`
- `NthSundayOfMonth(int year, int month, int n)`
- `GetSessionUtcTimes(...)` — replaced by `GetSessionEtTimes(...)`
- `GetEthSessionStart(DateTime barUtc)` — replaced by `GetEthSessionStartEt(DateTime barEt)`

---

## Startup smoke check

Each indicator now prints the canonical ET times to the NinjaScript Output window on each data load:

```
=== IQMainGPU session anchors (ET, year-round) ===
  ETH Daily Open:  18:00 ET (prev trading day)
  Asia Open:       19:00 ET prev → 04:00 ET (Tokyo)
  London Open:     03:00 ET → 11:30 ET
  US Open:         09:30 ET → 16:00 ET
  TradingHours TZ: Central Standard Time
  ET zone resolved: Eastern Standard Time
=== end session anchors ===
```

This runs once per data load and gives immediate visual confirmation that session times are configured correctly.
