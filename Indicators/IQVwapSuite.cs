// IQVwapSuite — Standalone VWAP indicator for NinjaTrader 8.
// Extracted from the VWAP engine inside IQMainUltimate.cs and extended with a third,
// continuous (24/7) anchor.  All three anchors share the same feature set:
//   • ETH VWAP        — resets at 18:00 ET (CME Globex daily open), DST-safe
//   • RTH VWAP        — resets at 09:30 ET (US cash open), DST-safe, only drawn during RTH
//   • 24/7 VWAP       — continuous; never resets, or resets weekly (Sunday 18:00 ET) by choice
//   • ±1σ / ±2σ / ±3σ standard-deviation bands (volume-weighted variance), each with
//     independent show / color / opacity / thickness
//   • Optional exclusive ring fill per band
//   • Dynamic line color (price above / below / neutral) or fixed per-anchor color
//   • Line thickness, line style (solid / dashed / dotted), opacity
//   • Right-edge price labels with per-anchor label text
//   • SharpDX GPU rendering throughout — no NinjaTrader draw objects
//   • Time[0] / Bars.GetTime(CurrentBar) is in the chart/PC time zone, not the trading-hours template zone
//
// This file is self-contained at runtime: it does NOT call into IQMainGPU.cs,
// IQMainGPU_Enhanced.cs or IQMainUltimate.cs.

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

// NinjaTrader 8 requires custom enums declared OUTSIDE all namespaces.
// Names are prefixed with IQVwap to avoid clashing with other scripts.

/// <summary>Line style for VWAP and band lines.</summary>
public enum IQVwapLineStyle { Solid, Dashed, Dotted }

/// <summary>Reset behaviour for the continuous (24/7) VWAP anchor.</summary>
public enum IQVwapContinuousReset
{
    /// <summary>Never reset — accumulates from the first bar loaded on the chart.</summary>
    Never,
    /// <summary>Reset every Sunday at 18:00 ET (CME weekly open).</summary>
    Weekly
}

