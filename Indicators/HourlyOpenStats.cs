#region Using declarations
using NinjaTrader.Gui.Tools;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class HourlyOpenStats : Indicator
    {
        #region Private Classes
        private class HourData
        {
            public int      Hour;
            public double   OpenPrice;
            public double   High;
            public double   Low;
            public double   ClosePrice;
            public double   TotalVolume;
            public double   UpVolume;
            public double   DownVolume;
            public int      BarCount;
            public int      StartBar;
            public int      EndBar;
            public DateTime StartTime;
            public bool     IsComplete;
        }

        private class HourHistoricalStats
        {
            public double   AvgRange;
            public double   LargestRange;
            public double   SmallestRange;
            public double   AvgVolume;
            public int      SampleCount;
        }
        #endregion

        #region Private Fields
        private Dictionary<string, HourData>        activeHours;
        private Dictionary<int, List<double>>       historicalRanges;
        private Dictionary<int, List<double>>       historicalVolumes;
        private HourData                            currentHourData;
        private int                                 lastHour = -1;
        private string                              lastHourKey = "";

        // SharpDX GPU rendering resources (cached, created once per render target)
        private SharpDX.Direct2D1.SolidColorBrush   dxHourOpenBrush;
        private SharpDX.Direct2D1.SolidColorBrush   dxBullishBoxBrush;
        private SharpDX.Direct2D1.SolidColorBrush   dxBearishBoxBrush;
        private SharpDX.Direct2D1.SolidColorBrush   dxLabelBrush;
        private SharpDX.Direct2D1.SolidColorBrush   dxPanelBgBrush;
        private SharpDX.Direct2D1.StrokeStyle        dxOpenLineStrokeStyle;
        private SharpDX.DirectWrite.TextFormat       dxTextFormat;
        private bool                                 dxResourcesCreated;
        #endregion

        #region Properties — Display
        [NinjaScriptProperty]
        [Display(Name = "Show Hour Open Lines", Order = 1, GroupName = "1. Display")]
        public bool ShowHourOpenLines { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Hour Range Boxes", Order = 2, GroupName = "1. Display")]
        public bool ShowHourRangeBoxes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Hour Labels", Order = 3, GroupName = "1. Display")]
        public bool ShowHourLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Stats Panel", Order = 4, GroupName = "1. Display")]
        public bool ShowStatsPanel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Skew Data", Order = 5, GroupName = "1. Display")]
        public bool ShowSkewData { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Pct Distributed", Order = 6, GroupName = "1. Display")]
        public bool ShowPctDistributed { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Labels: Current Hour Only", Order = 7, GroupName = "1. Display")]
        public bool LabelsCurrentOnly { get; set; }

        [NinjaScriptProperty]
        [Range(1, 24)]
        [Display(Name = "Labels: Max Hours to Show", Order = 8, GroupName = "1. Display")]
        public int MaxLabelHours { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stats Panel Position", Order = 9, GroupName = "1. Display")]
        public TextPosition StatsPanelPosition { get; set; }
        #endregion

        #region Properties — Lookback
        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Historical Lookback Days", Order = 1, GroupName = "2. Analysis")]
        public int LookbackDays { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Start Hour (24hr)", Order = 2, GroupName = "2. Analysis")]
        public int StartHour { get; set; }

        [NinjaScriptProperty]
        [Range(1, 24)]
        [Display(Name = "End Hour (24hr)", Order = 3, GroupName = "2. Analysis")]
        public int EndHour { get; set; }
        #endregion

        #region Properties — Appearance
        [XmlIgnore]
        [Display(Name = "Hour Open Line Color", Order = 1, GroupName = "3. Appearance")]
        public Brush HourOpenBrush { get; set; }
        [Browsable(false)]
        public string HourOpenBrushSerialize
        {
            get { return Serialize.BrushToString(HourOpenBrush); }
            set { HourOpenBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Bullish Box Color", Order = 2, GroupName = "3. Appearance")]
        public Brush BullishBoxBrush { get; set; }
        [Browsable(false)]
        public string BullishBoxBrushSerialize
        {
            get { return Serialize.BrushToString(BullishBoxBrush); }
            set { BullishBoxBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Bearish Box Color", Order = 3, GroupName = "3. Appearance")]
        public Brush BearishBoxBrush { get; set; }
        [Browsable(false)]
        public string BearishBoxBrushSerialize
        {
            get { return Serialize.BrushToString(BearishBoxBrush); }
            set { BearishBoxBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Label Text Color", Order = 4, GroupName = "3. Appearance")]
        public Brush LabelBrush { get; set; }
        [Browsable(false)]
        public string LabelBrushSerialize
        {
            get { return Serialize.BrushToString(LabelBrush); }
            set { LabelBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(3, 50)]
        [Display(Name = "Bullish Box Opacity %", Order = 5, GroupName = "3. Appearance")]
        public int BullishBoxOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(3, 50)]
        [Display(Name = "Bearish Box Opacity %", Order = 6, GroupName = "3. Appearance")]
        public int BearishBoxOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Hour Open Line Width", Order = 7, GroupName = "3. Appearance")]
        public int OpenLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Open Line Dash Style", Order = 8, GroupName = "3. Appearance")]
        public DashStyleHelper OpenLineDash { get; set; }

        [Display(Name = "Label Font", Order = 9, GroupName = "3. Appearance")]
        public SimpleFont LabelFont { get; set; }

        [NinjaScriptProperty]
        [Range(0, 60)]
        [Display(Name = "Label Y Offset (pixels)", Order = 10, GroupName = "3. Appearance")]
        public int LabelYOffset { get; set; }
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description             = "Hourly Open Statistics — tracks open price, range, volume, skew, and N-day avg comparison per hour. Inspired by institutional session analysis.";
                Name                    = "HourlyOpenStats";
                Calculate               = Calculate.OnBarClose;
                IsOverlay               = true;
                DisplayInDataBox        = true;
                DrawOnPricePanel        = true;
                PaintPriceMarkers       = false;
                IsSuspendedWhileInactive = true;

                // Display defaults
                ShowHourOpenLines       = true;
                ShowHourRangeBoxes      = true;
                ShowHourLabels          = true;
                ShowStatsPanel          = true;
                ShowSkewData            = true;
                ShowPctDistributed      = true;
                LabelsCurrentOnly       = false;
                MaxLabelHours           = 6;
                StatsPanelPosition      = TextPosition.TopLeft;

                // Analysis defaults
                LookbackDays            = 10;
                StartHour               = 0;
                EndHour                 = 24;

                // Appearance defaults
                HourOpenBrush           = Brushes.Yellow;
                BullishBoxBrush         = Brushes.LimeGreen;
                BearishBoxBrush         = Brushes.Crimson;
                LabelBrush              = Brushes.White;
                BullishBoxOpacity       = 10;
                BearishBoxOpacity       = 8;
                OpenLineWidth           = 2;
                OpenLineDash            = DashStyleHelper.DashDot;
                LabelFont               = new SimpleFont("Arial", 9);
                LabelYOffset            = 18;
            }
            else if (State == State.DataLoaded)
            {
                activeHours         = new Dictionary<string, HourData>();
                historicalRanges    = new Dictionary<int, List<double>>();
                historicalVolumes   = new Dictionary<int, List<double>>();

                for (int h = 0; h < 24; h++)
                {
                    historicalRanges[h]  = new List<double>();
                    historicalVolumes[h] = new List<double>();
                }
            }
            else if (State == State.Terminated)
            {
                DisposeSharpDXResources();
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 2) return;

            int barHour     = Time[0].Hour;
            int prevBarHour = Time[1].Hour;
            string dateKey  = Time[0].ToString("yyyyMMdd");
            string hourKey  = dateKey + "_" + barHour.ToString("D2");

            if (!IsHourInRange(barHour)) return;

            if (barHour != prevBarHour || !activeHours.ContainsKey(hourKey))
            {
                if (currentHourData != null && !currentHourData.IsComplete && barHour != lastHour)
                {
                    currentHourData.EndBar = CurrentBar - 1;
                    FinalizeHour(currentHourData);
                }

                if (!activeHours.ContainsKey(hourKey))
                {
                    HourData hd = new HourData
                    {
                        Hour        = barHour,
                        OpenPrice   = Open[0],
                        High        = High[0],
                        Low         = Low[0],
                        ClosePrice  = Close[0],
                        TotalVolume = Volume[0],
                        UpVolume    = Close[0] >= Open[0] ? Volume[0] : 0,
                        DownVolume  = Close[0] < Open[0] ? Volume[0] : 0,
                        BarCount    = 1,
                        StartBar    = CurrentBar,
                        EndBar      = CurrentBar,
                        StartTime   = Time[0],
                        IsComplete  = false
                    };

                    activeHours[hourKey] = hd;
                    currentHourData = hd;
                    lastHour = barHour;
                    lastHourKey = hourKey;
                }
            }

            if (activeHours.ContainsKey(hourKey))
            {
                HourData hd = activeHours[hourKey];
                hd.High         = Math.Max(hd.High, High[0]);
                hd.Low          = Math.Min(hd.Low, Low[0]);
                hd.ClosePrice   = Close[0];
                hd.TotalVolume += Volume[0];
                hd.BarCount++;

                if (Close[0] >= Open[0])
                    hd.UpVolume += Volume[0];
                else
                    hd.DownVolume += Volume[0];

                currentHourData = hd;
            }
        }

        #region Hour Range Check
        private bool IsHourInRange(int hour)
        {
            if (EndHour == 24) return hour >= StartHour;
            if (StartHour <= EndHour)
                return hour >= StartHour && hour < EndHour;
            else
                return hour >= StartHour || hour < EndHour;
        }
        #endregion

        #region Finalize Hour
        private void FinalizeHour(HourData hd)
        {
            hd.IsComplete = true;
            double range = hd.High - hd.Low;

            if (historicalRanges.ContainsKey(hd.Hour))
            {
                historicalRanges[hd.Hour].Add(range);
                if (historicalRanges[hd.Hour].Count > LookbackDays)
                    historicalRanges[hd.Hour].RemoveAt(0);
            }
            if (historicalVolumes.ContainsKey(hd.Hour))
            {
                historicalVolumes[hd.Hour].Add(hd.TotalVolume);
                if (historicalVolumes[hd.Hour].Count > LookbackDays)
                    historicalVolumes[hd.Hour].RemoveAt(0);
            }
        }
        #endregion

        #region Get Historical Stats
        private HourHistoricalStats GetStats(int hour)
        {
            HourHistoricalStats stats = new HourHistoricalStats();

            if (historicalRanges.ContainsKey(hour) && historicalRanges[hour].Count > 0)
            {
                List<double> ranges = historicalRanges[hour];
                stats.AvgRange      = ranges.Average();
                stats.LargestRange  = ranges.Max();
                stats.SmallestRange = ranges.Min();
                stats.SampleCount   = ranges.Count;
            }
            if (historicalVolumes.ContainsKey(hour) && historicalVolumes[hour].Count > 0)
            {
                stats.AvgVolume = historicalVolumes[hour].Average();
            }

            return stats;
        }
        #endregion

        #region SharpDX Resource Helpers
        private SharpDX.Color4 ToColor4(System.Windows.Media.Brush wpfBrush, float alpha)
        {
            System.Windows.Media.Color c = ((System.Windows.Media.SolidColorBrush)wpfBrush).Color;
            return new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, alpha);
        }

        private void CreateSharpDXResources(SharpDX.Direct2D1.RenderTarget rt)
        {
            if (dxResourcesCreated) return;

            dxHourOpenBrush   = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(HourOpenBrush, 1f));
            dxBullishBoxBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(BullishBoxBrush, BullishBoxOpacity / 100f));
            dxBearishBoxBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(BearishBoxBrush, BearishBoxOpacity / 100f));
            dxLabelBrush      = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(LabelBrush, 1f));
            dxPanelBgBrush    = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0f, 0f, 0f, 0.6f));

            if (OpenLineDash != DashStyleHelper.Solid)
            {
                SharpDX.Direct2D1.StrokeStyleProperties strokeProps = new SharpDX.Direct2D1.StrokeStyleProperties();
                switch (OpenLineDash)
                {
                    case DashStyleHelper.Dash:       strokeProps.DashStyle = SharpDX.Direct2D1.DashStyle.Dash;       break;
                    case DashStyleHelper.DashDot:    strokeProps.DashStyle = SharpDX.Direct2D1.DashStyle.DashDot;    break;
                    case DashStyleHelper.DashDotDot: strokeProps.DashStyle = SharpDX.Direct2D1.DashStyle.DashDotDot; break;
                    case DashStyleHelper.Dot:        strokeProps.DashStyle = SharpDX.Direct2D1.DashStyle.Dot;        break;
                    default:                         strokeProps.DashStyle = SharpDX.Direct2D1.DashStyle.Solid;      break;
                }
                dxOpenLineStrokeStyle = new SharpDX.Direct2D1.StrokeStyle(rt.Factory, strokeProps);
            }

            float   fontSize   = LabelFont != null ? (float)LabelFont.Size : 9f;
            string  fontFamily = LabelFont != null ? LabelFont.Family.ToString() : "Arial";

            dxTextFormat = new SharpDX.DirectWrite.TextFormat(
                NinjaTrader.Core.Globals.DirectWriteFactory,
                fontFamily,
                SharpDX.DirectWrite.FontWeight.Normal,
                SharpDX.DirectWrite.FontStyle.Normal,
                SharpDX.DirectWrite.FontStretch.Normal,
                fontSize);

            dxResourcesCreated = true;
        }

        private void DisposeSharpDXResources()
        {
            if (dxHourOpenBrush      != null) { dxHourOpenBrush.Dispose();      dxHourOpenBrush      = null; }
            if (dxBullishBoxBrush    != null) { dxBullishBoxBrush.Dispose();    dxBullishBoxBrush    = null; }
            if (dxBearishBoxBrush    != null) { dxBearishBoxBrush.Dispose();    dxBearishBoxBrush    = null; }
            if (dxLabelBrush         != null) { dxLabelBrush.Dispose();         dxLabelBrush         = null; }
            if (dxPanelBgBrush       != null) { dxPanelBgBrush.Dispose();       dxPanelBgBrush       = null; }
            if (dxOpenLineStrokeStyle != null) { dxOpenLineStrokeStyle.Dispose(); dxOpenLineStrokeStyle = null; }
            if (dxTextFormat         != null) { dxTextFormat.Dispose();         dxTextFormat         = null; }
            dxResourcesCreated = false;
        }

        public override void OnRenderTargetChanged()
        {
            DisposeSharpDXResources();
        }
        #endregion

        #region OnRender — GPU Rendering
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (activeHours == null || activeHours.Count == 0)
                return;

            SharpDX.Direct2D1.RenderTarget renderTarget = RenderTarget;
            if (renderTarget == null)
                return;

            if (!dxResourcesCreated)
                CreateSharpDXResources(renderTarget);

            int firstBar = ChartBars.FromIndex;
            int lastBar  = ChartBars.ToIndex;

            foreach (var kvp in activeHours)
            {
                string   hourKey   = kvp.Key;
                HourData hd        = kvp.Value;
                int      hourEndBar = hd.IsComplete ? hd.EndBar : CurrentBar;

                // Skip hours entirely outside the visible range
                if (hourEndBar < firstBar || hd.StartBar > lastBar)
                    continue;

                int   clampedStart = Math.Max(hd.StartBar, firstBar);
                int   clampedEnd   = Math.Min(hourEndBar, lastBar);
                float xStart       = chartControl.GetXByBarIndex(ChartBars, clampedStart);
                float xEnd         = chartControl.GetXByBarIndex(ChartBars, clampedEnd);
                float boxWidth     = xEnd - xStart;

                // 1. Hour Range Box
                if (ShowHourRangeBoxes && hd.BarCount > 1)
                {
                    float yHigh     = chartScale.GetYByValue(hd.High);
                    float yLow      = chartScale.GetYByValue(hd.Low);
                    float boxHeight = yLow - yHigh;

                    if (boxWidth > 0 && boxHeight > 0)
                    {
                        bool   bullish  = hd.ClosePrice >= hd.OpenPrice;
                        SharpDX.Direct2D1.SolidColorBrush boxBrush = bullish ? dxBullishBoxBrush : dxBearishBoxBrush;
                        renderTarget.FillRectangle(new SharpDX.RectangleF(xStart, yHigh, boxWidth, boxHeight), boxBrush);
                    }
                }

                // 2. Hour Open Line
                if (ShowHourOpenLines)
                {
                    float yOpen = chartScale.GetYByValue(hd.OpenPrice);
                    var   p1    = new SharpDX.Vector2(xStart, yOpen);
                    var   p2    = new SharpDX.Vector2(xEnd,   yOpen);

                    if (dxOpenLineStrokeStyle != null)
                        renderTarget.DrawLine(p1, p2, dxHourOpenBrush, OpenLineWidth, dxOpenLineStrokeStyle);
                    else
                        renderTarget.DrawLine(p1, p2, dxHourOpenBrush, OpenLineWidth);
                }

                // 3. Hour Label
                if (ShowHourLabels && ShouldShowLabel(hourKey, hd))
                {
                    string labelText = BuildLabel(hd);
                    float  yHigh     = chartScale.GetYByValue(hd.High);
                    float  labelX    = xStart + 4f;
                    float  labelY    = yHigh - LabelYOffset;

                    SharpDX.DirectWrite.TextLayout layout = new SharpDX.DirectWrite.TextLayout(
                        NinjaTrader.Core.Globals.DirectWriteFactory,
                        labelText, dxTextFormat, 200f, 200f);
                    renderTarget.DrawTextLayout(new SharpDX.Vector2(labelX, labelY), layout, dxLabelBrush);
                    layout.Dispose();
                }
            }

            // 4. Stats Panel — rendered last so it sits on top of everything
            if (ShowStatsPanel && currentHourData != null && !currentHourData.IsComplete
                && lastHourKey != "" && activeHours.ContainsKey(lastHourKey))
            {
                RenderStatsPanelDX(renderTarget, activeHours[lastHourKey]);
            }
        }
        #endregion

        #region Stats Panel — SharpDX
        private string BuildStatsPanelText(HourData hd)
        {
            HourHistoricalStats stats        = GetStats(hd.Hour);
            double              currentRange = hd.High - hd.Low;

            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("{0}-Day Average Range", LookbackDays);

            if (stats.SampleCount > 0)
            {
                sb.AppendFormat("\n{0}:00 Avg: ({1:F2}) / Current: ({2:F2})",
                    hd.Hour, stats.AvgRange, currentRange);
                sb.AppendFormat("\n{0}:00 Largest Range: ({1:F2})",
                    hd.Hour, stats.LargestRange);
                sb.AppendFormat("\n{0}:00 Smallest Range: ({1:F2})",
                    hd.Hour, stats.SmallestRange);

                if (stats.AvgRange > 0)
                    sb.AppendFormat("\nDistributed {0:F0}% of Avg Range",
                        (currentRange / stats.AvgRange) * 100);
            }
            else
            {
                sb.Append("\nInsufficient historical data");
            }

            sb.AppendFormat("\nRaw H/L: {0} / {1}",
                Instrument.MasterInstrument.FormatPrice(hd.High),
                Instrument.MasterInstrument.FormatPrice(hd.Low));

            if (ShowSkewData && hd.TotalVolume > 0)
            {
                double upPct   = (hd.UpVolume   / hd.TotalVolume) * 100;
                double dnPct   = (hd.DownVolume / hd.TotalVolume) * 100;
                string skewDir = upPct > 55 ? "Bullish" : (dnPct > 55 ? "Bearish" : "Balanced");
                sb.AppendFormat("\n{0}:00 Data Skew: {1} ({2:F0}/{3:F0})",
                    hd.Hour, skewDir, upPct, dnPct);
            }

            return sb.ToString();
        }

        private void RenderStatsPanelDX(SharpDX.Direct2D1.RenderTarget rt, HourData hd)
        {
            string text = BuildStatsPanelText(hd);

            SharpDX.DirectWrite.TextLayout layout = new SharpDX.DirectWrite.TextLayout(
                NinjaTrader.Core.Globals.DirectWriteFactory,
                text, dxTextFormat, 260f, 300f);

            float textWidth  = layout.Metrics.Width;
            float textHeight = layout.Metrics.Height;
            float padding    = 8f;
            float margin     = 10f;
            float panelW     = textWidth  + padding * 2;
            float panelH     = textHeight + padding * 2;

            float originX = (float)ChartPanel.X;
            float originY = (float)ChartPanel.Y;
            float totalW  = (float)ChartPanel.W;
            float totalH  = (float)ChartPanel.H;

            float panelX, panelY;
            switch (StatsPanelPosition)
            {
                case TextPosition.TopRight:
                    panelX = originX + totalW - panelW - margin;
                    panelY = originY + margin;
                    break;
                case TextPosition.BottomLeft:
                    panelX = originX + margin;
                    panelY = originY + totalH - panelH - margin;
                    break;
                case TextPosition.BottomRight:
                    panelX = originX + totalW - panelW - margin;
                    panelY = originY + totalH - panelH - margin;
                    break;
                case TextPosition.Center:
                    panelX = originX + (totalW - panelW) / 2f;
                    panelY = originY + (totalH - panelH) / 2f;
                    break;
                default: // TopLeft
                    panelX = originX + margin;
                    panelY = originY + margin;
                    break;
            }

            rt.FillRectangle(new SharpDX.RectangleF(panelX, panelY, panelW, panelH), dxPanelBgBrush);
            rt.DrawTextLayout(new SharpDX.Vector2(panelX + padding, panelY + padding), layout, dxLabelBrush);
            layout.Dispose();
        }
        #endregion

        #region Should Show Label
        private bool ShouldShowLabel(string hourKey, HourData hd)
        {
            if (LabelsCurrentOnly)
                return hourKey == lastHourKey;

            if (activeHours == null || activeHours.Count == 0) return true;

            List<string> sortedKeys = activeHours.Keys.OrderByDescending(k => k).ToList();
            int idx = sortedKeys.IndexOf(hourKey);

            return idx >= 0 && idx < MaxLabelHours;
        }
        #endregion

        #region Build Label String
        private string BuildLabel(HourData hd)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("Hour Open {0}:00", hd.Hour);

            HourHistoricalStats stats = GetStats(hd.Hour);
            if (stats.SampleCount > 0)
                sb.AppendFormat("\nAvg: {0:F0}", stats.AvgVolume);

            sb.AppendFormat("\nVol: {0:F0}", hd.TotalVolume);

            if (ShowSkewData && hd.TotalVolume > 0)
            {
                double upPct = (hd.UpVolume / hd.TotalVolume) * 100;
                double dnPct = (hd.DownVolume / hd.TotalVolume) * 100;
                string skewDir = upPct > 55 ? "Bullish" : (dnPct > 55 ? "Bearish" : "Balanced");
                sb.AppendFormat("\nSkew: {0} ({1:F0}/{2:F0})", skewDir, upPct, dnPct);
            }

            if (ShowPctDistributed)
            {
                double currentRange = hd.High - hd.Low;
                HourHistoricalStats s = GetStats(hd.Hour);
                if (s.AvgRange > 0)
                {
                    double pct = (currentRange / s.AvgRange) * 100;
                    sb.AppendFormat("\nDistributed: {0:F0}% of Avg", pct);
                }
            }

            return sb.ToString();
        }
        #endregion
    }
}