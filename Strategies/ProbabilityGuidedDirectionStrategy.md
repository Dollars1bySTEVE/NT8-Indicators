# ProbabilityGuidedDirectionStrategy

NinjaTrader 8 strategy that outputs only **BUY**, **SELL**, or **NO TRADE** from a probability-guided composite model.

## What it does

- Builds one composite score from:
  - **Structure** (EMA trend + structure lookback context)
  - **Momentum** (ROC + RSI + bar body direction)
  - **Volume** (relative participation + directional pressure)
  - **Regime** (optional ADX trend/chop filter)
- Converts the composite score to a directional probability.
- Enforces quality gates before any actionable signal:
  - minimum probability threshold
  - minimum risk/reward threshold
  - off-hours filter (unless enabled)
  - component conflict filter

## Modes

- **SignalOnly**: visual/print guidance only (no orders).
- **Automation**: places orders with stop/target and risk controls.

## Automation risk controls

- max daily loss / max daily profit guardrails
- max trades per day
- cooldown bars between entries
- optional runner breakeven move

## Walk-forward validation support

- Tags each signal as **TRAIN** or **TEST** using `TrainEndDateYyyyMMdd`.
- Option to disable automation in TRAIN period while still logging signals.
- Logs each signal with component scores + gate states.
- Logs trade outcomes with signal context for win rate / expectancy analysis.

## Suggested validation workflow

1. Run in **SignalOnly** first and collect logs.
2. Calibrate weights/thresholds on TRAIN period.
3. Validate on TEST period without changing calibrated parameters.
4. Review by instrument and session (`RTH`/`OFF`) using log fields.
5. Promote to **Automation** only after stable out-of-sample behavior.

