// IQVwapSuite — Standalone VWAP indicator for NinjaTrader 8.
// Extracted from the VWAP engine inside IQMainUltimate.cs and extended with a third,
// continuous (24/7) anchor.  All three anchors share the same feature set:
//   • ETH VWAP        — resets at 18:00 ET (CME Globex daily open), DST-safe
//   • RTH VWAP        — resets at 09:30 ET (US cash open), DST-safe, only drawn during RTH
//   • 24/7 VWAP       — continuous; never resets, or resets weekly (Sunday 18:00 ET) by choice
//   • ±1σ / ±2σ / ±3σ standard-deviation bands (volume-weighted variance), each with
//     independent show / color / opacity / thickness
//   • Optional fill between bands
//   • Dynamic line color (price above / below / neutral) or fixed per-anchor color
//   • Line thickness, line style (solid / dashed / dotted), opacity
//   • Right-edge price labels with per-anchor label text
//   • SharpDX GPU rendering throughout — no NinjaTrader draw objects
//
// This file is fully self-contained: it does NOT depend on IQMainGPU.cs, IQMainGPU_Enhanced.cs
// or IQMainUltimate.cs and can be imported into a clean NT8 install on its own.

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

// NinjaTrader 8 requires custom enums declared OUTSIDE all namespaces.
// Names are prefixed with IQVwap to avoid clashing with enums in IQMainGPU.cs.

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

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// IQVwapSuite — ETH, RTH and 24/7 VWAP with standard-deviation bands, GPU rendered.
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
        }

        /// <summary>One VWAP accumulator (ETH, RTH or Continuous).</summary>
        private class VwapAnchor
        {
            public List<VwapBarData> Data = new List<VwapBarData>(5000);   // indexed by bar index
            public double   CumPV;
            public double   CumVol;
            public double   CumTPVSq;
            public DateTime SessionStart = DateTime.MinValue;

            public void Reset(DateTime start)
            {
                SessionStart = start;
                CumPV = CumVol = CumTPVSq = 0;
            }

            public VwapBarData Accumulate(double tp, double vol)
            {
                if (vol > 0)
                {
                    CumPV    += tp * vol;
                    CumVol   += vol;
                    CumTPVSq += tp * tp * vol;
                }
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

        // ET time-zone helper — "Eastern Standard Time" is the Windows TZ id; handles EST↔EDT.
        private static readonly TimeZoneInfo EtZone = SafeFindEtZone();
        private static TimeZoneInfo SafeFindEtZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch (Exception) { return TimeZoneInfo.CreateCustomTimeZone("ET-Fallback", TimeSpan.FromHours(-5), "ET-Fallback", "ET-Fallback"); }
        }

        private DateTime BarTimeEt()
        {
            DateTime t = DateTime.SpecifyKind(Time[0], DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTime(t, Bars.TradingHours.TimeZoneInfo, EtZone);
        }

        // SharpDX resources
        private bool dxReady;
        private SharpDX.DirectWrite.Factory    dxWriteFactory;
        private SharpDX.DirectWrite.TextFormat dxLabelFormat;

        private SharpDX.Direct2D1.SolidColorBrush dxAboveBrush, dxBelowBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxEthBrush, dxRthBrush, dxContBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBand1Brush, dxBand2Brush, dxBand3Brush;
        private SharpDX.Direct2D1.SolidColorBrush dxFillBrush;

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
        [Display(Name = "Fill Between Bands", Order = 13, GroupName = "3. Bands")]
        public bool FillBands { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Band Fill Color", Order = 14, GroupName = "3. Bands")]
        [XmlIgnore]
        public Brush FillColor { get; set; }
        [Browsable(false)]
        public string FillColorSerializable { get => Serialize.BrushToString(FillColor); set => FillColor = Serialize.StringToBrush(value); }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Band Fill Opacity %", Order = 15, GroupName = "3. Bands")]
        public int FillOpacity { get; set; }

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
                FillBands      = false;
                FillColor      = Brushes.Yellow;
                FillOpacity    = 15;
            }
            else if (State == State.Configure)
            {
                MaximumBarsLookBack = MaximumBarsLookBack.Infinite;
            }
            else if (State == State.DataLoaded)
            {
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
            if (!(IsFirstTickOfBar || Calculate == Calculate.OnBarClose)) { ForceRefresh(); return; }

            DateTime barEt = BarTimeEt();
            double tp  = (High[0] + Low[0] + Close[0]) / 3.0;
            double vol = Volume[0];

            // ── ETH (18:00 ET → 18:00 ET) ─────────────────────────────────
            DateTime ethStart = GetEthSessionStartEt(barEt);
            if (ethAnchor.SessionStart != ethStart) ethAnchor.Reset(ethStart);
            ethAnchor.Store(CurrentBar, ethAnchor.Accumulate(tp, vol));

            // ── RTH (09:30 ET → 16:00 ET) ─────────────────────────────────
            DateTime rthStart = barEt.Date.AddHours(9).AddMinutes(30);
            DateTime rthEnd   = barEt.Date.AddHours(16);
            bool inRth = barEt >= rthStart && barEt < rthEnd;
            if (inRth)
            {
                if (rthAnchor.SessionStart != rthStart) rthAnchor.Reset(rthStart);
                rthAnchor.Store(CurrentBar, rthAnchor.Accumulate(tp, vol));
            }
            else if (!RthOnlyDuringSession && rthAnchor.CumVol > 0)
            {
                // Carry last RTH value flat through the overnight (no accumulation)
                rthAnchor.Store(CurrentBar, rthAnchor.Accumulate(tp, 0));
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
            contAnchor.Store(CurrentBar, contAnchor.Accumulate(tp, vol));

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
                if (ShowContinuousVwap) RenderVwapLine(chartControl, chartScale, fromBar, toBar, contAnchor, ContinuousLabel, dxContBrush);
                if (ShowEthVwap)        RenderVwapLine(chartControl, chartScale, fromBar, toBar, ethAnchor,  EthLabel,        dxEthBrush);
                if (ShowRthVwap)        RenderVwapLine(chartControl, chartScale, fromBar, toBar, rthAnchor,  RthLabel,        dxRthBrush);
            }
            catch (SharpDX.SharpDXException sdx) { Print("IQVwapSuite: SharpDX error: " + sdx.Message); dxReady = false; DisposeDXResources(); }
            catch (Exception ex) { Print("IQVwapSuite: render error [" + ex.GetType().Name + "]: " + ex.Message); }
        }

        private void RenderVwapLine(ChartControl cc, ChartScale cs, int fromBar, int toBar,
            VwapAnchor anchor, string label, SharpDX.Direct2D1.SolidColorBrush fixedBrush)
        {
            var rt = RenderTarget;
            if (rt == null || anchor == null || anchor.Data.Count == 0) return;

            // Bands behind the line
            if (ShowBand3 && dxBand3Brush != null) RenderBandPair(cc, cs, fromBar, toBar, anchor, 3, dxBand3Brush, Band3Thickness);
            if (ShowBand2 && dxBand2Brush != null) RenderBandPair(cc, cs, fromBar, toBar, anchor, 2, dxBand2Brush, Band2Thickness);
            if (ShowBand1 && dxBand1Brush != null) RenderBandPair(cc, cs, fromBar, toBar, anchor, 1, dxBand1Brush, Band1Thickness);

            // VWAP line
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

        private void RenderBandPair(ChartControl cc, ChartScale cs, int fromBar, int toBar,
            VwapAnchor anchor, int bandNum, SharpDX.Direct2D1.SolidColorBrush brush, int thickness)
        {
            var rt = RenderTarget;
            if (rt == null) return;

            for (int i = fromBar; i < toBar; i++)
            {
                if (i < 0 || i + 1 >= Bars.Count) continue;
                VwapBarData d0 = anchor.At(i), d1 = anchor.At(i + 1);
                if (d0 == null || d1 == null) continue;

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

                DrawStyledLine(x0, yU0, x1, yU1, brush, thickness, IQVwapLineStyle.Dashed);
                DrawStyledLine(x0, yL0, x1, yL1, brush, thickness, IQVwapLineStyle.Dashed);

                if (FillBands && dxFillBrush != null)
                {
                    float top = Math.Min(yU0, yU1), bottom = Math.Max(yL0, yL1), w = x1 - x0;
                    if (w > 0 && bottom > top)
                        rt.FillRectangle(new SharpDX.RectangleF(x0, top, w, bottom - top), dxFillBrush);
                }
            }
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
                dxFillBrush  = MakeBrush(rt, FillColor,  FillOpacity  / 100f);

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
            DisposeRef(ref dxFillBrush);
        }

        private static void DisposeRef<T>(ref T r) where T : class, IDisposable
        {
            if (r != null) { r.Dispose(); r = null; }
        }

        #endregion
    }
}
