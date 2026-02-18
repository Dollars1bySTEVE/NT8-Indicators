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
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SmartMoneyDashboard : Indicator
    {
        #region Enums
        public enum TrendCalculationMethod
        {
            PriceAction,
            EMA,
            Volume,
            Hybrid
        }
        
        public enum PanelPositionType
        {
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight
        }
        #endregion
        
        #region Private Classes
        private class TimeframeData
        {
            public int Minutes;
            public double PreviousClose;
            public double CurrentClose;
            public double PreviousVolume;
            public double CurrentVolume;
            public int BarsRemaining;
            public double PercentChange;
            public string TrendDirection;
            public int TrendConfidence; // For hybrid mode (0-3)
        }
        #endregion
        
        #region Private Fields
        private Dictionary<int, TimeframeData> timeframeDataDict;
        private List<int> timeframeMinutes;
        private EMA emaIndicator;
        private SMA smaIndicator;
        
        // SharpDX resources
        private bool resourcesCreated;
        private SharpDX.Direct2D1.SolidColorBrush backgroundBrush;
        private SharpDX.Direct2D1.SolidColorBrush textBrush;
        private SharpDX.Direct2D1.SolidColorBrush bullishBrush;
        private SharpDX.Direct2D1.SolidColorBrush bearishBrush;
        private SharpDX.Direct2D1.SolidColorBrush neutralBrush;
        private SharpDX.DirectWrite.TextFormat textFormat;
        private SharpDX.DirectWrite.TextFormat headerFormat;
        
        // Level 2 data availability
        private bool level2Available = false;
        #endregion
        
        #region State Management
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"Professional multi-timeframe monitoring dashboard for futures traders with time-to-close, % change, and trend analysis";
                Name = "SmartMoneyDashboard";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = false;
                
                // Display Toggles
                EnableDashboard = true;
                ShowTimeRemaining = true;
                ShowPercentPanel = true;
                ShowTrendBox = true;
                UseLevel2Data = false; // Off by default, requires broker support
                
                // Timeframe Configuration
                TimeframesInput = "60,240,1440"; // 1H, 4H, Daily - optimal for ES/NQ day traders
                MaxTimeframesToShow = 10;
                
                // Layout & Appearance
                PanelPosition = PanelPositionType.TopLeft;
                PanelOpacity = 85;
                FontSize = 10;
                PanelWidth = 300;
                RowHeight = 18;
                PaddingX = 10;
                PaddingY = 10;
                
                // Color Customization
                BullishColor = Brushes.LimeGreen;
                BearishColor = Brushes.Crimson;
                NeutralColor = Brushes.DarkGray;
                TextColor = Brushes.White;
                BackgroundColor = Brushes.Black;
                
                // Trend Detection Methods
                TrendMethod = TrendCalculationMethod.Hybrid;
                TrendEMAPeriod = 20;
                TrendSMAPeriod = 50;
                VolumeLookback = 20;
            }
            else if (State == State.Configure)
            {
                // Parse timeframes
                ParseTimeframes();
                
                // Add EMAs if needed for trend detection
                if (TrendMethod == TrendCalculationMethod.EMA || TrendMethod == TrendCalculationMethod.Hybrid)
                {
                    emaIndicator = EMA(TrendEMAPeriod);
                }
                
                if (TrendMethod == TrendCalculationMethod.Hybrid)
                {
                    smaIndicator = SMA(TrendSMAPeriod);
                }
            }
            else if (State == State.DataLoaded)
            {
                timeframeDataDict = new Dictionary<int, TimeframeData>();
                
                // Initialize timeframe data
                foreach (int minutes in timeframeMinutes)
                {
                    timeframeDataDict[minutes] = new TimeframeData
                    {
                        Minutes = minutes,
                        PreviousClose = 0,
                        CurrentClose = 0,
                        PreviousVolume = 0,
                        CurrentVolume = 0,
                        BarsRemaining = 0,
                        PercentChange = 0,
                        TrendDirection = "NEUTRAL",
                        TrendConfidence = 0
                    };
                }
                
                // Check for Level 2 data availability
                CheckLevel2Availability();
            }
            else if (State == State.Terminated)
            {
                DisposeResources();
            }
        }
        #endregion
        
        #region OnBarUpdate
        protected override void OnBarUpdate()
        {
            if (CurrentBar < Math.Max(TrendEMAPeriod, VolumeLookback))
                return;
                
            if (!EnableDashboard)
                return;
            
            // Update all timeframes
            UpdateTimeframeData();
        }
        #endregion
        
        #region OnRender
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (!EnableDashboard || ChartBars == null || Bars == null)
                return;
                
            if (!resourcesCreated)
            {
                CreateResources(chartControl);
            }
            
            if (resourcesCreated)
            {
                RenderDashboard(chartControl, chartScale);
            }
        }
        #endregion
        
        #region Timeframe Parsing
        private void ParseTimeframes()
        {
            timeframeMinutes = new List<int>();
            
            if (string.IsNullOrWhiteSpace(TimeframesInput))
            {
                // Default fallback
                timeframeMinutes.Add(60);
                timeframeMinutes.Add(240);
                timeframeMinutes.Add(1440);
                return;
            }
            
            string[] parts = TimeframesInput.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (string part in parts)
            {
                if (int.TryParse(part.Trim(), out int minutes) && minutes > 0)
                {
                    timeframeMinutes.Add(minutes);
                }
            }
            
            // Limit to MaxTimeframesToShow
            if (timeframeMinutes.Count > MaxTimeframesToShow)
            {
                timeframeMinutes = timeframeMinutes.Take(MaxTimeframesToShow).ToList();
            }
            
            // Sort timeframes
            timeframeMinutes.Sort();
        }
        #endregion
        
        #region Update Timeframe Data
        private void UpdateTimeframeData()
        {
            foreach (int minutes in timeframeMinutes)
            {
                if (!timeframeDataDict.ContainsKey(minutes))
                    continue;
                    
                TimeframeData data = timeframeDataDict[minutes];
                
                // Calculate bars remaining
                data.BarsRemaining = CalculateBarsRemaining(minutes);
                
                // Check if new bar for this timeframe and store previous close
                if (IsNewBar(minutes) && CurrentBar > 0)
                {
                    data.PreviousClose = Close[1];
                }
                
                // Update current close
                data.CurrentClose = Close[0];
                
                // Calculate % change
                if (data.PreviousClose > 0)
                {
                    data.PercentChange = ((data.CurrentClose - data.PreviousClose) / data.PreviousClose) * 100.0;
                }
                
                // Calculate trend direction
                CalculateTrend(data);
            }
        }
        
        private int CalculateBarsRemaining(int timeframeMinutes)
        {
            // Formula: ((TimeframeMinutes * 60) - SecondsElapsed) / 60 = minutes remaining
            
            // For higher timeframes, we need to calculate based on the timeframe
            int secondsInTimeframe = timeframeMinutes * 60;
            
            // Get current time and calculate elapsed time in current bar
            DateTime currentTime = Time[0];
            DateTime timeframeStart = GetTimeframeStart(currentTime, timeframeMinutes);
            
            TimeSpan elapsed = currentTime - timeframeStart;
            int secondsElapsed = (int)elapsed.TotalSeconds;
            
            int secondsRemaining = secondsInTimeframe - secondsElapsed;
            int minutesRemaining = Math.Max(0, secondsRemaining / 60);
            
            return minutesRemaining;
        }
        
        private DateTime GetTimeframeStart(DateTime currentTime, int minutes)
        {
            // Round down to the start of the timeframe period
            long ticks = currentTime.Ticks;
            long timeframeTicks = TimeSpan.FromMinutes(minutes).Ticks;
            long startTicks = (ticks / timeframeTicks) * timeframeTicks;
            return new DateTime(startTicks);
        }
        
        private bool IsNewBar(int timeframeMinutes)
        {
            if (CurrentBar < 1)
                return false;
                
            DateTime current = GetTimeframeStart(Time[0], timeframeMinutes);
            DateTime previous = GetTimeframeStart(Time[1], timeframeMinutes);
            
            return current != previous;
        }
        #endregion
        
        #region Trend Calculation
        private void CalculateTrend(TimeframeData data)
        {
            bool priceUp = false;
            bool emaUp = false;
            bool volumeUp = false;
            int signals = 0;
            
            switch (TrendMethod)
            {
                case TrendCalculationMethod.PriceAction:
                    priceUp = CalculatePriceActionTrend();
                    data.TrendDirection = priceUp ? "UP" : "DOWN";
                    data.TrendConfidence = 1;
                    break;
                    
                case TrendCalculationMethod.EMA:
                    emaUp = CalculateEMATrend();
                    data.TrendDirection = emaUp ? "UP" : "DOWN";
                    data.TrendConfidence = 1;
                    break;
                    
                case TrendCalculationMethod.Volume:
                    volumeUp = CalculateVolumeTrend();
                    data.TrendDirection = volumeUp ? "UP" : "DOWN";
                    data.TrendConfidence = 1;
                    break;
                    
                case TrendCalculationMethod.Hybrid:
                    priceUp = CalculatePriceActionTrend();
                    emaUp = CalculateEMATrend();
                    volumeUp = CalculateVolumeTrend();
                    
                    signals = (priceUp ? 1 : 0) + (emaUp ? 1 : 0) + (volumeUp ? 1 : 0);
                    
                    if (signals >= 2)
                        data.TrendDirection = "UP";
                    else if (signals == 0)
                        data.TrendDirection = "DOWN";
                    else
                        data.TrendDirection = "NEUTRAL";
                        
                    data.TrendConfidence = signals;
                    break;
            }
        }
        
        private bool CalculatePriceActionTrend()
        {
            if (CurrentBar < 1)
                return false;
                
            return Close[0] > Close[1];
        }
        
        private bool CalculateEMATrend()
        {
            if (emaIndicator == null || CurrentBar < TrendEMAPeriod)
                return false;
                
            return Close[0] > emaIndicator[0];
        }
        
        private bool CalculateVolumeTrend()
        {
            if (CurrentBar < VolumeLookback)
                return false;
                
            double avgVolume = 0;
            for (int i = 1; i <= VolumeLookback; i++)
            {
                avgVolume += Volume[i];
            }
            avgVolume /= VolumeLookback;
            
            return Volume[0] > avgVolume;
        }
        #endregion
        
        #region Level 2 Data
        private void CheckLevel2Availability()
        {
            // Level 2 data availability check
            // This is a simplified check - in real implementation, you would check broker capabilities
            try
            {
                // Check if market depth is available
                if (Bars != null && Bars.Instrument != null)
                {
                    // Most brokers don't expose Level 2 through standard NT8 API
                    // This would require custom broker adapter
                    level2Available = false;
                }
            }
            catch
            {
                level2Available = false;
            }
        }
        #endregion
        
        #region Rendering
        private void CreateResources(ChartControl chartControl)
        {
            try
            {
                // Get RenderTarget
                var renderTarget = chartControl.RenderTarget;
                if (renderTarget == null || renderTarget.IsDisposed)
                    return;
                
                // Create brushes
                float alpha = PanelOpacity / 100f;
                
                backgroundBrush = new SharpDX.Direct2D1.SolidColorBrush(
                    renderTarget,
                    ConvertColor(BackgroundColor, alpha)
                );
                
                textBrush = new SharpDX.Direct2D1.SolidColorBrush(
                    renderTarget,
                    ConvertColor(TextColor, 1.0f)
                );
                
                bullishBrush = new SharpDX.Direct2D1.SolidColorBrush(
                    renderTarget,
                    ConvertColor(BullishColor, 1.0f)
                );
                
                bearishBrush = new SharpDX.Direct2D1.SolidColorBrush(
                    renderTarget,
                    ConvertColor(BearishColor, 1.0f)
                );
                
                neutralBrush = new SharpDX.Direct2D1.SolidColorBrush(
                    renderTarget,
                    ConvertColor(NeutralColor, 1.0f)
                );
                
                // Create text formats
                textFormat = new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory,
                    "Arial",
                    SharpDX.DirectWrite.FontWeight.Normal,
                    SharpDX.DirectWrite.FontStyle.Normal,
                    FontSize
                );
                
                headerFormat = new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory,
                    "Arial",
                    SharpDX.DirectWrite.FontWeight.Bold,
                    SharpDX.DirectWrite.FontStyle.Normal,
                    FontSize + 1
                );
                
                resourcesCreated = true;
            }
            catch (Exception ex)
            {
                Print("Error creating resources: " + ex.Message);
                resourcesCreated = false;
            }
        }
        
        private SharpDX.Color4 ConvertColor(Brush brush, float alpha)
        {
            if (brush is SolidColorBrush solidBrush)
            {
                Color c = solidBrush.Color;
                return new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, alpha);
            }
            return new SharpDX.Color4(1f, 1f, 1f, alpha);
        }
        
        private void RenderDashboard(ChartControl chartControl, ChartScale chartScale)
        {
            if (!resourcesCreated)
                return;
                
            var renderTarget = chartControl.RenderTarget;
            if (renderTarget == null || renderTarget.IsDisposed)
                return;
            
            // Calculate panel dimensions
            int panelHeight = CalculatePanelHeight();
            
            // Get panel position
            SharpDX.RectangleF panelRect = GetPanelRectangle(chartControl, PanelWidth, panelHeight);
            
            // Draw background
            renderTarget.FillRectangle(panelRect, backgroundBrush);
            
            // Draw content
            float yOffset = panelRect.Top + PaddingY;
            float xOffset = panelRect.Left + PaddingX;
            
            // Draw header
            string header = "Smart Money Dashboard";
            renderTarget.DrawText(
                header,
                headerFormat,
                new SharpDX.RectangleF(xOffset, yOffset, panelRect.Width - PaddingX * 2, RowHeight),
                textBrush
            );
            yOffset += RowHeight + 5;
            
            // Draw Level 2 status if enabled
            if (UseLevel2Data)
            {
                string level2Status = level2Available ? "Level 2: Active" : "Level 2: Not Available (L1 Only)";
                var statusBrush = level2Available ? bullishBrush : neutralBrush;
                renderTarget.DrawText(
                    level2Status,
                    textFormat,
                    new SharpDX.RectangleF(xOffset, yOffset, panelRect.Width - PaddingX * 2, RowHeight),
                    statusBrush
                );
                yOffset += RowHeight;
            }
            
            yOffset += 5; // Extra spacing
            
            // Draw Panel A: Time to Close
            if (ShowTimeRemaining)
            {
                yOffset = DrawTimeToClosePanel(renderTarget, xOffset, yOffset, panelRect.Width - PaddingX * 2);
            }
            
            // Draw Panel B: % Change
            if (ShowPercentPanel)
            {
                yOffset = DrawPercentChangePanel(renderTarget, xOffset, yOffset, panelRect.Width - PaddingX * 2);
            }
            
            // Draw Panel C: Trend Direction
            if (ShowTrendBox)
            {
                yOffset = DrawTrendPanel(renderTarget, xOffset, yOffset, panelRect.Width - PaddingX * 2);
            }
        }
        
        private float DrawTimeToClosePanel(SharpDX.Direct2D1.RenderTarget renderTarget, float x, float y, float width)
        {
            // Header
            renderTarget.DrawText(
                "TIME TO CLOSE",
                headerFormat,
                new SharpDX.RectangleF(x, y, width, RowHeight),
                textBrush
            );
            y += RowHeight;
            
            // Draw each timeframe
            foreach (var kvp in timeframeDataDict.OrderBy(kv => kv.Key))
            {
                TimeframeData data = kvp.Value;
                string label = FormatTimeframeLabel(data.Minutes);
                string text = string.Format("{0}: {1} min", label, data.BarsRemaining);
                
                renderTarget.DrawText(
                    text,
                    textFormat,
                    new SharpDX.RectangleF(x, y, width, RowHeight),
                    textBrush
                );
                y += RowHeight;
            }
            
            y += 5; // Spacing
            return y;
        }
        
        private float DrawPercentChangePanel(SharpDX.Direct2D1.RenderTarget renderTarget, float x, float y, float width)
        {
            // Header
            renderTarget.DrawText(
                "% CHANGE",
                headerFormat,
                new SharpDX.RectangleF(x, y, width, RowHeight),
                textBrush
            );
            y += RowHeight;
            
            // Draw each timeframe
            foreach (var kvp in timeframeDataDict.OrderBy(kv => kv.Key))
            {
                TimeframeData data = kvp.Value;
                string label = FormatTimeframeLabel(data.Minutes);
                string arrow = data.PercentChange >= 0 ? "↑" : "↓";
                string text = string.Format("{0}: {1:+0.00;-0.00}% {2}", label, data.PercentChange, arrow);
                
                var brush = data.PercentChange >= 0 ? bullishBrush : bearishBrush;
                
                renderTarget.DrawText(
                    text,
                    textFormat,
                    new SharpDX.RectangleF(x, y, width, RowHeight),
                    brush
                );
                y += RowHeight;
            }
            
            y += 5; // Spacing
            return y;
        }
        
        private float DrawTrendPanel(SharpDX.Direct2D1.RenderTarget renderTarget, float x, float y, float width)
        {
            // Header
            string methodName = TrendMethod.ToString();
            string header = string.Format("TREND ({0})", methodName);
            
            renderTarget.DrawText(
                header,
                headerFormat,
                new SharpDX.RectangleF(x, y, width, RowHeight),
                textBrush
            );
            y += RowHeight;
            
            // Draw each timeframe
            foreach (var kvp in timeframeDataDict.OrderBy(kv => kv.Key))
            {
                TimeframeData data = kvp.Value;
                string label = FormatTimeframeLabel(data.Minutes);
                string confidence = TrendMethod == TrendCalculationMethod.Hybrid 
                    ? string.Format(" ({0}/3)", data.TrendConfidence)
                    : "";
                    
                string text = string.Format("{0}: {1}{2}", label, data.TrendDirection, confidence);
                
                SharpDX.Direct2D1.SolidColorBrush brush;
                if (data.TrendDirection == "UP")
                    brush = bullishBrush;
                else if (data.TrendDirection == "DOWN")
                    brush = bearishBrush;
                else
                    brush = neutralBrush;
                
                renderTarget.DrawText(
                    text,
                    textFormat,
                    new SharpDX.RectangleF(x, y, width, RowHeight),
                    brush
                );
                y += RowHeight;
            }
            
            // Add detailed hybrid breakdown if hybrid mode
            if (TrendMethod == TrendCalculationMethod.Hybrid && timeframeDataDict.Count > 0)
            {
                y += 5;
                
                bool priceUp = CalculatePriceActionTrend();
                bool emaUp = CalculateEMATrend();
                bool volumeUp = CalculateVolumeTrend();
                
                string priceStatus = priceUp ? "✓ Price" : "✗ Price";
                string emaStatus = emaUp ? "✓ EMA" : "✗ EMA";
                string volStatus = volumeUp ? "✓ Volume" : "✗ Volume";
                
                renderTarget.DrawText(
                    priceStatus,
                    textFormat,
                    new SharpDX.RectangleF(x, y, width, RowHeight),
                    priceUp ? bullishBrush : bearishBrush
                );
                y += RowHeight;
                
                renderTarget.DrawText(
                    emaStatus,
                    textFormat,
                    new SharpDX.RectangleF(x, y, width, RowHeight),
                    emaUp ? bullishBrush : bearishBrush
                );
                y += RowHeight;
                
                renderTarget.DrawText(
                    volStatus,
                    textFormat,
                    new SharpDX.RectangleF(x, y, width, RowHeight),
                    volumeUp ? bullishBrush : bearishBrush
                );
                y += RowHeight;
            }
            
            y += 5; // Spacing
            return y;
        }
        
        private string FormatTimeframeLabel(int minutes)
        {
            if (minutes < 60)
                return minutes + "m";
            else if (minutes < 1440)
                return (minutes / 60) + "H";
            else if (minutes == 1440)
                return "Daily";
            else if (minutes == 10080)
                return "Weekly";
            else
                return (minutes / 1440) + "D";
        }
        
        private int CalculatePanelHeight()
        {
            int height = PaddingY * 2;
            
            // Header
            height += RowHeight + 5;
            
            // Level 2 status if enabled
            if (UseLevel2Data)
                height += RowHeight;
            
            height += 5; // Extra spacing
            
            // Panel A: Time to Close
            if (ShowTimeRemaining)
            {
                height += RowHeight; // Header
                height += timeframeDataDict.Count * RowHeight; // Data rows
                height += 5; // Spacing
            }
            
            // Panel B: % Change
            if (ShowPercentPanel)
            {
                height += RowHeight; // Header
                height += timeframeDataDict.Count * RowHeight; // Data rows
                height += 5; // Spacing
            }
            
            // Panel C: Trend
            if (ShowTrendBox)
            {
                height += RowHeight; // Header
                height += timeframeDataDict.Count * RowHeight; // Data rows
                
                // Add hybrid breakdown rows if applicable
                if (TrendMethod == TrendCalculationMethod.Hybrid)
                {
                    height += 5 + (RowHeight * 3); // Price, EMA, Volume status
                }
                
                height += 5; // Spacing
            }
            
            return height;
        }
        
        private SharpDX.RectangleF GetPanelRectangle(ChartControl chartControl, int width, int height)
        {
            float x = 0;
            float y = 0;
            
            switch (PanelPosition)
            {
                case PanelPositionType.TopLeft:
                    x = 10;
                    y = 10;
                    break;
                    
                case PanelPositionType.TopRight:
                    x = chartControl.CanvasRight - width - 10;
                    y = 10;
                    break;
                    
                case PanelPositionType.BottomLeft:
                    x = 10;
                    y = chartControl.CanvasBottom - height - 10;
                    break;
                    
                case PanelPositionType.BottomRight:
                    x = chartControl.CanvasRight - width - 10;
                    y = chartControl.CanvasBottom - height - 10;
                    break;
            }
            
            return new SharpDX.RectangleF(x, y, width, height);
        }
        
        private void DisposeResources()
        {
            if (backgroundBrush != null && !backgroundBrush.IsDisposed)
            {
                backgroundBrush.Dispose();
                backgroundBrush = null;
            }
            
            if (textBrush != null && !textBrush.IsDisposed)
            {
                textBrush.Dispose();
                textBrush = null;
            }
            
            if (bullishBrush != null && !bullishBrush.IsDisposed)
            {
                bullishBrush.Dispose();
                bullishBrush = null;
            }
            
            if (bearishBrush != null && !bearishBrush.IsDisposed)
            {
                bearishBrush.Dispose();
                bearishBrush = null;
            }
            
            if (neutralBrush != null && !neutralBrush.IsDisposed)
            {
                neutralBrush.Dispose();
                neutralBrush = null;
            }
            
            if (textFormat != null && !textFormat.IsDisposed)
            {
                textFormat.Dispose();
                textFormat = null;
            }
            
            if (headerFormat != null && !headerFormat.IsDisposed)
            {
                headerFormat.Dispose();
                headerFormat = null;
            }
            
            resourcesCreated = false;
        }
        #endregion
        
        #region Properties
        
        // ==================== Display Toggles ====================
        [NinjaScriptProperty]
        [Display(Name = "Enable Dashboard", Order = 1, GroupName = "1. Display Toggles")]
        public bool EnableDashboard { get; set; }
        
        [NinjaScriptProperty]
        [Display(Name = "Show Time Remaining", Order = 2, GroupName = "1. Display Toggles")]
        public bool ShowTimeRemaining { get; set; }
        
        [NinjaScriptProperty]
        [Display(Name = "Show % Change Panel", Order = 3, GroupName = "1. Display Toggles")]
        public bool ShowPercentPanel { get; set; }
        
        [NinjaScriptProperty]
        [Display(Name = "Show Trend Box", Order = 4, GroupName = "1. Display Toggles")]
        public bool ShowTrendBox { get; set; }
        
        [NinjaScriptProperty]
        [Display(Name = "Use Level 2 Data (if available)", Order = 5, GroupName = "1. Display Toggles")]
        public bool UseLevel2Data { get; set; }
        
        // ==================== Timeframe Configuration ====================
        [NinjaScriptProperty]
        [Display(Name = "Timeframes (comma-separated minutes)", Order = 1, GroupName = "2. Timeframe Config", 
            Description = "E.g., '1,5,15,60' or '60,240,1440'. Profiles: Scalper(1,5,15,60), DayTrader(15,60,240), SwingTrader(60,240,1440)")]
        public string TimeframesInput { get; set; }
        
        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Max Timeframes to Show", Order = 2, GroupName = "2. Timeframe Config")]
        public int MaxTimeframesToShow { get; set; }
        
        // ==================== Layout & Appearance ====================
        [NinjaScriptProperty]
        [Display(Name = "Panel Position", Order = 1, GroupName = "3. Layout")]
        public PanelPositionType PanelPosition { get; set; }
        
        [NinjaScriptProperty]
        [Range(10, 100)]
        [Display(Name = "Panel Opacity (%)", Order = 2, GroupName = "3. Layout")]
        public int PanelOpacity { get; set; }
        
        [NinjaScriptProperty]
        [Range(8, 14)]
        [Display(Name = "Font Size", Order = 3, GroupName = "3. Layout")]
        public int FontSize { get; set; }
        
        [NinjaScriptProperty]
        [Range(50, 400)]
        [Display(Name = "Panel Width (px)", Order = 4, GroupName = "3. Layout")]
        public int PanelWidth { get; set; }
        
        [NinjaScriptProperty]
        [Range(10, 30)]
        [Display(Name = "Row Height (px)", Order = 5, GroupName = "3. Layout")]
        public int RowHeight { get; set; }
        
        [NinjaScriptProperty]
        [Range(5, 20)]
        [Display(Name = "Padding X (px)", Order = 6, GroupName = "3. Layout")]
        public int PaddingX { get; set; }
        
        [NinjaScriptProperty]
        [Range(5, 20)]
        [Display(Name = "Padding Y (px)", Order = 7, GroupName = "3. Layout")]
        public int PaddingY { get; set; }
        
        // ==================== Color Customization ====================
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
        [XmlIgnore]
        [Display(Name = "Background Color", Order = 5, GroupName = "4. Colors")]
        public Brush BackgroundColor { get; set; }
        
        [Browsable(false)]
        public string BackgroundColorSerializable
        {
            get { return Serialize.BrushToString(BackgroundColor); }
            set { BackgroundColor = Serialize.StringToBrush(value); }
        }
        
        // ==================== Trend Detection Methods ====================
        [NinjaScriptProperty]
        [Display(Name = "Trend Method", Order = 1, GroupName = "5. Trend Detection")]
        public TrendCalculationMethod TrendMethod { get; set; }
        
        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Trend EMA Period", Order = 2, GroupName = "5. Trend Detection")]
        public int TrendEMAPeriod { get; set; }
        
        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Trend SMA Period", Order = 3, GroupName = "5. Trend Detection")]
        public int TrendSMAPeriod { get; set; }
        
        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Volume Lookback Period", Order = 4, GroupName = "5. Trend Detection")]
        public int VolumeLookback { get; set; }
        
        #endregion
    }
}
