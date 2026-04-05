// IQ EMA 50 Cloud — Standalone GPU-accelerated EMA 50 line with StdDev cloud for NinjaTrader 8
// Renders the EMA(50) line and a cloud fill (EMA50 ± StdDev(Close,100)/4) using SharpDX.
// Pattern follows IQCandlesGPU.cs and IQSessionsGPU.cs.

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
#endregion

// NinjaTrader 8 requires custom enums declared OUTSIDE all namespaces
// so the auto-generated partial class code can resolve them without ambiguity.
// Reference: forum.ninjatrader.com threads #1182932, #95909, #1046853

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// IQ EMA 50 Cloud — Standalone GPU-accelerated indicator that renders:
    ///  • EMA(50) line with configurable color and thickness
    ///  • Cloud fill between EMA50 ± StdDev(Close,100)/4 bands with configurable opacity
    ///  • Optional cloud border lines (upper and lower band edges)
    ///  • Optional label "50" at the right edge of the EMA line
    ///
    /// Uses SharpDX DirectX GPU rendering for high-performance chart drawing.
    /// All SharpDX types fully qualified — no namespace conflicts.
    /// </summary>
    public class IQEma50Cloud : Indicator
    {
        // ════════════════════════════════════════════════════════════════════════
        #region Private fields

        // ── Cached indicator references (set in State.DataLoaded) ─────────────
        private NinjaTrader.NinjaScript.Indicators.EMA    ema50Ind;
        private NinjaTrader.NinjaScript.Indicators.StdDev stdDev100Ind;

        // ── SharpDX GPU resources ─────────────────────────────────────────────
        private bool dxReady;
        private SharpDX.DirectWrite.Factory        dxWriteFactory;
        private SharpDX.DirectWrite.TextFormat     dxLabelFormat;

        private SharpDX.Direct2D1.SolidColorBrush  dxEma50Brush;
        private SharpDX.Direct2D1.SolidColorBrush  dxCloudFillBrush;
        private SharpDX.Direct2D1.SolidColorBrush  dxCloudBorderBrush;

        #endregion
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
                Description              = "IQ EMA 50 Cloud — Standalone GPU-rendered EMA(50) line with StdDev(100)/4 cloud bands.";
                Name                     = "IQEma50Cloud";
                Calculate                = Calculate.OnBarClose;
                IsOverlay                = true;
                IsAutoScale              = false;
                DisplayInDataBox         = false;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = false;
                MaximumBarsLookBack      = MaximumBarsLookBack.TwoHundredFiftySix;

                // EMA 50
                ShowEma50       = true;
                var ema50Brush  = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(31, 188, 211));
                ema50Brush.Freeze();
                Ema50Color      = ema50Brush;
                Ema50Thickness  = 2;

                // Cloud
                ShowCloud       = true;
                var cloudBrush  = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(155, 47, 174));
                cloudBrush.Freeze();
                CloudFillColor  = cloudBrush;
                CloudFillOpacity = 24;
                ShowCloudBorder = false;

                // Label
                ShowLabel       = true;
            }
            else if (State == State.DataLoaded)
            {
                // Cache EMA/StdDev indicators so the render thread can access them
                // safely without calling the EMA()/StdDev() helpers on the render thread.
                // With TwoHundredFiftySix lookback the buffer holds max 256 values (0-255).
                ema50Ind     = EMA(50);
                stdDev100Ind = StdDev(Close, 100);
            }
            else if (State == State.Terminated)
            {
                DisposeDXResources();
            }
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region OnBarUpdate

        protected override void OnBarUpdate()
        {
            // No manual calculation needed — EMA and StdDev are managed by
            // the cached indicator references; rendering is handled in OnRender.
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region GPU Rendering — OnRender / OnRenderTargetChanged

        public override void OnRenderTargetChanged()
        {
            DisposeDXResources();
            dxReady = false;
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (Bars == null || ChartBars == null || RenderTarget == null)
                return;

            if (!dxReady)
            {
                try { CreateDXResources(); }
                catch (Exception ex)
                {
                    Print("IQEma50Cloud: Unexpected exception from CreateDXResources: " + ex.Message);
                    return;
                }
            }

            if (!dxReady)
                return;

            // Guard: indicator references set in State.DataLoaded; skip if not ready.
            if (ema50Ind == null || stdDev100Ind == null)
                return;

            int fromBar = ChartBars.FromIndex;
            int toBar   = ChartBars.ToIndex;

            if (fromBar > toBar)
                return;

            try
            {
                RenderEma50Cloud(chartControl, chartScale, fromBar, toBar);
            }
            catch (SharpDX.SharpDXException sdxEx)
            {
                Print("IQEma50Cloud: SharpDX error in OnRender, recreating resources: " + sdxEx.Message);
                dxReady = false;
                DisposeDXResources();
            }
            catch (Exception ex)
            {
                Print("IQEma50Cloud: RenderEma50Cloud [" + ex.GetType().Name + "]: " + ex.Message);
            }
        }

        // ── EMA 50 + cloud rendering ──────────────────────────────────────────
        private void RenderEma50Cloud(ChartControl cc, ChartScale cs, int fromBar, int toBar)
        {
            var rt = RenderTarget;
            if (rt == null) return;

            float prevX50 = 0, prevY50 = 0;
            float prevYCloudU = 0, prevYCloudL = 0;
            bool  first50   = true;
            bool  firstBar  = true;

            for (int barIdx = fromBar; barIdx <= toBar; barIdx++)
            {
                float x   = cc.GetXByBarIndex(ChartBars, barIdx);
                int   off = CurrentBar - barIdx;

                // Skip if offset is negative or beyond the 256-bar lookback window
                if (off < 0 || off > 255)
                {
                    firstBar = true;  // Reset continuity
                    continue;
                }

                // Use IsValidDataPointAt for proper series bounds checking
                bool has50     = barIdx >= 50  && ema50Ind.IsValidDataPointAt(off);
                bool hasStdDev = barIdx >= 100 && stdDev100Ind.IsValidDataPointAt(off);

                if (!has50)
                {
                    firstBar = true; // reset continuity when EMA is unavailable
                    continue;
                }

                double e50  = ema50Ind[off];
                float  y50  = cs.GetYByValue(e50);

                double sdOff  = 0;
                float  yClU   = y50;
                float  yClL   = y50;

                if (ShowCloud && hasStdDev)
                {
                    sdOff = stdDev100Ind[off] / 4.0;
                    yClU  = cs.GetYByValue(e50 + sdOff);
                    yClL  = cs.GetYByValue(e50 - sdOff);
                }

                if (!firstBar)
                {
                    // Cloud fill — bounding-rect fill per bar segment (avoids PathGeometry churn).
                    if (ShowCloud && hasStdDev && dxCloudFillBrush != null)
                    {
                        float cloudTop    = Math.Min(Math.Min(prevYCloudU, prevYCloudL), Math.Min(yClU, yClL));
                        float cloudBottom = Math.Max(Math.Max(prevYCloudU, prevYCloudL), Math.Max(yClU, yClL));
                        var   segRect     = new SharpDX.RectangleF(prevX50, cloudTop, x - prevX50, cloudBottom - cloudTop);
                        rt.FillRectangle(segRect, dxCloudFillBrush);
                    }

                    // Cloud border lines
                    if (ShowCloud && ShowCloudBorder && hasStdDev && dxCloudBorderBrush != null)
                    {
                        rt.DrawLine(new SharpDX.Vector2(prevX50, prevYCloudU), new SharpDX.Vector2(x, yClU), dxCloudBorderBrush, 1f);
                        rt.DrawLine(new SharpDX.Vector2(prevX50, prevYCloudL), new SharpDX.Vector2(x, yClL), dxCloudBorderBrush, 1f);
                    }

                    // EMA 50 line
                    if (ShowEma50 && !first50 && dxEma50Brush != null)
                        rt.DrawLine(new SharpDX.Vector2(prevX50, prevY50), new SharpDX.Vector2(x, y50), dxEma50Brush, Ema50Thickness);
                }

                prevX50     = x;
                prevY50     = y50;
                prevYCloudU = yClU;
                prevYCloudL = yClL;
                first50     = false;
                firstBar    = false;
            }

            // Label at the rightmost visible bar
            if (ShowLabel && ShowEma50 && !first50 && dxLabelFormat != null && dxEma50Brush != null)
            {
                float labelX = prevX50 + 6f;
                var   lr     = new SharpDX.RectangleF(labelX, prevY50 - 8f, 32f, 16f);
                rt.DrawText("50", dxLabelFormat, lr, dxEma50Brush);
            }
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region SharpDX resource management

        private void CreateDXResources()
        {
            var rt = RenderTarget;
            if (rt == null)
            {
                dxReady = false;
                return;
            }

            // Dispose any previously-created resources before recreating them
            // to avoid leaking GPU objects when the render target changes.
            DisposeDXResources();

            try
            {
                dxWriteFactory = new SharpDX.DirectWrite.Factory();
                dxLabelFormat  = new SharpDX.DirectWrite.TextFormat(dxWriteFactory, "Consolas", 12f);

                dxEma50Brush      = MakeBrush(rt, Ema50Color,      1f);
                dxCloudFillBrush  = MakeBrush(rt, CloudFillColor,  CloudFillOpacity / 100f);
                dxCloudBorderBrush= MakeBrush(rt, Ema50Color,      0.35f);

                dxReady = true;
            }
            catch (Exception ex)
            {
                Print("IQEma50Cloud: CreateDXResources failed [" + ex.GetType().Name + "]: " + ex.Message);
                dxReady = false;
                DisposeDXResources();
            }
        }

        private static SharpDX.Direct2D1.SolidColorBrush MakeBrush(
            SharpDX.Direct2D1.RenderTarget rt,
            System.Windows.Media.Brush wpfBrush,
            float opacity)
        {
            var scb = wpfBrush as System.Windows.Media.SolidColorBrush;
            if (scb != null)
            {
                System.Windows.Media.Color c;
                try   { c = scb.Color; }
                catch (InvalidOperationException) { c = System.Windows.Media.Colors.White; }
                return new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, opacity));
            }
            return new SharpDX.Direct2D1.SolidColorBrush(rt,
                new SharpDX.Color4(1f, 1f, 1f, opacity));
        }

        private void DisposeDXResources()
        {
            DisposeRef(ref dxWriteFactory);
            DisposeRef(ref dxLabelFormat);
            DisposeRef(ref dxEma50Brush);
            DisposeRef(ref dxCloudFillBrush);
            DisposeRef(ref dxCloudBorderBrush);
        }

        private static void DisposeRef<T>(ref T resource) where T : class, IDisposable
        {
            if (resource != null)
            {
                resource.Dispose();
                resource = null;
            }
        }

        #endregion
    }
}
