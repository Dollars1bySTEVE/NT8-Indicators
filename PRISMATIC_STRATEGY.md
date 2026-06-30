# The Prismatic Reversion Strategy
## A Complete Blueprint for Auction Market Theory & Liquidity Trading

---

## 1. Core Philosophy: The "Wall" & The Trap

The market is an auction mechanism seeking Fair Value.

### **The Wall (POC)**
The Point of Control (POC) of the first 15 minutes of the Asia (6:00 PM ET), London (3:00 AM ET), and NY (8:00 AM ET) sessions represents the "consensus price" where buyers and sellers were happiest.

### **The Trap**
When price abandons this POC and later returns, it tests the consensus. Retail traders see patterns like "Double Tops" or "Breakouts" at these levels. You view them as liquidity pools to be swept.

### **The Fuel**
A 4-Bar Reversal combined with a Liquidity Sweep confirms that the opposing side is trapped. Their panic (stop losses) fuels your directional move.

---

## 2. The Setup Hierarchy (A+ vs. Standard)

### **The A+ Setup: 10 Micros (Aggressive)**
This is your "Go Big" scenario. All conditions must align:

1. **Location**: Price interacts with a Stacked POC (e.g., Asia POC + NY POC) or a Fibonacci Golden Pocket (0.618–0.786).
2. **Pattern**: A 4-Bar Reversal forms (Gap Bar + Collecting Bars + Breakout).
3. **Confirmation**: A Liquidity Sweep (taking out a recent high/low) occurs simultaneously, followed by a Failed Continuation (e.g., a Gravestone Doji or Inverted Hammer).
4. **Structure**: Ideally, this forms a Breaker Block (a failed Order Block that flips role), trapping late entrants.

### **The Standard Setup: 3–5 Micros**
Any valid 4-Bar Reversal that lacks the full confluence of Stacked POCs, Fibonacci alignment, or a clear Sweep.
- Used to maintain market exposure without over-risking on lower-probability moves.

---

## 3. Position Sizing & Pyramiding Logic

Your sizing is dynamic, scaling with confirmation and confluence.

| Phase | Condition | Action | Size |
|-------|-----------|--------|------|
| **Initial Entry** | Price hits 0.618–0.786 Fib + POC | Enter Base Position | 5 Micros |
| **Confirmation Add** | Small Body/Big Wick candle prints at the zone | Add to Winner | +1 Micro |
| **High Conviction Add** | Breaker Block pattern confirms trapped liquidity | Aggressive Add | +5 Micros (Total: 10) |
| **Momentum Add** | Healthy pullback to 0.382 Fib in profit | Ride Continuation | +1 Micro |
| **Standard A+** | Direct 4-Bar Reversal + Sweep at POC (no pyramiding) | Direct Entry | 10 Micros |

---

## 4. Execution & Risk Management

### **Entry Trigger**
- **Timeframe**: Spot the "fight" on 5-second or 1-minute charts. Validate direction on 3m/5m/15m.
- **Signal**: Wait for the Directional Candle to close after the reversal pattern (e.g., the green candle after the Gravestone Doji). This confirms the trap is sprung.

### **Stop Loss (The "Wick" Rule)**
- **Placement**: Always placed at the extreme wick of the 4-Bar Reversal signal bar (plus/minus 1 tick).
- **Logic**: If price breaks this wick, the reversal thesis is invalid, and the "trap" has failed.

### **Take Profit (Prismatic Targets)**
- **TP1 (1:1 or 0.382 Fib)**: Watch for stalling candle behavior (Dojis, long wicks against you). If seen, exit half or move stop to breakeven.
- **TP2 (1:2 or 0.50 Fib)**: The standard target for mean reversion.
- **Runner (Continuation)**: If the move is fueled by a Breaker Block or massive sweep, hold the remainder for the next POC or Fibonacci Extension (1.272/1.618).

---

## 5. Implementation in NinjaTrader 8 (C# Overview)

To code this robustly, a custom NinjaScript strategy is required. Standard ATM strategies cannot handle the dynamic sizing and complex POC/Fib logic.

### **Key Components to Code:**

1. **Session POC Calculator**
   - Accumulate volume at price for the first 15 minutes of 18:00, 03:00, and 08:00 ET.
   - Store as `asiaPoc`, `londonPoc`, `nyPoc`.

2. **Dynamic Fibonacci**
   - Identify Swing Highs/Lows using a `Highest/Lowest` lookback.
   - Calculate levels: `0.382, 0.50, 0.618, 0.786`.

3. **Pattern Recognition**
   - **4-Bar Reversal**: Detect large range bar + 2 small inside bars + breakout.
   - **Candle Behavior**: Code logic for "Small Body/Big Wick" (`Math.Abs(Close - Open) < Range * 0.3`).
   - **Breaker Block**: Detect a failed Order Block (price closes through it) and subsequent retest.

4. **Conviction Sizing Engine**
   - Initialize `int orderSize = 3`.
   - If `isAPlusSetup` (4-Bar + Sweep + POC), set `orderSize = 10`.
   - If `isPyramidEntry` (Fib + POC), manage multiple entries (`EnterLong(5)`, then `EnterLong(1)`, etc.).

---

## 6. The "Takeaway" Workflow

Execute your trades using this 6-step checklist:

1. **Wait for the Sweep**: Let the algorithms flush stops (e.g., NY Open sweeping London Low).
2. **Identify the Wall**: Mark the Stacked POCs and Fibonacci Zones.
3. **Watch the Trap**: Look for retail patterns (Double Tops) failing at these walls.
4. **Confirm with Candle Behavior**: Wait for the Gravestone Doji or Failed Continuation.
5. **Execute with Conviction**: Size up (10 Micros) when the 4-Bar Reversal confirms the trap.
6. **Manage Dynamically**: Cut at 1:1 if stalling; ride 1:2+ if the "rocket" ignites.

---

## Key Takeaways

- **The Wall + The Trap = The Opportunity**: POCs are liquidity magnets; reversals there are high-probability.
- **Confluence is King**: Stacked POCs + Fibonacci + 4-Bar Reversals + Sweeps = A+ setups.
- **Size with Conviction**: 3–5 Micros for standard setups; 10 Micros for A+ alignment.
- **The Wick Rule is Sacred**: Your stop is the reversal bar's extreme. If it breaks, the thesis fails.
- **Prismatic Targets Lock in Profits**: TP1 at 1:1, TP2 at 1:2, and ride the remainder to the next POC/Extension.

---

**Trade well. Trade smart. Trade the auction.**
