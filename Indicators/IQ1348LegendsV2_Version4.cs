// IQ 1348 Legends V2 — NinjaTrader 8 indicator replicating the "13/48 Legends v8.0" system.
// Updated with dynamic EMA 13 bull/bear coloring on crossovers.
// Fixed version with alert implementation, error handling, and performance optimizations.

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
using NinjaTrader.Cbi;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class IQ1348LegendsV2 : Indicator
    {
        private int          _segmentStartBar = -1;
        private bool         _segmentIsBull   = true;
        private List<string> _ribbonTags      = new List<string>();

        private EMA _ema13;
        private EMA _ema48;
        private EMA _ema200;

        private NinjaTrader.Gui.Tools.SimpleFont _signalFont;
        private NinjaTrader.Gui.Tools.SimpleFont _biasFont;
        private NinjaTrader.Gui.Tools.SimpleFont _emaLabelFont;

        private static readonly int[] _keyLevelOffsets = { -2, -1, 0, 1, 2, 3, 4 };
        
        // Performance optimization: limit ribbon segments
        private const int MAX_RIBBON_SEGMENTS = 100;

        #region Parameters — 1. EMA Lines

        [NinjaScriptProperty]
        [Display(Name = "Show EMA 13", Order = 1, GroupName = "1. EMA Lines", Description = "Draw the EMA(13) fast line.")]
        public bool ShowEma13 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA 13 Bull Color", Order = 2, GroupName = "1. EMA Lines", Description = "Color of the 13 EMA when crossing above the 48 EMA.")]
        [XmlIgnore]
        public System.Windows.Media.Brush Ema13Color { get; set; }
        [Browsable(false)]
        public string Ema13ColorSerializable
        {
            get => Serialize.BrushToString(Ema13Color);
            set => Ema13Color = Serialize.StringToBrush(value);
        }

        [NinjaScriptProperty]
        [Display(Name = "EMA 13 Bear Color", Order = 3, GroupName = "1. EMA Lines", Description = "Color of the 13 EMA when crossing below the 48 EMA.")]
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
        [Display(Name = "Show EMA 48", Order = 5, GroupName = "1. EMA Lines", Description = "Draw the EMA(48) slow line.")]
        public bool ShowEma48 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA 48 Color", Order = 6, GroupName = "1. EMA Lines")]
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
        [Display(Name = "EMA 48 Thickness", Order = 7, GroupName = "1. EMA Lines")]
        public int Ema48Thickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show EMA 200", Order = 8, GroupName = "1. EMA Lines", Description = "Draw the EMA(200) macro filter line.")]
        public bool ShowEma200 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA 200 Color", Order = 9, GroupName = "1. EMA Lines")]
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
        [Display(Name = "EMA 200 Thickness", Order = 10, GroupName = "1. EMA Lines")]
        public int Ema200Thickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show EMA Labels", Order = 11, GroupName = "1. EMA Lines", Description = "Show EMA price labels.")]
        public bool ShowEmaLabels { get; set; }

        #endregion

        #region Parameters — 2. Ribbon

        [NinjaScriptProperty]
        [Display(Name = "Show Ribbon", Order = 1, GroupName = "2. Ribbon")]
        public bool ShowRibbon { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Ribbon Bull Color", Order = 2, GroupName = "2. Ribbon")]
        [XmlIgnore]
        public System.Windows.Media.Brush RibbonBullColor { get; set; }
        [Browsable(false)]
        public string RibbonBullColorSerializable
        {
            get => Serialize.BrushToString(RibbonBullColor);
            set => RibbonBullColor = Serialize.StringToBrush(value);
        }

        [NinjaScriptProperty]
        [Display(Name = "Ribbon Bear Color", Order = 3, GroupName = "2. Ribbon")]
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

        #region Parameters — 3. Volume Filter

        [NinjaScriptProperty]
        [Display(Name = "Show Low-Volume Candles", Order = 1, GroupName = "3. Volume Filter")]
        public bool ShowLowVolumeCandles { get; set; }

        [NinjaScriptProperty]
        [Range(2, 200)]
        [Display(Name = "Volume Period", Order = 2, GroupName = "3. Volume Filter")]
        public int VolumePeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 1.0)]
        [Display(Name = "Volume Threshold", Order = 3, GroupName = "3. Volume Filter")]
        public double VolumeThreshold { get; set; }

        #endregion

        #region Parameters — 4. Signals

        [NinjaScriptProperty]
        [Display(Name = "Show Signals", Order = 1, GroupName = "4. Signals")]
        public bool ShowSignals { get; set; }

        [NinjaScriptProperty]
        [Range(8, 32)]
        [Display(Name = "Signal Font Size", Order = 2, GroupName = "4. Signals")]
        public int SignalFontSize { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Signal Offset (ticks)", Order = 3, GroupName = "4. Signals")]
        public int SignalOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Play Alert Sound", Order = 4, GroupName = "4. Signals", Description = "Play sound alert on crossover signals.")]
        public bool PlayAlertSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert Sound File", Order = 5, GroupName = "4. Signals", Description = "Sound file name (must be in NinjaTrader sounds folder).")]
        public string AlertSoundFile { get; set; }

        #endregion

        #region Parameters — 5. Price Levels

        [NinjaScriptProperty]
        [Display(Name = "Show Price Levels", Order = 1, GroupName = "5. Price Levels")]
        public bool ShowPriceLevels { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 10000.0)]
        [Display(Name = "Level Spacing", Order = 2, GroupName = "5. Price Levels")]
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

        #region Parameters — 6. Info

        [NinjaScriptProperty]
        [Display(Name = "Show Bias Label", Order = 1, GroupName = "6. Info")]
        public bool ShowBiasLabel { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Bias Label Offset (ticks)", Order = 2, GroupName = "6. Info", Description = "Distance above price for bias label.")]
        public int BiasLabelOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Signal Text Offset Multiplier", Order = 3, GroupName = "6. Info", Description = "Multiplier for signal text offset (default 2.5).")]
        public double SignalTextOffsetMultiplier { get; set; }

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "IQ 1348 Legends V2 — Dynamic coloring and ribbon indicator with enhanced features.";
                Name                     = "IQ1348LegendsV2";
                Calculate                = Calculate.OnBarClose;
                IsOverlay                = true;
                IsAutoScale              = false;
                DisplayInDataBox         = false;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = false;
                MaximumBarsLookBack      = MaximumBarsLookBack.Infinite;

                ShowEma13      = true;
                var ema13Brush = new System.Windows.Media.SolidColorBrush(Colors.LimeGreen);
                ema13Brush.Freeze();
                Ema13Color     = ema13Brush;

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
                AlertSoundFile    = "Alert1.wav";

                ShowPriceLevels = true;
                LevelSpacing    = 50.0;
                var levelBrush  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(50, 255, 0, 0));
                levelBrush.Freeze();
                LevelColor = levelBrush;

                ShowBiasLabel = true;
                BiasLabelOffsetTicks = 10;
                SignalTextOffsetMultiplier = 2.5;

                AddPlot(new Stroke(Ema13Color,  Ema13Thickness),  PlotStyle.Line, "EMA13");
                AddPlot(new Stroke(Ema48Color,  Ema48Thickness),  PlotStyle.Line, "EMA48");
                AddPlot(new Stroke(Ema200Color, Ema200Thickness), PlotStyle.Line, "EMA200");
                AddPlot(new Stroke(Brushes.Transparent, 1),       PlotStyle.Line, "RibbonUpper");
                AddPlot(new Stroke(Brushes.Transparent, 1),       PlotStyle.Line, "RibbonLower");
            }
            else if (State == State.Configure)
            {
                // Ensure we have enough bars for EMA 200
                // No additional configuration needed here
            }
            else if (State == State.DataLoaded)
            {
                try
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

                    // Verify EMAs initialized properly
                    if (_ema13 == null || _ema48 == null || _ema200 == null)
                    {
                        Print("Error: EMA indicators failed to initialize");
                        return;
                    }

                    _signalFont   = new NinjaTrader.Gui.Tools.SimpleFont("Arial", SignalFontSize) { Bold = true };
                    _biasFont     = new NinjaTrader.Gui.Tools.SimpleFont("Arial", 10);
                    _emaLabelFont = new NinjaTrader.Gui.Tools.SimpleFont("Arial", 9) { Bold = true };

                    _segmentStartBar = -1;
                    _ribbonTags.Clear();
                }
                catch (Exception ex)
                {
                    Print(string.Format("Error in OnStateChange DataLoaded: {0}", ex.Message));
                }
            }
        }

        protected override void OnBarUpdate()
        {
            // Ensure we have enough bars for EMA 200
            if (CurrentBar < 201)
            {
                Values[3][0] = double.NaN;
                Values[4][0] = double.NaN;
                return;
            }

            try
            {
                // Null safety check
                if (_ema13 == null || _ema48 == null || _ema200 == null)
                    return;

                double ema13  = _ema13[0];
                double ema48  = _ema48[0];
                double ema200 = _ema200[0];

                Values[0][0] = ShowEma13  ? ema13  : double.NaN;
                Values[1][0] = ShowEma48  ? ema48  : double.NaN;
                Values[2][0] = ShowEma200 ? ema200 : double.NaN;

                // Dynamic EMA 13 coloring
                if (ShowEma13)
                {
                    PlotBrushes[0][0] = (ema13 > ema48) ? Ema13Color : Ema13BearColor;
                }

                Values[3][0] = ema13;
                Values[4][0] = ema48;

                // Ribbon drawing with performance optimization
                if (ShowRibbon)
                {
                    bool isBull   = ema13 > ema48;
                    bool prevBull = CurrentBar > 0 ? _ema13[1] > _ema48[1] : isBull;

                    if (_segmentStartBar < 0)
                    {
                        _segmentStartBar = CurrentBar;
                        _segmentIsBull   = isBull;
                    }

                    if (isBull != prevBull)
                    {
                        int endBarsAgo   = 1;
                        // NinjaTrader limitation: Draw.Region supports max 254 bars
                        int startBarsAgo = Math.Min(CurrentBar - _segmentStartBar, 254);

                        if (startBarsAgo >= endBarsAgo)
                        {
                            string sealTag = "RibbonV2_seg_" + _segmentStartBar;
                            _ribbonTags.Add(sealTag);
                            
                            // Remove oldest ribbon segment if we exceed the limit
                            if (_ribbonTags.Count > MAX_RIBBON_SEGMENTS)
                            {
                                string oldestTag = _ribbonTags[0];
                                RemoveDrawObject(oldestTag);
                                _ribbonTags.RemoveAt(0);
                            }
                            
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

                    // Draw current ribbon segment (limited to 254 bars)
                    int currentLen = Math.Min(CurrentBar - _segmentStartBar, 254);
                    Draw.Region(this, "RibbonV2Current",
                        0, currentLen,
                        Values[3], Values[4],
                        Brushes.Transparent,
                        isBull ? RibbonBullColor : RibbonBearColor,
                        RibbonOpacity);
                }
                else
                {
                    RemoveDrawObject("RibbonV2Current");
                    foreach (string tag in _ribbonTags)
                        RemoveDrawObject(tag);
                    _ribbonTags.Clear();
                    _segmentStartBar = -1;
                }

                // Volume filter
                if (ShowLowVolumeCandles && CurrentBar >= VolumePeriod)
                {
                    double avgVol = SMA(Volume, VolumePeriod)[0];
                    BarBrushes[0] = Volume[0] < avgVol * VolumeThreshold ? Brushes.Yellow : null;
                }
                else
                {
                    BarBrushes[0] = null;
                }

                // Signal detection and alerts
                if (ShowSignals)
                {
                    double arrowOffset = SignalOffsetTicks * TickSize;
                    double textOffset  = SignalOffsetTicks * SignalTextOffsetMultiplier * TickSize;

                    if (CrossAbove(_ema13, _ema48, 1))
                    {
                        Draw.ArrowUp(this, "BullSignal_" + CurrentBar, false, 0, Low[0] - arrowOffset, Brushes.LimeGreen);
                        Draw.Text(this, "BullLabel_" + CurrentBar, false, "LONG \u25b2", 0, Low[0] - textOffset, 0, Brushes.LimeGreen, _signalFont, System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
                        
                        // Play alert sound (only in real-time)
                        if (PlayAlertSound && State == State.Realtime)
                        {
                            try
                            {
                                PlaySound(AlertSoundFile);
                            }
                            catch (Exception ex)
                            {
                                Print(string.Format("Error playing alert sound '{0}': {1}", AlertSoundFile, ex.Message));
                            }
                        }
                    }
                    else if (CrossBelow(_ema13, _ema48, 1))
                    {
                        Draw.ArrowDown(this, "BearSignal_" + CurrentBar, false, 0, High[0] + arrowOffset, Brushes.OrangeRed);
                        Draw.Text(this, "BearLabel_" + CurrentBar, false, "SHORT \u25bc", 0, High[0] + textOffset, 0, Brushes.OrangeRed, _signalFont, System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
                        
                        // Play alert sound (only in real-time)
                        if (PlayAlertSound && State == State.Realtime)
                        {
                            try
                            {
                                PlaySound(AlertSoundFile);
                            }
                            catch (Exception ex)
                            {
                                Print(string.Format("Error playing alert sound '{0}': {1}", AlertSoundFile, ex.Message));
                            }
                        }
                    }
                }

                // Price levels
                if (ShowPriceLevels && LevelSpacing > 0)
                {
                    double baseLevel = Math.Floor(Close[0] / LevelSpacing) * LevelSpacing;
                    foreach (int offset in _keyLevelOffsets)
                    {
                        double level = baseLevel + offset * LevelSpacing;
                        Draw.HorizontalLine(this, "KL_" + offset.ToString(), level, LevelColor, DashStyleHelper.Solid, 1);
                    }
                }

                // Bias label
                if (ShowBiasLabel)
                {
                    double biasY = High[0] + BiasLabelOffsetTicks * TickSize;
                    if (Close[0] > ema200)
                        Draw.Text(this, "BiasLabel", false, "ABOVE 200 \u25b2", 0, biasY, 0, Brushes.LimeGreen, _biasFont, System.Windows.TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
                    else
                        Draw.Text(this, "BiasLabel", false, "BELOW 200 \u25bc", 0, biasY, 0, Brushes.OrangeRed, _biasFont, System.Windows.TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
                }

                // EMA labels
                if (ShowEmaLabels)
                {
                    if (ShowEma13)
                        Draw.Text(this, "EmaLabel13", false, "EMA13  " + ema13.ToString("F2"), 0, ema13, 0, (ema13 > ema48) ? Ema13Color : Ema13BearColor, _emaLabelFont, System.Windows.TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
                    if (ShowEma48)
                        Draw.Text(this, "EmaLabel48", false, "EMA48  " + ema48.ToString("F2"), 0, ema48, 0, Ema48Color, _emaLabelFont, System.Windows.TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
                    if (ShowEma200)
                        Draw.Text(this, "EmaLabel200", false, "EMA200 " + ema200.ToString("F2"), 0, ema200, 0, Ema200Color, _emaLabelFont, System.Windows.TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("Error in OnBarUpdate at bar {0}: {1}", CurrentBar, ex.Message));
            }
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.
// This namespace holds indicators in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.Indicators { }
#endregion

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private IQ1348LegendsV2[] cacheIQ1348LegendsV2;
		public IQ1348LegendsV2 IQ1348LegendsV2(bool showEma13, System.Windows.Media.Brush ema13Color, System.Windows.Media.Brush ema13BearColor, int ema13Thickness, bool showEma48, System.Windows.Media.Brush ema48Color, int ema48Thickness, bool showEma200, System.Windows.Media.Brush ema200Color, int ema200Thickness, bool showEmaLabels, bool showRibbon, System.Windows.Media.Brush ribbonBullColor, System.Windows.Media.Brush ribbonBearColor, int ribbonOpacity, bool showLowVolumeCandles, int volumePeriod, double volumeThreshold, bool showSignals, int signalFontSize, int signalOffsetTicks, bool playAlertSound, string alertSoundFile, bool showPriceLevels, double levelSpacing, System.Windows.Media.Brush levelColor, bool showBiasLabel, int biasLabelOffsetTicks, double signalTextOffsetMultiplier)
		{
			return IQ1348LegendsV2(Input, showEma13, ema13Color, ema13BearColor, ema13Thickness, showEma48, ema48Color, ema48Thickness, showEma200, ema200Color, ema200Thickness, showEmaLabels, showRibbon, ribbonBullColor, ribbonBearColor, ribbonOpacity, showLowVolumeCandles, volumePeriod, volumeThreshold, showSignals, signalFontSize, signalOffsetTicks, playAlertSound, alertSoundFile, showPriceLevels, levelSpacing, levelColor, showBiasLabel, biasLabelOffsetTicks, signalTextOffsetMultiplier);
		}

		public IQ1348LegendsV2 IQ1348LegendsV2(ISeries<double> input, bool showEma13, System.Windows.Media.Brush ema13Color, System.Windows.Media.Brush ema13BearColor, int ema13Thickness, bool showEma48, System.Windows.Media.Brush ema48Color, int ema48Thickness, bool showEma200, System.Windows.Media.Brush ema200Color, int ema200Thickness, bool showEmaLabels, bool showRibbon, System.Windows.Media.Brush ribbonBullColor, System.Windows.Media.Brush ribbonBearColor, int ribbonOpacity, bool showLowVolumeCandles, int volumePeriod, double volumeThreshold, bool showSignals, int signalFontSize, int signalOffsetTicks, bool playAlertSound, string alertSoundFile, bool showPriceLevels, double levelSpacing, System.Windows.Media.Brush levelColor, bool showBiasLabel, int biasLabelOffsetTicks, double signalTextOffsetMultiplier)
		{
			if (cacheIQ1348LegendsV2 != null)
				for (int idx = 0; idx < cacheIQ1348LegendsV2.Length; idx++)
					if (cacheIQ1348LegendsV2[idx] != null && cacheIQ1348LegendsV2[idx].ShowEma13 == showEma13 && cacheIQ1348LegendsV2[idx].Ema13Color == ema13Color && cacheIQ1348LegendsV2[idx].Ema13BearColor == ema13BearColor && cacheIQ1348LegendsV2[idx].Ema13Thickness == ema13Thickness && cacheIQ1348LegendsV2[idx].ShowEma48 == showEma48 && cacheIQ1348LegendsV2[idx].Ema48Color == ema48Color && cacheIQ1348LegendsV2[idx].Ema48Thickness == ema48Thickness && cacheIQ1348LegendsV2[idx].ShowEma200 == showEma200 && cacheIQ1348LegendsV2[idx].Ema200Color == ema200Color && cacheIQ1348LegendsV2[idx].Ema200Thickness == ema200Thickness && cacheIQ1348LegendsV2[idx].ShowEmaLabels == showEmaLabels && cacheIQ1348LegendsV2[idx].ShowRibbon == showRibbon && cacheIQ1348LegendsV2[idx].RibbonBullColor == ribbonBullColor && cacheIQ1348LegendsV2[idx].RibbonBearColor == ribbonBearColor && cacheIQ1348LegendsV2[idx].RibbonOpacity == ribbonOpacity && cacheIQ1348LegendsV2[idx].ShowLowVolumeCandles == showLowVolumeCandles && cacheIQ1348LegendsV2[idx].VolumePeriod == volumePeriod && cacheIQ1348LegendsV2[idx].VolumeThreshold == volumeThreshold && cacheIQ1348LegendsV2[idx].ShowSignals == showSignals && cacheIQ1348LegendsV2[idx].SignalFontSize == signalFontSize && cacheIQ1348LegendsV2[idx].SignalOffsetTicks == signalOffsetTicks && cacheIQ1348LegendsV2[idx].PlayAlertSound == playAlertSound && cacheIQ1348LegendsV2[idx].AlertSoundFile == alertSoundFile && cacheIQ1348LegendsV2[idx].ShowPriceLevels == showPriceLevels && cacheIQ1348LegendsV2[idx].LevelSpacing == levelSpacing && cacheIQ1348LegendsV2[idx].LevelColor == levelColor && cacheIQ1348LegendsV2[idx].ShowBiasLabel == showBiasLabel && cacheIQ1348LegendsV2[idx].BiasLabelOffsetTicks == biasLabelOffsetTicks && cacheIQ1348LegendsV2[idx].SignalTextOffsetMultiplier == signalTextOffsetMultiplier && cacheIQ1348LegendsV2[idx].EqualsInput(input))
						return cacheIQ1348LegendsV2[idx];
			return CacheIndicator<IQ1348LegendsV2>(new IQ1348LegendsV2(){ ShowEma13 = showEma13, Ema13Color = ema13Color, Ema13BearColor = ema13BearColor, Ema13Thickness = ema13Thickness, ShowEma48 = showEma48, Ema48Color = ema48Color, Ema48Thickness = ema48Thickness, ShowEma200 = showEma200, Ema200Color = ema200Color, Ema200Thickness = ema200Thickness, ShowEmaLabels = showEmaLabels, ShowRibbon = showRibbon, RibbonBullColor = ribbonBullColor, RibbonBearColor = ribbonBearColor, RibbonOpacity = ribbonOpacity, ShowLowVolumeCandles = showLowVolumeCandles, VolumePeriod = volumePeriod, VolumeThreshold = volumeThreshold, ShowSignals = showSignals, SignalFontSize = signalFontSize, SignalOffsetTicks = signalOffsetTicks, PlayAlertSound = playAlertSound, AlertSoundFile = alertSoundFile, ShowPriceLevels = showPriceLevels, LevelSpacing = levelSpacing, LevelColor = levelColor, ShowBiasLabel = showBiasLabel, BiasLabelOffsetTicks = biasLabelOffsetTicks, SignalTextOffsetMultiplier = signalTextOffsetMultiplier }, input, ref cacheIQ1348LegendsV2);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.IQ1348LegendsV2 IQ1348LegendsV2(bool showEma13, System.Windows.Media.Brush ema13Color, System.Windows.Media.Brush ema13BearColor, int ema13Thickness, bool showEma48, System.Windows.Media.Brush ema48Color, int ema48Thickness, bool showEma200, System.Windows.Media.Brush ema200Color, int ema200Thickness, bool showEmaLabels, bool showRibbon, System.Windows.Media.Brush ribbonBullColor, System.Windows.Media.Brush ribbonBearColor, int ribbonOpacity, bool showLowVolumeCandles, int volumePeriod, double volumeThreshold, bool showSignals, int signalFontSize, int signalOffsetTicks, bool playAlertSound, string alertSoundFile, bool showPriceLevels, double levelSpacing, System.Windows.Media.Brush levelColor, bool showBiasLabel, int biasLabelOffsetTicks, double signalTextOffsetMultiplier)
		{
			return indicator.IQ1348LegendsV2(Input, showEma13, ema13Color, ema13BearColor, ema13Thickness, showEma48, ema48Color, ema48Thickness, showEma200, ema200Color, ema200Thickness, showEmaLabels, showRibbon, ribbonBullColor, ribbonBearColor, ribbonOpacity, showLowVolumeCandles, volumePeriod, volumeThreshold, showSignals, signalFontSize, signalOffsetTicks, playAlertSound, alertSoundFile, showPriceLevels, levelSpacing, levelColor, showBiasLabel, biasLabelOffsetTicks, signalTextOffsetMultiplier);
		}

		public Indicators.IQ1348LegendsV2 IQ1348LegendsV2(ISeries<double> input , bool showEma13, System.Windows.Media.Brush ema13Color, System.Windows.Media.Brush ema13BearColor, int ema13Thickness, bool showEma48, System.Windows.Media.Brush ema48Color, int ema48Thickness, bool showEma200, System.Windows.Media.Brush ema200Color, int ema200Thickness, bool showEmaLabels, bool showRibbon, System.Windows.Media.Brush ribbonBullColor, System.Windows.Media.Brush ribbonBearColor, int ribbonOpacity, bool showLowVolumeCandles, int volumePeriod, double volumeThreshold, bool showSignals, int signalFontSize, int signalOffsetTicks, bool playAlertSound, string alertSoundFile, bool showPriceLevels, double levelSpacing, System.Windows.Media.Brush levelColor, bool showBiasLabel, int biasLabelOffsetTicks, double signalTextOffsetMultiplier)
		{
			return indicator.IQ1348LegendsV2(input, showEma13, ema13Color, ema13BearColor, ema13Thickness, showEma48, ema48Color, ema48Thickness, showEma200, ema200Color, ema200Thickness, showEmaLabels, showRibbon, ribbonBullColor, ribbonBearColor, ribbonOpacity, showLowVolumeCandles, volumePeriod, volumeThreshold, showSignals, signalFontSize, signalOffsetTicks, playAlertSound, alertSoundFile, showPriceLevels, levelSpacing, levelColor, showBiasLabel, biasLabelOffsetTicks, signalTextOffsetMultiplier);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.IQ1348LegendsV2 IQ1348LegendsV2(bool showEma13, System.Windows.Media.Brush ema13Color, System.Windows.Media.Brush ema13BearColor, int ema13Thickness, bool showEma48, System.Windows.Media.Brush ema48Color, int ema48Thickness, bool showEma200, System.Windows.Media.Brush ema200Color, int ema200Thickness, bool showEmaLabels, bool showRibbon, System.Windows.Media.Brush ribbonBullColor, System.Windows.Media.Brush ribbonBearColor, int ribbonOpacity, bool showLowVolumeCandles, int volumePeriod, double volumeThreshold, bool showSignals, int signalFontSize, int signalOffsetTicks, bool playAlertSound, string alertSoundFile, bool showPriceLevels, double levelSpacing, System.Windows.Media.Brush levelColor, bool showBiasLabel, int biasLabelOffsetTicks, double signalTextOffsetMultiplier)
		{
			return indicator.IQ1348LegendsV2(Input, showEma13, ema13Color, ema13BearColor, ema13Thickness, showEma48, ema48Color, ema48Thickness, showEma200, ema200Color, ema200Thickness, showEmaLabels, showRibbon, ribbonBullColor, ribbonBearColor, ribbonOpacity, showLowVolumeCandles, volumePeriod, volumeThreshold, showSignals, signalFontSize, signalOffsetTicks, playAlertSound, alertSoundFile, showPriceLevels, levelSpacing, levelColor, showBiasLabel, biasLabelOffsetTicks, signalTextOffsetMultiplier);
		}

		public Indicators.IQ1348LegendsV2 IQ1348LegendsV2(ISeries<double> input , bool showEma13, System.Windows.Media.Brush ema13Color, System.Windows.Media.Brush ema13BearColor, int ema13Thickness, bool showEma48, System.Windows.Media.Brush ema48Color, int ema48Thickness, bool showEma200, System.Windows.Media.Brush ema200Color, int ema200Thickness, bool showEmaLabels, bool showRibbon, System.Windows.Media.Brush ribbonBullColor, System.Windows.Media.Brush ribbonBearColor, int ribbonOpacity, bool showLowVolumeCandles, int volumePeriod, double volumeThreshold, bool showSignals, int signalFontSize, int signalOffsetTicks, bool playAlertSound, string alertSoundFile, bool showPriceLevels, double levelSpacing, System.Windows.Media.Brush levelColor, bool showBiasLabel, int biasLabelOffsetTicks, double signalTextOffsetMultiplier)
		{
			return indicator.IQ1348LegendsV2(input, showEma13, ema13Color, ema13BearColor, ema13Thickness, showEma48, ema48Color, ema48Thickness, showEma200, ema200Color, ema200Thickness, showEmaLabels, showRibbon, ribbonBullColor, ribbonBearColor, ribbonOpacity, showLowVolumeCandles, volumePeriod, volumeThreshold, showSignals, signalFontSize, signalOffsetTicks, playAlertSound, alertSoundFile, showPriceLevels, levelSpacing, levelColor, showBiasLabel, biasLabelOffsetTicks, signalTextOffsetMultiplier);
		}
	}
}

#endregion
