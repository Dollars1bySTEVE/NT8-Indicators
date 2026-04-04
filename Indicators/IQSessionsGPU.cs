// IQ Sessions GPU — Comprehensive GPU-accelerated Sessions & Levels indicator for NinjaTrader 8
// Replicates all session, pivot, EMA, and range features from the Traders Reality "trmain" indicator.
// Companion to IQCandlesGPU.cs — uses identical SharpDX rendering patterns.

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
#endregion

// NinjaTrader 8 requires custom enums declared OUTSIDE all namespaces
// so the auto-generated partial class code can resolve them without ambiguity.
// Reference: forum.ninjatrader.com threads #1182932, #95909, #1046853

/// <summary>Dashboard anchor corner on the chart for IQSessionsGPU.</summary>
public enum IQSDashboardPosition { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>Line style for pivots, M-levels, ADR lines, etc.</summary>
public enum IQSLineStyle { Solid, Dashed, Dotted }

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// IQ Sessions GPU — Comprehensive GPU-accelerated market sessions, pivots, EMAs, and range
    /// levels indicator for NinjaTrader 8. Companion to IQCandlesGPU.
    ///
    /// Features:
    ///  • 5 EMAs (5, 13, 50, 200, 800) with EMA50 cloud (StdDev bands)
    ///  • Classic Floor Pivot Points (PP, R1-R3, S1-S3) with M-level midpoints
    ///  • Yesterday and Last Week High/Low as step lines
    ///  • ADR / AWR / AMR (Average Daily/Weekly/Monthly Range) with 50% levels
    ///  • Range Daily Hi/Lo and Range Weekly Hi/Lo
    ///  • Daily Open horizontal line
    ///  • 8 Market Sessions with opening-range boxes and DST handling
    ///    (London, New York, Tokyo, Hong Kong, Sydney, EU Brinks, US Brinks, Frankfurt)
    ///  • Weekly Psy (Psychological) High/Low levels
    ///  • ADR/AWR/AMR statistics dashboard table
    ///  • DST reference table
    ///  • Price-cross alerts for all key levels
    ///  • All SharpDX types fully qualified — no namespace conflicts
    ///  • Enums outside namespace (NT8 compiler requirement)
    ///  • Bounded session-box collections for memory safety
    /// </summary>
    public class IQSessionsGPU : Indicator
    {
        // ════════════════════════════════════════════════════════════════════════
        #region Inner types

        /// <summary>One completed session window for rendering the session box.</summary>
        private class SessionBox
        {
            public DateTime StartTime;
            public DateTime EndTime;
            public int      StartBarIndex;
            public int      EndBarIndex;
            public double   SessionHigh;
            public double   SessionLow;
            public bool     IsComplete;
            public int      SessionId;   // 0=London,1=NY,2=Tokyo,3=HK,4=Sydney,5=EUBrinks,6=USBrinks,7=Frankfurt
        }

        /// <summary>Snapshot of pivot levels for a trading day.</summary>
        private class PivotSnapshot
        {
            public double PP, R1, R2, R3, S1, S2, S3;
            public double M0, M1, M2, M3, M4, M5;
            public int    StartBarIndex;
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Private fields

        // ── EMA values (computed in OnBarUpdate via NinjaTrader EMA() calls) ──
        private double ema5Val, ema13Val, ema50Val, ema200Val, ema800Val;
        private double ema50Upper, ema50Lower;   // cloud bands

        // ── Pivot data ────────────────────────────────────────────────────────
        private PivotSnapshot currentPivot;
        private double        dayHigh, dayLow, dayClose;
        private double        prevDayHigh, prevDayLow, prevDayClose;
        private bool          prevDayLoaded;

        // ── Yesterday / last week Hi/Lo ───────────────────────────────────────
        private double yesterdayHigh, yesterdayLow;
        private double lastWeekHigh, lastWeekLow;
        private int    currentDayOfWeek;
        private double weekHigh, weekLow;
        private bool   weekDataLoaded;
        private int    currentMonth;
        private double monthHigh, monthLow;
        private bool   monthDataLoaded;

        // ── ADR / AWR / AMR ───────────────────────────────────────────────────
        private Queue<double> dailyRanges;    // rolling window for ADR
        private Queue<double> weeklyRanges;   // rolling window for AWR
        private Queue<double> monthlyRanges;  // rolling window for AMR
        private Queue<double> rdRanges;       // rolling window for RD
        private Queue<double> rwRanges;       // rolling window for RW

        private double adrValue, awrValue, amrValue, rdValue, rwValue;
        private double adrHigh, adrLow, awrHigh, awrLow, amrHigh, amrLow;
        private double rdHigh, rdLow, rwHigh, rwLow;
        private double dailyOpen;

        // ── Session tracking ─────────────────────────────────────────────────
        private List<SessionBox> sessionBoxes;  // capped at 200
        private SessionBox[]     activeSessions; // one per session type (0-7)

        // ── Weekly Psy levels ─────────────────────────────────────────────────
        private double psyWeekHigh, psyWeekLow;
        private int    psyWeekStartBar;

        // ── Alert state (to avoid repeated alerts) ────────────────────────────
        private bool alertAdrHighFired, alertAdrLowFired;
        private bool alertAwrHighFired, alertAwrLowFired;
        private bool alertAmrHighFired, alertAmrLowFired;

        // ── Constants ─────────────────────────────────────────────────────────
        // Minimum bars required before computing any indicator values;
        // must be >= the largest EMA period (800).
        private const int MIN_BARS_REQUIRED = 800;

        // ── SharpDX GPU resources ─────────────────────────────────────────────
        private bool dxReady;
        private SharpDX.DirectWrite.Factory        dxWriteFactory;
        private SharpDX.DirectWrite.TextFormat     dxLabelFormat;
        private SharpDX.DirectWrite.TextFormat     dxSmallFormat;

        // EMA brushes
        private SharpDX.Direct2D1.SolidColorBrush dxEma5Brush;
        private SharpDX.Direct2D1.SolidColorBrush dxEma13Brush;
        private SharpDX.Direct2D1.SolidColorBrush dxEma50Brush;
        private SharpDX.Direct2D1.SolidColorBrush dxEma200Brush;
        private SharpDX.Direct2D1.SolidColorBrush dxEma800Brush;
        private SharpDX.Direct2D1.SolidColorBrush dxCloudFillBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxCloudBorderBrush;

        // Pivot brushes
        private SharpDX.Direct2D1.SolidColorBrush dxPPBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxRLevelBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxSLevelBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxMLevelBrush;

        // Yesterday / last week brushes
        private SharpDX.Direct2D1.SolidColorBrush dxYesterdayBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxLastWeekBrush;

        // ADR / AWR / AMR brushes
        private SharpDX.Direct2D1.SolidColorBrush dxAdrBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxAwrBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxAmrBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxRdBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxRwBrush;

        // Daily open brush
        private SharpDX.Direct2D1.SolidColorBrush dxDailyOpenBrush;

        // Session box brushes (one per session type)
        private SharpDX.Direct2D1.SolidColorBrush[] dxSessionBoxBrush;
        private SharpDX.Direct2D1.SolidColorBrush[] dxSessionBorderBrush;

        // Psy level brush
        private SharpDX.Direct2D1.SolidColorBrush dxPsyBrush;

