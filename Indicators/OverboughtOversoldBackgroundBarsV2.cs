#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// OverboughtOversoldBackgroundBars V2
    ///
    /// Always-on background "warning light": red while price is overbought,
    /// green while oversold, clear otherwise. Rendered via SharpDX for clean,
    /// fast visuals. Optional order-flow (delta) and Level 2 book-imbalance
    /// boosts intensify the tint when flow confirms the looming reversal.
    /// Optional on-chart status readout (RSI / delta / book imbalance).
    ///
    /// Built for renko-style charts (e.g. 6/3 NinZaRenko on NQ/MNQ) but
    /// instrument-agnostic — tune thresholds per instrument.
    /// </summary>
    public class OverboughtOversoldBackgroundBarsV2 : Indicator
    {
        private RSI rsi;

        // Per-bar zone record
        private Series<double> zoneSeries;   // 1 / -1 / 0
        private Series<double> alphaSeries;  // computed opacity per bar (0..1)

        // ---- SharpDX device-dependent resources ----
        private SharpDX.Direct2D1.SolidColorBrush dxObBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxOsBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxTextBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxPanelBrush;
        private TextFormat textFormat;

        // ---- Order flow (Level 1 tape) ----
        private double barDelta;             // aggressive buys - sells, current bar
        private double prevBarDelta;

        // ---- Level 2 book imbalance ----
        private readonly double[] bidSizes = new double[10];
        private readonly double[] askSizes = new double[10];
        private double bookImbalance;        // -1..+1 (bid-heavy positive)
        private DateTime lastDepthCalc = DateTime.MinValue;

        #region 1. Parameters
        [NinjaScriptProperty, Range(2, int.MaxValue)]
        [Display(Name = "RSI Period", GroupName = "1. Parameters", Order = 0)]
        public int RsiPeriod { get; set; }

        [NinjaScriptProperty, Range(1, int.MaxValue)]
        [Display(Name = "RSI Smooth", GroupName = "1. Parameters", Order = 1)]
        public int RsiSmooth { get; set; }

        [NinjaScriptProperty, Range(50, 100)]
        [Display(Name = "Overbought Threshold", GroupName = "1. Parameters", Order = 2)]
        public int OverboughtThreshold { get; set; }

        [NinjaScriptProperty, Range(0, 50)]
        [Display(Name = "Oversold Threshold", GroupName = "1. Parameters", Order = 3)]
        public int OversoldThreshold { get; set; }
        #endregion

        #region 2. Visuals
        [NinjaScriptProperty]
        [Display(Name = "Gradient Intensity", GroupName = "2. Visuals", Order = 0,
                 Description = "Opacity scales with RSI depth into the zone.")]
        public bool UseGradientIntensity { get; set; }

        [NinjaScriptProperty, Range(0, 100)]
        [Display(Name = "Min Opacity %", GroupName = "2. Visuals", Order = 1)]
        public int MinOpacityPct { get; set; }

        [NinjaScriptProperty, Range(0, 100)]
        [Display(Name = "Max Opacity %", GroupName = "2. Visuals", Order = 2)]
        public int MaxOpacityPct { get; set; }

        [XmlIgnore]
        [Display(Name = "Overbought Color", GroupName = "2. Visuals", Order = 3)]
        public System.Windows.Media.Brush OverboughtColor { get; set; }

        [Browsable(false)]
        public string OverboughtColorSerialize
        {
            get { return Serialize.BrushToString(OverboughtColor); }
            set { OverboughtColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Oversold Color", GroupName = "2. Visuals", Order = 4)]
        public System.Windows.Media.Brush OversoldColor { get; set; }

        [Browsable(false)]
        public string OversoldColorSerialize
        {
            get { return Serialize.BrushToString(OversoldColor); }
            set { OversoldColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Show Status Readout", GroupName = "2. Visuals", Order = 5,
                 Description = "On-chart corner readout: current RSI, zone, bar delta and L2 book imbalance. Useful while evaluating boost settings.")]
        public bool ShowStatusReadout { get; set; }
        #endregion

        #region 3. Order Flow Boost (real-time only)
        [NinjaScriptProperty]
        [Display(Name = "Enable Delta Boost", GroupName = "3. Order Flow Boost", Order = 0,
                 Description = "Real-time only. Boosts tint to max opacity when aggressive flow turns against the extreme (selling into overbought / buying into oversold).")]
        public bool EnableDeltaBoost { get; set; }

        [NinjaScriptProperty, Range(1, 100000)]
        [Display(Name = "Delta Boost Threshold (contracts)", GroupName = "3. Order Flow Boost", Order = 1,
                 Description = "Net opposing delta on the current bar required to trigger the boost. NQ start: 75-150. MNQ: scale up ~10x.")]
        public int DeltaBoostThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Level 2 Boost (experimental)", GroupName = "3. Order Flow Boost", Order = 2,
                 Description = "Real-time only. Boosts tint when the resting book stacks against the extreme. Book data can be spoofed; treat as supplementary.")]
        public bool EnableLevel2Boost { get; set; }

        [NinjaScriptProperty, Range(1, 10)]
        [Display(Name = "L2 Depth Levels", GroupName = "3. Order Flow Boost", Order = 3)]
        public int DepthLevels { get; set; }

        [NinjaScriptProperty, Range(50, 95)]
        [Display(Name = "L2 Imbalance % Trigger", GroupName = "3. Order Flow Boost", Order = 4,
                 Description = "One side must hold at least this % of visible size to trigger. 65-75 is reasonable.")]
        public int ImbalanceTriggerPct { get; set; }
        #endregion

        /// <summary>1 = overbought, -1 = oversold, 0 = neutral. Strategy-readable.</summary>
        [Browsable(false), XmlIgnore]
        public Series<double> ZoneState { get { return zoneSeries; } }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "V2: Continuous background warning light for RSI overbought (red) / oversold (green), SharpDX-rendered, with optional order-flow boosts and status readout.";
                Name = "OverboughtOversoldBackgroundBarsV2";
                IsOverlay = true;
                IsSuspendedWhileInactive = true;
                DisplayInDataBox = true;
                Calculate = Calculate.OnPriceChange;

                RsiPeriod = 14;
                RsiSmooth = 1;
                OverboughtThreshold = 70;
                OversoldThreshold = 30;

                UseGradientIntensity = true;
                MinOpacityPct = 15;
                MaxOpacityPct = 60;
                OverboughtColor = System.Windows.Media.Brushes.Crimson;
                OversoldColor = System.Windows.Media.Brushes.MediumSeaGreen;
                ShowStatusReadout = false;

                EnableDeltaBoost = false;
                DeltaBoostThreshold = 100;
                EnableLevel2Boost = false;
                DepthLevels = 5;
                ImbalanceTriggerPct = 70;
            }
            else if (State == State.Configure)
            {
                zoneSeries = new Series<double>(this);
                alphaSeries = new Series<double>(this);
            }
            else if (State == State.DataLoaded)
            {
                rsi = RSI(RsiPeriod, RsiSmooth);

                if (MinOpacityPct > MaxOpacityPct)
                {
                    int t = MinOpacityPct; MinOpacityPct = MaxOpacityPct; MaxOpacityPct = t;
                }
            }
            else if (State == State.Terminated)
            {
                DisposeDeviceResources();
                textFormat?.Dispose();
                textFormat = null;
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < RsiPeriod)
            {
                zoneSeries[0] = 0;
                alphaSeries[0] = 0;
                return;
            }

            if (IsFirstTickOfBar)
            {
                prevBarDelta = barDelta;
                barDelta = 0;
            }

            double rsiValue = rsi[0];

            if (rsiValue >= OverboughtThreshold)
            {
                zoneSeries[0] = 1;
                alphaSeries[0] = ComputeOpacity(1, rsiValue, OverboughtThreshold, 100);
            }
            else if (rsiValue <= OversoldThreshold)
            {
                zoneSeries[0] = -1;
                alphaSeries[0] = ComputeOpacity(-1, rsiValue, OversoldThreshold, 0);
            }
            else
            {
                zoneSeries[0] = 0;
                alphaSeries[0] = 0;
            }
        }

        /// <summary>Returns opacity 0..1 for the zone, including boost logic.</summary>
        private double ComputeOpacity(int zone, double rsiValue, int threshold, int extreme)
        {
            double minOp = MinOpacityPct / 100.0;
            double maxOp = MaxOpacityPct / 100.0;

            // Boosts always win: flow confirming the reversal = full intensity
            if (State == State.Realtime && IsBoosted(zone))
                return maxOp;

            if (!UseGradientIntensity)
                return maxOp;

            double span  = Math.Abs(extreme - threshold) * 0.8; // saturate before absolute extreme
            double depth = Math.Abs(rsiValue - threshold);
            double pct   = span <= 0 ? 1.0 : Math.Min(depth / span, 1.0);

            return minOp + pct * (maxOp - minOp);
        }

        private bool IsBoosted(int zone)
        {
            // Delta boost: aggressive flow turning against the extreme
            if (EnableDeltaBoost)
            {
                double effDelta = barDelta != 0 ? barDelta : prevBarDelta;
                if (zone == 1 && effDelta <= -DeltaBoostThreshold) return true;  // selling into OB
                if (zone == -1 && effDelta >= DeltaBoostThreshold) return true;  // buying into OS
            }

            // L2 boost: resting book stacked against the extreme
            if (EnableLevel2Boost)
            {
                double trig = ImbalanceTriggerPct / 100.0 * 2 - 1; // map 70% -> 0.4 imbalance
                if (zone == 1 && bookImbalance <= -trig) return true;  // asks dominant in OB
                if (zone == -1 && bookImbalance >= trig) return true;  // bids dominant in OS
            }

            return false;
        }

        // ---------------- Level 1 tape: cumulative bar delta ----------------
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if ((!EnableDeltaBoost &amp;&amp; !ShowStatusReadout) || e.MarketDataType != MarketDataType.Last)
                return;

            if (e.Price >= e.Ask)      barDelta += e.Volume;  // aggressive buy
            else if (e.Price <= e.Bid) barDelta -= e.Volume;  // aggressive sell
        }

        // ---------------- Level 2 book: throttled imbalance ----------------
        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if ((!EnableLevel2Boost &amp;&amp; !ShowStatusReadout) || e.Position >= 10)
                return;

            double size = (e.Operation == Operation.Remove) ? 0 : e.Volume;
            if (e.MarketDataType == MarketDataType.Bid)      bidSizes[e.Position] = size;
            else if (e.MarketDataType == MarketDataType.Ask) askSizes[e.Position] = size;

            // Throttle recompute to ~4x/sec — NQ depth events fire extremely fast
            var now = DateTime.UtcNow;
            if ((now - lastDepthCalc).TotalMilliseconds < 250)
                return;
            lastDepthCalc = now;

            double bid = 0, ask = 0;
            for (int i = 0; i < DepthLevels; i++)
            {
                bid += bidSizes[i];
                ask += askSizes[i];
            }

            double total = bid + ask;
            bookImbalance = total <= 0 ? 0 : (bid - ask) / total; // -1..+1
        }

        // ---------------- SharpDX rendering ----------------
        public override void OnRenderTargetChanged()
        {
            DisposeDeviceResources();

            if (RenderTarget == null)
                return;

            dxObBrush    = MakeDxBrush(OverboughtColor, Colors.Crimson);
            dxOsBrush    = MakeDxBrush(OversoldColor, Colors.MediumSeaGreen);
            dxTextBrush  = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(0.9f, 0.9f, 0.9f, 1f));
            dxPanelBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(0f, 0f, 0f, 0.55f));
        }

        private void DisposeDeviceResources()
        {
            dxObBrush?.Dispose();    dxObBrush = null;
            dxOsBrush?.Dispose();    dxOsBrush = null;
            dxTextBrush?.Dispose();  dxTextBrush = null;
            dxPanelBrush?.Dispose(); dxPanelBrush = null;
        }

        private SharpDX.Direct2D1.SolidColorBrush MakeDxBrush(System.Windows.Media.Brush src, System.Windows.Media.Color fallback)
        {
            var c = (src as System.Windows.Media.SolidColorBrush)?.Color ?? fallback;
            return new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                new Color4(c.R / 255f, c.G / 255f, c.B / 255f, 1f)); // alpha applied per-bar at draw time
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (Bars == null || ChartBars == null || dxObBrush == null || dxOsBrush == null)
                return;

            float top = ChartPanel.Y;
            float height = ChartPanel.H;
            float halfBarWidth = (float)chartControl.GetBarPaintWidth(ChartBars) / 2f;

            for (int idx = ChartBars.FromIndex; idx <= ChartBars.ToIndex; idx++)
            {
                if (idx < 0 || idx > CurrentBar)
                    continue;

                double zone = zoneSeries.GetValueAt(idx);
                if (zone == 0)
                    continue;

                float opacity = (float)alphaSeries.GetValueAt(idx);
                if (opacity <= 0f)
                    continue;

                float x = chartControl.GetXByBarIndex(ChartBars, idx);
                var rect = new RectangleF(x - halfBarWidth, top, halfBarWidth * 2f, height);

                var brush = zone > 0 ? dxObBrush : dxOsBrush;
                float saved = brush.Opacity;
                brush.Opacity = opacity;
                RenderTarget.FillRectangle(rect, brush);
                brush.Opacity = saved;
            }

            if (ShowStatusReadout)
                RenderStatusReadout(chartControl);
        }

        private void RenderStatusReadout(ChartControl chartControl)
        {
            if (CurrentBar < RsiPeriod || dxTextBrush == null || dxPanelBrush == null)
                return;

            if (textFormat == null)
                textFormat = new TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory,
                    "Consolas", FontWeight.Normal, FontStyle.Normal, 13f);

            double rsiValue = rsi[0];
            double zone = zoneSeries.GetValueAt(CurrentBar);
            string zoneTxt = zone > 0 ? "OVERBOUGHT" : zone < 0 ? "OVERSOLD" : "NEUTRAL";

            double effDelta = barDelta != 0 ? barDelta : prevBarDelta;
            bool boosted = State == State.Realtime &amp;&amp; zone != 0 &amp;&amp; IsBoosted((int)zone);

            string text =
                  "RSI(" + RsiPeriod + "): " + rsiValue.ToString("F1") + "  [" + zoneTxt + "]"
                + "\nDelta: " + (State == State.Realtime ? effDelta.ToString("+0;-0;0") : "n/a (hist)")
                + "\nBook:  " + (State == State.Realtime ? (bookImbalance * 100).ToString("+0;-0;0") + "% bid" : "n/a (hist)")
                + (boosted ? "\n** BOOST ACTIVE **" : "");

            using (var layout = new TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory,
                                               text, textFormat, 300f, 100f))
            {
                float pad = 6f;
                float x = ChartPanel.X + 10f;
                float y = ChartPanel.Y + 10f;
                var bg = new RectangleF(x - pad, y - pad,
                                        layout.Metrics.Width + pad * 2,
                                        layout.Metrics.Height + pad * 2);

                RenderTarget.FillRectangle(bg, dxPanelBrush);
                RenderTarget.DrawTextLayout(new Vector2(x, y), layout, dxTextBrush);
            }
        }
    }
}
