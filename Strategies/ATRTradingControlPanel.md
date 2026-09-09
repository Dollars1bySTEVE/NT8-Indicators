# ATRTradingControlPanel

NinjaTrader 8 unmanaged-order strategy that adds an on-chart **ATR CONTROL PANEL** for discretionary execution with ATR-sized stops, reward:risk targets, commission-aware sizing, pending entries, and automatic OCO brackets.

> **Live-order disclaimer:** this strategy can place real orders. Test in **Sim** first and verify all broker, routing, exchange, and platform fees yourself before using it on a live account.

## What it does

- Reads the built-in `ATR(...)` value live from the chart
- Converts ATR × multiplier into a tick-rounded stop distance
- Sizes quantity from **Max Loss Per Trade ($)**
- Includes round-turn friction: commission + exchange/clearing fees
- Shows the live trade plan on the chart before entry
- Supports:
  - **BUY MKT**
  - **SELL MKT**
  - **BUY LIMIT @**
  - **SELL LIMIT @**
  - **BUY STOP @**
  - **SELL STOP @**
- On fill, submits a stop-market + limit-target OCO bracket automatically
- Leaves the trader free to manually cancel/replace the strategy bracket or flatten the position after entry

## Install

1. Copy `Strategies/ATRTradingControlPanel.cs` into `Documents\NinjaTrader 8\bin\Custom\Strategies\`
2. Open **NinjaScript Editor**
3. Right-click the **Strategies** folder
4. Click **Compile**
5. Add **ATRTradingControlPanel** to a chart from the Strategies window

## Enable on a chart

1. Open a chart for the instrument you want to trade
2. Add the **ATRTradingControlPanel** strategy
3. Keep the strategy **Enabled**
4. Wait for **Realtime** status before using any entry button
5. Confirm the panel shows the expected ATR, stop, target, and quantity

## Settings

### Risk engine

- **ATR Period** — ATR lookback length. Default `14`.
- **ATR Multiplier** — stop distance = ATR × multiplier. Default `1.5`.
- **Reward:Risk** — target distance = stop distance × reward:risk. Default `2.0`.
- **Max Loss Per Trade ($)** — sizing budget used for quantity. Default `$100`.

### Friction / fees

- **Commission per side per contract ($)** — one-way commission input used in true-risk math.
- **Exchange/clearing fees per side per contract ($)** — one-way fee input used in true-risk math.
- **Fee Preset** — example-only commission presets:
  - `Custom`
  - `NinjaTraderLifetime`
  - `NinjaTraderFree`
  - `TradovateFree`
  - `Manual`
- **Instrument Fee Preset** — example-only exchange-fee presets for:
  - `ES`, `MES`, `NQ`, `MNQ`, `YM`, `MYM`, `RTY`, `M2K`, `CL`, `MCL`, `GC`, `MGC`

> Presets are only convenience examples. They are **approximate**, can change, and must be verified against your current broker / exchange / routing schedule.

### Sizing clamps

- **Min Quantity** — lower hard clamp on computed quantity. Default `1`.
- **Max Quantity** — upper hard clamp on computed quantity. Default `10`.
- If true risk for one contract is already above your max-loss budget, or if your minimum quantity would exceed that budget, the strategy blocks the trade and shows:
  - `Risk too small for 1 contract: true risk/contract = $X`

### Panel / execution behavior

- **Show plan lines on chart** — shows projected long/short entry, stop, and target lines while flat.
- **Use ATR at fill for pending orders** — pending order quantity is frozen at placement, but stop/target distance can be recalculated at fill from the then-current ATR.
- **Allow Multiple Entries** — default `false`. When on, the strategy only allows additional entries in the **same** direction as the current position/working plan.
- **Exit On Session Close** — exposed for traders who want NT8 session-close exit behavior. Default `false`.

## Calculation engine

The strategy follows this sizing model:

```text
atr                = ATR(AtrPeriod)[0]
stopDistancePts    = RoundToTick(atr * AtrMultiplier)   (minimum 1 tick)
stopTicks          = stopDistancePts / TickSize
targetDistancePts  = RoundToTick(stopDistancePts * RewardRisk)
grossRiskPerCt     = stopTicks * PointValue * TickSize
frictionPerCt      = 2 * (commissionPerSide + feesPerSide)
trueRiskPerCt      = grossRiskPerCt + frictionPerCt
quantity           = floor(MaxLoss / trueRiskPerCt), then clamped by Min/Max Quantity
trueRewardPerCt    = targetTicks * tickValue - frictionPerCt
trueRR             = trueRewardPerCt / trueRiskPerCt
totalRisk          = quantity * trueRiskPerCt
totalReward        = quantity * trueRewardPerCt
```

All stop/target prices are rounded with `Instrument.MasterInstrument.RoundToTickSize(...)`.

## Worked sizing example

**MNQ** (`TickSize = 0.25`, `$0.50/tick`)

- ATR = `12.0` points
- ATR Multiplier = `1.5`
- Stop = `18.0` points = `72` ticks
- Gross risk = `72 × $0.50 = $36.00`
- Commission = `$0.35/side`
- Fees = `$0.37/side`
- Friction = `2 × (0.35 + 0.37) = $1.44`
- True risk per contract = `$36.00 + $1.44 = $37.44`
- Max loss = `$100`
- Quantity = `floor(100 / 37.44) = 2`
- Total risk = `2 × 37.44 = $74.88`
- Reward:Risk = `2.0`
- Target = `36.0` points = `144` ticks
- Gross reward = `144 × $0.50 = $72.00`
- Net reward per contract = `$72.00 - $1.44 = $70.56`
- True R:R ≈ `70.56 / 37.44 = 1.88`

## Pending-order ATR tradeoff

Pending orders always keep the **entry quantity** that was computed when the order was placed.

- If **Use ATR at fill for pending orders = true**
  - the order keeps its original quantity
  - stop/target distances are recalculated from the ATR value at fill time
- If **Use ATR at fill for pending orders = false**
  - both quantity and stop/target distances stay frozen from placement time

This is a tradeoff between adapting to fill-time volatility and keeping the original preplanned distances unchanged.

## Buttons

- **BUY MKT / SELL MKT** — recompute the live ATR plan and submit a market entry
- **BUY LIMIT @ / SELL LIMIT @** — submit a pending limit entry at the price textbox value
- **BUY STOP @ / SELL STOP @** — submit a pending stop entry at the price textbox value
- **CANCEL PENDING** — cancels working entry orders from this strategy
- **FLATTEN** — cancels working orders from this strategy and submits a market order to flatten the current net position

Buttons are disabled when the strategy is not in **Realtime**. Entry buttons are also disabled when the computed quantity is `0`.

## Known limitations

- NinjaTrader 8 is required; this repository does not compile or run the strategy in CI.
- The panel uses standard NT8 chart-hosted WPF controls, so it must be attached to a charted strategy instance.
- Fee presets are examples only, not authoritative billing data.
- Quantity is fixed when a pending entry is placed; only the optional ATR-at-fill distance recalculation can change later.
- Manual scale-outs or other external partial exits are **not** automatically reconciled into a resized strategy bracket in this version; if you scale out manually, cancel or replace the remaining strategy bracket yourself.
- Advanced scale-outs, trailing-stop automation, and multi-target management are intentionally out of scope for this version.

## Recommended workflow

1. Start in **Sim**
2. Verify instrument tick size and point value
3. Verify commission and fee inputs
4. Verify the live plan lines match your intended setup
5. Test market and pending entries on replay / Sim before using live
