// IQ Scalp Ribbon — Fast-reaction ribbon for scalp entries and momentum confirmation
// Designed to complement the IQ1348LegendsV2 indicator with a tighter, inner ribbon

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
    public class IQScalpRibbon : Indicator
    {
        private int          _segmentStartBar = -1;
        private bool         _segmentIsBull   = true;
        private List<string> _ribbonTags      = new List<string>();

        private ISeries<double> _fastMA;
        private ISeries<double> _slowMA;

        private const int MAX_RIBBON_SEGMENTS = 100;

        #region Parameters — 1. Ribbon Settings

        [NinjaScriptProperty]
        [Display(Name = "Show Ribbon", Order = 1, GroupName = "1. Ribbon Settings")]
        public bool ShowRibbon { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Fast MA Period", Order = 2, GroupName = "1. Ribbon Settings", Description = "Default: 5")]
        public int FastPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Slow MA Period", Order = 3, GroupName = "1. Ribbon Settings", Description = "Default: 13")]
        public int SlowPeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use EMA (false = SMA)", Order = 4, GroupName = "1. Ribbon Settings", Description = "True = EMA, False = SMA")]
        public bool UseEMA { get; set; }

        #endregion

        #region Parameters — 2. Colors

        [NinjaScriptProperty]
        [Display(Name = "Ribbon Bull Color", Order = 1, GroupName = "2. Colors", Description = "Color when Fast > Slow")]
        [XmlIgnore]
        public System.Windows.Media.Brush RibbonBullColor { get; set; }
        [Browsable(false)]
        public string RibbonBullColorSerializable
        {
            get => Serialize.BrushToString(RibbonBullColor);
            set => RibbonBullColor = Serialize.StringToBrush(value);
        }

        [NinjaScriptProperty]
        [Display(Name = "Ribbon Bear Color", Order = 2, GroupName = "2. Colors", Description = "Color when Fast < Slow")]
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
        [Display(Name = "Ribbon Opacity %", Order = 3, GroupName = "2. Colors")]
        public int RibbonOpacity { get; set; }

        #endregion

        #region Parameters — 3. MA Lines

        [NinjaScriptProperty]
        [Display(Name = "Show MA Lines", Order = 1, GroupName = "3. MA Lines", Description = "Display the actual MA lines")]
        public bool ShowMALines { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Fast MA Bull Color", Order = 2, GroupName = "3. MA Lines")]
        [XmlIgnore]
        public System.Windows.Media.Brush FastMABullColor { get; set; }
        [Browsable(false)]
        public string FastMABullColorSerializable
        {
            get => Serialize.BrushToString(FastMABullColor);
            set => FastMABullColor = Serialize.StringToBrush(value);
        }

        [NinjaScriptProperty]
        [Display(Name = "Fast MA Bear Color", Order = 3, GroupName = "3. MA Lines")]
        [XmlIgnore]
        public System.Windows.Media.Brush FastMABearColor { get; set; }
        [Browsable(false)]
        public string FastMABearColorSerializable
        {
            get => Serialize.BrushToString(FastMABearColor);
            set => FastMABearColor = Serialize.StringToBrush(value);
        }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Fast MA Thickness", Order = 4, GroupName = "3. MA Lines")]
        public int FastMAThickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Slow MA Bull Color", Order = 5, GroupName = "3. MA Lines")]
        [XmlIgnore]
        public System.Windows.Media.Brush SlowMABullColor { get; set; }
        [Browsable(false)]
        public string SlowMABullColorSerializable
        {
            get => Serialize.BrushToString(SlowMABullColor);
            set => SlowMABullColor = Serialize.StringToBrush(value);
        }

        [NinjaScriptProperty]
        [Display(Name = "Slow MA Bear Color", Order = 6, GroupName = "3. MA Lines")]
        [XmlIgnore]
        public System.Windows.Media.Brush SlowMABearColor { get; set; }
        [Browsable(false)]
        public string SlowMABearColorSerializable
        {
            get => Serialize.BrushToString(SlowMABearColor);
            set => SlowMABearColor = Serialize.StringToBrush(value);
        }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Slow MA Thickness", Order = 7, GroupName = "3. MA Lines")]
        public int SlowMAThickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show MA Labels", Order = 8, GroupName = "3. MA Lines", Description = "Show MA price labels")]
        public bool ShowMALabels { get; set; }

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "IQ Scalp Ribbon — Fast-reaction ribbon for scalp entries and momentum confirmation";
                Name                     = "IQScalpRibbon";
                Calculate                = Calculate.OnBarClose;
                IsOverlay                = true;
                IsAutoScale              = false;
                DisplayInDataBox         = false;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = false;
                MaximumBarsLookBack      = MaximumBarsLookBack.Infinite;

                ShowRibbon   = true;
                FastPeriod   = 5;
                SlowPeriod   = 13;
                UseEMA       = true;

                var bullBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 188, 212)); // Teal
                bullBrush.Freeze();
                RibbonBullColor = bullBrush;

                var bearBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 82, 82)); // Red
                bearBrush.Freeze();
                RibbonBearColor = bearBrush;

                RibbonOpacity = 40;

                ShowMALines = true;

                var fastBullBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 230, 230)); // Bright Cyan
                fastBullBrush.Freeze();
                FastMABullColor = fastBullBrush;

                var fastBearBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 100, 100)); // Light Red
                fastBearBrush.Freeze();
                FastMABearColor = fastBearBrush;

                FastMAThickness = 1;

                var slowBullBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 150, 180)); // Darker Cyan
                slowBullBrush.Freeze();
                SlowMABullColor = slowBullBrush;

                var slowBearBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 60, 60)); // Darker Red
                slowBearBrush.Freeze();
                SlowMABearColor = slowBearBrush;

                SlowMAThickness = 1;
                ShowMALabels    = false;

                AddPlot(new Stroke(FastMABullColor, FastMAThickness), PlotStyle.Line, "FastMA");
                AddPlot(new Stroke(SlowMABullColor, SlowMAThickness), PlotStyle.Line, "SlowMA");
                AddPlot(new Stroke(Brushes.Transparent, 1),            PlotStyle.Line, "RibbonUpper");
                AddPlot(new Stroke(Brushes.Transparent, 1),            PlotStyle.Line, "RibbonLower");
            }
            else if (State == State.Configure)
            {
                // Configuration if needed
            }
            else if (State == State.DataLoaded)
            {
                try
                {
                    // Initialize moving averages based on type
                    if (UseEMA)
                    {
                        _fastMA = EMA(Close, FastPeriod);
                        _slowMA = EMA(Close, SlowPeriod);
                    }
                    else
                    {
                        _fastMA = SMA(Close, FastPeriod);
                        _slowMA = SMA(Close, SlowPeriod);
                    }

                    if (_fastMA == null || _slowMA == null)
                    {
                        Print("Error: Moving averages failed to initialize");
                        return;
                    }

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
            if (CurrentBar < Math.Max(FastPeriod, SlowPeriod))
            {
                Values[2][0] = double.NaN;
                Values[3][0] = double.NaN;
                return;
            }

            try
            {
                if (_fastMA == null || _slowMA == null)
                    return;

                double fastMA = _fastMA[0];
                double slowMA = _slowMA[0];

                bool isBull = fastMA > slowMA;

                // Set plot values
                if (ShowMALines)
                {
                    Values[0][0] = fastMA;
                    Values[1][0] = slowMA;

                    // Dynamic coloring for MA lines
                    PlotBrushes[0][0] = isBull ? FastMABullColor : FastMABearColor;
                    PlotBrushes[1][0] = isBull ? SlowMABullColor : SlowMABearColor;
                }
                else
                {
                    Values[0][0] = double.NaN;
                    Values[1][0] = double.NaN;
                }

                // Ribbon values
                Values[2][0] = fastMA;
                Values[3][0] = slowMA;

                // Ribbon drawing
                if (ShowRibbon)
                {
                    bool prevBull = CurrentBar > 0 ? _fastMA[1] > _slowMA[1] : isBull;

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
                            string sealTag = "ScalpRibbon_seg_" + _segmentStartBar;
                            _ribbonTags.Add(sealTag);

                            if (_ribbonTags.Count > MAX_RIBBON_SEGMENTS)
                            {
                                string oldestTag = _ribbonTags[0];
                                RemoveDrawObject(oldestTag);
                                _ribbonTags.RemoveAt(0);
                            }

                            Draw.Region(this, sealTag,
                                endBarsAgo, startBarsAgo,
                                Values[2], Values[3],
                                Brushes.Transparent,
                                _segmentIsBull ? RibbonBullColor : RibbonBearColor,
                                RibbonOpacity);
                        }

                        _segmentStartBar = CurrentBar;
                        _segmentIsBull   = isBull;
                    }

                    int currentLen = Math.Min(CurrentBar - _segmentStartBar, 254);
                    Draw.Region(this, "ScalpRibbonCurrent",
                        0, currentLen,
                        Values[2], Values[3],
                        Brushes.Transparent,
                        isBull ? RibbonBullColor : RibbonBearColor,
                        RibbonOpacity);
                }
                else
                {
                    RemoveDrawObject("ScalpRibbonCurrent");
                    foreach (string tag in _ribbonTags)
                        RemoveDrawObject(tag);
                    _ribbonTags.Clear();
                    _segmentStartBar = -1;
                }

                // MA Labels
                if (ShowMALabels && ShowMALines)
                {
                    var labelFont = new NinjaTrader.Gui.Tools.SimpleFont("Arial", 9) { Bold = true };
                    
                    string maType = UseEMA ? "EMA" : "SMA";
                    
                    Draw.Text(this, "FastMALabel", false, 
                        string.Format("{0}{1}  {2:F2}", maType, FastPeriod, fastMA), 
                        0, fastMA, 0, 
                        isBull ? FastMABullColor : FastMABearColor, 
                        labelFont, System.Windows.TextAlignment.Left, 
                        Brushes.Transparent, Brushes.Transparent, 0);
                    
                    Draw.Text(this, "SlowMALabel", false, 
                        string.Format("{0}{1}  {2:F2}", maType, SlowPeriod, slowMA), 
                        0, slowMA, 0, 
                        isBull ? SlowMABullColor : SlowMABearColor, 
                        labelFont, System.Windows.TextAlignment.Left, 
                        Brushes.Transparent, Brushes.Transparent, 0);
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
		private IQScalpRibbon[] cacheIQScalpRibbon;
		public IQScalpRibbon IQScalpRibbon(bool showRibbon, int fastPeriod, int slowPeriod, bool useEMA, System.Windows.Media.Brush ribbonBullColor, System.Windows.Media.Brush ribbonBearColor, int ribbonOpacity, bool showMALines, System.Windows.Media.Brush fastMABullColor, System.Windows.Media.Brush fastMABearColor, int fastMAThickness, System.Windows.Media.Brush slowMABullColor, System.Windows.Media.Brush slowMABearColor, int slowMAThickness, bool showMALabels)
		{
			return IQScalpRibbon(Input, showRibbon, fastPeriod, slowPeriod, useEMA, ribbonBullColor, ribbonBearColor, ribbonOpacity, showMALines, fastMABullColor, fastMABearColor, fastMAThickness, slowMABullColor, slowMABearColor, slowMAThickness, showMALabels);
		}

		public IQScalpRibbon IQScalpRibbon(ISeries<double> input, bool showRibbon, int fastPeriod, int slowPeriod, bool useEMA, System.Windows.Media.Brush ribbonBullColor, System.Windows.Media.Brush ribbonBearColor, int ribbonOpacity, bool showMALines, System.Windows.Media.Brush fastMABullColor, System.Windows.Media.Brush fastMABearColor, int fastMAThickness, System.Windows.Media.Brush slowMABullColor, System.Windows.Media.Brush slowMABearColor, int slowMAThickness, bool showMALabels)
		{
			if (cacheIQScalpRibbon != null)
				for (int idx = 0; idx < cacheIQScalpRibbon.Length; idx++)
					if (cacheIQScalpRibbon[idx] != null && cacheIQScalpRibbon[idx].ShowRibbon == showRibbon && cacheIQScalpRibbon[idx].FastPeriod == fastPeriod && cacheIQScalpRibbon[idx].SlowPeriod == slowPeriod && cacheIQScalpRibbon[idx].UseEMA == useEMA && cacheIQScalpRibbon[idx].RibbonBullColor == ribbonBullColor && cacheIQScalpRibbon[idx].RibbonBearColor == ribbonBearColor && cacheIQScalpRibbon[idx].RibbonOpacity == ribbonOpacity && cacheIQScalpRibbon[idx].ShowMALines == showMALines && cacheIQScalpRibbon[idx].FastMABullColor == fastMABullColor && cacheIQScalpRibbon[idx].FastMABearColor == fastMABearColor && cacheIQScalpRibbon[idx].FastMAThickness == fastMAThickness && cacheIQScalpRibbon[idx].SlowMABullColor == slowMABullColor && cacheIQScalpRibbon[idx].SlowMABearColor == slowMABearColor && cacheIQScalpRibbon[idx].SlowMAThickness == slowMAThickness && cacheIQScalpRibbon[idx].ShowMALabels == showMALabels && cacheIQScalpRibbon[idx].EqualsInput(input))
						return cacheIQScalpRibbon[idx];
			return CacheIndicator<IQScalpRibbon>(new IQScalpRibbon(){ ShowRibbon = showRibbon, FastPeriod = fastPeriod, SlowPeriod = slowPeriod, UseEMA = useEMA, RibbonBullColor = ribbonBullColor, RibbonBearColor = ribbonBearColor, RibbonOpacity = ribbonOpacity, ShowMALines = showMALines, FastMABullColor = fastMABullColor, FastMABearColor = fastMABearColor, FastMAThickness = fastMAThickness, SlowMABullColor = slowMABullColor, SlowMABearColor = slowMABearColor, SlowMAThickness = slowMAThickness, ShowMALabels = showMALabels }, input, ref cacheIQScalpRibbon);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.IQScalpRibbon IQScalpRibbon(bool showRibbon, int fastPeriod, int slowPeriod, bool useEMA, System.Windows.Media.Brush ribbonBullColor, System.Windows.Media.Brush ribbonBearColor, int ribbonOpacity, bool showMALines, System.Windows.Media.Brush fastMABullColor, System.Windows.Media.Brush fastMABearColor, int fastMAThickness, System.Windows.Media.Brush slowMABullColor, System.Windows.Media.Brush slowMABearColor, int slowMAThickness, bool showMALabels)
		{
			return indicator.IQScalpRibbon(Input, showRibbon, fastPeriod, slowPeriod, useEMA, ribbonBullColor, ribbonBearColor, ribbonOpacity, showMALines, fastMABullColor, fastMABearColor, fastMAThickness, slowMABullColor, slowMABearColor, slowMAThickness, showMALabels);
		}

		public Indicators.IQScalpRibbon IQScalpRibbon(ISeries<double> input , bool showRibbon, int fastPeriod, int slowPeriod, bool useEMA, System.Windows.Media.Brush ribbonBullColor, System.Windows.Media.Brush ribbonBearColor, int ribbonOpacity, bool showMALines, System.Windows.Media.Brush fastMABullColor, System.Windows.Media.Brush fastMABearColor, int fastMAThickness, System.Windows.Media.Brush slowMABullColor, System.Windows.Media.Brush slowMABearColor, int slowMAThickness, bool showMALabels)
		{
			return indicator.IQScalpRibbon(input, showRibbon, fastPeriod, slowPeriod, useEMA, ribbonBullColor, ribbonBearColor, ribbonOpacity, showMALines, fastMABullColor, fastMABearColor, fastMAThickness, slowMABullColor, slowMABearColor, slowMAThickness, showMALabels);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.IQScalpRibbon IQScalpRibbon(bool showRibbon, int fastPeriod, int slowPeriod, bool useEMA, System.Windows.Media.Brush ribbonBullColor, System.Windows.Media.Brush ribbonBearColor, int ribbonOpacity, bool showMALines, System.Windows.Media.Brush fastMABullColor, System.Windows.Media.Brush fastMABearColor, int fastMAThickness, System.Windows.Media.Brush slowMABullColor, System.Windows.Media.Brush slowMABearColor, int slowMAThickness, bool showMALabels)
		{
			return indicator.IQScalpRibbon(Input, showRibbon, fastPeriod, slowPeriod, useEMA, ribbonBullColor, ribbonBearColor, ribbonOpacity, showMALines, fastMABullColor, fastMABearColor, fastMAThickness, slowMABullColor, slowMABearColor, slowMAThickness, showMALabels);
		}

		public Indicators.IQScalpRibbon IQScalpRibbon(ISeries<double> input , bool showRibbon, int fastPeriod, int slowPeriod, bool useEMA, System.Windows.Media.Brush ribbonBullColor, System.Windows.Media.Brush ribbonBearColor, int ribbonOpacity, bool showMALines, System.Windows.Media.Brush fastMABullColor, System.Windows.Media.Brush fastMABearColor, int fastMAThickness, System.Windows.Media.Brush slowMABullColor, System.Windows.Media.Brush slowMABearColor, int slowMAThickness, bool showMALabels)
		{
			return indicator.IQScalpRibbon(input, showRibbon, fastPeriod, slowPeriod, useEMA, ribbonBullColor, ribbonBearColor, ribbonOpacity, showMALines, fastMABullColor, fastMABearColor, fastMAThickness, slowMABullColor, slowMABearColor, slowMAThickness, showMALabels);
		}
	}
}

#endregion
