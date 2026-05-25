// IQ 1348 Legends — NinjaTrader 8 indicator replicating the "13/48 Legends v8.0" system.
// Three EMA lines (13, 48, 200), dynamic ribbon cloud, yellow low-volume candle filter,
// crossover signal labels with alerts, horizontal key price level grid, 200 EMA bias label,
// EMA price labels on the chart.
//
// RIBBON FIX: Each bull/bear segment gets its own uniquely-tagged Draw.Region so the cloud
// colour is historically correct — green during long bias, red/orange during short bias.
// Previously a single shared tag was overwritten on every bar, making the whole lookback
// render with the *current* bias colour instead of the correct per-segment colour.
//
// Uses AddPlot() + Draw.Region() — no SharpDX GPU code required.

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
    /// IQ 1348 Legends — Full 13/48/200 EMA strategy indicator.
    ///
    /// Plot index map:
    ///   Values[0] = EMA 13  (fast, green)
    ///   Values[1] = EMA 48  (slow, teal)
    ///   Values[2] = EMA 200 (macro, pink-red)
    ///   Values[3] = RibbonUpper (transparent, anchors Draw.Region upper edge = EMA13)
    ///   Values[4] = RibbonLower (transparent, anchors Draw.Region lower edge = EMA48)
    /// </summary>
    public class IQ1348Legends : Indicator
    {
        // ── Ribbon segment tracking ──────────────────────────────────────────
        // Each time EMA13/48 bias flips we "seal" the outgoing segment with a
        // unique tag so its colour is frozen permanently.  The live (unsettled)
        // segment uses the tag "RibbonCurrent" and is redrawn every bar.
        private int          _segmentStartBar = -1;
        private bool         _segmentIsBull   = true;
        private List<string> _ribbonTags      = new List<string>();

        // Cached EMA indicator instances (created in State.DataLoaded).
        private EMA _ema13;
        private EMA _ema48;
        private EMA _ema200;

        // Fonts — cached so we don't allocate on every bar.
        private NinjaTrader.Gui.Tools.SimpleFont _signalFont;
        private NinjaTrader.Gui.Tools.SimpleFont _biasFont;
        private NinjaTrader.Gui.Tools.SimpleFont _emaLabelFont;

        // Static array — avoids a heap allocation on every bar close.
        private static readonly int[] _keyLevelOffsets = { -2, -1, 0, 1, 2, 3, 4 };

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
                Description              = "IQ 1348 Legends — 13/48/200 EMA strategy indicator with per-segment colour ribbon, signals, volume filter, and EMA labels.";
                Name                     = "IQ1348Legends";
                Calculate                = Calculate.OnBarClose;
                IsOverlay                = true;
                IsAutoScale              = false;
                DisplayInDataBox         = false;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = false;
                MaximumBarsLookBack      = MaximumBarsLookBack.Infinite;

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

                // Bright OrangeRed — clearly visible at 40 % opacity (old dark-maroon was not).
                var bearBrush = new System.Windows.Media.SolidColorBrush(Colors.OrangeRed);
                bearBrush.Freeze();
                RibbonBearColor = bearBrush;

                RibbonOpacity = 40;

                // ── 3. Volume Filter ──────────────────────────────────────────
                ShowLowVolumeCandles = true;
                VolumePeriod         = 20;
                VolumeThreshold      = 0.7;

                // ── 4. Signals ────────────────────────────────────────────────
                ShowSignals       = true;
                SignalFontSize    = 14;
                SignalOffsetTicks = 6;
                PlayAlertSound    = false;

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
                AddPlot(new Stroke(Ema13Color,  Ema13Thickness),  PlotStyle.Line, "EMA13");
                AddPlot(new Stroke(Ema48Color,  Ema48Thickness),  PlotStyle.Line, "EMA48");
                AddPlot(new Stroke(Ema200Color, Ema200Thickness), PlotStyle.Line, "EMA200");
                AddPlot(new Stroke(Brushes.Transparent, 1),       PlotStyle.Line, "RibbonUpper");
                AddPlot(new Stroke(Brushes.Transparent, 1),       PlotStyle.Line, "RibbonLower");
            }
            else if (State == State.DataLoaded)
            {
                Plots[0].Brush = Ema13Color;
                Plots[0].Width = Ema13Thickness;
                Plots[1].Brush = Ema48Color;
                Plots[1].Width = Ema48Thickness;
                Plots[2].Brush = Ema200Color;
                Plots[2].Width = Ema200Thickness;

                _ema13  = EMA(Close, 13);
                _ema48  = EMA(Close, 48);
                _ema200 = EMA(Close, 200);

                _signalFont   = new NinjaTrader.Gui.Tools.SimpleFont("Arial", SignalFontSize) { Bold = true };
                _biasFont     = new NinjaTrader.Gui.Tools.SimpleFont("Arial", 10);
                _emaLabelFont = new NinjaTrader.Gui.Tools.SimpleFont("Arial", 9) { Bold = true };

                // Reset segment tracker when data reloads.
                _segmentStartBar = -1;
                _ribbonTags.Clear();
            }
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════
        #region OnBarUpdate

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 201)
            {
                Values[3][0] = double.NaN;
                Values[4][0] = double.NaN;
                return;
            }

            double ema13  = _ema13[0];
            double ema48  = _ema48[0];
            double ema200 = _ema200[0];

            // ── 1. EMA Lines ─────────────────────────────────────────────────
            Values[0][0] = ShowEma13  ? ema13  : double.NaN;
            Values[1][0] = ShowEma48  ? ema48  : double.NaN;
            Values[2][0] = ShowEma200 ? ema200 : double.NaN;

            // ── 2. Ribbon anchor plots ────────────────────────────────────────
            Values[3][0] = ema13;
            Values[4][0] = ema48;

            if (ShowRibbon)
            {
                bool isBull   = ema13 > ema48;
                bool prevBull = _ema13[1] > _ema48[1];

                // Initialise segment tracker on the very first valid bar.
                if (_segmentStartBar < 0)
                {
                    _segmentStartBar = CurrentBar;
                    _segmentIsBull   = isBull;
                }

                // ── Bias flip: seal the outgoing segment with a frozen unique tag ──
                if (isBull != prevBull)
                {
                    // The outgoing segment ran from _segmentStartBar (inclusive) to
                    // CurrentBar-1 inclusive, i.e. 1 bar ago through N bars ago.
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

                // ── Always refresh the live (current) segment ─────────────────
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
                // ShowRibbon toggled off — remove everything.
                RemoveDrawObject("RibbonCurrent");
                foreach (string tag in _ribbonTags)
                    RemoveDrawObject(tag);
                _ribbonTags.Clear();
                _segmentStartBar = -1;
            }

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
                double arrowOffset = SignalOffsetTicks * TickSize;
                double textOffset  = SignalOffsetTicks * 2.5 * TickSize;

                if (CrossAbove(_ema13, _ema48, 1))
                {
                    Draw.ArrowUp(this, "BullSignal_" + CurrentBar, false,
                        0, Low[0] - arrowOffset, Brushes.LimeGreen);
                    Draw.Text(this, "BullLabel_" + CurrentBar, false,
                        "LONG \u25b2", 0, Low[0] - textOffset, 0,
                        Brushes.LimeGreen, _signalFont,
                        System.Windows.TextAlignment.Center,
                        Brushes.Transparent, Brushes.Transparent, 0);

                    if (PlayAlertSound)
                        Alert("CrossAlertLong", Priority.Medium, "EMA Cross - Long signal",
                            NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav",
                            10, Brushes.Yellow, Brushes.Black);
                }
                else if (CrossBelow(_ema13, _ema48, 1))
                {
                    Draw.ArrowDown(this, "BearSignal_" + CurrentBar, false,
                        0, High[0] + arrowOffset, Brushes.OrangeRed);
                    Draw.Text(this, "BearLabel_" + CurrentBar, false,
                        "SHORT \u25bc", 0, High[0] + textOffset, 0,
                        Brushes.OrangeRed, _signalFont,
                        System.Windows.TextAlignment.Center,
                        Brushes.Transparent, Brushes.Transparent, 0);

                    if (PlayAlertSound)
                        Alert("CrossAlertShort", Priority.Medium, "EMA Cross - Short signal",
                            NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav",
                            10, Brushes.Yellow, Brushes.Black);
                }
            }

            // ── 5. Horizontal Key Price Level Grid ───────────────────────────
            if (ShowPriceLevels && LevelSpacing > 0)
            {
                double baseLevel = Math.Floor(Close[0] / LevelSpacing) * LevelSpacing;
                foreach (int offset in _keyLevelOffsets)
                {
                    double level = baseLevel + offset * LevelSpacing;
                    Draw.HorizontalLine(this, "KL_" + offset.ToString(), level,
                        LevelColor, DashStyleHelper.Solid, 1);
                }
            }
            else
            {
                foreach (int offset in _keyLevelOffsets)
                    RemoveDrawObject("KL_" + offset.ToString());
            }

            // ── 6. 200 EMA Macro Bias Label ───────────────────────────────────
            if (ShowBiasLabel)
            {
                double biasY = High[0] + 10 * TickSize;
                if (Close[0] > ema200)
                    Draw.Text(this, "BiasLabel", false,
                        "ABOVE 200 \u25b2", 0, biasY, 0,
                        Brushes.LimeGreen, _biasFont,
                        System.Windows.TextAlignment.Left,
                        Brushes.Transparent, Brushes.Transparent, 0);
                else
                    Draw.Text(this, "BiasLabel", false,
                        "BELOW 200 \u25bc", 0, biasY, 0,
                        Brushes.OrangeRed, _biasFont,
                        System.Windows.TextAlignment.Left,
                        Brushes.Transparent, Brushes.Transparent, 0);
            }
            else
            {
                RemoveDrawObject("BiasLabel");
            }

            // ── 7. EMA Price Labels (current bar only) ────────────────────────
            if (ShowEmaLabels)
            {
                if (ShowEma13)
                    Draw.Text(this, "EmaLabel13", false,
                        "EMA13  " + ema13.ToString("F2"), 0, ema13, 0,
                        Brushes.LimeGreen, _emaLabelFont,
                        System.Windows.TextAlignment.Left,
                        Brushes.Transparent, Brushes.Transparent, 0);
                else
                    RemoveDrawObject("EmaLabel13");

                if (ShowEma48)
                    Draw.Text(this, "EmaLabel48", false,
                        "EMA48  " + ema48.ToString("F2"), 0, ema48, 0,
                        new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(31, 188, 211)),
                        _emaLabelFont,
                        System.Windows.TextAlignment.Left,
                        Brushes.Transparent, Brushes.Transparent, 0);
                else
                    RemoveDrawObject("EmaLabel48");

                if (ShowEma200)
                    Draw.Text(this, "EmaLabel200", false,
                        "EMA200 " + ema200.ToString("F2"), 0, ema200, 0,
                        new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(255, 80, 80)),
                        _emaLabelFont,
                        System.Windows.TextAlignment.Left,
                        Brushes.Transparent, Brushes.Transparent, 0);
                else
                    RemoveDrawObject("EmaLabel200");
            }
            else
            {
                RemoveDrawObject("EmaLabel13");
                RemoveDrawObject("EmaLabel48");
                RemoveDrawObject("EmaLabel200");
            }
        }

        #endregion
    }
}
