// IQ EMA 50 Cloud — NinjaTrader 8 built-in plot/region rendering
// Renders EMA(50) line and cloud fill between EMA50 ± StdDev(Close,100)/4 bands.
// Uses AddPlot() and Draw.Region() — no SharpDX GPU code, no ArgumentOutOfRangeException.

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// IQ EMA 50 Cloud — Standalone indicator that renders:
    ///  • EMA(50) line with configurable color and thickness
    ///  • Cloud fill between EMA50 ± StdDev(Close,100)/4 bands with configurable opacity
    ///  • Optional cloud border lines (upper and lower band edges)
    ///  • Optional label "50" at the right edge of the EMA line
    ///
    /// Uses NinjaTrader built-in Plot and Draw.Region for reliable, crash-free rendering.
    /// Same approach as PropTraderz MTFCloudsV1 — works perfectly with 256 bar lookback.
    /// </summary>
    public class IQEma50Cloud : Indicator
    {
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters

        [NinjaScriptProperty]
        [Display(Name = "Show EMA 50", Order = 1, GroupName = "1. EMA 50",
            Description = "Draw the EMA(50) line.")]
        public bool ShowEma50 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA 50 Color", Order = 2, GroupName = "1. EMA 50")]
        [XmlIgnore]
        public System.Windows.Media.Brush Ema50Color { get; set; }
        [Browsable(false)]
        public string Ema50ColorSerializable
        {
            get => Serialize.BrushToString(Ema50Color);
            set => Ema50Color = Serialize.StringToBrush(value);
        }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "EMA 50 Thickness", Order = 3, GroupName = "1. EMA 50")]
        public int Ema50Thickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Cloud", Order = 1, GroupName = "2. Cloud",
            Description = "Draw filled cloud between EMA50 ± StdDev(Close,100)/4 bands.")]
        public bool ShowCloud { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Cloud Fill Color", Order = 2, GroupName = "2. Cloud")]
        [XmlIgnore]
        public System.Windows.Media.Brush CloudFillColor { get; set; }
        [Browsable(false)]
        public string CloudFillColorSerializable
        {
            get => Serialize.BrushToString(CloudFillColor);
            set => CloudFillColor = Serialize.StringToBrush(value);
        }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Cloud Fill Opacity %", Order = 3, GroupName = "2. Cloud")]
        public int CloudFillOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Cloud Border", Order = 4, GroupName = "2. Cloud",
            Description = "Draw upper and lower band edge lines.")]
        public bool ShowCloudBorder { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Label", Order = 1, GroupName = "3. Label",
            Description = "Show '50' label at the right edge of the EMA line.")]
        public bool ShowLabel { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region State management — OnStateChange

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "IQ EMA 50 Cloud — EMA(50) line with StdDev(100)/4 cloud bands.";
                Name                     = "IQEma50Cloud";
                Calculate                = Calculate.OnBarClose;
                IsOverlay                = true;
                IsAutoScale              = false;
                DisplayInDataBox         = false;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = false;
                MaximumBarsLookBack      = MaximumBarsLookBack.TwoHundredFiftySix;

                // EMA 50 defaults
                ShowEma50      = true;
                var ema50Brush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(31, 188, 211));
                ema50Brush.Freeze();
                Ema50Color     = ema50Brush;
                Ema50Thickness = 2;

                // Cloud defaults
                ShowCloud        = true;
                var cloudBrush   = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(155, 47, 174));
                cloudBrush.Freeze();
                CloudFillColor   = cloudBrush;
                CloudFillOpacity = 24;
                ShowCloudBorder  = false;

                // Label defaults
                ShowLabel = true;

                // Plots: EMA50 line, CloudUpper (transparent), CloudLower (transparent)
                AddPlot(new Stroke(Ema50Color, Ema50Thickness), PlotStyle.Line, "EMA50");
                AddPlot(new Stroke(Brushes.Transparent, 1),     PlotStyle.Line, "CloudUpper");
                AddPlot(new Stroke(Brushes.Transparent, 1),     PlotStyle.Line, "CloudLower");
            }
            else if (State == State.DataLoaded)
            {
                // Sync EMA50 plot stroke to user-configured color and thickness
                Plots[0].Brush = Ema50Color;
                Plots[0].Width = Ema50Thickness;
            }
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region OnBarUpdate

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 100) return;

            double ema    = EMA(50)[0];
            double stdDev = StdDev(Close, 100)[0] / 4.0;

            // EMA50 plot — assign NaN to hide when ShowEma50 is off
            Values[0][0] = ShowEma50 ? ema : double.NaN;

            // Cloud band plots (used as data sources for Draw.Region)
            Values[1][0] = ema + stdDev;
            Values[2][0] = ema - stdDev;

            // Cloud fill between upper and lower band plots
            if (ShowCloud)
            {
                // Draw region from current bar back through all bars with valid data.
                // Cap at 254 to stay within the TwoHundredFiftySix (0-255 offset) window.
                int barsBack       = Math.Max(0, Math.Min(CurrentBar - 100, 254));
                Brush outlineBrush = ShowCloudBorder ? Ema50Color : Brushes.Transparent;
                Draw.Region(this, "EmaCloud", 0, barsBack,
                    Values[1], Values[2],
                    outlineBrush, CloudFillColor, CloudFillOpacity);
            }
            else
            {
                RemoveDrawObject("EmaCloud");
            }

            // Label at the right edge of the EMA line — small and subtle
            if (ShowLabel && ShowEma50)
                Draw.Text(this, "Ema50Label", false, "50", 0, ema + (TickSize * 2), 0, Ema50Color,
                    new NinjaTrader.Gui.Tools.SimpleFont("Arial", 9), System.Windows.TextAlignment.Left,
                    Brushes.Transparent, Brushes.Transparent, 0);
            else
                RemoveDrawObject("Ema50Label");
        }

        #endregion
    }
}
