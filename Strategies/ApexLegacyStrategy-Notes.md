# ApexLegacyStrategy — Development Notes
**Instrument:** NQ / MNQ  
**Platform:** NinjaTrader 8  
**Version:** 1.0.1  
**Last Updated:** 2026-08-02  
**Trader:** Dollars1bySTEVE  

---

## ✅ Version 1.0.1 — What's Built (NY RTH)

### Bug Fixes
- **CS0246 Fixed:** Removed invalid `OnSessionChange(SessionChangedEventArgs e)` override
  — NT8 Strategy API does not expose this method. Daily reset is now handled
  via `DateTime` date-change detection inside `OnBarUpdate()`.
- Session parameters changed from `TimeSpan` properties to `int` Hour/Minute
  pairs for cleaner NT8 parameter dialog display.

### Signals
- **SignalsMA** — 9-period SMA cross & close (primary, more precise)
- **iTrend Pro** — LinReg(3) vs LinReg(5) direction approximation
  - ⚠️ NOTE: If ninZaiTrendPro (ninZa.co) indicator is licensed,
    replace the LinReg approximation with direct Plot value access
    for exact matching to chart arrows
- **Signal Mode:** EitherOne OR RequireBoth (user selectable)

### Order Management (Internal — No ATM Template Needed)
- 2 NQ contracts OR 10 MNQ contracts (auto-detected)
- Stop: 80 ticks (20pts)
- T1:   80 ticks (20pts)
- T2:  160 ticks (40pts)
- Breakeven move on T1 hit: ✅

### Execution Modes
- **FullAuto**   — fires entry orders automatically
- **SemiAuto**   — draws arrows + plays sound, manual confirm via Chart Trader
- **AlertOnly**  — signal detection only, no orders placed

### Apex Legacy $50k Compliance Engine
- Daily profit cap:  $800  → hard stop on new entries, on-chart message
- Daily loss limit:  $400  → hard stop on new entries, on-chart message
- Account floor:     $50,500 → trading locked below this, on-chart message
- Daily state resets automatically at start of each new trading day

### Session
- NY RTH: 9:30 AM – 3:30 PM ET (configurable via parameters)
- `IsExitOnSessionClose = true` — auto-flats at session close

---

## 🔮 Future Development — TODO List

### 1. Asia Session Mode
- Session hours: ~6:00 PM – 12:00 AM ET (previous day open)
- Key levels to filter: Asia 4TH IB High / Mid / Low
- Typically lower volatility — suggest tighter targets
  - Stop: 40t | T1: 30t | T2: none (or 40t)
- Separate DailyProfitLimit for Asia (suggest $400)
- Parameter: `EnableAsiaSession [True/False]`
- Parameter: `AsiaDailyProfitLimit [$]`

### 2. London Session Mode
- Session hours: ~3:00 AM – 8:30 AM ET
- Key levels: London Open Price, London POC, London IB H/L
- London Open hour often provides strong directional push
- Suggest full targets (80t / 160t) during London Open
- Parameter: `EnableLondonSession [True/False]`

### 3. Multi-Session Selector
```
SessionMode:
  ├── NYOnly       (current)
  ├── AsiaOnly
  ├── LondonOnly
  ├── LondonAndNY
  └── All
```

### 4. IQKeyLevels Confluence Filter
- Read IQKeyLevelsGPU plot values for proximity scoring
- Auto-select contract size based on score:
  - AT level        → Full size (2 NQ / 10 MNQ)
  - NEAR level      → Standard (1 NQ / 5 MNQ)
  - AWAY from level → Reduced or skip
- Parameter: `EnableKeyLevelFilter [True/False]`
- Parameter: `KeyLevelProximityTicks [int]` (default 20)

### 5. SharpEngineAllInOne Bias Integration
- Read Bull/Bear bias from SharpEngine indicator
- Only take longs when Bias = Bull
- Only take shorts when Bias = Bear
- Skip trades when Bias = Neutral
- Parameter: `UseSharpEngineBias [True/False]`

### 6. ninZaiTrendPro Direct Integration
- Replace LinReg(3/5) approximation with licensed indicator plots
- Access DMI+ vs DMI- crossover directly
- More accurate signal matching to visual chart arrows

### 7. Apex Consistency Rule Warning
- Track "best single day P&L" across sessions
- Warn when today's P&L approaches 30% of cumulative total P&L
- On-screen display: `Best Day: $X | Total: $X | Ratio: X%`
- Parameter: `EnableConsistencyWarning [True/False]`

### 8. Payout Tracker Panel
- On-chart panel showing:
  - Current account balance
  - Profit from $50,000 base
  - Distance to $52,600 first withdrawal threshold
  - Payout number (1–6 lifecycle)
  - Estimated next payout amount

### 9. ATM Profile Reference (Manual Trading Companion)
- For use alongside AlertOnly / SemiAuto modes:
  - **ApexBlueprint**  (20pt stop / 20pt T1 / 40pt T2) ← recommended
  - **X24020nq**       (10pt stop / 5pt T1  / 10pt T2)
  - **4030nq**         (10pt stop / 7.5pt T1)
  - **NQ2STEP**        (revised: 50pt stop / 25pt T1 / 75pt T2)

### 10. MNQ Precision Scaling Options
- Currently: closes exactly 5 of 10 MNQ at T1, 5 at T2
- Future: add flexible split options (e.g., 3+7, 4+6, 6+4)
- Parameter: `T1ContractPercent [int]` (default 50)

---

## 📐 Parameter Reference (v1.0.1)

| Parameter | Default | Group |
|---|---|---|
| ExecutionMode | SemiAuto | 1. Execution |
| SignalRequirement | EitherOne | 1. Execution |
| InstrumentSetting | AutoDetect | 1. Execution |
| StopTicks | 80 | 2. Order Management |
| T1Ticks | 80 | 2. Order Management |
| T2Ticks | 160 | 2. Order Management |
| MoveToBreakeven | true | 2. Order Management |
| SignalsMAPeriod | 9 | 3. Signals |
| EnableCompliance | true | 4. Apex Compliance |
| DailyProfitLimit | 800 | 4. Apex Compliance |
| DailyLossLimit | 400 | 4. Apex Compliance |
| AccountFloor | 50500 | 4. Apex Compliance |
| SessionStartHour | 9 | 5. Session |
| SessionStartMinute | 30 | 5. Session |
| SessionEndHour | 15 | 5. Session |
| SessionEndMinute | 30 | 5. Session |

---

## 📦 Installation

```
1. Copy ApexLegacyStrategy.cs to:
   Documents\NinjaTrader 8\bin\Custom\Strategies\

2. In NT8: Tools > NinjaScript Editor
   → Tools > Compile NinjaScript (F5)
   → Verify: 0 errors, 0 warnings

3. Add to chart:
   Right-click chart → Strategies → ApexLegacyStrategy
   Configure parameters in the dialog

4. Recommended chart: NQ or MNQ, 5-Minute
   with iTrend Pro + SignalsMA already on chart for visual reference
```

---

## 🗒️ Trader Notes
- SignalsMA is more precise than iTrend — treat as primary entry signal
- iTrend best used as directional background filter / confirmation
- Keep best single day P&L under $800 to avoid Apex consistency rule issues
- 10 MNQ preferred over 2 NQ for precision scaling flexibility
- Apex Blueprint HTML file (Apex-Legacy-Blueprint.html) contains full payout rules
- Strategy acts as its own ATM — do NOT attach an ATM template when running FullAuto
