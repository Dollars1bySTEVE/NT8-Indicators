#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class BreakoutLiquiditySweep : Indicator
    {
        private int timeframeMinutes = 60;
        private int emaPeriod1 = 14;
        private int emaPeriod2 = 21;
        private int volumeLookback = 20;
        private bool detectAbsorption = true;
        private double volumeThreshold = 1.5;

        // Cached indicator references — avoids recalculating every bar
        private EMA ema14;
        private EMA ema21;
        private SMA avgVol;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"Breakout with Liquidity Sweep & Absorption Detection - Original concept by Alighten, enhanced by Dollars1bySTEVE";
                Name = "BreakoutLiquiditySweep";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                DisplayInDataBox = true;
                DrawOnPricePanel = true;
                DrawHorizontalGridLines = true;
                DrawVerticalGridLines = true;
                PaintPriceMarkers = true;
                ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                IsSuspendedWhileInactive = true;

                timeframeMinutes = 60;
                emaPeriod1 = 14;
                emaPeriod2 = 21;
                volumeLookback = 20;
                detectAbsorption = true;
                volumeThreshold = 1.5;

                AddPlot(Brushes.Crimson, "EMA 14");
                AddPlot(Brushes.Purple, "EMA 21");

                // Signal plots — Dot style so they show as markers, not connected lines
                AddPlot(new Stroke(Brushes.Lime, 2), PlotStyle.Dot, "Breakout Up Signal");
                AddPlot(new Stroke(Brushes.Red, 2), PlotStyle.Dot, "Breakout Down Signal");
                AddPlot(new Stroke(Brushes.Yellow, 2), PlotStyle.Dot, "Absorption Zone");
            }
            else if (State == State.Configure)
            {
                // HTF series for multi-timeframe context
                AddDataSeries(BarsPeriodType.Minute, timeframeMinutes);
            }
            else if (State == State.DataLoaded)
            {
                // Cache indicator instances once — much more efficient than calling EMA() every bar
                ema14  = EMA(Close, emaPeriod1);
                ema21  = EMA(Close, emaPeriod2);
                avgVol = SMA(Volume, volumeLookback);
            }
        }

        protected override void OnBarUpdate()
        {
            // Only process primary series
            if (BarsInProgress != 0)
                return;

            // Need enough bars for all indicators
            if (CurrentBar < Math.Max(Math.Max(emaPeriod1, emaPeriod2), volumeLookback))
                return;

            // Plot the EMAs
            Values[0][0] = ema14[0];
            Values[1][0] = ema21[0];

            // Calculate volume and range conditions
            double currentVolume = Volume[0];
            bool highVolume = currentVolume > avgVol[0] * volumeThreshold;

            // Small range = absorption (tight bars with big volume)
            double currentRange = High[0] - Low[0];
            double avgRange = 0;
            for (int i = 0; i < 3; i++)
                avgRange += High[i] - Low[i];
            avgRange /= 3.0;

            bool tightRange = currentRange < avgRange * 0.7;

            // ABSORPTION: High volume with tight range = orders being absorbed
            // Only flag if user has enabled absorption detection
            bool absorption = detectAbsorption && highVolume && tightRange;

            // Detect breakouts using cached EMA values
            bool breakoutUp   = Close[0] > ema14[0] && Close[1] <= ema14[1];
            bool breakoutDown = Close[0] < ema21[0] && Close[1] >= ema21[1];

            // SWEEP SIGNAL: Breakout + Absorption = Liquidity Sweep Event
            bool liquiditySweepUp   = breakoutUp && absorption;
            bool liquiditySweepDown = breakoutDown && absorption;

            // --- Bullish signals ---
            if (liquiditySweepUp)
            {
                Values[2][0] = Low[0];
                // Double arrow = Liquidity Sweep UP
                Draw.ArrowUp(this, "SweepUpA" + CurrentBar, false, 0, Low[0] - TickSize * 8, Brushes.Lime);
                Draw.ArrowUp(this, "SweepUpB" + CurrentBar, false, 0, Low[0] - TickSize * 12, Brushes.Lime);
            }
            else if (breakoutUp)
            {
                Values[2][0] = Low[0];
                // Single arrow = Normal Breakout UP
                Draw.ArrowUp(this, "BreakUp" + CurrentBar, false, 0, Low[0] - TickSize * 5, Brushes.Lime);
            }

            // --- Bearish signals ---
            if (liquiditySweepDown)
            {
                Values[3][0] = High[0];
                // Double arrow = Liquidity Sweep DOWN
                Draw.ArrowDown(this, "SweepDnA" + CurrentBar, false, 0, High[0] + TickSize * 8, Brushes.Red);
                Draw.ArrowDown(this, "SweepDnB" + CurrentBar, false, 0, High[0] + TickSize * 12, Brushes.Red);
            }
            else if (breakoutDown)
            {
                Values[3][0] = High[0];
                // Single arrow = Normal Breakout DOWN
                Draw.ArrowDown(this, "BreakDn" + CurrentBar, false, 0, High[0] + TickSize * 5, Brushes.Red);
            }

            // MARK ABSORPTION ZONES
            if (absorption)
            {
                Values[4][0] = (High[0] + Low[0]) / 2;
                Draw.Dot(this, "Absorb" + CurrentBar, false, 0, High[0] + TickSize * 2, Brushes.Yellow);
            }
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, 10000)]
        [Display(Name = "Timeframe Minutes", Description = "Higher timeframe period for multi-TF context.",
            Order = 1, GroupName = "Parameters")]
        public int TimeframeMinutes
        {
            get { return timeframeMinutes; }
            set { timeframeMinutes = value; }
        }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "EMA Period 1 (Short)", Description = "Fast EMA for bullish breakout detection.",
            Order = 2, GroupName = "Parameters")]
        public int EmaPeriod1
        {
            get { return emaPeriod1; }
            set { emaPeriod1 = value; }
        }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "EMA Period 2 (Long)", Description = "Slow EMA for bearish breakout detection.",
            Order = 3, GroupName = "Parameters")]
        public int EmaPeriod2
        {
            get { return emaPeriod2; }
            set { emaPeriod2 = value; }
        }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Volume Lookback Period", Description = "Bars to average for volume comparison.",
            Order = 4, GroupName = "Parameters")]
        public int VolumeLookback
        {
            get { return volumeLookback; }
            set { volumeLookback = value; }
        }

        [NinjaScriptProperty]
        [Range(1.0, 5.0)]
        [Display(Name = "Volume Threshold Multiplier", Description = "How many times above average volume to flag as high volume.",
            Order = 5, GroupName = "Parameters")]
        public double VolumeThreshold
        {
            get { return volumeThreshold; }
            set { volumeThreshold = value; }
        }

        [NinjaScriptProperty]
        [Display(Name = "Detect Absorption", Description = "Enable/disable absorption zone detection.",
            Order = 6, GroupName = "Parameters")]
        public bool DetectAbsorption
        {
            get { return detectAbsorption; }
            set { detectAbsorption = value; }
        }
        #endregion
    }
}