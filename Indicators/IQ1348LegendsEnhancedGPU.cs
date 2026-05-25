// IQ 1348 Legends Enhanced GPU — NinjaTrader 8 indicator replicating the "13/48 Legends v8.0" system with SharpDX rendering.
// Three EMA lines (13, 48, 200), dynamic ribbon cloud, yellow low-volume candle filter,
// crossover signal labels with alerts, horizontal key price level grid, 200 EMA bias label,
// EMA price labels on the chart.
//
// RIBBON FIX: Each bull/bear segment gets its own uniquely-tagged Draw.Region so the cloud
// colour is historically correct — green during long bias, red/orange during short bias.
// Previously a single shared tag was overwritten on every bar, making the whole lookback
// render with the *current* bias colour instead of the correct per-segment colour.
//
// Uses Draw.Region() for the ribbon and SharpDX GPU rendering for the remaining visuals.

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
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// IQ 1348 Legends Enhanced GPU — Full 13/48/200 EMA strategy indicator.
    ///
    /// Plot index map:
    ///   Values[0] = EMA 13  (transparent plot; rendered in SharpDX)
    ///   Values[1] = EMA 48  (transparent plot; rendered in SharpDX)
    ///   Values[2] = EMA 200 (transparent plot; rendered in SharpDX)
    ///   Values[3] = RibbonUpper (transparent, anchors Draw.Region upper edge = EMA13)
    ///   Values[4] = RibbonLower (transparent, anchors Draw.Region lower edge = EMA48)
    /// </summary>
    public class IQ1348LegendsEnhancedGPU : Indicator
    {
        // ── Ribbon segment tracking ──────────────────────────────────────────
        private int          _segmentStartBar = -1;
        private bool         _segmentIsBull   = true;
        private List<string> _ribbonTags      = new List<string>();

        // Cached indicator instances / per-bar state.
        private EMA        _ema13;
        private EMA        _ema48;
        private EMA        _ema200;
        private SMA        _volumeSma;
        private Series<int> _signalDirection;
        private Series<int> _lowVolumeFlags;

        // Static array — avoids a heap allocation on every bar close.
        private static readonly int[] _keyLevelOffsets = { -2, -1, 0, 1, 2, 3, 4 };

        // ── SharpDX resources ────────────────────────────────────────────────
        private bool dxReady;
        private SharpDX.DirectWrite.Factory    dxWriteFactory;
        private SharpDX.DirectWrite.TextFormat dxLabelFormat;
        private SharpDX.DirectWrite.TextFormat dxSignalFormat;

        private SharpDX.Direct2D1.SolidColorBrush dxEma13BullBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxEma13BearBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxEma48Brush;
        private SharpDX.Direct2D1.SolidColorBrush dxEma200Brush;
        private SharpDX.Direct2D1.SolidColorBrush dxLevelBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxSignalBullBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxSignalBearBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBiasLabelBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxYellowCandleBrush;

        // ═══════════════════════════════════════════════════════════════
        #region Parameters

        [NinjaScriptProperty]
        [Display(Name = "Show EMA 13", Order = 1, GroupName = "1. EMA Lines",
            Description = "Draw the EMA(13) fast line.")]
        public bool ShowEma13 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA 13 Bull Color", Order = 2, GroupName = "1. EMA Lines",
            Description = "EMA(13) line colour when EMA 13 > EMA 48 (bullish bias).")]
        [XmlIgnore]
        public System.Windows.Media.Brush Ema13BullColor { get; set; }
        [Browsable(false)]
        public string Ema13BullColorSerializable
        {
            get => Serialize.BrushToString(Ema13BullColor);
            set => Ema13BullColor = Serialize.StringToBrush(value);
        }

        [NinjaScriptProperty]
        [Display(Name = "EMA 13 Bear Color", Order = 3, GroupName = "1. EMA Lines",
            Description = "EMA(13) line colour when EMA 13 < EMA 48 (bearish bias).")]
        [XmlIgnore]
        public System.Windows.Media.Brush Ema13BearColor { get; set; }
        [Browsable(false)]
        public string Ema13BearColorSerializable
        {
            get => Serialize.BrushToString(Ema13BearColor);
            set => Ema13BearColor = Serialize.StringToBrush(value);
        }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "EMA 13 Thickness", Order = 4, GroupName = "1. EMA Lines")]
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
                Description              = "IQ 1348 Legends Enhanced GPU — 13/48/200 EMA strategy indicator with per-segment colour ribbon, signals, volume filter, EMA labels, and SharpDX rendering.";
                Name                     = "IQ1348 Legends Enhanced GPU";
                Calculate                = Calculate.OnBarClose;
                IsOverlay                = true;
                IsAutoScale              = false;
                DisplayInDataBox         = false;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = false;
                MaximumBarsLookBack      = MaximumBarsLookBack.Infinite;

                ShowEma13      = true;
                var ema13BullBrush = new System.Windows.Media.SolidColorBrush(Colors.LimeGreen);
                ema13BullBrush.Freeze();
                Ema13BullColor = ema13BullBrush;
                var ema13BearBrush = new System.Windows.Media.SolidColorBrush(Colors.OrangeRed);
                ema13BearBrush.Freeze();
                Ema13BearColor = ema13BearBrush;
                Ema13Thickness = 2;

                ShowEma48      = true;
                var ema48Brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(31, 188, 211));
                ema48Brush.Freeze();
                Ema48Color     = ema48Brush;
                Ema48Thickness = 2;

                ShowEma200      = true;
                var ema200Brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 80, 80));
                ema200Brush.Freeze();
                Ema200Color     = ema200Brush;
                Ema200Thickness = 1;

                ShowEmaLabels = true;

                ShowRibbon = true;

                var bullBrush = new System.Windows.Media.SolidColorBrush(Colors.LimeGreen);
                bullBrush.Freeze();
                RibbonBullColor = bullBrush;

                var bearBrush = new System.Windows.Media.SolidColorBrush(Colors.OrangeRed);
                bearBrush.Freeze();
                RibbonBearColor = bearBrush;

                RibbonOpacity = 40;

                ShowLowVolumeCandles = true;
                VolumePeriod         = 20;
                VolumeThreshold      = 0.7;

                ShowSignals       = true;
                SignalFontSize    = 14;
                SignalOffsetTicks = 6;
                PlayAlertSound    = false;

                ShowPriceLevels = true;
                LevelSpacing    = 50.0;
                var levelBrush  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(50, 255, 0, 0));
                levelBrush.Freeze();
                LevelColor = levelBrush;

                ShowBiasLabel = true;

                AddPlot(new Stroke(Brushes.Transparent, Ema13Thickness), PlotStyle.Line, "EMA13");
                AddPlot(new Stroke(Brushes.Transparent, Ema48Thickness), PlotStyle.Line, "EMA48");
                AddPlot(new Stroke(Brushes.Transparent, Ema200Thickness), PlotStyle.Line, "EMA200");
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "RibbonUpper");
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "RibbonLower");
            }
            else if (State == State.DataLoaded)
            {
                Plots[0].Brush = Brushes.Transparent;
                Plots[1].Brush = Brushes.Transparent;
                Plots[2].Brush = Brushes.Transparent;
                Plots[3].Brush = Brushes.Transparent;
                Plots[4].Brush = Brushes.Transparent;

                _ema13       = EMA(Close, 13);
                _ema48       = EMA(Close, 48);
                _ema200      = EMA(Close, 200);
                _volumeSma   = SMA(Volume, VolumePeriod);
                _signalDirection = new Series<int>(this, MaximumBarsLookBack.Infinite);
                _lowVolumeFlags  = new Series<int>(this, MaximumBarsLookBack.Infinite);

                _segmentStartBar = -1;
                _ribbonTags.Clear();
            }
            else if (State == State.Terminated)
            {
                DisposeDXResources();
            }
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════
        #region OnBarUpdate

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 201)
            {
                Values[0][0] = double.NaN;
                Values[1][0] = double.NaN;
                Values[2][0] = double.NaN;
                Values[3][0] = double.NaN;
                Values[4][0] = double.NaN;

                if (_signalDirection != null)
                    _signalDirection[0] = 0;
                if (_lowVolumeFlags != null)
                    _lowVolumeFlags[0] = 0;

                BarBrushes[0] = null;
                return;
            }

            double ema13  = _ema13[0];
            double ema48  = _ema48[0];
            double ema200 = _ema200[0];

            Values[0][0] = ShowEma13  ? ema13  : double.NaN;
            Values[1][0] = ShowEma48  ? ema48  : double.NaN;
            Values[2][0] = ShowEma200 ? ema200 : double.NaN;

            Values[3][0] = ema13;
            Values[4][0] = ema48;

            if (ShowRibbon)
            {
                bool isBull   = ema13 > ema48;
                bool prevBull = _ema13[1] > _ema48[1];

                if (_segmentStartBar < 0)
                {
                    _segmentStartBar = CurrentBar;
                    _segmentIsBull   = isBull;
                }

                if (isBull != prevBull)
                {
                    int endBarsAgo   = 1;
                    int startBarsAgo = Math.Min(CurrentBar - _segmentStartBar, 254);

                    if (startBarsAgo >= endBarsAgo)
                    {
                        string sealTag = "Ribbon_seg_" + _segmentStartBar;
                        _ribbonTags.Add(sealTag);
                        Draw.Region(this, sealTag,
                            endBarsAgo, startBarsAgo,
                            Values[3], Values[4],
                            Brushes.Transparent,
                            _segmentIsBull ? RibbonBullColor : RibbonBearColor,
                            RibbonOpacity);
                    }

                    _segmentStartBar = CurrentBar;
                    _segmentIsBull   = isBull;
                }

                int currentLen = Math.Min(CurrentBar - _segmentStartBar, 254);
                Draw.Region(this, "RibbonCurrent",
                    0, currentLen,
                    Values[3], Values[4],
                    Brushes.Transparent,
                    isBull ? RibbonBullColor : RibbonBearColor,
                    RibbonOpacity);
            }
            else
            {
                RemoveDrawObject("RibbonCurrent");
                foreach (string tag in _ribbonTags)
                    RemoveDrawObject(tag);
                _ribbonTags.Clear();
                _segmentStartBar = -1;
            }

            _lowVolumeFlags[0] = 0;
            if (ShowLowVolumeCandles && CurrentBar >= VolumePeriod)
            {
                double avgVol = _volumeSma[0];
                if (avgVol > 0 && Volume[0] < avgVol * VolumeThreshold)
                    _lowVolumeFlags[0] = 1;
            }

            BarBrushes[0] = null;

            _signalDirection[0] = 0;
            if (ShowSignals)
            {
                if (CrossAbove(_ema13, _ema48, 1))
                {
                    _signalDirection[0] = 1;

                    if (PlayAlertSound)
                        Alert("CrossAlertLong", Priority.Medium, "EMA Cross - Long signal",
                            NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav",
                            10, Brushes.Yellow, Brushes.Black);
                }
                else if (CrossBelow(_ema13, _ema48, 1))
                {
                    _signalDirection[0] = -1;

                    if (PlayAlertSound)
                        Alert("CrossAlertShort", Priority.Medium, "EMA Cross - Short signal",
                            NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav",
                            10, Brushes.Yellow, Brushes.Black);
                }
            }
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════
        #region OnRenderTargetChanged

        public override void OnRenderTargetChanged()
        {
            DisposeDXResources();
            dxReady = false;
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════
        #region OnRender

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
                    Print("IQ1348LegendsEnhancedGPU: Unexpected exception from CreateDXResources: " + ex.Message);
                    return;
                }
            }

            if (!dxReady)
                return;

            int fromBar = ChartBars.FromIndex;
            int toBar   = ChartBars.ToIndex;

            if (fromBar > toBar)
                return;

            float chartWidth = RenderTarget.Size.Width;

            try { RenderPriceLevels(chartControl, chartScale, chartWidth); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQ1348LegendsEnhancedGPU: SharpDX error RenderPriceLevels: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQ1348LegendsEnhancedGPU: RenderPriceLevels [" + ex.GetType().Name + "]: " + ex.Message); }

            try { RenderLowVolumeCandles(chartControl, chartScale, fromBar, toBar); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQ1348LegendsEnhancedGPU: SharpDX error RenderLowVolumeCandles: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQ1348LegendsEnhancedGPU: RenderLowVolumeCandles [" + ex.GetType().Name + "]: " + ex.Message); }

            try { RenderEmaLines(chartControl, chartScale, fromBar, toBar); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQ1348LegendsEnhancedGPU: SharpDX error RenderEmaLines: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQ1348LegendsEnhancedGPU: RenderEmaLines [" + ex.GetType().Name + "]: " + ex.Message); }

            try { RenderSignals(chartControl, chartScale, fromBar, toBar); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQ1348LegendsEnhancedGPU: SharpDX error RenderSignals: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQ1348LegendsEnhancedGPU: RenderSignals [" + ex.GetType().Name + "]: " + ex.Message); }

            try { RenderBiasLabel(chartControl, chartScale, fromBar, toBar); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQ1348LegendsEnhancedGPU: SharpDX error RenderBiasLabel: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQ1348LegendsEnhancedGPU: RenderBiasLabel [" + ex.GetType().Name + "]: " + ex.Message); }

            try { RenderEmaLabels(chartControl, chartScale, fromBar, toBar); }
            catch (SharpDX.SharpDXException sdxEx) { Print("IQ1348LegendsEnhancedGPU: SharpDX error RenderEmaLabels: " + sdxEx.Message); dxReady = false; DisposeDXResources(); return; }
            catch (Exception ex) { Print("IQ1348LegendsEnhancedGPU: RenderEmaLabels [" + ex.GetType().Name + "]: " + ex.Message); }
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════
        #region Render Helpers

        private void RenderEmaLines(ChartControl cc, ChartScale cs, int fromBar, int toBar)
        {
            if (RenderTarget == null)
                return;

            if (ShowEma13 && CurrentBar >= 13)
            {
                for (int barIdx = fromBar; barIdx <= toBar; barIdx++)
                {
                    int nextBar = barIdx + 1;
                    if (barIdx < 0 || nextBar >= Bars.Count)
                        continue;

                    int barsAgo0 = CurrentBar - barIdx;
                    int barsAgo1 = CurrentBar - nextBar;
                    if (barsAgo0 < 0 || barsAgo1 < 0)
                        continue;

                    double ema13_0 = _ema13[barsAgo0];
                    double ema13_1 = _ema13[barsAgo1];
                    double ema48_1 = _ema48[barsAgo1];
                    if (double.IsNaN(ema13_0) || double.IsNaN(ema13_1) || double.IsNaN(ema48_1))
                        continue;

                    float x0 = cc.GetXByBarIndex(ChartBars, barIdx);
                    float x1 = cc.GetXByBarIndex(ChartBars, nextBar);
                    float y0 = cs.GetYByValue(ema13_0);
                    float y1 = cs.GetYByValue(ema13_1);

                    var brush = ema13_1 > ema48_1 ? dxEma13BullBrush : dxEma13BearBrush;
                    RenderTarget.DrawLine(new SharpDX.Vector2(x0, y0), new SharpDX.Vector2(x1, y1), brush, Ema13Thickness);
                }
            }

            if (ShowEma48 && CurrentBar >= 48)
                RenderStaticEmaLine(cc, cs, fromBar, toBar, _ema48, dxEma48Brush, Ema48Thickness);

            if (ShowEma200 && CurrentBar >= 200)
                RenderStaticEmaLine(cc, cs, fromBar, toBar, _ema200, dxEma200Brush, Ema200Thickness);
        }

        private void RenderStaticEmaLine(ChartControl cc, ChartScale cs, int fromBar, int toBar, ISeries<double> series, SharpDX.Direct2D1.SolidColorBrush brush, float thickness)
        {
            if (series == null || brush == null || RenderTarget == null)
                return;

            for (int barIdx = fromBar; barIdx <= toBar; barIdx++)
            {
                int nextBar = barIdx + 1;
                if (barIdx < 0 || nextBar >= Bars.Count)
                    continue;

                int barsAgo0 = CurrentBar - barIdx;
                int barsAgo1 = CurrentBar - nextBar;
                if (barsAgo0 < 0 || barsAgo1 < 0)
                    continue;

                double value0 = series[barsAgo0];
                double value1 = series[barsAgo1];
                if (double.IsNaN(value0) || double.IsNaN(value1))
                    continue;

                float x0 = cc.GetXByBarIndex(ChartBars, barIdx);
                float x1 = cc.GetXByBarIndex(ChartBars, nextBar);
                float y0 = cs.GetYByValue(value0);
                float y1 = cs.GetYByValue(value1);

                RenderTarget.DrawLine(new SharpDX.Vector2(x0, y0), new SharpDX.Vector2(x1, y1), brush, thickness);
            }
        }

        private void RenderPriceLevels(ChartControl cc, ChartScale cs, float chartWidth)
        {
            if (!ShowPriceLevels || LevelSpacing <= 0 || dxLevelBrush == null || RenderTarget == null)
                return;

            double baseLevel = Math.Floor(Close[0] / LevelSpacing) * LevelSpacing;

            foreach (int offset in _keyLevelOffsets)
            {
                double level = baseLevel + offset * LevelSpacing;
                float y = cs.GetYByValue(level);
                RenderTarget.DrawLine(new SharpDX.Vector2(0f, y), new SharpDX.Vector2(chartWidth, y), dxLevelBrush, 1f);
            }
        }

        private void RenderSignals(ChartControl cc, ChartScale cs, int fromBar, int toBar)
        {
            if (!ShowSignals || _signalDirection == null || dxSignalFormat == null || RenderTarget == null)
                return;

            float markerRadius = 4.5f;
            double markerOffset = SignalOffsetTicks * TickSize;

            for (int barIdx = fromBar; barIdx <= toBar; barIdx++)
            {
                if (barIdx < 0 || barIdx >= Bars.Count)
                    continue;

                int barsAgo = CurrentBar - barIdx;
                if (barsAgo < 0)
                    continue;

                int direction = _signalDirection[barsAgo];
                if (direction == 0)
                    continue;

                float x = cc.GetXByBarIndex(ChartBars, barIdx);
                double anchorPrice = direction > 0 ? Bars.GetLow(barIdx) - markerOffset : Bars.GetHigh(barIdx) + markerOffset;
                float y = cs.GetYByValue(anchorPrice);
                var brush = direction > 0 ? dxSignalBullBrush : dxSignalBearBrush;
                string text = direction > 0 ? "LONG ▲" : "SHORT ▼";
                float textY = direction > 0 ? y - (SignalFontSize + 12f) : y + 4f;

                RenderTarget.FillEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(x, y), markerRadius, markerRadius), brush);
                RenderTarget.DrawText(text, dxSignalFormat, new SharpDX.RectangleF(x - 36f, textY, 72f, SignalFontSize + 10f), brush);
            }
        }

        private void RenderBiasLabel(ChartControl cc, ChartScale cs, int fromBar, int toBar)
        {
            if (!ShowBiasLabel || CurrentBar < 200 || dxLabelFormat == null || dxSignalFormat == null || dxBiasLabelBrush == null || RenderTarget == null)
                return;

            int labelBar = Math.Min(CurrentBar, Bars.Count - 1);
            if (labelBar < fromBar || labelBar > toBar)
                return;

            int barsAgo = CurrentBar - labelBar;
            if (barsAgo < 0)
                return;

            bool isBull = Bars.GetClose(labelBar) > _ema200[barsAgo];
            float x = cc.GetXByBarIndex(ChartBars, labelBar) + 8f;
            float y = cs.GetYByValue(Bars.GetHigh(labelBar) + 10 * TickSize);

            RenderTarget.DrawText("200 EMA", dxLabelFormat, new SharpDX.RectangleF(x, y - 18f, 100f, 14f), dxBiasLabelBrush);
            RenderTarget.DrawText(isBull ? "ABOVE ▲" : "BELOW ▼",
                dxSignalFormat,
                new SharpDX.RectangleF(x, y - 2f, 110f, SignalFontSize + 10f),
                isBull ? dxSignalBullBrush : dxSignalBearBrush);
        }

        private void RenderLowVolumeCandles(ChartControl cc, ChartScale cs, int fromBar, int toBar)
        {
            if (!ShowLowVolumeCandles || _lowVolumeFlags == null || dxYellowCandleBrush == null || RenderTarget == null)
                return;

            for (int barIdx = fromBar; barIdx <= toBar; barIdx++)
            {
                if (barIdx < 0 || barIdx >= Bars.Count)
                    continue;

                int barsAgo = CurrentBar - barIdx;
                if (barsAgo < 0 || _lowVolumeFlags[barsAgo] == 0)
                    continue;

                float x = cc.GetXByBarIndex(ChartBars, barIdx);
                float barW = Math.Max(1f, cc.GetBarPaintWidth(ChartBars) - 2f);
                float yO = cs.GetYByValue(Bars.GetOpen(barIdx));
                float yC = cs.GetYByValue(Bars.GetClose(barIdx));
                float top = Math.Min(yO, yC);
                float height = Math.Max(1f, Math.Abs(yC - yO));

                var rect = new SharpDX.RectangleF(x - barW / 2f, top, barW, height);
                RenderTarget.FillRectangle(rect, dxYellowCandleBrush);
                RenderTarget.DrawRectangle(rect, dxYellowCandleBrush, 1f);
            }
        }

        private void RenderEmaLabels(ChartControl cc, ChartScale cs, int fromBar, int toBar)
        {
            if (!ShowEmaLabels || dxLabelFormat == null || RenderTarget == null)
                return;

            int labelBar = Math.Min(CurrentBar, Bars.Count - 1);
            if (labelBar < fromBar || labelBar > toBar)
                return;

            int barsAgo = CurrentBar - labelBar;
            if (barsAgo < 0)
                return;

            float x = cc.GetXByBarIndex(ChartBars, labelBar) + 8f;
            double ema13 = _ema13[barsAgo];
            double ema48 = _ema48[barsAgo];
            double ema200 = _ema200[barsAgo];

            if (ShowEma13)
            {
                RenderTarget.DrawText("EMA13  " + FormatPrice(ema13),
                    dxLabelFormat,
                    new SharpDX.RectangleF(x, cs.GetYByValue(ema13) - 8f, 120f, 16f),
                    ema13 > ema48 ? dxEma13BullBrush : dxEma13BearBrush);
            }

            if (ShowEma48)
            {
                RenderTarget.DrawText("EMA48  " + FormatPrice(ema48),
                    dxLabelFormat,
                    new SharpDX.RectangleF(x, cs.GetYByValue(ema48) - 8f, 120f, 16f),
                    dxEma48Brush);
            }

            if (ShowEma200)
            {
                RenderTarget.DrawText("EMA200 " + FormatPrice(ema200),
                    dxLabelFormat,
                    new SharpDX.RectangleF(x, cs.GetYByValue(ema200) - 8f, 120f, 16f),
                    dxEma200Brush);
            }
        }

        private string FormatPrice(double value)
        {
            return Instrument != null && Instrument.MasterInstrument != null
                ? Instrument.MasterInstrument.FormatPrice(value)
                : value.ToString("F2");
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════
        #region SharpDX Resource Management

        private void CreateDXResources()
        {
            var rt = RenderTarget;
            if (rt == null) { dxReady = false; return; }

            DisposeDXResources();

            try
            {
                dxWriteFactory = new SharpDX.DirectWrite.Factory();
                dxLabelFormat  = new SharpDX.DirectWrite.TextFormat(dxWriteFactory, "Arial", 9f);
                dxSignalFormat = new SharpDX.DirectWrite.TextFormat(dxWriteFactory, "Arial", SignalFontSize);

                dxEma13BullBrush = MakeBrush(rt, Ema13BullColor, 1f);
                dxEma13BearBrush = MakeBrush(rt, Ema13BearColor, 1f);
                dxEma48Brush = MakeBrush(rt, Ema48Color, 1f);
                dxEma200Brush = MakeBrush(rt, Ema200Color, 1f);
                dxLevelBrush = MakeBrush(rt, LevelColor, 1f);
                dxSignalBullBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(50f / 255f, 205f / 255f, 50f / 255f, 1f));
                dxSignalBearBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(1f, 69f / 255f, 0f, 1f));
                dxBiasLabelBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(0.95f, 0.95f, 0.95f, 1f));
                dxYellowCandleBrush = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(1f, 1f, 0f, 0.75f));

                dxReady = true;
            }
            catch (Exception ex)
            {
                Print("IQ1348LegendsEnhancedGPU: CreateDXResources failed [" + ex.GetType().Name + "]: " + ex.Message);
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
                try { c = scb.Color; }
                catch (InvalidOperationException) { c = System.Windows.Media.Colors.White; }
                return new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, (c.A / 255f) * opacity));
            }

            return new SharpDX.Direct2D1.SolidColorBrush(rt,
                new SharpDX.Color4(1f, 1f, 1f, opacity));
        }

        private void DisposeDXResources()
        {
            DisposeRef(ref dxWriteFactory);
            DisposeRef(ref dxLabelFormat);
            DisposeRef(ref dxSignalFormat);
            DisposeRef(ref dxEma13BullBrush);
            DisposeRef(ref dxEma13BearBrush);
            DisposeRef(ref dxEma48Brush);
            DisposeRef(ref dxEma200Brush);
            DisposeRef(ref dxLevelBrush);
            DisposeRef(ref dxSignalBullBrush);
            DisposeRef(ref dxSignalBearBrush);
            DisposeRef(ref dxBiasLabelBrush);
            DisposeRef(ref dxYellowCandleBrush);
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

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private IQ1348LegendsEnhancedGPU[] cacheIQ1348LegendsEnhancedGPU;
		public IQ1348LegendsEnhancedGPU IQ1348LegendsEnhancedGPU()
		{
			return IQ1348LegendsEnhancedGPU(Input);
		}

		public IQ1348LegendsEnhancedGPU IQ1348LegendsEnhancedGPU(ISeries<double> input)
		{
			if (cacheIQ1348LegendsEnhancedGPU != null)
				for (int idx = 0; idx < cacheIQ1348LegendsEnhancedGPU.Length; idx++)
					if (cacheIQ1348LegendsEnhancedGPU[idx] != null && cacheIQ1348LegendsEnhancedGPU[idx].EqualsInput(input))
						return cacheIQ1348LegendsEnhancedGPU[idx];
			return CacheIndicator<IQ1348LegendsEnhancedGPU>(new IQ1348LegendsEnhancedGPU(), input, ref cacheIQ1348LegendsEnhancedGPU);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.IQ1348LegendsEnhancedGPU IQ1348LegendsEnhancedGPU()
		{
			return indicator.IQ1348LegendsEnhancedGPU(Input);
		}

		public Indicators.IQ1348LegendsEnhancedGPU IQ1348LegendsEnhancedGPU(ISeries<double> input)
		{
			return indicator.IQ1348LegendsEnhancedGPU(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.IQ1348LegendsEnhancedGPU IQ1348LegendsEnhancedGPU()
		{
			return indicator.IQ1348LegendsEnhancedGPU(Input);
		}

		public Indicators.IQ1348LegendsEnhancedGPU IQ1348LegendsEnhancedGPU(ISeries<double> input)
		{
			return indicator.IQ1348LegendsEnhancedGPU(input);
		}
	}
}

#endregion
