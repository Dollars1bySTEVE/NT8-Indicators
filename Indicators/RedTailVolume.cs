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

// RedTail Volume Enhanced
// Copyright (C) 2022-2025 RedTail Volume
// Converted to NinjaTrader 8
//
// Created by RedTail Indicators
//
// This indicator contains useful features:
// 1. "Buy/Sell" Volume is separated by utilizing the candle OHLC to determine a proxy ratio.
// 2. "Winning" Volume is always stacked on top giving you a clear visual indication of the candle winner and by how much.
// 3. Current candle volume ratio is shown in a table, with the winner always staying on the left side providing a fast visual indication.
// 4. Optional Moving Average plot to see relative volume.
// 5. Ripster-style volume statistics: 30-day average, 30-bar average, percentages, and unusual volume highlighting.

namespace NinjaTrader.NinjaScript.Indicators
{
    public class RedTailVolume : Indicator
    {
        private double buyVolume;
        private double sellVolume;
        private bool buyersWinning;
        private SMA volumeAverage;
        private double currentBuyPercent;
        private double currentSellPercent;
        
        // Ripster volume statistics
        private VOL dailyVolume;
        private double vol30DayAvg;
        private double todayVolume;
        private double percentOf30Day;
        private double avg30Bars;
        private double percentOf30Bar;
        private double currentBarVolume;
        
        // Daily range statistics
        private double avg30DayRange;
        private double todayRange;
        private double percentOfAvgRange;
        
        // Cached values for performance
        private DateTime lastCalculatedDate;
        private double cachedTodayHigh;
        private double cachedTodayLow;
        private double cachedTodayVolume;
        
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"RedTail Volume - Separates buy/sell volume with visual stacking and Ripster-style volume statistics";
                Name = "RedTailVolume";
                Calculate = Calculate.OnEachTick;
                IsOverlay = false;
                DisplayInDataBox = true;
                DrawOnPricePanel = false;
                DrawHorizontalGridLines = true;
                DrawVerticalGridLines = true;
                PaintPriceMarkers = true;
                ScaleJustification = ScaleJustification.Right;
                IsSuspendedWhileInactive = true;
                
                // Input parameters
                ShowAverage = false;
                AverageLength = 10;
                BuyVolumeColor = Brushes.Green;
                SellVolumeColor = Brushes.Red;
                
                // Ripster volume statistics parameters
                Show30DayAvg = true;
                ShowTodayVolume = true;
                ShowPercentOf30DayAvg = true;
                Show30BarAvg = true;
                ShowCurrentBar = true;
                ShowPercentOf30BarAvg = true;
                ShowBuySellPercent = true;
                UnusualVolumePercent = 200;
                
                // Daily range statistics parameters
                ShowDailyRangePanel = true;
                ShowAvg30DayRange = true;
                ShowTodayRange = true;
                ShowPercentOfAvgRange = true;
                
                // Panel color parameters
                RangePanelTextColor = Brushes.LightGray;
                RangePanelBgColor = Brushes.Transparent;
                RangePanelHighTextColor = Brushes.White;
                RangePanelHighBgColor = Brushes.DarkGreen;
                RangePanelMediumTextColor = Brushes.White;
                RangePanelMediumBgColor = Brushes.DarkOrange;
                
                VolumePanelTextColor = Brushes.LightGray;
                VolumePanelBgColor = Brushes.Black;
                VolumePanelHighTextColor = Brushes.White;
                VolumePanelHighBgColor = Brushes.DarkGreen;
                VolumePanelMediumTextColor = Brushes.White;
                VolumePanelMediumBgColor = Brushes.DarkOrange;
                VolumePanelBuyTextColor = Brushes.LimeGreen;
                VolumePanelSellTextColor = Brushes.Red;
                
