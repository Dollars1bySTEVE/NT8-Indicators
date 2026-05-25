// IQ 1348 Legends — NinjaTrader 8 indicator replicating the "13/48 Legends v8.0" system.
// Three EMA lines (13, 48, 200), SharpDX GPU ribbon cloud, yellow low-volume candle filter,
// crossover signal labels with alerts, horizontal key price level grid, 200 EMA bias label,
// EMA price labels on the chart.
//
// Architecture notes:
// - EMA13 uses per-bar dynamic plot coloring (bull/bear vs EMA48) via PlotBrushes.
// - Ribbon cloud is rendered on the GPU in OnRender (no Draw.Region segment tags).
// - Signal markers/labels, bias label, and EMA labels are rendered on the GPU.
// - MaximumBarsLookBack uses 256 bars and default Calculate mode is OnPriceChange.
// - Signal generation includes a configurable minimum-bar gap filter.

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// IQ 1348 Legends — Full 13/48/200 EMA strategy indicator.
    ///
    /// Plot index map:
    ///   Values[0] = EMA 13  (fast, dynamic bull/bear color)
    ///   Values[1] = EMA 48  (slow, teal)
    ///   Values[2] = EMA 200 (macro, pink-red)
    /// </summary>
    public class IQ1348Legends : Indicator
    {
        private struct SignalEvent
        {
            public int BarIndex;
            public bool IsBull;
            public double Price;
        }

        // Cached EMA indicator instances (created in State.DataLoaded).
        private EMA _ema13;
        private EMA _ema48;
        private EMA _ema200;

        // Signal cache for GPU rendering. _signals is mutated on the data thread
        // (OnBarUpdate) and enumerated on the UI thread (OnRender). All access
        // must be protected by _signalsLock; readers should snapshot under the
        // lock before iterating.
        private readonly List<SignalEvent> _signals = new List<SignalEvent>();
        private readonly object _signalsLock = new object();
        private int _lastSignalBar = int.MinValue / 2;

        // Snapshot buffer reused by RenderSignals so we don't allocate on every
        // render frame while still iterating outside the lock.
        private readonly List<SignalEvent> _signalsSnapshot = new List<SignalEvent>(64);

        // Preallocated buffer reused by RenderEmaLabels to avoid per-frame
        // List<> allocations in the hot OnRender path.
        private readonly List<(string Text, SharpDX.Direct2D1.SolidColorBrush Brush, float Y)> _emaLabelBuffer
            = new List<(string, SharpDX.Direct2D1.SolidColorBrush, float)>(3);

        // Tracks the last key-level base used by Draw.HorizontalLine so the grid
        // is only refreshed when the bar closes or the base level actually moves
        // (Calculate.OnPriceChange would otherwise repaint on every tick).
        private double _lastKeyLevelBase = double.NaN;

        // Static array — avoids a heap allocation on every bar close.
        private static readonly int[] _keyLevelOffsets = { -2, -1, 0, 1, 2, 3, 4 };

        // SharpDX resources.
        private SharpDX.Direct2D1.SolidColorBrush _bullBrushDx;
        private SharpDX.Direct2D1.SolidColorBrush _bearBrushDx;

        private SharpDX.Direct2D1.SolidColorBrush _ema13LabelBrushDx;
        private SharpDX.Direct2D1.SolidColorBrush _ema48LabelBrushDx;
        private SharpDX.Direct2D1.SolidColorBrush _ema200LabelBrushDx;

        private SharpDX.Direct2D1.SolidColorBrush _biasBullBrushDx;
        private SharpDX.Direct2D1.SolidColorBrush _biasBearBrushDx;

        private SharpDX.Direct2D1.SolidColorBrush _signalBullBrushDx;
        private SharpDX.Direct2D1.SolidColorBrush _signalBearBrushDx;

        private SharpDX.DirectWrite.TextFormat _mainTextFormat;
        private SharpDX.DirectWrite.TextFormat _signalTextFormat;
        private int _lastSignalFontSize = -1;

        // ═══════════════════════════════════════════════════════════════
        #region Parameters — 1. EMA Lines

        [NinjaScriptProperty]
        [Display(Name = "Show EMA 13", Order = 1, GroupName = "1. EMA Lines",
            Description = "Draw the EMA(13) fast line.")]
        public bool ShowEma13 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA 13 Color", Order = 2, GroupName = "1. EMA Lines")]
        [XmlIgnore]
        public System.Windows.Media.Brush Ema13Color { get; set; }
        [Browsable(false)]
        public string Ema13ColorSerializable
        {
            get => Serialize.BrushToString(Ema13Color);
            set => Ema13Color = Serialize.StringToBrush(value);
        }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "EMA 13 Thickness", Order = 3, GroupName = "1. EMA Lines")]
        public int Ema13Thickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show EMA 48", Order = 4, GroupName = "1. EMA Lines",
            Description = "Draw the EMA(48) slow line.")]
        public bool ShowEma48 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA 48 Color", Order = 5, GroupName = "1. EMA Lines")]
        [XmlIgnore]
        public System.Windows.Media.Brush Ema48Color { get; set; }
        [Browsable(false)]
        public string Ema48ColorSerializable
        {
            get => Serialize.BrushToString(Ema48Color);
            set => Ema48Color = Serialize.StringToBrush(value);
        }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "EMA 48 Thickness", Order = 6, GroupName = "1. EMA Lines")]
        public int Ema48Thickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show EMA 200", Order = 7, GroupName = "1. EMA Lines",
            Description = "Draw the EMA(200) macro filter line.")]
        public bool ShowEma200 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA 200 Color", Order = 8, GroupName = "1. EMA Lines")]
        [XmlIgnore]
        public System.Windows.Media.Brush Ema200Color { get; set; }
        [Browsable(false)]
        public string Ema200ColorSerializable
        {
            get => Serialize.BrushToString(Ema200Color);
            set => Ema200Color = Serialize.StringToBrush(value);
        }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "EMA 200 Thickness", Order = 9, GroupName = "1. EMA Lines")]
        public int Ema200Thickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show EMA Labels", Order = 10, GroupName = "1. EMA Lines",
            Description = "Show EMA 13 / 48 / 200 price labels floating on the chart at the current bar.")]
        public bool ShowEmaLabels { get; set; }

        #endregion

        // ═══════════════════════════════════════════════════════════════
        #region Parameters — 2. Ribbon

        [NinjaScriptProperty]
        [Display(Name = "Show Ribbon", Order = 1, GroupName = "2. Ribbon",
            Description = "Draw filled cloud ribbon between EMA 13 and EMA 48.")]
        public bool ShowRibbon { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Ribbon Bull Color", Order = 2, GroupName = "2. Ribbon",
            Description = "Fill colour when EMA 13 > EMA 48  (bullish / LONG bias).")]
        [XmlIgnore]
        public System.Windows.Media.Brush RibbonBullColor { get; set; }
        [Browsable(false)]
        public string RibbonBullColorSerializable
        {
            get => Serialize.BrushToString(RibbonBullColor);
            set => RibbonBullColor = Serialize.StringToBrush(value);
        }

        [NinjaScriptProperty]
        [Display(Name = "Ribbon Bear Color", Order = 3, GroupName = "2. Ribbon",
            Description = "Fill colour when EMA 13 < EMA 48  (bearish / SHORT bias).")]
        [XmlIgnore]
        public System.Windows.Media.Brush RibbonBearColor { get; set; }
        [Browsable(false)]
        public string RibbonBearColorSerializable
        {
            get => Serialize.BrushToString(RibbonBearColor);
            set => RibbonBearColor = Serialize.StringToBrush(value);
        }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Ribbon Opacity %", Order = 4, GroupName = "2. Ribbon",
            Description = "Opacity of the ribbon cloud fill (1 = nearly transparent, 100 = solid).")]
        public int RibbonOpacity { get; set; }

        #endregion

        // ═══════════════════════════════════════════════════════════════
        #region Parameters — 3. Volume Filter

        [NinjaScriptProperty]
        [Display(Name = "Show Low-Volume Candles", Order = 1, GroupName = "3. Volume Filter",
            Description = "Paint candles yellow when volume is below threshold.")]
        public bool ShowLowVolumeCandles { get; set; }

        [NinjaScriptProperty]
        [Range(2, 200)]
        [Display(Name = "Volume Period", Order = 2, GroupName = "3. Volume Filter",
            Description = "SMA period used to compute average volume.")]
        public int VolumePeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 1.0)]
        [Display(Name = "Volume Threshold", Order = 3, GroupName = "3. Volume Filter",
            Description = "Candles with volume below (AvgVolume x threshold) are painted yellow.")]
        public double VolumeThreshold { get; set; }

        #endregion

        // ═══════════════════════════════════════════════════════════════
        #region Parameters — 4. Signals

        [NinjaScriptProperty]
        [Display(Name = "Show Signals", Order = 1, GroupName = "4. Signals",
            Description = "Draw arrow + text label on EMA 13/48 crossovers.")]
        public bool ShowSignals { get; set; }

        [NinjaScriptProperty]
        [Range(8, 32)]
        [Display(Name = "Signal Font Size", Order = 2, GroupName = "4. Signals",
            Description = "Font size for the LONG/SHORT signal text labels.  Default 14.")]
        public int SignalFontSize { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Signal Offset (ticks)", Order = 3, GroupName = "4. Signals",
            Description = "How many ticks below/above the bar the signal arrow and text appear.")]
        public int SignalOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Play Alert Sound", Order = 4, GroupName = "4. Signals",
            Description = "Fire an alert sound on each EMA 13/48 crossover.")]
        public bool PlayAlertSound { get; set; }

        [NinjaScriptProperty]
        [Range(0, 500)]
        [Display(Name = "Min Bars Between Signals", Order = 5, GroupName = "4. Signals",
            Description = "Suppress new crossover signals that occur within this many bars of the previous signal. 0 disables the filter.")]
        public int MinBarsBetweenSignals { get; set; }

        #endregion

        // ═══════════════════════════════════════════════════════════════
        #region Parameters — 5. Price Levels

        [NinjaScriptProperty]
        [Display(Name = "Show Price Levels", Order = 1, GroupName = "5. Price Levels",
            Description = "Draw horizontal key price level grid.")]
        public bool ShowPriceLevels { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 10000.0)]
        [Display(Name = "Level Spacing", Order = 2, GroupName = "5. Price Levels",
            Description = "Spacing between horizontal price level lines.")]
        public double LevelSpacing { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Level Color", Order = 3, GroupName = "5. Price Levels")]
        [XmlIgnore]
        public System.Windows.Media.Brush LevelColor { get; set; }
        [Browsable(false)]
        public string LevelColorSerializable
        {
            get => Serialize.BrushToString(LevelColor);
            set => LevelColor = Serialize.StringToBrush(value);
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════
        #region Parameters — 6. Info

        [NinjaScriptProperty]
        [Display(Name = "Show Bias Label", Order = 1, GroupName = "6. Info",
            Description = "Show ABOVE/BELOW 200 EMA macro bias label on the most recent bar.")]
        public bool ShowBiasLabel { get; set; }

        #endregion

        // ═══════════════════════════════════════════════════════════════
        #region OnStateChange

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "IQ 1348 Legends — 13/48/200 EMA strategy indicator with GPU ribbon, signals, volume filter, and EMA labels.";
                Name                     = "IQ1348Legends";
                Calculate                = Calculate.OnPriceChange;
                IsOverlay                = true;
                IsAutoScale              = false;
                DisplayInDataBox         = false;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = false;
                MaximumBarsLookBack      = MaximumBarsLookBack.TwoHundredFiftySix;

                // ── 1. EMA Lines ─────────────────────────────────────────────
                ShowEma13      = true;
                var ema13Brush = new System.Windows.Media.SolidColorBrush(Colors.LimeGreen);
                ema13Brush.Freeze();
                Ema13Color     = ema13Brush;
                Ema13Thickness = 2;

                ShowEma48      = true;
                var ema48Brush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(31, 188, 211));
                ema48Brush.Freeze();
                Ema48Color     = ema48Brush;
                Ema48Thickness = 2;

                ShowEma200      = true;
                var ema200Brush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(255, 80, 80));
                ema200Brush.Freeze();
                Ema200Color     = ema200Brush;
                Ema200Thickness = 1;

                ShowEmaLabels = true;

                // ── 2. Ribbon ─────────────────────────────────────────────────
                ShowRibbon = true;

                var bullBrush = new System.Windows.Media.SolidColorBrush(Colors.LimeGreen);
                bullBrush.Freeze();
                RibbonBullColor = bullBrush;

                var bearBrush = new System.Windows.Media.SolidColorBrush(Colors.OrangeRed);
                bearBrush.Freeze();
                RibbonBearColor = bearBrush;

                RibbonOpacity = 40;

                // ── 3. Volume Filter ──────────────────────────────────────────
                ShowLowVolumeCandles = true;
                VolumePeriod         = 20;
                VolumeThreshold      = 0.7;

                // ── 4. Signals ────────────────────────────────────────────────
                ShowSignals           = true;
                SignalFontSize        = 14;
                SignalOffsetTicks     = 6;
                PlayAlertSound        = false;
                MinBarsBetweenSignals = 5;

                // ── 5. Price Levels ───────────────────────────────────────────
                ShowPriceLevels = true;
                LevelSpacing    = 50.0;
                var levelBrush  = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(50, 255, 0, 0));
                levelBrush.Freeze();
                LevelColor = levelBrush;

                // ── 6. Info ───────────────────────────────────────────────────
                ShowBiasLabel = true;

                // ── Plots ──────────────────────────────────────────────────────
                AddPlot(new Stroke(Ema13Color, Ema13Thickness), PlotStyle.Line, "EMA13");
                AddPlot(new Stroke(Ema48Color, Ema48Thickness), PlotStyle.Line, "EMA48");
                AddPlot(new Stroke(Ema200Color, Ema200Thickness), PlotStyle.Line, "EMA200");
            }
            else if (State == State.DataLoaded)
            {
                Plots[0].Brush = Ema13Color;
                Plots[0].Width = Ema13Thickness;
                Plots[1].Brush = Ema48Color;
                Plots[1].Width = Ema48Thickness;
                Plots[2].Brush = Ema200Color;
                Plots[2].Width = Ema200Thickness;

                if (RibbonBullColor != null && !RibbonBullColor.IsFrozen && RibbonBullColor.CanFreeze)
                    RibbonBullColor.Freeze();
                if (RibbonBearColor != null && !RibbonBearColor.IsFrozen && RibbonBearColor.CanFreeze)
                    RibbonBearColor.Freeze();

                _ema13  = EMA(Close, 13);
                _ema48  = EMA(Close, 48);
                _ema200 = EMA(Close, 200);

                lock (_signalsLock)
                    _signals.Clear();
                _lastSignalBar = int.MinValue / 2;
                _lastKeyLevelBase = double.NaN;
            }
            else if (State == State.Terminated)
            {
                DisposeDxResources();
            }
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════
        #region OnRenderTargetChanged

        public override void OnRenderTargetChanged()
        {
            base.OnRenderTargetChanged();

            DisposeDxResources();

            if (RenderTarget == null)
                return;

            try
            {
                _bullBrushDx = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                    ToDxColor4(RibbonBullColor, RibbonOpacity / 100f));
                _bearBrushDx = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                    ToDxColor4(RibbonBearColor, RibbonOpacity / 100f));

                _ema13LabelBrushDx = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                    new SharpDX.Color4(0f, 1f, 0f, 1f));
                _ema48LabelBrushDx = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                    new SharpDX.Color4(31f / 255f, 188f / 255f, 211f / 255f, 1f));
                _ema200LabelBrushDx = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                    new SharpDX.Color4(1f, 80f / 255f, 80f / 255f, 1f));

                _biasBullBrushDx = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                    new SharpDX.Color4(0f, 1f, 0f, 1f));
                _biasBearBrushDx = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                    new SharpDX.Color4(1f, 69f / 255f, 0f, 1f));

                _signalBullBrushDx = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                    new SharpDX.Color4(0f, 1f, 0f, 1f));
                _signalBearBrushDx = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                    new SharpDX.Color4(1f, 69f / 255f, 0f, 1f));

                _mainTextFormat = new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory,
                    "Arial",
                    SharpDX.DirectWrite.FontWeight.Bold,
                    SharpDX.DirectWrite.FontStyle.Normal,
                    SharpDX.DirectWrite.FontStretch.Normal,
                    11f)
                {
                    TextAlignment = SharpDX.DirectWrite.TextAlignment.Leading
                };

                EnsureSignalTextFormat();
            }
            catch
            {
            }
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════
        #region OnBarUpdate

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 201)
                return;

            double ema13  = _ema13[0];
            double ema48  = _ema48[0];
            double ema200 = _ema200[0];

            // ── 1. EMA Lines ─────────────────────────────────────────────────
            Values[0][0] = ShowEma13 ? ema13 : double.NaN;
            Values[1][0] = ShowEma48 ? ema48 : double.NaN;
            Values[2][0] = ShowEma200 ? ema200 : double.NaN;

            System.Windows.Media.Brush ema13PlotBrush;
            if (ema13 > ema48)
                ema13PlotBrush = RibbonBullColor;
            else if (ema13 < ema48)
                ema13PlotBrush = RibbonBearColor;
            else if (CurrentBar > 0 && PlotBrushes[0][1] != null)
                ema13PlotBrush = PlotBrushes[0][1];
            else
                ema13PlotBrush = RibbonBullColor;

            PlotBrushes[0][0] = ema13PlotBrush;

            // ── 3. Yellow Low-Volume Candle Filter ───────────────────────────
            if (ShowLowVolumeCandles && CurrentBar >= VolumePeriod)
            {
                double avgVol = SMA(Volume, VolumePeriod)[0];
                BarBrushes[0] = Volume[0] < avgVol * VolumeThreshold ? Brushes.Yellow : null;
            }
            else
            {
                BarBrushes[0] = null;
            }

            // ── 4. EMA Crossover Signals ──────────────────────────────────────
            if (ShowSignals)
            {
                bool crossedAbove = CrossAbove(_ema13, _ema48, 1);
                bool crossedBelow = CrossBelow(_ema13, _ema48, 1);

                if (crossedAbove || crossedBelow)
                {
                    int barsSinceLastSignal = CurrentBar - _lastSignalBar;
                    bool gapAllowed = MinBarsBetweenSignals <= 0 || barsSinceLastSignal >= MinBarsBetweenSignals;

                    // Explicit per-bar guard: with Calculate.OnPriceChange, CrossAbove/CrossBelow
                    // can evaluate true on multiple intrabar ticks. Without this, MinBarsBetweenSignals = 0
                    // would unintentionally enable multiple signals/alerts on the same bar.
                    bool alreadyFiredThisBar = _lastSignalBar == CurrentBar;

                    if (gapAllowed && !alreadyFiredThisBar)
                    {
                        bool isBull = crossedAbove;

                        // Filter signals by EMA200 bias for accurate readings:
                        // LONG only when price is at or above EMA200; SHORT only when at or below.
                        bool biasAligned = isBull ? Close[0] >= ema200 : Close[0] <= ema200;

                        if (biasAligned)
                        {
                            double arrowOffset = SignalOffsetTicks * TickSize;
                            double signalPrice = isBull
                                ? Low[0] - arrowOffset
                                : High[0] + arrowOffset;

                            lock (_signalsLock)
                            {
                                _signals.Add(new SignalEvent
                                {
                                    BarIndex = CurrentBar,
                                    IsBull = isBull,
                                    Price = signalPrice
                                });
                            }

                            _lastSignalBar = CurrentBar;

                            if (PlayAlertSound)
                            {
                                if (isBull)
                                    Alert("CrossAlertLong", Priority.Medium, "EMA Cross - Long signal",
                                        NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav",
                                        10, Brushes.Yellow, Brushes.Black);
                                else
                                    Alert("CrossAlertShort", Priority.Medium, "EMA Cross - Short signal",
                                        NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav",
                                        10, Brushes.Yellow, Brushes.Black);
                            }
                        }
                    }
                }
            }

            lock (_signalsLock)
                _signals.RemoveAll(s => CurrentBar - s.BarIndex > 256);

            // ── 5. Horizontal Key Price Level Grid ───────────────────────────
            if (ShowPriceLevels && LevelSpacing > 0)
            {
                double baseLevel = Math.Floor(Close[0] / LevelSpacing) * LevelSpacing;

                // With Calculate.OnPriceChange, OnBarUpdate fires on every tick.
                // Only repaint the grid when the bar closes or when the computed
                // base level actually moves — otherwise Draw.HorizontalLine would
                // be called on every tick for no visible change.
                if (IsFirstTickOfBar || baseLevel != _lastKeyLevelBase)
                {
                    foreach (int offset in _keyLevelOffsets)
                    {
                        double level = baseLevel + offset * LevelSpacing;
                        Draw.HorizontalLine(this, "KL_" + offset.ToString(), level,
                            LevelColor, DashStyleHelper.Solid, 1);
                    }
                    _lastKeyLevelBase = baseLevel;
                }
            }
            else
            {
                foreach (int offset in _keyLevelOffsets)
                    RemoveDrawObject("KL_" + offset.ToString());
                _lastKeyLevelBase = double.NaN;
            }
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════
        #region OnRender

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (Bars == null || ChartBars == null || chartControl == null || chartScale == null || RenderTarget == null)
                return;

            try
            {
                EnsureSignalTextFormat();

                if (ShowRibbon)
                    RenderRibbon(chartControl, chartScale);

                RenderEmaLabels(chartControl, chartScale);
                RenderBiasLabel(chartControl, chartScale);
                RenderSignals(chartControl, chartScale);
            }
            catch
            {
            }
        }

        private void RenderRibbon(ChartControl chartControl, ChartScale chartScale)
        {
            if (_bullBrushDx == null || _bearBrushDx == null)
                return;

            int firstIdx = ChartBars.FromIndex;
            int lastIdx = ChartBars.ToIndex;
            if (lastIdx < 200)
                return;

            int startIdx = Math.Max(firstIdx, 200);
            int endIdx = lastIdx - 1;
            for (int i = startIdx; i <= endIdx; i++)
            {
                double ema13Left = _ema13.GetValueAt(i);
                double ema13Right = _ema13.GetValueAt(i + 1);
                double ema48Left = _ema48.GetValueAt(i);
                double ema48Right = _ema48.GetValueAt(i + 1);

                if (double.IsNaN(ema13Left) || double.IsInfinity(ema13Left)
                    || double.IsNaN(ema13Right) || double.IsInfinity(ema13Right)
                    || double.IsNaN(ema48Left) || double.IsInfinity(ema48Left)
                    || double.IsNaN(ema48Right) || double.IsInfinity(ema48Right))
                    continue;

                bool isBull = ema13Left > ema48Left;
                bool rightIsBull = ema13Right > ema48Right;
                if (rightIsBull != isBull)
                    continue;

                float xLeft = chartControl.GetXByBarIndex(ChartBars, i);
                float xRight = chartControl.GetXByBarIndex(ChartBars, i + 1);
                float y13Left = chartScale.GetYByValue(ema13Left);
                float y13Right = chartScale.GetYByValue(ema13Right);
                float y48Left = chartScale.GetYByValue(ema48Left);
                float y48Right = chartScale.GetYByValue(ema48Right);

                if (float.IsNaN(y13Left) || float.IsInfinity(y13Left)
                    || float.IsNaN(y13Right) || float.IsInfinity(y13Right)
                    || float.IsNaN(y48Left) || float.IsInfinity(y48Left)
                    || float.IsNaN(y48Right) || float.IsInfinity(y48Right))
                    continue;

                using (var geometry = new SharpDX.Direct2D1.PathGeometry(RenderTarget.Factory))
                using (var sink = geometry.Open())
                {
                    sink.SetFillMode(SharpDX.Direct2D1.FillMode.Winding);
                    sink.BeginFigure(new SharpDX.Vector2(xLeft, y13Left), SharpDX.Direct2D1.FigureBegin.Filled);
                    sink.AddLine(new SharpDX.Vector2(xRight, y13Right));
                    sink.AddLine(new SharpDX.Vector2(xRight, y48Right));
                    sink.AddLine(new SharpDX.Vector2(xLeft, y48Left));
                    sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
                    sink.Close();

                    RenderTarget.FillGeometry(geometry, isBull ? _bullBrushDx : _bearBrushDx);
                }
            }
        }

        private void RenderEmaLabels(ChartControl chartControl, ChartScale chartScale)
        {
            if (!ShowEmaLabels || _mainTextFormat == null)
                return;

            int barIndex = ChartBars.ToIndex;
            if (barIndex < 0)
                barIndex = 0;
            if (barIndex > CurrentBar)
                barIndex = CurrentBar;
            if (barIndex < 200)
                return;

            float x = chartControl.GetXByBarIndex(ChartBars, barIndex) + 6f;
            const float minSpacing = 18f;
            var labels = _emaLabelBuffer;
            labels.Clear();

            if (ShowEma13 && _ema13LabelBrushDx != null)
            {
                double value = _ema13.GetValueAt(barIndex);
                float y = chartScale.GetYByValue(value) - 8f;
                labels.Add(("EMA13  " + value.ToString("F2"), _ema13LabelBrushDx, y));
            }

            if (ShowEma48 && _ema48LabelBrushDx != null)
            {
                double value = _ema48.GetValueAt(barIndex);
                float y = chartScale.GetYByValue(value) - 8f;
                labels.Add(("EMA48  " + value.ToString("F2"), _ema48LabelBrushDx, y));
            }

            if (ShowEma200 && _ema200LabelBrushDx != null)
            {
                double value = _ema200.GetValueAt(barIndex);
                float y = chartScale.GetYByValue(value) - 8f;
                labels.Add(("EMA200 " + value.ToString("F2"), _ema200LabelBrushDx, y));
            }

            labels.Sort((a, b) => a.Y.CompareTo(b.Y));

            for (int k = 1; k < labels.Count; k++)
            {
                float minY = labels[k - 1].Y + minSpacing;
                if (labels[k].Y < minY)
                    labels[k] = (labels[k].Text, labels[k].Brush, minY);
            }

            foreach (var label in labels)
            {
                if (float.IsNaN(label.Y) || float.IsInfinity(label.Y))
                    continue;

                RenderTarget.DrawText(
                    label.Text,
                    _mainTextFormat,
                    new SharpDX.RectangleF(x, label.Y, 220f, 24f),
                    label.Brush);
            }
        }

        private void RenderBiasLabel(ChartControl chartControl, ChartScale chartScale)
        {
            if (!ShowBiasLabel || _mainTextFormat == null || _biasBullBrushDx == null || _biasBearBrushDx == null)
                return;

            int lastVisible = ChartBars.ToIndex;
            if (lastVisible < 0 || lastVisible > CurrentBar)
                return;

            float x = chartControl.GetXByBarIndex(ChartBars, lastVisible) + 6f;
            float y = chartScale.GetYByValue(High.GetValueAt(lastVisible) + 10 * TickSize) - 12f;

            // Both position and label text/colour must reference the same bar
            // (lastVisible) so that scrolling the chart shows a consistent bias.
            double closeAtVisible = Close.GetValueAt(lastVisible);
            double ema200AtVisible = _ema200.GetValueAt(lastVisible);
            bool isBull = closeAtVisible > ema200AtVisible;
            RenderTarget.DrawText(
                isBull ? "ABOVE 200 ▲" : "BELOW 200 ▼",
                _mainTextFormat,
                new SharpDX.RectangleF(x, y, 220f, 26f),
                isBull ? _biasBullBrushDx : _biasBearBrushDx);
        }

        private void RenderSignals(ChartControl chartControl, ChartScale chartScale)
        {
            if (!ShowSignals || _signalTextFormat == null || _signalBullBrushDx == null || _signalBearBrushDx == null)
                return;

            int firstIdx = ChartBars.FromIndex;
            int lastIdx = ChartBars.ToIndex;
            float triHalfWidth = 6f;
            float triHeight = 10f;
            double textDelta = SignalOffsetTicks * 1.5 * TickSize;

            // Snapshot under lock so the data thread's Add/RemoveAll in
            // OnBarUpdate can't mutate _signals while we iterate (which would
            // throw InvalidOperationException and be swallowed by OnRender's
            // try/catch, making signals disappear intermittently).
            _signalsSnapshot.Clear();
            lock (_signalsLock)
            {
                if (_signalsSnapshot.Capacity < _signals.Count)
                    _signalsSnapshot.Capacity = _signals.Count;
                _signalsSnapshot.AddRange(_signals);
            }

            foreach (var signal in _signalsSnapshot)
            {
                if (signal.BarIndex < firstIdx || signal.BarIndex > lastIdx)
                    continue;

                float x = chartControl.GetXByBarIndex(ChartBars, signal.BarIndex);
                float yArrow = chartScale.GetYByValue(signal.Price);

                using (var tri = new SharpDX.Direct2D1.PathGeometry(RenderTarget.Factory))
                using (var sink = tri.Open())
                {
                    if (signal.IsBull)
                    {
                        sink.BeginFigure(new SharpDX.Vector2(x, yArrow - triHeight * 0.5f), SharpDX.Direct2D1.FigureBegin.Filled);
                        sink.AddLine(new SharpDX.Vector2(x - triHalfWidth, yArrow + triHeight * 0.5f));
                        sink.AddLine(new SharpDX.Vector2(x + triHalfWidth, yArrow + triHeight * 0.5f));
                    }
                    else
                    {
                        sink.BeginFigure(new SharpDX.Vector2(x, yArrow + triHeight * 0.5f), SharpDX.Direct2D1.FigureBegin.Filled);
                        sink.AddLine(new SharpDX.Vector2(x - triHalfWidth, yArrow - triHeight * 0.5f));
                        sink.AddLine(new SharpDX.Vector2(x + triHalfWidth, yArrow - triHeight * 0.5f));
                    }

                    sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
                    sink.Close();

                    RenderTarget.FillGeometry(tri, signal.IsBull ? _signalBullBrushDx : _signalBearBrushDx);
                }

                double textPrice = signal.IsBull ? signal.Price - textDelta : signal.Price + textDelta;
                float yText = chartScale.GetYByValue(textPrice) - 9f;
                RenderTarget.DrawText(
                    signal.IsBull ? "LONG ▲" : "SHORT ▼",
                    _signalTextFormat,
                    new SharpDX.RectangleF(x - 80f, yText, 160f, SignalFontSize + 12f),
                    signal.IsBull ? _signalBullBrushDx : _signalBearBrushDx);
            }
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════
        #region Helpers

        /// <summary>
        /// Converts a WPF brush into a SharpDX Color4, applying the supplied alpha.
        /// Only <see cref="System.Windows.Media.SolidColorBrush"/> is supported —
        /// any other brush type (gradient, image, etc.) is treated as opaque white
        /// because the ribbon parameters are exposed as solid-colour pickers.
        /// </summary>
        private static SharpDX.Color4 ToDxColor4(System.Windows.Media.Brush brush, float alpha)
        {
            var scb = brush as System.Windows.Media.SolidColorBrush;
            if (scb == null)
                return new SharpDX.Color4(1f, 1f, 1f, alpha);

            System.Windows.Media.Color c = scb.Color;
            // Combine the brush's own Opacity with the caller-supplied alpha so
            // partially-transparent SolidColorBrush inputs render correctly.
            float effectiveAlpha = alpha * (float)scb.Opacity * (c.A / 255f);
            if (effectiveAlpha < 0f) effectiveAlpha = 0f;
            else if (effectiveAlpha > 1f) effectiveAlpha = 1f;
            return new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, effectiveAlpha);
        }

        private void EnsureSignalTextFormat()
        {
            if (RenderTarget == null)
                return;

            if (_signalTextFormat != null && _lastSignalFontSize == SignalFontSize)
                return;

            if (_signalTextFormat != null)
            {
                _signalTextFormat.Dispose();
                _signalTextFormat = null;
            }

            _signalTextFormat = new SharpDX.DirectWrite.TextFormat(
                NinjaTrader.Core.Globals.DirectWriteFactory,
                "Arial",
                SharpDX.DirectWrite.FontWeight.Bold,
                SharpDX.DirectWrite.FontStyle.Normal,
                SharpDX.DirectWrite.FontStretch.Normal,
                SignalFontSize)
            {
                TextAlignment = SharpDX.DirectWrite.TextAlignment.Center
            };

            _lastSignalFontSize = SignalFontSize;
        }

        private void DisposeDxResources()
        {
            if (_bullBrushDx != null) { _bullBrushDx.Dispose(); _bullBrushDx = null; }
            if (_bearBrushDx != null) { _bearBrushDx.Dispose(); _bearBrushDx = null; }

            if (_ema13LabelBrushDx != null) { _ema13LabelBrushDx.Dispose(); _ema13LabelBrushDx = null; }
            if (_ema48LabelBrushDx != null) { _ema48LabelBrushDx.Dispose(); _ema48LabelBrushDx = null; }
            if (_ema200LabelBrushDx != null) { _ema200LabelBrushDx.Dispose(); _ema200LabelBrushDx = null; }

            if (_biasBullBrushDx != null) { _biasBullBrushDx.Dispose(); _biasBullBrushDx = null; }
            if (_biasBearBrushDx != null) { _biasBearBrushDx.Dispose(); _biasBearBrushDx = null; }

            if (_signalBullBrushDx != null) { _signalBullBrushDx.Dispose(); _signalBullBrushDx = null; }
            if (_signalBearBrushDx != null) { _signalBearBrushDx.Dispose(); _signalBearBrushDx = null; }

            if (_mainTextFormat != null) { _mainTextFormat.Dispose(); _mainTextFormat = null; }
            if (_signalTextFormat != null) { _signalTextFormat.Dispose(); _signalTextFormat = null; }
        }

        #endregion
    }
}
