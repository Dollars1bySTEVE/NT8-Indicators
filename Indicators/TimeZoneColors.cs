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
    public class TimeZoneColors : Indicator
    {
        private int CZn1St = 0, CZn2St = 0, CZn3St = 0;
        private int CZn1En = 0, CZn2En = 0, CZn3En = 0;
        private string currentZone = "";
        private int lastScreenBar = 0;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                 = @"Color the chart background for up to three custom time frames with adjustable opacity.";
                Name                        = "TimeZoneColors";
                Calculate                   = Calculate.OnBarClose;
                IsOverlay                   = true;
                DisplayInDataBox            = true;
                DrawOnPricePanel            = true;
                DrawHorizontalGridLines     = true;
                DrawVerticalGridLines       = true;
                PaintPriceMarkers           = true;
                ScaleJustification          = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                IsSuspendedWhileInactive    = true;

                Zn1HrSt        = 19;
                Zn1MinSt       = 0;
                Zn1HrEn        = 4;
                Zn1MinEn       = 0;
                Region1Name    = @"Tokyo";
                Region1Brush   = Brushes.Pink;

                Zn2HrSt        = 3;
                Zn2MinSt       = 0;
                Zn2HrEn        = 12;
                Zn2MinEn       = 0;
                Region2Name    = @"London";
                Region2Brush   = Brushes.Beige;

                Zn3HrSt        = 8;
                Zn3MinSt       = 0;
                Zn3HrEn        = 16;
                Zn3MinEn       = 0;
                Region3Name    = @"New York";
                Region3Brush   = Brushes.LightGreen;

                ColorAll       = false;
                AlertBool      = true;
                Font           = new SimpleFont("Arial", 16);

                // NEW: user-adjustable opacity (10 = very faint, 255 = full solid)
                ZoneOpacity    = 40;
            }
            else if (State == State.Historical)
            {
                CZn1St = ((Zn1HrSt * 10000) + (Zn1MinSt * 100));
                CZn1En = ((Zn1HrEn * 10000) + (Zn1MinEn * 100));

                CZn2St = ((Zn2HrSt * 10000) + (Zn2MinSt * 100));
                CZn2En = ((Zn2HrEn * 10000) + (Zn2MinEn * 100));

                CZn3St = ((Zn3HrSt * 10000) + (Zn3MinSt * 100));
                CZn3En = ((Zn3HrEn * 10000) + (Zn3MinEn * 100));
            }
        }

        protected override void OnBarUpdate()
        {
            int r = 0, g = 0, b = 0;

            string zone = GetTimeZone(0);

            SolidColorBrush br = ChartControl.Properties.ChartBackground as SolidColorBrush;
            SolidColorBrush region1Brush = Region1Brush as SolidColorBrush;
            SolidColorBrush region2Brush = Region2Brush as SolidColorBrush;
            SolidColorBrush region3Brush = Region3Brush as SolidColorBrush;

            if (zone.CompareTo("") == 0) { r = br.Color.R; g = br.Color.G; b = br.Color.B; }
            else
            {
                if (zone.Contains("Z3")) { r = region3Brush.Color.R; g = region3Brush.Color.G; b = region3Brush.Color.B; }
                if (zone.Contains("Z2")) { r = region2Brush.Color.R; g = region2Brush.Color.G; b = region2Brush.Color.B; }
                if (zone.Contains("Z1")) { r = region1Brush.Color.R; g = region1Brush.Color.G; b = region1Brush.Color.B; }

                //### Blend colors of overlapped timezones
                if (zone.Contains("+1"))
                {
                    r = (int)(Math.Abs((r - region1Brush.Color.R) * .35) + Math.Min(r, region1Brush.Color.R));
                    g = (int)(Math.Abs((g - region1Brush.Color.G) * .35) + Math.Min(g, region1Brush.Color.G));
                    b = (int)(Math.Abs((b - region1Brush.Color.B) * .35) + Math.Min(b, region1Brush.Color.B));
                }
                if (zone.Contains("+2"))
                {
                    r = (int)(Math.Abs((r - region2Brush.Color.R) * .35) + Math.Min(r, region2Brush.Color.R));
                    g = (int)(Math.Abs((g - region2Brush.Color.G) * .35) + Math.Min(g, region2Brush.Color.G));
                    b = (int)(Math.Abs((b - region2Brush.Color.B) * .35) + Math.Min(b, region2Brush.Color.B));
                }
                if (zone.Contains("+3"))
                {
                    r = (int)(Math.Abs((r - region3Brush.Color.R) * .35) + Math.Min(r, region3Brush.Color.R));
                    g = (int)(Math.Abs((g - region3Brush.Color.G) * .35) + Math.Min(g, region3Brush.Color.G));
                    b = (int)(Math.Abs((b - region3Brush.Color.B) * .35) + Math.Min(b, region3Brush.Color.B));
                }
            }

            // Limit check
            if (r > 255 || r < 0) r = Math.Max(0, Math.Min(255, r));
            if (g > 255 || g < 0) g = Math.Max(0, Math.Min(255, g));
            if (b > 255 || b < 0) b = Math.Max(0, Math.Min(255, b));

            // Use ZoneOpacity for the alpha channel — user controls how transparent the zones are
            byte alpha = (zone.CompareTo("") == 0) ? (byte)255 : (byte)ZoneOpacity;

            if (ColorAll == true)
                BackBrushAll = FromArgb(alpha, (byte)r, (byte)g, (byte)b);
            else
                BackBrush = FromArgb(alpha, (byte)r, (byte)g, (byte)b);

            if (AlertBool == true)
                if (ToTime(Time[0]) == CZn1St)
                    Alert("r1st", Priority.Medium, "Beginning of Time Region #1", "Alert2.wav", 0, Region1Brush, Brushes.Black);
                else if (ToTime(Time[0]) == CZn1En)
                    Alert("r1en", Priority.Medium, "End of Time Region #1", "Alert2.wav", 0, Region1Brush, Brushes.Black);
                else if (ToTime(Time[0]) == CZn2St)
                    Alert("r2st", Priority.Medium, "Beginning of Time Region #2", "Alert2.wav", 0, Region2Brush, Brushes.Black);
                else if (ToTime(Time[0]) == CZn2En)
                    Alert("r2en", Priority.Medium, "End of Time Region #2", "Alert2.wav", 0, Region2Brush, Brushes.Black);
                else if (ToTime(Time[0]) == CZn3St)
                    Alert("r3st", Priority.Medium, "Beginning of Time Region #3", "Alert2.wav", 0, Region3Brush, Brushes.Black);
                else if (ToTime(Time[0]) == CZn3En)
                    Alert("r3en", Priority.Medium, "End of Time Region #3", "Alert2.wav", 0, Region3Brush, Brushes.Black);
        }

        #region Misc

        private Brush FromArgb(byte a, byte r, byte g, byte b)
        {
            return (Brush)new BrushConverter().ConvertFrom("#" + a.ToString("X2") + r.ToString("X2") + g.ToString("X2") + b.ToString("X2"));
        }

        string GetTimeZone(int x, bool onRender = false)
        {
            int zone = 0;
            string zones = "";
            if (x < 0) x = 0;

            //### Check for Zone 1
            zone = 0;
            if (CZn1En < CZn1St)
            {
                if (ToTime(onRender ? Time.GetValueAt(x) : Time[x]) > CZn1St && ToTime(onRender ? Time.GetValueAt(x) : Time[x]) > CZn1En) zone = 1;
                if (ToTime(onRender ? Time.GetValueAt(x) : Time[x]) < CZn1St && ToTime(onRender ? Time.GetValueAt(x) : Time[x]) < CZn1En) zone = 1;
            }
            else if (ToTime(onRender ? Time.GetValueAt(x) : Time[x]) >= CZn1St && ToTime(onRender ? Time.GetValueAt(x) : Time[x]) < CZn1En) zone = 1;
            if (zone > 0) zones = (zones.CompareTo("") == 0) ? "Z" + zone + zones : "+" + zone + zones;

            //### Check for Zone 2
            zone = 0;
            if (CZn2En < CZn2St)
            {
                if (ToTime(onRender ? Time.GetValueAt(x) : Time[x]) > CZn2St && ToTime(onRender ? Time.GetValueAt(x) : Time[x]) > CZn2En) zone = 2;
                if (ToTime(onRender ? Time.GetValueAt(x) : Time[x]) < CZn2St && ToTime(onRender ? Time.GetValueAt(x) : Time[x]) < CZn2En) zone = 2;
            }
            else if (ToTime(onRender ? Time.GetValueAt(x) : Time[x]) >= CZn2St && ToTime(onRender ? Time.GetValueAt(x) : Time[x]) < CZn2En) zone = 2;
            if (zone > 0) zones = (zones.CompareTo("") == 0) ? "Z" + zone + zones : "+" + zone + zones;

            //### Check for Zone 3
            zone = 0;
            if (CZn3En < CZn3St)
            {
                if (ToTime(onRender ? Time.GetValueAt(x) : Time[x]) > CZn3St && ToTime(onRender ? Time.GetValueAt(x) : Time[x]) > CZn3En) zone = 3;
                if (ToTime(onRender ? Time.GetValueAt(x) : Time[x]) < CZn3St && ToTime(onRender ? Time.GetValueAt(x) : Time[x]) < CZn3En) zone = 3;
            }
            else if (ToTime(onRender ? Time.GetValueAt(x) : Time[x]) >= CZn3St && ToTime(onRender ? Time.GetValueAt(x) : Time[x]) < CZn3En) zone = 3;
            if (zone > 0) zones = (zones.CompareTo("") == 0) ? "Z" + zone + zones : "+" + zone + zones;

            return zones;
        }

        private Brush getContrastColor(Brush baseColor)
        {
            SolidColorBrush br = baseColor as SolidColorBrush;
            int r = br.Color.R > 127 ? 0 : 255;
            int g = br.Color.G > 127 ? 0 : 255;
            int b = br.Color.B > 127 ? 0 : 255;
            return FromArgb((byte)255, (byte)r, (byte)g, (byte)b);
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (ChartBars == null) return;
            string zone = "";

            if (GetTimeZone(ChartBars.ToIndex, true).Contains("1")) zone += Region1Name + "\n";
            if (GetTimeZone(ChartBars.ToIndex, true).Contains("2")) zone += Region2Name + "\n";
            if (GetTimeZone(ChartBars.ToIndex, true).Contains("3")) zone += Region3Name + "\n";
            if (zone.CompareTo(currentZone) != 0) currentZone = zone;
            if (BackBrushes.Get(ChartBars.ToIndex - 1) == null) return;
            Brush backColor = getContrastColor(BackBrushes.Get(ChartBars.ToIndex - 1));
            Brush foreColor = getContrastColor(backColor);
            Draw.TextFixed(this, "Zone", currentZone, TextPosition.TopRight, backColor, Font, foreColor, foreColor, 50);
        }

        #endregion

        #region Properties

        [NinjaScriptProperty]
        [Range(5, 255)]
        [Display(Name = "Zone Opacity", Description = "Background transparency: 5 = nearly invisible, 40 = subtle, 100 = medium, 255 = full solid.",
            Order = 0, GroupName = "Configuration")]
        public int ZoneOpacity
        { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Color All Panels?", Description = "Colors across all panels or just main price display panel",
            Order = 1, GroupName = "Configuration")]
        public bool ColorAll
        { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert on Begin/End?", Description = "Sound and display an alert with Alerts window open",
            Order = 2, GroupName = "Configuration")]
        public bool AlertBool
        { get; set; }

        [Display(Name = "Text Font", GroupName = "Configuration", Order = 3)]
        public Gui.Tools.SimpleFont Font
        { get; set; }

        // --- 1st Time Region ---
        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Start Hour", Description = "Hour for 1st time region to begin (24hr clock).",
            Order = 1, GroupName = "1st Time Region")]
        public int Zn1HrSt
        { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Start Minute", Description = "Minute for 1st time region to begin.",
            Order = 2, GroupName = "1st Time Region")]
        public int Zn1MinSt
        { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "End Hour", Description = "Hour for 1st time region to end (24hr clock).",
            Order = 3, GroupName = "1st Time Region")]
        public int Zn1HrEn
        { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "End Minute", Description = "Minute for 1st time region to end.",
            Order = 4, GroupName = "1st Time Region")]
        public int Zn1MinEn
        { get; set; }

        [XmlIgnore()]
        [Display(Name = "Region 1 Color", GroupName = "1st Time Region", Order = 5)]
        public Brush Region1Brush
        { get; set; }

        [Browsable(false)]
        public string Region1BrushSerialize
        {
            get { return Serialize.BrushToString(Region1Brush); }
            set { Region1Brush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Region 1 Name", Description = "Name of 1st time region.",
            Order = 6, GroupName = "1st Time Region")]
        public string Region1Name
        { get; set; }

        // --- 2nd Time Region ---
        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Start Hour", Description = "Hour for 2nd time region to begin (24hr clock).",
            Order = 1, GroupName = "2nd Time Region")]
        public int Zn2HrSt
        { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Start Minute", Description = "Minute for 2nd time region to begin.",
            Order = 2, GroupName = "2nd Time Region")]
        public int Zn2MinSt
        { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "End Hour", Description = "Hour for 2nd time region to end (24hr clock).",
            Order = 3, GroupName = "2nd Time Region")]
        public int Zn2HrEn
        { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "End Minute", Description = "Minute for 2nd time region to end.",
            Order = 4, GroupName = "2nd Time Region")]
        public int Zn2MinEn
        { get; set; }

        [XmlIgnore()]
        [Display(Name = "Region 2 Color", GroupName = "2nd Time Region", Order = 5)]
        public Brush Region2Brush
        { get; set; }

        [Browsable(false)]
        public string Region2BrushSerialize
        {
            get { return Serialize.BrushToString(Region2Brush); }
            set { Region2Brush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Region 2 Name", Description = "Name of 2nd time region.",
            Order = 6, GroupName = "2nd Time Region")]
        public string Region2Name
        { get; set; }

        // --- 3rd Time Region ---
        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Start Hour", Description = "Hour for 3rd time region to begin (24hr clock).",
            Order = 1, GroupName = "3rd Time Region")]
        public int Zn3HrSt
        { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Start Minute", Description = "Minute for 3rd time region to begin.",
            Order = 2, GroupName = "3rd Time Region")]
        public int Zn3MinSt
        { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "End Hour", Description = "Hour for 3rd time region to end (24hr clock).",
            Order = 3, GroupName = "3rd Time Region")]
        public int Zn3HrEn
        { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "End Minute", Description = "Minute for 3rd time region to end.",
            Order = 4, GroupName = "3rd Time Region")]
        public int Zn3MinEn
        { get; set; }

        [XmlIgnore()]
        [Display(Name = "Region 3 Color", GroupName = "3rd Time Region", Order = 5)]
        public Brush Region3Brush
        { get; set; }

        [Browsable(false)]
        public string Region3BrushSerialize
        {
            get { return Serialize.BrushToString(Region3Brush); }
            set { Region3Brush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Region 3 Name", Description = "Name of 3rd time region.",
            Order = 6, GroupName = "3rd Time Region")]
        public string Region3Name
        { get; set; }

        #endregion
    }
}