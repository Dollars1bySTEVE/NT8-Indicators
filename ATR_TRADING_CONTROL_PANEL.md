# ATR TRADING CONTROL PANEL

ATR TRADING CONTROL PANEL is a planned NinjaTrader 8 trade execution and risk management tool designed to standardize trade risk while adapting to market volatility.

The goal is simple:

- Keep **dollar risk per trade constant**
- Let the **stop adapt to ATR / volatility**
- Let **position size adjust automatically**
- Let the **target follow the chosen Reward:Risk**
- Include **fees and commissions** for true risk/reward calculations
- Support both **market orders** and **pending orders**
- Allow the trader to still manage the trade afterward with scale-outs, trailing stops, or custom exits

---

## Why this exists

A trading plan holds up not because the stop stays the same in every condition, but because the **dollar risk per trade stays the same** regardless of the condition.

When the stop moves with ATR and position size moves opposite it to hold the risk constant, a trader can stay disciplined without giving up responsiveness to the market.

ATR TRADING CONTROL PANEL is intended to automate the calculations behind that workflow so the trader does not need to repeat the same math every time a setup appears or every time an order fills.

---

## Core Inputs

The tool is centered around three primary settings:

### 1) ATR Multiplier
How far the stop should sit relative to current volatility.

- Example: `1.5`
- Example: `2.0`

### 2) Reward:Risk
How far the target should sit relative to the stop.

- Example: `1:2`
- Example: `1:3`

### 3) Max Loss per Trade
The maximum amount the trade is allowed to risk.

- Example: `$100`
- Example: `$250`

---

## Calculation Flow

The intended logic is:

1. Read the current ATR
2. Multiply ATR by the selected ATR Multiplier
3. Convert the stop distance into ticks / dollars per contract
4. Add commissions and fees
5. Calculate position size from Max Loss per Trade
6. Set the target automatically from the selected Reward:Risk
7. Submit or complete the trade with a bracket / OCO structure

---

## Trading Modes

### Market Order Mode
For traders who want immediate execution.

- User confirms the setup
- Tool calculates stop, target, and size
- Trade is submitted immediately with attached protection

### Pending Order Mode
For traders who want to plan ahead or step away from the screen.

- User defines the setup in advance
- Pending entry order remains active
- When the order fills, the tool uses the current ATR logic to size the trade and place stop / target automatically
- This allows the trade to complete without the trader needing to be present at the exact moment of fill

---

## Risk Logic

The tool is designed around the idea that:

- **Stop distance can change**
- **Position size should change opposite the stop**
- **Dollar risk should stay fixed**

That means:

- Lower ATR → tighter stop → more contracts may be allowed within the same risk limit
- Higher ATR → wider stop → fewer contracts may be allowed within the same risk limit

This is the core discipline model behind the tool.

---

## Commission and Fee Handling

A key requirement for this project is that risk calculations must include more than just price movement.

The tool should account for:

- exchange fees
- broker commissions
- platform or routing costs, if applicable
- account tier differences
- round-turn or per-side handling

This allows the tool to calculate **true risk** and **true reward:risk**, rather than stop-only approximations.

### Example logic

- Gross risk = stop distance converted to dollar terms
- Friction cost = commissions + fees
- True risk per contract = gross risk + friction cost
- Quantity = Max Loss per Trade ÷ True risk per contract

---

## Intended User Experience

The goal is to make execution feel like a single action:

- define the trade plan once
- let the tool handle the math
- click once on the chart
- execute with stop, target, and size already aligned to the plan

The trader should not have to repeatedly recalculate:

- ATR
- stop distance
- tick value
- contract size
- commissions
- target distance

---

## What this is not

This is not intended to be just:

- a risk calculator
- a chart ruler
- a target projection line tool
- a sizing helper only

It is intended to become a **risk-aware execution layer** for NinjaTrader 8.

---

## Planned Features

- ATR-based stop calculation
- Reward:Risk-based target calculation
- Max-loss-based position sizing
- Commission-aware sizing
- Market order support
- Pending order support
- Automatic post-fill bracket logic
- OCO order handling
- Support for discretionary trade management after entry
- Future support for scale-outs, trailing stops, and multiple targets

---

## Build Philosophy

The tool should standardize **entry risk**, not dictate every part of trade management.

That means the system should handle:

- entry
- stop
- target
- size
- true risk calculations

But after entry, the trader should still be free to manage the trade their own way.

---

## Development Notes

This README is a planning document for the future build of ATR TRADING CONTROL PANEL.

It is meant to stay in the repository as a reference point so the project can be revisited and implemented in stages.

Suggested implementation phases:

1. Trade calculation engine
2. Visual trade control panel
3. Market order execution
4. Pending order execution
5. Commission and fee presets
6. Bracket / OCO order automation
7. Trade management enhancements

---

## Status

Planned / not yet implemented.

---

## Working Title

**ATR TRADING CONTROL PANEL**

