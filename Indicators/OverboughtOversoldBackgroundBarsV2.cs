#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
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
    /// fast visuals (drawn behind the chart bars). Optional order-flow (delta)
    /// and Level 2 book-imbalance boosts intensify the tint when flow confirms
    /// the looming reversal. Optional on-chart status readout.
    ///
    /// Signal filtering (visuals only; ZoneState stays raw):
    ///  - Min Bars In Zone: zone must persist N consecutive bars (kills brief blips)
    ///  - Min RSI Depth: RSI must reach N points past the threshold during the run
    ///    (kills shallow zones). Thresholds define the ZONE; depth defines what's
    ///    WORTH SHOWING. Both back-fill retroactively on confirmation.
    ///
    /// Follow Mode (optional, default OFF — preserves classic behavior):
    ///  Once a band confirms, it latches and keeps painting at a steady reduced
    ///  opacity until the move exhausts (RSI crossing back through the Release
    ///  Level, default 50). Turns the band from a condition light ("RSI is
    ///  extreme now") into a signal state ("reversal active — follow until
    ///  exhausted"). Delta boost still overlays during the follow, so a
    ///  capitulation flush lights the band to max = exhaustion cue.
    ///
    /// Built for renko-style charts (e.g. 6/3 NinZaRenko on NQ/MNQ) but
    /// instrument-agnostic — tune thresholds per instrument.
    /// </summary>
    public class OverboughtOversoldBackgroundBarsV2 : Indicator
    {
        private RSI rsi;

        // Per-bar records (written in OnBarUpdate, safely read at render time)
        private Series<double> zoneSeries;   // raw RSI zone: 1 / -1 / 0 (unfiltered)
        private Series<double> paintSeries;  // zone actually painted (includes follow bars)
        private Series<double> alphaSeries;  // computed opacity per bar (0..1)
        private Series<double> rsiSeries;    // RSI value per bar (for readout)

        // Persistence + depth tracking for the current run
        private int zoneRunLength;           // consecutive bars in current zone
        private bool runDepthReached;        // RSI reached threshold +/- depth during run
        private bool runConfirmed;           // both conditions met -> painting

        // Follow Mode state
        private int followZone;              // 0 = not following; else 1 / -1

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

        [NinjaScriptProperty, Range(1, 50)]
        [Display(Name = "Min Bars In Zone", GroupName = "1. Parameters", Order = 4,
                 Description = "Band only shows once the zone has persisted this many consecutive bars (earlier bars back-fill on confirmation). Filters brief blips. Set 1 to disable.")]
        public int MinBarsInZone { get; set; }

        [NinjaScriptProperty, Range(0, 25)]
        [Display(Name = "Min RSI Depth", GroupName = "1. Parameters", Order = 5,
                 Description = "RSI must reach this many points PAST the threshold at some point during the run before the band paints (e.g. 3 with OB 75 requires RSI 78+). Filters shallow zones. Set 0 to disable.")]
        public int MinRsiDepth { get; set; }
        #endregion

        #region 2. Follow Mode
        [NinjaScriptProperty]
        [Display(Name = "Enable Follow Mode", GroupName = "2. Follow Mode", Order = 0,
                 Description = "Once a band confirms, it latches and keeps painting (reduced steady opacity) until RSI crosses back through the Release Level — the band follows the reversal move until exhausted, instead of ending when RSI leaves the extreme.")]
        public bool EnableFollowMode { get; set; }

        [NinjaScriptProperty, Range(20, 80)]
        [Display(Name = "Release Level", GroupName = "2. Follow Mode", Order = 1,
                 Description = "RSI level that ends a follow. Red releases when RSI drops below it; green releases when RSI rises above it. 50 = midline (symmetric).")]
        public int ReleaseLevel { get; set; }

        [NinjaScriptProperty, Range(0, 100)]
        [Display(Name = "Follow Opacity %", GroupName = "2. Follow Mode", Order = 2,
                 Description = "Steady opacity while following (after RSI leaves the extreme). Keep below Max Opacity so 'in the extreme' and 'following' stay visually distinct.")]
        public int FollowOpacityPct { get; set; }
        #endregion

        #region 3. Visuals
        [NinjaScriptProperty]
        [Display(Name = "Gradient Intensity", GroupName = "3. Visuals", Order = 0,
                 Description = "Opacity scales with RSI depth into the zone.")]
        public bool UseGradientIntensity { get; set; }

        [NinjaScriptProperty, Range(0, 100)]
        [Display(Name = "Min Opacity %", GroupName = "3. Visuals", Order = 1)]
        public int MinOpacityPct { get; set; }

        [NinjaScriptProperty, Range(0, 100)]
        [Display(Name = "Max Opacity %", GroupName = "3. Visuals", Order = 2)]
        public int MaxOpacityPct { get; set; }

        [XmlIgnore]
        [Display(Name = "Overbought Color", GroupName = "3. Visuals", Order = 3)]
        public System.Windows.Media.Brush OverboughtColor { get; set; }

        [Browsable(false)]
        public string OverboughtColorSerialize
        {
            get { return Serialize.BrushToString(OverboughtColor); }
            set { OverboughtColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Oversold Color", GroupName = "3. Visuals", Order = 4)]
        public System.Windows.Media.Brush OversoldColor { get; set; }

        [Browsable(false)]
        public string OversoldColorSerialize
        {
            get { return Serialize.BrushToString(OversoldColor); }
            set { OversoldColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Show Status Readout", GroupName = "3. Visuals", Order = 5,
                 Description = "On-chart corner readout: current RSI, zone/follow state, bar delta and L2 book imbalance. Useful while evaluating settings.")]
        public bool ShowStatusReadout { get; set; }
        #endregion

        #region 4. Order Flow Boost (real-time only)
        [NinjaScriptProperty]
        [Display(Name = "Enable Delta Boost", GroupName = "4. Order Flow Boost", Order = 0,
                 Description = "Real-time only. Boosts tint to max opacity when aggressive flow turns against the extreme (selling into overbought / buying into oversold). During a follow, a with-move capitulation flush also boosts = exhaustion cue.")]
        public bool EnableDeltaBoost { get; set; }

        [NinjaScriptProperty, Range(1, 100000)]
        [Display(Name = "Delta Boost Threshold (contracts)", GroupName = "4. Order Flow Boost", Order = 1,
                 Description = "Net opposing delta on the current bar required to trigger the boost. NQ start: 75-150. MNQ: scale up ~10x.")]
        public int DeltaBoostThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Level 2 Boost (experimental)", GroupName = "4. Order Flow Boost", Order = 2,
                 Description = "Real-time only. Boosts tint when the resting book stacks against the extreme. Book data can be spoofed; treat as supplementary.")]
        public bool EnableLevel2Boost { get; set; }

        [NinjaScriptProperty, Range(1, 10)]
        [Display(Name = "L2 Depth Levels", GroupName = "4. Order Flow Boost", Order = 3)]
        public int DepthLevels { get; set; }

        [NinjaScriptProperty, Range(50, 95)]
        [Display(Name = "L2 Imbalance % Trigger", GroupName = "4. Order Flow Boost", Order = 4,
                 Description = "One side must hold at least this % of visible size to trigger. 65-75 is reasonable.")]
        public int ImbalanceTriggerPct { get; set; }
        #endregion

        /// <summary>1 = overbought, -1 = oversold, 0 = neutral. Raw/unfiltered — persistence, depth and follow logic apply to visuals only.</summary>
        [Browsable(false), XmlIgnore]
        public Series<double> ZoneState { get { return zoneSeries; } }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "V2: Continuous background warning light for RSI overbought (red) / oversold (green), SharpDX-rendered behind the bars, with persistence + depth filters, optional follow mode, order-flow boosts and status readout.";
                Name = "OverboughtOversoldBackgroundBarsV2";
                IsOverlay = true;
                IsSuspendedWhileInactive = true;
                DisplayInDataBox = true;
                Calculate = Calculate.OnPriceChange;

                RsiPeriod = 14;
                RsiSmooth = 1;
                OverboughtThreshold = 75;
                OversoldThreshold = 25;
                MinBarsInZone = 3;
                MinRsiDepth = 3;

                EnableFollowMode = false;
                ReleaseLevel = 50;
                FollowOpacityPct = 20;

                UseGradientIntensity = true;
                MinOpacityPct = 5;
                MaxOpacityPct = 40;
                OverboughtColor = System.Windows.Media.Brushes.Crimson;
                OversoldColor = System.Windows.Media.Brushes.MediumSeaGreen;
                ShowStatusReadout = false;

                EnableDeltaBoost = true;
                DeltaBoostThreshold = 100;
                EnableLevel2Boost = false;
                DepthLevels = 5;
                ImbalanceTriggerPct = 70;
            }
            else if (State == State.Configure)
            {
                zoneSeries = new Series<double>(this);
                paintSeries = new Series<double>(this);
                alphaSeries = new Series<double>(this);
                rsiSeries = new Series<double>(this);
            }
            else if (State == State.DataLoaded)
            {
                rsi = RSI(RsiPeriod, RsiSmooth);

                if (MinOpacityPct > MaxOpacityPct)
                {
                    int t = MinOpacityPct; MinOpacityPct = MaxOpacityPct; MaxOpacityPct = t;
                }
            }
            else if (State == State.Historical)
            {
                // Render behind the chart bars so the tint never overpowers them
                if (ChartBars != null)
                    ZOrder = ChartBars.ZOrder - 1;
            }
            else if (State == State.Terminated)
            {
                DisposeDeviceResources();
                if (textFormat != null)
                {
                    textFormat.Dispose();
                    textFormat = null;
                }
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < RsiPeriod)
            {
                zoneSeries[0] = 0;
                paintSeries[0] = 0;
                alphaSeries[0] = 0;
                rsiSeries[0] = 0;
                zoneRunLength = 0;
                runDepthReached = false;
                runConfirmed = false;
                followZone = 0;
                return;
            }

            if (IsFirstTickOfBar)
            {
                prevBarDelta = barDelta;
                barDelta = 0;
            }

            double rsiValue = rsi[0];
            rsiSeries[0] = rsiValue;

            int zone;
            if (rsiValue >= OverboughtThreshold)      zone = 1;
            else if (rsiValue <= OversoldThreshold)   zone = -1;
            else                                      zone = 0;

            zoneSeries[0] = zone;

            // ---------------- In an RSI extreme zone ----------------
            if (zone != 0)
            {
                int threshold = zone == 1 ? OverboughtThreshold : OversoldThreshold;
                int extreme   = zone == 1 ? 100 : 0;
                double depthTarget = zone == 1 ? threshold + MinRsiDepth : threshold - MinRsiDepth;

                int run = 1;
                bool depthOk = MinRsiDepth <= 0
                    || (zone == 1 ? rsiValue >= depthTarget : rsiValue <= depthTarget);

                for (int back = 1; back <= CurrentBar; back++)
                {
                    if (zoneSeries[back] != zone) break;
                    run++;
                    if (!depthOk && MinRsiDepth > 0)
                    {
                        double backRsi = rsiSeries[back];
                        if (zone == 1 ? backRsi >= depthTarget : backRsi <= depthTarget)
                            depthOk = true;
                    }
                    if (run >= MinBarsInZone && (depthOk || MinRsiDepth <= 0) && back >= 50)
                        break;
                }

                zoneRunLength = run;
                runDepthReached = depthOk;

                // An opposite follow ends immediately when the other extreme confirms
                bool confirmedNow = run >= MinBarsInZone && depthOk;

                if (confirmedNow)
                {
                    runConfirmed = true;
                    paintSeries[0] = zone;
                    alphaSeries[0] = ComputeOpacity(zone, rsiValue, threshold, extreme);

                    // Retroactive fill: paint any unpainted bars of this run
                    for (int back = 1; back <= CurrentBar; back++)
                    {
                        if (zoneSeries[back] != zone) break;
                        if (alphaSeries[back] <= 0 || paintSeries[back] != zone)
                        {
                            paintSeries[back] = zone;
                            alphaSeries[back] = ComputeOpacity(zone, rsiSeries[back], threshold, extreme);
                        }
                    }

                    // Latch follow state
                    followZone = EnableFollowMode ? zone : 0;
                }
                else
                {
                    runConfirmed = false;

                    // Still following a previous confirmed band? Keep painting through
                    // this unconfirmed same-side or opposite pending zone.
                    if (EnableFollowMode && followZone != 0)
                        PaintFollow(rsiValue);
                    else
                    {
                        paintSeries[0] = 0;
                        alphaSeries[0] = 0;
                    }
                }
                return;
            }

            // ---------------- Neutral RSI ----------------
            zoneRunLength = 0;
            runDepthReached = false;
            runConfirmed = false;

            if (EnableFollowMode && followZone != 0)
            {
                // Release check: red releases when RSI falls below ReleaseLevel,
                // green releases when RSI rises above it.
                bool released = followZone == 1 ? rsiValue < ReleaseLevel
                                                : rsiValue > ReleaseLevel;
                if (released)
                {
                    followZone = 0;
                    paintSeries[0] = 0;
                    alphaSeries[0] = 0;
                }
                else
                    PaintFollow(rsiValue);
            }
            else
            {
                paintSeries[0] = 0;
                alphaSeries[0] = 0;
            }
        }

        /// <summary>Paints the current bar in follow state (steady reduced opacity; boost overlays).</summary>
        private void PaintFollow(double rsiValue)
        {
            paintSeries[0] = followZone;

            double op = FollowOpacityPct / 100.0;

            // Boost still overlays during the follow — a capitulation flush
            // (with-move extreme delta) is the exhaustion cue.
            if (State == State.Realtime && IsFollowBoosted(followZone))
                op = MaxOpacityPct / 100.0;

            alphaSeries[0] = op;
        }

        /// <summary>Returns opacity 0..1 for an in-zone bar, including boost logic.</summary>
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

        /// <summary>Boost while IN the extreme: opposing flow (reversal starting).</summary>
        private bool IsBoosted(int zone)
        {
            if (EnableDeltaBoost)
            {
                double effDelta = barDelta != 0 ? barDelta : prevBarDelta;
                if (zone == 1 && effDelta <= -DeltaBoostThreshold) return true;  // selling into OB
                if (zone == -1 && effDelta >= DeltaBoostThreshold) return true;  // buying into OS
            }

            if (EnableLevel2Boost)
            {
                double trig = ImbalanceTriggerPct / 100.0 * 2 - 1; // map 70% -> 0.4 imbalance
                if (zone == 1 && bookImbalance <= -trig) return true;  // asks dominant in OB
                if (zone == -1 && bookImbalance >= trig) return true;  // bids dominant in OS
            }

            return false;
        }

        /// <summary>Boost while FOLLOWING: with-move capitulation flush (exhaustion cue).
        /// Red follow (move down) boosts on extreme NEGATIVE delta (sellers puking into lows);
        /// green follow (move up) boosts on extreme POSITIVE delta (buyers chasing into highs).</summary>
        private bool IsFollowBoosted(int followZone)
        {
            if (!EnableDeltaBoost)
                return false;

            double effDelta = barDelta != 0 ? barDelta : prevBarDelta;
            if (followZone == 1 && effDelta <= -DeltaBoostThreshold) return true;
            if (followZone == -1 && effDelta >= DeltaBoostThreshold) return true;
            return false;
        }

        // ---------------- Level 1 tape: cumulative bar delta ----------------
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if ((!EnableDeltaBoost && !ShowStatusReadout) || e.MarketDataType != MarketDataType.Last)
                return;

            if (e.Price >= e.Ask)      barDelta += e.Volume;  // aggressive buy
            else if (e.Price <= e.Bid) barDelta -= e.Volume;  // aggressive sell
        }

        // ---------------- Level 2 book: throttled imbalance ----------------
        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (!EnableLevel2Boost || e.Position >= 10)
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
            if (dxObBrush != null)    { dxObBrush.Dispose();    dxObBrush = null; }
            if (dxOsBrush != null)    { dxOsBrush.Dispose();    dxOsBrush = null; }
            if (dxTextBrush != null)  { dxTextBrush.Dispose();  dxTextBrush = null; }
            if (dxPanelBrush != null) { dxPanelBrush.Dispose(); dxPanelBrush = null; }
        }

        private SharpDX.Direct2D1.SolidColorBrush MakeDxBrush(System.Windows.Media.Brush src, System.Windows.Media.Color fallback)
        {
            var solid = src as System.Windows.Media.SolidColorBrush;
            var c = solid != null ? solid.Color : fallback;
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

                double paintZone = paintSeries.GetValueAt(idx);
                if (paintZone == 0)
                    continue;

                float opacity = (float)alphaSeries.GetValueAt(idx);
                if (opacity <= 0f)
                    continue;

                float x = chartControl.GetXByBarIndex(ChartBars, idx);
                var rect = new RectangleF(x - halfBarWidth, top, halfBarWidth * 2f, height);

                var brush = paintZone > 0 ? dxObBrush : dxOsBrush;
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

            // Read from our own series — safe at render time (the RSI sub-indicator
            // indexers are unreliable from the render thread and can return price)
            double rsiValue = rsiSeries.GetValueAt(CurrentBar);
            double zone = zoneSeries.GetValueAt(CurrentBar);
            string zoneTxt = zone > 0 ? "OVERBOUGHT" : zone < 0 ? "OVERSOLD" : "NEUTRAL";

            // Pending-state detail: bars count and/or depth still unmet
            if (zone != 0 && !runConfirmed)
            {
                if (zoneRunLength < MinBarsInZone)
                    zoneTxt += " (" + zoneRunLength + "/" + MinBarsInZone + ")";
                if (MinRsiDepth > 0 && !runDepthReached)
                    zoneTxt += " (pending depth)";
            }

            // Follow state
            if (EnableFollowMode && followZone != 0 && !runConfirmed)
                zoneTxt += followZone == 1 ? "  >> FOLLOWING SHORT" : "  >> FOLLOWING LONG";

            double effDelta = barDelta != 0 ? barDelta : prevBarDelta;
            bool boosted = State == State.Realtime
                && ((zone != 0 && IsBoosted((int)zone))
                    || (followZone != 0 && !runConfirmed && IsFollowBoosted(followZone)));

            string bookTxt = !EnableLevel2Boost ? "off"
                : State == State.Realtime ? (bookImbalance * 100).ToString("+0;-0;0") + "% bid"
                : "n/a (hist)";

            string text =
                  "RSI(" + RsiPeriod + "): " + rsiValue.ToString("F1") + "  [" + zoneTxt + "]"
                + "\nDelta: " + (State == State.Realtime ? effDelta.ToString("+0;-0;0") : "n/a (hist)")
                + "\nBook:  " + bookTxt
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
