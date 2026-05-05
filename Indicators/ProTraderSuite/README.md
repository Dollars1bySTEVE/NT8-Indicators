# ProTraderSuite

> A single-file, GPU-rendered NinjaTrader 8 indicator suite that consolidates 12 professional trading modules into one polished overlay. All rendering is done via SharpDX (Direct2D) inside `OnRender` — no GDI+, no `Draw.*` chart objects.

---

## Feature Overview

| # | Module | Description |
|---|--------|-------------|
| 1 | **ATR Bands** | Wilder ATR upper/lower channel with GPU polygon fills and outline lines |
| 2 | **Trend Baseline** | EMA of typical price rendered as dotted ellipses |
| 3 | **Session VWAP** | Session-anchored VWAP line, resets at each session open |
| 4 | **Market Structure** | Pivot detection with HH / HL / LH / LL tags + BOS / CHoCH connecting lines |
| 5 | **Fair Value Gaps** | Classic 3-bar FVG detection (bullish & bearish) with iFVG flip on mitigation |
| 6 | **Key Levels** | Prior Day High / Low / Close and Daily Pivot sourced from a secondary Day series |
| 7 | **ATR Projections** | LoD+1ATR, HoD-1ATR, 0.5×ATR ↑/↓, 0.5×Weekly ATR ↑/↓ labeled lines |
| 8 | **Order Flow Bubbles** | Per-bar volume circles sized by volume, colored by buy/sell aggression |
| 9 | **Volume Profile** | Session-anchored histogram built tick-by-tick in `OnMarketData` with POC/VAH/VAL |
| 10 | **DOM Heatmap** | Live order-book strip at the right edge via `OnMarketDepth`, log-scaled brightness |
| 11 | **Cumulative Delta** | Floating panel overlay with session reset, divergence dots |
| 12 | **Per-Feature Alerts** | 9 independent alert events with separate enable, sound file, priority, and rearm seconds |

---

## Install Steps

1. Copy `ProTraderSuite.cs` to:
   ```
   %USERPROFILE%\Documents\NinjaTrader 8\bin\Custom\Indicators\ProTraderSuite\
   ```
   (Create the `ProTraderSuite` subfolder if it does not exist.)

2. Open NinjaTrader 8 and press **F5** in the NinjaScript Editor (or go to **Tools → NinjaScript Editor → Compile**). The indicator will compile in seconds.

3. Right-click any chart → **Indicators** → search for **ProTraderSuite** → click **Add**.

4. Configure the properties as desired and click **OK**.

---

## Requirements

| Requirement | Detail |
|-------------|--------|
| NinjaTrader 8 | Version 8.0.23 or newer recommended |
| Market Depth (L2) subscription | Required for **DOM Heatmap** (`OnMarketDepth`) and **True Bid/Ask Delta** mode. Supported on Rithmic, CQG, dxFeed, Kinetick L2. |
| L1 (top-of-book only) | Kinetick L1 users will see only the best bid/ask on the heatmap strip — no depth. |
| Secondary series | Day + Week series are added automatically via `AddDataSeries`. No manual action required. |

---

## Settings Cheat-Sheet

### 01. Bands
| Setting | Default | Description |
|---------|---------|-------------|
| Show Bands | `true` | Master toggle for ATR bands |
| ATR Period | `14` | Period for Wilder ATR (also used as baseline EMA period) |
| Band Multiplier | `1.5` | ATR multiplier for upper/lower band distance |
| Upper/Lower Fill Color + Opacity | Red 20% / LimeGreen 20% | Polygon fill between line and baseline |
| Upper/Lower Line Color + Opacity | Red 70% / LimeGreen 70% | Outline lines |

### 02. Baseline
| Setting | Default | Description |
|---------|---------|-------------|
| Show Baseline | `true` | Toggle the dotted EMA trend line |
| Baseline Color + Opacity | Black 80% | Color of the ellipse dots |

### 03. VWAP
| Setting | Default | Description |
|---------|---------|-------------|
| Show VWAP | `true` | Toggle session VWAP |
| VWAP Color + Opacity | Gold 85% | Line color |

