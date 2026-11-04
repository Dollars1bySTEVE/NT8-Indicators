# WeeklyLevelsFast

Standalone NinjaTrader 8 weekly reference levels, with background volume-profile
processing rather than an extra synchronous tick series in the chart.

**Status: first source version. Native NT8 compilation, chart behavior and startup
performance still need verification on Windows.** This is a new implementation of
the visible settings, not a numerical clone of a proprietary indicator.

## Features

- Weekly IB high/mid/low with configurable `HH:MM` duration; default `30:00`
  means **30 hours**, not 30 minutes.
- Extensions at +/-50%, 100%, 150% and 200% of the IB range beyond its boundaries.
- Current-week open, high/low midpoint, VAH/VAL/VPOC and VWAP.
- Prior-week high/low/close, open, midpoint and VAH/VAL/VPOC.
- Right-edge lines and labels; configurable plot colors, widths and dash styles.
- Optional right-edge IB/value-area highlights and VA-midpoint-as-POC lines.
- Historical trade-volume mode plus an explicitly labeled 1-minute estimate mode.
- No Level II dependency, trading signals, orders or all-in-one dashboard.

## Download and install

1. Open [`WeeklyLevelsFast.cs`](../WeeklyLevelsFast.cs) and use GitHub's
   **Download raw file** button. Download only this source file.
2. With NinjaTrader closed, copy it into
   `Documents\NinjaTrader 8\bin\Custom\Indicators\` (or your redirected user-data
   directory).
3. Open **New > NinjaScript Editor**, press **F5**, and check compilation errors.
4. Add **WeeklyLevelsFast** to an intraday price chart, initially in simulation.
   Match your intended instrument/contract and **Trading Hours** template.
5. Leave **Calculate = On each tick**; the refresh interval controls publication
   frequency and defaults to 500 ms. Auto scale defaults off.

The GitHub repository ZIP is not a NinjaScript import archive. Do not copy the
test program or other repository indicators into NT8 just to install this one.

## Why startup should do less repeated work

Basic chart-bar price previews are separate from an asynchronous historical-data
request. A worker aggregates source data incrementally, publishes snapshots at a
controlled interval, and caches eligible completed prior-week profiles locally.
Rendering reads the snapshot; it does not calculate profiles or create drawings
for every historical bar.

**First-time tick downloads and current-week tick reloads can still take time.**
Underlying NT8/provider data loading is shared and retains the requested tick
bars. This design does not establish a measured platform speedup by itself.
MinuteEstimate is faster to load but cannot reproduce exact trade profiles/VWAP.
Missing trade data is never silently replaced by estimated data.

## Calculation rules and limitations

The chart's trading-hours template supplies exchange trading dates. The week is
anchored to Monday's trading-day start (often Sunday evening for futures); IB
duration counts elapsed clock hours, including breaks. VA expands one tick at a
time from the highest-volume tick toward the heavier adjacent side until the
selected percentage is covered; ties choose the lower price.

Only current right-edge levels are rendered, not historical weekly trails.
Unseen vendor features such as proximity modes and price-scale label matching
are not implemented. Provider history gaps, holidays, daylight-saving changes,
connection lifecycle and cache invalidation need native acceptance checks.

See [INSTALL.txt](INSTALL.txt) for exact rules, cache management, data requirements,
known limitations and the full native acceptance checklist.

## Portable developer checks

From the repository root with .NET SDK 10:

```sh
dotnet run --project tests/WeeklyLevelsFast/WeeklyLevelsFast.Tests.csproj -c Release
```

The checks cover IB boundaries/extensions, VWAP, value-area coverage, developing
bar replacement, rollover and cache validation; they also parse the full source
as C# 5 and exercise one million synthetic trades. They do **not** compile
against NinjaTrader assemblies or verify the NT8 data/rendering lifecycle.
