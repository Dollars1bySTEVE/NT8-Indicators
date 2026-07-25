# SharpEngineAllInOne

`SharpEngineAllInOne` is a SharpDX-based NinjaTrader 8 overlay indicator that combines:

- Higher-timeframe directional background shading
- Level 2 liquidity wall visualization
- Simple order-flow-style reversal arrow signals
- On-chart HUD status text

## Data Series Configuration

The indicator now exposes both secondary confirmation series in the UI under **3. Data Series Settings**:

- **HTF Bars Period Type** (default: `Minute`)
- **HTF Bars Value** (default: `240`)
- **Confirm Bars Period Type** (default: `Tick`)
- **Confirm Bars Value** (default: `80`)
- **Swing Strength** (default: `5`)

> Note: Selecting `Renko` uses NinjaTrader's **native** `BarsPeriodType.Renko`.
> Custom add-on bar types (for example NinjaRenko/UniRenko) are intentionally not supported so this indicator stays dependency-free in a stock NT8 install.