### 04. Structure
| Setting | Default | Description |
|---------|---------|-------------|
| Show Structure | `true` | Toggle market-structure labels and lines |
| Structure Strength | `3` | Bars on each side of a pivot to qualify it |
| HH / HL / LH / LL Colors | Green / Blue / OrangeRed / Red | Tag fill colors |
| BOS Color | Cyan | Break-of-structure line color |
| CHoCH Color | Magenta | Change-of-character line color |
| Tag Opacity % | `85` | Opacity for colored tag boxes |
| BOS/CHoCH Opacity % | `70` | Opacity for structural lines |

### 05. FVG
| Setting | Default | Description |
|---------|---------|-------------|
| Show FVG | `true` | Toggle fair value gap boxes |
| FVG Min Ticks | `2` | Minimum gap size in ticks |
| Require Displacement | `false` | Only mark when middle bar body ≥ multiplier × ATR |
| Displacement Multiplier | `1.0` | ATR multiplier for displacement filter |
| Show iFVG (Inverted) | `true` | Show boxes that flipped after mitigation |
| Bull / Bear / iFVG Colors | Blue / OrangeRed / Magenta | Box fill colors |
| FVG Opacity % | `30` | Fill opacity |

### 06. Key Levels
| Setting | Default | Description |
|---------|---------|-------------|
| Show Key Levels | `true` | Toggle prior-day lines |
| Key Level Color + Opacity | DarkGray 80% | All four lines share this color |

Labels: `PDH` (prior day high), `PDL` (prior day low), `PDC` (prior day close), `DPivot` (classic floor pivot = (H+L+C)/3).

### 07. ATR Projections
| Setting | Default | Description |
|---------|---------|-------------|
| Show ATR Projections | `true` | Toggle projection lines |
| Weekly ATR Period | `5` | Number of weekly bars for weekly ATR calculation |
| Daily Proj Color + Opacity | OrangeRed 90% | Daily projection lines |
| Weekly Proj Color + Opacity | Purple 90% | Weekly projection lines |

Lines: `LOD+1D`, `HOD-1D`, `LOD+.5D`, `HOD-.5D`, `LOD+.5W`, `HOD-.5W`.

### 08. Order Flow Bubbles
| Setting | Default | Description |
|---------|---------|-------------|
| Show Bubbles | `true` | Toggle volume bubbles |
| Bubble Max Px | `40` | Maximum circle radius in pixels |
| Delta Mode | `ProxyByClose` | `ProxyByClose` uses midpoint; `TrueBidAskDelta` uses live Bid/Ask |
| Buy / Sell Bubble Colors | LimeGreen / Red | Circle fill colors |
| Bubble Opacity % | `70` | Circle fill opacity |
| Show Volume Text | `true` | Print volume K/M inside large bubbles |

### 09. Text
| Setting | Default | Description |
|---------|---------|-------------|
| Tag Font Size | `10` | Font size in points for all on-chart labels |

### 10. Volume Profile
| Setting | Default | Description |
|---------|---------|-------------|
| Show VP | `true` | Toggle volume profile |
| VP Width Px | `80` | Histogram width in pixels |
| VP Tick Bucket Size | `1` | Bucket size in ticks |
| VP Value Area % | `70` | Value-area percentage (50–95) |
| VP Split Buy/Sell | `true` | Render separate buy/sell bars vs. single total bar |
| VP Buy / Sell / Total Colors | Green / Red / Blue | Histogram bar colors |
| POC Color | Yellow | POC line and POC bucket highlight |
| VA Edge Color | Orange | VAH/VAL line color |
| VP Histo Opacity % | `50` | Histogram fill opacity |

### 11. DOM Heatmap
| Setting | Default | Description |
|---------|---------|-------------|
| Show Heatmap | `true` | Toggle the right-edge strip |
| Heatmap Mode | `Both` | `SnapshotOnly`, `HistoryOnly`, or `Both` |
| Strip Width Px | `60` | Width of the strip in pixels |
| Depth Levels | `20` | Number of levels each side |
| History Seconds | `300` | Trail length for accumulated history |
| Sample Rate Hz | `4` | Snapshots per second for history trail |
| Show Size Labels | `true` | Print size numbers on walls above threshold |
| Wall Threshold | `500` | Minimum size to flag as a "wall" (0 = disabled) |
| Bid / Ask Colors | DodgerBlue / OrangeRed | Bid-side and ask-side bar colors |
| Max Opacity % | `80` | Intensity multiplier (applied log-scaled) |

