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
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class TAHelperEnhanced : Indicator
    {
        #region Private Fields
        
        // SharpDX Resources
        private SharpDX.Direct2D1.SolidColorBrush panelBackgroundBrush;
        private SharpDX.Direct2D1.SolidColorBrush cellBackground1Brush;
        private SharpDX.Direct2D1.SolidColorBrush cellBackground2Brush;
        private SharpDX.Direct2D1.SolidColorBrush textBrush;
        private SharpDX.Direct2D1.SolidColorBrush strongSellBrush;
        private SharpDX.Direct2D1.SolidColorBrush sellBrush;
        private SharpDX.Direct2D1.SolidColorBrush neutralBrush;
        private SharpDX.Direct2D1.SolidColorBrush buyBrush;
        private SharpDX.Direct2D1.SolidColorBrush strongBuyBrush;
        private SharpDX.DirectWrite.TextFormat textFormat;
        private SharpDX.DirectWrite.TextFormat headerFormat;
        private bool resourcesCreated;
        
        // Signal counters
        private int oscillatorBuy, oscillatorSell, oscillatorNeutral;
        private int maBuy, maSell, maNeutral;
        private int pivotBuy, pivotSell, pivotNeutral;
        
        // Pivot point values
        private double pp, r1, r2, r3, s1, s2, s3;
        private double fibPP, fibR1, fibR2, fibR3, fibS1, fibS2, fibS3;
        private double woodiePP, woodieR1, woodieR2, woodieS1, woodieS2;
        private double camR4, camR3, camR2, camR1, camPP, camS1, camS2, camS3, camS4;
        
        private DateTime lastCalculatedDate;
        
        #endregion
        
        #region State Management
        
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"TAHelperEnhanced - Comprehensive technical analysis dashboard aggregating 32 indicators";
                Name = "TAHelperEnhanced";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                IsAutoScale = false;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = true;
                ScaleJustification = ScaleJustification.Overlay;
                
                // Position settings
                HorizontalPosition = 2; // Right
                VerticalPosition = 2; // Bottom
                TableScale = 1.0;
                TableOpacity = 90;
                TableMargin = 10;
                
                // Color settings
                StrongSellColor = Brushes.Red;
                SellColor = Brushes.Salmon;
                NeutralColor = Brushes.Gray;
                BuyColor = Brushes.LightGreen;
                StrongBuyColor = Brushes.Green;
                PanelBackgroundColor = Brushes.Black;
                CellBackground1Color = Brushes.DimGray;
                CellBackground2Color = Brushes.DarkSlateGray;
            }
            else if (State == State.DataLoaded)
            {
                lastCalculatedDate = DateTime.MinValue;
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
            if (CurrentBar < 200) // Need enough bars for calculations
                return;
            
            // Calculate pivot points once per day
            DateTime currentDate = Time[0].Date;
            if (currentDate != lastCalculatedDate)
            {
                CalculatePivotPoints();
                lastCalculatedDate = currentDate;
            }
            
            // Reset counters
            oscillatorBuy = oscillatorSell = oscillatorNeutral = 0;
            maBuy = maSell = maNeutral = 0;
            pivotBuy = pivotSell = pivotNeutral = 0;
            
            // Calculate all signals
            CalculateOscillators();
            CalculateMovingAverages();
            AnalyzePivotPoints();
        }
        
        #endregion
        
        #region Oscillator Calculations
        
        private void CalculateOscillators()
        {
            double close = Close[0];
            
            // 1. RSI (14)
            double rsi = RSI(14, 3)[0];
            if (rsi < 30) oscillatorBuy++;
            else if (rsi > 70) oscillatorSell++;
            else oscillatorNeutral++;
            
            // 2. Stochastics (14,3,3)
            double stochK = Stochastics(3, 14, 3)[0];
            if (stochK < 20) oscillatorBuy++;
            else if (stochK > 80) oscillatorSell++;
            else oscillatorNeutral++;
            
            // 3. CCI (20)
            double cci = CCI(20)[0];
            if (cci < -100) oscillatorBuy++;
            else if (cci > 100) oscillatorSell++;
            else oscillatorNeutral++;
            
            // 4. ADX/DMI (14)
            double adx = ADX(14)[0];
            double diPlus = DM(14).DiPlus[0];
            double diMinus = DM(14).DiMinus[0];
            if (adx > 25)
            {
                if (diMinus > diPlus) oscillatorSell++;
                else if (diPlus > diMinus) oscillatorBuy++;
                else oscillatorNeutral++;
            }
            else
            {
                oscillatorNeutral++;
            }
            
            // 5. Awesome Oscillator (SMA(HL/2, 5) - SMA(HL/2, 34))
            double ao = SMA(5)[0] - SMA(34)[0];
            if (ao < 0) oscillatorSell++;
            else if (ao > 0) oscillatorBuy++;
            else oscillatorNeutral++;
            
            // 6. Momentum (10)
            double momentum = Momentum(10)[0];
            if (momentum < 0) oscillatorSell++;
            else if (momentum > 0) oscillatorBuy++;
            else oscillatorNeutral++;
            
            // 7. MACD Histogram
            double macdHist = MACD(12, 26, 9).Diff[0];
            double macdHistPrev = MACD(12, 26, 9).Diff[1];
            if (macdHist < macdHistPrev) oscillatorSell++;
            else if (macdHist > macdHistPrev) oscillatorBuy++;
            else oscillatorNeutral++;
            
            // 8. Stoch RSI (14)
            double stochRSI = StochRSI(14)[0];
            if (stochRSI < 20) oscillatorBuy++;
            else if (stochRSI > 80) oscillatorSell++;
            else oscillatorNeutral++;
            
            // 9. Williams %R (14)
            double williamsR = WilliamsR(14)[0];
            if (williamsR < -80) oscillatorBuy++;
            else if (williamsR > -20) oscillatorSell++;
            else oscillatorNeutral++;
            
            // 10. Bull/Bear Power (Close - EMA(13))
            double bullPower = close - EMA(13)[0];
            double bullPowerSMA = 0;
            for (int i = 0; i < 13 && i <= CurrentBar; i++)
            {
                bullPowerSMA += Close[i] - EMA(13)[i];
            }
            bullPowerSMA /= Math.Min(13, CurrentBar + 1);
            
            if (bullPowerSMA < 0) oscillatorSell++;
            else if (bullPowerSMA > 0) oscillatorBuy++;
            else oscillatorNeutral++;
            
            // 11. Ultimate Oscillator (simplified version)
            double uo = CalculateUltimateOscillator();
            if (uo < 30) oscillatorBuy++;
            else if (uo > 70) oscillatorSell++;
            else oscillatorNeutral++;
        }
        
        private double CalculateUltimateOscillator()
        {
            // Simplified Ultimate Oscillator calculation
            if (CurrentBar < 28) return 50;
            
            double bp7 = 0, bp14 = 0, bp28 = 0;
            double tr7 = 0, tr14 = 0, tr28 = 0;
            
            for (int i = 0; i < 28; i++)
            {
                double trueLow = Math.Min(Low[i], (i < CurrentBar ? Close[i + 1] : Low[i]));
                double trueRange = Math.Max(High[i], (i < CurrentBar ? Close[i + 1] : High[i])) - trueLow;
                double buyingPressure = Close[i] - trueLow;
                
                if (i < 7)
                {
                    bp7 += buyingPressure;
                    tr7 += trueRange;
                }
                if (i < 14)
                {
                    bp14 += buyingPressure;
                    tr14 += trueRange;
                }
                bp28 += buyingPressure;
                tr28 += trueRange;
            }
            
            double avg7 = tr7 > 0 ? bp7 / tr7 : 0;
            double avg14 = tr14 > 0 ? bp14 / tr14 : 0;
            double avg28 = tr28 > 0 ? bp28 / tr28 : 0;
            
            return ((4 * avg7) + (2 * avg14) + avg28) / 7.0 * 100;
        }
        
        #endregion
        
        #region Moving Average Calculations
        
        private void CalculateMovingAverages()
        {
            double close = Close[0];
            
            // EMA and SMA for various periods
            int[] periods = { 5, 10, 20, 30, 50, 100, 200 };
            
            foreach (int period in periods)
            {
                if (CurrentBar >= period)
                {
                    // EMA
                    double ema = EMA(period)[0];
                    if (close < ema) maSell++;
                    else if (close > ema) maBuy++;
                    else maNeutral++;
                    
                    // SMA
                    double sma = SMA(period)[0];
                    if (close < sma) maSell++;
                    else if (close > sma) maBuy++;
                    else maNeutral++;
                }
                else
                {
                    maNeutral += 2; // Count both EMA and SMA as neutral if not enough bars
                }
            }
            
            // Ichimoku Base Line (Tenkan-sen: (9-period high + 9-period low)/2)
            if (CurrentBar >= 9)
            {
                double high9 = MAX(High, 9)[0];
                double low9 = MIN(Low, 9)[0];
                double ichimokuBase = (high9 + low9) / 2;
                
                if (close < ichimokuBase) maSell++;
                else if (close > ichimokuBase) maBuy++;
                else maNeutral++;
            }
            else
            {
                maNeutral++;
            }
            
            // VWMA (20) - Volume Weighted Moving Average
            if (CurrentBar >= 20)
            {
                double vwma = VWMA(20)[0];
                if (close < vwma) maSell++;
                else if (close > vwma) maBuy++;
                else maNeutral++;
            }
            else
            {
                maNeutral++;
            }
            
            // HMA (9) - Hull Moving Average
            if (CurrentBar >= 9)
            {
                double hma = HMA(9)[0];
                if (close < hma) maSell++;
                else if (close > hma) maBuy++;
                else maNeutral++;
            }
            else
            {
                maNeutral++;
            }
        }
        
        #endregion
        
        #region Pivot Point Calculations
        
        private void CalculatePivotPoints()
        {
            // Get yesterday's high, low, close
            int barsAgo = 0;
            double high = 0, low = double.MaxValue, open = 0, close = 0;
            
            // Find yesterday's values
            DateTime yesterday = Time[0].Date.AddDays(-1);
            for (int i = 0; i <= CurrentBar && i < 500; i++)
            {
                if (Time[i].Date == yesterday)
                {
                    if (barsAgo == 0)
                    {
                        close = Close[i];
                        open = Open[i];
                    }
                    high = Math.Max(high, High[i]);
                    low = Math.Min(low, Low[i]);
                    barsAgo++;
                }
                else if (barsAgo > 0)
                {
                    break;
                }
            }
            
            if (barsAgo == 0 || low == double.MaxValue)
            {
                // Use current day if yesterday not found
                high = MAX(High, Math.Min(CurrentBar, 100))[0];
                low = MIN(Low, Math.Min(CurrentBar, 100))[0];
                close = Close[Math.Min(10, CurrentBar)];
                open = Open[Math.Min(10, CurrentBar)];
            }
            
            // Traditional Pivot Points
            pp = (high + low + close) / 3;
            r1 = 2 * pp - low;
            s1 = 2 * pp - high;
            r2 = pp + (high - low);
            s2 = pp - (high - low);
            r3 = high + 2 * (pp - low);
            s3 = low - 2 * (high - pp);
            
            // Fibonacci Pivot Points
            fibPP = pp;
            fibR1 = pp + 0.382 * (high - low);
            fibS1 = pp - 0.382 * (high - low);
            fibR2 = pp + 0.618 * (high - low);
            fibS2 = pp - 0.618 * (high - low);
            fibR3 = pp + 1.000 * (high - low);
            fibS3 = pp - 1.000 * (high - low);
            
            // Woodie Pivot Points
            woodiePP = (high + low + 2 * open) / 4;
            woodieR1 = 2 * woodiePP - low;
            woodieS1 = 2 * woodiePP - high;
            woodieR2 = woodiePP + (high - low);
            woodieS2 = woodiePP - (high - low);
            
            // Camarilla Pivot Points
            double range = high - low;
            camPP = close;
            camR1 = close + range * 1.1 / 12;
            camR2 = close + range * 1.1 / 6;
            camR3 = close + range * 1.1 / 4;
            camR4 = close + range * 1.1 / 2;
            camS1 = close - range * 1.1 / 12;
            camS2 = close - range * 1.1 / 6;
            camS3 = close - range * 1.1 / 4;
            camS4 = close - range * 1.1 / 2;
        }
        
        private void AnalyzePivotPoints()
        {
            double close = Close[0];
            
            // Traditional
            if (close > pp) pivotBuy++;
            else if (close < pp) pivotSell++;
            else pivotNeutral++;
            
            // Fibonacci
            if (close > fibPP) pivotBuy++;
            else if (close < fibPP) pivotSell++;
            else pivotNeutral++;
            
            // Woodie
            if (close > woodiePP) pivotBuy++;
            else if (close < woodiePP) pivotSell++;
            else pivotNeutral++;
            
            // Camarilla
            if (close > camPP) pivotBuy++;
            else if (close < camPP) pivotSell++;
            else pivotNeutral++;
        }
        
        #endregion
        
        #region Market Signal Logic
        
        private string GetMarketSignal(out int totalBuy, out int totalSell, out int totalNeutral)
        {
            totalBuy = oscillatorBuy + maBuy + pivotBuy;
            totalSell = oscillatorSell + maSell + pivotSell;
            totalNeutral = oscillatorNeutral + maNeutral + pivotNeutral;
            
            int total = totalBuy + totalSell + totalNeutral;
            if (total == 0) return "NEUTRAL";
            
            double buyPercent = (double)totalBuy / total * 100;
            double sellPercent = (double)totalSell / total * 100;
            
            // STRONG BUY: All 3 categories show more buys AND buy_point > 50%
            if (oscillatorBuy > oscillatorSell && maBuy > maSell && pivotBuy > pivotSell && buyPercent > 50)
                return "STRONG BUY";
            
            // BUY: Total buys > total sells AND buy_point > 50%
            if (totalBuy > totalSell && buyPercent > 50)
                return "BUY";
            
            // STRONG SELL: All 3 categories show more sells AND sell_point > 50%
            if (oscillatorSell > oscillatorBuy && maSell > maBuy && pivotSell > pivotBuy && sellPercent > 50)
                return "STRONG SELL";
            
            // SELL: Total sells > total buys AND sell_point > 50%
            if (totalSell > totalBuy && sellPercent > 50)
                return "SELL";
            
            return "NEUTRAL";
        }
        
        #endregion
        
        #region Rendering
        
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            
            if (CurrentBar < 200 || Bars == null || chartControl == null)
                return;
            
            if (!resourcesCreated)
                CreateResources();
            
            RenderDashboard(chartControl);
        }
        
        private void CreateResources()
        {
            try
            {
                if (RenderTarget == null) return;
                
                // Create brushes
                panelBackgroundBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, 
                    SharpDXHelpers.ToSharpDXColor(PanelBackgroundColor, TableOpacity));
                cellBackground1Brush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, 
                    SharpDXHelpers.ToSharpDXColor(CellBackground1Color, TableOpacity));
                cellBackground2Brush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, 
                    SharpDXHelpers.ToSharpDXColor(CellBackground2Color, TableOpacity));
                textBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, 
                    SharpDXHelpers.ToSharpDX(Colors.White));
                strongSellBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, 
                    SharpDXHelpers.ToSharpDXColor(StrongSellColor, 100));
                sellBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, 
                    SharpDXHelpers.ToSharpDXColor(SellColor, 100));
                neutralBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, 
                    SharpDXHelpers.ToSharpDXColor(NeutralColor, 100));
                buyBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, 
                    SharpDXHelpers.ToSharpDXColor(BuyColor, 100));
                strongBuyBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, 
                    SharpDXHelpers.ToSharpDXColor(StrongBuyColor, 100));
                
                // Create text formats
                textFormat = new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory,
                    "Arial", (float)(11 * TableScale))
                {
                    TextAlignment = SharpDX.DirectWrite.TextAlignment.Leading,
                    WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap
                };
                
                headerFormat = new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory,
                    "Arial", 
                    SharpDX.DirectWrite.FontWeight.Bold,
                    SharpDX.DirectWrite.FontStyle.Normal,
                    (float)(14 * TableScale))
                {
                    TextAlignment = SharpDX.DirectWrite.TextAlignment.Center,
                    WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap
                };
                
                resourcesCreated = true;
            }
            catch (Exception ex)
            {
                Print("Error creating resources: " + ex.Message);
            }
        }
        
        private void RenderDashboard(ChartControl chartControl)
        {
            try
            {
                int totalBuy, totalSell, totalNeutral;
                string signal = GetMarketSignal(out totalBuy, out totalSell, out totalNeutral);
                
                // Calculate dimensions
                float cellWidth = (float)(80 * TableScale);
                float cellHeight = (float)(25 * TableScale);
                float headerHeight = (float)(35 * TableScale);
                float padding = (float)(5 * TableScale);
                
                float tableWidth = cellWidth * 3 + padding * 4;
                float tableHeight = headerHeight + cellHeight * 4 + padding * 5;
                
                // Calculate position
                float x, y;
                switch (HorizontalPosition)
                {
                    case 0: // Left
                        x = TableMargin;
                        break;
                    case 1: // Center
                        x = (chartControl.ActualWidth - tableWidth) / 2;
                        break;
                    default: // Right
                        x = chartControl.ActualWidth - tableWidth - TableMargin;
                        break;
                }
                
                switch (VerticalPosition)
                {
                    case 0: // Top
                        y = TableMargin;
                        break;
                    case 1: // Middle
                        y = (chartControl.ActualHeight - tableHeight) / 2;
                        break;
                    default: // Bottom
                        y = chartControl.ActualHeight - tableHeight - TableMargin;
                        break;
                }
                
                // Draw panel background
                RenderTarget.FillRectangle(
                    new RectangleF(x, y, tableWidth, tableHeight),
                    panelBackgroundBrush);
                
                // Draw header
                SharpDX.Direct2D1.SolidColorBrush headerBrush;
                switch (signal)
                {
                    case "STRONG SELL":
                        headerBrush = strongSellBrush;
                        break;
                    case "SELL":
                        headerBrush = sellBrush;
                        break;
                    case "BUY":
                        headerBrush = buyBrush;
                        break;
                    case "STRONG BUY":
                        headerBrush = strongBuyBrush;
                        break;
                    default:
                        headerBrush = neutralBrush;
                        break;
                }
                
                RenderTarget.FillRectangle(
                    new RectangleF(x + padding, y + padding, tableWidth - padding * 2, headerHeight),
                    headerBrush);
                
                RenderTarget.DrawText(
                    signal,
                    headerFormat,
                    new RectangleF(x + padding, y + padding, tableWidth - padding * 2, headerHeight),
                    textBrush);
                
                // Draw column headers
                float currentY = y + padding + headerHeight + padding;
                string[] headers = { "Category", "Sell", "Buy" };
                for (int i = 0; i < headers.Length; i++)
                {
                    float currentX = x + padding + i * (cellWidth + padding);
                    RenderTarget.FillRectangle(
                        new RectangleF(currentX, currentY, cellWidth, cellHeight),
                        cellBackground1Brush);
                    RenderTarget.DrawText(
                        headers[i],
                        textFormat,
                        new RectangleF(currentX + padding, currentY, cellWidth - padding, cellHeight),
                        textBrush);
                }
                
                // Draw rows
                string[] categories = { "Oscillators", "Moving Avg", "Pivot Points" };
                int[] sellCounts = { oscillatorSell, maSell, pivotSell };
                int[] buyCounts = { oscillatorBuy, maBuy, pivotBuy };
                
                for (int row = 0; row < 3; row++)
                {
                    currentY += cellHeight + padding;
                    SharpDX.Direct2D1.SolidColorBrush rowBrush = (row % 2 == 0) ? cellBackground1Brush : cellBackground2Brush;
                    
                    // Category name
                    RenderTarget.FillRectangle(
                        new RectangleF(x + padding, currentY, cellWidth, cellHeight),
                        rowBrush);
                    RenderTarget.DrawText(
                        categories[row],
                        textFormat,
                        new RectangleF(x + padding * 2, currentY, cellWidth - padding, cellHeight),
                        textBrush);
                    
                    // Sell count
                    RenderTarget.FillRectangle(
                        new RectangleF(x + padding * 2 + cellWidth, currentY, cellWidth, cellHeight),
                        rowBrush);
                    RenderTarget.DrawText(
                        sellCounts[row].ToString(),
                        textFormat,
                        new RectangleF(x + padding * 3 + cellWidth, currentY, cellWidth - padding, cellHeight),
                        textBrush);
                    
                    // Buy count
                    RenderTarget.FillRectangle(
                        new RectangleF(x + padding * 3 + cellWidth * 2, currentY, cellWidth, cellHeight),
                        rowBrush);
                    RenderTarget.DrawText(
                        buyCounts[row].ToString(),
                        textFormat,
                        new RectangleF(x + padding * 4 + cellWidth * 2, currentY, cellWidth - padding, cellHeight),
                        textBrush);
                }
                
                // Draw percentage bar
                currentY += cellHeight + padding;
                int total = totalBuy + totalSell + totalNeutral;
                if (total > 0)
                {
                    float barWidth = tableWidth - padding * 2;
                    float sellWidth = barWidth * totalSell / total;
                    float neutralWidth = barWidth * totalNeutral / total;
                    float buyWidth = barWidth * totalBuy / total;
                    
                    // Sell section
                    if (sellWidth > 0)
                    {
                        RenderTarget.FillRectangle(
                            new RectangleF(x + padding, currentY, sellWidth, cellHeight),
                            sellBrush);
                    }
                    
                    // Neutral section
                    if (neutralWidth > 0)
                    {
                        RenderTarget.FillRectangle(
                            new RectangleF(x + padding + sellWidth, currentY, neutralWidth, cellHeight),
                            neutralBrush);
                    }
                    
                    // Buy section
                    if (buyWidth > 0)
                    {
                        RenderTarget.FillRectangle(
                            new RectangleF(x + padding + sellWidth + neutralWidth, currentY, buyWidth, cellHeight),
                            buyBrush);
                    }
                    
                    // Draw percentages
                    string percentText = string.Format("Sell: {0:F0}% | Neutral: {1:F0}% | Buy: {2:F0}%",
                        (double)totalSell / total * 100,
                        (double)totalNeutral / total * 100,
                        (double)totalBuy / total * 100);
                    
                    RenderTarget.DrawText(
                        percentText,
                        textFormat,
                        new RectangleF(x + padding, currentY, barWidth, cellHeight),
                        textBrush);
                }
            }
            catch (Exception ex)
            {
                Print("Error rendering dashboard: " + ex.Message);
            }
        }
        
        private void DisposeResources()
        {
            try
            {
                if (panelBackgroundBrush != null) { panelBackgroundBrush.Dispose(); panelBackgroundBrush = null; }
                if (cellBackground1Brush != null) { cellBackground1Brush.Dispose(); cellBackground1Brush = null; }
                if (cellBackground2Brush != null) { cellBackground2Brush.Dispose(); cellBackground2Brush = null; }
                if (textBrush != null) { textBrush.Dispose(); textBrush = null; }
                if (strongSellBrush != null) { strongSellBrush.Dispose(); strongSellBrush = null; }
                if (sellBrush != null) { sellBrush.Dispose(); sellBrush = null; }
                if (neutralBrush != null) { neutralBrush.Dispose(); neutralBrush = null; }
                if (buyBrush != null) { buyBrush.Dispose(); buyBrush = null; }
                if (strongBuyBrush != null) { strongBuyBrush.Dispose(); strongBuyBrush = null; }
                if (textFormat != null) { textFormat.Dispose(); textFormat = null; }
                if (headerFormat != null) { headerFormat.Dispose(); headerFormat = null; }
                
                resourcesCreated = false;
            }
            catch (Exception ex)
            {
                Print("Error disposing resources: " + ex.Message);
            }
        }
        
        #endregion
        
        #region Properties
        
        [NinjaScriptProperty]
        [Range(0, 2)]
        [Display(Name="Horizontal Position", Description="0=Left, 1=Center, 2=Right", Order=1, GroupName="Position")]
        public int HorizontalPosition { get; set; }
        
        [NinjaScriptProperty]
        [Range(0, 2)]
        [Display(Name="Vertical Position", Description="0=Top, 1=Middle, 2=Bottom", Order=2, GroupName="Position")]
        public int VerticalPosition { get; set; }
        
        [NinjaScriptProperty]
        [Range(0.5, 3.0)]
        [Display(Name="Table Scale", Description="Size multiplier", Order=3, GroupName="Position")]
        public double TableScale { get; set; }
        
        [NinjaScriptProperty]
        [Range(10, 100)]
        [Display(Name="Table Opacity", Description="Transparency %", Order=4, GroupName="Position")]
        public int TableOpacity { get; set; }
        
        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name="Table Margin", Description="Distance from edge", Order=5, GroupName="Position")]
        public int TableMargin { get; set; }
        
        [XmlIgnore]
        [Display(Name="Strong Sell Color", Description="STRONG SELL header color", Order=1, GroupName="Colors")]
        public Brush StrongSellColor { get; set; }
        
        [Browsable(false)]
        public string StrongSellColorSerializable
        {
            get { return Serialize.BrushToString(StrongSellColor); }
            set { StrongSellColor = Serialize.StringToBrush(value); }
        }
        
        [XmlIgnore]
        [Display(Name="Sell Color", Description="SELL header color", Order=2, GroupName="Colors")]
        public Brush SellColor { get; set; }
        
        [Browsable(false)]
        public string SellColorSerializable
        {
            get { return Serialize.BrushToString(SellColor); }
            set { SellColor = Serialize.StringToBrush(value); }
        }
        
        [XmlIgnore]
        [Display(Name="Neutral Color", Description="NEUTRAL header color", Order=3, GroupName="Colors")]
        public Brush NeutralColor { get; set; }
        
        [Browsable(false)]
        public string NeutralColorSerializable
        {
            get { return Serialize.BrushToString(NeutralColor); }
            set { NeutralColor = Serialize.StringToBrush(value); }
        }
        
        [XmlIgnore]
        [Display(Name="Buy Color", Description="BUY header color", Order=4, GroupName="Colors")]
        public Brush BuyColor { get; set; }
        
        [Browsable(false)]
        public string BuyColorSerializable
        {
            get { return Serialize.BrushToString(BuyColor); }
            set { BuyColor = Serialize.StringToBrush(value); }
        }
        
        [XmlIgnore]
        [Display(Name="Strong Buy Color", Description="STRONG BUY header color", Order=5, GroupName="Colors")]
        public Brush StrongBuyColor { get; set; }
        
        [Browsable(false)]
        public string StrongBuyColorSerializable
        {
            get { return Serialize.BrushToString(StrongBuyColor); }
            set { StrongBuyColor = Serialize.StringToBrush(value); }
        }
        
        [XmlIgnore]
        [Display(Name="Panel Background", Description="Dashboard background color", Order=6, GroupName="Colors")]
        public Brush PanelBackgroundColor { get; set; }
        
        [Browsable(false)]
        public string PanelBackgroundColorSerializable
        {
            get { return Serialize.BrushToString(PanelBackgroundColor); }
            set { PanelBackgroundColor = Serialize.StringToBrush(value); }
        }
        
        [XmlIgnore]
        [Display(Name="Cell Background 1", Description="First alternating cell color", Order=7, GroupName="Colors")]
        public Brush CellBackground1Color { get; set; }
        
        [Browsable(false)]
        public string CellBackground1ColorSerializable
        {
            get { return Serialize.BrushToString(CellBackground1Color); }
            set { CellBackground1Color = Serialize.StringToBrush(value); }
        }
        
        [XmlIgnore]
        [Display(Name="Cell Background 2", Description="Second alternating cell color", Order=8, GroupName="Colors")]
        public Brush CellBackground2Color { get; set; }
        
        [Browsable(false)]
        public string CellBackground2ColorSerializable
        {
            get { return Serialize.BrushToString(CellBackground2Color); }
            set { CellBackground2Color = Serialize.StringToBrush(value); }
        }
        
        #endregion
    }
}
