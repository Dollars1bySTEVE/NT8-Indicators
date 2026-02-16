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

// DO NOT add "using SharpDX.Direct2D1" or "using SharpDX.DirectWrite" at the top level
// to avoid ambiguous type references with System.Windows.Media.
// Instead, fully qualify all SharpDX types inline (proven pattern from working NT8 indicators).

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SmartMoneyStructure : Indicator
    {
        #region Enums
        public enum StructureType
        {
            BOS,
            CHoCH
        }

        public enum StructureDirection
        {
            Bullish,
            Bearish
        }
        #endregion

        #region Private Classes
        private class SwingPoint
        {
            public int BarIndex { get; set; }
            public double Price { get; set; }
            public bool IsHigh { get; set; }
        }

        private class StructureEvent
        {
            public StructureType Type { get; set; }
            public StructureDirection Direction { get; set; }
            public int StartBar { get; set; }
            public double Price { get; set; }
            public int EndBar { get; set; }
        }
        #endregion

        #region Private Fields
        private List<SwingPoint> swingHighs;
        private List<SwingPoint> swingLows;
        private List<StructureEvent> structureEvents;
        private StructureDirection currentTrend;
        private bool trendEstablished;

        private SharpDX.Direct2D1.SolidColorBrush dxBullishBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBearishBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBullishZoneBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxBearishZoneBrush;
        private SharpDX.DirectWrite.TextFormat dxTextFormat;
        private bool dxResourcesCreated;
        #endregion

        #region State Management
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                 = @"Detects Break of Structure (BOS) and Change of Character (CHoCH) for Smart Money Concepts trading.";
                Name                        = "SmartMoneyStructure";
                Calculate                   = Calculate.OnBarClose;
                IsOverlay                   = true;
                DisplayInDataBox            = false;
                DrawOnPricePanel            = true;
                PaintPriceMarkers           = false;
                IsSuspendedWhileInactive    = true;

                SwingStrength               = 3;
                ConfirmationBars            = 1;

                BullishColor                = Brushes.LimeGreen;
                BearishColor                = Brushes.Crimson;

                LineWidth                   = 2;
                LineStyle                   = DashStyleHelper.Dash;

                ShowLabels                  = true;
                LabelFontSize              = 11;

                ShowZones                   = true;
                ZoneOpacity                 = 15;
                ZoneExtendBars              = 8;
            }
            else if (State == State.Configure)
            {
            }
            else if (State == State.DataLoaded)
            {
                swingHighs      = new List<SwingPoint>();
                swingLows       = new List<SwingPoint>();
                structureEvents = new List<StructureEvent>();
                trendEstablished = false;
            }
            else if (State == State.Terminated)
            {
                DisposeSharpDXResources();
            }
        }
        #endregion

        #region Bar Update Logic
        protected override void OnBarUpdate()
        {
            if (CurrentBar < SwingStrength * 2 + 1)
                return;

            DetectSwingPoints();
            DetectStructureBreaks();
        }
        #endregion

        #region Swing Point Detection
        private void DetectSwingPoints()
        {
            int lookback = SwingStrength;
            int candidateBar = CurrentBar - lookback;

            if (candidateBar < lookback)
                return;

            double candidateHigh = High.GetValueAt(candidateBar);
            bool isSwingHigh = true;

            for (int i = 1; i <= lookback; i++)
            {
                if (High.GetValueAt(candidateBar - i) >= candidateHigh ||
                    High.GetValueAt(candidateBar + i) >= candidateHigh)
                {
                    isSwingHigh = false;
                    break;
                }
            }

            if (isSwingHigh)
            {
                if (swingHighs.Count == 0 || swingHighs[swingHighs.Count - 1].BarIndex != candidateBar)
                {
                    swingHighs.Add(new SwingPoint
                    {
                        BarIndex = candidateBar,
                        Price = candidateHigh,
                        IsHigh = true
                    });
                }
            }

            double candidateLow = Low.GetValueAt(candidateBar);
            bool isSwingLow = true;

            for (int i = 1; i <= lookback; i++)
            {
                if (Low.GetValueAt(candidateBar - i) <= candidateLow ||
                    Low.GetValueAt(candidateBar + i) <= candidateLow)
                {
                    isSwingLow = false;
                    break;
                }
            }

            if (isSwingLow)
            {
                if (swingLows.Count == 0 || swingLows[swingLows.Count - 1].BarIndex != candidateBar)
                {
                    swingLows.Add(new SwingPoint
                    {
                        BarIndex = candidateBar,
                        Price = candidateLow,
                        IsHigh = false
                    });
                }
            }
        }
        #endregion

        #region Structure Break Detection
        private void DetectStructureBreaks()
        {
            if (swingHighs.Count < 2 && swingLows.Count < 2)
                return;

            if (!trendEstablished)
            {
                if (swingHighs.Count >= 2 && swingLows.Count >= 2)
                {
                    SwingPoint lastSH = swingHighs[swingHighs.Count - 1];
                    SwingPoint prevSH = swingHighs[swingHighs.Count - 2];
                    SwingPoint lastSL = swingLows[swingLows.Count - 1];
                    SwingPoint prevSL = swingLows[swingLows.Count - 2];

                    if (lastSH.Price > prevSH.Price && lastSL.Price > prevSL.Price)
                    {
                        currentTrend = StructureDirection.Bullish;
                        trendEstablished = true;
                    }
                    else if (lastSH.Price < prevSH.Price && lastSL.Price < prevSL.Price)
                    {
                        currentTrend = StructureDirection.Bearish;
                        trendEstablished = true;
                    }
                    else
                    {
                        currentTrend = StructureDirection.Bullish;
                        trendEstablished = true;
                    }
                }
                else
                    return;
            }

            if (swingHighs.Count >= 1)
            {
                SwingPoint lastHigh = swingHighs[swingHighs.Count - 1];

                if (HasConfirmedBreakAbove(lastHigh.Price, lastHigh.BarIndex))
                {
                    if (!EventExistsAt(lastHigh.Price, StructureDirection.Bullish))
                    {
                        int breakBar = FindBreakBarAbove(lastHigh.Price, lastHigh.BarIndex);

                        if (breakBar > 0)
                        {
                            StructureType type;

                            if (currentTrend == StructureDirection.Bullish)
                            {
                                type = StructureType.BOS;
                            }
                            else
                            {
                                type = StructureType.CHoCH;
                                currentTrend = StructureDirection.Bullish;
                            }

                            structureEvents.Add(new StructureEvent
                            {
                                Type = type,
                                Direction = StructureDirection.Bullish,
                                StartBar = lastHigh.BarIndex,
                                Price = lastHigh.Price,
                                EndBar = breakBar
                            });
                        }
                    }
                }
            }

            if (swingLows.Count >= 1)
            {
                SwingPoint lastLow = swingLows[swingLows.Count - 1];

                if (HasConfirmedBreakBelow(lastLow.Price, lastLow.BarIndex))
                {
                    if (!EventExistsAt(lastLow.Price, StructureDirection.Bearish))
                    {
                        int breakBar = FindBreakBarBelow(lastLow.Price, lastLow.BarIndex);

                        if (breakBar > 0)
                        {
                            StructureType type;

                            if (currentTrend == StructureDirection.Bearish)
                            {
                                type = StructureType.BOS;
                            }
                            else
                            {
                                type = StructureType.CHoCH;
                                currentTrend = StructureDirection.Bearish;
                            }

                            structureEvents.Add(new StructureEvent
                            {
                                Type = type,
                                Direction = StructureDirection.Bearish,
                                StartBar = lastLow.BarIndex,
                                Price = lastLow.Price,
                                EndBar = breakBar
                            });
                        }
                    }
                }
            }
        }
        #endregion

        #region Confirmation Helpers
        private bool HasConfirmedBreakAbove(double price, int swingBar)
        {
            int consecutive = 0;

            for (int i = CurrentBar; i > swingBar; i--)
            {
                if (Close.GetValueAt(i) > price)
                {
                    consecutive++;
                    if (consecutive >= ConfirmationBars)
                        return true;
                }
                else
                {
                    consecutive = 0;
                }
            }
            return false;
        }

        private bool HasConfirmedBreakBelow(double price, int swingBar)
        {
            int consecutive = 0;

            for (int i = CurrentBar; i > swingBar; i--)
            {
                if (Close.GetValueAt(i) < price)
                {
                    consecutive++;
                    if (consecutive >= ConfirmationBars)
                        return true;
                }
                else
                {
                    consecutive = 0;
                }
            }
            return false;
        }

        private int FindBreakBarAbove(double price, int swingBar)
        {
            for (int i = swingBar + 1; i <= CurrentBar; i++)
            {
                if (Close.GetValueAt(i) > price)
                    return i;
            }
            return -1;
        }

        private int FindBreakBarBelow(double price, int swingBar)
        {
            for (int i = swingBar + 1; i <= CurrentBar; i++)
            {
                if (Close.GetValueAt(i) < price)
                    return i;
            }
            return -1;
        }

        private bool EventExistsAt(double price, StructureDirection direction)
        {
            for (int i = structureEvents.Count - 1; i >= Math.Max(0, structureEvents.Count - 20); i--)
            {
                var e = structureEvents[i];
                if (Math.Abs(e.Price - price) < TickSize && e.Direction == direction)
                    return true;
            }
            return false;
        }
        #endregion

        #region SharpDX Rendering

        private SharpDX.Color4 WpfBrushToColor4(System.Windows.Media.Brush wpfBrush, float alpha)
        {
            System.Windows.Media.Color c = ((System.Windows.Media.SolidColorBrush)wpfBrush).Color;
            return new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, alpha);
        }

        private void CreateSharpDXResources(SharpDX.Direct2D1.RenderTarget renderTarget)
        {
            if (dxResourcesCreated)
                return;

            dxBullishBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, WpfBrushToColor4(BullishColor, 1f));
            dxBearishBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, WpfBrushToColor4(BearishColor, 1f));

            float zoneAlpha = ZoneOpacity / 100f;
            dxBullishZoneBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, WpfBrushToColor4(BullishColor, zoneAlpha));
            dxBearishZoneBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, WpfBrushToColor4(BearishColor, zoneAlpha));

            int clampedSize = Math.Min(LabelFontSize, 12);

            dxTextFormat = new SharpDX.DirectWrite.TextFormat(
                NinjaTrader.Core.Globals.DirectWriteFactory,
                "Arial",
                SharpDX.DirectWrite.FontWeight.Bold,
                SharpDX.DirectWrite.FontStyle.Normal,
                SharpDX.DirectWrite.FontStretch.Normal,
                clampedSize);

            dxTextFormat.TextAlignment = SharpDX.DirectWrite.TextAlignment.Center;
            dxTextFormat.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;

            dxResourcesCreated = true;
        }

        private void DisposeSharpDXResources()
        {
            if (dxBullishBrush != null)     { dxBullishBrush.Dispose();     dxBullishBrush = null; }
            if (dxBearishBrush != null)     { dxBearishBrush.Dispose();     dxBearishBrush = null; }
            if (dxBullishZoneBrush != null)  { dxBullishZoneBrush.Dispose(); dxBullishZoneBrush = null; }
            if (dxBearishZoneBrush != null)  { dxBearishZoneBrush.Dispose(); dxBearishZoneBrush = null; }
            if (dxTextFormat != null)        { dxTextFormat.Dispose();        dxTextFormat = null; }
            dxResourcesCreated = false;
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (structureEvents == null || structureEvents.Count == 0)
                return;

            SharpDX.Direct2D1.RenderTarget renderTarget = RenderTarget;
            if (renderTarget == null)
                return;

            if (!dxResourcesCreated)
                CreateSharpDXResources(renderTarget);

            int firstVisibleBar = ChartBars.FromIndex;
            int lastVisibleBar  = ChartBars.ToIndex;

            SharpDX.Direct2D1.StrokeStyle strokeStyle = null;
            if (LineStyle != DashStyleHelper.Solid)
            {
                SharpDX.Direct2D1.StrokeStyleProperties strokeProps = new SharpDX.Direct2D1.StrokeStyleProperties();
                switch (LineStyle)
                {
                    case DashStyleHelper.Dash:
                        strokeProps.DashStyle = SharpDX.Direct2D1.DashStyle.Dash;
                        break;
                    case DashStyleHelper.DashDot:
                        strokeProps.DashStyle = SharpDX.Direct2D1.DashStyle.DashDot;
                        break;
                    case DashStyleHelper.DashDotDot:
                        strokeProps.DashStyle = SharpDX.Direct2D1.DashStyle.DashDotDot;
                        break;
                    case DashStyleHelper.Dot:
                        strokeProps.DashStyle = SharpDX.Direct2D1.DashStyle.Dot;
                        break;
                    default:
                        strokeProps.DashStyle = SharpDX.Direct2D1.DashStyle.Solid;
                        break;
                }
                strokeStyle = new SharpDX.Direct2D1.StrokeStyle(renderTarget.Factory, strokeProps);
            }

            foreach (var evt in structureEvents)
            {
                if (evt.EndBar < firstVisibleBar || evt.StartBar > lastVisibleBar)
                    continue;

                float xStart = chartControl.GetXByBarIndex(ChartBars, Math.Max(evt.StartBar, firstVisibleBar));
                float xEnd   = chartControl.GetXByBarIndex(ChartBars, Math.Min(evt.EndBar, lastVisibleBar));
                float y      = chartScale.GetYByValue(evt.Price);

                SharpDX.Direct2D1.SolidColorBrush lineBrush = evt.Direction == StructureDirection.Bullish ? dxBullishBrush : dxBearishBrush;
                SharpDX.Direct2D1.SolidColorBrush zoneBrush = evt.Direction == StructureDirection.Bullish ? dxBullishZoneBrush : dxBearishZoneBrush;

                if (strokeStyle != null)
                    renderTarget.DrawLine(new SharpDX.Vector2(xStart, y), new SharpDX.Vector2(xEnd, y), lineBrush, LineWidth, strokeStyle);
                else
                    renderTarget.DrawLine(new SharpDX.Vector2(xStart, y), new SharpDX.Vector2(xEnd, y), lineBrush, LineWidth);

                if (ShowZones)
                {
                    float zoneHeight;
                    float zoneTop;

                    if (evt.Direction == StructureDirection.Bullish)
                    {
                        double zoneBottomPrice = evt.Price - (evt.Price * 0.0015);
                        float yBottom = chartScale.GetYByValue(zoneBottomPrice);
                        zoneTop = y;
                        zoneHeight = yBottom - y;
                    }
                    else
                    {
                        double zoneTopPrice = evt.Price + (evt.Price * 0.0015);
                        zoneTop = chartScale.GetYByValue(zoneTopPrice);
                        zoneHeight = y - zoneTop;
                    }

                    int zoneEndBar = Math.Min(evt.EndBar + ZoneExtendBars, lastVisibleBar);
                    float xZoneEnd = chartControl.GetXByBarIndex(ChartBars, zoneEndBar);

                    if (zoneHeight > 0)
                    {
                        SharpDX.RectangleF rect = new SharpDX.RectangleF(xStart, zoneTop, xZoneEnd - xStart, zoneHeight);
                        renderTarget.FillRectangle(rect, zoneBrush);
                    }
                }

                if (ShowLabels)
                {
                    string label = evt.Type == StructureType.BOS ? "BOS" : "CHoCH";
                    float labelX = (xStart + xEnd) / 2f;
                    float labelY = evt.Direction == StructureDirection.Bullish ? y - 16 : y + 4;

                    SharpDX.DirectWrite.TextLayout textLayout = new SharpDX.DirectWrite.TextLayout(
                        NinjaTrader.Core.Globals.DirectWriteFactory,
                        label,
                        dxTextFormat,
                        60,
                        16);

                    renderTarget.DrawTextLayout(new SharpDX.Vector2(labelX - 30, labelY), textLayout, lineBrush);
                    textLayout.Dispose();
                }
            }

            if (strokeStyle != null)
                strokeStyle.Dispose();
        }

        public override void OnRenderTargetChanged()
        {
            DisposeSharpDXResources();
        }
        #endregion

        #region User Properties
        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Swing Strength", Description = "Number of bars on each side to confirm a swing high/low.",
            Order = 1, GroupName = "1. Structure Detection")]
        public int SwingStrength { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Confirmation Bars", Description = "Number of consecutive candles that must close beyond the swing level to confirm the break.",
            Order = 2, GroupName = "1. Structure Detection")]
        public int ConfirmationBars { get; set; }

        [XmlIgnore]
        [Display(Name = "Bullish Color", Description = "Color for bullish BOS/CHoCH (long).",
            Order = 1, GroupName = "2. Appearance")]
        public System.Windows.Media.Brush BullishColor { get; set; }

        [Browsable(false)]
        public string BullishColorSerializable
        {
            get { return Serialize.BrushToString(BullishColor); }
            set { BullishColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Bearish Color", Description = "Color for bearish BOS/CHoCH (short).",
            Order = 2, GroupName = "2. Appearance")]
        public System.Windows.Media.Brush BearishColor { get; set; }

        [Browsable(false)]
        public string BearishColorSerializable
        {
            get { return Serialize.BrushToString(BearishColor); }
            set { BearishColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Line Width", Description = "Thickness of structure lines.",
            Order = 3, GroupName = "2. Appearance")]
        public int LineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Line Style", Description = "Dash style for structure lines.",
            Order = 4, GroupName = "2. Appearance")]
        public DashStyleHelper LineStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Labels", Description = "Display BOS/CHoCH text labels on the chart.",
            Order = 5, GroupName = "2. Appearance")]
        public bool ShowLabels { get; set; }

        [NinjaScriptProperty]
        [Range(8, 12)]
        [Display(Name = "Label Font Size", Description = "Font size for labels (max 12px).",
            Order = 6, GroupName = "2. Appearance")]
        public int LabelFontSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Zones", Description = "Draw shaded background zones at structure break levels.",
            Order = 1, GroupName = "3. Zones")]
        public bool ShowZones { get; set; }

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "Zone Opacity %", Description = "Transparency of background zones (5 = very faint, 50 = visible).",
            Order = 2, GroupName = "3. Zones")]
        public int ZoneOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Zone Extend Bars", Description = "How many bars past the break the zone shading extends.",
            Order = 3, GroupName = "3. Zones")]
        public int ZoneExtendBars { get; set; }
        #endregion
    }
}