                // Add plots with new identifiers
                AddPlot(new Stroke(Brushes.Green, 2), PlotStyle.Bar, "Buy Volume");
                AddPlot(new Stroke(Brushes.Red, 2), PlotStyle.Bar, "Sell Volume");
                AddPlot(new Stroke(Brushes.White, 2), PlotStyle.Line, "Volume Average");
            }
            else if (State == State.Configure)
            {
                // Add daily volume series for 30-day average calculation
                AddDataSeries(BarsPeriodType.Day, 1);
            }
            else if (State == State.DataLoaded)
            {
                volumeAverage = SMA(Volume, AverageLength);
                dailyVolume = VOL(BarsArray[1]);
                lastCalculatedDate = DateTime.MinValue;
                cachedTodayHigh = double.MinValue;
                cachedTodayLow = double.MaxValue;
                cachedTodayVolume = 0;
            }
        }

        protected override void OnBarUpdate()
        {
            // Handle multi-series bar updates
            if (BarsInProgress == 1)
            {
                // Daily bar updated, recalculate daily statistics
                return;
            }
            
            if (CurrentBars[0] < 0 || Volume[0] == 0)
                return;
                
            // Calculate proxy buyer/seller volume using candle OHLC
            double range = High[0] - Low[0];
            
            if (range > 0)
            {
                buyVolume = Volume[0] * (Close[0] - Low[0]) / range;
                sellVolume = Volume[0] * (High[0] - Close[0]) / range;
            }
            else
            {
                // Handle case where High == Low (no range)
                buyVolume = Volume[0] * 0.5;
                sellVolume = Volume[0] * 0.5;
            }
            
            // Determine who's winning
            buyersWinning = buyVolume >= sellVolume;
            
            // Calculate percentages - ensure they always add up to 100%
            double totalCalcVolume = buyVolume + sellVolume;
            if (totalCalcVolume > 0)
            {
                currentBuyPercent = (buyVolume / totalCalcVolume) * 100;
                currentSellPercent = (sellVolume / totalCalcVolume) * 100;
            }
            else
            {
                currentBuyPercent = 50;
                currentSellPercent = 50;
            }
            
            // Set plot values - plot 0 gets the larger volume, plot 1 gets the smaller volume
            // But we assign colors based on buy/sell, not winning/losing
            if (buyersWinning)
            {
                // Buyers winning: buy volume is larger, goes to plot 0
                Values[0][0] = buyVolume; // Larger volume (buy)
                Values[1][0] = sellVolume; // Smaller volume (sell)
            }
            else
            {
                // Sellers winning: sell volume is larger, goes to plot 0
                Values[0][0] = sellVolume; // Larger volume (sell)
                Values[1][0] = buyVolume; // Smaller volume (buy)
            }
            
            // Always color plot 0 and plot 1 based on which volume type they're showing
            if (buyersWinning)
            {
                PlotBrushes[0][0] = BuyVolumeColor; // Plot 0 has buy volume (green)
                PlotBrushes[1][0] = SellVolumeColor; // Plot 1 has sell volume (red)
            }
            else
            {
                PlotBrushes[0][0] = SellVolumeColor; // Plot 0 has sell volume (red)
                PlotBrushes[1][0] = BuyVolumeColor; // Plot 1 has buy volume (green)
            }
            
            // Average volume plot
            if (ShowAverage && CurrentBar >= AverageLength)
            {
                Values[2][0] = volumeAverage[0];
            }
            else
            {
                Values[2][0] = double.NaN;
            }
            
            // Store current bar volume for display
            currentBarVolume = Volume[0];
            
            // Calculate Ripster volume statistics
            CalculateVolumeStatistics();
        }
        
        private void CalculateVolumeStatistics()
        {
            // Only process on primary bars
            if (BarsInProgress != 0)
                return;
            
            DateTime today = Time[0].Date;
            
            // Check if we've moved to a new day - reset cached values
            if (today != lastCalculatedDate)
            {
                lastCalculatedDate = today;
                cachedTodayHigh = High[0];
                cachedTodayLow = Low[0];
                cachedTodayVolume = Volume[0];
            }
            else
            {
                // Same day - update incrementally
                if (High[0] > cachedTodayHigh)
                    cachedTodayHigh = High[0];
                if (Low[0] < cachedTodayLow)
                    cachedTodayLow = Low[0];
                cachedTodayVolume += Volume[0];
            }
            
            // Use cached values
            todayVolume = cachedTodayVolume;
            todayRange = cachedTodayHigh - cachedTodayLow;
                
            // Calculate 30-day averages (only when we have daily bar data)
            if (CurrentBars[1] >= 30 && BarsArray[1] != null)
            {
                double sumVolume = 0;
                double sumRange = 0;
                
                for (int i = 1; i <= 30; i++)
                {
                    if (i < BarsArray[1].Count)
                    {
                        sumVolume += Volumes[1][i];
                        // Calculate range for each daily bar
                        double dailyHigh = Highs[1][i];
                        double dailyLow = Lows[1][i];
                        sumRange += (dailyHigh - dailyLow);
                    }
                }
                
                vol30DayAvg = sumVolume / 30.0;
                avg30DayRange = sumRange / 30.0;
            }
            else if (BarsArray[1] != null && CurrentBars[1] >= 0)
            {
                // If we don't have 30 days yet, calculate with what we have
                double sumVolume = 0;
                double sumRange = 0;
                int count = Math.Min(CurrentBars[1], 30);
                
                for (int i = 1; i <= count; i++)
                {
                    if (i < BarsArray[1].Count)
                    {
                        sumVolume += Volumes[1][i];
                        double dailyHigh = Highs[1][i];
                        double dailyLow = Lows[1][i];
                        sumRange += (dailyHigh - dailyLow);
                    }
                }
                
                if (count > 0)
                {
                    vol30DayAvg = sumVolume / count;
                    avg30DayRange = sumRange / count;
                }
            }
            
            // Calculate percentages
            if (vol30DayAvg > 0 && todayVolume > 0)
            {
                percentOf30Day = Math.Round((todayVolume / vol30DayAvg) * 100, 0);
            }
            
            if (avg30DayRange > 0 && todayRange > 0)
            {
                percentOfAvgRange = Math.Round((todayRange / avg30DayRange) * 100, 0);
            }
            
            // Calculate 30-bar average (using current timeframe)
            if (CurrentBars[0] >= 30)
            {
                double sum = 0;
                for (int i = 1; i <= 30; i++)
                {
                    sum += Volumes[0][i];
                }
                avg30Bars = sum / 30.0;
                
                // Calculate percentage of 30-bar average
                if (avg30Bars > 0)
                {
                    percentOf30Bar = Math.Round((currentBarVolume / avg30Bars) * 100, 0);
                }
            }
        }
        
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            
            if (Bars == null || ChartControl == null || CurrentBar < 0)
                return;
            
            // Render Daily Range Panel (Top-Left)
            if (ShowDailyRangePanel)
            {
                RenderDailyRangePanel();
            }
            
            // Render Volume Statistics Panel (Top-Right)
            RenderVolumeStatsPanel();
        }
        
        private void RenderDailyRangePanel()
        {
            string rangeText = "";
            
            // Determine colors based on range percentage
            Brush rangeTextColor = RangePanelTextColor;
            Brush rangeBgColor = RangePanelBgColor;
            
            // Highlight if today's range is unusual
            if (percentOfAvgRange >= 150)
            {
                rangeBgColor = RangePanelHighBgColor;
                rangeTextColor = RangePanelHighTextColor;
            }
            else if (percentOfAvgRange >= 100)
            {
                rangeBgColor = RangePanelMediumBgColor;
                rangeTextColor = RangePanelMediumTextColor;
            }
            
            // Build the range label text with brackets
            if (ShowAvg30DayRange && avg30DayRange > 0)
            {
                rangeText += $"[Avg 30 Day Range: {avg30DayRange.ToString("F2")}]  ";
            }
            
            if (ShowTodayRange && todayRange > 0)
            {
                rangeText += $"[Today Range: {todayRange.ToString("F2")}]  ";
            }
            
            if (ShowPercentOfAvgRange && percentOfAvgRange > 0)
            {
                rangeText += $"[{percentOfAvgRange}%]";
            }
            
            if (!string.IsNullOrEmpty(rangeText))
            {
                Draw.TextFixed(this, "DailyRangeStats", $" {rangeText} ", TextPosition.TopLeft, 
                    rangeTextColor, new SimpleFont("Arial", 11), 
                    Brushes.Transparent, rangeBgColor, 0);
            }
        }
        
        private void RenderVolumeStatsPanel()
        {
            
            string labelText = "";
            
            // Determine colors based on buy/sell percentages and unusual volume
            Brush textColor = VolumePanelTextColor;
            Brush bgColor = VolumePanelBgColor;
            
            // Highlight unusual volume with different background
            if ((ShowPercentOf30DayAvg && percentOf30Day >= UnusualVolumePercent) ||
                (ShowPercentOf30BarAvg && percentOf30Bar >= UnusualVolumePercent))
            {
                bgColor = VolumePanelHighBgColor;
                textColor = VolumePanelHighTextColor;
            }
            else if ((ShowPercentOf30DayAvg && percentOf30Day >= 100) ||
                     (ShowPercentOf30BarAvg && percentOf30Bar >= 100))
            {
                bgColor = VolumePanelMediumBgColor;
                textColor = VolumePanelMediumTextColor;
            }
            
            // Build the label text with boxed sections
            if (Show30DayAvg && vol30DayAvg > 0)
            {
                labelText += $"[Avg 30 Days: {Math.Round(vol30DayAvg, 0):N0}]  ";
            }
            
            if (ShowTodayVolume && todayVolume > 0)
            {
                labelText += $"[Today: {Math.Round(todayVolume, 0):N0}]  ";
            }
            
            if (ShowPercentOf30DayAvg && percentOf30Day > 0)
            {
                labelText += $"[{percentOf30Day}%]  ";
            }
            
            if (Show30BarAvg && avg30Bars > 0)
            {
                labelText += $"[Avg 30 Bars: {Math.Round(avg30Bars, 0):N0}]  ";
            }
            
            if (ShowCurrentBar)
            {
                labelText += $"[Cur Bar: {Math.Round(currentBarVolume, 0):N0}]  ";
            }
            
            if (ShowPercentOf30BarAvg && percentOf30Bar > 0)
            {
                labelText += $"[{percentOf30Bar}%]  ";
            }
            
            if (ShowBuySellPercent)
            {
                // Color code based on buy/sell percentage for the final determination
                if (currentSellPercent > 51)
                {
                    textColor = VolumePanelSellTextColor;
                }
                else if (currentBuyPercent > 51)
                {
                    textColor = VolumePanelBuyTextColor;
                }
                
                labelText += $"[Buy: {currentBuyPercent:F1}%]  [Sell: {currentSellPercent:F1}%]";
            }
            
            if (!string.IsNullOrEmpty(labelText))
            {
                Draw.TextFixed(this, "VolumeStats", $" {labelText} ", TextPosition.TopRight, 
                    textColor, new SimpleFont("Arial", 11), 
                    Brushes.Transparent, Brushes.Transparent, 0);
            }
        }

        #region Properties
        
        [NinjaScriptProperty]
        [Display(Name = "Show Average", Description = "Show moving average line", Order = 1, GroupName = "Parameters")]
        public bool ShowAverage { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Average Length", Description = "Length for moving average calculation", Order = 2, GroupName = "Parameters")]
        public int AverageLength { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Buy Volume Color", Description = "Color for buy volume bars", Order = 3, GroupName = "Colors")]
        public Brush BuyVolumeColor { get; set; }

        [Browsable(false)]
        public string BuyVolumeColorSerializable
        {
            get { return Serialize.BrushToString(BuyVolumeColor); }
            set { BuyVolumeColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Sell Volume Color", Description = "Color for sell volume bars", Order = 4, GroupName = "Colors")]
        public Brush SellVolumeColor { get; set; }

        [Browsable(false)]
        public string SellVolumeColorSerializable
        {
            get { return Serialize.BrushToString(SellVolumeColor); }
            set { SellVolumeColor = Serialize.StringToBrush(value); }
        }
        
        // Ripster Volume Statistics Properties
        [NinjaScriptProperty]
        [Display(Name = "Show 30-Day Average", Description = "Display 30-day volume average", Order = 1, GroupName = "Ripster Statistics")]
        public bool Show30DayAvg { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Today Volume", Description = "Display today's total volume", Order = 2, GroupName = "Ripster Statistics")]
        public bool ShowTodayVolume { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show % of 30-Day Avg", Description = "Display percentage of 30-day average", Order = 3, GroupName = "Ripster Statistics")]
        public bool ShowPercentOf30DayAvg { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show 30-Bar Average", Description = "Display 30-bar volume average", Order = 4, GroupName = "Ripster Statistics")]
        public bool Show30BarAvg { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Current Bar", Description = "Display current bar volume", Order = 5, GroupName = "Ripster Statistics")]
        public bool ShowCurrentBar { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show % of 30-Bar Avg", Description = "Display percentage of 30-bar average", Order = 6, GroupName = "Ripster Statistics")]
        public bool ShowPercentOf30BarAvg { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Buy/Sell %", Description = "Display current bar buy/sell percentages", Order = 7, GroupName = "Ripster Statistics")]
        public bool ShowBuySellPercent { get; set; }

        [NinjaScriptProperty]
        [Range(100, 500)]
        [Display(Name = "Unusual Volume %", Description = "Threshold for unusual volume highlighting", Order = 8, GroupName = "Ripster Statistics")]
        public int UnusualVolumePercent { get; set; }
        
        // Daily Range Statistics Properties
        [NinjaScriptProperty]
        [Display(Name = "Show Daily Range Panel", Description = "Display daily range statistics panel", Order = 1, GroupName = "Daily Range Statistics")]
        public bool ShowDailyRangePanel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Avg 30-Day Range", Description = "Display 30-day average range", Order = 2, GroupName = "Daily Range Statistics")]
        public bool ShowAvg30DayRange { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Today Range", Description = "Display today's range", Order = 3, GroupName = "Daily Range Statistics")]
        public bool ShowTodayRange { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show % of Avg Range", Description = "Display percentage of average range", Order = 4, GroupName = "Daily Range Statistics")]
        public bool ShowPercentOfAvgRange { get; set; }

        // Daily Range Panel Color Properties
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Normal Text Color", Description = "Text color for normal range", Order = 1, GroupName = "Range Panel Colors")]
        public Brush RangePanelTextColor { get; set; }

        [Browsable(false)]
        public string RangePanelTextColorSerializable
        {
            get { return Serialize.BrushToString(RangePanelTextColor); }
            set { RangePanelTextColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Normal Background", Description = "Background color for normal range", Order = 2, GroupName = "Range Panel Colors")]
        public Brush RangePanelBgColor { get; set; }

        [Browsable(false)]
        public string RangePanelBgColorSerializable
        {
            get { return Serialize.BrushToString(RangePanelBgColor); }
            set { RangePanelBgColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "High Text Color (>=150%)", Description = "Text color when range >= 150% of average", Order = 3, GroupName = "Range Panel Colors")]
        public Brush RangePanelHighTextColor { get; set; }

        [Browsable(false)]
        public string RangePanelHighTextColorSerializable
        {
            get { return Serialize.BrushToString(RangePanelHighTextColor); }
            set { RangePanelHighTextColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "High Background (>=150%)", Description = "Background color when range >= 150% of average", Order = 4, GroupName = "Range Panel Colors")]
        public Brush RangePanelHighBgColor { get; set; }

        [Browsable(false)]
        public string RangePanelHighBgColorSerializable
        {
            get { return Serialize.BrushToString(RangePanelHighBgColor); }
            set { RangePanelHighBgColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Medium Text Color (>=100%)", Description = "Text color when range >= 100% of average", Order = 5, GroupName = "Range Panel Colors")]
        public Brush RangePanelMediumTextColor { get; set; }

        [Browsable(false)]
        public string RangePanelMediumTextColorSerializable
        {
            get { return Serialize.BrushToString(RangePanelMediumTextColor); }
            set { RangePanelMediumTextColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Medium Background (>=100%)", Description = "Background color when range >= 100% of average", Order = 6, GroupName = "Range Panel Colors")]
        public Brush RangePanelMediumBgColor { get; set; }

        [Browsable(false)]
        public string RangePanelMediumBgColorSerializable
        {
            get { return Serialize.BrushToString(RangePanelMediumBgColor); }
            set { RangePanelMediumBgColor = Serialize.StringToBrush(value); }
        }

        // Volume Panel Color Properties
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Normal Text Color", Description = "Text color for normal volume", Order = 1, GroupName = "Volume Panel Colors")]
        public Brush VolumePanelTextColor { get; set; }

        [Browsable(false)]
        public string VolumePanelTextColorSerializable
        {
            get { return Serialize.BrushToString(VolumePanelTextColor); }
            set { VolumePanelTextColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Normal Background", Description = "Background color for normal volume", Order = 2, GroupName = "Volume Panel Colors")]
        public Brush VolumePanelBgColor { get; set; }

        [Browsable(false)]
        public string VolumePanelBgColorSerializable
        {
            get { return Serialize.BrushToString(VolumePanelBgColor); }
            set { VolumePanelBgColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "High Volume Text", Description = "Text color for unusual volume", Order = 3, GroupName = "Volume Panel Colors")]
        public Brush VolumePanelHighTextColor { get; set; }

        [Browsable(false)]
        public string VolumePanelHighTextColorSerializable
        {
            get { return Serialize.BrushToString(VolumePanelHighTextColor); }
            set { VolumePanelHighTextColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "High Volume Background", Description = "Background color for unusual volume", Order = 4, GroupName = "Volume Panel Colors")]
        public Brush VolumePanelHighBgColor { get; set; }

        [Browsable(false)]
        public string VolumePanelHighBgColorSerializable
        {
            get { return Serialize.BrushToString(VolumePanelHighBgColor); }
            set { VolumePanelHighBgColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Medium Volume Text", Description = "Text color for medium volume (>=100%)", Order = 5, GroupName = "Volume Panel Colors")]
        public Brush VolumePanelMediumTextColor { get; set; }

        [Browsable(false)]
        public string VolumePanelMediumTextColorSerializable
        {
            get { return Serialize.BrushToString(VolumePanelMediumTextColor); }
            set { VolumePanelMediumTextColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Medium Volume Background", Description = "Background color for medium volume (>=100%)", Order = 6, GroupName = "Volume Panel Colors")]
        public Brush VolumePanelMediumBgColor { get; set; }

        [Browsable(false)]
        public string VolumePanelMediumBgColorSerializable
        {
            get { return Serialize.BrushToString(VolumePanelMediumBgColor); }
            set { VolumePanelMediumBgColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Buy Winning Text Color", Description = "Text color when buyers are winning (>51%)", Order = 7, GroupName = "Volume Panel Colors")]
        public Brush VolumePanelBuyTextColor { get; set; }

        [Browsable(false)]
        public string VolumePanelBuyTextColorSerializable
        {
            get { return Serialize.BrushToString(VolumePanelBuyTextColor); }
            set { VolumePanelBuyTextColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Sell Winning Text Color", Description = "Text color when sellers are winning (>51%)", Order = 8, GroupName = "Volume Panel Colors")]
        public Brush VolumePanelSellTextColor { get; set; }

        [Browsable(false)]
        public string VolumePanelSellTextColorSerializable
        {
            get { return Serialize.BrushToString(VolumePanelSellTextColor); }
            set { VolumePanelSellTextColor = Serialize.StringToBrush(value); }
        }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> BuyVolume
        {
            get { return Values[0]; }
        }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> SellVolume
        {
            get { return Values[1]; }
        }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> AverageVolume
        {
            get { return Values[2]; }
        }

        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private RedTailVolume[] cacheRedTailVolume;
		public RedTailVolume RedTailVolume(bool showAverage, int averageLength, Brush buyVolumeColor, Brush sellVolumeColor, bool show30DayAvg, bool showTodayVolume, bool showPercentOf30DayAvg, bool show30BarAvg, bool showCurrentBar, bool showPercentOf30BarAvg, bool showBuySellPercent, int unusualVolumePercent, bool showDailyRangePanel, bool showAvg30DayRange, bool showTodayRange, bool showPercentOfAvgRange, Brush rangePanelTextColor, Brush rangePanelBgColor, Brush rangePanelHighTextColor, Brush rangePanelHighBgColor, Brush rangePanelMediumTextColor, Brush rangePanelMediumBgColor, Brush volumePanelTextColor, Brush volumePanelBgColor, Brush volumePanelHighTextColor, Brush volumePanelHighBgColor, Brush volumePanelMediumTextColor, Brush volumePanelMediumBgColor, Brush volumePanelBuyTextColor, Brush volumePanelSellTextColor)
		{
			return RedTailVolume(Input, showAverage, averageLength, buyVolumeColor, sellVolumeColor, show30DayAvg, showTodayVolume, showPercentOf30DayAvg, show30BarAvg, showCurrentBar, showPercentOf30BarAvg, showBuySellPercent, unusualVolumePercent, showDailyRangePanel, showAvg30DayRange, showTodayRange, showPercentOfAvgRange, rangePanelTextColor, rangePanelBgColor, rangePanelHighTextColor, rangePanelHighBgColor, rangePanelMediumTextColor, rangePanelMediumBgColor, volumePanelTextColor, volumePanelBgColor, volumePanelHighTextColor, volumePanelHighBgColor, volumePanelMediumTextColor, volumePanelMediumBgColor, volumePanelBuyTextColor, volumePanelSellTextColor);
		}

		public RedTailVolume RedTailVolume(ISeries<double> input, bool showAverage, int averageLength, Brush buyVolumeColor, Brush sellVolumeColor, bool show30DayAvg, bool showTodayVolume, bool showPercentOf30DayAvg, bool show30BarAvg, bool showCurrentBar, bool showPercentOf30BarAvg, bool showBuySellPercent, int unusualVolumePercent, bool showDailyRangePanel, bool showAvg30DayRange, bool showTodayRange, bool showPercentOfAvgRange, Brush rangePanelTextColor, Brush rangePanelBgColor, Brush rangePanelHighTextColor, Brush rangePanelHighBgColor, Brush rangePanelMediumTextColor, Brush rangePanelMediumBgColor, Brush volumePanelTextColor, Brush volumePanelBgColor, Brush volumePanelHighTextColor, Brush volumePanelHighBgColor, Brush volumePanelMediumTextColor, Brush volumePanelMediumBgColor, Brush volumePanelBuyTextColor, Brush volumePanelSellTextColor)
		{
			if (cacheRedTailVolume != null)
				for (int idx = 0; idx < cacheRedTailVolume.Length; idx++)
					if (cacheRedTailVolume[idx] != null && cacheRedTailVolume[idx].ShowAverage == showAverage && cacheRedTailVolume[idx].AverageLength == averageLength && cacheRedTailVolume[idx].BuyVolumeColor == buyVolumeColor && cacheRedTailVolume[idx].SellVolumeColor == sellVolumeColor && cacheRedTailVolume[idx].Show30DayAvg == show30DayAvg && cacheRedTailVolume[idx].ShowTodayVolume == showTodayVolume && cacheRedTailVolume[idx].ShowPercentOf30DayAvg == showPercentOf30DayAvg && cacheRedTailVolume[idx].Show30BarAvg == show30BarAvg && cacheRedTailVolume[idx].ShowCurrentBar == showCurrentBar && cacheRedTailVolume[idx].ShowPercentOf30BarAvg == showPercentOf30BarAvg && cacheRedTailVolume[idx].ShowBuySellPercent == showBuySellPercent && cacheRedTailVolume[idx].UnusualVolumePercent == unusualVolumePercent && cacheRedTailVolume[idx].ShowDailyRangePanel == showDailyRangePanel && cacheRedTailVolume[idx].ShowAvg30DayRange == showAvg30DayRange && cacheRedTailVolume[idx].ShowTodayRange == showTodayRange && cacheRedTailVolume[idx].ShowPercentOfAvgRange == showPercentOfAvgRange && cacheRedTailVolume[idx].RangePanelTextColor == rangePanelTextColor && cacheRedTailVolume[idx].RangePanelBgColor == rangePanelBgColor && cacheRedTailVolume[idx].RangePanelHighTextColor == rangePanelHighTextColor && cacheRedTailVolume[idx].RangePanelHighBgColor == rangePanelHighBgColor && cacheRedTailVolume[idx].RangePanelMediumTextColor == rangePanelMediumTextColor && cacheRedTailVolume[idx].RangePanelMediumBgColor == rangePanelMediumBgColor && cacheRedTailVolume[idx].VolumePanelTextColor == volumePanelTextColor && cacheRedTailVolume[idx].VolumePanelBgColor == volumePanelBgColor && cacheRedTailVolume[idx].VolumePanelHighTextColor == volumePanelHighTextColor && cacheRedTailVolume[idx].VolumePanelHighBgColor == volumePanelHighBgColor && cacheRedTailVolume[idx].VolumePanelMediumTextColor == volumePanelMediumTextColor && cacheRedTailVolume[idx].VolumePanelMediumBgColor == volumePanelMediumBgColor && cacheRedTailVolume[idx].VolumePanelBuyTextColor == volumePanelBuyTextColor && cacheRedTailVolume[idx].VolumePanelSellTextColor == volumePanelSellTextColor && cacheRedTailVolume[idx].EqualsInput(input))
						return cacheRedTailVolume[idx];
			return CacheIndicator<RedTailVolume>(new RedTailVolume(){ ShowAverage = showAverage, AverageLength = averageLength, BuyVolumeColor = buyVolumeColor, SellVolumeColor = sellVolumeColor, Show30DayAvg = show30DayAvg, ShowTodayVolume = showTodayVolume, ShowPercentOf30DayAvg = showPercentOf30DayAvg, Show30BarAvg = show30BarAvg, ShowCurrentBar = showCurrentBar, ShowPercentOf30BarAvg = showPercentOf30BarAvg, ShowBuySellPercent = showBuySellPercent, UnusualVolumePercent = unusualVolumePercent, ShowDailyRangePanel = showDailyRangePanel, ShowAvg30DayRange = showAvg30DayRange, ShowTodayRange = showTodayRange, ShowPercentOfAvgRange = showPercentOfAvgRange, RangePanelTextColor = rangePanelTextColor, RangePanelBgColor = rangePanelBgColor, RangePanelHighTextColor = rangePanelHighTextColor, RangePanelHighBgColor = rangePanelHighBgColor, RangePanelMediumTextColor = rangePanelMediumTextColor, RangePanelMediumBgColor = rangePanelMediumBgColor, VolumePanelTextColor = volumePanelTextColor, VolumePanelBgColor = volumePanelBgColor, VolumePanelHighTextColor = volumePanelHighTextColor, VolumePanelHighBgColor = volumePanelHighBgColor, VolumePanelMediumTextColor = volumePanelMediumTextColor, VolumePanelMediumBgColor = volumePanelMediumBgColor, VolumePanelBuyTextColor = volumePanelBuyTextColor, VolumePanelSellTextColor = volumePanelSellTextColor }, input, ref cacheRedTailVolume);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.RedTailVolume RedTailVolume(bool showAverage, int averageLength, Brush buyVolumeColor, Brush sellVolumeColor, bool show30DayAvg, bool showTodayVolume, bool showPercentOf30DayAvg, bool show30BarAvg, bool showCurrentBar, bool showPercentOf30BarAvg, bool showBuySellPercent, int unusualVolumePercent, bool showDailyRangePanel, bool showAvg30DayRange, bool showTodayRange, bool showPercentOfAvgRange, Brush rangePanelTextColor, Brush rangePanelBgColor, Brush rangePanelHighTextColor, Brush rangePanelHighBgColor, Brush rangePanelMediumTextColor, Brush rangePanelMediumBgColor, Brush volumePanelTextColor, Brush volumePanelBgColor, Brush volumePanelHighTextColor, Brush volumePanelHighBgColor, Brush volumePanelMediumTextColor, Brush volumePanelMediumBgColor, Brush volumePanelBuyTextColor, Brush volumePanelSellTextColor)
		{
			return indicator.RedTailVolume(Input, showAverage, averageLength, buyVolumeColor, sellVolumeColor, show30DayAvg, showTodayVolume, showPercentOf30DayAvg, show30BarAvg, showCurrentBar, showPercentOf30BarAvg, showBuySellPercent, unusualVolumePercent, showDailyRangePanel, showAvg30DayRange, showTodayRange, showPercentOfAvgRange, rangePanelTextColor, rangePanelBgColor, rangePanelHighTextColor, rangePanelHighBgColor, rangePanelMediumTextColor, rangePanelMediumBgColor, volumePanelTextColor, volumePanelBgColor, volumePanelHighTextColor, volumePanelHighBgColor, volumePanelMediumTextColor, volumePanelMediumBgColor, volumePanelBuyTextColor, volumePanelSellTextColor);
		}

		public Indicators.RedTailVolume RedTailVolume(ISeries<double> input , bool showAverage, int averageLength, Brush buyVolumeColor, Brush sellVolumeColor, bool show30DayAvg, bool showTodayVolume, bool showPercentOf30DayAvg, bool show30BarAvg, bool showCurrentBar, bool showPercentOf30BarAvg, bool showBuySellPercent, int unusualVolumePercent, bool showDailyRangePanel, bool showAvg30DayRange, bool showTodayRange, bool showPercentOfAvgRange, Brush rangePanelTextColor, Brush rangePanelBgColor, Brush rangePanelHighTextColor, Brush rangePanelHighBgColor, Brush rangePanelMediumTextColor, Brush rangePanelMediumBgColor, Brush volumePanelTextColor, Brush volumePanelBgColor, Brush volumePanelHighTextColor, Brush volumePanelHighBgColor, Brush volumePanelMediumTextColor, Brush volumePanelMediumBgColor, Brush volumePanelBuyTextColor, Brush volumePanelSellTextColor)
		{
			return indicator.RedTailVolume(input, showAverage, averageLength, buyVolumeColor, sellVolumeColor, show30DayAvg, showTodayVolume, showPercentOf30DayAvg, show30BarAvg, showCurrentBar, showPercentOf30BarAvg, showBuySellPercent, unusualVolumePercent, showDailyRangePanel, showAvg30DayRange, showTodayRange, showPercentOfAvgRange, rangePanelTextColor, rangePanelBgColor, rangePanelHighTextColor, rangePanelHighBgColor, rangePanelMediumTextColor, rangePanelMediumBgColor, volumePanelTextColor, volumePanelBgColor, volumePanelHighTextColor, volumePanelHighBgColor, volumePanelMediumTextColor, volumePanelMediumBgColor, volumePanelBuyTextColor, volumePanelSellTextColor);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.RedTailVolume RedTailVolume(bool showAverage, int averageLength, Brush buyVolumeColor, Brush sellVolumeColor, bool show30DayAvg, bool showTodayVolume, bool showPercentOf30DayAvg, bool show30BarAvg, bool showCurrentBar, bool showPercentOf30BarAvg, bool showBuySellPercent, int unusualVolumePercent, bool showDailyRangePanel, bool showAvg30DayRange, bool showTodayRange, bool showPercentOfAvgRange, Brush rangePanelTextColor, Brush rangePanelBgColor, Brush rangePanelHighTextColor, Brush rangePanelHighBgColor, Brush rangePanelMediumTextColor, Brush rangePanelMediumBgColor, Brush volumePanelTextColor, Brush volumePanelBgColor, Brush volumePanelHighTextColor, Brush volumePanelHighBgColor, Brush volumePanelMediumTextColor, Brush volumePanelMediumBgColor, Brush volumePanelBuyTextColor, Brush volumePanelSellTextColor)
		{
			return indicator.RedTailVolume(Input, showAverage, averageLength, buyVolumeColor, sellVolumeColor, show30DayAvg, showTodayVolume, showPercentOf30DayAvg, show30BarAvg, showCurrentBar, showPercentOf30BarAvg, showBuySellPercent, unusualVolumePercent, showDailyRangePanel, showAvg30DayRange, showTodayRange, showPercentOfAvgRange, rangePanelTextColor, rangePanelBgColor, rangePanelHighTextColor, rangePanelHighBgColor, rangePanelMediumTextColor, rangePanelMediumBgColor, volumePanelTextColor, volumePanelBgColor, volumePanelHighTextColor, volumePanelHighBgColor, volumePanelMediumTextColor, volumePanelMediumBgColor, volumePanelBuyTextColor, volumePanelSellTextColor);
		}

		public Indicators.RedTailVolume RedTailVolume(ISeries<double> input , bool showAverage, int averageLength, Brush buyVolumeColor, Brush sellVolumeColor, bool show30DayAvg, bool showTodayVolume, bool showPercentOf30DayAvg, bool show30BarAvg, bool showCurrentBar, bool showPercentOf30BarAvg, bool showBuySellPercent, int unusualVolumePercent, bool showDailyRangePanel, bool showAvg30DayRange, bool showTodayRange, bool showPercentOfAvgRange, Brush rangePanelTextColor, Brush rangePanelBgColor, Brush rangePanelHighTextColor, Brush rangePanelHighBgColor, Brush rangePanelMediumTextColor, Brush rangePanelMediumBgColor, Brush volumePanelTextColor, Brush volumePanelBgColor, Brush volumePanelHighTextColor, Brush volumePanelHighBgColor, Brush volumePanelMediumTextColor, Brush volumePanelMediumBgColor, Brush volumePanelBuyTextColor, Brush volumePanelSellTextColor)
		{
			return indicator.RedTailVolume(input, showAverage, averageLength, buyVolumeColor, sellVolumeColor, show30DayAvg, showTodayVolume, showPercentOf30DayAvg, show30BarAvg, showCurrentBar, showPercentOf30BarAvg, showBuySellPercent, unusualVolumePercent, showDailyRangePanel, showAvg30DayRange, showTodayRange, showPercentOfAvgRange, rangePanelTextColor, rangePanelBgColor, rangePanelHighTextColor, rangePanelHighBgColor, rangePanelMediumTextColor, rangePanelMediumBgColor, volumePanelTextColor, volumePanelBgColor, volumePanelHighTextColor, volumePanelHighBgColor, volumePanelMediumTextColor, volumePanelMediumBgColor, volumePanelBuyTextColor, volumePanelSellTextColor);
		}
	}
}

#endregion
