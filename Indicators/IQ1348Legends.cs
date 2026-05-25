// IQ 1348 Legends — NinjaTrader 8 indicator replicating the "13/48 Legends v8.0" system.
// Three EMA lines (13, 48, 200), dynamic ribbon cloud, yellow low-volume candle filter,
// crossover signal labels with alerts, horizontal key price level grid, 200 EMA bias label.
// Uses AddPlot() + Draw.Region() — no SharpDX GPU code required.
// Template source: IQEma50Cloud.cs (ribbon/brush pattern), IQCandlesGPU.cs (BarBrushes).
// Do NOT add top-level SharpDX using directives — qualify inline if ever needed.

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
        // Tracks whether the ribbon was last drawn as bullish (true), bearish (false),
        // or unset (null) so RemoveDrawObject is only called on a state transition.
        private bool? _lastRibbonBull;

        // ════════════════════════════════════════════════════════════════════════
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

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 2. Ribbon

        [NinjaScriptProperty]
        [Display(Name = "Show Ribbon", Order = 1, GroupName = "2. Ribbon",
            Description = "Draw filled cloud ribbon between EMA 13 and EMA 48.")]
        public bool ShowRibbon { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Ribbon Bull Color", Order = 2, GroupName = "2. Ribbon",
            Description = "Fill color when EMA 13 > EMA 48 (bullish).")]
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
            Description = "Fill color when EMA 13 < EMA 48 (bearish).")]
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
        [Display(Name = "Ribbon Opacity %", Order = 4, GroupName = "2. Ribbon")]
        public int RibbonOpacity { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
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
            Description = "Candles with volume below (AvgVolume × threshold) are painted yellow.")]
        public double VolumeThreshold { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 4. Signals

        [NinjaScriptProperty]
        [Display(Name = "Show Signals", Order = 1, GroupName = "4. Signals",
            Description = "Draw arrow + text label on EMA 13/48 crossovers.")]
        public bool ShowSignals { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Play Alert Sound", Order = 2, GroupName = "4. Signals",
            Description = "Fire an alert sound on each EMA 13/48 crossover.")]
        public bool PlayAlertSound { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
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
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 6. Info

        [NinjaScriptProperty]
        [Display(Name = "Show Bias Label", Order = 1, GroupName = "6. Info",
            Description = "Show ABOVE/BELOW 200 EMA macro bias label.")]
        public bool ShowBiasLabel { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region State management — OnStateChange

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "IQ 1348 Legends — 13/48/200 EMA strategy indicator with ribbon, signals, and volume filter.";
                Name                     = "IQ1348Legends";
                Calculate                = Calculate.OnBarClose;
                IsOverlay                = true;
                IsAutoScale              = false;
                DisplayInDataBox         = false;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = false;
                IsCustomBarColor         = true;
                MaximumBarsLookBack      = MaximumBarsLookBack.Infinite;

                // ── 1. EMA Lines ────────────────────────────────────────────
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

                // ── 2. Ribbon ───────────────────────────────────────────────
                ShowRibbon      = true;
                var bullBrush   = new System.Windows.Media.SolidColorBrush(Colors.LimeGreen);
                bullBrush.Freeze();
                RibbonBullColor = bullBrush;
                var bearBrush   = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(139, 0, 0));
                bearBrush.Freeze();
                RibbonBearColor = bearBrush;
                RibbonOpacity   = 30;

                // ── 3. Volume Filter ────────────────────────────────────────
                ShowLowVolumeCandles = true;
                VolumePeriod         = 20;
                VolumeThreshold      = 0.7;

                // ── 4. Signals ──────────────────────────────────────────────
                ShowSignals    = true;
                PlayAlertSound = false;

                // ── 5. Price Levels ─────────────────────────────────────────
                ShowPriceLevels = true;
                LevelSpacing    = 50.0;
                var levelBrush  = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(50, 255, 0, 0));
                levelBrush.Freeze();
                LevelColor = levelBrush;

                // ── 6. Info ─────────────────────────────────────────────────
                ShowBiasLabel = true;

                // ── Plots ────────────────────────────────────────────────────
                // [0] EMA 13
                AddPlot(new Stroke(Ema13Color, Ema13Thickness), PlotStyle.Line, "EMA13");
                // [1] EMA 48
                AddPlot(new Stroke(Ema48Color, Ema48Thickness), PlotStyle.Line, "EMA48");
                // [2] EMA 200
                AddPlot(new Stroke(Ema200Color, Ema200Thickness), PlotStyle.Line, "EMA200");
                // [3] RibbonUpper — transparent anchor for Draw.Region
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "RibbonUpper");
                // [4] RibbonLower — transparent anchor for Draw.Region
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "RibbonLower");
            }
            else if (State == State.DataLoaded)
            {
                // Sync EMA plot strokes to user-configured colors and thicknesses
                Plots[0].Brush = Ema13Color;
                Plots[0].Width = Ema13Thickness;
                Plots[1].Brush = Ema48Color;
                Plots[1].Width = Ema48Thickness;
                Plots[2].Brush = Ema200Color;
                Plots[2].Width = Ema200Thickness;
            }
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region OnBarUpdate

        protected override void OnBarUpdate()
        {
            // Require at least 200 bars to compute all EMAs
            if (CurrentBar < 200)
            {
                Values[3][0] = double.NaN;
                Values[4][0] = double.NaN;
                return;
            }

            double ema13  = EMA(Close, 13)[0];
            double ema48  = EMA(Close, 48)[0];
            double ema200 = EMA(Close, 200)[0];

            // ── 1. EMA Lines ────────────────────────────────────────────────
            Values[0][0] = ShowEma13  ? ema13  : double.NaN;
            Values[1][0] = ShowEma48  ? ema48  : double.NaN;
            Values[2][0] = ShowEma200 ? ema200 : double.NaN;

            // ── 2. Ribbon anchor plots (always set for Draw.Region data) ────
            Values[3][0] = ema13;
            Values[4][0] = ema48;

            if (ShowRibbon)
            {
                // Guard: barsBack must stay within the MaximumBarsLookBack window.
                // NT8's Draw.Region startBarsAgo/endBarsAgo use a 0-based offset where
                // the maximum valid index is 254 (indices 0–254 = 255 slots max).
                int barsBack = Math.Min(CurrentBar - 200, 254);
                bool isBull  = ema13 > ema48;

                if (isBull)
                {
                    Draw.Region(this, "RibbonBull", 0, barsBack,
                        Values[3], Values[4],
                        Brushes.Transparent, RibbonBullColor, RibbonOpacity);
                    if (_lastRibbonBull != true)
                        RemoveDrawObject("RibbonBear");
                }
                else
                {
                    Draw.Region(this, "RibbonBear", 0, barsBack,
                        Values[3], Values[4],
                        Brushes.Transparent, RibbonBearColor, RibbonOpacity);
                    if (_lastRibbonBull != false)
                        RemoveDrawObject("RibbonBull");
                }
                _lastRibbonBull = isBull;
            }
            else
            {
                if (_lastRibbonBull.HasValue)
                {
                    RemoveDrawObject("RibbonBull");
                    RemoveDrawObject("RibbonBear");
                    _lastRibbonBull = null;
                }
            }

            // ── 3. Yellow Low-Volume Candle Filter ──────────────────────────
            if (ShowLowVolumeCandles && CurrentBar >= VolumePeriod)
            {
                double avgVol = SMA(Volume, VolumePeriod)[0];
                if (Volume[0] < avgVol * VolumeThreshold)
                    BarBrushes[0] = Brushes.Yellow;
                else
                    BarBrushes[0] = null;
            }
            else
            {
                BarBrushes[0] = null;
            }

            // ── 4. EMA Crossover Signal Labels ──────────────────────────────
            if (ShowSignals)
            {
                // Bullish cross: EMA13 crosses above EMA48
                if (CrossAbove(EMA(Close, 13), EMA(Close, 48), 1))
                {
                    Draw.ArrowUp(this, "BullSignal_" + CurrentBar, false,
                        0, Low[0] - 2 * TickSize, Brushes.LimeGreen);
                    Draw.Text(this, "BullLabel_" + CurrentBar, false,
                        "Long \u25b2", 0, Low[0] - 4 * TickSize, 0,
                        Brushes.LimeGreen,
                        new NinjaTrader.Gui.Tools.SimpleFont("Arial", 9),
                        System.Windows.TextAlignment.Center,
                        Brushes.Transparent, Brushes.Transparent, 0);

                    if (PlayAlertSound)
                        Alert("CrossAlert_" + CurrentBar, Priority.Medium, "EMA Cross — Long signal",
                            NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav",
                            10, Brushes.Yellow, Brushes.Black);
                }
                // Bearish cross: EMA13 crosses below EMA48
                else if (CrossBelow(EMA(Close, 13), EMA(Close, 48), 1))
                {
                    Draw.ArrowDown(this, "BearSignal_" + CurrentBar, false,
                        0, High[0] + 2 * TickSize, Brushes.Red);
                    Draw.Text(this, "BearLabel_" + CurrentBar, false,
                        "Short \u25bc", 0, High[0] + 4 * TickSize, 0,
                        Brushes.Red,
                        new NinjaTrader.Gui.Tools.SimpleFont("Arial", 9),
                        System.Windows.TextAlignment.Center,
                        Brushes.Transparent, Brushes.Transparent, 0);

                    if (PlayAlertSound)
                        Alert("CrossAlert_" + CurrentBar, Priority.Medium, "EMA Cross — Short signal",
                            NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav",
                            10, Brushes.Yellow, Brushes.Black);
                }
            }

            // ── 5. Horizontal Key Price Level Grid ──────────────────────────
            int[] keyLevelOffsets = { -2, -1, 0, 1, 2, 3, 4 };
            if (ShowPriceLevels && LevelSpacing > 0)
            {
                double baseLevel = Math.Floor(Close[0] / LevelSpacing) * LevelSpacing;
                foreach (int offset in keyLevelOffsets)
                {
                    double level = baseLevel + offset * LevelSpacing;
                    string tag   = $"KL_{offset}";
                    Draw.HorizontalLine(this, tag, level, LevelColor,
                        DashStyleHelper.Solid, 1);
                }
            }
            else
            {
                foreach (int offset in keyLevelOffsets)
                    RemoveDrawObject($"KL_{offset}");
            }

            // ── 6. 200 EMA Macro Bias Label ─────────────────────────────────
            if (ShowBiasLabel)
            {
                double biasY = High[0] + 10 * TickSize;
                var    biasFont = new NinjaTrader.Gui.Tools.SimpleFont("Arial", 9);
                if (Close[0] > ema200)
                    Draw.Text(this, "BiasLabel", false,
                        "ABOVE 200 \u25b2", 0, biasY, 0,
                        Brushes.LimeGreen,
                        biasFont,
                        System.Windows.TextAlignment.Left,
                        Brushes.Transparent, Brushes.Transparent, 0);
                else
                    Draw.Text(this, "BiasLabel", false,
                        "BELOW 200 \u25bc", 0, biasY, 0,
                        Brushes.Red,
                        biasFont,
                        System.Windows.TextAlignment.Left,
                        Brushes.Transparent, Brushes.Transparent, 0);
            }
            else
            {
                RemoveDrawObject("BiasLabel");
            }
        }

        #endregion
    }
}
