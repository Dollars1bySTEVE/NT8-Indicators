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
    public class DGT_PriceAction_SR : Indicator
    {
        #region Private Variables

        private SMA volumeSMA;
        private ATR atrIndicator;
        private MIN lowestLow3;
        private MAX highestHigh3;

        private double[] vpVolStorage;
        private double[] vpVolStorageBull;
        private double vpPriceHi;
        private double vpPriceLo;
        private double vpPriceStep;
        private double vpMaxVol;
        private int vpPocIdx;
        private int vpVAHIdx;
        private int vpVALIdx;
        private double vpPocPrice;
        private double vpVAHPrice;
        private double vpVALPrice;
        private bool vpReady;
        private int vpCacheFrom;
        private int vpCacheTo;

        private List<SRLevel> srLevels;
        private List<SpikeZone> spikeZones;
        private List<SpikeZone> hiVolZones;

        private SharpDX.Direct2D1.SolidColorBrush dxHVN;
        private SharpDX.Direct2D1.SolidColorBrush dxAVN;
        private SharpDX.Direct2D1.SolidColorBrush dxLVN;
        private SharpDX.Direct2D1.SolidColorBrush dxBull;
        private SharpDX.Direct2D1.SolidColorBrush dxBear;
        private SharpDX.Direct2D1.SolidColorBrush dxPOC;
        private SharpDX.Direct2D1.SolidColorBrush dxVAH;
        private SharpDX.Direct2D1.SolidColorBrush dxVAL;
        private SharpDX.Direct2D1.SolidColorBrush dxSupply;
        private SharpDX.Direct2D1.SolidColorBrush dxDemand;
        private SharpDX.Direct2D1.SolidColorBrush dxBorder;
        private SharpDX.Direct2D1.SolidColorBrush dxTxt;
        private SharpDX.DirectWrite.Factory dwFactory;
        private SharpDX.DirectWrite.TextFormat dwFmt;

        private System.Windows.Media.SolidColorBrush wpfHiBull;
        private System.Windows.Media.SolidColorBrush wpfHiBear;
        private System.Windows.Media.SolidColorBrush wpfLoBull;
        private System.Windows.Media.SolidColorBrush wpfLoBear;

        #endregion

        private class SRLevel
        {
            public int BarIndex;
            public double Price;
            public bool IsBull;
        }

        private class SpikeZone
        {
            public int BarIndex;
            public double ExtPrice;
            public double ClsPrice;
            public double MidPrice;
            public bool IsBull;
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"DGT Price Action S/R ported from TradingView. Level 1 data only.";
                Name = "DGT Price Action SR";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                DrawHorizontalGridLines = true;
                DrawVerticalGridLines = true;
                PaintPriceMarkers = true;
                ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                IsSuspendedWhileInactive = true;
                BarsRequiredToPlot = 20;

                // 01 SR Targets
                SREnabled = true;
                SRSource = DGTSRSource.Volume;
                SRLookback = 360;
                SRBullColor = System.Windows.Media.Brushes.LimeGreen;
                SRBearColor = System.Windows.Media.Brushes.MediumSeaGreen;
                SRWidth = 2;
                SRDash = DashStyleHelper.Solid;
                SROpacity = 70;
                SRMaxLines = 10;
                SRDedup = 10;
                SRExtendFull = true;
                SRShowLabels = true;

                // 02 Volume Spike
                SpikeEnabled = true;
                SpikeThreshold = 4.669;
                SpikeDisplay = DGTDisplayMode.Lines;
                SpikeBullColor = System.Windows.Media.Brushes.DodgerBlue;
                SpikeBearColor = System.Windows.Media.Brushes.Salmon;
                SpikeWidth = 1;
                SpikeDash = DashStyleHelper.Solid;
                SpikeOpacity = 50;
                SpikeZoneOpacity = 5;
                SpikeBullLvl = DGTLevel.Both;
                SpikeBearLvl = DGTLevel.Both;
                SpikeMarkers = true;
                SpikeLabels = true;
                SpikeMaxZones = 8;
                SpikeDedup = 8;

                // 03 High Volatility
                HVEnabled = true;
                HVAtrLen = 11;
                HVAtrMult = 2.718;
                HVDisplay = DGTDisplayMode.Both;
                HVBullColor = System.Windows.Media.Brushes.Teal;
                HVBearColor = System.Windows.Media.Brushes.Crimson;
                HVWidth = 1;
                HVDash = DashStyleHelper.Solid;
                HVOpacity = 50;
                HVZoneOpacity = 5;
                HVBullLvl = DGTLevel.Both;
                HVBearLvl = DGTLevel.Both;
                HVMarkers = true;
                HVLabels = true;
                HVMaxZones = 8;
                HVDedup = 8;

                // 04 Volume Profile
                VPEnabled = true;
                VPMode = DGTLookback.VisibleRange;
                VPFixedBars = 360;
                VPRows = 100;
                VPVA = 68;
                VPShowPOC = true;
                VPShowVAH = true;
                VPShowVAL = true;
                VPPocClr = System.Windows.Media.Brushes.Red;
                VPVahClr = System.Windows.Media.Brushes.Yellow;
                VPValClr = System.Windows.Media.Brushes.Yellow;
                VPHvnClr = System.Windows.Media.Brushes.DarkOrange;
                VPAvnClr = System.Windows.Media.Brushes.Gray;
                VPLvnClr = System.Windows.Media.Brushes.DimGray;
                VPBullClr = System.Windows.Media.Brushes.Teal;
                VPBearClr = System.Windows.Media.Brushes.Crimson;
                VPOpacity = 60;
                VPLabels = true;
                VPWidthPct = 30;

                // 05 Supply & Demand
                SDEnabled = true;
                SDThreshold = 10;
                SupplyColor = System.Windows.Media.Brushes.Red;
                DemandColor = System.Windows.Media.Brushes.Teal;
                SDOpacity = 10;

                // 06 Bar Coloring
                BarColorEnabled = true;
                BarHiThr = 1.618;
                BarLoThr = 0.618;
                VolSmaLen = 89;
            }
            else if (State == State.DataLoaded)
            {
                volumeSMA = SMA(Volume, VolSmaLen);
                atrIndicator = ATR(HVAtrLen);
                lowestLow3 = MIN(Low, 3);
                highestHigh3 = MAX(High, 3);

                vpVolStorage = new double[VPRows + 1];
                vpVolStorageBull = new double[VPRows + 1];
                vpReady = false;
                vpCacheFrom = -1;
                vpCacheTo = -1;
                srLevels = new List<SRLevel>();
                spikeZones = new List<SpikeZone>();
                hiVolZones = new List<SpikeZone>();

                wpfHiBull = MkFrz(0, 100, 0);
                wpfHiBear = MkFrz(145, 0, 0);
                wpfLoBull = MkFrz(127, 255, 212);
                wpfLoBear = MkFrz(255, 152, 0);

                dwFactory = new SharpDX.DirectWrite.Factory();
            }
            else if (State == State.Terminated)
            {
                DisposeDX();
                if (dwFmt != null) { dwFmt.Dispose(); dwFmt = null; }
                if (dwFactory != null) { dwFactory.Dispose(); dwFactory = null; }
            }
        }

        private System.Windows.Media.SolidColorBrush MkFrz(byte r, byte g, byte b)
        {
            var br = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            br.Freeze(); return br;
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < Math.Max(VolSmaLen, HVAtrLen) + 5)
                return;

            double v0 = Volume[0] > 0 ? Volume[0] : 0;
            double v1 = Volume[1] > 0 ? Volume[1] : 0;
            double v2 = Volume[2] > 0 ? Volume[2] : 0;
            bool vUp = v0 >= v1;
            bool vUp1 = v1 >= v2;

            bool bull = Close[0] > Open[0];
            bool bear = Close[0] < Open[0];
            bool bull1 = Close[1] > Open[1];
            bool bull2 = Close[2] > Open[2];
            bool bear1 = Close[1] < Open[1];
            bool bear2 = Close[2] < Open[2];

            bool pUp = Close[0] > Close[1];
            bool pUp1 = Close[1] > Close[2];
            bool pUp2 = Close[2] > Close[3];
            bool pDn = Close[0] < Close[1];
            bool pDn1 = Close[1] < Close[2];
            bool pDn2 = Close[2] > Close[3];

            double lo3 = lowestLow3[0];
            double hi3 = highestHigh3[0];
            double sma = volumeSMA[0];
            double wATR = HVAtrMult * atrIndicator[0];
            double bRng = Math.Abs(High[0] - Low[0]);
            double srDedupRange = SRDedup * TickSize;
            double spkDedupRange = SpikeDedup * TickSize;
            double hvDedupRange = HVDedup * TickSize;

            // Trim by lookback
            srLevels.RemoveAll(l => CurrentBar - l.BarIndex > SRLookback);
            spikeZones.RemoveAll(l => CurrentBar - l.BarIndex > SRLookback);
            hiVolZones.RemoveAll(l => CurrentBar - l.BarIndex > SRLookback);

            // ── 1. S&R TARGETS ──
            if (SREnabled)
            {
                bool fall, rise;
                if (SRSource == DGTSRSource.Volume)
                {
                    fall = bear && bear1 && bear2 && v0 > sma && vUp && vUp1;
                    rise = bull && bull1 && bull2 && v0 > sma && vUp && vUp1;
                }
                else
                {
                    fall = bear && bear1 && bear2 && pDn && pDn1 && pDn2;
                    rise = bull && bull1 && bull2 && pUp && pUp1 && pUp2;
                }
                if (fall)
                    AddDedupedSR(srLevels, new SRLevel { BarIndex = CurrentBar, Price = lo3, IsBull = false }, srDedupRange, SRMaxLines);
                if (rise)
                    AddDedupedSR(srLevels, new SRLevel { BarIndex = CurrentBar, Price = hi3, IsBull = true }, srDedupRange, SRMaxLines);
            }

            // ── 2. VOLUME SPIKE ──
            if (SpikeEnabled && v0 > 0 && v0 > SpikeThreshold * sma)
            {
                if (SpikeMarkers)
                    Draw.Diamond(this, "Ex_" + CurrentBar, false, 0,
                        High[0] + 3 * TickSize, MakeFrozen(bull ? SpikeBullColor : SpikeBearColor, 90));

                double ext = bull ? High[0] : Low[0];
                double cls = Close[0];
                double mid = (ext + cls) / 2.0;
                var newZone = new SpikeZone { BarIndex = CurrentBar, ExtPrice = ext, ClsPrice = cls, MidPrice = mid, IsBull = bull };
                AddDedupedZone(spikeZones, newZone, spkDedupRange, SpikeMaxZones);
            }

            // ── 3. HIGH VOLATILITY ──
            if (HVEnabled && bRng > wATR)
            {
                if (HVMarkers)
                    Draw.Diamond(this, "HV_" + CurrentBar, false, 0,
                        Low[0] - 3 * TickSize, MakeFrozen(bull ? HVBullColor : HVBearColor, 90));

                double ext = bull ? High[0] : Low[0];
                double cls = Close[0];
                double mid = (ext + cls) / 2.0;
                var newZone = new SpikeZone { BarIndex = CurrentBar, ExtPrice = ext, ClsPrice = cls, MidPrice = mid, IsBull = bull };
                AddDedupedZone(hiVolZones, newZone, hvDedupRange, HVMaxZones);
            }

            // ── 4. COLORED BARS ──
            if (BarColorEnabled && v0 > 0)
            {
                if (v0 > sma * BarHiThr)
                { BarBrushes[0] = bear ? wpfHiBear : wpfHiBull; CandleOutlineBrushes[0] = BarBrushes[0]; }
                else if (v0 < sma * BarLoThr)
                { BarBrushes[0] = bear ? wpfLoBear : wpfLoBull; CandleOutlineBrushes[0] = BarBrushes[0]; }
            }

            // ── 5. ALERTS ──
            if (State == State.Realtime)
            {
                string nm = Instrument.MasterInstrument.Name;
                if (v0 > sma * SpikeThreshold && SpikeEnabled)
                    Alert("Ex_" + CurrentBar, Priority.Medium, nm + " Spike " + Close[0].ToString("F2"),
                        NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav", 10,
                        System.Windows.Media.Brushes.Orange, System.Windows.Media.Brushes.Black);
                if (bRng > wATR && HVEnabled)
                    Alert("HV_" + CurrentBar, Priority.Medium, nm + " HiVol " + Close[0].ToString("F2"),
                        NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10,
                        System.Windows.Media.Brushes.LimeGreen, System.Windows.Media.Brushes.Black);
            }
        }

        #region Level Management

        private void AddDedupedSR(List<SRLevel> list, SRLevel newLvl, double dedupRange, int maxCount)
        {
            list.RemoveAll(l => Math.Abs(l.Price - newLvl.Price) <= dedupRange);
            list.Add(newLvl);
            while (list.Count > maxCount) list.RemoveAt(0);
        }

        private void AddDedupedZone(List<SpikeZone> list, SpikeZone newZone, double dedupRange, int maxCount)
        {
            // Remove if new zone's extreme OR close is within dedup range of existing zone's extreme
            list.RemoveAll(z => Math.Abs(z.ExtPrice - newZone.ExtPrice) <= dedupRange
                             || Math.Abs(z.ClsPrice - newZone.ExtPrice) <= dedupRange
                             || Math.Abs(z.ExtPrice - newZone.ClsPrice) <= dedupRange);
            list.Add(newZone);
            while (list.Count > maxCount) list.RemoveAt(0);
        }

        #endregion

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (chartControl == null || chartScale == null || ChartBars == null || Bars == null) return;
            if (Bars.Count < 2 || IsInHitTest) return;

            try
            {
                SharpDX.Direct2D1.RenderTarget rt = RenderTarget;
                if (rt == null) return;
                MakeDX(rt);

                int fi = ChartBars.FromIndex;
                int li = ChartBars.ToIndex;
                if (fi < 0 || li < 0 || fi >= li) return;

                float canvasLeft = (float)chartControl.CanvasLeft;
                float canvasRight = (float)chartControl.CanvasRight;
                float lastBarX = chartControl.GetXByBarIndex(ChartBars, li);
                float lineStopX = lastBarX + (float)chartControl.Properties.BarDistance;
                if (lineStopX > canvasRight) lineStopX = canvasRight;

                // ── S&R TARGET LINES (split bull/bear) ──
                if (SREnabled && srLevels != null)
                {
                    foreach (var lv in srLevels)
                    {
                        float y = chartScale.GetYByValue(lv.Price);
                        float startX = SRExtendFull ? canvasLeft :
                            chartControl.GetXByBarIndex(ChartBars, Math.Max(fi, Math.Min(lv.BarIndex, li)));
                        var clr = lv.IsBull ? SRBullColor : SRBearColor;

                        using (var brush = DXB2(rt, clr, SROpacity / 100f))
                        {
                            if (SRDash != DashStyleHelper.Solid)
                            { using (var style = MakeStroke(rt, SRDash)) { rt.DrawLine(V2(startX, y), V2(lineStopX, y), brush, SRWidth, style); } }
                            else
                                rt.DrawLine(V2(startX, y), V2(lineStopX, y), brush, SRWidth);

                            if (SRShowLabels)
                                DXLabelClipped(rt, lineStopX + 3, y, lv.Price.ToString("F1"), brush, canvasRight);
                        }
                    }
                }

                // ── SPIKE ZONES/LINES ──
                if (SpikeEnabled && spikeZones != null)
                    RenderZones(rt, chartControl, chartScale, spikeZones, SpikeDisplay, SpikeBullColor, SpikeBearColor,
                        SpikeWidth, SpikeDash, SpikeOpacity, SpikeZoneOpacity, SpikeBullLvl, SpikeBearLvl,
                        SpikeLabels, fi, li, lineStopX, canvasRight, true);

                // ── HIVOL ZONES/LINES ──
                if (HVEnabled && hiVolZones != null)
                    RenderZones(rt, chartControl, chartScale, hiVolZones, HVDisplay, HVBullColor, HVBearColor,
                        HVWidth, HVDash, HVOpacity, HVZoneOpacity, HVBullLvl, HVBearLvl,
                        HVLabels, fi, li, lineStopX, canvasRight, false);

                // ── VOLUME PROFILE & S/D ──
                if (VPEnabled || SDEnabled)
                {
                    int pf, pl;
                    if (VPMode == DGTLookback.VisibleRange) { pf = fi; pl = li; }
                    else { pl = li; pf = Math.Max(0, li - VPFixedBars); }

                    if (!vpReady || pf != vpCacheFrom || pl != vpCacheTo)
                    { CalcVP(pf, pl); vpCacheFrom = pf; vpCacheTo = pl; }

                    if (vpReady && vpMaxVol > 0)
                    {
                        float marginW = canvasRight - lastBarX;
                        float profW = Math.Max(20f, marginW * (VPWidthPct / 100f));
                        float gap = Math.Max(4f, (marginW - profW) * 0.15f);
                        float profL = lastBarX + gap;
                        float profR = profL + profW;
                        if (profR > canvasRight - 2) profR = canvasRight - 2;
                        profW = profR - profL;
                        float vpSX = chartControl.GetXByBarIndex(ChartBars, Math.Max(fi, pf));
                        float lblX = profR + 3;

                        // S&D zones
                        if (SDEnabled)
                        {
                            float sdR = lastBarX;
                            double thr = SDThreshold / 100.0;
                            double midPrice = vpPocPrice;

                            for (int lv = 0; lv < VPRows; lv++)
                            {
                                if (vpVolStorage[lv] / vpMaxVol < thr)
                                {
                                    double zonePrice = vpPriceLo + (lv + 0.5) * vpPriceStep;
                                    float yt = chartScale.GetYByValue(vpPriceLo + (lv + 1) * vpPriceStep);
                                    float yb = chartScale.GetYByValue(vpPriceLo + lv * vpPriceStep);
                                    var zoneBrush = zonePrice >= midPrice ? dxSupply : dxDemand;
                                    rt.FillRectangle(new SharpDX.RectangleF(vpSX, yt, sdR - vpSX, yb - yt), zoneBrush);
                                }
                            }
                        }

                        // Volume profile histogram
                        if (VPEnabled)
                        {
                            float bt = chartScale.GetYByValue(vpPriceHi);
                            float bb = chartScale.GetYByValue(vpPriceLo);
                            using (var ds = new SharpDX.Direct2D1.StrokeStyle(rt.Factory,
                                new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Dot }))
                            { rt.DrawRectangle(new SharpDX.RectangleF(vpSX, bt, profR - vpSX, bb - bt), dxBorder, 1f, ds); }

                            for (int lv = 0; lv < VPRows; lv++)
                            {
                                double r = vpVolStorage[lv] / vpMaxVol;
                                float bw = (float)(r * profW);
                                float yt = chartScale.GetYByValue(vpPriceLo + (lv + 0.75) * vpPriceStep);
                                float yb = chartScale.GetYByValue(vpPriceLo + (lv + 0.25) * vpPriceStep);
                                float bh = Math.Max(1f, yb - yt);
                                var nb = r > 0.8 ? dxHVN : r < 0.2 ? dxLVN : dxAVN;
                                rt.FillRectangle(new SharpDX.RectangleF(profR - bw, yt, bw, bh), nb);

                                double bv = vpVolStorageBull[lv]; double tv = vpVolStorage[lv];
                                if (tv > 0)
                                {
                                    double bp = 2 * bv - tv;
                                    float dw = (float)(Math.Abs(bp) / vpMaxVol * profW * 0.5);
                                    float domX = profR + 2;
                                    if (domX + dw < canvasRight - 2)
                                        rt.FillRectangle(new SharpDX.RectangleF(domX, yt, dw, bh), bp > 0 ? dxBull : dxBear);
                                }
                            }

                            if (VPShowPOC) { float y = chartScale.GetYByValue(vpPocPrice); rt.DrawLine(V2(vpSX, y), V2(profR, y), dxPOC, 2f); if (VPLabels) DXLabelClipped(rt, lblX, y, "POC " + vpPocPrice.ToString("F2"), dxPOC, canvasRight); }
                            if (VPShowVAH) { float y = chartScale.GetYByValue(vpVAHPrice); rt.DrawLine(V2(vpSX, y), V2(profR, y), dxVAH, 2f); if (VPLabels) DXLabelClipped(rt, lblX, y, "VAH " + vpVAHPrice.ToString("F2"), dxVAH, canvasRight); }
                            if (VPShowVAL) { float y = chartScale.GetYByValue(vpVALPrice); rt.DrawLine(V2(vpSX, y), V2(profR, y), dxVAL, 2f); if (VPLabels) DXLabelClipped(rt, lblX, y, "VAL " + vpVALPrice.ToString("F2"), dxVAL, canvasRight); }
                            if (VPLabels)
                            {
                                DXLabelClipped(rt, lblX, chartScale.GetYByValue(vpPriceHi), vpPriceHi.ToString("F2") + " H", dxTxt, canvasRight);
                                DXLabelClipped(rt, lblX, chartScale.GetYByValue(vpPriceLo), vpPriceLo.ToString("F2") + " L", dxTxt, canvasRight);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Print("DGT Render: " + ex.Message); }
        }

        #region Zone/Line Renderer

        private void RenderZones(SharpDX.Direct2D1.RenderTarget rt, ChartControl cc, ChartScale cs,
            List<SpikeZone> zones, DGTDisplayMode mode,
            System.Windows.Media.Brush bullClr, System.Windows.Media.Brush bearClr,
            int lineW, DashStyleHelper dash, int lineOp, int zoneOp,
            DGTLevel bullLvl, DGTLevel bearLvl, bool showLabels,
            int fi, int li, float lineStopX, float canvasRight, bool isSpike)
        {
            foreach (var z in zones)
            {
                if (z.BarIndex < fi) continue;
                float startX = cc.GetXByBarIndex(ChartBars, Math.Max(fi, Math.Min(z.BarIndex, li)));
                var clr = z.IsBull ? bullClr : bearClr;
                DGTLevel lvl = z.IsBull ? bullLvl : bearLvl;

                // Zone fill
                if (mode == DGTDisplayMode.Zone || mode == DGTDisplayMode.Both)
                {
                    float yExt = cs.GetYByValue(z.ExtPrice);
                    float yCls = cs.GetYByValue(z.ClsPrice);
                    float yTop = Math.Min(yExt, yCls);
                    float yBot = Math.Max(yExt, yCls);
                    float zH = Math.Max(1f, yBot - yTop);

                    using (var zoneBrush = DXB2(rt, clr, zoneOp / 100f))
                    { rt.FillRectangle(new SharpDX.RectangleF(startX, yTop, lineStopX - startX, zH), zoneBrush); }
                }

                // Lines
                if (mode == DGTDisplayMode.Lines || mode == DGTDisplayMode.Both)
                {
                    using (var lineBrush = DXB2(rt, clr, lineOp / 100f))
                    {
                        if (lvl == DGTLevel.HighOrLow || lvl == DGTLevel.Both)
                        {
                            float y = cs.GetYByValue(z.ExtPrice);
                            DrawStyledLine(rt, startX, y, lineStopX, lineBrush, lineW, dash);
                        }
                        if (lvl == DGTLevel.Close || lvl == DGTLevel.Both)
                        {
                            float y = cs.GetYByValue(z.ClsPrice);
                            DrawStyledLine(rt, startX, y, lineStopX, lineBrush, lineW, dash);
                        }
                        if (lvl == DGTLevel.Both)
                        {
                            float y = cs.GetYByValue(z.MidPrice);
                            using (var dotStyle = MakeStroke(rt, DashStyleHelper.Dot))
                            { rt.DrawLine(V2(startX, y), V2(lineStopX, y), lineBrush, Math.Max(1, lineW - 1), dotStyle); }
                        }
                    }
                }

                // Label
                if (showLabels)
                {
                    string arrow = z.IsBull ? (isSpike ? "\u25B2 " : "\u25B3 ") : (isSpike ? "\u25BC " : "\u25BD ");
                    float labelY = cs.GetYByValue(z.ExtPrice);
                    using (var lblBrush = DXB2(rt, clr, 0.9f))
                    { DXLabelClipped(rt, lineStopX + 3, labelY, arrow + z.ExtPrice.ToString("F1"), lblBrush, canvasRight); }
                }
            }
        }

        private void DrawStyledLine(SharpDX.Direct2D1.RenderTarget rt, float x1, float y, float x2,
            SharpDX.Direct2D1.SolidColorBrush brush, int w, DashStyleHelper dash)
        {
            if (dash != DashStyleHelper.Solid)
            { using (var style = MakeStroke(rt, dash)) { rt.DrawLine(V2(x1, y), V2(x2, y), brush, w, style); } }
            else
                rt.DrawLine(V2(x1, y), V2(x2, y), brush, w);
        }

        #endregion

        #region VP Calc

        private void CalcVP(int from, int to)
        {
            vpReady = false;
            if (from < 0 || to <= from) return;
            to = Math.Min(to, Bars.Count - 1); from = Math.Max(from, 0);
            if (vpVolStorage == null || vpVolStorage.Length != VPRows + 1)
            { vpVolStorage = new double[VPRows + 1]; vpVolStorageBull = new double[VPRows + 1]; }

            vpPriceHi = double.MinValue; vpPriceLo = double.MaxValue;
            for (int i = from; i <= to; i++)
            { if (Bars.GetHigh(i) > vpPriceHi) vpPriceHi = Bars.GetHigh(i); if (Bars.GetLow(i) < vpPriceLo) vpPriceLo = Bars.GetLow(i); }
            if (vpPriceHi <= vpPriceLo) return;
            vpPriceStep = (vpPriceHi - vpPriceLo) / VPRows;
            if (vpPriceStep <= 0) return;

            Array.Clear(vpVolStorage, 0, vpVolStorage.Length);
            Array.Clear(vpVolStorageBull, 0, vpVolStorageBull.Length);

            for (int i = from; i <= to; i++)
            {
                double h = Bars.GetHigh(i), l = Bars.GetLow(i), vol = Bars.GetVolume(i), sp = h - l;
                bool bu = Bars.GetClose(i) > Bars.GetOpen(i);
                if (vol <= 0) continue;
                for (int lv = 0; lv < VPRows; lv++)
                {
                    double ll = vpPriceLo + lv * vpPriceStep, lh = ll + vpPriceStep;
                    if (h >= ll && l < lh)
                    { double vc = sp == 0 ? vol : vol * (vpPriceStep / sp); vpVolStorage[lv] += vc; if (bu) vpVolStorageBull[lv] += vc; }
                }
            }

            vpMaxVol = 0; vpPocIdx = 0;
            for (int i = 0; i < VPRows; i++) if (vpVolStorage[i] > vpMaxVol) { vpMaxVol = vpVolStorage[i]; vpPocIdx = i; }
            if (vpMaxVol <= 0) return;

            double tot = 0; for (int i = 0; i < VPRows; i++) tot += vpVolStorage[i];
            double vat = tot * (VPVA / 100.0), vaa = vpVolStorage[vpPocIdx];
            vpVAHIdx = vpPocIdx; vpVALIdx = vpPocIdx;
            while (vaa < vat)
            {
                if (vpVALIdx <= 0 && vpVAHIdx >= VPRows - 1) break;
                double a = vpVAHIdx < VPRows - 1 ? vpVolStorage[vpVAHIdx + 1] : 0;
                double b = vpVALIdx > 0 ? vpVolStorage[vpVALIdx - 1] : 0;
                if (a >= b) { vaa += a; vpVAHIdx++; } else { vaa += b; vpVALIdx--; }
            }
            vpPocPrice = vpPriceLo + (vpPocIdx + 0.5) * vpPriceStep;
            vpVAHPrice = vpPriceLo + (vpVAHIdx + 1.0) * vpPriceStep;
            vpVALPrice = vpPriceLo + vpVALIdx * vpPriceStep;
            vpReady = true;
        }

        #endregion

        #region DX Resources

        private void MakeDX(SharpDX.Direct2D1.RenderTarget rt)
        {
            if (dxHVN != null && !dxHVN.IsDisposed) return;
            float po = VPOpacity / 100f; float so = SDOpacity / 100f;
            dxHVN = DXB(rt, VPHvnClr, po); dxAVN = DXB(rt, VPAvnClr, po); dxLVN = DXB(rt, VPLvnClr, po * 0.5f);
            dxBull = DXB(rt, VPBullClr, 0.5f); dxBear = DXB(rt, VPBearClr, 0.5f);
            dxPOC = DXB(rt, VPPocClr, 1f); dxVAH = DXB(rt, VPVahClr, 1f); dxVAL = DXB(rt, VPValClr, 1f);
            dxSupply = DXB(rt, SupplyColor, so);
            dxDemand = DXB(rt, DemandColor, so);
            dxBorder = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0.5f, 0.5f, 0.5f, 0.37f));
            dxTxt = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0.78f, 0.78f, 0.78f, 0.9f));
            if (dwFmt == null || dwFmt.IsDisposed)
                dwFmt = new SharpDX.DirectWrite.TextFormat(dwFactory, "Arial",
                    SharpDX.DirectWrite.FontWeight.Normal, SharpDX.DirectWrite.FontStyle.Normal, 11f);
        }

        private SharpDX.Direct2D1.SolidColorBrush DXB(SharpDX.Direct2D1.RenderTarget rt, System.Windows.Media.Brush w, float o)
        {
            if (w is System.Windows.Media.SolidColorBrush s)
                return new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(s.Color.R / 255f, s.Color.G / 255f, s.Color.B / 255f, o));
            return new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0.5f, 0.5f, 0.5f, o));
        }

        private SharpDX.Direct2D1.SolidColorBrush DXB2(SharpDX.Direct2D1.RenderTarget rt, System.Windows.Media.Brush w, float o)
        { return DXB(rt, w, o); }

        private SharpDX.Direct2D1.StrokeStyle MakeStroke(SharpDX.Direct2D1.RenderTarget rt, DashStyleHelper dash)
        {
            SharpDX.Direct2D1.DashStyle ds = SharpDX.Direct2D1.DashStyle.Solid;
            if (dash == DashStyleHelper.Dash) ds = SharpDX.Direct2D1.DashStyle.Dash;
            else if (dash == DashStyleHelper.DashDot) ds = SharpDX.Direct2D1.DashStyle.DashDot;
            else if (dash == DashStyleHelper.DashDotDot) ds = SharpDX.Direct2D1.DashStyle.DashDotDot;
            else if (dash == DashStyleHelper.Dot) ds = SharpDX.Direct2D1.DashStyle.Dot;
            return new SharpDX.Direct2D1.StrokeStyle(rt.Factory, new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = ds });
        }

        private SharpDX.Vector2 V2(float x, float y) { return new SharpDX.Vector2(x, y); }

        public override void OnRenderTargetChanged() { DisposeDX(); }

        private void DisposeDX()
        {
            Dp(ref dxHVN); Dp(ref dxAVN); Dp(ref dxLVN); Dp(ref dxBull); Dp(ref dxBear);
            Dp(ref dxPOC); Dp(ref dxVAH); Dp(ref dxVAL); Dp(ref dxSupply); Dp(ref dxDemand);
            Dp(ref dxBorder); Dp(ref dxTxt); Dp(ref dwFmt);
        }

        private void Dp<T>(ref T r) where T : class, IDisposable { if (r != null) { r.Dispose(); r = null; } }

        #endregion

        #region Helpers

        private void DXLabelClipped(SharpDX.Direct2D1.RenderTarget rt, float x, float y, string t, SharpDX.Direct2D1.Brush b, float maxX)
        {
            if (dwFactory == null || dwFmt == null) return;
            float availW = maxX - x - 2;
            if (availW < 20) return;
            using (var ly = new SharpDX.DirectWrite.TextLayout(dwFactory, t, dwFmt, availW, 20))
            { rt.DrawTextLayout(V2(x, y - 10), ly, b); }
        }

        private System.Windows.Media.SolidColorBrush MakeFrozen(System.Windows.Media.Brush bas, int op)
        {
            if (bas is System.Windows.Media.SolidColorBrush s)
            {
                var b = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb((byte)(255 * op / 100), s.Color.R, s.Color.G, s.Color.B));
                b.Freeze(); return b;
            }
            var fb = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);
            fb.Freeze(); return fb;
        }

        #endregion

        #region Properties

        // ── 01 S&R Targets ──
        [NinjaScriptProperty][Display(Name="Enable", Order=1, GroupName="01 SR Targets")]
        public bool SREnabled { get; set; }

        [NinjaScriptProperty][Display(Name="Source", Order=2, GroupName="01 SR Targets")]
        public DGTSRSource SRSource { get; set; }

        [NinjaScriptProperty][Range(10,5000)][Display(Name="Lookback", Order=3, GroupName="01 SR Targets")]
        public int SRLookback { get; set; }

        [XmlIgnore][Display(Name="Bull Color", Order=4, GroupName="01 SR Targets")]
        public System.Windows.Media.Brush SRBullColor { get; set; }
        [Browsable(false)] public string SRBullColorS { get { return Serialize.BrushToString(SRBullColor); } set { SRBullColor = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name="Bear Color", Order=5, GroupName="01 SR Targets")]
        public System.Windows.Media.Brush SRBearColor { get; set; }
        [Browsable(false)] public string SRBearColorS { get { return Serialize.BrushToString(SRBearColor); } set { SRBearColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty][Range(1,5)][Display(Name="Width", Order=6, GroupName="01 SR Targets")]
        public int SRWidth { get; set; }

        [NinjaScriptProperty][Display(Name="Style", Order=7, GroupName="01 SR Targets")]
        public DashStyleHelper SRDash { get; set; }

        [NinjaScriptProperty][Range(1,100)][Display(Name="Opacity", Order=8, GroupName="01 SR Targets")]
        public int SROpacity { get; set; }

        [NinjaScriptProperty][Range(3,30)][Display(Name="Max Lines", Order=9, GroupName="01 SR Targets")]
        public int SRMaxLines { get; set; }

        [NinjaScriptProperty][Range(1,50)][Display(Name="Dedup Ticks", Order=10, GroupName="01 SR Targets")]
        public int SRDedup { get; set; }

        [NinjaScriptProperty][Display(Name="Extend Full", Order=11, GroupName="01 SR Targets")]
        public bool SRExtendFull { get; set; }

        [NinjaScriptProperty][Display(Name="Labels", Order=12, GroupName="01 SR Targets")]
        public bool SRShowLabels { get; set; }

        // ── 02 Volume Spike ──
        [NinjaScriptProperty][Display(Name="Enable", Order=1, GroupName="02 Spike")]
        public bool SpikeEnabled { get; set; }

        [NinjaScriptProperty][Range(0.1,20.0)][Display(Name="Threshold", Order=2, GroupName="02 Spike")]
        public double SpikeThreshold { get; set; }

        [NinjaScriptProperty][Display(Name="Display", Order=3, GroupName="02 Spike")]
        public DGTDisplayMode SpikeDisplay { get; set; }

        [XmlIgnore][Display(Name="Bull Color", Order=4, GroupName="02 Spike")]
        public System.Windows.Media.Brush SpikeBullColor { get; set; }
        [Browsable(false)] public string SpikeBullColorS { get { return Serialize.BrushToString(SpikeBullColor); } set { SpikeBullColor = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name="Bear Color", Order=5, GroupName="02 Spike")]
        public System.Windows.Media.Brush SpikeBearColor { get; set; }
        [Browsable(false)] public string SpikeBearColorS { get { return Serialize.BrushToString(SpikeBearColor); } set { SpikeBearColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty][Range(1,5)][Display(Name="Width", Order=6, GroupName="02 Spike")]
        public int SpikeWidth { get; set; }

        [NinjaScriptProperty][Display(Name="Style", Order=7, GroupName="02 Spike")]
        public DashStyleHelper SpikeDash { get; set; }

        [NinjaScriptProperty][Range(1,100)][Display(Name="Line Opacity", Order=8, GroupName="02 Spike")]
        public int SpikeOpacity { get; set; }

        [NinjaScriptProperty][Range(5,50)][Display(Name="Zone Opacity", Order=9, GroupName="02 Spike")]
        public int SpikeZoneOpacity { get; set; }

        [NinjaScriptProperty][Display(Name="BullLvl", Order=10, GroupName="02 Spike")]
        public DGTLevel SpikeBullLvl { get; set; }

        [NinjaScriptProperty][Display(Name="BearLvl", Order=11, GroupName="02 Spike")]
        public DGTLevel SpikeBearLvl { get; set; }

        [NinjaScriptProperty][Display(Name="Markers", Order=12, GroupName="02 Spike")]
        public bool SpikeMarkers { get; set; }

        [NinjaScriptProperty][Display(Name="Labels", Order=13, GroupName="02 Spike")]
        public bool SpikeLabels { get; set; }

        [NinjaScriptProperty][Range(5,50)][Display(Name="Max Zones", Order=14, GroupName="02 Spike")]
        public int SpikeMaxZones { get; set; }

        [NinjaScriptProperty][Range(1,50)][Display(Name="Dedup Ticks", Order=15, GroupName="02 Spike")]
        public int SpikeDedup { get; set; }

        // ── 03 High Volatility ──
        [NinjaScriptProperty][Display(Name="Enable", Order=1, GroupName="03 HiVol")]
        public bool HVEnabled { get; set; }

        [NinjaScriptProperty][Range(1,100)][Display(Name="ATR Len", Order=2, GroupName="03 HiVol")]
        public int HVAtrLen { get; set; }

        [NinjaScriptProperty][Range(0.1,10.0)][Display(Name="ATR Mult", Order=3, GroupName="03 HiVol")]
        public double HVAtrMult { get; set; }

        [NinjaScriptProperty][Display(Name="Display", Order=4, GroupName="03 HiVol")]
        public DGTDisplayMode HVDisplay { get; set; }

        [XmlIgnore][Display(Name="Bull Color", Order=5, GroupName="03 HiVol")]
        public System.Windows.Media.Brush HVBullColor { get; set; }
        [Browsable(false)] public string HVBullColorS { get { return Serialize.BrushToString(HVBullColor); } set { HVBullColor = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name="Bear Color", Order=6, GroupName="03 HiVol")]
        public System.Windows.Media.Brush HVBearColor { get; set; }
        [Browsable(false)] public string HVBearColorS { get { return Serialize.BrushToString(HVBearColor); } set { HVBearColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty][Range(1,5)][Display(Name="Width", Order=7, GroupName="03 HiVol")]
        public int HVWidth { get; set; }

        [NinjaScriptProperty][Display(Name="Style", Order=8, GroupName="03 HiVol")]
        public DashStyleHelper HVDash { get; set; }

        [NinjaScriptProperty][Range(1,100)][Display(Name="Line Opacity", Order=9, GroupName="03 HiVol")]
        public int HVOpacity { get; set; }

        [NinjaScriptProperty][Range(5,50)][Display(Name="Zone Opacity", Order=10, GroupName="03 HiVol")]
        public int HVZoneOpacity { get; set; }

        [NinjaScriptProperty][Display(Name="BullLvl", Order=11, GroupName="03 HiVol")]
        public DGTLevel HVBullLvl { get; set; }

        [NinjaScriptProperty][Display(Name="BearLvl", Order=12, GroupName="03 HiVol")]
        public DGTLevel HVBearLvl { get; set; }

        [NinjaScriptProperty][Display(Name="Markers", Order=13, GroupName="03 HiVol")]
        public bool HVMarkers { get; set; }

        [NinjaScriptProperty][Display(Name="Labels", Order=14, GroupName="03 HiVol")]
        public bool HVLabels { get; set; }

        [NinjaScriptProperty][Range(5,50)][Display(Name="Max Zones", Order=15, GroupName="03 HiVol")]
        public int HVMaxZones { get; set; }

        [NinjaScriptProperty][Range(1,50)][Display(Name="Dedup Ticks", Order=16, GroupName="03 HiVol")]
        public int HVDedup { get; set; }

        // ── 04 Volume Profile ──
        [NinjaScriptProperty][Display(Name="Enable", Order=1, GroupName="04 VP")]
        public bool VPEnabled { get; set; }

        [NinjaScriptProperty][Display(Name="Mode", Order=2, GroupName="04 VP")]
        public DGTLookback VPMode { get; set; }

        [NinjaScriptProperty][Range(10,5000)][Display(Name="FixedBars", Order=3, GroupName="04 VP")]
        public int VPFixedBars { get; set; }

        [NinjaScriptProperty][Range(10,200)][Display(Name="Rows", Order=4, GroupName="04 VP")]
        public int VPRows { get; set; }

        [NinjaScriptProperty][Range(1,100)][Display(Name="VA Pct", Order=5, GroupName="04 VP")]
        public int VPVA { get; set; }

        [NinjaScriptProperty][Display(Name="POC", Order=6, GroupName="04 VP")]
        public bool VPShowPOC { get; set; }

        [NinjaScriptProperty][Display(Name="VAH", Order=7, GroupName="04 VP")]
        public bool VPShowVAH { get; set; }

        [NinjaScriptProperty][Display(Name="VAL", Order=8, GroupName="04 VP")]
        public bool VPShowVAL { get; set; }

        [XmlIgnore][Display(Name="POC Clr", Order=9, GroupName="04 VP")]
        public System.Windows.Media.Brush VPPocClr { get; set; }
        [Browsable(false)] public string VPPocClrS { get { return Serialize.BrushToString(VPPocClr); } set { VPPocClr = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name="VAH Clr", Order=10, GroupName="04 VP")]
        public System.Windows.Media.Brush VPVahClr { get; set; }
        [Browsable(false)] public string VPVahClrS { get { return Serialize.BrushToString(VPVahClr); } set { VPVahClr = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name="VAL Clr", Order=11, GroupName="04 VP")]
        public System.Windows.Media.Brush VPValClr { get; set; }
        [Browsable(false)] public string VPValClrS { get { return Serialize.BrushToString(VPValClr); } set { VPValClr = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name="HVN Clr", Order=12, GroupName="04 VP")]
        public System.Windows.Media.Brush VPHvnClr { get; set; }
        [Browsable(false)] public string VPHvnClrS { get { return Serialize.BrushToString(VPHvnClr); } set { VPHvnClr = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name="AVN Clr", Order=13, GroupName="04 VP")]
        public System.Windows.Media.Brush VPAvnClr { get; set; }
        [Browsable(false)] public string VPAvnClrS { get { return Serialize.BrushToString(VPAvnClr); } set { VPAvnClr = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name="LVN Clr", Order=14, GroupName="04 VP")]
        public System.Windows.Media.Brush VPLvnClr { get; set; }
        [Browsable(false)] public string VPLvnClrS { get { return Serialize.BrushToString(VPLvnClr); } set { VPLvnClr = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name="Bull Clr", Order=15, GroupName="04 VP")]
        public System.Windows.Media.Brush VPBullClr { get; set; }
        [Browsable(false)] public string VPBullClrS { get { return Serialize.BrushToString(VPBullClr); } set { VPBullClr = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name="Bear Clr", Order=16, GroupName="04 VP")]
        public System.Windows.Media.Brush VPBearClr { get; set; }
        [Browsable(false)] public string VPBearClrS { get { return Serialize.BrushToString(VPBearClr); } set { VPBearClr = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty][Range(10,100)][Display(Name="Opacity", Order=17, GroupName="04 VP")]
        public int VPOpacity { get; set; }

        [NinjaScriptProperty][Display(Name="Labels", Order=18, GroupName="04 VP")]
        public bool VPLabels { get; set; }

        [NinjaScriptProperty][Range(10,80)][Display(Name="Width %", Order=19, GroupName="04 VP")]
        public int VPWidthPct { get; set; }

        // ── 05 Supply & Demand ──
        [NinjaScriptProperty][Display(Name="Enable", Order=1, GroupName="05 Supply Demand")]
        public bool SDEnabled { get; set; }

        [NinjaScriptProperty][Range(0,50)][Display(Name="Threshold", Order=2, GroupName="05 Supply Demand")]
        public int SDThreshold { get; set; }

        [XmlIgnore][Display(Name="Supply Color", Order=3, GroupName="05 Supply Demand")]
        public System.Windows.Media.Brush SupplyColor { get; set; }
        [Browsable(false)] public string SupplyColorS { get { return Serialize.BrushToString(SupplyColor); } set { SupplyColor = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name="Demand Color", Order=4, GroupName="05 Supply Demand")]
        public System.Windows.Media.Brush DemandColor { get; set; }
        [Browsable(false)] public string DemandColorS { get { return Serialize.BrushToString(DemandColor); } set { DemandColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty][Range(5,80)][Display(Name="Opacity", Order=5, GroupName="05 Supply Demand")]
        public int SDOpacity { get; set; }

        // ── 06 Bar Coloring ──
        [NinjaScriptProperty][Display(Name="Enable", Order=1, GroupName="06 Bars")]
        public bool BarColorEnabled { get; set; }

        [NinjaScriptProperty][Range(1.0,10.0)][Display(Name="Hi Thr", Order=2, GroupName="06 Bars")]
        public double BarHiThr { get; set; }

        [NinjaScriptProperty][Range(0.1,1.0)][Display(Name="Lo Thr", Order=3, GroupName="06 Bars")]
        public double BarLoThr { get; set; }

        // ── 07 General ──
        [NinjaScriptProperty][Range(10,500)][Display(Name="Vol SMA", Order=1, GroupName="07 General")]
        public int VolSmaLen { get; set; }

        #endregion
    }
}

public enum DGTSRSource { Volume, Price }
public enum DGTLevel { [Description("High/Low")] HighOrLow, Close, Both }
public enum DGTLookback { [Description("Visible Range")] VisibleRange, [Description("Fixed Range")] FixedRange }
public enum DGTDisplayMode { Lines, Zone, Both }
