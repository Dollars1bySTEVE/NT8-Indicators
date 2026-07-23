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
    /// fast visuals (drawn behind the chart bars; ZOrder is re-enforced every
    /// render pass because NT8 can reshuffle Z-orders at runtime). Optional
    /// order-flow (delta) and Level 2 book-imbalance boosts intensify the tint
    /// when flow confirms the looming reversal. Optional on-chart status readout.
    ///
    /// Signal filtering (visuals only; ZoneState stays raw):
    ///  - Min Bars In Zone: zone must persist N consecutive bars (kills brief blips)
    ///  - Min RSI Depth: RSI must reach N points past the threshold during the run
    ///    (kills shallow zones). Thresholds define the ZONE; depth defines what's
    ///    WORTH SHOWING. Both back-fill retroactively on confirmation — and
    ///    retro-CLEAR if an intrabar confirmation flickers away before holding
    ///    (prevents "ghost" bands from one-tick confirmations under
    ///    Calculate.OnPriceChange).
    ///
    /// Flicker-proofing: under OnPriceChange a confirmation can pass for a single
    /// tick and die on the next. Two defenses:
    ///  - Follow latches are only taken from BAR-CLOSE-solid confirmations
    ///    (evaluated on the first tick of the next bar), never from intrabar
    ///    ticks — one-tick phantoms cannot spawn ghost follows.
    ///  - Retro-clear runs even while a follow is active, protecting only the
    ///    bars the active follow legitimately owns (from its origin bar forward).
    ///
    /// Follow Mode (optional, default OFF — preserves classic behavior):
    ///  Once a band confirms (bar-close solid), it latches and keeps painting at
    ///  a steady reduced opacity until one of two exits fires (Release Offset
    ///  from the 50 midline):
    ///   - EXHAUSTION: RSI crosses deep through the midline WITH the move
    ///     (red releases at RSI < 50-offset; green at RSI > 50+offset) — the
    ///     reversal ran its course.
    ///   - INVALIDATION: RSI recovers AGAINST the signal past the opposite side
    ///     (red releases at RSI > 50+offset; green at RSI < 50-offset) — the
    ///     reversal never came; signal failed, stop painting.
    ///  Delta boost still overlays during the follow, so a with-move capitulation
    ///  flush lights the band to max = exhaustion cue.
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
        private bool runFlushSeen;           // with-move opposing-flow flush seen during current run

        // Follow Mode state
        private int followZone;              // 0 = not following; else 1 / -1
        private int followOriginBar;         // CurrentBar index where the latched run started (paint ownership)

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
                 Description = "Once a band confirms (held to a bar close), it latches and keeps painting (reduced steady opacity) until the move exhausts (RSI crosses deep through the midline with the move) or the signal invalidates (RSI recovers against it). See Release Offset.")]
        public bool EnableFollowMode { get; set; }

        [NinjaScriptProperty, Range(0, 30)]
        [Display(Name = "Release Offset", GroupName = "2. Follow Mode", Order = 1,
                 Description = "Offset from the RSI 50 midline for both follow exits. Red follow: exhaustion release at RSI < (50 - offset), invalidation release at RSI > (50 + offset). Green mirrored. 0 = release at midline; 10 = red holds to 40 / invalidates at 60. Larger = follows run longer.")]
        public int ReleaseOffset { get; set; }

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
        [Display(Name = "Require Delta Confluence", GroupName = "4. Order Flow Boost", Order = 2,
                 Description = "Real-time only. Soft gate: a run that passed Min Bars + Min RSI Depth stays at Unconfirmed Opacity until an opposing-flow with-move flush occurs during that run.")]
        public bool RequireDeltaConfluence { get; set; }

        [NinjaScriptProperty, Range(1, 100000)]
        [Display(Name = "Confluence Threshold (contracts)", GroupName = "4. Order Flow Boost", Order = 3,
                 Description = "Net opposing delta required to count as a run-confirming flush (separate from Delta Boost Threshold so it can be lower).")]
        public int ConfluenceThreshold { get; set; }

        [NinjaScriptProperty, Range(0, 100)]
        [Display(Name = "Unconfirmed Opacity %", GroupName = "4. Order Flow Boost", Order = 4,
                 Description = "Fixed opacity for runs that passed bars+depth but are still pending the confluence flush.")]
        public int UnconfirmedOpacityPct { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Level 2 Boost (experimental)", GroupName = "4. Order Flow Boost", Order = 5,
                 Description = "Real-time only. Boosts tint when the resting book stacks against the extreme. Book data can be spoofed; treat as supplementary.")]
        public bool EnableLevel2Boost { get; set; }

        [NinjaScriptProperty, Range(1, 10)]
        [Display(Name = "L2 Depth Levels", GroupName = "4. Order Flow Boost", Order = 6)]
        public int DepthLevels { get; set; }

        [NinjaScriptProperty, Range(50, 95)]
        [Display(Name = "L2 Imbalance % Trigger", GroupName = "4. Order Flow Boost", Order = 7,
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
                Description = "V2: Continuous background warning light for RSI overbought (red) / oversold (green), SharpDX-rendered behind the bars, with persistence + depth filters, optional follow mode (exhaustion/invalidation releases, flicker-proof bar-close latching), order-flow boosts and status readout.";
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
                ReleaseOffset = 10;
                FollowOpacityPct = 20;

                UseGradientIntensity = true;
                MinOpacityPct = 5;
                MaxOpacityPct = 40;
                OverboughtColor = System.Windows.Media.Brushes.Crimson;
                OversoldColor = System.Windows.Media.Brushes.MediumSeaGreen;
                ShowStatusReadout = false;

                EnableDeltaBoost = true;
                DeltaBoostThreshold = 100;
                RequireDeltaConfluence = false;
                ConfluenceThreshold = 100;
                UnconfirmedOpacityPct = 15;
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
                followOriginBar = -1;

                if (MinOpacityPct > MaxOpacityPct)
                {
                    int t = MinOpacityPct; MinOpacityPct = MaxOpacityPct; MaxOpacityPct = t;
                }
            }
            else if (State == State.Historical)
            {
                // Render behind the chart bars so the tint never overpowers them.
                // (Also re-enforced every render pass — NT8 can reshuffle Z-orders
                // at runtime when indicators/templates change.)
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
                runFlushSeen = false;
                followZone = 0;
                followOriginBar = -1;
                return;
            }

            if (IsFirstTickOfBar)
            {
                prevBarDelta = barDelta;
                barDelta = 0;

                // ---- Bar-close-solid follow latch (flicker-proof) ----
                // Follows may ONLY latch from a confirmation that held through a
                // completed bar. Evaluate the just-closed bar (index 1): if its
                // run passed Min Bars + Min Depth at the close, latch the follow.
                // Intrabar tick-confirmations can paint live but can never latch —
                // so a one-tick phantom cannot spawn a ghost follow.
                if (EnableFollowMode && CurrentBar > 0)
                {
                    double closedZone = zoneSeries[1];
                    if (closedZone != 0)
                    {
                        int runLen; bool depthHeld; int originBar;
                        EvaluateRun(1, (int)closedZone, out runLen, out depthHeld, out originBar);

                        bool requiresFlush = RequireDeltaConfluence && State == State.Realtime;
                        if (runLen >= MinBarsInZone && depthHeld && (!requiresFlush || runFlushSeen))
                        {
                            followZone = (int)closedZone;
                            followOriginBar = originBar;
                        }
                    }
                }
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
                if (zoneSeries[1] != zone)
                    runFlushSeen = false;

                if (State == State.Realtime && IsConfluenceFlush(zone))
                    runFlushSeen = true;

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

                bool confirmedNow = run >= MinBarsInZone && depthOk;
                bool requiresFlush = RequireDeltaConfluence && State == State.Realtime;
                bool fullConfirmedNow = confirmedNow && (!requiresFlush || runFlushSeen);
                double unconfirmedOpacity = UnconfirmedOpacityPct / 100.0;

                if (confirmedNow)
                {
                    runConfirmed = fullConfirmedNow;
                    paintSeries[0] = zone;
                    alphaSeries[0] = fullConfirmedNow
                        ? ComputeOpacity(zone, rsiValue, threshold, extreme)
                        : unconfirmedOpacity;

                    // Retroactive fill: paint any unpainted bars of this run.
                    // If the run graduates from whisper to full, upgrade earlier
                    // whisper bars to full gradient opacity.
                    // NOTE: does NOT latch the follow — latching happens only from
                    // bar-close-solid confirmations (see IsFirstTickOfBar block).
                    for (int back = 1; back <= CurrentBar; back++)
                    {
                        if (zoneSeries[back] != zone) break;
                        bool isWhisperBar = requiresFlush
                            && paintSeries[back] == zone
                            && alphaSeries[back] > 0
                            && Math.Abs(alphaSeries[back] - unconfirmedOpacity) < 0.000001;

                        if (alphaSeries[back] <= 0 || paintSeries[back] != zone || (fullConfirmedNow && isWhisperBar))
                        {
                            paintSeries[back] = zone;
                            alphaSeries[back] = fullConfirmedNow
                                ? ComputeOpacity(zone, rsiSeries[back], threshold, extreme)
                                : unconfirmedOpacity;
                        }
                    }
                }
                else
                {
                    runConfirmed = false;

                    // Retroactive CLEAR: a tick-confirmation may have back-filled
                    // this run and then died. Un-paint the run's bars — but never
                    // touch bars owned by an active bar-close-solid follow (those
                    // are at or after followOriginBar).
                    for (int back = 1; back <= CurrentBar; back++)
                    {
                        if (zoneSeries[back] != zone) break;

                        int barIdx = CurrentBar - back;
                        bool ownedByFollow = EnableFollowMode && followZone != 0
                            && followOriginBar >= 0 && barIdx >= followOriginBar;

                        if (!ownedByFollow && paintSeries[back] == zone)
                        {
                            paintSeries[back] = 0;
                            alphaSeries[back] = 0;
                        }
                    }

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
            runFlushSeen = false;

            // Retroactive CLEAR on zone exit: if the just-ended run never held its
            // confirmation (an intrabar flicker back-filled it), un-paint it now.
            // Runs even while following — bars owned by the active follow (at or
            // after followOriginBar) are protected.
            if (CurrentBar > 0)
            {
                double prevZone = zoneSeries[1];
                if (prevZone != 0)
                {
                    int runLen; bool depthHeld; int originBar;
                    EvaluateRun(1, (int)prevZone, out runLen, out depthHeld, out originBar);

                    bool runWasValid = runLen >= MinBarsInZone && depthHeld;
                    if (!runWasValid)
                    {
                        for (int back = 1; back <= runLen; back++)
                        {
                            int barIdx = CurrentBar - back;
                            bool ownedByFollow = EnableFollowMode && followZone != 0
                                && followOriginBar >= 0 && barIdx >= followOriginBar;

                            if (!ownedByFollow && paintSeries[back] == prevZone)
                            {
                                paintSeries[back] = 0;
                                alphaSeries[back] = 0;
                            }
                        }
                    }
                }
            }

            if (EnableFollowMode && followZone != 0)
            {
                // Two release conditions, both offset from the 50 midline:
                //
                // EXHAUSTION — RSI crossed deep through the midline WITH the move:
                //   red (move down):  RSI < 50 - offset
                //   green (move up):  RSI > 50 + offset
                //
                // INVALIDATION — RSI recovered AGAINST the signal:
                //   red:   RSI > 50 + offset (rally resumed; reversal never came)
                //   green: RSI < 50 - offset (selloff resumed)
                double lowRelease  = 50 - ReleaseOffset;
                double highRelease = 50 + ReleaseOffset;

                bool released = followZone == 1
                    ? (rsiValue < lowRelease || rsiValue > highRelease)
                    : (rsiValue > highRelease || rsiValue < lowRelease);

                if (released)
                {
                    followZone = 0;
                    followOriginBar = -1;
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

        /// <summary>
        /// Walks the consecutive same-zone run ending at barsAgo (inclusive, going
        /// back in time). Returns its length, whether Min RSI Depth was reached,
        /// and the absolute CurrentBar-index of the run's first (oldest) bar.
        /// </summary>
        private void EvaluateRun(int startBarsAgo, int zone, out int runLen, out bool depthHeld, out int originBar)
        {
            runLen = 0;
            depthHeld = MinRsiDepth <= 0;
            double thr = zone == 1 ? OverboughtThreshold : OversoldThreshold;
            double dTarget = zone == 1 ? thr + MinRsiDepth : thr - MinRsiDepth;

            int back = startBarsAgo;
            for (; back <= CurrentBar; back++)
            {
                if (zoneSeries[back] != zone) break;
                runLen++;
                if (!depthHeld)
                {
                    double backRsi = rsiSeries[back];
                    if (zone == 1 ? backRsi >= dTarget : backRsi <= dTarget)
                        depthHeld = true;
                }
            }

            originBar = CurrentBar - (back - 1);
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

        /// <summary>
        /// Run-confluence flush while IN the extreme: opposing tape with the move
        /// against the extreme (capitulation).
        /// </summary>
        private bool IsConfluenceFlush(int zone)
        {
            double effDelta = barDelta != 0 ? barDelta : prevBarDelta;
            if (zone == 1 && effDelta <= -ConfluenceThreshold) return true; // selling into OB
            if (zone == -1 && effDelta >= ConfluenceThreshold) return true; // buying into OS
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

            // Self-healing ZOrder: NT8 can reshuffle Z-orders at runtime (indicator
            // add/remove, template apply, reconnect). If anything pushed us at or
            // above the chart bars, drop back below so the tint never covers them.
            if (ZOrder >= ChartBars.ZOrder)
                ZOrder = ChartBars.ZOrder - 1;

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

            if (State == State.Realtime
                && RequireDeltaConfluence
                && zone != 0
                && zoneRunLength >= MinBarsInZone
                && (MinRsiDepth <= 0 || runDepthReached)
                && !runFlushSeen)
                zoneTxt += " (pending flush)";

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
