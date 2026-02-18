#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SmartMoneyDashboard : Indicator
    {
        #region Enums
        public enum TrendMethod { PriceAction = 0, EMA = 1, Volume = 2, Hybrid = 3 }
        public enum TrendDirection { Up = 0, Down = 1, Neutral = 2 }
        #endregion

        #region Data Structures
        private class TimeframeData
        {
            public int BarsPeriodMinutes { get; set; }
            public double PreviousClose { get; set; }
            public double CurrentClose { get; set; }
            public double PercentChange { get; set; }
            public DateTime LastBarTime { get; set; }
            public int BarsRemaining { get; set; }
            public int DataSeriesIndex { get; set; }
            public TrendDirection TrendDirection { get; set; }
            public int SignalsConfirmed { get; set; }
        }
        #endregion

        #region Private Fields
        private Dictionary<int, TimeframeData> timeframeMetrics = new Dictionary<int, TimeframeData>();
        private List<int> timeframesList = new List<int>();
        private int maxDataSeries = 0;

        private bool resourcesCreated = false;
        private SharpDX.Direct2D1.SolidColorBrush bullishBrush;
        private SharpDX.Direct2D1.SolidColorBrush bearishBrush;
        private SharpDX.Direct2D1.SolidColorBrush neutralBrush;
        private SharpDX.Direct2D1.SolidColorBrush textBrush;
        private SharpDX.Direct2D1.SolidColorBrush dividerBrush;
        private SharpDX.DirectWrite.TextFormat textFormat;
        private SharpDX.DirectWrite.TextFormat headerFormat;
        private SharpDX.DirectWrite.TextFormat smallFormat;

        private EMA trendEMA;
        private SMA trendSMA;
        private TrendDirection currentTrendDirection = TrendDirection.Neutral;
        private bool level2Available = false;
        #endregion

        #region State Management
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"SmartMoneyDashboard - Multi-timeframe performance dashboard

RECOMMENDED SETUP:
• Best positioned on LEFT side (Top Left or Bottom Left) for all chart types
• Ideal: Create a dedicated blank chart for a professional dashboard view
  - Turn off candles, lines, and price axis visibility for clean look
  - Results in distraction-free monitoring dashboard
• Live Trading Charts: Apply to Top Left for optimal placement
  - Doesn't interfere with price action and order placement

FEATURES:
- Real-time multi-timeframe monitoring (customizable timeframes)
- Live % change tracking with directional arrows
- Hybrid trend detection with 3/3 signal confirmation
- Customizable colors and text sizing
- Lightweight and responsive";
                Name = "SmartMoneyDashboard";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = true;

                EnableDashboard = true;
                ShowTimeRemaining = true;
                ShowPercentPanel = true;
                ShowTrendBox = true;
                UseLevel1Data = true;
                UseLevel2Data = false;
                TimeframesInput = "60,240,1440";
                MaxTimeframesToShow = 5;
                PanelPosition = TextPosition.TopLeft;
                FontSize = 10;
                PanelWidth = 220;
                RowHeight = 17;
                BullishColor = Brushes.LimeGreen;
                BearishColor = Brushes.Crimson;
                NeutralColor = Brushes.DarkGray;
                TextColor = Brushes.White;
                TrendDetectionMethodInput = 3;
                TrendEMAPeriod = 20;
                TrendSMAPeriod = 50;
                VolumeLookback = 20;
            }
            else if (State == State.Configure)
            {
                ParseTimeframesInput();
                for (int i = 0; i < timeframesList.Count && i < MaxTimeframesToShow; i++)
                {
                    AddDataSeries(BarsPeriodType.Minute, timeframesList[i]);
                    maxDataSeries++;
                }
            }
            else if (State == State.DataLoaded)
            {
                foreach (int minutes in timeframesList.Take(MaxTimeframesToShow))
                {
                    if (!timeframeMetrics.ContainsKey(minutes))
                    {
                        timeframeMetrics[minutes] = new TimeframeData
                        {
                            BarsPeriodMinutes = minutes,
                            DataSeriesIndex = timeframesList.IndexOf(minutes) + 1,
                            TrendDirection = TrendDirection.Neutral,
                            BarsRemaining = minutes
                        };
                    }
                }

                if ((TrendMethod)TrendDetectionMethodInput == TrendMethod.EMA || (TrendMethod)TrendDetectionMethodInput == TrendMethod.Hybrid)
                    trendEMA = EMA(Close, TrendEMAPeriod);
                if ((TrendMethod)TrendDetectionMethodInput == TrendMethod.Hybrid)
                    trendSMA = SMA(Close, TrendSMAPeriod);

                level2Available = false;
            }
            else if (State == State.Terminated)
            {
                DisposeSharpDXResources();
            }
        }
        #endregion

        #region Bar Update
        protected override void OnBarUpdate()
        {
            if (!EnableDashboard || CurrentBar < TrendEMAPeriod)
                return;

            if (BarsInProgress == 0)
            {
                CalculateTrendDirection();
                
                // Update real-time metrics for all timeframes on primary series
                for (int i = 0; i < timeframesList.Count && i < MaxTimeframesToShow; i++)
                {
                    int minutes = timeframesList[i];
                    if (timeframeMetrics.ContainsKey(minutes))
                    {
                        TimeframeData tfData = timeframeMetrics[minutes];
                        int dataSeriesIndex = i + 1;
                        
                        // Update real-time percent change using current price from secondary series
                        if (dataSeriesIndex <= BarsArray.Length - 1 && CurrentBars[dataSeriesIndex] >= 0)
                        {
                            double currentPrice = Closes[dataSeriesIndex][0];
                            if (tfData.PreviousClose != 0)
                                tfData.PercentChange = ((currentPrice - tfData.PreviousClose) / tfData.PreviousClose) * 100.0;
                        }
                        
                        // Update bars remaining in real-time
                        tfData.BarsRemaining = CalculateBarsRemaining(minutes);
                        tfData.TrendDirection = currentTrendDirection;
                    }
                }
            }

            if (BarsInProgress > 0 && BarsInProgress <= maxDataSeries && BarsInProgress - 1 < timeframesList.Count)
            {
                int minutes = timeframesList[BarsInProgress - 1];
                if (timeframeMetrics.ContainsKey(minutes))
                {
                    TimeframeData tfData = timeframeMetrics[minutes];
                    tfData.PreviousClose = tfData.CurrentClose;
                    tfData.CurrentClose = Close[0];
                    tfData.LastBarTime = Time[0];
                }
            }
        }
        #endregion

        #region Calculations
        private void ParseTimeframesInput()
        {
            timeframesList.Clear();
            try
            {
                string[] parts = TimeframesInput.Split(',');
                foreach (string part in parts)
                {
                    if (int.TryParse(part.Trim(), out int minutes) && minutes > 0 && !timeframesList.Contains(minutes))
                        timeframesList.Add(minutes);
                }
                timeframesList.Sort();
            }
            catch { timeframesList.Add(60); timeframesList.Add(240); timeframesList.Add(1440); }
        }

        private int CalculateBarsRemaining(int barsPeriodMinutes)
        {
            try
            {
                DateTime now = Time[0];
                int currentMinute = now.Minute;
                int minutesElapsed = currentMinute % barsPeriodMinutes;
                int barsRemaining = barsPeriodMinutes - minutesElapsed;
                return barsRemaining > 0 ? barsRemaining : barsPeriodMinutes;
            }
            catch
            {
                return barsPeriodMinutes;
            }
        }

        private void CalculateTrendDirection()
        {
            TrendMethod method = (TrendMethod)TrendDetectionMethodInput;
            int bullishSignals = 0;

            if (Close[0] > Close[1]) bullishSignals++;
            if (trendEMA != null && trendEMA.Count > 0 && Close[0] > trendEMA[0]) bullishSignals++;
            if (Volume[0] > Volume[1]) bullishSignals++;
            if (trendSMA != null && trendSMA.Count > 0 && Close[0] > trendSMA[0]) bullishSignals++;

            switch (method)
            {
                case TrendMethod.PriceAction:
                    currentTrendDirection = Close[0] > Close[1] ? TrendDirection.Up : TrendDirection.Down;
                    break;
                case TrendMethod.EMA:
                    currentTrendDirection = (trendEMA != null && trendEMA.Count > 0 && Close[0] > trendEMA[0]) ? TrendDirection.Up : TrendDirection.Down;
                    break;
                case TrendMethod.Volume:
                    currentTrendDirection = Volume[0] > Volume[1] ? TrendDirection.Up : TrendDirection.Down;
                    break;
                case TrendMethod.Hybrid:
                    currentTrendDirection = bullishSignals >= 2 ? TrendDirection.Up : (bullishSignals == 1 ? TrendDirection.Neutral : TrendDirection.Down);
                    break;
            }

            if (method == TrendMethod.Hybrid)
                foreach (var tfData in timeframeMetrics.Values)
                    tfData.SignalsConfirmed = bullishSignals;
        }
        #endregion

        #region SharpDX Rendering
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (!EnableDashboard || RenderTarget == null || chartControl == null) return;
            if (!resourcesCreated) CreateSharpDXResources(RenderTarget);

            float panelX, panelY;
            CalculatePanelPosition(chartControl, out panelX, out panelY);

            float currentY = panelY;
            if (ShowTimeRemaining) DrawTimeRemainingPanel(panelX, ref currentY);
            if (ShowPercentPanel) { DrawPercentChangePanel(panelX, ref currentY); }
            if (ShowTrendBox) { DrawTrendBox(panelX, ref currentY); }
        }

        private void CalculatePanelPosition(ChartControl chartControl, out float panelX, out float panelY)
        {
            float padding = 10;

            if (PanelPosition == TextPosition.TopRight)
            {
                panelX = (float)chartControl.ActualWidth - PanelWidth - padding;
                panelY = padding;
            }
            else if (PanelPosition == TextPosition.TopLeft)
            {
                panelX = padding;
                panelY = padding;
            }
            else if (PanelPosition == TextPosition.BottomLeft)
            {
                panelX = padding;
                float totalHeight = (RowHeight * (timeframeMetrics.Count + 3)) + 20;
                panelY = (float)chartControl.ActualHeight - totalHeight - padding;
            }
            else if (PanelPosition == TextPosition.BottomRight)
            {
                panelX = (float)chartControl.ActualWidth - PanelWidth - padding;
                float totalHeight = (RowHeight * (timeframeMetrics.Count + 3)) + 20;
                panelY = (float)chartControl.ActualHeight - totalHeight - padding;
            }
            else
            {
                panelX = padding;
                panelY = padding;
            }
        }

        private void CreateSharpDXResources(SharpDX.Direct2D1.RenderTarget renderTarget)
        {
            if (resourcesCreated) return;
            try
            {
                bullishBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, WpfColorToColor4(BullishColor));
                bearishBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, WpfColorToColor4(BearishColor));
                neutralBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, WpfColorToColor4(NeutralColor));
                textBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, WpfColorToColor4(TextColor));
                dividerBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, new SharpDX.Color4(1f, 1f, 1f, 0.15f));

                textFormat = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial",
                    SharpDX.DirectWrite.FontWeight.Normal, SharpDX.DirectWrite.FontStyle.Normal,
                    SharpDX.DirectWrite.FontStretch.Normal, FontSize);
                headerFormat = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial",
                    SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal,
                    SharpDX.DirectWrite.FontStretch.Normal, FontSize + 2);
                smallFormat = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial",
                    SharpDX.DirectWrite.FontWeight.Normal, SharpDX.DirectWrite.FontStyle.Normal,
                    SharpDX.DirectWrite.FontStretch.Normal, FontSize - 1);

                resourcesCreated = true;
            }
            catch (Exception ex) { Print("SmartMoneyDashboard Error: " + ex.Message); }
        }

        private void DisposeSharpDXResources()
        {
            if (bullishBrush != null) { bullishBrush.Dispose(); bullishBrush = null; }
            if (bearishBrush != null) { bearishBrush.Dispose(); bearishBrush = null; }
            if (neutralBrush != null) { neutralBrush.Dispose(); neutralBrush = null; }
            if (textBrush != null) { textBrush.Dispose(); textBrush = null; }
            if (dividerBrush != null) { dividerBrush.Dispose(); dividerBrush = null; }
            if (textFormat != null) { textFormat.Dispose(); textFormat = null; }
            if (headerFormat != null) { headerFormat.Dispose(); headerFormat = null; }
            if (smallFormat != null) { smallFormat.Dispose(); smallFormat = null; }
            resourcesCreated = false;
        }

        private void DrawTimeRemainingPanel(float x, ref float y)
        {
            DrawText("TIME TO CLOSE", x, y, headerFormat, textBrush);
            y += RowHeight + 2;
            foreach (var kvp in timeframeMetrics.OrderBy(x => x.Key).Take(MaxTimeframesToShow))
            {
                string label = FormatTimeframeLabel(kvp.Key);
                string text = $"{label.PadRight(5)}: {kvp.Value.BarsRemaining.ToString().PadLeft(3)}b";
                DrawText(text, x, y, textFormat, textBrush);
                y += RowHeight - 1;
            }
            y += 8;
        }

        private void DrawPercentChangePanel(float x, ref float y)
        {
            DrawText("% CHANGE", x, y, headerFormat, textBrush);
            y += RowHeight + 2;
            foreach (var kvp in timeframeMetrics.OrderBy(x => x.Key).Take(MaxTimeframesToShow))
            {
                string label = FormatTimeframeLabel(kvp.Key);
                string pctStr = kvp.Value.PercentChange.ToString("+0.00;-0.00;0.00");
                string arrow = kvp.Value.PercentChange > 0 ? "↑" : (kvp.Value.PercentChange < 0 ? "↓" : " ");
                string text = $"{label.PadRight(5)}: {pctStr.PadLeft(7)} {arrow}";
                var colorBrush = kvp.Value.PercentChange > 0 ? bullishBrush : (kvp.Value.PercentChange < 0 ? bearishBrush : neutralBrush);
                DrawText(text, x, y, textFormat, colorBrush);
                y += RowHeight - 1;
            }
            y += 8;
        }

        private void DrawTrendBox(float x, ref float y)
        {
            DrawText("TREND", x, y, headerFormat, textBrush);
            y += RowHeight + 2;
            
            string trendText = currentTrendDirection.ToString().ToUpper();
            var trendBrush = currentTrendDirection == TrendDirection.Up ? bullishBrush : 
                            (currentTrendDirection == TrendDirection.Down ? bearishBrush : neutralBrush);
            
            SharpDX.RectangleF trendRect = new SharpDX.RectangleF(x, y, PanelWidth - 20, RowHeight + 3);
            RenderTarget.FillRectangle(trendRect, trendBrush);
            DrawText(trendText, x + 5, y + 2, headerFormat, textBrush);
            
            y += RowHeight + 5;
            
            TrendMethod method = (TrendMethod)TrendDetectionMethodInput;
            string methodText = method == TrendMethod.Hybrid && timeframeMetrics.Count > 0 
                ? $"Hybrid ({timeframeMetrics.Values.FirstOrDefault()?.SignalsConfirmed ?? 0}/3)"
                : $"{method}";
            DrawText(methodText, x, y, smallFormat, textBrush);
            y += RowHeight;
        }

        private void DrawText(string text, float x, float y, SharpDX.DirectWrite.TextFormat format, SharpDX.Direct2D1.SolidColorBrush brush)
        {
            try
            {
                SharpDX.DirectWrite.TextLayout layout = new SharpDX.DirectWrite.TextLayout(
                    NinjaTrader.Core.Globals.DirectWriteFactory, text, format, 400, 100);
                RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, y), layout, brush);
                layout.Dispose();
            }
            catch { }
        }

        public override void OnRenderTargetChanged() { DisposeSharpDXResources(); }

        private string FormatTimeframeLabel(int minutes)
        {
            if (minutes < 60) return minutes + "m";
            if (minutes == 60) return "1H";
            if (minutes == 240) return "4H";
            if (minutes == 1440) return "1D";
            if (minutes < 1440) return (minutes / 60) + "H";
            return "D";
        }

        private SharpDX.Color4 WpfColorToColor4(Brush brush)
        {
            if (brush is SolidColorBrush solidBrush)
            {
                Color c = solidBrush.Color;
                return new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, 1f);
            }
            return new SharpDX.Color4(1f, 1f, 1f, 1f);
        }
        #endregion

        #region User Properties
        [NinjaScriptProperty]
        [Display(Name = "Enable Dashboard", Order = 1, GroupName = "1. Main")]
        public bool EnableDashboard { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Time Remaining", Order = 2, GroupName = "1. Main")]
        public bool ShowTimeRemaining { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show % Change", Order = 3, GroupName = "1. Main")]
        public bool ShowPercentPanel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Trend", Order = 4, GroupName = "1. Main")]
        public bool ShowTrendBox { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Level 1", Order = 5, GroupName = "1. Main")]
        public bool UseLevel1Data { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Level 2", Order = 6, GroupName = "1. Main")]
        public bool UseLevel2Data { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Timeframes", Description = "60,240,1440", Order = 1, GroupName = "2. Timeframes")]
        public string TimeframesInput { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Max Display", Order = 2, GroupName = "2. Timeframes")]
        public int MaxTimeframesToShow { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Position", Description = "Recommended: Top Left for best results", Order = 1, GroupName = "3. Layout")]
        public TextPosition PanelPosition { get; set; }

        [NinjaScriptProperty]
        [Range(8, 14)]
        [Display(Name = "Font Size", Order = 2, GroupName = "3. Layout")]
        public int FontSize { get; set; }

        [NinjaScriptProperty]
        [Range(50, 400)]
        [Display(Name = "Panel Width", Order = 3, GroupName = "3. Layout")]
        public int PanelWidth { get; set; }

        [NinjaScriptProperty]
        [Range(10, 20)]
        [Display(Name = "Row Height", Order = 4, GroupName = "3. Layout")]
        public int RowHeight { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Bullish Color", Order = 1, GroupName = "4. Colors")]
        public Brush BullishColor { get; set; }

        [Browsable(false)]
        public string BullishColorSerializable
        {
            get { return Serialize.BrushToString(BullishColor); }
            set { BullishColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Bearish Color", Order = 2, GroupName = "4. Colors")]
        public Brush BearishColor { get; set; }

        [Browsable(false)]
        public string BearishColorSerializable
        {
            get { return Serialize.BrushToString(BearishColor); }
            set { BearishColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Neutral Color", Order = 3, GroupName = "4. Colors")]
        public Brush NeutralColor { get; set; }

        [Browsable(false)]
        public string NeutralColorSerializable
        {
            get { return Serialize.BrushToString(NeutralColor); }
            set { NeutralColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Text Color", Order = 4, GroupName = "4. Colors")]
        public Brush TextColor { get; set; }

        [Browsable(false)]
        public string TextColorSerializable
        {
            get { return Serialize.BrushToString(TextColor); }
            set { TextColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(0, 3)]
        [Display(Name = "Trend Method", Description = "0=PriceAction, 1=EMA, 2=Volume, 3=Hybrid", Order = 1, GroupName = "5. Trend")]
        public int TrendDetectionMethodInput { get; set; }

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "EMA Period", Order = 2, GroupName = "5. Trend")]
        public int TrendEMAPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(5, 100)]
        [Display(Name = "SMA Period", Order = 3, GroupName = "5. Trend")]
        public int TrendSMAPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "Volume Lookback", Order = 4, GroupName = "5. Trend")]
        public int VolumeLookback { get; set; }
        #endregion
    }
}