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
            public double   TotalVolume;
            public double   UpVolume;
            public double   DownVolume;
            public int      BarCount;
            public int      StartBar;
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
                        TotalVolume = Volume[0],
                        UpVolume    = Close[0] >= Open[0] ? Volume[0] : 0,
                        DownVolume  = Close[0] < Open[0] ? Volume[0] : 0,
                        BarCount    = 1,
                        StartBar    = CurrentBar,
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
                hd.TotalVolume += Volume[0];
                hd.BarCount++;

                if (Close[0] >= Open[0])
                    hd.UpVolume += Volume[0];
                else
                    hd.DownVolume += Volume[0];

                currentHourData = hd;
            }

            if (activeHours.ContainsKey(hourKey))
                DrawHourVisuals(hourKey, activeHours[hourKey]);
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

        #region Draw Hour Visuals
        private void DrawHourVisuals(string hourKey, HourData hd)
        {
            string tagBase = "HOS_" + hourKey;
            int barsBack = CurrentBar - hd.StartBar;
            if (barsBack < 0) barsBack = 0;

            // 1. Hour Range Box (drawn first so open line renders on top)
            if (ShowHourRangeBoxes && hd.BarCount > 1)
            {
                bool bullish = Close[0] >= hd.OpenPrice;
                Brush baseBrush = bullish ? BullishBoxBrush : BearishBoxBrush;
                int opacity = bullish ? BullishBoxOpacity : BearishBoxOpacity;
                Brush boxBrush = ApplyOpacity(baseBrush, opacity);

                Draw.Rectangle(this, tagBase + "_box", false,
                    barsBack, hd.High, 0, hd.Low,
                    Brushes.Transparent, boxBrush, 0);
            }

            // 2. Hour Open Line — on top of the box
            if (ShowHourOpenLines)
            {
                Draw.Line(this, tagBase + "_open", false,
                    barsBack, hd.OpenPrice, 0, hd.OpenPrice,
                    HourOpenBrush, OpenLineDash, OpenLineWidth);
            }

            // 3. Hour Label — only for recent hours to prevent overlap
            if (ShowHourLabels && ShouldShowLabel(hourKey, hd))
            {
                string label = BuildLabel(hd);

                Draw.Text(this, tagBase + "_label", false, label,
                    barsBack, hd.High,
                    LabelYOffset,
                    LabelBrush,
                    LabelFont,
                    TextAlignment.Left,
                    Brushes.Transparent,
                    Brushes.Transparent,
                    0);
            }

            // 4. Stats Panel — user-configurable position
            if (ShowStatsPanel && !hd.IsComplete && hourKey == lastHourKey)
                DrawStatsPanel(hd);
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

        #region Apply Opacity Helper
        private Brush ApplyOpacity(Brush baseBrush, int opacityPercent)
        {
            byte alpha = (byte)(255 * opacityPercent / 100);
            System.Windows.Media.SolidColorBrush solidBrush = baseBrush as System.Windows.Media.SolidColorBrush;
            if (solidBrush != null)
            {
                System.Windows.Media.Color c = solidBrush.Color;
                Brush result = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(alpha, c.R, c.G, c.B));
                result.Freeze();
                return result;
            }
            return baseBrush;
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

        #region Draw Stats Panel
        private void DrawStatsPanel(HourData hd)
        {
            HourHistoricalStats stats = GetStats(hd.Hour);
            double currentRange = hd.High - hd.Low;

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
                double upPct = (hd.UpVolume / hd.TotalVolume) * 100;
                double dnPct = (hd.DownVolume / hd.TotalVolume) * 100;
                string skewDir = upPct > 55 ? "Bullish" : (dnPct > 55 ? "Bearish" : "Balanced");
                sb.AppendFormat("\n{0}:00 Data Skew: {1} ({2:F0}/{3:F0})",
                    hd.Hour, skewDir, upPct, dnPct);
            }

            Draw.TextFixed(this, "HOS_StatsPanel", sb.ToString(),
                StatsPanelPosition, LabelBrush, LabelFont,
                Brushes.Transparent, Brushes.Transparent, 0);
        }
        #endregion
    }
}