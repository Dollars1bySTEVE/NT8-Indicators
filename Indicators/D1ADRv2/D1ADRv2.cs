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

// NinjaTrader 8 requires custom enums to be declared OUTSIDE all namespaces
// so the auto-generated partial class code can resolve them.
// See: forum.ninjatrader.com threads #1182932, #95909, #1046853
public enum DailyOpenMode
{
    Session,
    Midnight,
    Custom
}

namespace NinjaTrader.NinjaScript.Indicators
{
    public class D1ADRv2 : Indicator
    {
        #region Private Fields
        private List<double> dailyRanges;
        private List<double> weeklyRanges;
        private Dictionary<DateTime, double> dailyOpens;
        private Dictionary<DateTime, double> dailyHighs;
        private Dictionary<DateTime, double> dailyLows;
        private Dictionary<DateTime, double> dailyCloses;
        private Dictionary<DateTime, double> weeklyOpens;
        private Dictionary<DateTime, double> weeklyHighs;
        private Dictionary<DateTime, double> weeklyLows;
        private Dictionary<DateTime, double> weeklyCloses;
        
        private double currentADR;
        private double currentAWR;
        private double todayOpen;
        private double todayHigh;
        private double todayLow;
        private double todayClose;
        private double weekOpen;
        private double weekHigh;
        private double weekLow;
        private double weekClose;
        
        private DateTime lastDailyDate;
        private DateTime lastWeeklyDate;

        // Computed line values used by OnRender
        private double adrHigh;
        private double adrLow;
        private double awrHigh;
        private double awrLow;
        private double dailyPP, dailyR1, dailyR2, dailyR3, dailyS1, dailyS2, dailyS3;
        private double weeklyPP, weeklyR1, weeklyR2, weeklyR3, weeklyS1, weeklyS2, weeklyS3;

        // Session bar data for SharpDX rendering
        private List<int>    asiaBarIndices;
        private List<double> asiaOpenPrices;
        private List<int>    londonBarIndices;
        private List<double> londonOpenPrices;
        private List<int>    nyBarIndices;
        private List<double> nyOpenPrices;

        // SharpDX resources
        private bool dxResourcesCreated;
        private SharpDX.Direct2D1.SolidColorBrush adrHighBrush;
        private SharpDX.Direct2D1.SolidColorBrush adrLowBrush;
        private SharpDX.Direct2D1.SolidColorBrush adrFillBrush;
        private SharpDX.Direct2D1.SolidColorBrush awrHighBrush;
        private SharpDX.Direct2D1.SolidColorBrush awrLowBrush;
        private SharpDX.Direct2D1.SolidColorBrush awrFillBrush;
        private SharpDX.Direct2D1.SolidColorBrush dailyOpenBrush;
        private SharpDX.Direct2D1.SolidColorBrush pivotBrush;
        private SharpDX.Direct2D1.SolidColorBrush r1Brush;
        private SharpDX.Direct2D1.SolidColorBrush r2Brush;
        private SharpDX.Direct2D1.SolidColorBrush r3Brush;
        private SharpDX.Direct2D1.SolidColorBrush s1Brush;
        private SharpDX.Direct2D1.SolidColorBrush s2Brush;
        private SharpDX.Direct2D1.SolidColorBrush s3Brush;
        private SharpDX.Direct2D1.SolidColorBrush asiaSessionBrush;
        private SharpDX.Direct2D1.SolidColorBrush londonSessionBrush;
        private SharpDX.Direct2D1.SolidColorBrush nySessionBrush;
        private SharpDX.Direct2D1.SolidColorBrush labelBrush;
        private SharpDX.Direct2D1.SolidColorBrush infoBrush;
        private SharpDX.Direct2D1.StrokeStyle adrStrokeStyle;
        private SharpDX.Direct2D1.StrokeStyle awrStrokeStyle;
        private SharpDX.Direct2D1.StrokeStyle dailyOpenStrokeStyle;
        private SharpDX.Direct2D1.StrokeStyle pivotStrokeStyle;
        private SharpDX.Direct2D1.StrokeStyle sessionStrokeStyle;
        private SharpDX.Direct2D1.StrokeStyle midPivotStrokeStyle;
        private SharpDX.DirectWrite.TextFormat labelTextFormat;
        private SharpDX.DirectWrite.TextFormat textFormat;

        private double yesterdayHigh;
        private double yesterdayLow;
        private double yesterdayClose;
        private double lastWeekHigh;
        private double lastWeekLow;
        private double lastWeekClose;
        #endregion

        #region State Management
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"Daily Average Range (ADR) v2 - Enhanced with chart scaling fixes + Weekly Average Range (AWR) + Pivot Points + Session Opens";
                Name = "D1ADRv2";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                IsAutoScale = false;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = true;

                // ADR Settings
                ADRPeriod = 14;
                ADRMultiplier = 1.0;
                ShowADR = true;
                ADRHighColor = Brushes.DodgerBlue;
                ADRLowColor = Brushes.OrangeRed;
                ADRLineWidth = 2;
                ADRLineStyle = DashStyleHelper.Dash;
                ShowADRFill = true;
                ADRFillOpacity = 10;

                // AWR Settings
                AWRPeriod = 4;
                AWRMultiplier = 1.0;
                ShowAWR = true;
                AWRHighColor = Brushes.Cyan;
                AWRLowColor = Brushes.Magenta;
                AWRLineWidth = 2;
                AWRLineStyle = DashStyleHelper.Dash;
                ShowAWRFill = true;
                AWRFillOpacity = 8;

                // Daily Open Settings
                ShowDailyOpen = true;
                DailyOpenColor = Brushes.Yellow;
                DailyOpenWidth = 2;
                DailyOpenStyle = DashStyleHelper.Dot;
                DailyOpenModeType = DailyOpenMode.Session;
                CustomOpenHour = 9;
                CustomOpenMinute = 30;

                // Session Opens
                ShowAsiaSession = true;
                AsiaOpenHour = 0;
                AsiaOpenMinute = 0;
                AsiaSessionColor = Brushes.Pink;
                ShowAsiaHorizontalLine = true;
                
