# Drawing Tool Tile Pro

A floating, dockable drawing-tool palette for **NinjaTrader 8** — based on NinjaTrader's stock Drawing Tool Tile, upgraded with a background-opacity control, expand/collapse toggle, one-click trashcan, and a built-in **freehand grease-pencil pen**.

![Drawing Tool Tile Pro on a NQ 5-minute chart]<img width="636" height="600" alt="image" src="https://github.com/user-attachments/assets/044be6f8-ffd1-4b92-9c7a-f6b608f19b46" />

## Features

- 🎨 **One-click drawing tools** — a compact tile of your chosen drawing tools, always on the chart
- ✏ **Freehand Pen (grease pencil)** — arm the pen button and draw directly on the chart like a marker on glass. Round caps/joins, configurable color, size, and opacity (defaults: 5px yellow @ 80%). DPI-aware: strokes land exactly under your cursor at any Windows display scaling
- 🗑 **Trashcan button** — one click removes all user-drawn text notes **and** all pen strokes. Locked objects and anything drawn by indicators/strategies are preserved
- 👁 **Adjustable background opacity** (0–100%) so the tile doesn't hide price action
- ⏫ **Expand/collapse toggle** — single-click the grip handle to collapse the tile out of the way; configurable "Start Expanded" default
- 🖱 **Draggable** — grab the grip handle and place the tile anywhere on the chart panel; position is remembered
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
| **Arm the pen** | Click the ✏ button (it highlights orange) |
| **Draw a stroke** | With the pen armed, press-and-drag on the chart; release ends the stroke |
| **Disarm the pen** | Click ✏ again, **right-click** the chart, or click any other drawing tool |
| Remove text notes + pen strokes | Click the 🗑 button |
| Collapse / expand the tile | Single-click the grip handle (dotted strip) |
| Move the tile | Click and drag the grip handle |
| Open indicator properties | Double-click the grip handle |

### Pen behavior details

- While the pen is **armed**, chart panning with the left mouse button is suspended (that's how you draw). Right-click or disarm to get normal chart behavior back instantly.
- The tile itself always stays clickable, even while the pen is armed.
- Strokes render smoothly while drawing (repaints are throttled to ~33fps for performance). On very busy charts a slight lag while dragging is normal — the stroke catches up instantly on release.

## Important: Number of Rows vs. Selected Tools

The **Number of rows** setting controls how many tool buttons stack vertically before wrapping into a new column:

- If you select **more tools than rows**, the tile grows **wider** with extra columns.
  - e.g., 18 tools with `Number of rows = 5` → 4 columns (5 + 5 + 5 + 3)
- To keep a clean **single vertical column**, set `Number of rows` **equal to (or greater than) the number of drawing tools you've selected**.
  - e.g., 18 tools selected → set `Number of rows = 18`
- Want a **horizontal toolbar** instead? Set `Number of rows = 1` — every tool gets its own column, forming a horizontal strip. Any value in between gives you a grid.

The ✏ pen and 🗑 trashcan buttons always sit at the bottom of the tile, spanning its full width.

## Settings

| Setting | Default | Description |
|---|---|---|
| Number of rows | 5 | Tool buttons per column before wrapping to a new column |
| Background Opacity (%) | 80 | Tile background transparency |
| Start Expanded | true | Whether the tile starts expanded when the chart loads |
| Pen Color | Yellow | Marker ink color |
| Pen Size (px) | 5 | Marker stroke width (1–20) |
| Pen Opacity (%) | 80 | Marker ink transparency (grease-pencil look) |
| Drawing Tools (category) | 10 common tools | Check/uncheck any installed drawing tool to show/hide it on the tile |
| Visible only when focused | false | Hide the tile when the chart window is not active |

## ⚠️ Notes

- **Pen strokes are screen-anchored, not price-anchored** — they stay where you drew them on screen and do **not** move with the candles when you scroll, pan, or zoom. Think of them as marker on the monitor glass, perfect for quick live markups.
- **Pen strokes are session notes** — they are cleared when the chart is closed or the indicator is reloaded (F5). They are not saved to the workspace.
- The **trashcan has no undo** — one click permanently removes all user-drawn, unlocked text notes and every pen stroke.
- The trashcan only removes `Text` objects with `IsUserDrawn == true` that are not locked; lines, rectangles, fibs, and anything drawn by indicators or strategies are preserved. Lock a text note to protect it from the trashcan.

## Version History

| Version | Changes |
|---|---|
| v3c | Pen performance: repaints throttled to ~33fps while drawing |
| v3b.2 | Pen accuracy: DPI-correct coordinate conversion (strokes land exactly under the cursor) |
| v3b.1 | Tile stays clickable while pen is armed; right-click disarms |
| v3b | Freehand pen mouse drawing |
| v3a | Pen rendering pipeline, pen properties, trashcan clears pen strokes |
| v2.1 | Trashcan button (clears user-drawn text notes) |
| v1 | Opacity + expand/collapse + drag improvements over the stock tile |

## License / Credit

Based on NinjaTrader LLC's stock Drawing Tool Tile indicator. Modifications: background opacity, expand/collapse toggle, trashcan button, and freehand grease-pencil pen.
