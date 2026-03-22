// Bollinger Bands GPU — Enhanced volatility indicator for NinjaTrader 8
// GPU-accelerated rendering via SharpDX OnRender()

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
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
using SharpDX;
using SharpDX.Direct2D1;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class BollingerBandsGPU : Indicator
    {
        private SMA sma;
        private StdDev stdDev;
        private Series<double> bandwidth;
        private Series<int> breakoutSignal;

        #region Parameters
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Period", Order = 1, GroupName = "Parameters")]
        public int Period { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Std Dev Multiplier", Order = 2, GroupName = "Parameters")]
        public double StdDevMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Expansion Multiplier", Order = 3, GroupName = "Parameters")]
        public double ExpansionMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1.0)]
        [Display(Name = "Squeeze Threshold", Order = 4, GroupName = "Parameters")]
        public double SqueezeThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Band Fill", Order = 5, GroupName = "Visuals")]
        public bool ShowBandFill { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Expansion Fill", Order = 6, GroupName = "Visuals")]
        public bool ShowExpansionFill { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Squeeze Dots", Order = 7, GroupName = "Visuals")]
        public bool ShowSqueezeDots { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Breakout Arrows", Order = 8, GroupName = "Visuals")]
        public bool ShowBreakoutArrows { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Squeeze Dot Size", Order = 9, GroupName = "Visuals")]
        public int SqueezeDotSize { get; set; }

        [NinjaScriptProperty]
        [Range(4, 30)]
        [Display(Name = "Arrow Size", Order = 10, GroupName = "Visuals")]
        public int ArrowSize { get; set; }
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"Bollinger Bands with GPU-accelerated fills, squeeze dots, and breakout arrows";
                Name = "BollingerBandsGPU";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                DisplayInDataBox = true;
                DrawOnPricePanel = true;
                DrawHorizontalGridLines = true;
                DrawVerticalGridLines = true;
                PaintPriceMarkers = true;
                ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                IsSuspendedWhileInactive = true;

                Period = 20;
                StdDevMultiplier = 2.0;
                ExpansionMultiplier = 2.5;
                SqueezeThreshold = 0.02;

                ShowBandFill = true;
                ShowExpansionFill = true;
                ShowSqueezeDots = true;
                ShowBreakoutArrows = true;
                SqueezeDotSize = 5;
                ArrowSize = 12;

                AddPlot(new Stroke(Brushes.White, 3), PlotStyle.Line, "Middle");
                AddPlot(new Stroke(Brushes.Cyan, DashStyleHelper.Dash, 2), PlotStyle.Line, "Upper");
                AddPlot(new Stroke(Brushes.Cyan, DashStyleHelper.Dash, 2), PlotStyle.Line, "Lower");
                AddPlot(new Stroke(Brushes.DarkCyan, DashStyleHelper.Dot, 1), PlotStyle.Line, "ExpansionUpper");
                AddPlot(new Stroke(Brushes.DarkCyan, DashStyleHelper.Dot, 1), PlotStyle.Line, "ExpansionLower");
            }
            else if (State == State.DataLoaded)
            {
                sma = SMA(Period);
                stdDev = StdDev(Period);
                bandwidth = new Series<double>(this);
                breakoutSignal = new Series<int>(this, MaximumBarsLookBack.TwoHundredFiftySix);
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < Period)
                return;

            double middle = sma[0];
            double stdDevValue = stdDev[0];

            double upper = middle + (StdDevMultiplier * stdDevValue);
            double lower = middle - (StdDevMultiplier * stdDevValue);
            double expansionUpper = middle + (ExpansionMultiplier * stdDevValue);
            double expansionLower = middle - (ExpansionMultiplier * stdDevValue);

            bandwidth[0] = middle != 0 ? (upper - lower) / middle : 0;

            Values[0][0] = middle;
            Values[1][0] = upper;
            Values[2][0] = lower;
            Values[3][0] = expansionUpper;
            Values[4][0] = expansionLower;

            if (ShowSqueezeDots && IsInSqueeze())
                PlotBrushes[0][0] = Brushes.Yellow;

            breakoutSignal[0] = 0;
            if (ShowBreakoutArrows)
            {
                bool prevAvailable = CurrentBar > Period;

                if (Close[0] > upper && (!prevAvailable || Close[1] <= Values[1][1]))
                    breakoutSignal[0] = 1;
                else if (Close[0] < lower && (!prevAvailable || Close[1] >= Values[2][1]))
                    breakoutSignal[0] = -1;
            }
        }

        #region SharpDX GPU Rendering
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (Bars == null || ChartBars == null)
                return;

            int firstBarIdx = ChartBars.FromIndex;
            int lastBarIdx = ChartBars.ToIndex;
            firstBarIdx = Math.Max(firstBarIdx, Period);

            if (firstBarIdx > lastBarIdx)
                return;

            var renderTarget = RenderTarget;
            if (renderTarget == null)
                return;

            if (ShowBandFill || ShowExpansionFill)
                RenderFills(renderTarget, chartControl, chartScale, firstBarIdx, lastBarIdx);

            if (ShowSqueezeDots)
                RenderSqueezeDots(renderTarget, chartControl, chartScale, firstBarIdx, lastBarIdx);

            if (ShowBreakoutArrows)
                RenderBreakoutArrows(renderTarget, chartControl, chartScale, firstBarIdx, lastBarIdx);
        }

        private void RenderFills(RenderTarget renderTarget, ChartControl chartControl,
            ChartScale chartScale, int firstBarIdx, int lastBarIdx)
        {
            SharpDX.Color bandColor = new SharpDX.Color(0, 180, 255, 40);
            SharpDX.Color expColor = new SharpDX.Color(160, 160, 160, 20);

            using (var bandBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, bandColor))
            using (var expBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, expColor))
            {
                for (int barIdx = firstBarIdx; barIdx < lastBarIdx; barIdx++)
                {
                    int nextBarIdx = barIdx + 1;
                    if (nextBarIdx > lastBarIdx)
                        break;

                    if (barIdx - Displacement < 0 || barIdx - Displacement >= Values[0].Count)
                        continue;
                    if (nextBarIdx - Displacement < 0 || nextBarIdx - Displacement >= Values[0].Count)
                        continue;

                    float x1 = chartControl.GetXByBarIndex(ChartBars, barIdx);
                    float x2 = chartControl.GetXByBarIndex(ChartBars, nextBarIdx);

                    if (ShowBandFill)
                    {
                        float upper1 = chartScale.GetYByValue(Values[1].GetValueAt(barIdx));
                        float lower1 = chartScale.GetYByValue(Values[2].GetValueAt(barIdx));
                        float upper2 = chartScale.GetYByValue(Values[1].GetValueAt(nextBarIdx));
                        float lower2 = chartScale.GetYByValue(Values[2].GetValueAt(nextBarIdx));
                        FillQuad(renderTarget, bandBrush, x1, upper1, lower1, x2, upper2, lower2);
                    }

                    if (ShowExpansionFill)
                    {
                        float eu1 = chartScale.GetYByValue(Values[3].GetValueAt(barIdx));
                        float u1  = chartScale.GetYByValue(Values[1].GetValueAt(barIdx));
                        float eu2 = chartScale.GetYByValue(Values[3].GetValueAt(nextBarIdx));
                        float u2  = chartScale.GetYByValue(Values[1].GetValueAt(nextBarIdx));
                        FillQuad(renderTarget, expBrush, x1, eu1, u1, x2, eu2, u2);

                        float l1  = chartScale.GetYByValue(Values[2].GetValueAt(barIdx));
                        float el1 = chartScale.GetYByValue(Values[4].GetValueAt(barIdx));
                        float l2  = chartScale.GetYByValue(Values[2].GetValueAt(nextBarIdx));
                        float el2 = chartScale.GetYByValue(Values[4].GetValueAt(nextBarIdx));
                        FillQuad(renderTarget, expBrush, x1, l1, el1, x2, l2, el2);
                    }
                }
            }
        }

        private void FillQuad(RenderTarget renderTarget, SharpDX.Direct2D1.SolidColorBrush brush,
            float x1, float top1, float bot1, float x2, float top2, float bot2)
        {
            var factory = renderTarget.Factory;

            using (var geo = new SharpDX.Direct2D1.PathGeometry(factory))
            {
                using (var sink = geo.Open())
                {
                    sink.BeginFigure(new Vector2(x1, top1), FigureBegin.Filled);
                    sink.AddLine(new Vector2(x2, top2));
                    sink.AddLine(new Vector2(x2, bot2));
                    sink.AddLine(new Vector2(x1, bot1));
                    sink.EndFigure(FigureEnd.Closed);
                    sink.Close();
                }
                renderTarget.FillGeometry(geo, brush);
            }
        }

        private void RenderSqueezeDots(RenderTarget renderTarget, ChartControl chartControl,
            ChartScale chartScale, int firstBarIdx, int lastBarIdx)
        {
            SharpDX.Color dotColor = new SharpDX.Color(255, 255, 0, 230);

            using (var dotBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, dotColor))
            {
                for (int barIdx = firstBarIdx; barIdx <= lastBarIdx; barIdx++)
                {
                    if (barIdx - Displacement < 0 || barIdx - Displacement >= bandwidth.Count)
                        continue;

                    double bw = bandwidth.GetValueAt(barIdx);
                    if (bw >= SqueezeThreshold || bw == 0)
                        continue;

                    float x = chartControl.GetXByBarIndex(ChartBars, barIdx);
                    float y = chartScale.GetYByValue(Values[0].GetValueAt(barIdx));

                    var ellipse = new SharpDX.Direct2D1.Ellipse(new Vector2(x, y), SqueezeDotSize, SqueezeDotSize);
                    renderTarget.FillEllipse(ellipse, dotBrush);
                }
            }
        }

        private void RenderBreakoutArrows(RenderTarget renderTarget, ChartControl chartControl,
            ChartScale chartScale, int firstBarIdx, int lastBarIdx)
        {
            SharpDX.Color upColor = new SharpDX.Color(0, 255, 0, 230);
            SharpDX.Color downColor = new SharpDX.Color(255, 0, 255, 230);

            using (var upBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, upColor))
            using (var downBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, downColor))
            {
                for (int barIdx = firstBarIdx; barIdx <= lastBarIdx; barIdx++)
                {
                    if (barIdx - Displacement < 0 || barIdx - Displacement >= breakoutSignal.Count)
                        continue;

                    int signal = breakoutSignal.GetValueAt(barIdx);
                    if (signal == 0)
                        continue;

                    float x = chartControl.GetXByBarIndex(ChartBars, barIdx);

                    if (signal == 1)
                    {
                        double low = Bars.GetLow(barIdx);
                        float y = chartScale.GetYByValue(low) + ArrowSize + 4;
                        DrawArrowUp(renderTarget, upBrush, x, y, ArrowSize);
                    }
                    else if (signal == -1)
                    {
                        double high = Bars.GetHigh(barIdx);
                        float y = chartScale.GetYByValue(high) - ArrowSize - 4;
                        DrawArrowDown(renderTarget, downBrush, x, y, ArrowSize);
                    }
                }
            }
        }

        private void DrawArrowUp(RenderTarget renderTarget, SharpDX.Direct2D1.SolidColorBrush brush,
            float cx, float cy, float size)
        {
            var factory = renderTarget.Factory;
            float half = size * 0.5f;

            using (var geo = new SharpDX.Direct2D1.PathGeometry(factory))
            {
                using (var sink = geo.Open())
                {
                    sink.BeginFigure(new Vector2(cx, cy - half), FigureBegin.Filled);
                    sink.AddLine(new Vector2(cx + half, cy + half));
                    sink.AddLine(new Vector2(cx - half, cy + half));
                    sink.EndFigure(FigureEnd.Closed);
                    sink.Close();
                }
                renderTarget.FillGeometry(geo, brush);
            }
        }

        private void DrawArrowDown(RenderTarget renderTarget, SharpDX.Direct2D1.SolidColorBrush brush,
            float cx, float cy, float size)
        {
            var factory = renderTarget.Factory;
            float half = size * 0.5f;

            using (var geo = new SharpDX.Direct2D1.PathGeometry(factory))
            {
                using (var sink = geo.Open())
                {
                    sink.BeginFigure(new Vector2(cx, cy + half), FigureBegin.Filled);
                    sink.AddLine(new Vector2(cx + half, cy - half));
                    sink.AddLine(new Vector2(cx - half, cy - half));
                    sink.EndFigure(FigureEnd.Closed);
                    sink.Close();
                }
                renderTarget.FillGeometry(geo, brush);
            }
        }
        #endregion

        #region Properties
        [Browsable(false)]
        [XmlIgnore]
        public Series<double> Middle
        {
            get { return Values[0]; }
        }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> Upper
        {
            get { return Values[1]; }
        }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> Lower
        {
            get { return Values[2]; }
        }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> ExpansionUpper
        {
            get { return Values[3]; }
        }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> ExpansionLower
        {
            get { return Values[4]; }
        }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> Bandwidth
        {
            get { return bandwidth; }
        }

        public bool IsInSqueeze()
        {
            if (CurrentBar < Period)
                return false;
            return bandwidth[0] < SqueezeThreshold;
        }

        public bool IsExpanding()
        {
            if (CurrentBar < Period + 2)
                return false;
            return bandwidth[0] > bandwidth[1] && bandwidth[1] < bandwidth[2];
        }

        public bool IsBreakoutAbove(double price)
        {
            if (CurrentBar < Period)
                return false;
            return price > Values[1][0];
        }

        public bool IsBreakoutBelow(double price)
        {
            if (CurrentBar < Period)
                return false;
            return price < Values[2][0];
        }
        #endregion
    }
}
