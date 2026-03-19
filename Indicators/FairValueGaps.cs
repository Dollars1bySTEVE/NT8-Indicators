#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
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
    public class FairValueGaps : Indicator
    {
        #region Private Classes
        private class FVGZone
        {
            public int FormationBar { get; set; }
            public int PrimarySeriesBar { get; set; }
            public double HighPrice { get; set; }
            public double LowPrice { get; set; }
            public double OriginalHigh { get; set; }
            public double OriginalLow { get; set; }
            public FVGDirection Direction { get; set; }
            public double FillPercent { get; set; }
            public bool IsFilled { get; set; }
            public int TimeframeMinutes { get; set; }
            public int DataSeriesIndex { get; set; }
            public DateTime FormationTime { get; set; }
            public int MitigationBar { get; set; }
            public double MidPrice => (OriginalHigh + OriginalLow) / 2.0;
            public double GapSize => OriginalHigh - OriginalLow;
        }
        #endregion

        #region Private Fields
        private List<FVGZone> fvgZones;
        private List<FVGZone> htfZones;
        private List<FVGZone> ltfZones;
        
        private int htfDataSeriesIndex = -1;
        private int ltfDataSeriesIndex = -1;
        private int currentTFMinutes;
        private int higherTFMinutes;
        private int lowerTFMinutes;
        private DateTime lastSessionDate = DateTime.MinValue;
        private int lastPrimaryBar = 0;
        
        private SharpDX.Direct2D1.SolidColorBrush dxBullishZoneBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBearishZoneBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBullishBorderBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBearishBorderBrush;
        
        private SharpDX.Direct2D1.SolidColorBrush dxHTFBullishZoneBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxHTFBearishZoneBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxHTFBullishBorderBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxHTFBearishBorderBrush;
        
        private SharpDX.Direct2D1.SolidColorBrush dxLTFBullishZoneBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxLTFBearishZoneBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxLTFBullishBorderBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxLTFBearishBorderBrush;
        
        private SharpDX.Direct2D1.SolidColorBrush dxMidlineBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxTextBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxTextBgBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxOverlapBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxFilledZoneBrush;
        private SharpDX.DirectWrite.TextFormat dxTextFormat;
        private bool dxResourcesCreated;
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Fair Value Gaps (FVG) Indicator - DGT Style. Original Concept by DGT (dgtrd) on TradingView. NinjaTrader 8 port by Dollars1bySTEVE. IMPORTANT: Disable Auto Scale in chart properties.";
                Name = "FairValueGaps";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = true;
                MinGapSize = 0.0;
                MaxHistoricalGaps = 50;
                FillLogicMode = FVGFillLogic.AnyTouch;
                AutoRemoveFilled = true;
                ShowPartiallyFilled = true;
                MaxZoneAgeBars = 0;
                MaxZonesPerTimeframe = 50;
                ClearOnNewSession = false;
                MTFMode = FVGTimeframeMode.CurrentOnly;
                LTFSelectionMode = FVGLowerTFMode.Auto;
                ManualHTFMinutes = 60;
                ManualLTFMinutes = 1;
                BullishColor = Brushes.DodgerBlue;
                BearishColor = Brushes.Tomato;
                BullishBorderColor = Brushes.RoyalBlue;
                BearishBorderColor = Brushes.Crimson;
                ZoneOpacity = 25;
                BorderWidth = 1;
                HTFBullishColor = Brushes.MediumPurple;
                HTFBearishColor = Brushes.Maroon;
                HTFBullishBorderColor = Brushes.BlueViolet;
                HTFBearishBorderColor = Brushes.DarkRed;
                HTFOpacity = 40;
                HTFBorderWidth = 2;
                LTFBullishColor = Brushes.Cyan;
                LTFBearishColor = Brushes.Orange;
                LTFBullishBorderColor = Brushes.DarkCyan;
                LTFBearishBorderColor = Brushes.DarkOrange;
                LTFOpacity = 20;
                LTFBorderWidth = 1;
                ShowMidline = true;
                MidlineColor = Brushes.Gray;
                MidlineStyle = DashStyleHelper.Dot;
                MidlineWidth = 1;
                ShowOverlaps = true;
                OverlapColor = Brushes.Gold;
                OverlapOpacity = 40;
                ShowLabels = true;
                ShowFillPercent = true;
                LabelFontSize = 9;
                LabelPosition = FVGLabelPosition.InsideTop;
                FilledZoneOpacity = 15;
                EnableDebug = false;
            }
            else if (State == State.Configure)
            {
                currentTFMinutes = GetTimeframeMinutes(BarsPeriod);
                if (EnableDebug) Print("[FVG] Configuring... Current TF: " + currentTFMinutes + "m, MTF Mode: " + MTFMode);
                if (MTFMode == FVGTimeframeMode.WithHigherTF || MTFMode == FVGTimeframeMode.AllTimeframes)
                {
                    higherTFMinutes = ManualHTFMinutes > currentTFMinutes ? ManualHTFMinutes : GetAutoHigherTF(currentTFMinutes);
                    if (higherTFMinutes > currentTFMinutes)
                    {
                        AddDataSeries(BarsPeriodType.Minute, higherTFMinutes);
                        htfDataSeriesIndex = 1;
                        if (EnableDebug) Print("[FVG] Added HTF: " + higherTFMinutes + "m (index " + htfDataSeriesIndex + ")");
                    }
                }
                if (MTFMode == FVGTimeframeMode.WithLowerTF || MTFMode == FVGTimeframeMode.AllTimeframes)
                {
                    if (LTFSelectionMode == FVGLowerTFMode.Auto) lowerTFMinutes = GetAutoLowerTF(currentTFMinutes);
                    else lowerTFMinutes = ManualLTFMinutes < currentTFMinutes ? ManualLTFMinutes : GetAutoLowerTF(currentTFMinutes);
                    if (lowerTFMinutes < currentTFMinutes && lowerTFMinutes >= 1)
                    {
                        int seriesIdx = htfDataSeriesIndex > 0 ? 2 : 1;
                        AddDataSeries(BarsPeriodType.Minute, lowerTFMinutes);
                        ltfDataSeriesIndex = seriesIdx;
                        if (EnableDebug) Print("[FVG] Added LTF: " + lowerTFMinutes + "m (index " + ltfDataSeriesIndex + ")");
                    }
                }
            }
            else if (State == State.DataLoaded)
            {
                fvgZones = new List<FVGZone>();
                htfZones = new List<FVGZone>();
                ltfZones = new List<FVGZone>();
                if (EnableDebug) Print("[FVG] Data loaded. HTF index: " + htfDataSeriesIndex + ", LTF index: " + ltfDataSeriesIndex);
            }
            else if (State == State.Terminated) { DisposeSharpDXResources(); }
        }

        private int GetTimeframeMinutes(BarsPeriod bp)
        {
            if (bp == null) return 1;
            switch (bp.BarsPeriodType)
            {
                case BarsPeriodType.Minute: return bp.Value;
                case BarsPeriodType.Day: return bp.Value * 1440;
                case BarsPeriodType.Week: return bp.Value * 10080;
                case BarsPeriodType.Month: return bp.Value * 43200;
                case BarsPeriodType.Second: return Math.Max(1, bp.Value / 60);
                default: return 1;
            }
        }

        private int GetAutoHigherTF(int currentTF)
        {
            if (currentTF <= 1) return 5;
            if (currentTF <= 5) return 15;
            if (currentTF <= 15) return 60;
            if (currentTF <= 60) return 240;
            if (currentTF <= 240) return 1440;
            return 10080;
        }

        private int GetAutoLowerTF(int currentTF)
        {
            if (currentTF >= 1440) return 240;
            if (currentTF >= 240) return 60;
            if (currentTF >= 60) return 15;
            if (currentTF >= 15) return 5;
            if (currentTF >= 5) return 1;
            return 1;
        }

        private string GetTimeframeLabel(int minutes)
        {
            if (minutes >= 10080) return (minutes / 10080) + "W";
            if (minutes >= 1440) return (minutes / 1440) + "D";
            if (minutes >= 60) return (minutes / 60) + "H";
            return minutes + "m";
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 0)
            {
                if (CurrentBar < 3) return;
                lastPrimaryBar = CurrentBar;
                if (ClearOnNewSession)
                {
                    DateTime currentDate = Time[0].Date;
                    if (lastSessionDate != DateTime.MinValue && currentDate != lastSessionDate)
                    { fvgZones.Clear(); htfZones.Clear(); ltfZones.Clear(); }
                    lastSessionDate = currentDate;
                }
                DetectFVG(0, currentTFMinutes, fvgZones, CurrentBar);
                UpdateFillStatus(fvgZones);
                UpdateFillStatus(htfZones);
                UpdateFillStatus(ltfZones);
                ApplyZoneExpiration(fvgZones);
                ApplyZoneExpiration(htfZones);
                ApplyZoneExpiration(ltfZones);
                if (EnableDebug && CurrentBar % 100 == 0)
                {
                    int htfFilled = htfZones.Count(z => z.IsFilled);
                    Print("[FVG] Bar " + CurrentBar + " | Current: " + fvgZones.Count + " (" + fvgZones.Count(z=>!z.IsFilled) + " active) | HTF: " + htfZones.Count + " (" + (htfZones.Count - htfFilled) + " active, " + htfFilled + " filled) | LTF: " + ltfZones.Count);
                }
            }
            else if (BarsInProgress == htfDataSeriesIndex && htfDataSeriesIndex > 0)
            {
                if (CurrentBars[htfDataSeriesIndex] < 3 || CurrentBars[0] < 1) return;
                int beforeCount = htfZones.Count;
                DetectFVG(htfDataSeriesIndex, higherTFMinutes, htfZones, CurrentBars[0]);
                if (EnableDebug && htfZones.Count > beforeCount)
                {
                    var z = htfZones[htfZones.Count - 1];
                    Print("[FVG] NEW HTF: " + GetTimeframeLabel(z.TimeframeMinutes) + " " + z.Direction + " @ " + z.OriginalHigh.ToString("F2") + "-" + z.OriginalLow.ToString("F2") + " | Bar: " + z.PrimarySeriesBar);
                }
            }
            else if (BarsInProgress == ltfDataSeriesIndex && ltfDataSeriesIndex > 0)
            {
                if (CurrentBars[ltfDataSeriesIndex] < 3 || CurrentBars[0] < 1) return;
                DetectFVG(ltfDataSeriesIndex, lowerTFMinutes, ltfZones, CurrentBars[0]);
            }
        }

        private void DetectFVG(int seriesIndex, int tfMinutes, List<FVGZone> targetList, int primaryBar)
        {
            double high0, low0, high2, low2; int seriesBar; DateTime barTime;
            if (seriesIndex == 0) { high0 = High[0]; low0 = Low[0]; high2 = High[2]; low2 = Low[2]; seriesBar = CurrentBar; barTime = Time[0]; }
            else { high0 = Highs[seriesIndex][0]; low0 = Lows[seriesIndex][0]; high2 = Highs[seriesIndex][2]; low2 = Lows[seriesIndex][2]; seriesBar = CurrentBars[seriesIndex]; barTime = Times[seriesIndex][0]; }
            if (low0 > high2)
            {
                double gapSize = low0 - high2;
                if (gapSize >= MinGapSize * TickSize && !ZoneExistsNear(targetList, barTime, FVGDirection.Bullish, low0, high2))
                    targetList.Add(new FVGZone { FormationBar = seriesBar, PrimarySeriesBar = primaryBar, HighPrice = low0, LowPrice = high2, OriginalHigh = low0, OriginalLow = high2, Direction = FVGDirection.Bullish, FillPercent = 0, IsFilled = false, TimeframeMinutes = tfMinutes, DataSeriesIndex = seriesIndex, FormationTime = barTime, MitigationBar = -1 });
            }
            if (high0 < low2)
            {
                double gapSize = low2 - high0;
                if (gapSize >= MinGapSize * TickSize && !ZoneExistsNear(targetList, barTime, FVGDirection.Bearish, low2, high0))
                    targetList.Add(new FVGZone { FormationBar = seriesBar, PrimarySeriesBar = primaryBar, HighPrice = low2, LowPrice = high0, OriginalHigh = low2, OriginalLow = high0, Direction = FVGDirection.Bearish, FillPercent = 0, IsFilled = false, TimeframeMinutes = tfMinutes, DataSeriesIndex = seriesIndex, FormationTime = barTime, MitigationBar = -1 });
            }
        }

        private bool ZoneExistsNear(List<FVGZone> zones, DateTime time, FVGDirection direction, double high, double low)
        {
            foreach (var z in zones)
            {
                if (z.Direction != direction) continue;
                if (Math.Abs((z.FormationTime - time).TotalMinutes) > 5) continue;
                if (Math.Abs(z.OriginalHigh - high) < TickSize * 2 && Math.Abs(z.OriginalLow - low) < TickSize * 2) return true;
            }
            return false;
        }

        private void UpdateFillStatus(List<FVGZone> zones)
        {
            if (zones == null || zones.Count == 0 || CurrentBar < 1) return;
            double currentHigh = High[0]; double currentLow = Low[0]; double currentClose = Close[0];
            foreach (var zone in zones)
            {
                if (zone.IsFilled) continue;
                double gapSize = zone.GapSize;
                if (gapSize <= 0) continue;
                if (zone.Direction == FVGDirection.Bullish)
                {
                    if (currentLow <= zone.HighPrice)
                    {
                        zone.HighPrice = Math.Min(zone.HighPrice, currentLow);
                        zone.FillPercent = Math.Min(100, ((zone.OriginalHigh - zone.HighPrice) / gapSize) * 100);
                        bool filled = false;
                        switch (FillLogicMode)
                        {
                            case FVGFillLogic.AnyTouch: filled = currentLow <= zone.OriginalLow; break;
                            case FVGFillLogic.Midpoint: filled = currentLow <= zone.MidPrice; break;
                            case FVGFillLogic.WickSweep: filled = currentLow < Math.Min(Open[0], Close[0]) && currentLow <= zone.OriginalLow; break;
                            case FVGFillLogic.BodyBeyond: filled = currentClose <= zone.OriginalLow; break;
                        }
                        if (filled) { zone.IsFilled = true; zone.MitigationBar = CurrentBar; zone.FillPercent = 100; }
                    }
                }
                else
                {
                    if (currentHigh >= zone.LowPrice)
                    {
                        zone.LowPrice = Math.Max(zone.LowPrice, currentHigh);
                        zone.FillPercent = Math.Min(100, ((zone.LowPrice - zone.OriginalLow) / gapSize) * 100);
                        bool filled = false;
                        switch (FillLogicMode)
                        {
                            case FVGFillLogic.AnyTouch: filled = currentHigh >= zone.OriginalHigh; break;
                            case FVGFillLogic.Midpoint: filled = currentHigh >= zone.MidPrice; break;
                            case FVGFillLogic.WickSweep: filled = currentHigh > Math.Max(Open[0], Close[0]) && currentHigh >= zone.OriginalHigh; break;
                            case FVGFillLogic.BodyBeyond: filled = currentClose >= zone.OriginalHigh; break;
                        }
                        if (filled) { zone.IsFilled = true; zone.MitigationBar = CurrentBar; zone.FillPercent = 100; }
                    }
                }
            }
        }

        private void ApplyZoneExpiration(List<FVGZone> zones)
        {
            if (zones == null) return;
            if (AutoRemoveFilled) zones.RemoveAll(z => z.IsFilled);
            if (MaxZoneAgeBars > 0)
            {
                if (AutoRemoveFilled) zones.RemoveAll(z => (CurrentBar - z.PrimarySeriesBar) > MaxZoneAgeBars);
                else zones.RemoveAll(z => !z.IsFilled && (CurrentBar - z.PrimarySeriesBar) > MaxZoneAgeBars);
            }
            if (MaxZonesPerTimeframe > 0)
            {
                if (AutoRemoveFilled) { if (zones.Count > MaxZonesPerTimeframe) zones.RemoveRange(0, zones.Count - MaxZonesPerTimeframe); }
                else
                {
                    var unfilled = zones.Where(z => !z.IsFilled).OrderBy(z => z.PrimarySeriesBar).ToList();
                    if (unfilled.Count > MaxZonesPerTimeframe)
                    {
                        int removeCount = unfilled.Count - MaxZonesPerTimeframe;
                        for (int i = 0; i < removeCount; i++) zones.Remove(unfilled[i]);
                    }
                }
            }
        }

        private SharpDX.Color4 ToColor4(Brush brush, float alpha)
        {
            if (brush == null) return new SharpDX.Color4(1f, 1f, 1f, alpha);
            var c = ((SolidColorBrush)brush).Color;
            return new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, alpha);
        }

        private void CreateSharpDXResources(SharpDX.Direct2D1.RenderTarget rt)
        {
            if (dxResourcesCreated) return;
            dxBullishZoneBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(BullishColor, ZoneOpacity / 100f));
            dxBearishZoneBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(BearishColor, ZoneOpacity / 100f));
            dxBullishBorderBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(BullishBorderColor, 0.9f));
            dxBearishBorderBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(BearishBorderColor, 0.9f));
            dxHTFBullishZoneBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(HTFBullishColor, HTFOpacity / 100f));
            dxHTFBearishZoneBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(HTFBearishColor, HTFOpacity / 100f));
            dxHTFBullishBorderBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(HTFBullishBorderColor, 0.9f));
            dxHTFBearishBorderBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(HTFBearishBorderColor, 0.9f));
            dxLTFBullishZoneBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(LTFBullishColor, LTFOpacity / 100f));
            dxLTFBearishZoneBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(LTFBearishColor, LTFOpacity / 100f));
            dxLTFBullishBorderBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(LTFBullishBorderColor, 0.9f));
            dxLTFBearishBorderBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(LTFBearishBorderColor, 0.9f));
            dxMidlineBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(MidlineColor, 0.6f));
            dxTextBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(1f, 1f, 1f, 0.95f));
            dxTextBgBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0f, 0f, 0f, 0.65f));
            dxOverlapBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, ToColor4(OverlapColor, OverlapOpacity / 100f));
            dxFilledZoneBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(0.5f, 0.5f, 0.5f, FilledZoneOpacity / 100f));
            dxTextFormat = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial", SharpDX.DirectWrite.FontWeight.Normal, SharpDX.DirectWrite.FontStyle.Normal, SharpDX.DirectWrite.FontStretch.Normal, Math.Max(8, LabelFontSize));
            dxResourcesCreated = true;
        }

        private void DisposeSharpDXResources()
        {
            if (dxBullishZoneBrush != null) { dxBullishZoneBrush.Dispose(); dxBullishZoneBrush = null; }
            if (dxBearishZoneBrush != null) { dxBearishZoneBrush.Dispose(); dxBearishZoneBrush = null; }
            if (dxBullishBorderBrush != null) { dxBullishBorderBrush.Dispose(); dxBullishBorderBrush = null; }
            if (dxBearishBorderBrush != null) { dxBearishBorderBrush.Dispose(); dxBearishBorderBrush = null; }
            if (dxHTFBullishZoneBrush != null) { dxHTFBullishZoneBrush.Dispose(); dxHTFBullishZoneBrush = null; }
            if (dxHTFBearishZoneBrush != null) { dxHTFBearishZoneBrush.Dispose(); dxHTFBearishZoneBrush = null; }
            if (dxHTFBullishBorderBrush != null) { dxHTFBullishBorderBrush.Dispose(); dxHTFBullishBorderBrush = null; }
            if (dxHTFBearishBorderBrush != null) { dxHTFBearishBorderBrush.Dispose(); dxHTFBearishBorderBrush = null; }
            if (dxLTFBullishZoneBrush != null) { dxLTFBullishZoneBrush.Dispose(); dxLTFBullishZoneBrush = null; }
            if (dxLTFBearishZoneBrush != null) { dxLTFBearishZoneBrush.Dispose(); dxLTFBearishZoneBrush = null; }
            if (dxLTFBullishBorderBrush != null) { dxLTFBullishBorderBrush.Dispose(); dxLTFBullishBorderBrush = null; }
            if (dxLTFBearishBorderBrush != null) { dxLTFBearishBorderBrush.Dispose(); dxLTFBearishBorderBrush = null; }
            if (dxMidlineBrush != null) { dxMidlineBrush.Dispose(); dxMidlineBrush = null; }
            if (dxTextBrush != null) { dxTextBrush.Dispose(); dxTextBrush = null; }
            if (dxTextBgBrush != null) { dxTextBgBrush.Dispose(); dxTextBgBrush = null; }
            if (dxOverlapBrush != null) { dxOverlapBrush.Dispose(); dxOverlapBrush = null; }
            if (dxFilledZoneBrush != null) { dxFilledZoneBrush.Dispose(); dxFilledZoneBrush = null; }
            if (dxTextFormat != null) { dxTextFormat.Dispose(); dxTextFormat = null; }
            dxResourcesCreated = false;
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (fvgZones == null || RenderTarget == null || chartControl == null || ChartBars == null) return;
            if (!dxResourcesCreated) CreateSharpDXResources(RenderTarget);
            int firstBar = ChartBars.FromIndex; int lastBar = ChartBars.ToIndex;
            if (ShowOverlaps && MTFMode != FVGTimeframeMode.CurrentOnly) RenderOverlaps(RenderTarget, chartControl, chartScale, firstBar, lastBar);
            if (ltfZones != null && ltfZones.Count > 0) RenderZones(RenderTarget, chartControl, chartScale, ltfZones, dxLTFBullishZoneBrush, dxLTFBearishZoneBrush, dxLTFBullishBorderBrush, dxLTFBearishBorderBrush, LTFBorderWidth, firstBar, lastBar);
            RenderZones(RenderTarget, chartControl, chartScale, fvgZones, dxBullishZoneBrush, dxBearishZoneBrush, dxBullishBorderBrush, dxBearishBorderBrush, BorderWidth, firstBar, lastBar);
            if (htfZones != null && htfZones.Count > 0) RenderZones(RenderTarget, chartControl, chartScale, htfZones, dxHTFBullishZoneBrush, dxHTFBearishZoneBrush, dxHTFBullishBorderBrush, dxHTFBearishBorderBrush, HTFBorderWidth, firstBar, lastBar);
        }

        private void RenderZones(SharpDX.Direct2D1.RenderTarget rt, ChartControl cc, ChartScale cs, List<FVGZone> zones, SharpDX.Direct2D1.SolidColorBrush bullFill, SharpDX.Direct2D1.SolidColorBrush bearFill, SharpDX.Direct2D1.SolidColorBrush bullBorder, SharpDX.Direct2D1.SolidColorBrush bearBorder, int borderWidth, int firstBar, int lastBar)
        {
            if (zones == null || zones.Count == 0) return;
            SharpDX.Direct2D1.StrokeStyle midStroke = null;
            if (ShowMidline)
            {
                var props = new SharpDX.Direct2D1.StrokeStyleProperties();
                props.DashStyle = MidlineStyle == DashStyleHelper.Dot ? SharpDX.Direct2D1.DashStyle.Dot : MidlineStyle == DashStyleHelper.Dash ? SharpDX.Direct2D1.DashStyle.Dash : MidlineStyle == DashStyleHelper.DashDot ? SharpDX.Direct2D1.DashStyle.DashDot : SharpDX.Direct2D1.DashStyle.Solid;
                midStroke = new SharpDX.Direct2D1.StrokeStyle(rt.Factory, props);
            }
            foreach (var zone in zones)
            {
                if (!ShowPartiallyFilled && zone.FillPercent > 0 && !zone.IsFilled) continue;
                int startBar = zone.PrimarySeriesBar;
                int endBar = zone.IsFilled && zone.MitigationBar > 0 ? zone.MitigationBar : lastPrimaryBar;
                if (endBar < firstBar || startBar > lastBar) continue;
                int drawStart = Math.Max(startBar, firstBar);
                int drawEnd = Math.Min(endBar, lastBar);
                if (drawEnd < drawStart) continue;
                double renderHigh, renderLow;
                if (zone.IsFilled) { renderHigh = zone.OriginalHigh; renderLow = zone.OriginalLow; }
                else { renderHigh = zone.HighPrice; renderLow = zone.LowPrice; }
                float x1 = cc.GetXByBarIndex(ChartBars, drawStart);
                float x2 = cc.GetXByBarIndex(ChartBars, drawEnd);
                float y1 = cs.GetYByValue(renderHigh);
                float y2 = cs.GetYByValue(renderLow);
                if (y2 <= y1 || x2 <= x1) continue;
                SharpDX.Direct2D1.SolidColorBrush fill;
                SharpDX.Direct2D1.SolidColorBrush border;
                if (zone.IsFilled) { fill = dxFilledZoneBrush; border = zone.Direction == FVGDirection.Bullish ? bullBorder : bearBorder; }
                else { fill = zone.Direction == FVGDirection.Bullish ? bullFill : bearFill; border = zone.Direction == FVGDirection.Bullish ? bullBorder : bearBorder; }
                var rect = new SharpDX.RectangleF(x1, y1, x2 - x1, y2 - y1);
                rt.FillRectangle(rect, fill);
                rt.DrawRectangle(rect, border, borderWidth);
                if (ShowMidline)
                {
                    float yMid = cs.GetYByValue(zone.MidPrice);
                    if (yMid > y1 && yMid < y2 && midStroke != null) rt.DrawLine(new SharpDX.Vector2(x1, yMid), new SharpDX.Vector2(x2, yMid), dxMidlineBrush, MidlineWidth, midStroke);
                }
                if (ShowLabels && startBar >= firstBar && startBar <= lastBar)
                {
                    float labelX = cc.GetXByBarIndex(ChartBars, startBar) + 3;
                    float labelY = LabelPosition == FVGLabelPosition.InsideBottom ? y2 - 14 : LabelPosition == FVGLabelPosition.AboveZone ? y1 - 14 : LabelPosition == FVGLabelPosition.BelowZone ? y2 + 2 : y1 + 2;
                    string label = GetTimeframeLabel(zone.TimeframeMinutes) + " FVG " + (zone.Direction == FVGDirection.Bullish ? "▲" : "▼");
                    if (ShowFillPercent && zone.FillPercent > 0) label += " | " + zone.FillPercent.ToString("F0") + "%";
                    if (zone.IsFilled) label += " ✓";
                    using (var layout = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, label, dxTextFormat, 250, 20))
                    {
                        var m = layout.Metrics;
                        rt.FillRectangle(new SharpDX.RectangleF(labelX - 2, labelY - 1, m.Width + 6, m.Height + 2), dxTextBgBrush);
                        rt.DrawTextLayout(new SharpDX.Vector2(labelX, labelY), layout, dxTextBrush);
                    }
                }
            }
            if (midStroke != null) midStroke.Dispose();
        }

        private void RenderOverlaps(SharpDX.Direct2D1.RenderTarget rt, ChartControl cc, ChartScale cs, int firstBar, int lastBar)
        {
            var all = new List<FVGZone>();
            all.AddRange(fvgZones.Where(z => !z.IsFilled));
            if (htfZones != null) all.AddRange(htfZones.Where(z => !z.IsFilled));
            if (ltfZones != null) all.AddRange(ltfZones.Where(z => !z.IsFilled));
            for (int i = 0; i < all.Count; i++)
            {
                for (int j = i + 1; j < all.Count; j++)
                {
                    var z1 = all[i]; var z2 = all[j];
                    if (z1.TimeframeMinutes == z2.TimeframeMinutes) continue;
                    double oHigh = Math.Min(z1.HighPrice, z2.HighPrice);
                    double oLow = Math.Max(z1.LowPrice, z2.LowPrice);
                    if (oHigh <= oLow) continue;
                    int startBar = Math.Max(z1.PrimarySeriesBar, z2.PrimarySeriesBar);
                    int endBar = lastPrimaryBar;
                    if (endBar < firstBar || startBar > lastBar) continue;
                    float x1 = cc.GetXByBarIndex(ChartBars, Math.Max(startBar, firstBar));
                    float x2 = cc.GetXByBarIndex(ChartBars, Math.Min(endBar, lastBar));
                    float y1 = cs.GetYByValue(oHigh);
                    float y2 = cs.GetYByValue(oLow);
                    if (y2 > y1 && x2 > x1) rt.FillRectangle(new SharpDX.RectangleF(x1, y1, x2 - x1, y2 - y1), dxOverlapBrush);
                }
            }
        }

        public override void OnRenderTargetChanged() { DisposeSharpDXResources(); }

        [NinjaScriptProperty][Range(0, 100)][Display(Name = "Min Gap Size (Ticks)", Order = 1, GroupName = "1. Detection")] public double MinGapSize { get; set; }
        [NinjaScriptProperty][Range(10, 500)][Display(Name = "Max Historical Gaps", Order = 2, GroupName = "1. Detection")] public int MaxHistoricalGaps { get; set; }
        [NinjaScriptProperty][Display(Name = "Fill Logic", Order = 3, GroupName = "1. Detection")] public FVGFillLogic FillLogicMode { get; set; }
        [NinjaScriptProperty][Display(Name = "Auto-Remove Filled", Order = 4, GroupName = "1. Detection")] public bool AutoRemoveFilled { get; set; }
        [NinjaScriptProperty][Display(Name = "Show Partially Filled", Order = 5, GroupName = "1. Detection")] public bool ShowPartiallyFilled { get; set; }
        [NinjaScriptProperty][Range(0, 10000)][Display(Name = "Max Zone Age (Bars)", Order = 1, GroupName = "2. Zone Expiration")] public int MaxZoneAgeBars { get; set; }
        [NinjaScriptProperty][Range(1, 500)][Display(Name = "Max Zones Per TF", Order = 2, GroupName = "2. Zone Expiration")] public int MaxZonesPerTimeframe { get; set; }
        [NinjaScriptProperty][Display(Name = "Clear On New Session", Order = 3, GroupName = "2. Zone Expiration")] public bool ClearOnNewSession { get; set; }
        [NinjaScriptProperty][Display(Name = "Timeframe Mode", Order = 1, GroupName = "3. Multi-Timeframe")] public FVGTimeframeMode MTFMode { get; set; }
        [NinjaScriptProperty][Display(Name = "Lower TF Selection", Order = 2, GroupName = "3. Multi-Timeframe")] public FVGLowerTFMode LTFSelectionMode { get; set; }
        [NinjaScriptProperty][Range(1, 10080)][Display(Name = "Higher TF (Minutes)", Order = 3, GroupName = "3. Multi-Timeframe")] public int ManualHTFMinutes { get; set; }
        [NinjaScriptProperty][Range(1, 1440)][Display(Name = "Lower TF (Minutes)", Order = 4, GroupName = "3. Multi-Timeframe")] public int ManualLTFMinutes { get; set; }
        [XmlIgnore][Display(Name = "Bullish Fill", Order = 1, GroupName = "4. Current TF Colors")] public Brush BullishColor { get; set; }
        [Browsable(false)] public string BullishColorSerializable { get { return Serialize.BrushToString(BullishColor); } set { BullishColor = Serialize.StringToBrush(value); } }
        [XmlIgnore][Display(Name = "Bearish Fill", Order = 2, GroupName = "4. Current TF Colors")] public Brush BearishColor { get; set; }
        [Browsable(false)] public string BearishColorSerializable { get { return Serialize.BrushToString(BearishColor); } set { BearishColor = Serialize.StringToBrush(value); } }
        [XmlIgnore][Display(Name = "Bullish Border", Order = 3, GroupName = "4. Current TF Colors")] public Brush BullishBorderColor { get; set; }
        [Browsable(false)] public string BullishBorderColorSerializable { get { return Serialize.BrushToString(BullishBorderColor); } set { BullishBorderColor = Serialize.StringToBrush(value); } }
        [XmlIgnore][Display(Name = "Bearish Border", Order = 4, GroupName = "4. Current TF Colors")] public Brush BearishBorderColor { get; set; }
        [Browsable(false)] public string BearishBorderColorSerializable { get { return Serialize.BrushToString(BearishBorderColor); } set { BearishBorderColor = Serialize.StringToBrush(value); } }
        [NinjaScriptProperty][Range(5, 80)][Display(Name = "Zone Opacity %", Order = 5, GroupName = "4. Current TF Colors")] public int ZoneOpacity { get; set; }
        [NinjaScriptProperty][Range(1, 5)][Display(Name = "Border Width", Order = 6, GroupName = "4. Current TF Colors")] public int BorderWidth { get; set; }
        [XmlIgnore][Display(Name = "HTF Bullish Fill", Order = 1, GroupName = "5. Higher TF Colors")] public Brush HTFBullishColor { get; set; }
        [Browsable(false)] public string HTFBullishColorSerializable { get { return Serialize.BrushToString(HTFBullishColor); } set { HTFBullishColor = Serialize.StringToBrush(value); } }
        [XmlIgnore][Display(Name = "HTF Bearish Fill", Order = 2, GroupName = "5. Higher TF Colors")] public Brush HTFBearishColor { get; set; }
        [Browsable(false)] public string HTFBearishColorSerializable { get { return Serialize.BrushToString(HTFBearishColor); } set { HTFBearishColor = Serialize.StringToBrush(value); } }
        [XmlIgnore][Display(Name = "HTF Bullish Border", Order = 3, GroupName = "5. Higher TF Colors")] public Brush HTFBullishBorderColor { get; set; }
        [Browsable(false)] public string HTFBullishBorderColorSerializable { get { return Serialize.BrushToString(HTFBullishBorderColor); } set { HTFBullishBorderColor = Serialize.StringToBrush(value); } }
        [XmlIgnore][Display(Name = "HTF Bearish Border", Order = 4, GroupName = "5. Higher TF Colors")] public Brush HTFBearishBorderColor { get; set; }
        [Browsable(false)] public string HTFBearishBorderColorSerializable { get { return Serialize.BrushToString(HTFBearishBorderColor); } set { HTFBearishBorderColor = Serialize.StringToBrush(value); } }
        [NinjaScriptProperty][Range(5, 80)][Display(Name = "HTF Opacity %", Order = 5, GroupName = "5. Higher TF Colors")] public int HTFOpacity { get; set; }
        [NinjaScriptProperty][Range(1, 5)][Display(Name = "HTF Border Width", Order = 6, GroupName = "5. Higher TF Colors")] public int HTFBorderWidth { get; set; }
        [XmlIgnore][Display(Name = "LTF Bullish Fill", Order = 1, GroupName = "6. Lower TF Colors")] public Brush LTFBullishColor { get; set; }
        [Browsable(false)] public string LTFBullishColorSerializable { get { return Serialize.BrushToString(LTFBullishColor); } set { LTFBullishColor = Serialize.StringToBrush(value); } }
        [XmlIgnore][Display(Name = "LTF Bearish Fill", Order = 2, GroupName = "6. Lower TF Colors")] public Brush LTFBearishColor { get; set; }
        [Browsable(false)] public string LTFBearishColorSerializable { get { return Serialize.BrushToString(LTFBearishColor); } set { LTFBearishColor = Serialize.StringToBrush(value); } }
        [XmlIgnore][Display(Name = "LTF Bullish Border", Order = 3, GroupName = "6. Lower TF Colors")] public Brush LTFBullishBorderColor { get; set; }
        [Browsable(false)] public string LTFBullishBorderColorSerializable { get { return Serialize.BrushToString(LTFBullishBorderColor); } set { LTFBullishBorderColor = Serialize.StringToBrush(value); } }
        [XmlIgnore][Display(Name = "LTF Bearish Border", Order = 4, GroupName = "6. Lower TF Colors")] public Brush LTFBearishBorderColor { get; set; }
        [Browsable(false)] public string LTFBearishBorderColorSerializable { get { return Serialize.BrushToString(LTFBearishBorderColor); } set { LTFBearishBorderColor = Serialize.StringToBrush(value); } }
        [NinjaScriptProperty][Range(5, 80)][Display(Name = "LTF Opacity %", Order = 5, GroupName = "6. Lower TF Colors")] public int LTFOpacity { get; set; }
        [NinjaScriptProperty][Range(1, 5)][Display(Name = "LTF Border Width", Order = 6, GroupName = "6. Lower TF Colors")] public int LTFBorderWidth { get; set; }
        [NinjaScriptProperty][Display(Name = "Show Midline", Order = 1, GroupName = "7. Midline")] public bool ShowMidline { get; set; }
        [XmlIgnore][Display(Name = "Midline Color", Order = 2, GroupName = "7. Midline")] public Brush MidlineColor { get; set; }
        [Browsable(false)] public string MidlineColorSerializable { get { return Serialize.BrushToString(MidlineColor); } set { MidlineColor = Serialize.StringToBrush(value); } }
        [NinjaScriptProperty][Display(Name = "Midline Style", Order = 3, GroupName = "7. Midline")] public DashStyleHelper MidlineStyle { get; set; }
        [NinjaScriptProperty][Range(1, 3)][Display(Name = "Midline Width", Order = 4, GroupName = "7. Midline")] public int MidlineWidth { get; set; }
        [NinjaScriptProperty][Display(Name = "Show Overlaps", Order = 1, GroupName = "8. Overlaps")] public bool ShowOverlaps { get; set; }
        [XmlIgnore][Display(Name = "Overlap Color", Order = 2, GroupName = "8. Overlaps")] public Brush OverlapColor { get; set; }
        [Browsable(false)] public string OverlapColorSerializable { get { return Serialize.BrushToString(OverlapColor); } set { OverlapColor = Serialize.StringToBrush(value); } }
        [NinjaScriptProperty][Range(10, 80)][Display(Name = "Overlap Opacity %", Order = 3, GroupName = "8. Overlaps")] public int OverlapOpacity { get; set; }
        [NinjaScriptProperty][Display(Name = "Show Labels", Order = 1, GroupName = "9. Labels")] public bool ShowLabels { get; set; }
        [NinjaScriptProperty][Display(Name = "Show Fill %", Order = 2, GroupName = "9. Labels")] public bool ShowFillPercent { get; set; }
        [NinjaScriptProperty][Range(8, 14)][Display(Name = "Font Size", Order = 3, GroupName = "9. Labels")] public int LabelFontSize { get; set; }
        [NinjaScriptProperty][Display(Name = "Label Position", Order = 4, GroupName = "9. Labels")] public FVGLabelPosition LabelPosition { get; set; }
        [NinjaScriptProperty][Range(5, 50)][Display(Name = "Filled Zone Opacity %", Order = 1, GroupName = "10. Filled Zones")] public int FilledZoneOpacity { get; set; }
        [NinjaScriptProperty][Display(Name = "Enable Debug", Order = 1, GroupName = "11. Debug")] public bool EnableDebug { get; set; }
    }
}

public enum FVGDirection { Bullish, Bearish }
public enum FVGFillLogic { [Description("Any Touch")] AnyTouch, [Description("Midpoint (50%)")] Midpoint, [Description("Wick Sweep")] WickSweep, [Description("Body Beyond")] BodyBeyond }
public enum FVGTimeframeMode { [Description("Current Only")] CurrentOnly, [Description("With Higher TF")] WithHigherTF, [Description("With Lower TF")] WithLowerTF, [Description("All Timeframes")] AllTimeframes }
public enum FVGLowerTFMode { Auto, Manual }
public enum FVGLabelPosition { [Description("Inside Top")] InsideTop, [Description("Inside Bottom")] InsideBottom, [Description("Above Zone")] AboveZone, [Description("Below Zone")] BelowZone }
