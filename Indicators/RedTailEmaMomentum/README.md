# RedTailEmaMomentum

An independent NinjaTrader 8 indicator, not a modification of protected SignalsMA.
It draws up to three EMAs and arrows on the price panel and sends native NT8
alerts with a configurable long/short WAV sound.

## Installation

In the NinjaScript Editor create an indicator named `RedTailEmaMomentum`.
Replace its generated contents with `../RedTailEmaMomentum.cs`, compile with F5,
then add **RedTailEmaMomentum** to the chart. NT8 generates its own indicator
accessor code when compiling. Keep **Calculate = On each tick**.

## Settings and exact signal rules

Defaults: three EMAs (9, 21, 50), EMA crossover mode, intrabar timing,
RSI(14) midpoint 50, arrows offset 10 ticks, audio enabled.

EMA periods must increase from fast to middle to slow. Disabled EMAs are not
plotted or used. One EMA is supported in PriceCross mode; EmaCrossover requires
two or three.

| Mode | Long signal | Short signal |
|---|---|---|
| EmaCrossover | Fast EMA crosses above middle; if enabled, price must also be above slow EMA | Fast crosses below middle; if enabled, price must also be below slow EMA |
| PriceCross | Input crosses above fast EMA, with fast > middle > slow for enabled EMAs | Input crosses below fast EMA, with fast < middle < slow for enabled EMAs |

The momentum dropdown offers **None**, **RSI**, or **MACD**. RSI requires longs
strictly above the midpoint and shorts strictly below it. The filter uses the
RSI value, not its smoothed average. MACD requires a positive histogram (`Diff`)
for longs and a negative histogram for shorts; fast/slow/signal periods default
to 12/26/9 and are configurable. None disables momentum confirmation.
Filters must pass **at the crossing**: if momentum or EMA
alignment qualifies later, the indicator does not send a delayed alert for the
old crossing. Use the normal price/Close input series for price-cross signals.

**Intrabar** checks crossings between consecutive incoming updates. It does not
wait for the candle close and emits no more than one alert per direction per
bar. A long and short may both occur in a whipsaw candle. Intrabar arrows remain
as a record of the triggered event even if price reverses.
The first live update establishes a baseline rather than announcing a stale
crossing from before startup.

**BarClose** checks the completed bar on the first update of the next bar,
compares it with the preceding close, and places its arrow on the completed
bar. Waiting for that first update is intentional, including after a trading
pause; it is not an immediate intrabar alert.

Signals start after at least the longest enabled EMA/momentum period has elapsed.
Historical arrows do not play sounds. Without Tick Replay, historical data only
reconstructs close-to-close crossings; it cannot reproduce live intrabar events.
Consequently, intrabar arrows may differ after a reload.

## Audio

Use full paths to existing WAV files. Defaults are NT8 `sounds/Alert1.wav` and
`sounds/Alert2.wav`. You can substitute your existing spoken long/short WAVs.
Missing files are reported in NinjaScript Output and the Control Center Log;
the arrow is still drawn but that audio/Alerts notification is skipped.

The indicator calls NT8's alert API directly when a qualifying live update
arrives, without a timer or intentional delay. Data-feed timing, computer load,
NT8 audio handling and silence at the beginning of a WAV can still affect when
you hear it. This is not a guarantee of zero-latency audio or profitable signals.
It never submits orders.

Actual NinjaTrader compilation, playback and chart rendering require NT8 on
Windows. Start with simulation before relying on live alerts.

## Developer checks

Run `dotnet run --project tests/RedTailEmaMomentum/RedTailEmaMomentum.Tests.csproj`.
The checks execute the production indicator against a minimal NT8 API model
with controlled EMA/RSI/MACD values. They cover signal/filter rules, alert timing,
deduplication, historical/live transitions and missing sounds, not NT8's built-in
indicator calculations, real audio playback or WPF rendering.