/// <summary>Band gating mode for IQVwapSuite. Named distinctly from IQMainGPU's
/// IQVwapBandWindow so this file stays standalone and never collides.</summary>
public enum IQVwapSuiteBandWindow { AnchorSession, CustomEtTimes }

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// IQVwapSuite — ETH, RTH and 24/7 VWAP with standard-deviation bands, GPU rendered.
    /// Bar times come from the chart/PC time zone, not the trading-hours template zone.
    /// </summary>
    public class IQVwapSuite : Indicator
    {
        // ═══════════════════════════════════════════════════════════════
        #region Inner types

        /// <summary>Per-bar VWAP and standard deviation band data.</summary>
        private class VwapBarData
        {
            public double Vwap;
            public double StdDev;
            public double Band1Upper, Band1Lower;
            public double Band2Upper, Band2Lower;
            public double Band3Upper, Band3Lower;
            public DateTime BarEt;
            public bool InBandWindow;
            public bool InRthSession;
        }

        /// <summary>One VWAP accumulator (ETH, RTH or Continuous).</summary>
        private class VwapAnchor
        {
            public List<VwapBarData> Data = new List<VwapBarData>(5000);   // indexed by bar index
            public double   CumPV;
            public double   CumVol;
            public double   CumTPVSq;
            public double   ClosedCumPV;
            public double   ClosedCumVol;
            public double   ClosedCumTPVSq;
            public DateTime SessionStart = DateTime.MinValue;
            private int     closedBarIdx = -1;
            private int     lastBarIdx = -1;
            private double  lastBarPV;
            private double  lastBarVol;
            private double  lastBarTPVSq;

            public void Reset(DateTime start)
            {
                SessionStart = start;
                CumPV = CumVol = CumTPVSq = 0;
                ClosedCumPV = ClosedCumVol = ClosedCumTPVSq = 0;
                closedBarIdx = -1;
                lastBarIdx = -1;
                lastBarPV = lastBarVol = lastBarTPVSq = 0;
            }

            public bool HasAnyVolume()
            {
                return ClosedCumVol > 0 || CumVol > 0;
            }

            public void PrepareForBar(int barIdx)
            {
                if (lastBarIdx < 0 || barIdx <= lastBarIdx || closedBarIdx >= lastBarIdx) return;
                ClosedCumPV += lastBarPV;
                ClosedCumVol += lastBarVol;
                ClosedCumTPVSq += lastBarTPVSq;
                closedBarIdx = lastBarIdx;
            }

            public VwapBarData AccumulateForDisplay(double tp, double vol, int barIdx)
            {
                double barPV = 0;
                double barTPVSq = 0;
                if (vol > 0)
                {
                    barPV = tp * vol;
                    barTPVSq = tp * tp * vol;
                }
                CumPV = ClosedCumPV + barPV;
                CumVol = ClosedCumVol + vol;
                CumTPVSq = ClosedCumTPVSq + barTPVSq;
                lastBarIdx = barIdx;
                lastBarPV = barPV;
                lastBarVol = vol;
                lastBarTPVSq = barTPVSq;

                double vwap = CumVol > 0 ? CumPV / CumVol : tp;
                double var  = CumVol > 0 ? Math.Max(0, (CumTPVSq / CumVol) - vwap * vwap) : 0;
                double sd   = Math.Sqrt(var);
                return new VwapBarData
                {
                    Vwap = vwap, StdDev = sd,
                    Band1Upper = vwap + sd,     Band1Lower = vwap - sd,
                    Band2Upper = vwap + 2 * sd, Band2Lower = vwap - 2 * sd,
                    Band3Upper = vwap + 3 * sd, Band3Lower = vwap - 3 * sd
                };
            }

            /// <summary>Store <paramref name="d"/> at <paramref name="barIdx"/>, padding with nulls.</summary>
            public void Store(int barIdx, VwapBarData d)
            {
                while (Data.Count <= barIdx) Data.Add(null);
                Data[barIdx] = d;
            }

            public VwapBarData At(int barIdx)
            {
                if (barIdx < 0 || barIdx >= Data.Count) return null;
                return Data[barIdx];
            }
        }

        #endregion
        // ═══════════════════════════════════════════════════════════════
        #region Private fields

        private VwapAnchor ethAnchor, rthAnchor, contAnchor;
        private const int AnchorKindEth = 0;
        private const int AnchorKindRth = 1;
        private const int AnchorKindContinuous = 2;

        // ET time-zone helper — "Eastern Standard Time" is the Windows TZ id; handles EST↔EDT.
        private static readonly TimeZoneInfo EtZone = SafeFindEtZone();
        private static TimeZoneInfo SafeFindEtZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch (Exception) { return TimeZoneInfo.CreateCustomTimeZone("ET-Fallback", TimeSpan.FromHours(-5), "ET-Fallback", "ET-Fallback"); }
        }

        private DateTime BarTimeEt()
        {
            DateTime t = Bars.GetTime(CurrentBar);
            TimeZoneInfo sourceZone = null;

            if (NinjaTrader.Core.Globals.GeneralOptions != null)
                sourceZone = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;
            if (sourceZone == null)
                sourceZone = TimeZoneInfo.Local;

            if (sourceZone == EtZone || string.Equals(sourceZone.Id, EtZone.Id, StringComparison.OrdinalIgnoreCase))
                return t;

            DateTime tUnspec = DateTime.SpecifyKind(t, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTime(tUnspec, sourceZone, EtZone);
        }

        private static readonly TimeSpan BandWindowStartDefaultEt = new TimeSpan(20, 0, 0);
        private static readonly TimeSpan BandWindowEndDefaultEt   = new TimeSpan(3, 0, 0);
        private bool bandWindowStartParseWarned;
        private bool bandWindowEndParseWarned;

        // SharpDX resources
        private bool dxReady;
        private SharpDX.DirectWrite.Factory    dxWriteFactory;
        private SharpDX.DirectWrite.TextFormat dxLabelFormat;

        private SharpDX.Direct2D1.SolidColorBrush dxAboveBrush, dxBelowBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxEthBrush, dxRthBrush, dxContBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBand1Brush, dxBand2Brush, dxBand3Brush;
        private SharpDX.Direct2D1.SolidColorBrush dxBand1FillBrush, dxBand2FillBrush, dxBand3FillBrush;

        #endregion
        // ═══════════════════════════════════════════════════════════════
        #region Parameters — 1. Anchors

        [NinjaScriptProperty]
        [Display(Name = "Show ETH VWAP", Order = 1, GroupName = "1. Anchors",
            Description = "ETH VWAP resets at 18:00 ET (CME Globex daily open).")]
        public bool ShowEthVwap { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ETH Label", Order = 2, GroupName = "1. Anchors")]
        public string EthLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ETH Color (fixed / neutral)", Order = 3, GroupName = "1. Anchors")]
        [XmlIgnore]
        public Brush EthColor { get; set; }
        [Browsable(false)]
        public string EthColorSerializable { get => Serialize.BrushToString(EthColor); set => EthColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "Show RTH VWAP", Order = 4, GroupName = "1. Anchors",
            Description = "RTH VWAP resets at 09:30 ET (US cash open) and is drawn only during 09:30–16:00 ET.")]
        public bool ShowRthVwap { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RTH Label", Order = 5, GroupName = "1. Anchors")]
        public string RthLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RTH Color (fixed / neutral)", Order = 6, GroupName = "1. Anchors")]
        [XmlIgnore]
        public Brush RthColor { get; set; }
        [Browsable(false)]
        public string RthColorSerializable { get => Serialize.BrushToString(RthColor); set => RthColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "RTH Draw Only During Session", Order = 7, GroupName = "1. Anchors",
            Description = "ON = RTH VWAP hidden outside 09:30–16:00 ET. OFF = last RTH value carried flat through the overnight.")]
        public bool RthOnlyDuringSession { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show 24/7 VWAP", Order = 8, GroupName = "1. Anchors",
            Description = "Continuous VWAP — never resets, or resets weekly (Sunday 18:00 ET).")]
        public bool ShowContinuousVwap { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "24/7 Reset Mode", Order = 9, GroupName = "1. Anchors")]
        public IQVwapContinuousReset ContinuousReset { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "24/7 Label", Order = 10, GroupName = "1. Anchors")]
        public string ContinuousLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "24/7 Color (fixed / neutral)", Order = 11, GroupName = "1. Anchors")]
        [XmlIgnore]
        public Brush ContinuousColor { get; set; }
        [Browsable(false)]
        public string ContinuousColorSerializable { get => Serialize.BrushToString(ContinuousColor); set => ContinuousColor = Serialize.StringToBrush(value); }

        #endregion
        // ═══════════════════════════════════════════════════════════════
        #region Parameters — 2. Line

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "VWAP Line Thickness", Order = 1, GroupName = "2. Line")]
        public int LineThickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "VWAP Line Style", Order = 2, GroupName = "2. Line")]
        public IQVwapLineStyle LineStyle { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "VWAP Opacity %", Order = 3, GroupName = "2. Line")]
        public int LineOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Price Labels", Order = 4, GroupName = "2. Line")]
        public bool ShowLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Dynamic Color (price vs VWAP)", Order = 5, GroupName = "2. Line",
            Description = "ON = line colored Above/Below color by price position. OFF = each anchor uses its own fixed color.")]
        public bool DynamicColor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Color (Price Above)", Order = 6, GroupName = "2. Line")]
        [XmlIgnore]
        public Brush AboveColor { get; set; }
        [Browsable(false)]
        public string AboveColorSerializable { get => Serialize.BrushToString(AboveColor); set => AboveColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Display(Name = "Color (Price Below)", Order = 7, GroupName = "2. Line")]
        [XmlIgnore]
        public Brush BelowColor { get; set; }
        [Browsable(false)]
        public string BelowColorSerializable { get => Serialize.BrushToString(BelowColor); set => BelowColor = Serialize.StringToBrush(value); }

        #endregion
        // ═══════════════════════════════════════════════════════════════
        #region Parameters — 3. Bands

        [NinjaScriptProperty]
        [Display(Name = "Show ±1σ Bands", Order = 1, GroupName = "3. Bands")]
        public bool ShowBand1 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Band 1 Color", Order = 2, GroupName = "3. Bands")]
        [XmlIgnore]
        public Brush Band1Color { get; set; }
        [Browsable(false)]
        public string Band1ColorSerializable { get => Serialize.BrushToString(Band1Color); set => Band1Color = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Band 1 Opacity %", Order = 3, GroupName = "3. Bands")]
        public int Band1Opacity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3)]
        [Display(Name = "Band 1 Thickness", Order = 4, GroupName = "3. Bands")]
        public int Band1Thickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show ±2σ Bands", Order = 5, GroupName = "3. Bands")]
        public bool ShowBand2 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Band 2 Color", Order = 6, GroupName = "3. Bands")]
        [XmlIgnore]
        public Brush Band2Color { get; set; }
        [Browsable(false)]
        public string Band2ColorSerializable { get => Serialize.BrushToString(Band2Color); set => Band2Color = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Band 2 Opacity %", Order = 7, GroupName = "3. Bands")]
        public int Band2Opacity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3)]
        [Display(Name = "Band 2 Thickness", Order = 8, GroupName = "3. Bands")]
        public int Band2Thickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show ±3σ Bands", Order = 9, GroupName = "3. Bands")]
        public bool ShowBand3 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Band 3 Color", Order = 10, GroupName = "3. Bands")]
        [XmlIgnore]
        public Brush Band3Color { get; set; }
        [Browsable(false)]
        public string Band3ColorSerializable { get => Serialize.BrushToString(Band3Color); set => Band3Color = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Band 3 Opacity %", Order = 11, GroupName = "3. Bands")]
        public int Band3Opacity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3)]
        [Display(Name = "Band 3 Thickness", Order = 12, GroupName = "3. Bands")]
        public int Band3Thickness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Fill Band 1", Order = 13, GroupName = "3. Bands")]
        public bool FillBand1 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Band 1 Fill Color", Order = 14, GroupName = "3. Bands")]
        [XmlIgnore]
        public Brush Band1FillColor { get; set; }
        [Browsable(false)]
        public string Band1FillColorSerializable { get => Serialize.BrushToString(Band1FillColor); set => Band1FillColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Band 1 Fill Opacity %", Order = 15, GroupName = "3. Bands")]
        public int Band1FillOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Fill Band 2", Order = 16, GroupName = "3. Bands")]
        public bool FillBand2 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Band 2 Fill Color", Order = 17, GroupName = "3. Bands")]
        [XmlIgnore]
        public Brush Band2FillColor { get; set; }
        [Browsable(false)]
        public string Band2FillColorSerializable { get => Serialize.BrushToString(Band2FillColor); set => Band2FillColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Band 2 Fill Opacity %", Order = 18, GroupName = "3. Bands")]
        public int Band2FillOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Fill Band 3", Order = 19, GroupName = "3. Bands")]
        public bool FillBand3 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Band 3 Fill Color", Order = 20, GroupName = "3. Bands")]
        [XmlIgnore]
        public Brush Band3FillColor { get; set; }
        [Browsable(false)]
        public string Band3FillColorSerializable { get => Serialize.BrushToString(Band3FillColor); set => Band3FillColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Band 3 Fill Opacity %", Order = 21, GroupName = "3. Bands")]
        public int Band3FillOpacity { get; set; }

        #endregion
        // ═══════════════════════════════════════════════════════════════
        #region Parameters — 4. Band Window

        [NinjaScriptProperty]
        [Display(Name = "ETH Bands Enabled", Order = 1, GroupName = "4. Band Window")]
        public bool EthBandsEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RTH Bands Enabled", Order = 2, GroupName = "4. Band Window")]
        public bool RthBandsEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "24/7 Bands Enabled", Order = 3, GroupName = "4. Band Window")]
        public bool ContinuousBandsEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Band Window Mode", Order = 4, GroupName = "4. Band Window")]
        public IQVwapSuiteBandWindow BandWindowMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Band Window Start ET (HH:mm)", Order = 5, GroupName = "4. Band Window")]
        public string BandWindowStartEt { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Band Window End ET (HH:mm)", Order = 6, GroupName = "4. Band Window")]
        public string BandWindowEndEt { get; set; }

        #endregion
        // ═══════════════════════════════════════════════════════════════
        #region OnStateChange

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "IQVwapSuite — standalone ETH / RTH / 24-7 VWAP with σ bands, GPU rendered.";
                Name                     = "IQVwapSuite";
                Calculate                = Calculate.OnEachTick;
                IsOverlay                = true;
                IsAutoScale              = false;
                DisplayInDataBox         = false;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = false;

                // 1. Anchors
                ShowEthVwap          = true;
                EthLabel             = "ETH VWAP";
                EthColor             = Brushes.Yellow;
                ShowRthVwap          = true;
                RthLabel             = "RTH VWAP";
                RthColor             = Brushes.Cyan;
                RthOnlyDuringSession = true;
                ShowContinuousVwap   = true;
                ContinuousReset      = IQVwapContinuousReset.Weekly;
                ContinuousLabel      = "24/7 VWAP";
                ContinuousColor      = Brushes.Magenta;

                // 2. Line
                LineThickness = 2;
                LineStyle     = IQVwapLineStyle.Solid;
                LineOpacity   = 90;
                ShowLabels    = true;
                DynamicColor  = false;
                AboveColor    = Brushes.LimeGreen;
                BelowColor    = Brushes.Crimson;

                // 3. Bands
                ShowBand1      = true;
                Band1Color     = Brushes.DodgerBlue;
                Band1Opacity   = 60;
                Band1Thickness = 1;
                ShowBand2      = true;
                Band2Color     = Brushes.Orange;
                Band2Opacity   = 50;
                Band2Thickness = 1;
                ShowBand3      = false;
                Band3Color     = Brushes.Purple;
                Band3Opacity   = 40;
                Band3Thickness = 1;
                FillBand1       = false;
                Band1FillColor  = Brushes.DodgerBlue;
                Band1FillOpacity = 15;
                FillBand2       = false;
                Band2FillColor  = Brushes.Orange;
                Band2FillOpacity = 15;
                FillBand3       = false;
                Band3FillColor  = Brushes.Purple;
                Band3FillOpacity = 15;

                // 4. Band Window
                EthBandsEnabled        = true;
                RthBandsEnabled        = true;
                ContinuousBandsEnabled = false;
                BandWindowMode         = IQVwapSuiteBandWindow.AnchorSession;
                BandWindowStartEt      = "20:00";
                BandWindowEndEt        = "03:00";
            }
            else if (State == State.Configure)
            {
                MaximumBarsLookBack = MaximumBarsLookBack.Infinite;
            }
            else if (State == State.DataLoaded)
            {
                bandWindowStartParseWarned = false;
                bandWindowEndParseWarned   = false;
                ethAnchor  = new VwapAnchor();
                rthAnchor  = new VwapAnchor();
                contAnchor = new VwapAnchor();
            }
            else if (State == State.Terminated)
            {
                DisposeDXResources();
            }
        }

        #endregion
        // ═══════════════════════════════════════════════════════════════
        #region OnBarUpdate

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 0) return;

            DateTime barEt = BarTimeEt();
            double tp  = (High[0] + Low[0] + Close[0]) / 3.0;
            double vol = Volume[0];
            bool inBandWindow = IsBarInBandWindowEt(barEt);

            // ── ETH (18:00 ET → 18:00 ET) ─────────────────────────────────
            DateTime ethStart = GetEthSessionStartEt(barEt);
            if (ethAnchor.SessionStart != ethStart) ethAnchor.Reset(ethStart);
            ethAnchor.PrepareForBar(CurrentBar);
            ethAnchor.Store(CurrentBar, FinalizeBarData(ethAnchor.AccumulateForDisplay(tp, vol, CurrentBar), barEt, inBandWindow, false));

            // ── RTH (09:30 ET → 16:00 ET) ─────────────────────────────────
            DateTime rthStart = barEt.Date.AddHours(9).AddMinutes(30);
            DateTime rthEnd   = barEt.Date.AddHours(16);
            bool inRth = barEt >= rthStart && barEt < rthEnd;
            if (inRth)
            {
                if (rthAnchor.SessionStart != rthStart) rthAnchor.Reset(rthStart);
                rthAnchor.PrepareForBar(CurrentBar);
                rthAnchor.Store(CurrentBar, FinalizeBarData(rthAnchor.AccumulateForDisplay(tp, vol, CurrentBar), barEt, inBandWindow, true));
            }
            else if (!RthOnlyDuringSession && rthAnchor.HasAnyVolume())
            {
                // Carry last RTH value flat through the overnight (no accumulation)
                rthAnchor.PrepareForBar(CurrentBar);
                rthAnchor.Store(CurrentBar, FinalizeBarData(rthAnchor.AccumulateForDisplay(tp, 0, CurrentBar), barEt, inBandWindow, false));
            }
            else
            {
                rthAnchor.Store(CurrentBar, null);
            }

            // ── 24/7 continuous ───────────────────────────────────────────
            if (ContinuousReset == IQVwapContinuousReset.Weekly)
            {
                DateTime weekStart = GetWeekStartEt(barEt);
                if (contAnchor.SessionStart != weekStart) contAnchor.Reset(weekStart);
            }
            else if (contAnchor.SessionStart == DateTime.MinValue)
            {
                contAnchor.Reset(barEt);
            }
            contAnchor.PrepareForBar(CurrentBar);
            contAnchor.Store(CurrentBar, FinalizeBarData(contAnchor.AccumulateForDisplay(tp, vol, CurrentBar), barEt, inBandWindow, inRth));

            ForceRefresh();
        }

        /// <summary>ETH session start = most recent 18:00 ET at or before barEt.</summary>
        private static DateTime GetEthSessionStartEt(DateTime barEt)
        {
            DateTime today18 = barEt.Date.AddHours(18);
            return barEt >= today18 ? today18 : barEt.Date.AddDays(-1).AddHours(18);
        }

        /// <summary>Weekly anchor = most recent Sunday 18:00 ET at or before barEt.</summary>
        private static DateTime GetWeekStartEt(DateTime barEt)
        {
            DateTime ethStart = GetEthSessionStartEt(barEt);           // 18:00 of some day
            int daysSinceSunday = (int)ethStart.DayOfWeek;              // Sun=0
            return ethStart.AddDays(-daysSinceSunday);
        }

        private static VwapBarData FinalizeBarData(VwapBarData data, DateTime barEt, bool inBandWindow, bool inRthSession)
        {
            if (data == null) return null;
            data.BarEt        = barEt;
            data.InBandWindow = inBandWindow;
            data.InRthSession = inRthSession;
            return data;
        }

        private bool IsBarInBandWindowEt(DateTime barEt)
        {
            TimeSpan startEt;
            TimeSpan endEt;
            TryParseBandWindowTime(BandWindowStartEt, "BandWindowStartEt", BandWindowStartDefaultEt, ref bandWindowStartParseWarned, out startEt);
            TryParseBandWindowTime(BandWindowEndEt, "BandWindowEndEt", BandWindowEndDefaultEt, ref bandWindowEndParseWarned, out endEt);
            return IsTimeOfDayInWindow(barEt.TimeOfDay, startEt, endEt);
        }

        private void TryParseBandWindowTime(string text, string propertyName, TimeSpan fallback, ref bool warned, out TimeSpan parsed)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                parsed = fallback;
                if (!warned)
                {
                    Print("IQVwapSuite: " + propertyName + " is empty. Using default " + fallback.ToString(@"hh\:mm") + " ET.");
                    warned = true;
                }
                return;
            }

            if (TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out parsed) ||
                TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out parsed))
            {
                if (parsed >= TimeSpan.Zero && parsed < TimeSpan.FromDays(1))
                    return;
            }

            parsed = fallback;
            if (!warned)
            {
                Print("IQVwapSuite: Invalid " + propertyName + " value '" + text + "'. Using default " + fallback.ToString(@"hh\:mm") + " ET.");
                warned = true;
            }
        }

        private static bool IsTimeOfDayInWindow(TimeSpan t, TimeSpan startEt, TimeSpan endEt)
        {
            if (startEt == endEt)
                return false;

            if (startEt < endEt)
                return t >= startEt && t < endEt;

            return t >= startEt || t < endEt;
        }

        #endregion
        // ═══════════════════════════════════════════════════════════════
        #region OnRender

        public override void OnRenderTargetChanged()
        {
            DisposeDXResources();
            dxReady = false;
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (Bars == null || ChartBars == null || RenderTarget == null) return;

            if (!dxReady)
            {
                try { CreateDXResources(); }
                catch (Exception ex) { Print("IQVwapSuite: CreateDXResources failed: " + ex.Message); return; }
            }
            if (!dxReady) return;

            int fromBar = ChartBars.FromIndex;
            int toBar   = ChartBars.ToIndex;
            if (fromBar > toBar) return;

            try
            {
                if (ShowContinuousVwap) RenderVwapLine(chartControl, chartScale, fromBar, toBar, contAnchor, ContinuousLabel, dxContBrush, AnchorKindContinuous);
                if (ShowEthVwap)        RenderVwapLine(chartControl, chartScale, fromBar, toBar, ethAnchor,  EthLabel,        dxEthBrush,  AnchorKindEth);
                if (ShowRthVwap)        RenderVwapLine(chartControl, chartScale, fromBar, toBar, rthAnchor,  RthLabel,        dxRthBrush,  AnchorKindRth);
            }
            catch (SharpDX.SharpDXException sdx) { Print("IQVwapSuite: SharpDX error: " + sdx.Message); dxReady = false; DisposeDXResources(); }
            catch (Exception ex) { Print("IQVwapSuite: render error [" + ex.GetType().Name + "]: " + ex.Message); }
        }

        private void RenderVwapLine(ChartControl cc, ChartScale cs, int fromBar, int toBar,
            VwapAnchor anchor, string label, SharpDX.Direct2D1.SolidColorBrush fixedBrush, int anchorKind)
        {
            var rt = RenderTarget;
            if (rt == null || anchor == null || anchor.Data.Count == 0) return;

            if (FillBand3 && dxBand3FillBrush != null) RenderBandFill(cc, cs, fromBar, toBar, anchor, 3, dxBand3FillBrush, anchorKind);
            if (FillBand2 && dxBand2FillBrush != null) RenderBandFill(cc, cs, fromBar, toBar, anchor, 2, dxBand2FillBrush, anchorKind);
            if (FillBand1 && dxBand1FillBrush != null) RenderBandFill(cc, cs, fromBar, toBar, anchor, 1, dxBand1FillBrush, anchorKind);

            if (ShowBand3 && dxBand3Brush != null) RenderBandLines(cc, cs, fromBar, toBar, anchor, 3, dxBand3Brush, Band3Thickness, anchorKind);
            if (ShowBand2 && dxBand2Brush != null) RenderBandLines(cc, cs, fromBar, toBar, anchor, 2, dxBand2Brush, Band2Thickness, anchorKind);
            if (ShowBand1 && dxBand1Brush != null) RenderBandLines(cc, cs, fromBar, toBar, anchor, 1, dxBand1Brush, Band1Thickness, anchorKind);

            for (int i = fromBar; i < toBar; i++)
            {
                if (i < 0 || i + 1 >= Bars.Count) continue;
                VwapBarData d0 = anchor.At(i), d1 = anchor.At(i + 1);
                if (d0 == null || d1 == null) continue;

                float x0 = cc.GetXByBarIndex(ChartBars, i), x1 = cc.GetXByBarIndex(ChartBars, i + 1);
                float y0 = cs.GetYByValue(d0.Vwap),          y1 = cs.GetYByValue(d1.Vwap);

                SharpDX.Direct2D1.SolidColorBrush brush = fixedBrush;
                if (DynamicColor)
                {
                    double c = Bars.GetClose(i + 1);
                    brush = c > d1.Vwap ? dxAboveBrush : c < d1.Vwap ? dxBelowBrush : fixedBrush;
                }
                if (brush == null) continue;
                DrawStyledLine(x0, y0, x1, y1, brush, LineThickness, LineStyle);
            }

            // Label at rightmost visible bar with data
            if (ShowLabels && dxLabelFormat != null)
            {
                int lblBar = Math.Min(toBar, Bars.Count - 1);
                VwapBarData last = anchor.At(lblBar);
                if (last == null) return;

                float  y      = cs.GetYByValue(last.Vwap);
                float  labelX = cc.GetXByBarIndex(ChartBars, lblBar) + 6f;
                string txt    = label + " " + Instrument.MasterInstrument.FormatPrice(last.Vwap);

                SharpDX.Direct2D1.SolidColorBrush lb = fixedBrush;
                if (DynamicColor)
                {
                    double c = Bars.GetClose(lblBar);
                    lb = c > last.Vwap ? dxAboveBrush : c < last.Vwap ? dxBelowBrush : fixedBrush;
                }
                if (lb != null)
                    rt.DrawText(txt, dxLabelFormat, new SharpDX.RectangleF(labelX, y - 8f, 160f, 16f), lb);
            }
        }

        private void RenderBandLines(ChartControl cc, ChartScale cs, int fromBar, int toBar,
            VwapAnchor anchor, int bandNum, SharpDX.Direct2D1.SolidColorBrush brush, int thickness, int anchorKind)
        {
            var rt = RenderTarget;
            if (rt == null) return;

            for (int i = fromBar; i < toBar; i++)
            {
                if (i < 0 || i + 1 >= Bars.Count) continue;
                VwapBarData d0 = anchor.At(i), d1 = anchor.At(i + 1);
                if (d0 == null || d1 == null) continue;
                if (!ShouldRenderBandSegment(d0, d1, anchorKind)) continue;

                double u0, l0, u1, l1;
                switch (bandNum)
                {
                    case 1: u0 = d0.Band1Upper; l0 = d0.Band1Lower; u1 = d1.Band1Upper; l1 = d1.Band1Lower; break;
                    case 2: u0 = d0.Band2Upper; l0 = d0.Band2Lower; u1 = d1.Band2Upper; l1 = d1.Band2Lower; break;
                    case 3: u0 = d0.Band3Upper; l0 = d0.Band3Lower; u1 = d1.Band3Upper; l1 = d1.Band3Lower; break;
                    default: continue;
                }

                float x0 = cc.GetXByBarIndex(ChartBars, i), x1 = cc.GetXByBarIndex(ChartBars, i + 1);
                float yU0 = cs.GetYByValue(u0), yU1 = cs.GetYByValue(u1);
                float yL0 = cs.GetYByValue(l0), yL1 = cs.GetYByValue(l1);
                if (!IsFinite(yU0) || !IsFinite(yU1) || !IsFinite(yL0) || !IsFinite(yL1)) continue;

                DrawStyledLine(x0, yU0, x1, yU1, brush, thickness, IQVwapLineStyle.Dashed);
                DrawStyledLine(x0, yL0, x1, yL1, brush, thickness, IQVwapLineStyle.Dashed);
            }
        }

        private void RenderBandFill(ChartControl cc, ChartScale cs, int fromBar, int toBar,
            VwapAnchor anchor, int bandNum, SharpDX.Direct2D1.SolidColorBrush fillBrush, int anchorKind)
        {
            var rt = RenderTarget;
            if (rt == null || fillBrush == null) return;

            using (var path = new SharpDX.Direct2D1.PathGeometry(rt.Factory))
            {
                bool hasFigure = false;

                using (var sink = path.Open())
                {
                    for (int i = fromBar; i < toBar; i++)
                    {
                        if (i < 0 || i + 1 >= Bars.Count) continue;
                        VwapBarData d0 = anchor.At(i), d1 = anchor.At(i + 1);
                        if (d0 == null || d1 == null) continue;
                        if (!ShouldRenderBandSegment(d0, d1, anchorKind)) continue;

                        float x0 = cc.GetXByBarIndex(ChartBars, i);
                        float x1 = cc.GetXByBarIndex(ChartBars, i + 1);
                        if (x1 <= x0) continue;

                        if (bandNum == 1)
                        {
                            hasFigure |= AddBandQuadFigure(sink, cs, x0, x1, d0.Band1Upper, d1.Band1Upper, d0.Band1Lower, d1.Band1Lower);
                        }
                        else if (bandNum == 2)
                        {
                            hasFigure |= AddBandQuadFigure(sink, cs, x0, x1, d0.Band2Upper, d1.Band2Upper, d0.Band1Upper, d1.Band1Upper);
                            hasFigure |= AddBandQuadFigure(sink, cs, x0, x1, d0.Band2Lower, d1.Band2Lower, d0.Band1Lower, d1.Band1Lower);
                        }
                        else if (bandNum == 3)
                        {
                            hasFigure |= AddBandQuadFigure(sink, cs, x0, x1, d0.Band3Upper, d1.Band3Upper, d0.Band2Upper, d1.Band2Upper);
                            hasFigure |= AddBandQuadFigure(sink, cs, x0, x1, d0.Band3Lower, d1.Band3Lower, d0.Band2Lower, d1.Band2Lower);
                        }
                    }

                    sink.Close();
                }

                if (hasFigure)
                    rt.FillGeometry(path, fillBrush);
            }
        }

        private bool ShouldRenderBandSegment(VwapBarData d0, VwapBarData d1, int anchorKind)
        {
            bool anchorEnabled = anchorKind == AnchorKindEth
                ? EthBandsEnabled
                : anchorKind == AnchorKindRth
                    ? RthBandsEnabled
                    : ContinuousBandsEnabled;
            if (!anchorEnabled)
                return false;

            if (BandWindowMode == IQVwapSuiteBandWindow.CustomEtTimes)
                return d0.InBandWindow && d1.InBandWindow;

            if (anchorKind == AnchorKindEth)
                return GetEthSessionStartEt(d0.BarEt) == GetEthSessionStartEt(d1.BarEt);
            if (anchorKind == AnchorKindRth)
                return d0.InRthSession && d1.InRthSession;
            return true;
        }

        private static bool AddBandQuadFigure(SharpDX.Direct2D1.GeometrySink sink, ChartScale cs,
            float x0, float x1, double outer0, double outer1, double inner0, double inner1)
        {
            float yOuter0 = cs.GetYByValue(outer0);
            float yOuter1 = cs.GetYByValue(outer1);
            float yInner0 = cs.GetYByValue(inner0);
            float yInner1 = cs.GetYByValue(inner1);

            if (!IsFinite(yOuter0) || !IsFinite(yOuter1) || !IsFinite(yInner0) || !IsFinite(yInner1))
                return false;

            sink.BeginFigure(new SharpDX.Vector2(x0, yOuter0), SharpDX.Direct2D1.FigureBegin.Filled);
            sink.AddLine(new SharpDX.Vector2(x1, yOuter1));
            sink.AddLine(new SharpDX.Vector2(x1, yInner1));
            sink.AddLine(new SharpDX.Vector2(x0, yInner0));
            sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
            return true;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private void DrawStyledLine(float x1, float y1, float x2, float y2,
            SharpDX.Direct2D1.SolidColorBrush brush, float strokeWidth, IQVwapLineStyle style)
        {
            if (brush == null) return;
            var rt = RenderTarget;

            if (style == IQVwapLineStyle.Solid)
            {
                rt.DrawLine(new SharpDX.Vector2(x1, y1), new SharpDX.Vector2(x2, y2), brush, strokeWidth);
                return;
            }

            float dashLen = style == IQVwapLineStyle.Dashed ? 8f : 3f;
            float gapLen  = style == IQVwapLineStyle.Dashed ? 4f : 3f;
            float total   = (float)Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
            if (total < 1f) return;
            float dx = (x2 - x1) / total, dy = (y2 - y1) / total;
            float pos = 0f; bool drawing = true;
            while (pos < total)
            {
                float seg = drawing ? dashLen : gapLen;
                float end = Math.Min(pos + seg, total);
                if (drawing)
                    rt.DrawLine(new SharpDX.Vector2(x1 + dx * pos, y1 + dy * pos),
                                new SharpDX.Vector2(x1 + dx * end, y1 + dy * end), brush, strokeWidth);
                pos += seg; drawing = !drawing;
            }
        }

        #endregion
        // ═══════════════════════════════════════════════════════════════
        #region SharpDX resource management

        private void CreateDXResources()
        {
            var rt = RenderTarget;
            if (rt == null) { dxReady = false; return; }
            DisposeDXResources();

            try
            {
                dxWriteFactory = new SharpDX.DirectWrite.Factory();
                dxLabelFormat  = new SharpDX.DirectWrite.TextFormat(dxWriteFactory, "Consolas", 12f);

                float lineA = LineOpacity / 100f;
                dxEthBrush   = MakeBrush(rt, EthColor,        lineA);
                dxRthBrush   = MakeBrush(rt, RthColor,        lineA);
                dxContBrush  = MakeBrush(rt, ContinuousColor, lineA);
                dxAboveBrush = MakeBrush(rt, AboveColor,      lineA);
                dxBelowBrush = MakeBrush(rt, BelowColor,      lineA);

                dxBand1Brush = MakeBrush(rt, Band1Color, Band1Opacity / 100f);
                dxBand2Brush = MakeBrush(rt, Band2Color, Band2Opacity / 100f);
                dxBand3Brush = MakeBrush(rt, Band3Color, Band3Opacity / 100f);
                dxBand1FillBrush = MakeBrush(rt, Band1FillColor, Band1FillOpacity / 100f);
                dxBand2FillBrush = MakeBrush(rt, Band2FillColor, Band2FillOpacity / 100f);
                dxBand3FillBrush = MakeBrush(rt, Band3FillColor, Band3FillOpacity / 100f);

                dxReady = true;
            }
            catch (Exception ex)
            {
                Print("IQVwapSuite: CreateDXResources failed [" + ex.GetType().Name + "]: " + ex.Message);
                dxReady = false;
                DisposeDXResources();
            }
        }

        private static SharpDX.Direct2D1.SolidColorBrush MakeBrush(SharpDX.Direct2D1.RenderTarget rt, Brush wpfBrush, float opacity)
        {
            var scb = wpfBrush as SolidColorBrush;
            if (scb != null)
            {
                System.Windows.Media.Color c;
                try { c = scb.Color; } catch (InvalidOperationException) { c = Colors.White; }
                return new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, opacity));
            }
            return new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(1f, 1f, 1f, opacity));
        }

        private void DisposeDXResources()
        {
            DisposeRef(ref dxWriteFactory);
            DisposeRef(ref dxLabelFormat);
            DisposeRef(ref dxEthBrush);
            DisposeRef(ref dxRthBrush);
            DisposeRef(ref dxContBrush);
            DisposeRef(ref dxAboveBrush);
            DisposeRef(ref dxBelowBrush);
            DisposeRef(ref dxBand1Brush);
            DisposeRef(ref dxBand2Brush);
            DisposeRef(ref dxBand3Brush);
            DisposeRef(ref dxBand1FillBrush);
            DisposeRef(ref dxBand2FillBrush);
            DisposeRef(ref dxBand3FillBrush);
        }

        private static void DisposeRef<T>(ref T r) where T : class, IDisposable
        {
            if (r != null) { r.Dispose(); r = null; }
        }

        #endregion
    }
}
