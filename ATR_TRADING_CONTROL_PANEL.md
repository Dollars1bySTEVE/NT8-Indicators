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

## Project Scope

This project is a **NinjaTrader 8 trade execution/control panel**.

It is intended to be:

- a chart-based execution tool
- a commission-aware risk calculator
- an ATR-based trade planner
- a market and pending order control panel

It is **not** intended to be:

- a signal generator
- a strategy-only automation system
- a backtesting framework
- a portfolio-level risk manager
- a general-purpose indicator unrelated to trade execution

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
- optional slippage buffer, if enabled

This allows the tool to calculate **true risk** and **true reward:risk**, rather than stop-only approximations.

### Example logic

- Gross risk = stop distance converted to dollar terms
- Friction cost = commissions + fees
- True risk per contract = gross risk + friction cost
- Quantity = Max Loss per Trade ÷ True risk per contract

---

## Execution Rules and Assumptions

This project should explicitly define a few execution rules so behavior stays consistent:

### ATR Lock Behavior
The tool should support a choice between:

- **Lock ATR at order creation**
- **Recalculate ATR on fill**

Recommended default:

- Market orders: use ATR at click time
- Pending orders: recalculate ATR at fill time unless the user locks it on submit

### Entry Source
The tool should support at least one clear chart-based entry source, with future room for others:

- chart click
- draggable entry line
- manual order panel input
- Chart Trader integration

### Supported Order Types
The initial build should clearly define whether it supports:

- market entries
- limit entries
- stop-market entries
- stop-limit entries
- bracket / OCO attachment after fill

### Instrument Math
The tool must account for instrument-specific contract math such as:

- tick size
- tick value
- point value
- micros vs minis
- futures contract differences

### Rounding Rules
The tool should handle rounding and validation consistently:

- round quantity down
- reject zero-size trades
- enforce valid tick increments for stop/target prices
- warn if the calculated size is below minimum tradable quantity

### Risk Warnings
The tool should warn when:

- commissions push the trade beyond the max loss limit
- ATR is too wide for the chosen risk budget
- quantity rounds down to zero
- the pending order fills far from the original setup assumptions
- slippage would move realized loss outside the intended limit

### Pending Order Edge Cases
The tool should define behavior for:

- partial fills
- cancellation before fill
- ATR changing significantly before fill
- market moving sharply before the bracket is attached

### Slippage Handling
The tool should define whether slippage is:

- ignored
- estimated with a buffer
- included as a configurable input

Recommended default:

- include a configurable slippage buffer, disabled unless the user turns it on

---

## Defaults

A planning document is more useful when the starting assumptions are clear.

Suggested defaults:

- ATR Period: `14`
- ATR Multiplier: `1.5`
- Reward:Risk: `1:2`
- Max Loss per Trade: user-defined
- Commission handling: enabled
- Slippage buffer: optional / off by default
- Pending order behavior: recalculate on fill unless locked

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

## After Entry

The tool should standardize **entry risk**, not dictate every part of trade management.

It should handle:

- entry
- stop
- target
- size
- true risk calculations

After entry, the trader should still be free to manage the trade their own way.

Future support may include:

- scale-outs
- trailing stops
- breakeven moves
- runners
- multiple targets

---

## What this is not

This is not intended to be just:

- a risk calculator
- a chart ruler
- a target projection line tool
- a sizing helper only
- a full portfolio manager
- a signal generator

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