        // Dashboard brushes
        private SharpDX.Direct2D1.SolidColorBrush dxDashBgBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxDashTextBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxDashHeaderBrush;
        private SharpDX.DirectWrite.TextFormat     dxDashFormat;

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 1. Label Offsets

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Label X Offset (bars)", Order = 1, GroupName = "1. Label Offsets",
            Description = "Horizontal offset in bars for right-side line labels.")]
        public int LabelOffsetBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "Label Y Offset (ticks)", Order = 2, GroupName = "1. Label Offsets",
            Description = "Vertical offset in ticks for line labels to avoid overlap.")]
        public int LabelOffsetTicks { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 2. EMAs

        [NinjaScriptProperty]
        [Display(Name = "Show EMA 5", Order = 1, GroupName = "2. EMAs")]
        public bool ShowEma5 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA 5 Color", Order = 2, GroupName = "2. EMAs")]
        [XmlIgnore]
        public System.Windows.Media.Brush Ema5Color { get; set; }
        [Browsable(false)]
        public string Ema5ColorSerializable { get => Serialize.BrushToString(Ema5Color); set => Ema5Color = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "EMA 5 Thickness", Order = 3, GroupName = "2. EMAs")]
        public int Ema5Thickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show EMA 13", Order = 4, GroupName = "2. EMAs")]
        public bool ShowEma13 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA 13 Color", Order = 5, GroupName = "2. EMAs")]
        [XmlIgnore]
        public System.Windows.Media.Brush Ema13Color { get; set; }
        [Browsable(false)]
        public string Ema13ColorSerializable { get => Serialize.BrushToString(Ema13Color); set => Ema13Color = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "EMA 13 Thickness", Order = 6, GroupName = "2. EMAs")]
        public int Ema13Thickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show EMA 50", Order = 7, GroupName = "2. EMAs")]
        public bool ShowEma50 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA 50 Color", Order = 8, GroupName = "2. EMAs")]
        [XmlIgnore]
        public System.Windows.Media.Brush Ema50Color { get; set; }
        [Browsable(false)]
        public string Ema50ColorSerializable { get => Serialize.BrushToString(Ema50Color); set => Ema50Color = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "EMA 50 Thickness", Order = 9, GroupName = "2. EMAs")]
        public int Ema50Thickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show EMA 50 Cloud", Order = 10, GroupName = "2. EMAs",
            Description = "Draw fill between EMA50 +/- StdDev(Close,100)/4 bands.")]
        public bool ShowEma50Cloud { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Cloud Fill Color", Order = 11, GroupName = "2. EMAs")]
        [XmlIgnore]
        public System.Windows.Media.Brush CloudFillColor { get; set; }
        [Browsable(false)]
        public string CloudFillColorSerializable { get => Serialize.BrushToString(CloudFillColor); set => CloudFillColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Cloud Fill Opacity %", Order = 12, GroupName = "2. EMAs")]
        public int CloudFillOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show EMA 200", Order = 13, GroupName = "2. EMAs")]
        public bool ShowEma200 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA 200 Color", Order = 14, GroupName = "2. EMAs")]
        [XmlIgnore]
        public System.Windows.Media.Brush Ema200Color { get; set; }
        [Browsable(false)]
        public string Ema200ColorSerializable { get => Serialize.BrushToString(Ema200Color); set => Ema200Color = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "EMA 200 Thickness", Order = 15, GroupName = "2. EMAs")]
        public int Ema200Thickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show EMA 800", Order = 16, GroupName = "2. EMAs")]
        public bool ShowEma800 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA 800 Color", Order = 17, GroupName = "2. EMAs")]
        [XmlIgnore]
        public System.Windows.Media.Brush Ema800Color { get; set; }
        [Browsable(false)]
        public string Ema800ColorSerializable { get => Serialize.BrushToString(Ema800Color); set => Ema800Color = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "EMA 800 Thickness", Order = 18, GroupName = "2. EMAs")]
        public int Ema800Thickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show EMA Labels", Order = 19, GroupName = "2. EMAs")]
        public bool ShowEmaLabels { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 3. Pivot Points

        [NinjaScriptProperty]
        [Display(Name = "Show Pivot PP", Order = 1, GroupName = "3. Pivot Points")]
        public bool ShowPP { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show R1/S1", Order = 2, GroupName = "3. Pivot Points")]
        public bool ShowLevel1 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show R2/S2", Order = 3, GroupName = "3. Pivot Points")]
        public bool ShowLevel2 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show R3/S3", Order = 4, GroupName = "3. Pivot Points")]
        public bool ShowLevel3 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Pivot Labels", Order = 5, GroupName = "3. Pivot Points")]
        public bool ShowPivotLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Pivot Line Style", Order = 6, GroupName = "3. Pivot Points")]
        public IQSLineStyle PivotLineStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PP Color", Order = 7, GroupName = "3. Pivot Points")]
        [XmlIgnore]
        public System.Windows.Media.Brush PPColor { get; set; }
        [Browsable(false)]
        public string PPColorSerializable { get => Serialize.BrushToString(PPColor); set => PPColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "R Levels Color", Order = 8, GroupName = "3. Pivot Points")]
        [XmlIgnore]
        public System.Windows.Media.Brush RLevelColor { get; set; }
        [Browsable(false)]
        public string RLevelColorSerializable { get => Serialize.BrushToString(RLevelColor); set => RLevelColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "S Levels Color", Order = 9, GroupName = "3. Pivot Points")]
        [XmlIgnore]
        public System.Windows.Media.Brush SLevelColor { get; set; }
        [Browsable(false)]
        public string SLevelColorSerializable { get => Serialize.BrushToString(SLevelColor); set => SLevelColor = Serialize.StringToBrush(value); }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 4. M Levels

        [NinjaScriptProperty]
        [Display(Name = "Show M Levels", Order = 1, GroupName = "4. M Levels",
            Description = "Show midpoint levels between each pair of pivot levels.")]
        public bool ShowMLevels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show M Labels", Order = 2, GroupName = "4. M Levels")]
        public bool ShowMLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "M Level Color", Order = 3, GroupName = "4. M Levels")]
        [XmlIgnore]
        public System.Windows.Media.Brush MLevelColor { get; set; }
        [Browsable(false)]
        public string MLevelColorSerializable { get => Serialize.BrushToString(MLevelColor); set => MLevelColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "M Level Opacity %", Order = 4, GroupName = "4. M Levels")]
        public int MLevelOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "M Line Style", Order = 5, GroupName = "4. M Levels")]
        public IQSLineStyle MLevelLineStyle { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 5. Yesterday / Last Week Hi/Lo

        [NinjaScriptProperty]
        [Display(Name = "Show Yesterday Hi/Lo", Order = 1, GroupName = "5. Yesterday / Last Week Hi/Lo")]
        public bool ShowYesterday { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Yesterday Labels", Order = 2, GroupName = "5. Yesterday / Last Week Hi/Lo")]
        public bool ShowYesterdayLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Yesterday Color", Order = 3, GroupName = "5. Yesterday / Last Week Hi/Lo")]
        [XmlIgnore]
        public System.Windows.Media.Brush YesterdayColor { get; set; }
        [Browsable(false)]
        public string YesterdayColorSerializable { get => Serialize.BrushToString(YesterdayColor); set => YesterdayColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "Show Last Week Hi/Lo", Order = 4, GroupName = "5. Yesterday / Last Week Hi/Lo")]
        public bool ShowLastWeek { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Last Week Labels", Order = 5, GroupName = "5. Yesterday / Last Week Hi/Lo")]
        public bool ShowLastWeekLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Last Week Color", Order = 6, GroupName = "5. Yesterday / Last Week Hi/Lo")]
        [XmlIgnore]
        public System.Windows.Media.Brush LastWeekColor { get; set; }
        [Browsable(false)]
        public string LastWeekColorSerializable { get => Serialize.BrushToString(LastWeekColor); set => LastWeekColor = Serialize.StringToBrush(value); }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 6. ADR

        [NinjaScriptProperty]
        [Display(Name = "Show ADR", Order = 1, GroupName = "6. ADR",
            Description = "Average Daily Range high/low bands.")]
        public bool ShowAdr { get; set; }

        [NinjaScriptProperty]
        [Range(1, 31)]
        [Display(Name = "ADR Length (days)", Order = 2, GroupName = "6. ADR")]
        public int AdrLength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ADR Use Daily Open", Order = 3, GroupName = "6. ADR",
            Description = "When enabled: ADR High = DailyOpen + ADR/2, Low = DailyOpen - ADR/2.")]
        public bool AdrUseDailyOpen { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show ADR 50%", Order = 4, GroupName = "6. ADR")]
        public bool ShowAdr50 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show ADR Labels", Order = 5, GroupName = "6. ADR")]
        public bool ShowAdrLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show ADR Range Label", Order = 6, GroupName = "6. ADR",
            Description = "Display the current ADR pip value as a label.")]
        public bool ShowAdrRangeLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ADR Color", Order = 7, GroupName = "6. ADR")]
        [XmlIgnore]
        public System.Windows.Media.Brush AdrColor { get; set; }
        [Browsable(false)]
        public string AdrColorSerializable { get => Serialize.BrushToString(AdrColor); set => AdrColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "ADR Opacity %", Order = 8, GroupName = "6. ADR")]
        public int AdrOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ADR Line Style", Order = 9, GroupName = "6. ADR")]
        public IQSLineStyle AdrLineStyle { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 7. AWR

        [NinjaScriptProperty]
        [Display(Name = "Show AWR", Order = 1, GroupName = "7. AWR",
            Description = "Average Weekly Range high/low bands.")]
        public bool ShowAwr { get; set; }

        [NinjaScriptProperty]
        [Range(1, 52)]
        [Display(Name = "AWR Length (weeks)", Order = 2, GroupName = "7. AWR")]
        public int AwrLength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show AWR 50%", Order = 3, GroupName = "7. AWR")]
        public bool ShowAwr50 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show AWR Labels", Order = 4, GroupName = "7. AWR")]
        public bool ShowAwrLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AWR Color", Order = 5, GroupName = "7. AWR")]
        [XmlIgnore]
        public System.Windows.Media.Brush AwrColor { get; set; }
        [Browsable(false)]
        public string AwrColorSerializable { get => Serialize.BrushToString(AwrColor); set => AwrColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "AWR Opacity %", Order = 6, GroupName = "7. AWR")]
        public int AwrOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AWR Line Style", Order = 7, GroupName = "7. AWR")]
        public IQSLineStyle AwrLineStyle { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 8. AMR

        [NinjaScriptProperty]
        [Display(Name = "Show AMR", Order = 1, GroupName = "8. AMR",
            Description = "Average Monthly Range high/low bands.")]
        public bool ShowAmr { get; set; }

        [NinjaScriptProperty]
        [Range(1, 24)]
        [Display(Name = "AMR Length (months)", Order = 2, GroupName = "8. AMR")]
        public int AmrLength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show AMR 50%", Order = 3, GroupName = "8. AMR")]
        public bool ShowAmr50 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show AMR Labels", Order = 4, GroupName = "8. AMR")]
        public bool ShowAmrLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AMR Color", Order = 5, GroupName = "8. AMR")]
        [XmlIgnore]
        public System.Windows.Media.Brush AmrColor { get; set; }
        [Browsable(false)]
        public string AmrColorSerializable { get => Serialize.BrushToString(AmrColor); set => AmrColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "AMR Opacity %", Order = 6, GroupName = "8. AMR")]
        public int AmrOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "AMR Line Style", Order = 7, GroupName = "8. AMR")]
        public IQSLineStyle AmrLineStyle { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 9. RD Hi/Lo

        [NinjaScriptProperty]
        [Display(Name = "Show RD Hi/Lo", Order = 1, GroupName = "9. RD Hi/Lo",
            Description = "Range Daily High/Low bands.")]
        public bool ShowRd { get; set; }

        [NinjaScriptProperty]
        [Range(1, 31)]
        [Display(Name = "RD Length (days)", Order = 2, GroupName = "9. RD Hi/Lo")]
        public int RdLength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show RD Labels", Order = 3, GroupName = "9. RD Hi/Lo")]
        public bool ShowRdLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RD Color", Order = 4, GroupName = "9. RD Hi/Lo")]
        [XmlIgnore]
        public System.Windows.Media.Brush RdColor { get; set; }
        [Browsable(false)]
        public string RdColorSerializable { get => Serialize.BrushToString(RdColor); set => RdColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "RD Opacity %", Order = 5, GroupName = "9. RD Hi/Lo")]
        public int RdOpacity { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 10. RW Hi/Lo

        [NinjaScriptProperty]
        [Display(Name = "Show RW Hi/Lo", Order = 1, GroupName = "10. RW Hi/Lo",
            Description = "Range Weekly High/Low bands.")]
        public bool ShowRw { get; set; }

        [NinjaScriptProperty]
        [Range(1, 52)]
        [Display(Name = "RW Length (weeks)", Order = 2, GroupName = "10. RW Hi/Lo")]
        public int RwLength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show RW Labels", Order = 3, GroupName = "10. RW Hi/Lo")]
        public bool ShowRwLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RW Color", Order = 4, GroupName = "10. RW Hi/Lo")]
        [XmlIgnore]
        public System.Windows.Media.Brush RwColor { get; set; }
        [Browsable(false)]
        public string RwColorSerializable { get => Serialize.BrushToString(RwColor); set => RwColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "RW Opacity %", Order = 5, GroupName = "10. RW Hi/Lo")]
        public int RwOpacity { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 11. Range Table

        [NinjaScriptProperty]
        [Display(Name = "Show Range Table", Order = 1, GroupName = "11. Range Table",
            Description = "Show ADR/AWR/AMR statistics dashboard.")]
        public bool ShowRangeTable { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show ADR in Pips", Order = 2, GroupName = "11. Range Table")]
        public bool TableShowAdrPips { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show ADR in Currency", Order = 3, GroupName = "11. Range Table")]
        public bool TableShowAdrCurrency { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show RD Pips", Order = 4, GroupName = "11. Range Table")]
        public bool TableShowRdPips { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Table Position", Order = 5, GroupName = "11. Range Table")]
        public IQSDashboardPosition TablePosition { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Table Background Color", Order = 6, GroupName = "11. Range Table")]
        [XmlIgnore]
        public System.Windows.Media.Brush TableBgColor { get; set; }
        [Browsable(false)]
        public string TableBgColorSerializable { get => Serialize.BrushToString(TableBgColor); set => TableBgColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "Table Text Color", Order = 7, GroupName = "11. Range Table")]
        [XmlIgnore]
        public System.Windows.Media.Brush TableTextColor { get; set; }
        [Browsable(false)]
        public string TableTextColorSerializable { get => Serialize.BrushToString(TableTextColor); set => TableTextColor = Serialize.StringToBrush(value); }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 12. Daily Open

        [NinjaScriptProperty]
        [Display(Name = "Show Daily Open", Order = 1, GroupName = "12. Daily Open",
            Description = "Draw a horizontal line at the current day's open price.")]
        public bool ShowDailyOpen { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Historical Daily Opens", Order = 2, GroupName = "12. Daily Open")]
        public bool ShowHistoricalDailyOpens { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Daily Open Color", Order = 3, GroupName = "12. Daily Open")]
        [XmlIgnore]
        public System.Windows.Media.Brush DailyOpenColor { get; set; }
        [Browsable(false)]
        public string DailyOpenColorSerializable { get => Serialize.BrushToString(DailyOpenColor); set => DailyOpenColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "Daily Open Line Style", Order = 4, GroupName = "12. Daily Open")]
        public IQSLineStyle DailyOpenLineStyle { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 13. Sessions — London

        [NinjaScriptProperty]
        [Display(Name = "Show London Session", Order = 1, GroupName = "13. Sessions — London")]
        public bool ShowLondon { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "London Label", Order = 2, GroupName = "13. Sessions — London")]
        public string LondonLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "London Show Label", Order = 3, GroupName = "13. Sessions — London")]
        public bool LondonShowLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "London Show Opening Range", Order = 4, GroupName = "13. Sessions — London")]
        public bool LondonShowOpeningRange { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "London Box Color", Order = 5, GroupName = "13. Sessions — London")]
        [XmlIgnore]
        public System.Windows.Media.Brush LondonColor { get; set; }
        [Browsable(false)]
        public string LondonColorSerializable { get => Serialize.BrushToString(LondonColor); set => LondonColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "London Opacity %", Order = 6, GroupName = "13. Sessions — London")]
        public int LondonOpacity { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 14. Sessions — New York

        [NinjaScriptProperty]
        [Display(Name = "Show New York Session", Order = 1, GroupName = "14. Sessions — New York")]
        public bool ShowNewYork { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "New York Label", Order = 2, GroupName = "14. Sessions — New York")]
        public string NewYorkLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "New York Show Label", Order = 3, GroupName = "14. Sessions — New York")]
        public bool NewYorkShowLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "New York Show Opening Range", Order = 4, GroupName = "14. Sessions — New York")]
        public bool NewYorkShowOpeningRange { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "New York Box Color", Order = 5, GroupName = "14. Sessions — New York")]
        [XmlIgnore]
        public System.Windows.Media.Brush NewYorkColor { get; set; }
        [Browsable(false)]
        public string NewYorkColorSerializable { get => Serialize.BrushToString(NewYorkColor); set => NewYorkColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "New York Opacity %", Order = 6, GroupName = "14. Sessions — New York")]
        public int NewYorkOpacity { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 15. Sessions — Tokyo

        [NinjaScriptProperty]
        [Display(Name = "Show Tokyo Session", Order = 1, GroupName = "15. Sessions — Tokyo")]
        public bool ShowTokyo { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Tokyo Label", Order = 2, GroupName = "15. Sessions — Tokyo")]
        public string TokyoLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Tokyo Show Label", Order = 3, GroupName = "15. Sessions — Tokyo")]
        public bool TokyoShowLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Tokyo Show Opening Range", Order = 4, GroupName = "15. Sessions — Tokyo")]
        public bool TokyoShowOpeningRange { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Tokyo Box Color", Order = 5, GroupName = "15. Sessions — Tokyo")]
        [XmlIgnore]
        public System.Windows.Media.Brush TokyoColor { get; set; }
        [Browsable(false)]
        public string TokyoColorSerializable { get => Serialize.BrushToString(TokyoColor); set => TokyoColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Tokyo Opacity %", Order = 6, GroupName = "15. Sessions — Tokyo")]
        public int TokyoOpacity { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 16. Sessions — Hong Kong

        [NinjaScriptProperty]
        [Display(Name = "Show Hong Kong Session", Order = 1, GroupName = "16. Sessions — Hong Kong")]
        public bool ShowHongKong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Hong Kong Label", Order = 2, GroupName = "16. Sessions — Hong Kong")]
        public string HongKongLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Hong Kong Show Label", Order = 3, GroupName = "16. Sessions — Hong Kong")]
        public bool HongKongShowLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Hong Kong Show Opening Range", Order = 4, GroupName = "16. Sessions — Hong Kong")]
        public bool HongKongShowOpeningRange { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Hong Kong Box Color", Order = 5, GroupName = "16. Sessions — Hong Kong")]
        [XmlIgnore]
        public System.Windows.Media.Brush HongKongColor { get; set; }
        [Browsable(false)]
        public string HongKongColorSerializable { get => Serialize.BrushToString(HongKongColor); set => HongKongColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Hong Kong Opacity %", Order = 6, GroupName = "16. Sessions — Hong Kong")]
        public int HongKongOpacity { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 17. Sessions — Sydney

        [NinjaScriptProperty]
        [Display(Name = "Show Sydney Session", Order = 1, GroupName = "17. Sessions — Sydney")]
        public bool ShowSydney { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Sydney Label", Order = 2, GroupName = "17. Sessions — Sydney")]
        public string SydneyLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Sydney Show Label", Order = 3, GroupName = "17. Sessions — Sydney")]
        public bool SydneyShowLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Sydney Show Opening Range", Order = 4, GroupName = "17. Sessions — Sydney")]
        public bool SydneyShowOpeningRange { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Sydney Box Color", Order = 5, GroupName = "17. Sessions — Sydney")]
        [XmlIgnore]
        public System.Windows.Media.Brush SydneyColor { get; set; }
        [Browsable(false)]
        public string SydneyColorSerializable { get => Serialize.BrushToString(SydneyColor); set => SydneyColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Sydney Opacity %", Order = 6, GroupName = "17. Sessions — Sydney")]
        public int SydneyOpacity { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 18. Sessions — EU Brinks

        [NinjaScriptProperty]
        [Display(Name = "Show EU Brinks Session", Order = 1, GroupName = "18. Sessions — EU Brinks")]
        public bool ShowEuBrinks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EU Brinks Label", Order = 2, GroupName = "18. Sessions — EU Brinks")]
        public string EuBrinksLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EU Brinks Show Label", Order = 3, GroupName = "18. Sessions — EU Brinks")]
        public bool EuBrinksShowLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EU Brinks Show Opening Range", Order = 4, GroupName = "18. Sessions — EU Brinks")]
        public bool EuBrinksShowOpeningRange { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EU Brinks Box Color", Order = 5, GroupName = "18. Sessions — EU Brinks")]
        [XmlIgnore]
        public System.Windows.Media.Brush EuBrinksColor { get; set; }
        [Browsable(false)]
        public string EuBrinksColorSerializable { get => Serialize.BrushToString(EuBrinksColor); set => EuBrinksColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "EU Brinks Opacity %", Order = 6, GroupName = "18. Sessions — EU Brinks")]
        public int EuBrinksOpacity { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 19. Sessions — US Brinks

        [NinjaScriptProperty]
        [Display(Name = "Show US Brinks Session", Order = 1, GroupName = "19. Sessions — US Brinks")]
        public bool ShowUsBrinks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "US Brinks Label", Order = 2, GroupName = "19. Sessions — US Brinks")]
        public string UsBrinksLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "US Brinks Show Label", Order = 3, GroupName = "19. Sessions — US Brinks")]
        public bool UsBrinksShowLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "US Brinks Show Opening Range", Order = 4, GroupName = "19. Sessions — US Brinks")]
        public bool UsBrinksShowOpeningRange { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "US Brinks Box Color", Order = 5, GroupName = "19. Sessions — US Brinks")]
        [XmlIgnore]
        public System.Windows.Media.Brush UsBrinksColor { get; set; }
        [Browsable(false)]
        public string UsBrinksColorSerializable { get => Serialize.BrushToString(UsBrinksColor); set => UsBrinksColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "US Brinks Opacity %", Order = 6, GroupName = "19. Sessions — US Brinks")]
        public int UsBrinksOpacity { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 20. Sessions — Frankfurt

        [NinjaScriptProperty]
        [Display(Name = "Show Frankfurt Session", Order = 1, GroupName = "20. Sessions — Frankfurt")]
        public bool ShowFrankfurt { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Frankfurt Label", Order = 2, GroupName = "20. Sessions — Frankfurt")]
        public string FrankfurtLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Frankfurt Show Label", Order = 3, GroupName = "20. Sessions — Frankfurt")]
        public bool FrankfurtShowLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Frankfurt Show Opening Range", Order = 4, GroupName = "20. Sessions — Frankfurt")]
        public bool FrankfurtShowOpeningRange { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Frankfurt Box Color", Order = 5, GroupName = "20. Sessions — Frankfurt")]
        [XmlIgnore]
        public System.Windows.Media.Brush FrankfurtColor { get; set; }
        [Browsable(false)]
        public string FrankfurtColorSerializable { get => Serialize.BrushToString(FrankfurtColor); set => FrankfurtColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Frankfurt Opacity %", Order = 6, GroupName = "20. Sessions — Frankfurt")]
        public int FrankfurtOpacity { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 21. Psy Levels

        [NinjaScriptProperty]
        [Display(Name = "Show Weekly Psy Levels", Order = 1, GroupName = "21. Psy Levels",
            Description = "Psychological weekly high/low levels from Sydney/Tokyo session start.")]
        public bool ShowPsyLevels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Psy Use Crypto (Sydney) Start", Order = 2, GroupName = "21. Psy Levels",
            Description = "Use Sydney session start (crypto markets). When off, uses Tokyo start (forex).")]
        public bool PsyUseSydney { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Psy Labels", Order = 3, GroupName = "21. Psy Levels")]
        public bool ShowPsyLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Psy Level Color", Order = 4, GroupName = "21. Psy Levels")]
        [XmlIgnore]
        public System.Windows.Media.Brush PsyColor { get; set; }
        [Browsable(false)]
        public string PsyColorSerializable { get => Serialize.BrushToString(PsyColor); set => PsyColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Psy Opacity %", Order = 5, GroupName = "21. Psy Levels")]
        public int PsyOpacity { get; set; }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Parameters — 22. DST Table

        [NinjaScriptProperty]
        [Display(Name = "Show DST Table", Order = 1, GroupName = "22. DST Table",
            Description = "Show reference table of DST rules for each session.")]
        public bool ShowDstTable { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "DST Table Position", Order = 2, GroupName = "22. DST Table")]
        public IQSDashboardPosition DstTablePosition { get; set; }

        #endregion

        // ════════════════════════════════════════════════════════════════════════
        #region State management — OnStateChange

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "IQ Sessions GPU — Market sessions, pivots, EMAs, and range levels with GPU rendering. Companion to IQCandlesGPU.";
                Name                     = "IQSessionsGPU";
                Calculate                = Calculate.OnBarClose;
                IsOverlay                = true;
                DisplayInDataBox         = false;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = false;

                // 1. Label Offsets
                LabelOffsetBars  = 2;
                LabelOffsetTicks = 1;

                // 2. EMAs
                ShowEma5         = true;
                var ema5Brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(254, 234, 74));
                ema5Brush.Freeze();
                Ema5Color        = ema5Brush;
                Ema5Thickness    = 1;
                ShowEma13        = true;
                var ema13Brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(253, 84, 87));
                ema13Brush.Freeze();
                Ema13Color       = ema13Brush;
                Ema13Thickness   = 1;
                ShowEma50        = true;
                var ema50Brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(31, 188, 211));
                ema50Brush.Freeze();
                Ema50Color       = ema50Brush;
                Ema50Thickness   = 2;
                ShowEma50Cloud   = true;
                var cloudBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(155, 47, 174));
                cloudBrush.Freeze();
                CloudFillColor   = cloudBrush;
                CloudFillOpacity = 24;
                ShowEma200       = true;
                Ema200Color      = Brushes.White;
                Ema200Thickness  = 2;
                ShowEma800       = true;
                var ema800Brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(50, 34, 144));
                ema800Brush.Freeze();
                Ema800Color      = ema800Brush;
                Ema800Thickness  = 2;
                ShowEmaLabels    = true;

                // 3. Pivot Points
                ShowPP           = true;
                ShowLevel1       = true;
                ShowLevel2       = true;
                ShowLevel3       = false;
                ShowPivotLabels  = true;
                PivotLineStyle   = IQSLineStyle.Dashed;
                PPColor          = Brushes.Yellow;
                RLevelColor      = Brushes.LimeGreen;
                SLevelColor      = Brushes.IndianRed;

                // 4. M Levels
                ShowMLevels      = true;
                ShowMLabels      = true;
                MLevelColor      = Brushes.White;
                MLevelOpacity    = 50;
                MLevelLineStyle  = IQSLineStyle.Dotted;

                // 5. Yesterday / Last Week Hi/Lo
                ShowYesterday        = true;
                ShowYesterdayLabels  = true;
                YesterdayColor       = Brushes.CornflowerBlue;
                ShowLastWeek         = true;
                ShowLastWeekLabels   = true;
                LastWeekColor        = Brushes.MediumSeaGreen;

                // 6. ADR
                ShowAdr          = true;
                AdrLength        = 14;
                AdrUseDailyOpen  = false;
                ShowAdr50        = true;
                ShowAdrLabels    = true;
                ShowAdrRangeLabel= true;
                AdrColor         = Brushes.DodgerBlue;
                AdrOpacity       = 70;
                AdrLineStyle     = IQSLineStyle.Dashed;

                // 7. AWR
                ShowAwr          = true;
                AwrLength        = 4;
                ShowAwr50        = true;
                ShowAwrLabels    = true;
                AwrColor         = Brushes.Orange;
                AwrOpacity       = 50;
                AwrLineStyle     = IQSLineStyle.Dashed;

                // 8. AMR
                ShowAmr          = true;
                AmrLength        = 6;
                ShowAmr50        = true;
                ShowAmrLabels    = true;
                AmrColor         = Brushes.IndianRed;
                AmrOpacity       = 50;
                AmrLineStyle     = IQSLineStyle.Dashed;

                // 9. RD
                ShowRd           = true;
                RdLength         = 15;
                ShowRdLabels     = true;
                RdColor          = Brushes.Crimson;
                RdOpacity        = 30;

                // 10. RW
                ShowRw           = true;
                RwLength         = 13;
                ShowRwLabels     = true;
                RwColor          = Brushes.SteelBlue;
                RwOpacity        = 30;

                // 11. Range Table
                ShowRangeTable      = true;
                TableShowAdrPips    = true;
                TableShowAdrCurrency= false;
                TableShowRdPips     = true;
                TablePosition       = IQSDashboardPosition.TopRight;
                TableBgColor        = Brushes.Black;
                TableTextColor      = Brushes.White;

                // 12. Daily Open
                ShowDailyOpen            = true;
                ShowHistoricalDailyOpens = false;
                var dailyOpenBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(254, 234, 78));
                dailyOpenBrush.Freeze();
                DailyOpenColor           = dailyOpenBrush;
                DailyOpenLineStyle       = IQSLineStyle.Solid;

                // 13. London  UTC 08:00–16:30 (UK DST -1hr)
                ShowLondon             = true;
                LondonLabel            = "London";
                LondonShowLabel        = true;
                LondonShowOpeningRange = true;
                LondonColor            = System.Windows.Media.Brushes.SteelBlue;
                LondonOpacity          = 15;

                // 14. New York  UTC 14:30–21:00 (US DST -1hr)
                ShowNewYork             = true;
                NewYorkLabel            = "New York";
                NewYorkShowLabel        = true;
                NewYorkShowOpeningRange = true;
                NewYorkColor            = System.Windows.Media.Brushes.ForestGreen;
                NewYorkOpacity          = 15;

                // 15. Tokyo  UTC 00:00–06:00 (no DST)
                ShowTokyo             = true;
                TokyoLabel            = "Tokyo";
                TokyoShowLabel        = true;
                TokyoShowOpeningRange = true;
                TokyoColor            = System.Windows.Media.Brushes.Crimson;
                TokyoOpacity          = 15;

                // 16. Hong Kong  UTC 01:30–08:00 (no DST)
                ShowHongKong             = true;
                HongKongLabel            = "Hong Kong";
                HongKongShowLabel        = true;
                HongKongShowOpeningRange = true;
                HongKongColor            = System.Windows.Media.Brushes.Orange;
                HongKongOpacity          = 15;

                // 17. Sydney  UTC 22:00–06:00 (AU DST -1hr)
                ShowSydney             = true;
                SydneyLabel            = "Sydney";
                SydneyShowLabel        = true;
                SydneyShowOpeningRange = true;
                SydneyColor            = System.Windows.Media.Brushes.DarkViolet;
                SydneyOpacity          = 15;

                // 18. EU Brinks  UTC 08:00–09:00 (UK DST)
                ShowEuBrinks             = true;
                EuBrinksLabel            = "EU Brinks";
                EuBrinksShowLabel        = true;
                EuBrinksShowOpeningRange = true;
                EuBrinksColor            = System.Windows.Media.Brushes.DeepSkyBlue;
                EuBrinksOpacity          = 20;

                // 19. US Brinks  UTC 14:00–15:00 (US DST)
                ShowUsBrinks             = true;
                UsBrinksLabel            = "US Brinks";
                UsBrinksShowLabel        = true;
                UsBrinksShowOpeningRange = true;
                UsBrinksColor            = System.Windows.Media.Brushes.LimeGreen;
                UsBrinksOpacity          = 20;

                // 20. Frankfurt  UTC 07:00–16:30 (UK DST)
                ShowFrankfurt             = true;
                FrankfurtLabel            = "Frankfurt";
                FrankfurtShowLabel        = true;
                FrankfurtShowOpeningRange = true;
                FrankfurtColor            = System.Windows.Media.Brushes.Gold;
                FrankfurtOpacity          = 12;

                // 21. Psy Levels
                ShowPsyLevels  = true;
                PsyUseSydney   = false;
                ShowPsyLabels  = true;
                PsyColor       = Brushes.Orange;
                PsyOpacity     = 30;

                // 22. DST Table
                ShowDstTable     = false;
                DstTablePosition = IQSDashboardPosition.BottomLeft;
            }
            else if (State == State.DataLoaded)
            {
                dailyRanges   = new Queue<double>(32);
                weeklyRanges  = new Queue<double>(56);
                monthlyRanges = new Queue<double>(26);
                rdRanges      = new Queue<double>(32);
                rwRanges      = new Queue<double>(56);

                sessionBoxes   = new List<SessionBox>(200);
                activeSessions = new SessionBox[8];

                prevDayLoaded  = false;
                weekDataLoaded = false;
                currentDayOfWeek = -1;
                monthDataLoaded  = false;
                currentMonth     = -1;

                alertAdrHighFired = alertAdrLowFired = false;
                alertAwrHighFired = alertAwrLowFired = false;
                alertAmrHighFired = alertAmrLowFired = false;
            }
            else if (State == State.Terminated)
            {
                DisposeDXResources();
            }
        }

        // ── Helper: convert System.Windows.Media.Color to SolidColorBrush ────
        // (Extension usage avoided for NT8 compatibility; use inline new SolidColorBrush)

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region OnBarUpdate — indicator computations

        protected override void OnBarUpdate()
        {
            if (CurrentBar < MIN_BARS_REQUIRED)
                return;

            // ── 1. EMAs ───────────────────────────────────────────────────────
            ema5Val   = EMA(5)[0];
            ema13Val  = EMA(13)[0];
            ema50Val  = EMA(50)[0];
            ema200Val = EMA(200)[0];
            ema800Val = EMA(800)[0];

            // EMA 50 cloud bands: ema50 ± StdDev(Close, 100) / 4
            if (ShowEma50Cloud && CurrentBar >= 100)
            {
                double sd = StdDev(Close, 100)[0];
                ema50Upper = ema50Val + sd / 4.0;
                ema50Lower = ema50Val - sd / 4.0;
            }

            // ── 2. Day / week / month tracking ───────────────────────────────
            if (IsFirstTickOfBar || Calculate == Calculate.OnBarClose)
            {
                DateTime barTime = Time[0];

                // Detect new trading day
                DateTime prevDate = CurrentBar > 0 ? Time[1].Date : barTime.Date;
                bool newDay = barTime.Date != prevDate;
                if (newDay || CurrentBar == 0)
                {
                    // Archive yesterday's data
                    if (prevDayLoaded)
                    {
                        yesterdayHigh  = dayHigh;
                        yesterdayLow   = dayLow;
                        prevDayHigh    = dayHigh;
                        prevDayLow     = dayLow;
                        prevDayClose   = dayClose;

                        // Store daily range for ADR/RD
                        double dRange = dayHigh - dayLow;
                        if (dailyRanges.Count >= AdrLength) dailyRanges.Dequeue();
                        dailyRanges.Enqueue(dRange);
                        if (rdRanges.Count >= RdLength) rdRanges.Dequeue();
                        rdRanges.Enqueue(dRange);

                        // Compute new pivots
                        ComputePivots();
                    }

                    // Reset day tracking
                    dayHigh   = High[0];
                    dayLow    = Low[0];
                    dayClose  = Close[0];
                    dailyOpen = Open[0];
                    prevDayLoaded = true;

                    // Reset ADR alerts
                    alertAdrHighFired = alertAdrLowFired = false;
                }
                else
                {
                    if (High[0] > dayHigh) dayHigh = High[0];
                    if (Low[0]  < dayLow)  dayLow  = Low[0];
                    dayClose = Close[0];
                }

                // Detect new week (Monday or day-of-week rolls over)
                int dow = (int)barTime.DayOfWeek;
                if (dow != currentDayOfWeek)
                {
                    bool newWeek = (dow < currentDayOfWeek) || (currentDayOfWeek == -1);
                    if (newWeek && weekDataLoaded)
                    {
                        lastWeekHigh = weekHigh;
                        lastWeekLow  = weekLow;

                        double wRange = weekHigh - weekLow;
                        if (weeklyRanges.Count >= AwrLength) weeklyRanges.Dequeue();
                        weeklyRanges.Enqueue(wRange);
                        if (rwRanges.Count >= RwLength) rwRanges.Dequeue();
                        rwRanges.Enqueue(wRange);

                        alertAwrHighFired = alertAwrLowFired = false;

                        // Psy levels reset at week start
                        psyWeekHigh    = High[0];
                        psyWeekLow     = Low[0];
                        psyWeekStartBar = CurrentBar;
                    }
                    else if (!weekDataLoaded)
                    {
                        weekHigh = High[0];
                        weekLow  = Low[0];
                        psyWeekHigh    = High[0];
                        psyWeekLow     = Low[0];
                        psyWeekStartBar = CurrentBar;
                    }

                    currentDayOfWeek = dow;
                    weekDataLoaded   = true;
                }

                // Update running week high/low
                if (weekDataLoaded)
                {
                    if (High[0] > weekHigh) weekHigh = High[0];
                    if (Low[0]  < weekLow)  weekLow  = Low[0];
                }

                // Detect new calendar month
                int mon = barTime.Month;
                if (mon != currentMonth)
                {
                    if (monthDataLoaded && monthHigh > 0 && monthLow > 0)
                    {
                        double mRange = monthHigh - monthLow;
                        if (monthlyRanges.Count >= AmrLength) monthlyRanges.Dequeue();
                        monthlyRanges.Enqueue(mRange);
                        alertAmrHighFired = alertAmrLowFired = false;
                    }
                    monthHigh       = High[0];
                    monthLow        = Low[0];
                    currentMonth    = mon;
                    monthDataLoaded = true;
                }
                else if (monthDataLoaded)
                {
                    if (High[0] > monthHigh) monthHigh = High[0];
                    if (Low[0]  < monthLow)  monthLow  = Low[0];
                }
            }

            // ── 3. Compute ADR / AWR / AMR / RD / RW values ──────────────────
            if (dailyRanges.Count > 0)
            {
                adrValue = dailyRanges.Average();
                double todayRange = dayHigh - dayLow;

                if (AdrUseDailyOpen && dailyOpen > 0)
                {
                    adrHigh = dailyOpen + adrValue / 2.0;
                    adrLow  = dailyOpen - adrValue / 2.0;
                }
                else
                {
                    adrHigh = dayLow + adrValue;
                    adrLow  = dayHigh - adrValue;
                    // Expand symmetrically
                    double slack = (adrValue - todayRange) / 2.0;
                    adrHigh = dayHigh + slack;
                    adrLow  = dayLow  - slack;
                }
            }

            if (weeklyRanges.Count > 0)
            {
                awrValue = weeklyRanges.Average();
                double wSlack = (awrValue - (weekHigh - weekLow)) / 2.0;
                awrHigh  = weekHigh + wSlack;
                awrLow   = weekLow  - wSlack;
            }

            if (monthlyRanges.Count > 0)
            {
                amrValue = monthlyRanges.Average();
                // AMR uses daily open as centre for monthly bands
                if (dailyOpen > 0)
                {
                    amrHigh = dailyOpen + amrValue / 2.0;
                    amrLow  = dailyOpen - amrValue / 2.0;
                }
            }

            if (rdRanges.Count > 0)
            {
                rdValue = rdRanges.Average();
                double rdSlack = (rdValue - (dayHigh - dayLow)) / 2.0;
                rdHigh = dayHigh + rdSlack;
                rdLow  = dayLow  - rdSlack;
            }

            if (rwRanges.Count > 0)
            {
                rwValue = rwRanges.Average();
                double rwSlack = (rwValue - (weekHigh - weekLow)) / 2.0;
                rwHigh = weekHigh + rwSlack;
                rwLow  = weekLow  - rwSlack;
            }

            // ── 4. Session tracking ───────────────────────────────────────────
            if (IsFirstTickOfBar || Calculate == Calculate.OnBarClose)
            {
                UpdateSessions(Time[0]);

                // Psy level tracking
                if (psyWeekStartBar > 0)
                {
                    if (High[0] > psyWeekHigh) psyWeekHigh = High[0];
                    if (Low[0]  < psyWeekLow)  psyWeekLow  = Low[0];
                }
            }

            // ── 5. Alerts ─────────────────────────────────────────────────────
            if (IsFirstTickOfBar && adrValue > 0)
            {
                double c = Close[0];
                if (!alertAdrHighFired && c >= adrHigh)
                {
                    alertAdrHighFired = true;
                    Alert("IQS_AdrHigh", Priority.Medium, "IQ Sessions: ADR High reached",
                        NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav", 10, Brushes.DodgerBlue, Brushes.Black);
                }
                if (!alertAdrLowFired && c <= adrLow)
                {
                    alertAdrLowFired = true;
                    Alert("IQS_AdrLow", Priority.Medium, "IQ Sessions: ADR Low reached",
                        NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav", 10, Brushes.DodgerBlue, Brushes.Black);
                }
                if (awrValue > 0)
                {
                    if (!alertAwrHighFired && c >= awrHigh)
                    {
                        alertAwrHighFired = true;
                        Alert("IQS_AwrHigh", Priority.Low, "IQ Sessions: AWR High reached",
                            NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10, Brushes.Orange, Brushes.Black);
                    }
                    if (!alertAwrLowFired && c <= awrLow)
                    {
                        alertAwrLowFired = true;
                        Alert("IQS_AwrLow", Priority.Low, "IQ Sessions: AWR Low reached",
                            NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10, Brushes.Orange, Brushes.Black);
                    }
                }
                if (amrValue > 0 && !alertAmrHighFired && c >= amrHigh)
                {
                    alertAmrHighFired = true;
                    Alert("IQS_AmrHigh", Priority.Low, "IQ Sessions: AMR High reached",
                        NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert3.wav", 10, Brushes.IndianRed, Brushes.Black);
                }
                if (amrValue > 0 && !alertAmrLowFired && c <= amrLow)
                {
                    alertAmrLowFired = true;
                    Alert("IQS_AmrLow", Priority.Low, "IQ Sessions: AMR Low reached",
                        NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert3.wav", 10, Brushes.IndianRed, Brushes.Black);
                }

                // Pivot level crossing alerts
                if (currentPivot != null)
                {
                    double prev = CurrentBar > 0 ? Close[1] : c;
                    CheckPivotCrossAlert(prev, c, currentPivot.PP,  "PP");
                    CheckPivotCrossAlert(prev, c, currentPivot.R1,  "R1");
                    CheckPivotCrossAlert(prev, c, currentPivot.R2,  "R2");
                    CheckPivotCrossAlert(prev, c, currentPivot.R3,  "R3");
                    CheckPivotCrossAlert(prev, c, currentPivot.S1,  "S1");
                    CheckPivotCrossAlert(prev, c, currentPivot.S2,  "S2");
                    CheckPivotCrossAlert(prev, c, currentPivot.S3,  "S3");
                }
            }

            ForceRefresh();
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region Helper methods — pivots, sessions, DST

        private void ComputePivots()
        {
            if (prevDayHigh == 0 && prevDayLow == 0)
                return;

            double h = prevDayHigh, l = prevDayLow, c = prevDayClose;
            double pp = (h + l + c) / 3.0;
            double r1 = 2 * pp - l;
            double s1 = 2 * pp - h;
            double r2 = pp - s1 + r1;
            double s2 = pp - r1 + s1;
            double r3 = 2 * pp + h - 2 * l;
            double s3 = 2 * pp - (2 * h - l);

            currentPivot = new PivotSnapshot
            {
                PP = pp, R1 = r1, R2 = r2, R3 = r3,
                S1 = s1, S2 = s2, S3 = s3,
                // M levels: midpoints between adjacent pivot levels
                M0 = (s2 + s3) / 2.0,
                M1 = (s1 + s2) / 2.0,
                M2 = (pp + s1) / 2.0,
                M3 = (pp + r1) / 2.0,
                M4 = (r1 + r2) / 2.0,
                M5 = (r2 + r3) / 2.0,
                StartBarIndex = CurrentBar
            };
        }

        // ── DST detection helpers ─────────────────────────────────────────────
        // UK DST: last Sunday in March → last Sunday in October
        private static bool IsUkDst(DateTime utc)
        {
            if (utc.Month < 3 || utc.Month > 10) return false;
            if (utc.Month > 3 && utc.Month < 10) return true;
            DateTime lastSunday = LastSundayOfMonth(utc.Year, utc.Month);
            if (utc.Month == 3)  return utc >= lastSunday.AddHours(1);
            if (utc.Month == 10) return utc <  lastSunday.AddHours(1);
            return false;
        }

        // US DST: 2nd Sunday in March → 1st Sunday in November
        private static bool IsUsDst(DateTime utc)
        {
            if (utc.Month < 3 || utc.Month > 11) return false;
            if (utc.Month > 3 && utc.Month < 11) return true;
            if (utc.Month == 3)
            {
                DateTime second = NthSundayOfMonth(utc.Year, 3, 2);
                return utc >= second.AddHours(2);
            }
            // November: 1st Sunday
            DateTime first = NthSundayOfMonth(utc.Year, 11, 1);
            return utc < first.AddHours(2);
        }

        // AU DST: 1st Sunday of October → 1st Sunday of April
        private static bool IsAuDst(DateTime utc)
        {
            // Flip: AU DST is southern hemisphere — active Oct–Apr
            if (utc.Month >= 4 && utc.Month <= 9) return false;
            if (utc.Month > 4  && utc.Month < 10) return false;
            if (utc.Month == 4)
            {
                DateTime first = NthSundayOfMonth(utc.Year, 4, 1);
                return utc < first.AddHours(2);
            }
            if (utc.Month == 10)
            {
                DateTime first = NthSundayOfMonth(utc.Year, 10, 1);
                return utc >= first.AddHours(2);
            }
            return true;  // Nov–Mar
        }

        private static DateTime LastSundayOfMonth(int year, int month)
        {
            DateTime last = new DateTime(year, month, DateTime.DaysInMonth(year, month));
            int dow = (int)last.DayOfWeek;
            return last.AddDays(-dow);
        }

        private static DateTime NthSundayOfMonth(int year, int month, int n)
        {
            DateTime first = new DateTime(year, month, 1);
            int daysToSunday = ((7 - (int)first.DayOfWeek) % 7);
            return first.AddDays(daysToSunday + (n - 1) * 7);
        }

        /// <summary>
        /// Returns the UTC start/end times for a named session on a given UTC date,
        /// applying the appropriate DST offset.
        /// sessionId: 0=London,1=NY,2=Tokyo,3=HK,4=Sydney,5=EUBrinks,6=USBrinks,7=Frankfurt
        /// </summary>
        private void GetSessionUtcTimes(int sessionId, DateTime utcDate,
            out DateTime start, out DateTime end)
        {
            // Base UTC times (non-DST)
            // London:     08:00–16:30  UK DST → 07:00–15:30
            // New York:   14:30–21:00  US DST → 13:30–20:00
            // Tokyo:      00:00–06:00  No DST
            // Hong Kong:  01:30–08:00  No DST
            // Sydney:     22:00–06:00  AU DST → 21:00–05:00
            // EU Brinks:  08:00–09:00  UK DST → 07:00–08:00
            // US Brinks:  14:00–15:00  US DST → 13:00–14:00
            // Frankfurt:  07:00–16:30  UK DST → 06:00–15:30

            DateTime day = utcDate.Date;
            switch (sessionId)
            {
                case 0: // London
                {
                    int offset = IsUkDst(day) ? -1 : 0;
                    start = day.AddHours(8 + offset);
                    end   = day.AddHours(16 + offset).AddMinutes(30);
                    break;
                }
                case 1: // New York
                {
                    int offset = IsUsDst(day) ? -1 : 0;
                    start = day.AddHours(14 + offset).AddMinutes(30);
                    end   = day.AddHours(21 + offset);
                    break;
                }
                case 2: // Tokyo
                    start = day.AddHours(0);
                    end   = day.AddHours(6);
                    break;
                case 3: // Hong Kong
                    start = day.AddHours(1).AddMinutes(30);
                    end   = day.AddHours(8);
                    break;
                case 4: // Sydney — spans midnight; start on previous calendar day UTC
                {
                    int offset = IsAuDst(day) ? -1 : 0;
                    start = day.AddDays(-1).AddHours(22 + offset);
                    end   = day.AddHours(6 + offset);
                    break;
                }
                case 5: // EU Brinks
                {
                    int offset = IsUkDst(day) ? -1 : 0;
                    start = day.AddHours(8 + offset);
                    end   = day.AddHours(9 + offset);
                    break;
                }
                case 6: // US Brinks
                {
                    int offset = IsUsDst(day) ? -1 : 0;
                    start = day.AddHours(14 + offset);
                    end   = day.AddHours(15 + offset);
                    break;
                }
                case 7: // Frankfurt
                default:
                {
                    int offset = IsUkDst(day) ? -1 : 0;
                    start = day.AddHours(7 + offset);
                    end   = day.AddHours(16 + offset).AddMinutes(30);
                    break;
                }
            }
        }

        private bool IsSessionEnabled(int id)
        {
            switch (id)
            {
                case 0: return ShowLondon;
                case 1: return ShowNewYork;
                case 2: return ShowTokyo;
                case 3: return ShowHongKong;
                case 4: return ShowSydney;
                case 5: return ShowEuBrinks;
                case 6: return ShowUsBrinks;
                case 7: return ShowFrankfurt;
                default: return false;
            }
        }

        private bool SessionShowOpeningRange(int id)
        {
            switch (id)
            {
                case 0: return LondonShowOpeningRange;
                case 1: return NewYorkShowOpeningRange;
                case 2: return TokyoShowOpeningRange;
                case 3: return HongKongShowOpeningRange;
                case 4: return SydneyShowOpeningRange;
                case 5: return EuBrinksShowOpeningRange;
                case 6: return UsBrinksShowOpeningRange;
                case 7: return FrankfurtShowOpeningRange;
                default: return false;
            }
        }

        private string GetSessionLabel(int id)
        {
            switch (id)
            {
                case 0: return LondonLabel;
                case 1: return NewYorkLabel;
                case 2: return TokyoLabel;
                case 3: return HongKongLabel;
                case 4: return SydneyLabel;
                case 5: return EuBrinksLabel;
                case 6: return UsBrinksLabel;
                case 7: return FrankfurtLabel;
                default: return "";
            }
        }

        private bool SessionShowLabel(int id)
        {
            switch (id)
            {
                case 0: return LondonShowLabel;
                case 1: return NewYorkShowLabel;
                case 2: return TokyoShowLabel;
                case 3: return HongKongShowLabel;
                case 4: return SydneyShowLabel;
                case 5: return EuBrinksShowLabel;
                case 6: return UsBrinksShowLabel;
                case 7: return FrankfurtShowLabel;
                default: return false;
            }
        }

        private void UpdateSessions(DateTime barTimeUtc)
        {
            for (int id = 0; id < 8; id++)
            {
                if (!IsSessionEnabled(id))
                    continue;

                DateTime sStart, sEnd;
                GetSessionUtcTimes(id, barTimeUtc, out sStart, out sEnd);

                bool inSession = barTimeUtc >= sStart && barTimeUtc < sEnd;

                if (inSession)
                {
                    if (activeSessions[id] == null || activeSessions[id].StartTime != sStart)
                    {
                        // New session window — create or replace active box
                        var box = new SessionBox
                        {
                            StartTime     = sStart,
                            EndTime       = sEnd,
                            StartBarIndex = CurrentBar,
                            EndBarIndex   = CurrentBar,
                            SessionHigh   = High[0],
                            SessionLow    = Low[0],
                            IsComplete    = false,
                            SessionId     = id
                        };
                        activeSessions[id] = box;

                        // Add to list (capped at 200)
                        if (sessionBoxes.Count >= 200)
                            sessionBoxes.RemoveAt(0);
                        sessionBoxes.Add(box);
                    }
                    else
                    {
                        // Update active session box
                        var box = activeSessions[id];
                        if (High[0] > box.SessionHigh) box.SessionHigh = High[0];
                        if (Low[0]  < box.SessionLow)  box.SessionLow  = Low[0];
                        box.EndBarIndex = CurrentBar;
                    }
                }
                else
                {
                    // Bar is outside session — mark current box as complete
                    if (activeSessions[id] != null && !activeSessions[id].IsComplete)
                    {
                        activeSessions[id].IsComplete = true;
                        activeSessions[id] = null;
                    }
                }
            }
        }

        private void CheckPivotCrossAlert(double prev, double curr, double level, string name)
        {
            if (level == 0) return;
            bool crossed = (prev < level && curr >= level) || (prev > level && curr <= level);
            if (crossed)
                Alert("IQS_Pivot_" + name, Priority.Medium,
                    "IQ Sessions: Price crossed " + name + " @ " + level.ToString("F5"),
                    NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10,
                    Brushes.Yellow, Brushes.Black);
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region GPU Rendering — OnRender / OnRenderTargetChanged

        public override void OnRenderTargetChanged()
        {
            DisposeDXResources();
            dxReady = false;
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (Bars == null || ChartBars == null || RenderTarget == null)
                return;

            if (!dxReady)
                CreateDXResources();

            if (!dxReady)
                return;

            var rt = RenderTarget;
            float rtW = rt.Size.Width;
            float rtH = rt.Size.Height;

            int fromBar = ChartBars.FromIndex;
            int toBar   = ChartBars.ToIndex;

            if (fromBar > toBar)
                return;

            // ── 1. Session boxes ──────────────────────────────────────────────
            RenderSessionBoxes(chartControl, chartScale, rtW, rtH);

            // ── 2. EMA lines + cloud ──────────────────────────────────────────
            RenderEmas(chartControl, chartScale, fromBar, toBar);

            // ── 3. Pivot levels ───────────────────────────────────────────────
            if (currentPivot != null)
                RenderPivots(chartControl, chartScale, rtW);

            // ── 4. Yesterday / last week Hi/Lo ────────────────────────────────
            RenderYesterdayLevels(chartControl, chartScale, rtW);
            RenderLastWeekLevels(chartControl, chartScale, rtW);

            // ── 5. ADR / AWR / AMR / RD / RW ─────────────────────────────────
            if (adrValue > 0 && ShowAdr)  RenderHorizontalBand(chartControl, chartScale, rtW, adrHigh, adrLow, dxAdrBrush, "ADR H", "ADR L", ShowAdrLabels, adrHigh, adrLow, ShowAdr50);
            if (awrValue > 0 && ShowAwr)  RenderHorizontalBand(chartControl, chartScale, rtW, awrHigh, awrLow, dxAwrBrush, "AWR H", "AWR L", ShowAwrLabels, awrHigh, awrLow, ShowAwr50);
            if (amrValue > 0 && ShowAmr)  RenderHorizontalBand(chartControl, chartScale, rtW, amrHigh, amrLow, dxAmrBrush, "AMR H", "AMR L", ShowAmrLabels, amrHigh, amrLow, ShowAmr50);
            if (rdValue  > 0 && ShowRd)   RenderHorizontalBand(chartControl, chartScale, rtW, rdHigh,  rdLow,  dxRdBrush,  "RD H",  "RD L",  ShowRdLabels,  rdHigh,  rdLow,  false);
            if (rwValue  > 0 && ShowRw)   RenderHorizontalBand(chartControl, chartScale, rtW, rwHigh,  rwLow,  dxRwBrush,  "RW H",  "RW L",  ShowRwLabels,  rwHigh,  rwLow,  false);

            // ── 6. Daily open ─────────────────────────────────────────────────
            if (ShowDailyOpen && dailyOpen > 0)
                RenderSingleLine(chartControl, chartScale, rtW, dailyOpen, dxDailyOpenBrush, "DO", ShowDailyOpen);

            // ── 7. Weekly Psy levels ──────────────────────────────────────────
            if (ShowPsyLevels && psyWeekHigh > 0)
            {
                RenderSingleLine(chartControl, chartScale, rtW, psyWeekHigh, dxPsyBrush, "Psy H", ShowPsyLabels);
                RenderSingleLine(chartControl, chartScale, rtW, psyWeekLow,  dxPsyBrush, "Psy L", ShowPsyLabels);
            }

            // ── 8. ADR/AWR/AMR Dashboard table ────────────────────────────────
            if (ShowRangeTable)
                RenderRangeTable(chartControl, chartScale, rtW, rtH);

            // ── 9. DST reference table ────────────────────────────────────────
            if (ShowDstTable)
                RenderDstTable(chartControl, chartScale, rtW, rtH);
        }

        // ── Session box rendering ─────────────────────────────────────────────
        private void RenderSessionBoxes(ChartControl cc, ChartScale cs, float rtW, float rtH)
        {
            var rt = RenderTarget;
            DateTime now = Time[0];

            foreach (var box in sessionBoxes)
            {
                if (!SessionShowOpeningRange(box.SessionId))
                    continue;

                int sid = box.SessionId;
                if (dxSessionBoxBrush == null || dxSessionBoxBrush[sid] == null)
                    continue;

                // Get X coordinates
                int startIdx = Math.Max(ChartBars.FromIndex, box.StartBarIndex);
                int endIdx   = Math.Min(ChartBars.ToIndex,   box.EndBarIndex);
                if (startIdx > endIdx) continue;

                float xLeft  = cc.GetXByBarIndex(ChartBars, startIdx)
                             - cc.GetBarPaintWidth(ChartBars) / 2f;
                float xRight = cc.GetXByBarIndex(ChartBars, endIdx)
                             + cc.GetBarPaintWidth(ChartBars) / 2f;

                float yHigh = cs.GetYByValue(box.SessionHigh);
                float yLow  = cs.GetYByValue(box.SessionLow);

                float boxH = Math.Max(1f, yLow - yHigh);
                var rect = new SharpDX.RectangleF(xLeft, yHigh, xRight - xLeft, boxH);

                // Fill
                rt.FillRectangle(rect, dxSessionBoxBrush[sid]);

                // Border (top & bottom hi/lo lines)
                if (dxSessionBorderBrush != null && dxSessionBorderBrush[sid] != null)
                {
                    rt.DrawLine(new SharpDX.Vector2(xLeft,  yHigh),
                                new SharpDX.Vector2(xRight, yHigh),
                                dxSessionBorderBrush[sid], 1.5f);
                    rt.DrawLine(new SharpDX.Vector2(xLeft,  yLow),
                                new SharpDX.Vector2(xRight, yLow),
                                dxSessionBorderBrush[sid], 1.5f);
                }

                // Session label (top-left corner of box)
                if (SessionShowLabel(sid) && dxLabelFormat != null)
                {
                    string lbl = GetSessionLabel(sid);
                    float  lx  = xLeft + 3f;
                    float  ly  = yHigh + 2f;
                    var    lr  = new SharpDX.RectangleF(lx, ly, 120f, 16f);
                    if (dxSessionBorderBrush != null && dxSessionBorderBrush[sid] != null)
                        rt.DrawText(lbl, dxLabelFormat, lr, dxSessionBorderBrush[sid]);
                }
            }
        }

        // ── EMA rendering ─────────────────────────────────────────────────────
        private void RenderEmas(ChartControl cc, ChartScale cs, int fromBar, int toBar)
        {
            var rt = RenderTarget;

            // Pre-gather previous bar X for continuity
            float prevX5 = 0, prevX13 = 0, prevX50 = 0, prevX200 = 0, prevX800 = 0;
            float prevY5 = 0, prevY13 = 0, prevY50 = 0, prevY200 = 0, prevY800 = 0;
            float prevYCloudU = 0, prevYCloudL = 0;
            bool firstBar = true;

            for (int barIdx = fromBar; barIdx <= toBar; barIdx++)
            {
                if (barIdx < MIN_BARS_REQUIRED)
                    continue;

                float x = cc.GetXByBarIndex(ChartBars, barIdx);

                // Retrieve EMA values via Bars data at barIdx offset from CurrentBar
                int   off   = CurrentBar - barIdx;
                if (off < 0) continue;

                double e5   = EMA(5)[off];
                double e13  = EMA(13)[off];
                double e50  = EMA(50)[off];
                double e200 = EMA(200)[off];
                double e800 = EMA(800)[off];

                float y5   = cs.GetYByValue(e5);
                float y13  = cs.GetYByValue(e13);
                float y50  = cs.GetYByValue(e50);
                float y200 = cs.GetYByValue(e200);
                float y800 = cs.GetYByValue(e800);

                double sdOff = 0;
                float  yClU  = y50, yClL = y50;
                if (ShowEma50Cloud && barIdx >= 100)
                {
                    sdOff = StdDev(Close, 100)[off] / 4.0;
                    yClU  = cs.GetYByValue(e50 + sdOff);
                    yClL  = cs.GetYByValue(e50 - sdOff);
                }

                if (!firstBar)
                {
                    // Cloud fill geometry
                    if (ShowEma50Cloud && dxCloudFillBrush != null)
                    {
                        var geom = new SharpDX.Direct2D1.PathGeometry(rt.Factory);
                        var sink = geom.Open();
                        sink.BeginFigure(new SharpDX.Vector2(prevX50, prevYCloudU), SharpDX.Direct2D1.FigureBegin.Filled);
                        sink.AddLine(new SharpDX.Vector2(x,       yClU));
                        sink.AddLine(new SharpDX.Vector2(x,       yClL));
                        sink.AddLine(new SharpDX.Vector2(prevX50, prevYCloudL));
                        sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
                        sink.Close();
                        rt.FillGeometry(geom, dxCloudFillBrush);
                        sink.Dispose();
                        geom.Dispose();
                    }

                    if (ShowEma5   && dxEma5Brush   != null) rt.DrawLine(new SharpDX.Vector2(prevX5,   prevY5),   new SharpDX.Vector2(x, y5),   dxEma5Brush,   Ema5Thickness);
                    if (ShowEma13  && dxEma13Brush  != null) rt.DrawLine(new SharpDX.Vector2(prevX13,  prevY13),  new SharpDX.Vector2(x, y13),  dxEma13Brush,  Ema13Thickness);
                    if (ShowEma50  && dxEma50Brush  != null) rt.DrawLine(new SharpDX.Vector2(prevX50,  prevY50),  new SharpDX.Vector2(x, y50),  dxEma50Brush,  Ema50Thickness);
                    if (ShowEma200 && dxEma200Brush != null) rt.DrawLine(new SharpDX.Vector2(prevX200, prevY200), new SharpDX.Vector2(x, y200), dxEma200Brush, Ema200Thickness);
                    if (ShowEma800 && dxEma800Brush != null) rt.DrawLine(new SharpDX.Vector2(prevX800, prevY800), new SharpDX.Vector2(x, y800), dxEma800Brush, Ema800Thickness);
                }

                prevX5 = prevX13 = prevX50 = prevX200 = prevX800 = x;
                prevY5   = y5;   prevY13  = y13;  prevY50 = y50;
                prevY200 = y200; prevY800 = y800;
                prevYCloudU = yClU; prevYCloudL = yClL;
                firstBar = false;
            }

            // Labels at rightmost visible bar
            if (ShowEmaLabels && !firstBar && dxLabelFormat != null)
            {
                float labelX = prevX5 + 6f;
                DrawLineLabel(labelX, prevY5,   "5",   dxEma5Brush);
                DrawLineLabel(labelX, prevY13,  "13",  dxEma13Brush);
                DrawLineLabel(labelX, prevY50,  "50",  dxEma50Brush);
                DrawLineLabel(labelX, prevY200, "200", dxEma200Brush);
                DrawLineLabel(labelX, prevY800, "800", dxEma800Brush);
            }
        }

        private void DrawLineLabel(float x, float y, string text,
            SharpDX.Direct2D1.SolidColorBrush brush)
        {
            if (brush == null || dxLabelFormat == null) return;
            var lr = new SharpDX.RectangleF(x, y - 8f, 40f, 16f);
            RenderTarget.DrawText(text, dxLabelFormat, lr, brush);
        }

        // ── Pivot rendering ───────────────────────────────────────────────────
        private void RenderPivots(ChartControl cc, ChartScale cs, float rtW)
        {
            if (currentPivot == null) return;
            var rt = RenderTarget;

            if (ShowPP     && dxPPBrush    != null) RenderPivotLine(cs, rtW, currentPivot.PP, dxPPBrush,    "PP",   ShowPivotLabels, PivotLineStyle);
            if (ShowLevel1 && dxRLevelBrush!= null) RenderPivotLine(cs, rtW, currentPivot.R1, dxRLevelBrush,"R1",   ShowPivotLabels, PivotLineStyle);
            if (ShowLevel1 && dxSLevelBrush!= null) RenderPivotLine(cs, rtW, currentPivot.S1, dxSLevelBrush,"S1",   ShowPivotLabels, PivotLineStyle);
            if (ShowLevel2 && dxRLevelBrush!= null) RenderPivotLine(cs, rtW, currentPivot.R2, dxRLevelBrush,"R2",   ShowPivotLabels, PivotLineStyle);
            if (ShowLevel2 && dxSLevelBrush!= null) RenderPivotLine(cs, rtW, currentPivot.S2, dxSLevelBrush,"S2",   ShowPivotLabels, PivotLineStyle);
            if (ShowLevel3 && dxRLevelBrush!= null) RenderPivotLine(cs, rtW, currentPivot.R3, dxRLevelBrush,"R3",   ShowPivotLabels, PivotLineStyle);
            if (ShowLevel3 && dxSLevelBrush!= null) RenderPivotLine(cs, rtW, currentPivot.S3, dxSLevelBrush,"S3",   ShowPivotLabels, PivotLineStyle);

            if (ShowMLevels && dxMLevelBrush != null)
            {
                RenderPivotLine(cs, rtW, currentPivot.M0, dxMLevelBrush, ShowMLabels ? "M0" : "", ShowMLabels, MLevelLineStyle);
                RenderPivotLine(cs, rtW, currentPivot.M1, dxMLevelBrush, ShowMLabels ? "M1" : "", ShowMLabels, MLevelLineStyle);
                RenderPivotLine(cs, rtW, currentPivot.M2, dxMLevelBrush, ShowMLabels ? "M2" : "", ShowMLabels, MLevelLineStyle);
                RenderPivotLine(cs, rtW, currentPivot.M3, dxMLevelBrush, ShowMLabels ? "M3" : "", ShowMLabels, MLevelLineStyle);
                RenderPivotLine(cs, rtW, currentPivot.M4, dxMLevelBrush, ShowMLabels ? "M4" : "", ShowMLabels, MLevelLineStyle);
                RenderPivotLine(cs, rtW, currentPivot.M5, dxMLevelBrush, ShowMLabels ? "M5" : "", ShowMLabels, MLevelLineStyle);
            }
        }

        private void RenderPivotLine(ChartScale cs, float rtW, double price,
            SharpDX.Direct2D1.SolidColorBrush brush, string label, bool showLabel, IQSLineStyle style)
        {
            if (price == 0) return;
            float y = cs.GetYByValue(price);
            DrawStyledLine(0f, y, rtW, y, brush, 1f, style);
            if (showLabel && label.Length > 0 && dxLabelFormat != null)
            {
                string txt  = label + " " + price.ToString("F5");
                var    rect = new SharpDX.RectangleF(rtW - 80f, y - 8f, 78f, 16f);
                RenderTarget.DrawText(txt, dxLabelFormat, rect, brush);
            }
        }

        private void RenderYesterdayLevels(ChartControl cc, ChartScale cs, float rtW)
        {
            if (!ShowYesterday || yesterdayHigh == 0) return;
            RenderSingleLine(cc, cs, rtW, yesterdayHigh, dxYesterdayBrush, ShowYesterdayLabels ? "YH" : "", ShowYesterdayLabels);
            RenderSingleLine(cc, cs, rtW, yesterdayLow,  dxYesterdayBrush, ShowYesterdayLabels ? "YL" : "", ShowYesterdayLabels);
        }

        private void RenderLastWeekLevels(ChartControl cc, ChartScale cs, float rtW)
        {
            if (!ShowLastWeek || lastWeekHigh == 0) return;
            RenderSingleLine(cc, cs, rtW, lastWeekHigh, dxLastWeekBrush, ShowLastWeekLabels ? "LWH" : "", ShowLastWeekLabels);
            RenderSingleLine(cc, cs, rtW, lastWeekLow,  dxLastWeekBrush, ShowLastWeekLabels ? "LWL" : "", ShowLastWeekLabels);
        }

        private void RenderSingleLine(ChartControl cc, ChartScale cs, float rtW, double price,
            SharpDX.Direct2D1.SolidColorBrush brush, string label, bool showLabel)
        {
            if (price == 0 || brush == null) return;
            float y = cs.GetYByValue(price);
            DrawStyledLine(0f, y, rtW, y, brush, 1.5f, IQSLineStyle.Solid);
            if (showLabel && label.Length > 0 && dxLabelFormat != null)
            {
                string txt  = label + " " + price.ToString("F5");
                var    rect = new SharpDX.RectangleF(rtW - 90f, y - 8f, 88f, 16f);
                RenderTarget.DrawText(txt, dxLabelFormat, rect, brush);
            }
        }

        private void RenderHorizontalBand(ChartControl cc, ChartScale cs, float rtW,
            double high, double low, SharpDX.Direct2D1.SolidColorBrush brush,
            string highLabel, string lowLabel, bool showLabels,
            double h, double l, bool show50)
        {
            if (brush == null || high == 0 || low == 0) return;
            float yH = cs.GetYByValue(high);
            float yL = cs.GetYByValue(low);
            DrawStyledLine(0f, yH, rtW, yH, brush, 1.5f, IQSLineStyle.Dashed);
            DrawStyledLine(0f, yL, rtW, yL, brush, 1.5f, IQSLineStyle.Dashed);
            if (showLabels && dxLabelFormat != null)
            {
                RenderTarget.DrawText(highLabel + " " + high.ToString("F5"), dxLabelFormat,
                    new SharpDX.RectangleF(rtW - 100f, yH - 8f, 98f, 16f), brush);
                RenderTarget.DrawText(lowLabel  + " " + low.ToString("F5"),  dxLabelFormat,
                    new SharpDX.RectangleF(rtW - 100f, yL - 8f, 98f, 16f), brush);
            }
            if (show50)
            {
                double mid  = (high + low) / 2.0;
                float  yMid = cs.GetYByValue(mid);
                DrawStyledLine(0f, yMid, rtW, yMid, brush, 1f, IQSLineStyle.Dotted);
            }
        }

        // ── Styled line drawing (Solid / Dashed / Dotted) ─────────────────────
        private void DrawStyledLine(float x1, float y1, float x2, float y2,
            SharpDX.Direct2D1.SolidColorBrush brush, float strokeWidth, IQSLineStyle style)
        {
            if (brush == null) return;
            var rt = RenderTarget;

            if (style == IQSLineStyle.Solid)
            {
                rt.DrawLine(new SharpDX.Vector2(x1, y1),
                            new SharpDX.Vector2(x2, y2), brush, strokeWidth);
                return;
            }

            // Dashed / Dotted — manual segment drawing
            float dashLen  = style == IQSLineStyle.Dashed ? 8f : 3f;
            float gapLen   = style == IQSLineStyle.Dashed ? 4f : 3f;
            float totalLen = (float)Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
            if (totalLen < 1f) return;
            float dx = (x2 - x1) / totalLen;
            float dy = (y2 - y1) / totalLen;
            float pos = 0f;
            bool  drawing = true;

            while (pos < totalLen)
            {
                float segLen = drawing ? dashLen : gapLen;
                float endPos = Math.Min(pos + segLen, totalLen);
                if (drawing)
                {
                    rt.DrawLine(
                        new SharpDX.Vector2(x1 + dx * pos,    y1 + dy * pos),
                        new SharpDX.Vector2(x1 + dx * endPos, y1 + dy * endPos),
                        brush, strokeWidth);
                }
                pos    += segLen;
                drawing = !drawing;
            }
        }

        // ── Range dashboard table ─────────────────────────────────────────────
        private void RenderRangeTable(ChartControl cc, ChartScale cs, float rtW, float rtH)
        {
            if (dxDashBgBrush == null || dxDashTextBrush == null || dxDashFormat == null)
                return;

            double pipSize = TickSize;
            string[] rows =
            {
                string.Format("ADR  {0,6:F0} pips",  adrValue / pipSize),
                string.Format("AWR  {0,6:F0} pips",  awrValue / pipSize),
                string.Format("AMR  {0,6:F0} pips",  amrValue / pipSize),
                string.Format("RD   {0,6:F0} pips",  rdValue  / pipSize),
                string.Format("RW   {0,6:F0} pips",  rwValue  / pipSize),
            };

            float cellH = 18f;
            float tableW = 170f;
            float tableH = rows.Length * cellH + 24f;
            float margin = 8f;

            float tx, ty;
            GetTablePosition(TablePosition, rtW, rtH, tableW, tableH, margin, out tx, out ty);

            var bg = new SharpDX.RectangleF(tx, ty, tableW, tableH);
            RenderTarget.FillRectangle(bg, dxDashBgBrush);

            // Header
            var hdr = new SharpDX.RectangleF(tx + 4f, ty + 2f, tableW - 8f, 18f);
            RenderTarget.DrawText("Range Statistics", dxDashFormat, hdr, dxDashHeaderBrush ?? dxDashTextBrush);

            for (int i = 0; i < rows.Length; i++)
            {
                var rect = new SharpDX.RectangleF(tx + 4f, ty + 22f + i * cellH, tableW - 8f, cellH);
                RenderTarget.DrawText(rows[i], dxDashFormat, rect, dxDashTextBrush);
            }
        }

        // ── DST info table ────────────────────────────────────────────────────
        private void RenderDstTable(ChartControl cc, ChartScale cs, float rtW, float rtH)
        {
            if (dxDashBgBrush == null || dxDashTextBrush == null || dxDashFormat == null)
                return;

            string[] rows =
            {
                "UK DST: Last Sun Mar → Last Sun Oct",
                "US DST: 2nd Sun Mar → 1st Sun Nov",
                "AU DST: 1st Sun Oct → 1st Sun Apr",
                "London  08:00-16:30 UTC (UK DST -1)",
                "NY      14:30-21:00 UTC (US DST -1)",
                "Tokyo   00:00-06:00 UTC (no DST)",
                "HK      01:30-08:00 UTC (no DST)",
                "Sydney  22:00-06:00 UTC (AU DST -1)",
                "EUBrnks 08:00-09:00 UTC (UK DST -1)",
                "USBrnks 14:00-15:00 UTC (US DST -1)",
                "Frankft 07:00-16:30 UTC (UK DST -1)",
            };

            float cellH  = 16f;
            float tableW = 280f;
            float tableH = rows.Length * cellH + 24f;
            float margin = 8f;

            float tx, ty;
            GetTablePosition(DstTablePosition, rtW, rtH, tableW, tableH, margin, out tx, out ty);

            var bg = new SharpDX.RectangleF(tx, ty, tableW, tableH);
            RenderTarget.FillRectangle(bg, dxDashBgBrush);

            var hdr = new SharpDX.RectangleF(tx + 4f, ty + 2f, tableW - 8f, 18f);
            RenderTarget.DrawText("DST Reference", dxDashFormat, hdr, dxDashHeaderBrush ?? dxDashTextBrush);

            for (int i = 0; i < rows.Length; i++)
            {
                var rect = new SharpDX.RectangleF(tx + 4f, ty + 22f + i * cellH, tableW - 8f, cellH);
                RenderTarget.DrawText(rows[i], dxSmallFormat ?? dxDashFormat, rect, dxDashTextBrush);
            }
        }

        private static void GetTablePosition(IQSDashboardPosition pos,
            float rtW, float rtH, float tableW, float tableH, float margin,
            out float tx, out float ty)
        {
            switch (pos)
            {
                case IQSDashboardPosition.TopLeft:
                    tx = margin; ty = margin; break;
                case IQSDashboardPosition.TopRight:
                    tx = rtW - tableW - margin; ty = margin; break;
                case IQSDashboardPosition.BottomLeft:
                    tx = margin; ty = rtH - tableH - margin; break;
                default: // BottomRight
                    tx = rtW - tableW - margin; ty = rtH - tableH - margin; break;
            }
        }

        #endregion
        // ════════════════════════════════════════════════════════════════════════
        #region SharpDX resource management

        private void CreateDXResources()
        {
            var rt = RenderTarget;
            if (rt == null) return;

            try
            {
                dxWriteFactory = new SharpDX.DirectWrite.Factory();
                dxLabelFormat  = new SharpDX.DirectWrite.TextFormat(dxWriteFactory, "Consolas", 10f);
                dxSmallFormat  = new SharpDX.DirectWrite.TextFormat(dxWriteFactory, "Consolas",  9f);
                dxDashFormat   = new SharpDX.DirectWrite.TextFormat(dxWriteFactory, "Consolas", 11f);

                // EMA brushes
                dxEma5Brush   = MakeBrush(rt, Ema5Color,   1f);
                dxEma13Brush  = MakeBrush(rt, Ema13Color,  1f);
                dxEma50Brush  = MakeBrush(rt, Ema50Color,  1f);
                dxEma200Brush = MakeBrush(rt, Ema200Color, 1f);
                dxEma800Brush = MakeBrush(rt, Ema800Color, 1f);
                dxCloudFillBrush   = MakeBrush(rt, CloudFillColor, CloudFillOpacity / 100f);
                dxCloudBorderBrush = MakeBrush(rt, Ema50Color, 0.35f);

                // Pivot brushes
                dxPPBrush     = MakeBrush(rt, PPColor,      0.85f);
                dxRLevelBrush = MakeBrush(rt, RLevelColor,  0.85f);
                dxSLevelBrush = MakeBrush(rt, SLevelColor,  0.85f);
                dxMLevelBrush = MakeBrush(rt, MLevelColor,  MLevelOpacity / 100f);

                // Yesterday / last week
                dxYesterdayBrush = MakeBrush(rt, YesterdayColor, 0.8f);
                dxLastWeekBrush  = MakeBrush(rt, LastWeekColor,  0.8f);

                // ADR / AWR / AMR / RD / RW
                dxAdrBrush = MakeBrush(rt, AdrColor, AdrOpacity / 100f);
                dxAwrBrush = MakeBrush(rt, AwrColor, AwrOpacity / 100f);
                dxAmrBrush = MakeBrush(rt, AmrColor, AmrOpacity / 100f);
                dxRdBrush  = MakeBrush(rt, RdColor,  RdOpacity  / 100f);
                dxRwBrush  = MakeBrush(rt, RwColor,  RwOpacity  / 100f);

                // Daily open
                dxDailyOpenBrush = MakeBrush(rt, DailyOpenColor, 0.9f);

                // Session brushes — per session
                dxSessionBoxBrush    = new SharpDX.Direct2D1.SolidColorBrush[8];
                dxSessionBorderBrush = new SharpDX.Direct2D1.SolidColorBrush[8];
                System.Windows.Media.Brush[] sessionColors =
                {
                    LondonColor, NewYorkColor, TokyoColor, HongKongColor,
                    SydneyColor, EuBrinksColor, UsBrinksColor, FrankfurtColor
                };
                int[] sessionOpacities =
                {
                    LondonOpacity, NewYorkOpacity, TokyoOpacity, HongKongOpacity,
                    SydneyOpacity, EuBrinksOpacity, UsBrinksOpacity, FrankfurtOpacity
                };
                for (int i = 0; i < 8; i++)
                {
                    dxSessionBoxBrush[i]    = MakeBrush(rt, sessionColors[i], sessionOpacities[i] / 100f);
                    dxSessionBorderBrush[i] = MakeBrush(rt, sessionColors[i], 0.85f);
                }

                // Psy levels
                dxPsyBrush = MakeBrush(rt, PsyColor, PsyOpacity / 100f);

                // Dashboard
                dxDashBgBrush    = new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color((byte)10, (byte)10, (byte)20, (byte)200));
                dxDashTextBrush  = MakeBrush(rt, TableTextColor, 0.95f);
                dxDashHeaderBrush= new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(1f, 0.85f, 0.2f, 1f));

                dxReady = true;
            }
            catch
            {
                dxReady = false;
            }
        }

        private static SharpDX.Direct2D1.SolidColorBrush MakeBrush(
            SharpDX.Direct2D1.RenderTarget rt,
            System.Windows.Media.Brush wpfBrush,
            float opacity)
        {
            var scb = wpfBrush as System.Windows.Media.SolidColorBrush;
            if (scb != null)
            {
                var c = scb.Color;
                return new SharpDX.Direct2D1.SolidColorBrush(rt,
                    new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, opacity));
            }
            return new SharpDX.Direct2D1.SolidColorBrush(rt,
                new SharpDX.Color4(1f, 1f, 1f, opacity));
        }

        private void DisposeDXResources()
        {
            DisposeRef(ref dxWriteFactory);
            DisposeRef(ref dxLabelFormat);
            DisposeRef(ref dxSmallFormat);
            DisposeRef(ref dxDashFormat);
            DisposeRef(ref dxEma5Brush);
            DisposeRef(ref dxEma13Brush);
            DisposeRef(ref dxEma50Brush);
            DisposeRef(ref dxEma200Brush);
            DisposeRef(ref dxEma800Brush);
            DisposeRef(ref dxCloudFillBrush);
            DisposeRef(ref dxCloudBorderBrush);
            DisposeRef(ref dxPPBrush);
            DisposeRef(ref dxRLevelBrush);
            DisposeRef(ref dxSLevelBrush);
            DisposeRef(ref dxMLevelBrush);
            DisposeRef(ref dxYesterdayBrush);
            DisposeRef(ref dxLastWeekBrush);
            DisposeRef(ref dxAdrBrush);
            DisposeRef(ref dxAwrBrush);
            DisposeRef(ref dxAmrBrush);
            DisposeRef(ref dxRdBrush);
            DisposeRef(ref dxRwBrush);
            DisposeRef(ref dxDailyOpenBrush);
            DisposeRef(ref dxPsyBrush);
            DisposeRef(ref dxDashBgBrush);
            DisposeRef(ref dxDashTextBrush);
            DisposeRef(ref dxDashHeaderBrush);

            if (dxSessionBoxBrush != null)
            {
                for (int i = 0; i < dxSessionBoxBrush.Length; i++)
                    DisposeRef(ref dxSessionBoxBrush[i]);
                dxSessionBoxBrush = null;
            }
            if (dxSessionBorderBrush != null)
            {
                for (int i = 0; i < dxSessionBorderBrush.Length; i++)
                    DisposeRef(ref dxSessionBorderBrush[i]);
                dxSessionBorderBrush = null;
            }
        }

        private static void DisposeRef<T>(ref T resource) where T : class, IDisposable
        {
            if (resource != null)
            {
                resource.Dispose();
                resource = null;
            }
        }

        #endregion
    }
}