                ShowLondonSession = true;
                LondonOpenHour = 8;
                LondonOpenMinute = 0;
                LondonSessionColor = Brushes.Beige;
                ShowLondonHorizontalLine = true;
                
                ShowNYSession = true;
                NYOpenHour = 13;
                NYOpenMinute = 30;
                NYSessionColor = Brushes.LightGreen;
                ShowNYHorizontalLine = true;
                
                ShowSessionVerticalLines = true;
                SessionLineWidth = 1;
                SessionLineStyle = DashStyleHelper.Dot;

                // Pivot Points
                ShowDailyPivots = true;
                ShowWeeklyPivots = false;
                ShowR3S3 = true;
                ShowMidPivots = false;
                PivotColor = Brushes.Gray;
                R1Color = Brushes.Green;
                R2Color = Brushes.LimeGreen;
                R3Color = Brushes.Lime;
                S1Color = Brushes.Red;
                S2Color = Brushes.OrangeRed;
                S3Color = Brushes.DarkRed;
                PivotLineWidth = 1;
                PivotLineStyle = DashStyleHelper.Dash;

                // Labels & Info
                ShowLabels = true;
                LabelColor = Brushes.White;
                LabelFontSize = 10;
                ShowInfoBox = true;
                InfoBoxPosition = TextPosition.TopLeft;
            }
            else if (State == State.Configure)
            {
            }
            else if (State == State.DataLoaded)
            {
                dailyRanges = new List<double>();
                weeklyRanges = new List<double>();
                dailyOpens = new Dictionary<DateTime, double>();
                dailyHighs = new Dictionary<DateTime, double>();
                dailyLows = new Dictionary<DateTime, double>();
                dailyCloses = new Dictionary<DateTime, double>();
                weeklyOpens = new Dictionary<DateTime, double>();
                weeklyHighs = new Dictionary<DateTime, double>();
                weeklyLows = new Dictionary<DateTime, double>();
                weeklyCloses = new Dictionary<DateTime, double>();
                
                lastDailyDate = DateTime.MinValue;
                lastWeeklyDate = DateTime.MinValue;

                asiaBarIndices   = new List<int>();
                asiaOpenPrices   = new List<double>();
                londonBarIndices = new List<int>();
                londonOpenPrices = new List<double>();
                nyBarIndices     = new List<int>();
                nyOpenPrices     = new List<double>();
            }
            else if (State == State.Terminated)
            {
                DisposeResources();
            }
        }
        #endregion

        #region Bar Update Logic
        protected override void OnBarUpdate()
        {
            if (CurrentBar < 1)
                return;

            DateTime currentDate = Time[0].Date;
            
            // Track daily data
            if (currentDate != lastDailyDate)
            {
                if (lastDailyDate != DateTime.MinValue)
                {
                    FinalizeDailyData();
                }
                
                todayOpen = Open[0];
                todayHigh = High[0];
                todayLow = Low[0];
                todayClose = Close[0];
                lastDailyDate = currentDate;
            }
            else
            {
                todayHigh = Math.Max(todayHigh, High[0]);
                todayLow = Math.Min(todayLow, Low[0]);
                todayClose = Close[0];
            }

            // Track weekly data
            DateTime weekStart = GetWeekStart(currentDate);
            if (weekStart != lastWeeklyDate)
            {
                if (lastWeeklyDate != DateTime.MinValue)
                {
                    FinalizeWeeklyData();
                }
                
                weekOpen = Open[0];
                weekHigh = High[0];
                weekLow = Low[0];
                weekClose = Close[0];
                lastWeeklyDate = weekStart;
            }
            else
            {
                weekHigh = Math.Max(weekHigh, High[0]);
                weekLow = Math.Min(weekLow, Low[0]);
                weekClose = Close[0];
            }

            // Calculate ADR and AWR
            CalculateADR();
            CalculateAWR();
            
            // Compute ADR lines (stored for OnRender)
            ComputeADRLines();
            
            // Compute AWR lines (stored for OnRender)
            ComputeAWRLines();
            
            // Compute Pivot Points (stored for OnRender)
            ComputePivotPoints();
            
            // Record session bar indices for vertical/horizontal line rendering
            RecordSessionBars();
        }
        #endregion

        #region Calculation Methods
        private void FinalizeDailyData()
        {
            double range = todayHigh - todayLow;
            dailyRanges.Add(range);
            
            dailyOpens[lastDailyDate] = todayOpen;
            dailyHighs[lastDailyDate] = todayHigh;
            dailyLows[lastDailyDate] = todayLow;
            dailyCloses[lastDailyDate] = todayClose;
            
            yesterdayHigh = todayHigh;
            yesterdayLow = todayLow;
            yesterdayClose = todayClose;
            
            if (dailyRanges.Count > ADRPeriod * 2)
                dailyRanges.RemoveAt(0);
        }

        private void FinalizeWeeklyData()
        {
            double range = weekHigh - weekLow;
            weeklyRanges.Add(range);
            
            weeklyOpens[lastWeeklyDate] = weekOpen;
            weeklyHighs[lastWeeklyDate] = weekHigh;
            weeklyLows[lastWeeklyDate] = weekLow;
            weeklyCloses[lastWeeklyDate] = weekClose;
            
            lastWeekHigh = weekHigh;
            lastWeekLow = weekLow;
            lastWeekClose = weekClose;
            
            if (weeklyRanges.Count > AWRPeriod * 2)
                weeklyRanges.RemoveAt(0);
        }

        private void CalculateADR()
        {
            if (dailyRanges.Count < Math.Min(ADRPeriod, 5))
            {
                currentADR = 0;
                return;
            }

            int count = Math.Min(ADRPeriod, dailyRanges.Count);
            double sum = 0;
            for (int i = dailyRanges.Count - count; i < dailyRanges.Count; i++)
            {
                sum += dailyRanges[i];
            }
            
            currentADR = (sum / count) * ADRMultiplier;
        }

        private void CalculateAWR()
        {
            if (weeklyRanges.Count < Math.Min(AWRPeriod, 2))
            {
                currentAWR = 0;
                return;
            }

            int count = Math.Min(AWRPeriod, weeklyRanges.Count);
            double sum = 0;
            for (int i = weeklyRanges.Count - count; i < weeklyRanges.Count; i++)
            {
                sum += weeklyRanges[i];
            }
            
            currentAWR = (sum / count) * AWRMultiplier;
        }

        private DateTime GetWeekStart(DateTime date)
        {
            int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
            return date.AddDays(-diff).Date;
        }

        private void ComputeADRLines()
        {
            if (!ShowADR || currentADR <= 0)
            {
                adrHigh = 0;
                adrLow  = 0;
                return;
            }
            adrHigh = todayOpen + (currentADR / 2.0);
            adrLow  = todayOpen - (currentADR / 2.0);
        }

        private void ComputeAWRLines()
        {
            if (!ShowAWR || currentAWR <= 0)
            {
                awrHigh = 0;
                awrLow  = 0;
                return;
            }
            awrHigh = weekOpen + (currentAWR / 2.0);
            awrLow  = weekOpen - (currentAWR / 2.0);
        }

        private void ComputePivotPoints()
        {
            if (ShowDailyPivots && yesterdayClose > 0)
                ComputeDailyPivots();

            if (ShowWeeklyPivots && lastWeekClose > 0)
                ComputeWeeklyPivots();
        }

        private void ComputeDailyPivots()
        {
            dailyPP = CalculatePivot(yesterdayHigh, yesterdayLow, yesterdayClose);
            dailyR1 = CalculateR1(dailyPP, yesterdayLow);
            dailyS1 = CalculateS1(dailyPP, yesterdayHigh);
            dailyR2 = CalculateR2(dailyPP, yesterdayHigh, yesterdayLow);
            dailyS2 = CalculateS2(dailyPP, yesterdayHigh, yesterdayLow);
            dailyR3 = CalculateR3(dailyPP, yesterdayHigh, yesterdayLow);
            dailyS3 = CalculateS3(dailyPP, yesterdayHigh, yesterdayLow);
        }

        private void ComputeWeeklyPivots()
        {
            weeklyPP = CalculatePivot(lastWeekHigh, lastWeekLow, lastWeekClose);
            weeklyR1 = CalculateR1(weeklyPP, lastWeekLow);
            weeklyS1 = CalculateS1(weeklyPP, lastWeekHigh);
            weeklyR2 = CalculateR2(weeklyPP, lastWeekHigh, lastWeekLow);
            weeklyS2 = CalculateS2(weeklyPP, lastWeekHigh, lastWeekLow);
            weeklyR3 = CalculateR3(weeklyPP, lastWeekHigh, lastWeekLow);
            weeklyS3 = CalculateS3(weeklyPP, lastWeekHigh, lastWeekLow);
        }

        private void RecordSessionBars()
        {
            DateTime currentTime = Time[0];

            if (ShowAsiaSession && IsSessionOpen(currentTime, AsiaOpenHour, AsiaOpenMinute))
            {
                asiaBarIndices.Add(CurrentBar);
                asiaOpenPrices.Add(Open[0]);
            }

            if (ShowLondonSession && IsSessionOpen(currentTime, LondonOpenHour, LondonOpenMinute))
            {
                londonBarIndices.Add(CurrentBar);
                londonOpenPrices.Add(Open[0]);
            }

            if (ShowNYSession && IsSessionOpen(currentTime, NYOpenHour, NYOpenMinute))
            {
                nyBarIndices.Add(CurrentBar);
                nyOpenPrices.Add(Open[0]);
            }
        }

        private bool IsSessionOpen(DateTime time, int hour, int minute)
        {
            return time.Hour == hour && time.Minute == minute;
        }

        private double CalculatePivot(double high, double low, double close)
        {
            return (high + low + close) / 3.0;
        }

        private double CalculateR1(double pivot, double low)
        {
            return (2 * pivot) - low;
        }

        private double CalculateS1(double pivot, double high)
        {
            return (2 * pivot) - high;
        }

        private double CalculateR2(double pivot, double high, double low)
        {
            return pivot + (high - low);
        }

        private double CalculateS2(double pivot, double high, double low)
        {
            return pivot - (high - low);
        }

        private double CalculateR3(double pivot, double high, double low)
        {
            return high + 2 * (pivot - low);
        }

        private double CalculateS3(double pivot, double high, double low)
        {
            return low - 2 * (high - pivot);
        }
        #endregion

        #region SharpDX Rendering
        private void CreateResources(SharpDX.Direct2D1.RenderTarget rt)
        {
            if (dxResourcesCreated)
                return;

            // Line brushes
            adrHighBrush     = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(ADRHighColor,        1.0f));
            adrLowBrush      = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(ADRLowColor,         1.0f));
            adrFillBrush     = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(ADRHighColor,        OpacityToAlpha(ADRFillOpacity)));
            awrHighBrush     = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(AWRHighColor,        1.0f));
            awrLowBrush      = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(AWRLowColor,         1.0f));
            awrFillBrush     = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(AWRHighColor,        OpacityToAlpha(AWRFillOpacity)));
            dailyOpenBrush   = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(DailyOpenColor,      1.0f));
            pivotBrush       = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(PivotColor,          1.0f));
            r1Brush          = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(R1Color,             1.0f));
            r2Brush          = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(R2Color,             1.0f));
            r3Brush          = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(R3Color,             1.0f));
            s1Brush          = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(S1Color,             1.0f));
            s2Brush          = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(S2Color,             1.0f));
            s3Brush          = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(S3Color,             1.0f));
            asiaSessionBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(AsiaSessionColor,    1.0f));
            londonSessionBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(LondonSessionColor, 1.0f));
            nySessionBrush   = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(NYSessionColor,      1.0f));
            labelBrush       = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(LabelColor,          1.0f));
            infoBrush        = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(LabelColor,          0.9f));

            // Stroke styles
            adrStrokeStyle      = CreateStrokeStyle(rt, ADRLineStyle);
            awrStrokeStyle      = CreateStrokeStyle(rt, AWRLineStyle);
            dailyOpenStrokeStyle = CreateStrokeStyle(rt, DailyOpenStyle);
            pivotStrokeStyle    = CreateStrokeStyle(rt, PivotLineStyle);
            sessionStrokeStyle  = CreateStrokeStyle(rt, SessionLineStyle);
            midPivotStrokeStyle = CreateStrokeStyle(rt, DashStyleHelper.Dot);

            // Text formats
            labelTextFormat = new SharpDX.DirectWrite.TextFormat(
                NinjaTrader.Core.Globals.DirectWriteFactory,
                "Arial",
                SharpDX.DirectWrite.FontWeight.Normal,
                SharpDX.DirectWrite.FontStyle.Normal,
                SharpDX.DirectWrite.FontStretch.Normal,
                (float)LabelFontSize);

            textFormat = new SharpDX.DirectWrite.TextFormat(
                NinjaTrader.Core.Globals.DirectWriteFactory,
                "Arial",
                SharpDX.DirectWrite.FontWeight.Normal,
                SharpDX.DirectWrite.FontStyle.Normal,
                SharpDX.DirectWrite.FontStretch.Normal,
                12);

            dxResourcesCreated = true;
        }

        private void DisposeResources()
        {
            void SafeDispose<T>(ref T obj) where T : class, IDisposable
            {
                if (obj != null) { obj.Dispose(); obj = null; }
            }

            SafeDispose(ref adrHighBrush);
            SafeDispose(ref adrLowBrush);
            SafeDispose(ref adrFillBrush);
            SafeDispose(ref awrHighBrush);
            SafeDispose(ref awrLowBrush);
            SafeDispose(ref awrFillBrush);
            SafeDispose(ref dailyOpenBrush);
            SafeDispose(ref pivotBrush);
            SafeDispose(ref r1Brush);
            SafeDispose(ref r2Brush);
            SafeDispose(ref r3Brush);
            SafeDispose(ref s1Brush);
            SafeDispose(ref s2Brush);
            SafeDispose(ref s3Brush);
            SafeDispose(ref asiaSessionBrush);
            SafeDispose(ref londonSessionBrush);
            SafeDispose(ref nySessionBrush);
            SafeDispose(ref labelBrush);
            SafeDispose(ref infoBrush);
            SafeDispose(ref adrStrokeStyle);
            SafeDispose(ref awrStrokeStyle);
            SafeDispose(ref dailyOpenStrokeStyle);
            SafeDispose(ref pivotStrokeStyle);
            SafeDispose(ref sessionStrokeStyle);
            SafeDispose(ref midPivotStrokeStyle);
            SafeDispose(ref labelTextFormat);
            SafeDispose(ref textFormat);
            dxResourcesCreated = false;
        }

        // Convert WPF brush to SharpDX Color4
        private SharpDX.Color4 ToColor4(Brush wpfBrush, float alpha)
        {
            if (wpfBrush is SolidColorBrush scb)
            {
                Color c = scb.Color;
                return new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, alpha);
            }
            return new SharpDX.Color4(1f, 1f, 1f, alpha);
        }

        private float OpacityToAlpha(int opacityPercent) => opacityPercent / 100f;

        // Create a stroke style for the given NinjaTrader dash style (null = solid)
        private SharpDX.Direct2D1.StrokeStyle CreateStrokeStyle(SharpDX.Direct2D1.RenderTarget rt, DashStyleHelper dashStyle)
        {
            if (dashStyle == DashStyleHelper.Solid)
                return null;

            var props = new SharpDX.Direct2D1.StrokeStyleProperties();
            switch (dashStyle)
            {
                case DashStyleHelper.Dash:       props.DashStyle = SharpDX.Direct2D1.DashStyle.Dash;       break;
                case DashStyleHelper.Dot:        props.DashStyle = SharpDX.Direct2D1.DashStyle.Dot;        break;
                case DashStyleHelper.DashDot:    props.DashStyle = SharpDX.Direct2D1.DashStyle.DashDot;    break;
                case DashStyleHelper.DashDotDot: props.DashStyle = SharpDX.Direct2D1.DashStyle.DashDotDot; break;
                default: return null;
            }
            return new SharpDX.Direct2D1.StrokeStyle(rt.Factory, props);
        }

        // Draw a full-width horizontal line at the given price
        private void RenderHLine(ChartControl cc, ChartScale cs,
                                 double price,
                                 SharpDX.Direct2D1.SolidColorBrush brush,
                                 float width,
                                 SharpDX.Direct2D1.StrokeStyle strokeStyle)
        {
            float y      = cs.GetYByValue(price);
            float xStart = cc.GetXByBarIndex(ChartBars, ChartBars.FromIndex);
            float xEnd   = cc.GetXByBarIndex(ChartBars, ChartBars.ToIndex);
            var   p1     = new SharpDX.Vector2(xStart, y);
            var   p2     = new SharpDX.Vector2(xEnd,   y);
            if (strokeStyle != null)
                RenderTarget.DrawLine(p1, p2, brush, width, strokeStyle);
            else
                RenderTarget.DrawLine(p1, p2, brush, width);
        }

        // Draw a vertical line at a specific bar index
        private void RenderVLine(ChartControl cc, int barIndex,
                                 SharpDX.Direct2D1.SolidColorBrush brush,
                                 float width,
                                 SharpDX.Direct2D1.StrokeStyle strokeStyle)
        {
            if (barIndex < ChartBars.FromIndex || barIndex > ChartBars.ToIndex)
                return;
            float x       = cc.GetXByBarIndex(ChartBars, barIndex);
            float yTop    = ChartPanel.Y;
            float yBottom = ChartPanel.Y + ChartPanel.H;
            var   p1      = new SharpDX.Vector2(x, yTop);
            var   p2      = new SharpDX.Vector2(x, yBottom);
            if (strokeStyle != null)
                RenderTarget.DrawLine(p1, p2, brush, width, strokeStyle);
            else
                RenderTarget.DrawLine(p1, p2, brush, width);
        }

        // Draw a text label near the right edge of the chart at the given price
        private void RenderLabel(ChartControl cc, ChartScale cs,
                                 double price, string text,
                                 SharpDX.Direct2D1.SolidColorBrush brush)
        {
            float xEnd = cc.GetXByBarIndex(ChartBars, ChartBars.ToIndex);
            float y    = cs.GetYByValue(price) - labelTextFormat.FontSize - 2;
            using (var layout = new SharpDX.DirectWrite.TextLayout(
                NinjaTrader.Core.Globals.DirectWriteFactory,
                text, labelTextFormat, 200, labelTextFormat.FontSize + 4))
            {
                RenderTarget.DrawTextLayout(new SharpDX.Vector2(xEnd - layout.Metrics.Width - 4, y), layout, brush);
            }
        }

        // Draw a text label at a specific bar x-position (for session labels)
        private void RenderBarLabel(ChartControl cc, ChartScale cs,
                                    int barIndex, double price, string text,
                                    SharpDX.Direct2D1.SolidColorBrush brush)
        {
            if (barIndex < ChartBars.FromIndex || barIndex > ChartBars.ToIndex)
                return;
            float x = cc.GetXByBarIndex(ChartBars, barIndex) + 2;
            float y = cs.GetYByValue(price) - labelTextFormat.FontSize - 2;
            using (var layout = new SharpDX.DirectWrite.TextLayout(
                NinjaTrader.Core.Globals.DirectWriteFactory,
                text, labelTextFormat, 150, labelTextFormat.FontSize + 4))
            {
                RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, y), layout, brush);
            }
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (RenderTarget == null || ChartControl == null)
                return;

            if (!dxResourcesCreated)
                CreateResources(RenderTarget);

            float xStart = chartControl.GetXByBarIndex(ChartBars, ChartBars.FromIndex);
            float xEnd   = chartControl.GetXByBarIndex(ChartBars, ChartBars.ToIndex);

            // ── ADR fill + lines ──────────────────────────────────────────────
            if (ShowADR && adrHigh > 0)
            {
                if (ShowADRFill)
                {
                    float yTop    = chartScale.GetYByValue(adrHigh);
                    float yBottom = chartScale.GetYByValue(adrLow);
                    RenderTarget.FillRectangle(
                        new SharpDX.RectangleF(xStart, yTop, xEnd - xStart, yBottom - yTop),
                        adrFillBrush);
                }
                RenderHLine(chartControl, chartScale, adrHigh, adrHighBrush, ADRLineWidth, adrStrokeStyle);
                RenderHLine(chartControl, chartScale, adrLow,  adrLowBrush,  ADRLineWidth, adrStrokeStyle);
                if (ShowLabels)
                {
                    RenderLabel(chartControl, chartScale, adrHigh, "ADR High", adrHighBrush);
                    RenderLabel(chartControl, chartScale, adrLow,  "ADR Low",  adrLowBrush);
                }
            }

            // ── AWR fill + lines ──────────────────────────────────────────────
            if (ShowAWR && awrHigh > 0)
            {
                if (ShowAWRFill)
                {
                    float yTop    = chartScale.GetYByValue(awrHigh);
                    float yBottom = chartScale.GetYByValue(awrLow);
                    RenderTarget.FillRectangle(
                        new SharpDX.RectangleF(xStart, yTop, xEnd - xStart, yBottom - yTop),
                        awrFillBrush);
                }
                RenderHLine(chartControl, chartScale, awrHigh, awrHighBrush, AWRLineWidth, awrStrokeStyle);
                RenderHLine(chartControl, chartScale, awrLow,  awrLowBrush,  AWRLineWidth, awrStrokeStyle);
                if (ShowLabels)
                {
                    RenderLabel(chartControl, chartScale, awrHigh, "AWR High", awrHighBrush);
                    RenderLabel(chartControl, chartScale, awrLow,  "AWR Low",  awrLowBrush);
                }
            }

            // ── Daily Open ────────────────────────────────────────────────────
            if (ShowDailyOpen && todayOpen > 0)
            {
                RenderHLine(chartControl, chartScale, todayOpen, dailyOpenBrush, DailyOpenWidth, dailyOpenStrokeStyle);
                if (ShowLabels)
                    RenderLabel(chartControl, chartScale, todayOpen, "Daily Open", dailyOpenBrush);
            }

            // ── Daily Pivots ──────────────────────────────────────────────────
            if (ShowDailyPivots && yesterdayClose > 0)
            {
                float pw = PivotLineWidth;
                RenderHLine(chartControl, chartScale, dailyPP, pivotBrush, pw, pivotStrokeStyle);
                RenderHLine(chartControl, chartScale, dailyR1, r1Brush,    pw, pivotStrokeStyle);
                RenderHLine(chartControl, chartScale, dailyS1, s1Brush,    pw, pivotStrokeStyle);
                RenderHLine(chartControl, chartScale, dailyR2, r2Brush,    pw, pivotStrokeStyle);
                RenderHLine(chartControl, chartScale, dailyS2, s2Brush,    pw, pivotStrokeStyle);
                if (ShowR3S3)
                {
                    RenderHLine(chartControl, chartScale, dailyR3, r3Brush, pw, pivotStrokeStyle);
                    RenderHLine(chartControl, chartScale, dailyS3, s3Brush, pw, pivotStrokeStyle);
                }
                if (ShowLabels)
                {
                    RenderLabel(chartControl, chartScale, dailyPP, "PP",  pivotBrush);
                    RenderLabel(chartControl, chartScale, dailyR1, "R1",  r1Brush);
                    RenderLabel(chartControl, chartScale, dailyS1, "S1",  s1Brush);
                    RenderLabel(chartControl, chartScale, dailyR2, "R2",  r2Brush);
                    RenderLabel(chartControl, chartScale, dailyS2, "S2",  s2Brush);
                    if (ShowR3S3)
                    {
                        RenderLabel(chartControl, chartScale, dailyR3, "R3", r3Brush);
                        RenderLabel(chartControl, chartScale, dailyS3, "S3", s3Brush);
                    }
                }
                if (ShowMidPivots)
                {
                    RenderHLine(chartControl, chartScale, (dailyPP + dailyR1) / 2.0, pivotBrush, 1, midPivotStrokeStyle);
                    RenderHLine(chartControl, chartScale, (dailyR1 + dailyR2) / 2.0, r1Brush,    1, midPivotStrokeStyle);
                    RenderHLine(chartControl, chartScale, (dailyR2 + dailyR3) / 2.0, r2Brush,    1, midPivotStrokeStyle);
                    RenderHLine(chartControl, chartScale, (dailyPP + dailyS1) / 2.0, pivotBrush, 1, midPivotStrokeStyle);
                    RenderHLine(chartControl, chartScale, (dailyS1 + dailyS2) / 2.0, s1Brush,    1, midPivotStrokeStyle);
                    RenderHLine(chartControl, chartScale, (dailyS2 + dailyS3) / 2.0, s2Brush,    1, midPivotStrokeStyle);
                }
            }

            // ── Weekly Pivots ─────────────────────────────────────────────────
            if (ShowWeeklyPivots && lastWeekClose > 0)
            {
                float pw = PivotLineWidth;
                RenderHLine(chartControl, chartScale, weeklyPP, pivotBrush, pw, pivotStrokeStyle);
                RenderHLine(chartControl, chartScale, weeklyR1, r1Brush,    pw, pivotStrokeStyle);
                RenderHLine(chartControl, chartScale, weeklyS1, s1Brush,    pw, pivotStrokeStyle);
                RenderHLine(chartControl, chartScale, weeklyR2, r2Brush,    pw, pivotStrokeStyle);
                RenderHLine(chartControl, chartScale, weeklyS2, s2Brush,    pw, pivotStrokeStyle);
                if (ShowR3S3)
                {
                    RenderHLine(chartControl, chartScale, weeklyR3, r3Brush, pw, pivotStrokeStyle);
                    RenderHLine(chartControl, chartScale, weeklyS3, s3Brush, pw, pivotStrokeStyle);
                }
                if (ShowLabels)
                {
                    RenderLabel(chartControl, chartScale, weeklyPP, "W-PP", pivotBrush);
                    RenderLabel(chartControl, chartScale, weeklyR1, "W-R1", r1Brush);
                    RenderLabel(chartControl, chartScale, weeklyS1, "W-S1", s1Brush);
                    RenderLabel(chartControl, chartScale, weeklyR2, "W-R2", r2Brush);
                    RenderLabel(chartControl, chartScale, weeklyS2, "W-S2", s2Brush);
                    if (ShowR3S3)
                    {
                        RenderLabel(chartControl, chartScale, weeklyR3, "W-R3", r3Brush);
                        RenderLabel(chartControl, chartScale, weeklyS3, "W-S3", s3Brush);
                    }
                }
                if (ShowMidPivots)
                {
                    RenderHLine(chartControl, chartScale, (weeklyPP + weeklyR1) / 2.0, pivotBrush, 1, midPivotStrokeStyle);
                    RenderHLine(chartControl, chartScale, (weeklyR1 + weeklyR2) / 2.0, r1Brush,    1, midPivotStrokeStyle);
                    RenderHLine(chartControl, chartScale, (weeklyR2 + weeklyR3) / 2.0, r2Brush,    1, midPivotStrokeStyle);
                    RenderHLine(chartControl, chartScale, (weeklyPP + weeklyS1) / 2.0, pivotBrush, 1, midPivotStrokeStyle);
                    RenderHLine(chartControl, chartScale, (weeklyS1 + weeklyS2) / 2.0, s1Brush,    1, midPivotStrokeStyle);
                    RenderHLine(chartControl, chartScale, (weeklyS2 + weeklyS3) / 2.0, s2Brush,    1, midPivotStrokeStyle);
                }
            }

            // ── Session markers ───────────────────────────────────────────────
            for (int i = 0; i < asiaBarIndices.Count; i++)
            {
                int    bi    = asiaBarIndices[i];
                double price = asiaOpenPrices[i];
                if (ShowSessionVerticalLines)
                    RenderVLine(chartControl, bi, asiaSessionBrush, SessionLineWidth, sessionStrokeStyle);
                if (ShowAsiaHorizontalLine)
                    RenderHLine(chartControl, chartScale, price, asiaSessionBrush, SessionLineWidth, sessionStrokeStyle);
                if (ShowLabels)
                    RenderBarLabel(chartControl, chartScale, bi, price, "Asia", labelBrush);
            }

            for (int i = 0; i < londonBarIndices.Count; i++)
            {
                int    bi    = londonBarIndices[i];
                double price = londonOpenPrices[i];
                if (ShowSessionVerticalLines)
                    RenderVLine(chartControl, bi, londonSessionBrush, SessionLineWidth, sessionStrokeStyle);
                if (ShowLondonHorizontalLine)
                    RenderHLine(chartControl, chartScale, price, londonSessionBrush, SessionLineWidth, sessionStrokeStyle);
                if (ShowLabels)
                    RenderBarLabel(chartControl, chartScale, bi, price, "London", labelBrush);
            }

            for (int i = 0; i < nyBarIndices.Count; i++)
            {
                int    bi    = nyBarIndices[i];
                double price = nyOpenPrices[i];
                if (ShowSessionVerticalLines)
                    RenderVLine(chartControl, bi, nySessionBrush, SessionLineWidth, sessionStrokeStyle);
                if (ShowNYHorizontalLine)
                    RenderHLine(chartControl, chartScale, price, nySessionBrush, SessionLineWidth, sessionStrokeStyle);
                if (ShowLabels)
                    RenderBarLabel(chartControl, chartScale, bi, price, "NY", labelBrush);
            }

            // ── Info box ──────────────────────────────────────────────────────
            if (ShowInfoBox && currentADR > 0)
                DrawInfoBox(chartControl, chartScale);
        }

        private void DrawInfoBox(ChartControl chartControl, ChartScale chartScale)
        {
            double currentRange = todayHigh - todayLow;
            double adrPct       = currentADR > 0 ? (currentRange / currentADR) * 100 : 0;
            double weekRange    = weekHigh - weekLow;
            double awrPct       = currentAWR > 0 ? (weekRange / currentAWR) * 100 : 0;

            string infoText = string.Format(
                "ADR ({0}d): {1:F2}\nDay Range: {2:F2} ({3:F1}% used)\n" +
                "AWR ({4}w): {5:F2}\nWeek Range: {6:F2} ({7:F1}% used)",
                ADRPeriod, currentADR, currentRange, adrPct,
                AWRPeriod, currentAWR, weekRange, awrPct);

            float x = 10;
            float y = 30;
            if (InfoBoxPosition == TextPosition.TopRight)
                x = (float)chartControl.ActualWidth - 210;

            using (var layout = new SharpDX.DirectWrite.TextLayout(
                NinjaTrader.Core.Globals.DirectWriteFactory,
                infoText, textFormat, 200, 100))
            {
                var bgRect  = new SharpDX.RectangleF(x - 5, y - 5, 210, 110);
                var bgBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                    new SharpDX.Color4(0f, 0f, 0f, 0.7f));
                RenderTarget.FillRectangle(bgRect, bgBrush);
                bgBrush.Dispose();

                RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, y), layout, infoBrush);
            }
        }

        public override void OnRenderTargetChanged()
        {
            DisposeResources();
        }
        #endregion

        #region User Properties
        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "ADR Period (Days)", Order = 1, GroupName = "1. ADR Settings")]
        public int ADRPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 5.0)]
        [Display(Name = "ADR Multiplier", Order = 2, GroupName = "1. ADR Settings")]
        public double ADRMultiplier { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show ADR Lines", Order = 3, GroupName = "1. ADR Settings")]
        public bool ShowADR { get; set; }

        [XmlIgnore]
        [Display(Name = "ADR High Color", Order = 4, GroupName = "1. ADR Settings")]
        public Brush ADRHighColor { get; set; }

        [Browsable(false)]
        public string ADRHighColorSerializable
        {
            get { return Serialize.BrushToString(ADRHighColor); }
            set { ADRHighColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "ADR Low Color", Order = 5, GroupName = "1. ADR Settings")]
        public Brush ADRLowColor { get; set; }

        [Browsable(false)]
        public string ADRLowColorSerializable
        {
            get { return Serialize.BrushToString(ADRLowColor); }
            set { ADRLowColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "ADR Line Width", Order = 6, GroupName = "1. ADR Settings")]
        public int ADRLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ADR Line Style", Order = 7, GroupName = "1. ADR Settings")]
        public DashStyleHelper ADRLineStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show ADR Fill", Order = 8, GroupName = "1. ADR Settings")]
        public bool ShowADRFill { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "ADR Fill Opacity %", Order = 9, GroupName = "1. ADR Settings")]
        public int ADRFillOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 52)]
        [Display(Name = "AWR Period (Weeks)", Order = 1, GroupName = "2. AWR Settings")]
        public int AWRPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 5.0)]
        [Display(Name = "AWR Multiplier", Order = 2, GroupName = "2. AWR Settings")]
        public double AWRMultiplier { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show AWR Lines", Order = 3, GroupName = "2. AWR Settings")]
        public bool ShowAWR { get; set; }

        [XmlIgnore]
        [Display(Name = "AWR High Color", Order = 4, GroupName = "2. AWR Settings")]
        public Brush AWRHighColor { get; set; }

        [Browsable(false)]
        public string AWRHighColorSerializable
        {
            get { return Serialize.BrushToString(AWRHighColor); }
            set { AWRHighColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "AWR Low Color", Order = 5, GroupName = "2. AWR Settings")]
        public Brush AWRLowColor { get; set; }

        [Browsable(false)]
        public string AWRLowColorSerializable
        {
            get { return Serialize.BrushToString(AWRLowColor); }
            set { AWRLowColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "AWR Line Width", Order = 6, GroupName = "2. AWR Settings")]
        public int AWRLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AWR Line Style", Order = 7, GroupName = "2. AWR Settings")]
        public DashStyleHelper AWRLineStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show AWR Fill", Order = 8, GroupName = "2. AWR Settings")]
        public bool ShowAWRFill { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "AWR Fill Opacity %", Order = 9, GroupName = "2. AWR Settings")]
        public int AWRFillOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Daily Open", Order = 1, GroupName = "3. Daily Open")]
        public bool ShowDailyOpen { get; set; }

        [XmlIgnore]
        [Display(Name = "Daily Open Color", Order = 2, GroupName = "3. Daily Open")]
        public Brush DailyOpenColor { get; set; }

        [Browsable(false)]
        public string DailyOpenColorSerializable
        {
            get { return Serialize.BrushToString(DailyOpenColor); }
            set { DailyOpenColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Daily Open Width", Order = 3, GroupName = "3. Daily Open")]
        public int DailyOpenWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Daily Open Style", Order = 4, GroupName = "3. Daily Open")]
        public DashStyleHelper DailyOpenStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Daily Open Mode", Order = 5, GroupName = "3. Daily Open")]
        public DailyOpenMode DailyOpenModeType { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Custom Open Hour", Order = 6, GroupName = "3. Daily Open")]
        public int CustomOpenHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Custom Open Minute", Order = 7, GroupName = "3. Daily Open")]
        public int CustomOpenMinute { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Asia Session", Order = 1, GroupName = "4. Session Opens")]
        public bool ShowAsiaSession { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Asia Open Hour (UTC)", Order = 2, GroupName = "4. Session Opens")]
        public int AsiaOpenHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Asia Open Minute", Order = 3, GroupName = "4. Session Opens")]
        public int AsiaOpenMinute { get; set; }

        [XmlIgnore]
        [Display(Name = "Asia Session Color", Order = 4, GroupName = "4. Session Opens")]
        public Brush AsiaSessionColor { get; set; }

        [Browsable(false)]
        public string AsiaSessionColorSerializable
        {
            get { return Serialize.BrushToString(AsiaSessionColor); }
            set { AsiaSessionColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Show Asia Horizontal Line", Order = 5, GroupName = "4. Session Opens")]
        public bool ShowAsiaHorizontalLine { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show London Session", Order = 6, GroupName = "4. Session Opens")]
        public bool ShowLondonSession { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "London Open Hour (UTC)", Order = 7, GroupName = "4. Session Opens")]
        public int LondonOpenHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "London Open Minute", Order = 8, GroupName = "4. Session Opens")]
        public int LondonOpenMinute { get; set; }

        [XmlIgnore]
        [Display(Name = "London Session Color", Order = 9, GroupName = "4. Session Opens")]
        public Brush LondonSessionColor { get; set; }

        [Browsable(false)]
        public string LondonSessionColorSerializable
        {
            get { return Serialize.BrushToString(LondonSessionColor); }
            set { LondonSessionColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Show London Horizontal Line", Order = 10, GroupName = "4. Session Opens")]
        public bool ShowLondonHorizontalLine { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show NY Session", Order = 11, GroupName = "4. Session Opens")]
        public bool ShowNYSession { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "NY Open Hour (UTC)", Order = 12, GroupName = "4. Session Opens")]
        public int NYOpenHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "NY Open Minute", Order = 13, GroupName = "4. Session Opens")]
        public int NYOpenMinute { get; set; }

        [XmlIgnore]
        [Display(Name = "NY Session Color", Order = 14, GroupName = "4. Session Opens")]
        public Brush NYSessionColor { get; set; }

        [Browsable(false)]
        public string NYSessionColorSerializable
        {
            get { return Serialize.BrushToString(NYSessionColor); }
            set { NYSessionColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Show NY Horizontal Line", Order = 15, GroupName = "4. Session Opens")]
        public bool ShowNYHorizontalLine { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Session Vertical Lines", Order = 16, GroupName = "4. Session Opens")]
        public bool ShowSessionVerticalLines { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Session Line Width", Order = 17, GroupName = "4. Session Opens")]
        public int SessionLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session Line Style", Order = 18, GroupName = "4. Session Opens")]
        public DashStyleHelper SessionLineStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Daily Pivots", Order = 1, GroupName = "5. Pivot Points")]
        public bool ShowDailyPivots { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Weekly Pivots", Order = 2, GroupName = "5. Pivot Points")]
        public bool ShowWeeklyPivots { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show R3/S3", Order = 3, GroupName = "5. Pivot Points")]
        public bool ShowR3S3 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Mid Pivots", Order = 4, GroupName = "5. Pivot Points")]
        public bool ShowMidPivots { get; set; }

        [XmlIgnore]
        [Display(Name = "Pivot Point Color", Order = 5, GroupName = "5. Pivot Points")]
        public Brush PivotColor { get; set; }

        [Browsable(false)]
        public string PivotColorSerializable
        {
            get { return Serialize.BrushToString(PivotColor); }
            set { PivotColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "R1 Color", Order = 6, GroupName = "5. Pivot Points")]
        public Brush R1Color { get; set; }

        [Browsable(false)]
        public string R1ColorSerializable
        {
            get { return Serialize.BrushToString(R1Color); }
            set { R1Color = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "R2 Color", Order = 7, GroupName = "5. Pivot Points")]
        public Brush R2Color { get; set; }

        [Browsable(false)]
        public string R2ColorSerializable
        {
            get { return Serialize.BrushToString(R2Color); }
            set { R2Color = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "R3 Color", Order = 8, GroupName = "5. Pivot Points")]
        public Brush R3Color { get; set; }

        [Browsable(false)]
        public string R3ColorSerializable
        {
            get { return Serialize.BrushToString(R3Color); }
            set { R3Color = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "S1 Color", Order = 9, GroupName = "5. Pivot Points")]
        public Brush S1Color { get; set; }

        [Browsable(false)]
        public string S1ColorSerializable
        {
            get { return Serialize.BrushToString(S1Color); }
            set { S1Color = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "S2 Color", Order = 10, GroupName = "5. Pivot Points")]
        public Brush S2Color { get; set; }

        [Browsable(false)]
        public string S2ColorSerializable
        {
            get { return Serialize.BrushToString(S2Color); }
            set { S2Color = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "S3 Color", Order = 11, GroupName = "5. Pivot Points")]
        public Brush S3Color { get; set; }

        [Browsable(false)]
        public string S3ColorSerializable
        {
            get { return Serialize.BrushToString(S3Color); }
            set { S3Color = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(1, 3)]
        [Display(Name = "Pivot Line Width", Order = 12, GroupName = "5. Pivot Points")]
        public int PivotLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Pivot Line Style", Order = 13, GroupName = "5. Pivot Points")]
        public DashStyleHelper PivotLineStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Labels", Order = 1, GroupName = "6. Labels & Info")]
        public bool ShowLabels { get; set; }

        [XmlIgnore]
        [Display(Name = "Label Color", Order = 2, GroupName = "6. Labels & Info")]
        public Brush LabelColor { get; set; }

        [Browsable(false)]
        public string LabelColorSerializable
        {
            get { return Serialize.BrushToString(LabelColor); }
            set { LabelColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(8, 16)]
        [Display(Name = "Label Font Size", Order = 3, GroupName = "6. Labels & Info")]
        public int LabelFontSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Info Box", Order = 4, GroupName = "6. Labels & Info")]
        public bool ShowInfoBox { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Info Box Position", Order = 5, GroupName = "6. Labels & Info")]
        public TextPosition InfoBoxPosition { get; set; }
        #endregion
    }
}
