#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.Core.FloatingPoint;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SharpEngineAllInOne : Indicator
    {
        private readonly Dictionary<double, long> askDepth = new Dictionary<double, long>();
        private readonly Dictionary<double, long> bidDepth = new Dictionary<double, long>();

        private Swing swingHtf;
        private Swing swingConfirm;
        private Series<int> signalSeries;

        private SharpDX.Direct2D1.SolidColorBrush dxBullShade;
        private SharpDX.Direct2D1.SolidColorBrush dxBearShade;
        private SharpDX.Direct2D1.SolidColorBrush dxAskWall;
        private SharpDX.Direct2D1.SolidColorBrush dxBidWall;
        private SharpDX.Direct2D1.SolidColorBrush dxHudText;
        private SharpDX.Direct2D1.SolidColorBrush dxArrowUp;
        private SharpDX.Direct2D1.SolidColorBrush dxArrowDown;

        private TextFormat hudFormat;
        private TextFormat wallFormat;
        private StrokeStyle wallStrokeStyle;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Clean SharpDX Multi-Timeframe Order Flow & L2 Engine.";
                Name = "SharpEngineAllInOne";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = true;

                EnableL2Walls = true;
                MinLotThreshold = 35;
                WallDashStyle = DashStyleHelper.Dash;
                EnableHtfShading = true;
                EnableOrderFlowSignals = true;

                HtfBarsPeriodType = BarsPeriodType.Minute;
                HtfBarsValue = 240;
                ConfirmBarsPeriodType = BarsPeriodType.Tick;
                ConfirmBarsValue = 80;
                SwingStrength = 5;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(HtfBarsPeriodType, HtfBarsValue);
                AddDataSeries(ConfirmBarsPeriodType, ConfirmBarsValue);
            }
            else if (State == State.DataLoaded)
            {
                swingHtf = Swing(BarsArray[1], SwingStrength);
                swingConfirm = Swing(BarsArray[2], SwingStrength);
                signalSeries = new Series<int>(this, MaximumBarsLookBack.Infinite);
            }
            else if (State == State.Terminated)
            {
                DisposeDxResources();
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;

            if (CurrentBar < 2 || CurrentBars.Length < 3 || CurrentBars[1] < SwingStrength || CurrentBars[2] < SwingStrength)
            {
                if (signalSeries != null)
                    signalSeries[0] = 0;
                return;
            }

            double sLowHtf = swingHtf.SwingLow[0];
            double sHighHtf = swingHtf.SwingHigh[0];
            double sLowConf = swingConfirm.SwingLow[0];
            double sHighConf = swingConfirm.SwingHigh[0];

            bool hasLowHtf = !double.IsNaN(sLowHtf) && sLowHtf.ApproxCompare(0) > 0;
            bool hasLowConf = !double.IsNaN(sLowConf) && sLowConf.ApproxCompare(0) > 0;
            bool hasHighHtf = !double.IsNaN(sHighHtf) && sHighHtf.ApproxCompare(0) > 0;
            bool hasHighConf = !double.IsNaN(sHighConf) && sHighConf.ApproxCompare(0) > 0;

            bool htfBull = hasLowHtf && hasLowConf && Close[0] > sLowHtf && Close[0] > sLowConf;
            bool htfBear = hasHighHtf && hasHighConf && Close[0] < sHighHtf && Close[0] < sHighConf;

            if (!EnableOrderFlowSignals)
            {
                signalSeries[0] = 0;
                return;
            }

            bool prevDown = Close[1] < Open[1];
            bool prevUp = Close[1] > Open[1];
            bool currUp = Close[0] > Open[0];
            bool currDown = Close[0] < Open[0];

            int signal = 0;
            if (htfBull && prevDown && currUp)
                signal = 1;
            else if (htfBear && prevUp && currDown)
                signal = -1;

            signalSeries[0] = signal;
        }

        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (e == null)
                return;

            bool isBid = e.MarketDataType == MarketDataType.Bid;
            bool isAsk = e.MarketDataType == MarketDataType.Ask;
            if (!isBid && !isAsk)
                return;

            double price = Instrument.MasterInstrument.RoundToTickSize(e.Price);
            long volume = Math.Max(0L, e.Volume);

            if (isAsk)
            {
                lock (askDepth)
                {
                    if (e.Operation == Operation.Remove || volume == 0)
                    {
                        if (askDepth.ContainsKey(price))
                            askDepth.Remove(price);
                    }
                    else
                    {
                        askDepth[price] = volume;
                    }
                }
            }
            else
            {
                lock (bidDepth)
                {
                    if (e.Operation == Operation.Remove || volume == 0)
                    {
                        if (bidDepth.ContainsKey(price))
                            bidDepth.Remove(price);
                    }
                    else
                    {
                        bidDepth[price] = volume;
                    }
                }
            }
        }

        public override void OnRenderTargetChanged()
        {
            DisposeDxResources();

            if (RenderTarget == null)
                return;

            try
            {
                dxBullShade = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(0.13f, 0.69f, 0.30f, 0.04f));
                dxBearShade = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(0.86f, 0.08f, 0.24f, 0.04f));
                dxAskWall = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(0.86f, 0.08f, 0.24f, 0.92f));
                dxBidWall = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(0.18f, 0.55f, 0.34f, 0.92f));
                dxHudText = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(0.92f, 0.92f, 0.92f, 0.95f));
                dxArrowUp = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(0.24f, 0.74f, 0.36f, 0.95f));
                dxArrowDown = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(0.88f, 0.20f, 0.20f, 0.95f));

                hudFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI", FontWeight.SemiBold, FontStyle.Normal, 12f);
                wallFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI", FontWeight.Bold, FontStyle.Normal, 10f);

                DashStyle dashStyle = DashStyle.Solid;
                switch (WallDashStyle)
                {
                    case DashStyleHelper.Dash: dashStyle = DashStyle.Dash; break;
                    case DashStyleHelper.Dot: dashStyle = DashStyle.Dot; break;
                    case DashStyleHelper.DashDot: dashStyle = DashStyle.DashDot; break;
                    case DashStyleHelper.DashDotDot: dashStyle = DashStyle.DashDotDot; break;
                }

                wallStrokeStyle = new StrokeStyle(Core.Globals.D2DFactory, new StrokeStyleProperties
                {
                    DashStyle = dashStyle,
                    StartCap = CapStyle.Flat,
                    EndCap = CapStyle.Flat,
                    LineJoin = LineJoin.Miter
                });
            }
            catch
            {
                DisposeDxResources();
            }
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (chartControl == null || chartScale == null || ChartBars == null || RenderTarget == null || CurrentBar < 2)
                return;

            int firstBar = ChartBars.FromIndex;
            int lastBar = ChartBars.ToIndex;
            if (firstBar < 0 || lastBar < firstBar)
                return;

            if (EnableHtfShading && swingHtf != null && swingConfirm != null)
            {
                double sLowHtf = swingHtf.SwingLow[0];
                double sHighHtf = swingHtf.SwingHigh[0];
                double sLowConf = swingConfirm.SwingLow[0];
                double sHighConf = swingConfirm.SwingHigh[0];

                bool hasLowHtf = !double.IsNaN(sLowHtf) && sLowHtf.ApproxCompare(0) > 0;
                bool hasLowConf = !double.IsNaN(sLowConf) && sLowConf.ApproxCompare(0) > 0;
                bool hasHighHtf = !double.IsNaN(sHighHtf) && sHighHtf.ApproxCompare(0) > 0;
                bool hasHighConf = !double.IsNaN(sHighConf) && sHighConf.ApproxCompare(0) > 0;

                bool htfBull = hasLowHtf && hasLowConf && Close[0] > sLowHtf && Close[0] > sLowConf;
                bool htfBear = hasHighHtf && hasHighConf && Close[0] < sHighHtf && Close[0] < sHighConf;

                if (htfBull && dxBullShade != null)
                {
                    RenderTarget.FillRectangle(
                        new RectangleF(ChartPanel.X, ChartPanel.Y, ChartPanel.W, ChartPanel.H),
                        dxBullShade);
                }
                else if (htfBear && dxBearShade != null)
                {
                    RenderTarget.FillRectangle(
                        new RectangleF(ChartPanel.X, ChartPanel.Y, ChartPanel.W, ChartPanel.H),
                        dxBearShade);
                }
            }

            if (EnableL2Walls && dxAskWall != null && dxBidWall != null)
            {
                lock (askDepth)
                {
                    foreach (KeyValuePair<double, long> level in askDepth)
                    {
                        if (level.Value < MinLotThreshold)
                            continue;

                        float y = chartScale.GetYByValue(level.Key);
                        RenderTarget.DrawLine(
                            new Vector2(ChartPanel.X, y),
                            new Vector2(ChartPanel.X + ChartPanel.W, y),
                            dxAskWall,
                            1.5f,
                            wallStrokeStyle);

                        if (wallFormat != null)
                        {
                            using (var layout = new TextLayout(Core.Globals.DirectWriteFactory, "ASK x" + level.Value, wallFormat, 120f, 14f))
                            {
                                RenderTarget.DrawTextLayout(new Vector2(ChartPanel.X + 6f, y - 14f), layout, dxAskWall);
                            }
                        }
                    }
                }

                lock (bidDepth)
                {
                    foreach (KeyValuePair<double, long> level in bidDepth)
                    {
                        if (level.Value < MinLotThreshold)
                            continue;

                        float y = chartScale.GetYByValue(level.Key);
                        RenderTarget.DrawLine(
                            new Vector2(ChartPanel.X, y),
                            new Vector2(ChartPanel.X + ChartPanel.W, y),
                            dxBidWall,
                            1.5f,
                            wallStrokeStyle);

                        if (wallFormat != null)
                        {
                            using (var layout = new TextLayout(Core.Globals.DirectWriteFactory, "BID x" + level.Value, wallFormat, 120f, 14f))
                            {
                                RenderTarget.DrawTextLayout(new Vector2(ChartPanel.X + 6f, y), layout, dxBidWall);
                            }
                        }
                    }
                }
            }

            if (hudFormat != null && dxHudText != null)
            {
                string hud = "ENGINE: ACTIVE | L2 WALL THRESHOLD: " + MinLotThreshold + " CONTRACTS";
                using (var layout = new TextLayout(Core.Globals.DirectWriteFactory, hud, hudFormat, ChartPanel.W, 22f))
                {
                    RenderTarget.DrawTextLayout(new Vector2(ChartPanel.X + 8f, ChartPanel.Y + ChartPanel.H - 24f), layout, dxHudText);
                }
            }

            if (EnableOrderFlowSignals && signalSeries != null)
            {
                for (int bar = firstBar; bar <= lastBar; bar++)
                {
                    int signal = signalSeries.GetValueAt(bar);
                    if (signal == 0)
                        continue;

                    float x = chartControl.GetXByBarIndex(ChartBars, bar);
                    float y = signal > 0
                        ? chartScale.GetYByValue(Low.GetValueAt(bar) - (2 * TickSize))
                        : chartScale.GetYByValue(High.GetValueAt(bar) + (2 * TickSize));

                    using (var geo = new PathGeometry(Core.Globals.D2DFactory))
                    {
                        using (var sink = geo.Open())
                        {
                            if (signal > 0)
                            {
                                sink.BeginFigure(new Vector2(x, y), FigureBegin.Filled);
                                sink.AddLine(new Vector2(x - 6f, y + 10f));
                                sink.AddLine(new Vector2(x + 6f, y + 10f));
                            }
                            else
                            {
                                sink.BeginFigure(new Vector2(x, y), FigureBegin.Filled);
                                sink.AddLine(new Vector2(x - 6f, y - 10f));
                                sink.AddLine(new Vector2(x + 6f, y - 10f));
                            }

                            sink.EndFigure(FigureEnd.Closed);
                            sink.Close();
                        }

                        RenderTarget.FillGeometry(geo, signal > 0 ? dxArrowUp : dxArrowDown);
                    }
                }
            }
        }

        private void DisposeDxResources()
        {
            if (wallStrokeStyle != null)
            {
                wallStrokeStyle.Dispose();
                wallStrokeStyle = null;
            }

            if (hudFormat != null)
            {
                hudFormat.Dispose();
                hudFormat = null;
            }

            if (wallFormat != null)
            {
                wallFormat.Dispose();
                wallFormat = null;
            }

            if (dxBullShade != null)
            {
                dxBullShade.Dispose();
                dxBullShade = null;
            }

            if (dxBearShade != null)
            {
                dxBearShade.Dispose();
                dxBearShade = null;
            }

            if (dxAskWall != null)
            {
                dxAskWall.Dispose();
                dxAskWall = null;
            }

            if (dxBidWall != null)
            {
                dxBidWall.Dispose();
                dxBidWall = null;
            }

            if (dxHudText != null)
            {
                dxHudText.Dispose();
                dxHudText = null;
            }

            if (dxArrowUp != null)
            {
                dxArrowUp.Dispose();
                dxArrowUp = null;
            }

            if (dxArrowDown != null)
            {
                dxArrowDown.Dispose();
                dxArrowDown = null;
            }
        }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Enable L2 Walls", Order = 1, GroupName = "1. Main Settings")]
        public bool EnableL2Walls { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Min Lot Threshold", Order = 2, GroupName = "1. Main Settings")]
        public int MinLotThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Wall Dash Style", Order = 3, GroupName = "1. Main Settings")]
        public DashStyleHelper WallDashStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable HTF Shading", Order = 4, GroupName = "1. Main Settings")]
        public bool EnableHtfShading { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Order Flow Signals", Order = 5, GroupName = "1. Main Settings")]
        public bool EnableOrderFlowSignals { get; set; }

        // Uses only NinjaTrader's built-in BarsPeriodType enum values.
        // Selecting Renko here uses native NT8 Renko; custom add-on bar types (e.g. NinjaRenko/UniRenko) are intentionally not supported.
        [NinjaScriptProperty]
        [Display(Name = "HTF Bars Period Type", Order = 6, GroupName = "3. Data Series Settings")]
        public BarsPeriodType HtfBarsPeriodType { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "HTF Bars Value", Order = 7, GroupName = "3. Data Series Settings")]
        public int HtfBarsValue { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Confirm Bars Period Type", Order = 8, GroupName = "3. Data Series Settings")]
        public BarsPeriodType ConfirmBarsPeriodType { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Confirm Bars Value", Order = 9, GroupName = "3. Data Series Settings")]
        public int ConfirmBarsValue { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Swing Strength", Order = 10, GroupName = "3. Data Series Settings")]
        public int SwingStrength { get; set; }
        #endregion
    }
}