### 12. Cumulative Delta
| Setting | Default | Description |
|---------|---------|-------------|
| Show CD Panel | `true` | Toggle the floating panel |
| Panel Position | `Bottom` | `Bottom` or `Top` of price panel |
| Panel Height Px | `120` | Panel height in pixels |
| Session Reset | `true` | Reset cumulative delta at each session open |
| Show Divergence | `true` | Plot bull/bear divergence dots |
| Up / Down Colors | LimeGreen / Red | Per-bar direction colors |
| BG / Zero-Line Colors | Black / Gray | Panel background and zero line |
| Divergence Dot Color | Yellow | Color of divergence markers |
| Panel Opacity % | `70` | Background fill opacity |

### 13. Alerts
Each of the 9 alert events has four independent settings:

| Setting | Description |
|---------|-------------|
| `— Enable` | Master on/off for this specific alert |
| `— Sound` | WAV filename (e.g. `Alert1.wav`) or full path |
| `— Priority` | `Low`, `Medium`, or `High` |
| `— Rearm Sec` | Minimum seconds before the same alert fires again |

---

## Per-Feature Alert Table

| Event | Trigger Condition | Default Sound | Default Priority | Default Rearm |
|-------|-------------------|---------------|-----------------|---------------|
| **FVG Fill** | A tracked FVG is mitigated (price closes through it) | Alert2.wav | Medium | 15 s |
| **iFVG Created** | A mitigated FVG flips to inverted FVG | Alert3.wav | High | 15 s |
| **BOS** | Break of structure detected (HH after HH, LL after LL) | Alert1.wav | High | 10 s |
| **CHoCH** | Change of character detected (LH after HH, HL after LL) | Alert1.wav | High | 10 s |
| **ATR Target Hit** | Price touches any of the 6 ATR projection levels | Alert2.wav | Medium | 30 s |
| **CD Divergence** | New bull or bear divergence dot is plotted | Alert4.wav | High | 20 s |
| **VWAP Cross** | Price close crosses through VWAP (sign change) | Alert2.wav | Medium | 20 s |
| **Wall Added** | A new DOM wall appears above the threshold | Alert3.wav | High | 5 s |
| **Wall Pulled** | A tracked DOM wall drops below the threshold | Alert4.wav | High | 5 s |

> **Tip:** To use a custom WAV file, enter either a filename (e.g. `MySound.wav`) and it will look in `<NT8 Install>\sounds\`, or enter the full path (e.g. `C:\Sounds\MySound.wav`).

---

## Performance Notes

- **GPU-rendered**: All visuals are drawn via SharpDX Direct2D inside `OnRender`. No WPF/GDI+ overhead.
- **Brush caching**: All `SolidColorBrush` objects are created once in `CreateDXResources()` (called on `OnRenderTargetChanged`) and disposed in `DisposeDXResources()`. There are no per-frame allocations for brushes.
- **Memory-bounded lists**: `swings` (max 200), `fvgs` (max 150), `bubbles` (max 500), `cdBars` (max 600). `depthHistory` is pruned by time window.
- **`Calculate.OnEachTick`**: Required for true delta, volume profile, and DOM heatmap. For performance on slower machines, disable modules you don't need using the group toggles.

---

## Known Limitations

1. **Order-book depth depends on data feed.** Rithmic and CQG typically provide 10–20 levels each side. dxFeed provides 5–10. Kinetick L1 shows only best bid/ask.

2. **Weekly ATR accuracy.** The weekly ATR is calculated from a secondary `BarsPeriodType.Week` series. On instruments where weekly bars are not loaded (e.g., continuous contracts with gaps), the weekly projections will display `0` until the series has enough bars.

3. **True Delta accuracy.** `TrueBidAskDelta` mode classifies trades by comparing last price to best ask/bid. Inside-spread trades are split 50/50. This is a Level 1 proxy — for true exchange-level aggressor tagging you need a footprint data feed.

4. **Session VWAP.** Uses `Bars.IsFirstBarOfSession` which is controlled by the instrument's trading hours template. If trading hours are set incorrectly, the reset point will be wrong.

5. **Heatmap history.** `depthHistory` accumulates depth snapshots for `HeatmapHistorySeconds` seconds. On very active instruments at high `SampleRateHz`, memory usage for history will be higher.

---

## Screenshots

*← Add chart screenshots here after attaching the indicator to an NQ/ES 5-minute chart.*

---

## License

MIT — see the root [`README.md`](../../README.md) or repository license file.
