# Drawing Tool Tile Pro

A floating, dockable drawing-tool palette for **NinjaTrader 8** — based on NinjaTrader's stock Drawing Tool Tile, with quality-of-life upgrades for fast freehand/markup workflows.

![Drawing Tool Tile Pro on a NQ 5-minute chart]<img width="636" height="600" alt="image" src="https://github.com/user-attachments/assets/044be6f8-ffd1-4b92-9c7a-f6b608f19b46" />


## Features

- 🎨 **One-click drawing tools** — a compact tile of your chosen drawing tools, always on the chart
- 🗑 **Trashcan button** — one click removes **ALL** manually drawn objects from the chart (pen strokes, lines, rectangles, text, etc.). Indicator-drawn objects are left untouched
- 👁 **Adjustable background opacity** (0–100%) so the tile doesn't hide price action
- ⏫ **Expand/collapse toggle** — single-click the grip handle to collapse the tile out of the way; configurable "Start Expanded" default
- 🖱 **Draggable** — grab the grip handle and place the tile anywhere on the chart panel; position is remembered
- 🖊 **Custom drawing tool support** — correctly renders geometry-based icons from custom tools (e.g., a freehand Pen tool), instead of showing raw path text
- ⚙️ **Configurable tool list** — pick exactly which drawing tools appear via the indicator properties (Drawing Tools category)

## Installation

1. In NinjaTrader 8: **Tools → Edit NinjaScript → Indicator → New**
2. Name it `DrawingToolTilePro`
3. Select all (Ctrl+A), delete, and paste the entire contents of [`Indicators/DrawingToolTilePro.cs`](Indicators/DrawingToolTilePro.cs)
4. Save (Ctrl+S) and compile (F5)
5. Add the **"Drawing Tool Tile Pro"** indicator to any chart

> Tip: save it into your chart template so it's on every chart automatically.

## Usage

| Action | How |
|---|---|
| Start a drawing tool | Click its icon on the tile |
| Remove ALL drawings | Click the 🗑 button at the bottom of the tile |
| Collapse / expand the tile | Single-click the grip handle (dotted strip) |
| Move the tile | Click and drag the grip handle |
| Open indicator properties | Double-click the grip handle |

## Important: Number of Rows vs. Selected Tools

The **Number of rows** setting controls how many tool buttons stack vertically before wrapping into a new column:

- If you select **more tools than rows**, the tile grows **wider** with extra columns.
  - e.g., 18 tools with `Number of rows = 5` → 4 columns (5 + 5 + 5 + 3)
- To keep a clean **single vertical column**, set `Number of rows` **equal to (or greater than) the number of drawing tools you've selected**.
  - e.g., 18 tools selected → set `Number of rows = 18`
- Want a **horizontal toolbar** instead? Set `Number of rows = 1` — every tool gets its own column, forming a horizontal strip. Any value in between gives you a grid (e.g., 2 rows × 9 columns for 18 tools).

**Count your selected tools** in the Drawing Tools category of the indicator properties and set the rows to match.

## Settings

| Setting | Default | Description |
|---|---|---|
| Number of rows | 5 | Tool buttons per column before wrapping to a new column. **Set this to ≥ the number of tools you've selected for a single vertical column** (e.g., 18 tools → 18 rows) |
| Background Opacity (%) | 80 | Tile background transparency |
| Start Expanded | true | Whether the tile starts expanded when the chart loads |
| Drawing Tools (category) | 10 common tools | Check/uncheck any installed drawing tool to show/hide it on the tile |
| Visible only when focused | false | Hide the tile when the chart window is not active |

## ⚠️ Notes

- The **trashcan has no undo** — one click permanently removes every user-drawn object on the chart.
- The trashcan only removes objects with `IsUserDrawn == true`; anything drawn by indicators or strategies is preserved.
- Works great alongside a freehand **Pen** drawing tool — draw strokes freely, then wipe the chart clean with one click.

## License / Credit

Based on NinjaTrader LLC's stock Drawing Tool Tile indicator. Modifications: background opacity, expand/collapse toggle, trashcan (remove-all-drawings) button, and geometry-icon rendering fix.
