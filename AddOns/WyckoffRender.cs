// AddOns/WyckoffRender.cs
// WyckoffRenderControl — GPU-accelerated SharpDX drawing helpers
// NT8 8.1.6.3 / SharpDX 2.6.3 compliant
// Adapted from itchy5/NT8-OrderFlowKit

using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace SightEngine
{
    /// <summary>
    /// Base helper class that wraps a SharpDX RenderTarget and provides:
    ///   • A brush cache (invalidated on RT change)
    ///   • A static BrushToColor dictionary + SolidColorBrush fallback
    ///   • RenderTarget null/disposed guards on every draw call
    ///   • A heat-gradient color interpolator
    ///   • Thin wrapper draw methods (myFillRectangle, myDrawLine, etc.)
    /// </summary>
    public class WyckoffRenderControl
    {
        // ── Public state set by the indicator before every OnRender ──────────────

        /// <summary>The SharpDX render target provided by NinjaTrader.</summary>
        public SharpDX.Direct2D1.RenderTarget RENDER_TARGET;

        /// <summary>The chart scale for the current panel (provides GetYByValue).</summary>
        public NinjaTrader.Gui.Chart.ChartScale CHART_SCALE;

        /// <summary>Width of the chart panel in pixels.</summary>
        public float PanelW;

        /// <summary>Height of the chart panel in pixels.</summary>
        public float PanelH;

        /// <summary>Right-margin width (the BookMap column) in pixels.</summary>
        public float marginRight;

        // ── Brush cache ──────────────────────────────────────────────────────────

        private readonly Dictionary<int, SharpDX.Direct2D1.SolidColorBrush> _brushCache
            = new Dictionary<int, SharpDX.Direct2D1.SolidColorBrush>();

        private SharpDX.Direct2D1.RenderTarget _lastRenderTarget;

        private SharpDX.Direct2D1.SolidColorBrush GetCachedBrush(SharpDX.Color color)
        {
            // Invalidate cache when RenderTarget changes (resize / device reset)
            if (!ReferenceEquals(_lastRenderTarget, RENDER_TARGET))
            {
                DisposeBrushCache();
                _lastRenderTarget = RENDER_TARGET;
            }

            int key = color.ToRgba();
            SharpDX.Direct2D1.SolidColorBrush brush;
            if (!_brushCache.TryGetValue(key, out brush) || brush == null || brush.IsDisposed)
            {
                if (brush != null && !brush.IsDisposed)
                    brush.Dispose();

                brush = new SharpDX.Direct2D1.SolidColorBrush(RENDER_TARGET, color);
                _brushCache[key] = brush;
            }
            return brush;
        }

        public void DisposeBrushCache()
        {
            foreach (var b in _brushCache.Values)
            {
                if (b != null && !b.IsDisposed)
                    b.Dispose();
            }
            _brushCache.Clear();
        }

        // ── BrushToColor — static dictionary + SolidColorBrush fallback ─────────

        // Keys are WPF brush hex-string representations (Brushes.X.ToString()).
        // The SolidColorBrush fallback handles every case not in this map,
        // including custom hex colours specified by the user in the property grid.
        private static readonly Dictionary<string, SharpDX.Color> _brushColorMap
            = BuildBrushColorMap();

        private static Dictionary<string, SharpDX.Color> BuildBrushColorMap()
        {
            var m = new Dictionary<string, SharpDX.Color>(StringComparer.OrdinalIgnoreCase);

            // Helper: WPF hex ARGB → SharpDX.Color
            // WPF Brushes.X.ToString() returns "#AARRGGBB" hex strings.
            Action<string, byte, byte, byte, byte> add = (hex, r, g, b, a) =>
                m[hex] = new SharpDX.Color(r, g, b, a);

            // Common named colors (hex values as produced by WPF SolidColorBrush.ToString())
            add("#FFF0F8FF", 240, 248, 255, 255);  // AliceBlue
            add("#FFFAEBD7", 250, 235, 215, 255);  // AntiqueWhite
            add("#FF00FFFF", 0,   255, 255, 255);  // Aqua / Cyan
            add("#FF7FFFD4", 127, 255, 212, 255);  // Aquamarine
            add("#FFF0FFFF", 240, 255, 255, 255);  // Azure
            add("#FFF5F5DC", 245, 245, 220, 255);  // Beige
            add("#FFFFE4C4", 255, 228, 196, 255);  // Bisque
            add("#FF000000", 0,   0,   0,   255);  // Black
            add("#FFFFEBCD", 255, 235, 205, 255);  // BlanchedAlmond
            add("#FF0000FF", 0,   0,   255, 255);  // Blue
            add("#FF8A2BE2", 138, 43,  226, 255);  // BlueViolet
            add("#FFA52A2A", 165, 42,  42,  255);  // Brown
            add("#FFDEB887", 222, 184, 135, 255);  // BurlyWood
            add("#FF5F9EA0", 95,  158, 160, 255);  // CadetBlue
            add("#FF7FFF00", 127, 255, 0,   255);  // Chartreuse
            add("#FFD2691E", 210, 105, 30,  255);  // Chocolate
            add("#FFFF7F50", 255, 127, 80,  255);  // Coral
            add("#FF6495ED", 100, 149, 237, 255);  // CornflowerBlue
            add("#FFFFF8DC", 255, 248, 220, 255);  // Cornsilk
            add("#FFDC143C", 220, 20,  60,  255);  // Crimson
            add("#FF00008B", 0,   0,   139, 255);  // DarkBlue
            add("#FF008B8B", 0,   139, 139, 255);  // DarkCyan
            add("#FFB8860B", 184, 134, 11,  255);  // DarkGoldenrod
            add("#FFA9A9A9", 169, 169, 169, 255);  // DarkGray
            add("#FF006400", 0,   100, 0,   255);  // DarkGreen
            add("#FFBDB76B", 189, 183, 107, 255);  // DarkKhaki
            add("#FF8B008B", 139, 0,   139, 255);  // DarkMagenta
            add("#FF556B2F", 85,  107, 47,  255);  // DarkOliveGreen
            add("#FFFF8C00", 255, 140, 0,   255);  // DarkOrange
            add("#FF9932CC", 153, 50,  204, 255);  // DarkOrchid
            add("#FF8B0000", 139, 0,   0,   255);  // DarkRed
            add("#FFE9967A", 233, 150, 122, 255);  // DarkSalmon
            add("#FF8FBC8F", 143, 188, 143, 255);  // DarkSeaGreen
            add("#FF483D8B", 72,  61,  139, 255);  // DarkSlateBlue
            add("#FF2F4F4F", 47,  79,  79,  255);  // DarkSlateGray
            add("#FF00CED1", 0,   206, 209, 255);  // DarkTurquoise
            add("#FF9400D3", 148, 0,   211, 255);  // DarkViolet
            add("#FFFF1493", 255, 20,  147, 255);  // DeepPink
            add("#FF00BFFF", 0,   191, 255, 255);  // DeepSkyBlue
            add("#FF696969", 105, 105, 105, 255);  // DimGray
            add("#FF1E90FF", 30,  144, 255, 255);  // DodgerBlue
            add("#FFB22222", 178, 34,  34,  255);  // Firebrick
            add("#FFFFFAF0", 255, 250, 240, 255);  // FloralWhite
            add("#FF228B22", 34,  139, 34,  255);  // ForestGreen
            add("#FFFF00FF", 255, 0,   255, 255);  // Fuchsia / Magenta
            add("#FFDCDCDC", 220, 220, 220, 255);  // Gainsboro
            add("#FFF8F8FF", 248, 248, 255, 255);  // GhostWhite
            add("#FFFFD700", 255, 215, 0,   255);  // Gold
            add("#FFDAA520", 218, 165, 32,  255);  // Goldenrod
            add("#FF808080", 128, 128, 128, 255);  // Gray
            add("#FF008000", 0,   128, 0,   255);  // Green
            add("#FFADFF2F", 173, 255, 47,  255);  // GreenYellow
            add("#FFF0FFF0", 240, 255, 240, 255);  // Honeydew
            add("#FFFF69B4", 255, 105, 180, 255);  // HotPink
            add("#FFCD5C5C", 205, 92,  92,  255);  // IndianRed
            add("#FF4B0082", 75,  0,   130, 255);  // Indigo
            add("#FFFFFFF0", 255, 255, 240, 255);  // Ivory
            add("#FFF0E68C", 240, 230, 140, 255);  // Khaki
            add("#FFE6E6FA", 230, 230, 250, 255);  // Lavender
            add("#FFFFF0F5", 255, 240, 245, 255);  // LavenderBlush
            add("#FF7CFC00", 124, 252, 0,   255);  // LawnGreen
            add("#FFFFFACD", 255, 250, 205, 255);  // LemonChiffon
            add("#FFADD8E6", 173, 216, 230, 255);  // LightBlue
            add("#FFF08080", 240, 128, 128, 255);  // LightCoral
            add("#FFE0FFFF", 224, 255, 255, 255);  // LightCyan
            add("#FFFAFAD2", 250, 250, 210, 255);  // LightGoldenrodYellow
            add("#FFD3D3D3", 211, 211, 211, 255);  // LightGray
            add("#FF90EE90", 144, 238, 144, 255);  // LightGreen
            add("#FFFFB6C1", 255, 182, 193, 255);  // LightPink
            add("#FFFFA07A", 255, 160, 122, 255);  // LightSalmon
            add("#FF20B2AA", 32,  178, 170, 255);  // LightSeaGreen
            add("#FF87CEFA", 135, 206, 250, 255);  // LightSkyBlue
            add("#FF778899", 119, 136, 153, 255);  // LightSlateGray
            add("#FFB0C4DE", 176, 196, 222, 255);  // LightSteelBlue
            add("#FFFFFFE0", 255, 255, 224, 255);  // LightYellow
            add("#FF00FF00", 0,   255, 0,   255);  // Lime
            add("#FF32CD32", 50,  205, 50,  255);  // LimeGreen
            add("#FFFAF0E6", 250, 240, 230, 255);  // Linen
            add("#FF800000", 128, 0,   0,   255);  // Maroon
            add("#FF66CDAA", 102, 205, 170, 255);  // MediumAquamarine
            add("#FF0000CD", 0,   0,   205, 255);  // MediumBlue
            add("#FFBA55D3", 186, 85,  211, 255);  // MediumOrchid
            add("#FF9370DB", 147, 112, 219, 255);  // MediumPurple
            add("#FF3CB371", 60,  179, 113, 255);  // MediumSeaGreen
            add("#FF7B68EE", 123, 104, 238, 255);  // MediumSlateBlue
            add("#FF00FA9A", 0,   250, 154, 255);  // MediumSpringGreen
            add("#FF48D1CC", 72,  209, 204, 255);  // MediumTurquoise
            add("#FFC71585", 199, 21,  133, 255);  // MediumVioletRed
            add("#FF191970", 25,  25,  112, 255);  // MidnightBlue
            add("#FFF5FFFA", 245, 255, 250, 255);  // MintCream
            add("#FFFFE4E1", 255, 228, 225, 255);  // MistyRose
            add("#FFFFE4B5", 255, 228, 181, 255);  // Moccasin
            add("#FFFFDEAD", 255, 222, 173, 255);  // NavajoWhite
            add("#FF000080", 0,   0,   128, 255);  // Navy
            add("#FFFDF5E6", 253, 245, 230, 255);  // OldLace
            add("#FF808000", 128, 128, 0,   255);  // Olive
            add("#FF6B8E23", 107, 142, 35,  255);  // OliveDrab
            add("#FFFFA500", 255, 165, 0,   255);  // Orange
            add("#FFFF4500", 255, 69,  0,   255);  // OrangeRed
            add("#FFDA70D6", 218, 112, 214, 255);  // Orchid
            add("#FFEEE8AA", 238, 232, 170, 255);  // PaleGoldenrod
            add("#FF98FB98", 152, 251, 152, 255);  // PaleGreen
            add("#FFAFEEEE", 175, 238, 238, 255);  // PaleTurquoise
            add("#FFDB7093", 219, 112, 147, 255);  // PaleVioletRed
            add("#FFFFEFD5", 255, 239, 213, 255);  // PapayaWhip
            add("#FFFFDAB9", 255, 218, 185, 255);  // PeachPuff
            add("#FFCD853F", 205, 133, 63,  255);  // Peru
            add("#FFFFC0CB", 255, 192, 203, 255);  // Pink
            add("#FFDDA0DD", 221, 160, 221, 255);  // Plum
            add("#FFB0E0E6", 176, 224, 230, 255);  // PowderBlue
            add("#FF800080", 128, 0,   128, 255);  // Purple
            add("#FFFF0000", 255, 0,   0,   255);  // Red
            add("#FFBC8F8F", 188, 143, 143, 255);  // RosyBrown
            add("#FF4169E1", 65,  105, 225, 255);  // RoyalBlue
            add("#FF8B4513", 139, 69,  19,  255);  // SaddleBrown
            add("#FFFA8072", 250, 128, 114, 255);  // Salmon
            add("#FFF4A460", 244, 164, 96,  255);  // SandyBrown
            add("#FF2E8B57", 46,  139, 87,  255);  // SeaGreen
            add("#FFFFF5EE", 255, 245, 238, 255);  // SeaShell
            add("#FFA0522D", 160, 82,  45,  255);  // Sienna
            add("#FFC0C0C0", 192, 192, 192, 255);  // Silver
            add("#FF87CEEB", 135, 206, 235, 255);  // SkyBlue
            add("#FF6A5ACD", 106, 90,  205, 255);  // SlateBlue
            add("#FF708090", 112, 128, 144, 255);  // SlateGray
            add("#FFFFFAFA", 255, 250, 250, 255);  // Snow
            add("#FF00FF7F", 0,   255, 127, 255);  // SpringGreen
            add("#FF4682B4", 70,  130, 180, 255);  // SteelBlue
            add("#FFD2B48C", 210, 180, 140, 255);  // Tan
            add("#FF008080", 0,   128, 128, 255);  // Teal
            add("#FFD8BFD8", 216, 191, 216, 255);  // Thistle
            add("#FFFF6347", 255, 99,  71,  255);  // Tomato
            add("#00FFFFFF", 255, 255, 255, 0);    // Transparent
            add("#FF40E0D0", 64,  224, 208, 255);  // Turquoise
            add("#FFEE82EE", 238, 130, 238, 255);  // Violet
            add("#FFF5DEB3", 245, 222, 179, 255);  // Wheat
            add("#FFFFFFFF", 255, 255, 255, 255);  // White
            add("#FFF5F5F5", 245, 245, 245, 255);  // WhiteSmoke
            add("#FFFFFF00", 255, 255, 0,   255);  // Yellow
            add("#FF9ACD32", 154, 205, 50,  255);  // YellowGreen

            return m;
        }

        /// <summary>
        /// Converts a WPF <see cref="Brush"/> to a SharpDX <see cref="SharpDX.Color"/>.
        /// Named brushes are looked up in a static dictionary for performance;
        /// any SolidColorBrush (including custom hex colours) falls back to direct
        /// channel extraction.
        /// </summary>
        public static SharpDX.Color BrushToColor(System.Windows.Media.Brush brushToConvert)
        {
            if (brushToConvert == null)
                return SharpDX.Color.Transparent;

            // Fast path: named WPF brushes stored as "#AARRGGBB" strings
            string key = brushToConvert.ToString();
            SharpDX.Color dxColor;
            if (_brushColorMap.TryGetValue(key, out dxColor))
                return dxColor;

            // Fallback: extract channels directly (handles any SolidColorBrush)
            var scb = brushToConvert as System.Windows.Media.SolidColorBrush;
            if (scb != null)
            {
                var c = scb.Color;
                return new SharpDX.Color(c.R, c.G, c.B, c.A);
            }

            return SharpDX.Color.Transparent;
        }

        // ── Heat gradient ────────────────────────────────────────────────────────

        /// <summary>
        /// Linearly interpolates between <paramref name="coldColor"/> (intensity=0)
        /// and <paramref name="hotColor"/> (intensity=1).
        /// </summary>
        protected SharpDX.Color GetHeatColor(float intensity,
            SharpDX.Color coldColor, SharpDX.Color hotColor)
        {
            intensity = Math.Max(0f, Math.Min(1f, intensity));
            return new SharpDX.Color(
                (byte)(coldColor.R + (hotColor.R - coldColor.R) * intensity),
                (byte)(coldColor.G + (hotColor.G - coldColor.G) * intensity),
                (byte)(coldColor.B + (hotColor.B - coldColor.B) * intensity),
                (byte)(coldColor.A + (hotColor.A - coldColor.A) * intensity));
        }

        // ── Draw method helpers ──────────────────────────────────────────────────
        //
        // All methods:
        //   1. Guard on RENDER_TARGET null / disposed.
        //   2. Obtain brush from cache (no alloc per frame).
        //   3. Set opacity on the cached brush before the draw call.
        //   4. Do NOT dispose the brush — it lives in the cache.

        public void myFillRectangle(ref SharpDX.RectangleF rect,
            SharpDX.Color color, float opacity)
        {
            if (RENDER_TARGET == null || RENDER_TARGET.IsDisposed) return;
            var brush = GetCachedBrush(color);
            brush.Opacity = Math2.Clampf(opacity, 0f, 1f);
            RENDER_TARGET.FillRectangle(rect, brush);
        }

        public void myDrawRectangle(ref SharpDX.RectangleF rect,
            SharpDX.Color color, float opacity, float strokeWidth)
        {
            if (RENDER_TARGET == null || RENDER_TARGET.IsDisposed) return;
            var brush = GetCachedBrush(color);
            brush.Opacity = Math2.Clampf(opacity, 0f, 1f);
            RENDER_TARGET.DrawRectangle(rect, brush, Math.Max(0.5f, strokeWidth));
        }

        public void myFillEllipse(ref SharpDX.Direct2D1.Ellipse ellipse,
            SharpDX.Color color, float opacity)
        {
            if (RENDER_TARGET == null || RENDER_TARGET.IsDisposed) return;
            var brush = GetCachedBrush(color);
            brush.Opacity = Math2.Clampf(opacity, 0f, 1f);
            RENDER_TARGET.FillEllipse(ellipse, brush);
        }

        public void myDrawEllipse(ref SharpDX.Direct2D1.Ellipse ellipse,
            SharpDX.Color color, float opacity, float strokeWidth)
        {
            if (RENDER_TARGET == null || RENDER_TARGET.IsDisposed) return;
            var brush = GetCachedBrush(color);
            brush.Opacity = Math2.Clampf(opacity, 0f, 1f);
            RENDER_TARGET.DrawEllipse(ellipse, brush, Math.Max(0.5f, strokeWidth));
        }

        public void myDrawLine(ref SharpDX.Vector2 p0, ref SharpDX.Vector2 p1,
            SharpDX.Color color, float opacity, float strokeWidth,
            SharpDX.Direct2D1.StrokeStyle strokeStyle)
        {
            if (RENDER_TARGET == null || RENDER_TARGET.IsDisposed) return;
            var brush = GetCachedBrush(color);
            brush.Opacity = Math2.Clampf(opacity, 0f, 1f);
            RENDER_TARGET.DrawLine(p0, p1, brush, Math.Max(0.5f, strokeWidth), strokeStyle);
        }

        public void myDrawText(string text,
            SharpDX.DirectWrite.TextFormat format,
            ref SharpDX.RectangleF rect,
            SharpDX.Color color, float opacity)
        {
            if (RENDER_TARGET == null || RENDER_TARGET.IsDisposed) return;
            if (format == null || string.IsNullOrEmpty(text)) return;
            var brush = GetCachedBrush(color);
            brush.Opacity = Math2.Clampf(opacity, 0f, 1f);
            RENDER_TARGET.DrawText(text, format, rect, brush);
        }

        // ── Text format helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Creates a <see cref="SharpDX.DirectWrite.TextFormat"/> using the
        /// NinjaTrader global DirectWrite factory. Returns null on failure.
        /// </summary>
        protected static SharpDX.DirectWrite.TextFormat CreateTextFormat(
            string fontFamily, float fontSize,
            SharpDX.DirectWrite.FontWeight weight   = SharpDX.DirectWrite.FontWeight.Normal,
            SharpDX.DirectWrite.FontStyle  style    = SharpDX.DirectWrite.FontStyle.Normal,
            SharpDX.DirectWrite.FontStretch stretch = SharpDX.DirectWrite.FontStretch.Normal)
        {
            try
            {
                return new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory,
                    fontFamily, weight, style, stretch, fontSize);
            }
            catch
            {
                return null;
            }
        }
    }
}
