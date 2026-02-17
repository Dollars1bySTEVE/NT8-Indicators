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
    public class D1ADR : Indicator
    {
        #region Enums
        public enum DailyOpenMode
        {
            Session,
            Midnight,
            Custom
        }
        #endregion

        #region Private Fields
        private List<double> dailyRanges;
        private List<double> weeklyRanges;
        private Dictionary<DateTime, double> dailyOpens;
        private Dictionary<DateTime, double> dailyHighs;
        private Dictionary<DateTime, double> dailyLows;
        private Dictionary<DateTime, double> weeklyOpens;
        private Dictionary<DateTime, double> weeklyHighs;
        private Dictionary<DateTime, double> weeklyLows;
        
        private double currentADR;
        private double currentAWR;
        private double todayOpen;
        private double todayHigh;
        private double todayLow;
        private double weekOpen;
        private double weekHigh;
        private double weekLow;
        
        private DateTime lastDailyDate;
        private DateTime lastWeeklyDate;
        private DateTime asiaSessionTime;
        private DateTime londonSessionTime;
        private DateTime nySessionTime;
        
        private bool resourcesCreated;
        private SharpDX.Direct2D1.SolidColorBrush adrFillBrush;
        private SharpDX.Direct2D1.SolidColorBrush awrFillBrush;
        private SharpDX.Direct2D1.SolidColorBrush infoBrush;
        private SharpDX.DirectWrite.TextFormat textFormat;
        private SharpDX.DirectWrite.TextFormat labelFormat;
        #endregion

        #region State Management
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"Daily Average Range (ADR) + Weekly Average Range (AWR) + Pivot Points + Session Opens";
                Name = "D1ADR";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = true;

                // ADR Settings
                ADRPeriod = 14;
                ADRMultiplier = 1.0;
                ShowADR = true;
                ADRColor = Brushes.DodgerBlue;
                ADRLineWidth = 2;
                ADRLineStyle = DashStyleHelper.Solid;
                ShowADRFill = true;
                ADRFillOpacity = 10;

                // AWR Settings
                AWRPeriod = 4;
                AWRMultiplier = 1.0;
                ShowAWR = true;
                AWRColor = Brushes.Purple;
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
                AsiaOpenHour = 19;
                AsiaOpenMinute = 0;
                AsiaSessionColor = Brushes.Pink;
                
                ShowLondonSession = true;
                LondonOpenHour = 3;
                LondonOpenMinute = 0;
                LondonSessionColor = Brushes.Beige;
                
                ShowNYSession = true;
                NYOpenHour = 9;
                NYOpenMinute = 30;
                NYSessionColor = Brushes.LightGreen;
                
                SessionLineWidth = 1;
                SessionLineStyle = DashStyleHelper.Dot;

                // Pivot Points
                ShowDailyPivots = true;
                ShowWeeklyPivots = false;
                ShowMidPivots = false;
                PivotColor = Brushes.Gray;
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
                weeklyOpens = new Dictionary<DateTime, double>();
                weeklyHighs = new Dictionary<DateTime, double>();
                weeklyLows = new Dictionary<DateTime, double>();
                
                lastDailyDate = DateTime.MinValue;
                lastWeeklyDate = DateTime.MinValue;
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
                lastDailyDate = currentDate;
            }
            else
            {
                todayHigh = Math.Max(todayHigh, High[0]);
                todayLow = Math.Min(todayLow, Low[0]);
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
                lastWeeklyDate = weekStart;
            }
            else
            {
                weekHigh = Math.Max(weekHigh, High[0]);
                weekLow = Math.Min(weekLow, Low[0]);
            }

            // Calculate ADR and AWR
            CalculateADR();
            CalculateAWR();
            
            // Draw session markers
            DrawSessionMarkers();
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

        private void DrawSessionMarkers()
        {
            DateTime currentTime = Time[0];
            
            if (ShowAsiaSession && IsSessionOpen(currentTime, AsiaOpenHour, AsiaOpenMinute))
            {
                string tag = "AsiaSession_" + currentTime.ToString("yyyyMMddHHmm");
                Draw.VerticalLine(this, tag, 0, AsiaSessionColor, SessionLineStyle, SessionLineWidth);
                if (ShowLabels)
                {
                    Draw.Text(this, tag + "Label", "Asia", 0, High[0] + TickSize * 5, LabelColor);
                }
            }
            
            if (ShowLondonSession && IsSessionOpen(currentTime, LondonOpenHour, LondonOpenMinute))
            {
                string tag = "LondonSession_" + currentTime.ToString("yyyyMMddHHmm");
                Draw.VerticalLine(this, tag, 0, LondonSessionColor, SessionLineStyle, SessionLineWidth);
                if (ShowLabels)
                {
                    Draw.Text(this, tag + "Label", "London", 0, High[0] + TickSize * 5, LabelColor);
                }
            }
            
            if (ShowNYSession && IsSessionOpen(currentTime, NYOpenHour, NYOpenMinute))
            {
                string tag = "NYSession_" + currentTime.ToString("yyyyMMddHHmm");
                Draw.VerticalLine(this, tag, 0, NYSessionColor, SessionLineStyle, SessionLineWidth);
                if (ShowLabels)
                {
                    Draw.Text(this, tag + "Label", "NY", 0, High[0] + TickSize * 5, LabelColor);
                }
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
        private void CreateResources(SharpDX.Direct2D1.RenderTarget renderTarget)
        {
            if (resourcesCreated)
                return;

            adrFillBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, 
                ConvertColor(ADRColor, ADRFillOpacity / 100f));
            awrFillBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, 
                ConvertColor(AWRColor, AWRFillOpacity / 100f));
            infoBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, 
                ConvertColor(LabelColor, 0.8f));

            textFormat = new SharpDX.DirectWrite.TextFormat(
                NinjaTrader.Core.Globals.DirectWriteFactory,
                "Arial",
                SharpDX.DirectWrite.FontWeight.Normal,
                SharpDX.DirectWrite.FontStyle.Normal,
                SharpDX.DirectWrite.FontStretch.Normal,
                12);

            labelFormat = new SharpDX.DirectWrite.TextFormat(
                NinjaTrader.Core.Globals.DirectWriteFactory,
                "Arial",
                SharpDX.DirectWrite.FontWeight.Normal,
                SharpDX.DirectWrite.FontStyle.Normal,
                SharpDX.DirectWrite.FontStretch.Normal,
                LabelFontSize);

            resourcesCreated = true;
        }

        private void DisposeResources()
        {
            if (adrFillBrush != null) { adrFillBrush.Dispose(); adrFillBrush = null; }
            if (awrFillBrush != null) { awrFillBrush.Dispose(); awrFillBrush = null; }
            if (infoBrush != null) { infoBrush.Dispose(); infoBrush = null; }
            if (textFormat != null) { textFormat.Dispose(); textFormat = null; }
            if (labelFormat != null) { labelFormat.Dispose(); labelFormat = null; }
            resourcesCreated = false;
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

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (RenderTarget == null || ChartControl == null)
                return;

            if (!resourcesCreated)
                CreateResources(RenderTarget);

            // Draw ADR fill zones
            if (ShowADR && ShowADRFill && currentADR > 0)
            {
                float yTop = chartScale.GetYByValue(todayOpen + currentADR / 2);
                float yBottom = chartScale.GetYByValue(todayOpen - currentADR / 2);
                float xStart = chartControl.GetXByBarIndex(ChartBars, ChartBars.FromIndex);
                float xEnd = chartControl.GetXByBarIndex(ChartBars, ChartBars.ToIndex);
                
                SharpDX.RectangleF rect = new SharpDX.RectangleF(xStart, yTop, xEnd - xStart, yBottom - yTop);
                RenderTarget.FillRectangle(rect, adrFillBrush);
            }

            // Draw AWR fill zones
            if (ShowAWR && ShowAWRFill && currentAWR > 0)
            {
                float yTop = chartScale.GetYByValue(weekOpen + currentAWR / 2);
                float yBottom = chartScale.GetYByValue(weekOpen - currentAWR / 2);
                float xStart = chartControl.GetXByBarIndex(ChartBars, ChartBars.FromIndex);
                float xEnd = chartControl.GetXByBarIndex(ChartBars, ChartBars.ToIndex);
                
                SharpDX.RectangleF rect = new SharpDX.RectangleF(xStart, yTop, xEnd - xStart, yBottom - yTop);
                RenderTarget.FillRectangle(rect, awrFillBrush);
            }

            // Draw info box
            if (ShowInfoBox && currentADR > 0)
            {
                DrawInfoBox(chartControl, chartScale);
            }
        }

        private void DrawInfoBox(ChartControl chartControl, ChartScale chartScale)
        {
            double currentRange = todayHigh - todayLow;
            double adrPct = currentADR > 0 ? (currentRange / currentADR) * 100 : 0;
            double awrPct = currentAWR > 0 ? (currentRange / currentAWR) * 100 : 0;

            string infoText = string.Format(
                "ADR ({0}d): {1:F2}\nCurrent: {2:F2} ({3:F1}%)\n" +
                "AWR ({4}w): {5:F2} ({6:F1}%)",
                ADRPeriod, currentADR, currentRange, adrPct,
                AWRPeriod, currentAWR, awrPct);

            SharpDX.DirectWrite.TextLayout textLayout = new SharpDX.DirectWrite.TextLayout(
                NinjaTrader.Core.Globals.DirectWriteFactory,
                infoText,
                textFormat,
                200,
                100);

            float x = 10;
            float y = 30;
            
            if (InfoBoxPosition == TextPosition.TopRight)
                x = (float)chartControl.ActualWidth - 210;

            SharpDX.RectangleF bgRect = new SharpDX.RectangleF(x - 5, y - 5, 200, 80);
            var bgBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, 
                new SharpDX.Color4(0f, 0f, 0f, 0.7f));
            RenderTarget.FillRectangle(bgRect, bgBrush);
            bgBrush.Dispose();

            RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, y), textLayout, infoBrush);
            textLayout.Dispose();
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
        [Display(Name = "Show ADR", Order = 3, GroupName = "1. ADR Settings")]
        public bool ShowADR { get; set; }

        [XmlIgnore]
        [Display(Name = "ADR Color", Order = 4, GroupName = "1. ADR Settings")]
        public Brush ADRColor { get; set; }

        [Browsable(false)]
        public string ADRColorSerializable
        {
            get { return Serialize.BrushToString(ADRColor); }
            set { ADRColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "ADR Line Width", Order = 5, GroupName = "1. ADR Settings")]
        public int ADRLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ADR Line Style", Order = 6, GroupName = "1. ADR Settings")]
        public DashStyleHelper ADRLineStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show ADR Fill", Order = 7, GroupName = "1. ADR Settings")]
        public bool ShowADRFill { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "ADR Fill Opacity %", Order = 8, GroupName = "1. ADR Settings")]
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
        [Display(Name = "Show AWR", Order = 3, GroupName = "2. AWR Settings")]
        public bool ShowAWR { get; set; }

        [XmlIgnore]
        [Display(Name = "AWR Color", Order = 4, GroupName = "2. AWR Settings")]
        public Brush AWRColor { get; set; }

        [Browsable(false)]
        public string AWRColorSerializable
        {
            get { return Serialize.BrushToString(AWRColor); }
            set { AWRColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "AWR Line Width", Order = 5, GroupName = "2. AWR Settings")]
        public int AWRLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AWR Line Style", Order = 6, GroupName = "2. AWR Settings")]
        public DashStyleHelper AWRLineStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show AWR Fill", Order = 7, GroupName = "2. AWR Settings")]
        public bool ShowAWRFill { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "AWR Fill Opacity %", Order = 8, GroupName = "2. AWR Settings")]
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
        [Display(Name = "Show London Session", Order = 5, GroupName = "4. Session Opens")]
        public bool ShowLondonSession { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "London Open Hour (UTC)", Order = 6, GroupName = "4. Session Opens")]
        public int LondonOpenHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "London Open Minute", Order = 7, GroupName = "4. Session Opens")]
        public int LondonOpenMinute { get; set; }

        [XmlIgnore]
        [Display(Name = "London Session Color", Order = 8, GroupName = "4. Session Opens")]
        public Brush LondonSessionColor { get; set; }

        [Browsable(false)]
        public string LondonSessionColorSerializable
        {
            get { return Serialize.BrushToString(LondonSessionColor); }
            set { LondonSessionColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Show NY Session", Order = 9, GroupName = "4. Session Opens")]
        public bool ShowNYSession { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "NY Open Hour (UTC)", Order = 10, GroupName = "4. Session Opens")]
        public int NYOpenHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "NY Open Minute", Order = 11, GroupName = "4. Session Opens")]
        public int NYOpenMinute { get; set; }

        [XmlIgnore]
        [Display(Name = "NY Session Color", Order = 12, GroupName = "4. Session Opens")]
        public Brush NYSessionColor { get; set; }

        [Browsable(false)]
        public string NYSessionColorSerializable
        {
            get { return Serialize.BrushToString(NYSessionColor); }
            set { NYSessionColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Session Line Width", Order = 13, GroupName = "4. Session Opens")]
        public int SessionLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session Line Style", Order = 14, GroupName = "4. Session Opens")]
        public DashStyleHelper SessionLineStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Daily Pivots", Order = 1, GroupName = "5. Pivot Points")]
        public bool ShowDailyPivots { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Weekly Pivots", Order = 2, GroupName = "5. Pivot Points")]
        public bool ShowWeeklyPivots { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Mid Pivots", Order = 3, GroupName = "5. Pivot Points")]
        public bool ShowMidPivots { get; set; }

        [XmlIgnore]
        [Display(Name = "Pivot Color", Order = 4, GroupName = "5. Pivot Points")]
        public Brush PivotColor { get; set; }

        [Browsable(false)]
        public string PivotColorSerializable
        {
            get { return Serialize.BrushToString(PivotColor); }
            set { PivotColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(1, 3)]
        [Display(Name = "Pivot Line Width", Order = 5, GroupName = "5. Pivot Points")]
        public int PivotLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Pivot Line Style", Order = 6, GroupName = "5. Pivot Points")]
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