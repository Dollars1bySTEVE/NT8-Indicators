#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
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
        private const float LabelVerticalPadding = 2f;
        private const float LabelHorizontalPadding = 4f;
        private const float LabelLayoutWidth = 220f;
        private const float LabelLayoutHeightPadding = 4f;

        private readonly Dictionary<double, long> askDepth = new Dictionary<double, long>();
        private readonly Dictionary<double, long> bidDepth = new Dictionary<double, long>();

        private Swing swingHtf;
        private Swing swingConfirm;
        private Series<int> signalSeries;
        private Series<int> biasSeries;
        private Series<double> sessionVwapSeries;
        private Series<double> weeklyVwapSeries;

        private DateTime currentDayDate = DateTime.MinValue;
        private DateTime currentWeekStart = DateTime.MinValue;

        private double currentDayOpen;
        private double currentDayHigh;
        private double currentDayLow;
        private double priorDayOpen;
        private double priorDayHigh;
        private double priorDayLow;

        private double currentWeekOpen;
        private double currentWeekHigh;
        private double currentWeekLow;
        private double priorWeekOpen;
        private double priorWeekHigh;
        private double priorWeekLow;

        private double currentSessionOpen;
        private double currentSessionHigh;
        private double currentSessionLow;
        private double priorSessionOpen;
        private double priorSessionHigh;
        private double priorSessionLow;

        private double sessionCumPv;
        private double sessionCumVol;
        private double weeklyCumPv;
        private double weeklyCumVol;

        private readonly SortedDictionary<double, double> sessionProfile = new SortedDictionary<double, double>();
        private readonly SortedDictionary<double, double> weeklyProfile = new SortedDictionary<double, double>();

        private double sessionPoc = double.NaN;
        private double sessionVah = double.NaN;
        private double sessionVal = double.NaN;
        private double weeklyPoc = double.NaN;
        private double weeklyVah = double.NaN;
        private double weeklyVal = double.NaN;

        private SharpDX.Direct2D1.SolidColorBrush dxBullShade;
        private SharpDX.Direct2D1.SolidColorBrush dxBearShade;
        private SharpDX.Direct2D1.SolidColorBrush dxAskWall;
        private SharpDX.Direct2D1.SolidColorBrush dxBidWall;
        private SharpDX.Direct2D1.SolidColorBrush dxHudText;
        private SharpDX.Direct2D1.SolidColorBrush dxArrowUp;
        private SharpDX.Direct2D1.SolidColorBrush dxArrowDown;

        private SharpDX.Direct2D1.SolidColorBrush dxPrevDayOpen;
        private SharpDX.Direct2D1.SolidColorBrush dxPrevDayHigh;
        private SharpDX.Direct2D1.SolidColorBrush dxPrevDayLow;
        private SharpDX.Direct2D1.SolidColorBrush dxPrevWeekOpen;
        private SharpDX.Direct2D1.SolidColorBrush dxPrevWeekHigh;
        private SharpDX.Direct2D1.SolidColorBrush dxPrevWeekLow;
        private SharpDX.Direct2D1.SolidColorBrush dxPrevSessionOpen;
        private SharpDX.Direct2D1.SolidColorBrush dxPrevSessionHigh;
        private SharpDX.Direct2D1.SolidColorBrush dxPrevSessionLow;
        private SharpDX.Direct2D1.SolidColorBrush dxCurrentSessionOpen;
        private SharpDX.Direct2D1.SolidColorBrush dxCurrentSessionHigh;
        private SharpDX.Direct2D1.SolidColorBrush dxCurrentSessionLow;

        private SharpDX.Direct2D1.SolidColorBrush dxSessionPoc;
        private SharpDX.Direct2D1.SolidColorBrush dxSessionVah;
        private SharpDX.Direct2D1.SolidColorBrush dxSessionVal;
        private SharpDX.Direct2D1.SolidColorBrush dxSessionVwap;
        private SharpDX.Direct2D1.SolidColorBrush dxWeeklyPoc;
        private SharpDX.Direct2D1.SolidColorBrush dxWeeklyVah;
        private SharpDX.Direct2D1.SolidColorBrush dxWeeklyVal;
        private SharpDX.Direct2D1.SolidColorBrush dxWeeklyVwap;

        private TextFormat hudFormat;
        private TextFormat wallFormat;
        private TextFormat levelLabelFormat;
        private StrokeStyle wallStrokeStyle;
        private StrokeStyle referenceStrokeStyle;
        private StrokeStyle profileStrokeStyle;

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
                ShowBiasDebug = false;

                ShowPreviousDayLevels = true;
                ShowPreviousDayOpenLevel = true;
                ShowPreviousDayHighLevel = true;
                ShowPreviousDayLowLevel = true;
                PreviousDayOpenColor = System.Windows.Media.Brushes.Gold;
                PreviousDayHighColor = System.Windows.Media.Brushes.LimeGreen;
                PreviousDayLowColor = System.Windows.Media.Brushes.OrangeRed;

                ShowPreviousWeekLevels = true;
                ShowPreviousWeekOpenLevel = true;
                ShowPreviousWeekHighLevel = true;
                ShowPreviousWeekLowLevel = true;
                PreviousWeekOpenColor = System.Windows.Media.Brushes.Goldenrod;
                PreviousWeekHighColor = System.Windows.Media.Brushes.DeepSkyBlue;
                PreviousWeekLowColor = System.Windows.Media.Brushes.MediumVioletRed;

                ShowPreviousSessionLevels = true;
                ShowPreviousSessionOpenLevel = true;
                ShowPreviousSessionHighLevel = true;
                ShowPreviousSessionLowLevel = true;
                PreviousSessionOpenColor = System.Windows.Media.Brushes.Khaki;
                PreviousSessionHighColor = System.Windows.Media.Brushes.LightGreen;
                PreviousSessionLowColor = System.Windows.Media.Brushes.LightCoral;

                ShowCurrentSessionLevels = true;
                ShowCurrentSessionOpenLevel = true;
                ShowCurrentSessionHighLevel = true;
                ShowCurrentSessionLowLevel = true;
                CurrentSessionOpenColor = System.Windows.Media.Brushes.WhiteSmoke;
                CurrentSessionHighColor = System.Windows.Media.Brushes.Cyan;
                CurrentSessionLowColor = System.Windows.Media.Brushes.Magenta;

                ReferenceLineWidth = 1;
                ReferenceLineStyle = DashStyleHelper.Dash;
                ShowLevelLabels = true;
                LevelLabelFontSize = 10;

                ShowSessionProfileLevels = true;
                ShowWeeklyProfileLevels = true;
                ShowSessionPoc = true;
                ShowSessionVah = true;
                ShowSessionVal = true;
                ShowWeeklyPoc = true;
                ShowWeeklyVah = true;
                ShowWeeklyVal = true;
                ShowSessionVwap = true;
                ShowWeeklyVwap = true;
                ValueAreaPercent = 70;
                ProfileLineWidth = 2;
                ProfileLineStyle = DashStyleHelper.Solid;

                SessionPocColor = System.Windows.Media.Brushes.Gold;
                SessionVahColor = System.Windows.Media.Brushes.LimeGreen;
                SessionValColor = System.Windows.Media.Brushes.OrangeRed;
                SessionVwapColor = System.Windows.Media.Brushes.DodgerBlue;
                WeeklyPocColor = System.Windows.Media.Brushes.DarkGoldenrod;
                WeeklyVahColor = System.Windows.Media.Brushes.GreenYellow;
                WeeklyValColor = System.Windows.Media.Brushes.IndianRed;
                WeeklyVwapColor = System.Windows.Media.Brushes.DeepSkyBlue;

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
                biasSeries = new Series<int>(this, MaximumBarsLookBack.Infinite);
                sessionVwapSeries = new Series<double>(this, MaximumBarsLookBack.Infinite);
                weeklyVwapSeries = new Series<double>(this, MaximumBarsLookBack.Infinite);
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

            UpdateReferenceLevelsAndProfiles();

            if (CurrentBar < 2 || CurrentBars.Length < 3 || CurrentBars[1] < SwingStrength || CurrentBars[2] < SwingStrength)
            {
                if (signalSeries != null)
                    signalSeries[0] = 0;
                if (biasSeries != null)
                    biasSeries[0] = 0;
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

            biasSeries[0] = htfBull ? 1 : (htfBear ? -1 : 0);

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

        private void UpdateReferenceLevelsAndProfiles()
        {
            DateTime barTime = Time[0];
            DateTime barDate = barTime.Date;
            DateTime weekStart = GetWeekStart(barDate);
            bool isFirstBar = CurrentBar == 0;
            bool isNewSession = isFirstBar || Bars.IsFirstBarOfSession;
            bool isNewDay = isFirstBar || barDate != currentDayDate;
            bool isNewWeek = isFirstBar || weekStart != currentWeekStart;

            if (isNewWeek)
            {
                if (!isFirstBar)
                {
                    priorWeekOpen = currentWeekOpen;
                    priorWeekHigh = currentWeekHigh;
                    priorWeekLow = currentWeekLow;
                }

                currentWeekStart = weekStart;
                currentWeekOpen = Open[0];
                currentWeekHigh = High[0];
                currentWeekLow = Low[0];
                weeklyCumPv = 0;
                weeklyCumVol = 0;
                weeklyProfile.Clear();
                weeklyPoc = double.NaN;
                weeklyVah = double.NaN;
                weeklyVal = double.NaN;
            }
            else
            {
                currentWeekHigh = Math.Max(currentWeekHigh, High[0]);
                currentWeekLow = Math.Min(currentWeekLow, Low[0]);
            }

            if (isNewDay)
            {
                if (!isFirstBar)
                {
                    priorDayOpen = currentDayOpen;
                    priorDayHigh = currentDayHigh;
                    priorDayLow = currentDayLow;
                }

                currentDayDate = barDate;
                currentDayOpen = Open[0];
                currentDayHigh = High[0];
                currentDayLow = Low[0];
            }
            else
            {
                currentDayHigh = Math.Max(currentDayHigh, High[0]);
                currentDayLow = Math.Min(currentDayLow, Low[0]);
            }

            if (isNewSession)
            {
                if (!isFirstBar)
                {
                    priorSessionOpen = currentSessionOpen;
                    priorSessionHigh = currentSessionHigh;
                    priorSessionLow = currentSessionLow;
                }

                currentSessionOpen = Open[0];
                currentSessionHigh = High[0];
                currentSessionLow = Low[0];
                sessionCumPv = 0;
                sessionCumVol = 0;
                sessionProfile.Clear();
                sessionPoc = double.NaN;
                sessionVah = double.NaN;
                sessionVal = double.NaN;
            }
            else
            {
                currentSessionHigh = Math.Max(currentSessionHigh, High[0]);
                currentSessionLow = Math.Min(currentSessionLow, Low[0]);
            }

            double barVolume = Math.Max(0d, Volume[0]);
            double typicalPrice = (High[0] + Low[0] + Close[0]) / 3.0;
            double bucketPrice = Instrument.MasterInstrument.RoundToTickSize(typicalPrice);

            sessionCumPv += typicalPrice * barVolume;
            sessionCumVol += barVolume;
            weeklyCumPv += typicalPrice * barVolume;
            weeklyCumVol += barVolume;

            sessionVwapSeries[0] = sessionCumVol > 0 ? sessionCumPv / sessionCumVol : double.NaN;
            weeklyVwapSeries[0] = weeklyCumVol > 0 ? weeklyCumPv / weeklyCumVol : double.NaN;

            AddProfileVolume(sessionProfile, bucketPrice, barVolume);
            AddProfileVolume(weeklyProfile, bucketPrice, barVolume);

            ComputeValueArea(sessionProfile, out sessionPoc, out sessionVah, out sessionVal);
            ComputeValueArea(weeklyProfile, out weeklyPoc, out weeklyVah, out weeklyVal);
        }

        private void AddProfileVolume(SortedDictionary<double, double> profile, double price, double volume)
        {
            if (volume <= 0)
                return;

            if (profile.ContainsKey(price))
                profile[price] += volume;
            else
                profile[price] = volume;
        }

        private void ComputeValueArea(SortedDictionary<double, double> profile, out double poc, out double vah, out double val)
        {
            poc = double.NaN;
            vah = double.NaN;
            val = double.NaN;

            if (profile == null || profile.Count == 0)
                return;

            List<double> prices = new List<double>(profile.Keys);
            List<double> volumes = new List<double>(profile.Values);
            double totalVolume = 0;
            int pocIndex = 0;
            double maxVolume = 0;

            for (int i = 0; i < volumes.Count; i++)
            {
                totalVolume += volumes[i];
                if (volumes[i] > maxVolume)
                {
                    maxVolume = volumes[i];
                    pocIndex = i;
                }
            }

            if (totalVolume <= 0)
                return;

            double targetVolume = totalVolume * (ValueAreaPercent / 100.0);
            double cumVolume = volumes[pocIndex];
            int lowIndex = pocIndex;
            int highIndex = pocIndex;

            while (cumVolume < targetVolume && (lowIndex > 0 || highIndex < prices.Count - 1))
            {
                double downVolume = lowIndex > 0 ? volumes[lowIndex - 1] : -1d;
                double upVolume = highIndex < prices.Count - 1 ? volumes[highIndex + 1] : -1d;

                if (upVolume >= downVolume && highIndex < prices.Count - 1)
                {
                    highIndex++;
                    cumVolume += volumes[highIndex];
                }
                else if (lowIndex > 0)
                {
                    lowIndex--;
                    cumVolume += volumes[lowIndex];
                }
                else if (highIndex < prices.Count - 1)
                {
                    highIndex++;
                    cumVolume += volumes[highIndex];
                }
                else
                {
                    break;
                }
            }

            poc = prices[pocIndex];
            val = prices[lowIndex];
            vah = prices[highIndex];
        }

        private DateTime GetWeekStart(DateTime date)
        {
            int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
            return date.AddDays(-diff).Date;
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

                dxPrevDayOpen = CreateDxBrush(PreviousDayOpenColor);
                dxPrevDayHigh = CreateDxBrush(PreviousDayHighColor);
                dxPrevDayLow = CreateDxBrush(PreviousDayLowColor);
                dxPrevWeekOpen = CreateDxBrush(PreviousWeekOpenColor);
                dxPrevWeekHigh = CreateDxBrush(PreviousWeekHighColor);
                dxPrevWeekLow = CreateDxBrush(PreviousWeekLowColor);
                dxPrevSessionOpen = CreateDxBrush(PreviousSessionOpenColor);
                dxPrevSessionHigh = CreateDxBrush(PreviousSessionHighColor);
                dxPrevSessionLow = CreateDxBrush(PreviousSessionLowColor);
                dxCurrentSessionOpen = CreateDxBrush(CurrentSessionOpenColor);
                dxCurrentSessionHigh = CreateDxBrush(CurrentSessionHighColor);
                dxCurrentSessionLow = CreateDxBrush(CurrentSessionLowColor);
                dxSessionPoc = CreateDxBrush(SessionPocColor);
                dxSessionVah = CreateDxBrush(SessionVahColor);
                dxSessionVal = CreateDxBrush(SessionValColor);
                dxSessionVwap = CreateDxBrush(SessionVwapColor);
                dxWeeklyPoc = CreateDxBrush(WeeklyPocColor);
                dxWeeklyVah = CreateDxBrush(WeeklyVahColor);
                dxWeeklyVal = CreateDxBrush(WeeklyValColor);
                dxWeeklyVwap = CreateDxBrush(WeeklyVwapColor);

                hudFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI", FontWeight.SemiBold, FontStyle.Normal, 12f);
                wallFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI", FontWeight.Bold, FontStyle.Normal, 10f);
                levelLabelFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI", FontWeight.Normal, FontStyle.Normal, LevelLabelFontSize);

                wallStrokeStyle = CreateStrokeStyle(WallDashStyle);
                referenceStrokeStyle = CreateStrokeStyle(ReferenceLineStyle);
                profileStrokeStyle = CreateStrokeStyle(ProfileLineStyle);
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

            int renderLastBar = Math.Min(lastBar, CurrentBar);

            if (EnableHtfShading && biasSeries != null)
            {
                float halfWidth = (float)(chartControl.BarWidth * 0.5);
                for (int bar = firstBar; bar <= renderLastBar; bar++)
                {
                    int bias = biasSeries.GetValueAt(bar);
                    if (bias == 0)
                        continue;

                    SharpDX.Direct2D1.SolidColorBrush shadeBrush = bias > 0 ? dxBullShade : dxBearShade;
                    if (shadeBrush == null)
                        continue;

                    float cx = chartControl.GetXByBarIndex(ChartBars, bar);
                    RenderTarget.FillRectangle(new RectangleF(cx - halfWidth, ChartPanel.Y, chartControl.BarWidth, ChartPanel.H), shadeBrush);
                }
            }

            RenderReferenceLevels(chartControl, chartScale, renderLastBar);
            RenderProfileLevels(chartControl, chartScale, firstBar, renderLastBar);

            if (EnableL2Walls && dxAskWall != null && dxBidWall != null)
            {
                lock (askDepth)
                {
                    foreach (KeyValuePair<double, long> level in askDepth)
                    {
                        if (level.Value < MinLotThreshold)
                            continue;

                        float y = chartScale.GetYByValue(level.Key);
                        DrawDxLine(
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
                        DrawDxLine(
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

                if (ShowBiasDebug && biasSeries != null && swingHtf != null && swingConfirm != null)
                {
                    int curBias = renderLastBar >= 0 ? biasSeries.GetValueAt(renderLastBar) : 0;
                    double dbgLo = swingHtf.SwingLow[0];
                    double dbgHi = swingHtf.SwingHigh[0];
                    double dbgCLo = swingConfirm.SwingLow[0];
                    double dbgCHi = swingConfirm.SwingHigh[0];
                    string debugLine = string.Format(
                        "BIAS: BULL={0} BEAR={1} | HtfLo={2:F2} HtfHi={3:F2} ConfLo={4:F2} ConfHi={5:F2}",
                        curBias > 0 ? "Y" : "N", curBias < 0 ? "Y" : "N", dbgLo, dbgHi, dbgCLo, dbgCHi);
                    using (var layout = new TextLayout(Core.Globals.DirectWriteFactory, debugLine, hudFormat, ChartPanel.W, 22f))
                    {
                        RenderTarget.DrawTextLayout(new Vector2(ChartPanel.X + 8f, ChartPanel.Y + ChartPanel.H - 46f), layout, dxHudText);
                    }
                }
            }

            if (EnableOrderFlowSignals && signalSeries != null)
            {
                for (int bar = firstBar; bar <= renderLastBar; bar++)
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

        private void RenderReferenceLevels(ChartControl chartControl, ChartScale chartScale, int renderLastBar)
        {
            if (referenceStrokeStyle == null && ReferenceLineStyle != DashStyleHelper.Solid)
                return;

            if (ShowPreviousDayLevels)
            {
                if (ShowPreviousDayOpenLevel)
                    RenderLevel(chartControl, chartScale, priorDayOpen, "PD Open", dxPrevDayOpen, ReferenceLineWidth, referenceStrokeStyle);
                if (ShowPreviousDayHighLevel)
                    RenderLevel(chartControl, chartScale, priorDayHigh, "PD High", dxPrevDayHigh, ReferenceLineWidth, referenceStrokeStyle);
                if (ShowPreviousDayLowLevel)
                    RenderLevel(chartControl, chartScale, priorDayLow, "PD Low", dxPrevDayLow, ReferenceLineWidth, referenceStrokeStyle);
            }

            if (ShowPreviousWeekLevels)
            {
                if (ShowPreviousWeekOpenLevel)
                    RenderLevel(chartControl, chartScale, priorWeekOpen, "PW Open", dxPrevWeekOpen, ReferenceLineWidth, referenceStrokeStyle);
                if (ShowPreviousWeekHighLevel)
                    RenderLevel(chartControl, chartScale, priorWeekHigh, "PW High", dxPrevWeekHigh, ReferenceLineWidth, referenceStrokeStyle);
                if (ShowPreviousWeekLowLevel)
                    RenderLevel(chartControl, chartScale, priorWeekLow, "PW Low", dxPrevWeekLow, ReferenceLineWidth, referenceStrokeStyle);
            }

            if (ShowPreviousSessionLevels)
            {
                if (ShowPreviousSessionOpenLevel)
                    RenderLevel(chartControl, chartScale, priorSessionOpen, "PS Open", dxPrevSessionOpen, ReferenceLineWidth, referenceStrokeStyle);
                if (ShowPreviousSessionHighLevel)
                    RenderLevel(chartControl, chartScale, priorSessionHigh, "PS High", dxPrevSessionHigh, ReferenceLineWidth, referenceStrokeStyle);
                if (ShowPreviousSessionLowLevel)
                    RenderLevel(chartControl, chartScale, priorSessionLow, "PS Low", dxPrevSessionLow, ReferenceLineWidth, referenceStrokeStyle);
            }

            if (ShowCurrentSessionLevels)
            {
                if (ShowCurrentSessionOpenLevel)
                    RenderLevel(chartControl, chartScale, currentSessionOpen, "Session Open", dxCurrentSessionOpen, ReferenceLineWidth, referenceStrokeStyle);
                if (ShowCurrentSessionHighLevel)
                    RenderLevel(chartControl, chartScale, currentSessionHigh, "Session High", dxCurrentSessionHigh, ReferenceLineWidth, referenceStrokeStyle);
                if (ShowCurrentSessionLowLevel)
                    RenderLevel(chartControl, chartScale, currentSessionLow, "Session Low", dxCurrentSessionLow, ReferenceLineWidth, referenceStrokeStyle);
            }
        }

        private void RenderProfileLevels(ChartControl chartControl, ChartScale chartScale, int firstBar, int renderLastBar)
        {
            if (ShowSessionProfileLevels)
            {
                if (ShowSessionPoc)
                    RenderLevel(chartControl, chartScale, sessionPoc, "Session POC", dxSessionPoc, ProfileLineWidth, profileStrokeStyle);
                if (ShowSessionVah)
                    RenderLevel(chartControl, chartScale, sessionVah, "Session VAH", dxSessionVah, ProfileLineWidth, profileStrokeStyle);
                if (ShowSessionVal)
                    RenderLevel(chartControl, chartScale, sessionVal, "Session VAL", dxSessionVal, ProfileLineWidth, profileStrokeStyle);
            }

            if (ShowWeeklyProfileLevels)
            {
                if (ShowWeeklyPoc)
                    RenderLevel(chartControl, chartScale, weeklyPoc, "Weekly POC", dxWeeklyPoc, ProfileLineWidth, profileStrokeStyle);
                if (ShowWeeklyVah)
                    RenderLevel(chartControl, chartScale, weeklyVah, "Weekly VAH", dxWeeklyVah, ProfileLineWidth, profileStrokeStyle);
                if (ShowWeeklyVal)
                    RenderLevel(chartControl, chartScale, weeklyVal, "Weekly VAL", dxWeeklyVal, ProfileLineWidth, profileStrokeStyle);
            }

            if (ShowSessionVwap)
                RenderSeriesLine(chartControl, chartScale, sessionVwapSeries, dxSessionVwap, ProfileLineWidth, profileStrokeStyle, firstBar, renderLastBar, "Session VWAP");

            if (ShowWeeklyVwap)
                RenderSeriesLine(chartControl, chartScale, weeklyVwapSeries, dxWeeklyVwap, ProfileLineWidth, profileStrokeStyle, firstBar, renderLastBar, "Weekly VWAP");
        }

        private void RenderLevel(ChartControl chartControl, ChartScale chartScale, double price, string label, SharpDX.Direct2D1.SolidColorBrush brush, float width, StrokeStyle strokeStyle)
        {
            if (!IsRenderablePrice(price) || brush == null)
                return;

            float y = chartScale.GetYByValue(price);
            float xStart = chartControl.GetXByBarIndex(ChartBars, ChartBars.FromIndex);
            float xEnd = chartControl.GetXByBarIndex(ChartBars, ChartBars.ToIndex);
            Vector2 p1 = new Vector2(xStart, y);
            Vector2 p2 = new Vector2(xEnd, y);

            DrawDxLine(p1, p2, brush, width, strokeStyle);

            if (ShowLevelLabels)
                RenderRightLabel(chartControl, chartScale, price, label, brush);
        }

        private void RenderSeriesLine(ChartControl chartControl, ChartScale chartScale, Series<double> series, SharpDX.Direct2D1.SolidColorBrush brush, float width, StrokeStyle strokeStyle, int firstBar, int renderLastBar, string label)
        {
            if (series == null || brush == null || renderLastBar < firstBar)
                return;

            bool hasSegment = false;
            float lastX = 0f;
            float lastY = 0f;
            double lastValue = double.NaN;

            for (int bar = firstBar; bar <= renderLastBar; bar++)
            {
                double value = series.GetValueAt(bar);
                if (!IsRenderablePrice(value))
                {
                    hasSegment = false;
                    continue;
                }

                float x = chartControl.GetXByBarIndex(ChartBars, bar);
                float y = chartScale.GetYByValue(value);

                if (hasSegment)
                {
                    DrawDxLine(new Vector2(lastX, lastY), new Vector2(x, y), brush, width, strokeStyle);
                }

                hasSegment = true;
                lastX = x;
                lastY = y;
                lastValue = value;
            }

            if (ShowLevelLabels && IsRenderablePrice(lastValue))
                RenderRightLabel(chartControl, chartScale, lastValue, label, brush);
        }

        private void RenderRightLabel(ChartControl chartControl, ChartScale chartScale, double price, string text, SharpDX.Direct2D1.SolidColorBrush brush)
        {
            if (levelLabelFormat == null || brush == null)
                return;

            float xEnd = chartControl.GetXByBarIndex(ChartBars, ChartBars.ToIndex);
            float y = chartScale.GetYByValue(price) - levelLabelFormat.FontSize - LabelVerticalPadding;

            using (var layout = new TextLayout(Core.Globals.DirectWriteFactory, text, levelLabelFormat, LabelLayoutWidth, levelLabelFormat.FontSize + LabelLayoutHeightPadding))
            {
                RenderTarget.DrawTextLayout(new Vector2(xEnd - layout.Metrics.Width - LabelHorizontalPadding, y), layout, brush);
            }
        }

        private void DrawDxLine(Vector2 start, Vector2 end, SharpDX.Direct2D1.SolidColorBrush brush, float width, StrokeStyle strokeStyle)
        {
            if (brush == null)
                return;

            if (strokeStyle != null)
                RenderTarget.DrawLine(start, end, brush, width, strokeStyle);
            else
                RenderTarget.DrawLine(start, end, brush, width);
        }

        private bool IsRenderablePrice(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value.ApproxCompare(0) > 0;
        }

        private SharpDX.Direct2D1.SolidColorBrush CreateDxBrush(System.Windows.Media.Brush brush)
        {
            return new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, ToColor4(brush, 1f));
        }

        private Color4 ToColor4(System.Windows.Media.Brush brush, float alpha)
        {
            System.Windows.Media.SolidColorBrush solid = brush as System.Windows.Media.SolidColorBrush;
            if (solid == null)
                return new Color4(1f, 1f, 1f, alpha);

            System.Windows.Media.Color color = solid.Color;
            return new Color4(color.R / 255f, color.G / 255f, color.B / 255f, alpha);
        }

        private StrokeStyle CreateStrokeStyle(DashStyleHelper dashStyle)
        {
            if (dashStyle == DashStyleHelper.Solid)
                return null;

            SharpDX.Direct2D1.DashStyle style = SharpDX.Direct2D1.DashStyle.Solid;
            switch (dashStyle)
            {
                case DashStyleHelper.Dash:
                    style = SharpDX.Direct2D1.DashStyle.Dash;
                    break;
                case DashStyleHelper.Dot:
                    style = SharpDX.Direct2D1.DashStyle.Dot;
                    break;
                case DashStyleHelper.DashDot:
                    style = SharpDX.Direct2D1.DashStyle.DashDot;
                    break;
                case DashStyleHelper.DashDotDot:
                    style = SharpDX.Direct2D1.DashStyle.DashDotDot;
                    break;
            }

            return new StrokeStyle(Core.Globals.D2DFactory, new StrokeStyleProperties
            {
                DashStyle = style,
                StartCap = CapStyle.Flat,
                EndCap = CapStyle.Flat,
                LineJoin = LineJoin.Miter
            });
        }

        private void DisposeDxResources()
        {
            SafeDispose(ref wallStrokeStyle);
            SafeDispose(ref referenceStrokeStyle);
            SafeDispose(ref profileStrokeStyle);
            SafeDispose(ref hudFormat);
            SafeDispose(ref wallFormat);
            SafeDispose(ref levelLabelFormat);
            SafeDispose(ref dxBullShade);
            SafeDispose(ref dxBearShade);
            SafeDispose(ref dxAskWall);
            SafeDispose(ref dxBidWall);
            SafeDispose(ref dxHudText);
            SafeDispose(ref dxArrowUp);
            SafeDispose(ref dxArrowDown);
            SafeDispose(ref dxPrevDayOpen);
            SafeDispose(ref dxPrevDayHigh);
            SafeDispose(ref dxPrevDayLow);
            SafeDispose(ref dxPrevWeekOpen);
            SafeDispose(ref dxPrevWeekHigh);
            SafeDispose(ref dxPrevWeekLow);
            SafeDispose(ref dxPrevSessionOpen);
            SafeDispose(ref dxPrevSessionHigh);
            SafeDispose(ref dxPrevSessionLow);
            SafeDispose(ref dxCurrentSessionOpen);
            SafeDispose(ref dxCurrentSessionHigh);
            SafeDispose(ref dxCurrentSessionLow);
            SafeDispose(ref dxSessionPoc);
            SafeDispose(ref dxSessionVah);
            SafeDispose(ref dxSessionVal);
            SafeDispose(ref dxSessionVwap);
            SafeDispose(ref dxWeeklyPoc);
            SafeDispose(ref dxWeeklyVah);
            SafeDispose(ref dxWeeklyVal);
            SafeDispose(ref dxWeeklyVwap);
        }

        private void SafeDispose<T>(ref T resource) where T : class, IDisposable
        {
            if (resource == null)
                return;

            resource.Dispose();
            resource = null;
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

        [NinjaScriptProperty]
        [Display(Name = "Show Bias Debug", Order = 6, GroupName = "1. Main Settings")]
        public bool ShowBiasDebug { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Previous Day OH/L", Order = 1, GroupName = "2. Reference Levels")]
        public bool ShowPreviousDayLevels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Previous Day Open", Order = 2, GroupName = "2. Reference Levels")]
        public bool ShowPreviousDayOpenLevel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Previous Day High", Order = 3, GroupName = "2. Reference Levels")]
        public bool ShowPreviousDayHighLevel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Previous Day Low", Order = 4, GroupName = "2. Reference Levels")]
        public bool ShowPreviousDayLowLevel { get; set; }

        [XmlIgnore]
        [Display(Name = "Previous Day Open Color", Order = 5, GroupName = "2. Reference Levels")]
        public System.Windows.Media.Brush PreviousDayOpenColor { get; set; }

        [Browsable(false)]
        public string PreviousDayOpenColorSerializable
        {
            get { return Serialize.BrushToString(PreviousDayOpenColor); }
            set { PreviousDayOpenColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Previous Day High Color", Order = 6, GroupName = "2. Reference Levels")]
        public System.Windows.Media.Brush PreviousDayHighColor { get; set; }

        [Browsable(false)]
        public string PreviousDayHighColorSerializable
        {
            get { return Serialize.BrushToString(PreviousDayHighColor); }
            set { PreviousDayHighColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Previous Day Low Color", Order = 7, GroupName = "2. Reference Levels")]
        public System.Windows.Media.Brush PreviousDayLowColor { get; set; }

        [Browsable(false)]
        public string PreviousDayLowColorSerializable
        {
            get { return Serialize.BrushToString(PreviousDayLowColor); }
            set { PreviousDayLowColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Show Previous Week OH/L", Order = 8, GroupName = "2. Reference Levels")]
        public bool ShowPreviousWeekLevels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Previous Week Open", Order = 9, GroupName = "2. Reference Levels")]
        public bool ShowPreviousWeekOpenLevel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Previous Week High", Order = 10, GroupName = "2. Reference Levels")]
        public bool ShowPreviousWeekHighLevel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Previous Week Low", Order = 11, GroupName = "2. Reference Levels")]
        public bool ShowPreviousWeekLowLevel { get; set; }

        [XmlIgnore]
        [Display(Name = "Previous Week Open Color", Order = 12, GroupName = "2. Reference Levels")]
        public System.Windows.Media.Brush PreviousWeekOpenColor { get; set; }

        [Browsable(false)]
        public string PreviousWeekOpenColorSerializable
        {
            get { return Serialize.BrushToString(PreviousWeekOpenColor); }
            set { PreviousWeekOpenColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Previous Week High Color", Order = 13, GroupName = "2. Reference Levels")]
        public System.Windows.Media.Brush PreviousWeekHighColor { get; set; }

        [Browsable(false)]
        public string PreviousWeekHighColorSerializable
        {
            get { return Serialize.BrushToString(PreviousWeekHighColor); }
            set { PreviousWeekHighColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Previous Week Low Color", Order = 14, GroupName = "2. Reference Levels")]
        public System.Windows.Media.Brush PreviousWeekLowColor { get; set; }

        [Browsable(false)]
        public string PreviousWeekLowColorSerializable
        {
            get { return Serialize.BrushToString(PreviousWeekLowColor); }
            set { PreviousWeekLowColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Show Previous Session OH/L", Order = 15, GroupName = "2. Reference Levels")]
        public bool ShowPreviousSessionLevels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Previous Session Open", Order = 16, GroupName = "2. Reference Levels")]
        public bool ShowPreviousSessionOpenLevel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Previous Session High", Order = 17, GroupName = "2. Reference Levels")]
        public bool ShowPreviousSessionHighLevel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Previous Session Low", Order = 18, GroupName = "2. Reference Levels")]
        public bool ShowPreviousSessionLowLevel { get; set; }

        [XmlIgnore]
        [Display(Name = "Previous Session Open Color", Order = 19, GroupName = "2. Reference Levels")]
        public System.Windows.Media.Brush PreviousSessionOpenColor { get; set; }

        [Browsable(false)]
        public string PreviousSessionOpenColorSerializable
        {
            get { return Serialize.BrushToString(PreviousSessionOpenColor); }
            set { PreviousSessionOpenColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Previous Session High Color", Order = 20, GroupName = "2. Reference Levels")]
        public System.Windows.Media.Brush PreviousSessionHighColor { get; set; }

        [Browsable(false)]
        public string PreviousSessionHighColorSerializable
        {
            get { return Serialize.BrushToString(PreviousSessionHighColor); }
            set { PreviousSessionHighColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Previous Session Low Color", Order = 21, GroupName = "2. Reference Levels")]
        public System.Windows.Media.Brush PreviousSessionLowColor { get; set; }

        [Browsable(false)]
        public string PreviousSessionLowColorSerializable
        {
            get { return Serialize.BrushToString(PreviousSessionLowColor); }
            set { PreviousSessionLowColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Show Current Session OH/L", Order = 22, GroupName = "2. Reference Levels")]
        public bool ShowCurrentSessionLevels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Current Session Open", Order = 23, GroupName = "2. Reference Levels")]
        public bool ShowCurrentSessionOpenLevel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Current Session High", Order = 24, GroupName = "2. Reference Levels")]
        public bool ShowCurrentSessionHighLevel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Current Session Low", Order = 25, GroupName = "2. Reference Levels")]
        public bool ShowCurrentSessionLowLevel { get; set; }

        [XmlIgnore]
        [Display(Name = "Current Session Open Color", Order = 26, GroupName = "2. Reference Levels")]
        public System.Windows.Media.Brush CurrentSessionOpenColor { get; set; }

        [Browsable(false)]
        public string CurrentSessionOpenColorSerializable
        {
            get { return Serialize.BrushToString(CurrentSessionOpenColor); }
            set { CurrentSessionOpenColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Current Session High Color", Order = 27, GroupName = "2. Reference Levels")]
        public System.Windows.Media.Brush CurrentSessionHighColor { get; set; }

        [Browsable(false)]
        public string CurrentSessionHighColorSerializable
        {
            get { return Serialize.BrushToString(CurrentSessionHighColor); }
            set { CurrentSessionHighColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Current Session Low Color", Order = 28, GroupName = "2. Reference Levels")]
        public System.Windows.Media.Brush CurrentSessionLowColor { get; set; }

        [Browsable(false)]
        public string CurrentSessionLowColorSerializable
        {
            get { return Serialize.BrushToString(CurrentSessionLowColor); }
            set { CurrentSessionLowColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Reference Line Width", Order = 29, GroupName = "2. Reference Levels")]
        public int ReferenceLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Reference Line Style", Order = 30, GroupName = "2. Reference Levels")]
        public DashStyleHelper ReferenceLineStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Level Labels", Order = 31, GroupName = "2. Reference Levels")]
        public bool ShowLevelLabels { get; set; }

        [NinjaScriptProperty]
        [Range(8, 18)]
        [Display(Name = "Label Font Size", Order = 32, GroupName = "2. Reference Levels")]
        public int LevelLabelFontSize { get; set; }

        // Uses only NinjaTrader's built-in BarsPeriodType enum values.
        // Selecting Renko here uses native NT8 Renko; custom add-on bar types (e.g. NinjaRenko/UniRenko) are intentionally not supported.
        [NinjaScriptProperty]
        [Display(Name = "HTF Bars Period Type", Order = 1, GroupName = "3. Data Series Settings")]
        public BarsPeriodType HtfBarsPeriodType { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "HTF Bars Value", Order = 2, GroupName = "3. Data Series Settings")]
        public int HtfBarsValue { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Confirm Bars Period Type", Order = 3, GroupName = "3. Data Series Settings")]
        public BarsPeriodType ConfirmBarsPeriodType { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Confirm Bars Value", Order = 4, GroupName = "3. Data Series Settings")]
        public int ConfirmBarsValue { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Swing Strength", Order = 5, GroupName = "3. Data Series Settings")]
        public int SwingStrength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Session VAH/VAL/POC", Order = 1, GroupName = "4. Volume Profile Levels")]
        public bool ShowSessionProfileLevels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Session POC", Order = 2, GroupName = "4. Volume Profile Levels")]
        public bool ShowSessionPoc { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Session VAH", Order = 3, GroupName = "4. Volume Profile Levels")]
        public bool ShowSessionVah { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Session VAL", Order = 4, GroupName = "4. Volume Profile Levels")]
        public bool ShowSessionVal { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Weekly VAH/VAL/POC", Order = 5, GroupName = "4. Volume Profile Levels")]
        public bool ShowWeeklyProfileLevels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Weekly POC", Order = 6, GroupName = "4. Volume Profile Levels")]
        public bool ShowWeeklyPoc { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Weekly VAH", Order = 7, GroupName = "4. Volume Profile Levels")]
        public bool ShowWeeklyVah { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Weekly VAL", Order = 8, GroupName = "4. Volume Profile Levels")]
        public bool ShowWeeklyVal { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Session VWAP", Order = 9, GroupName = "4. Volume Profile Levels")]
        public bool ShowSessionVwap { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Weekly VWAP", Order = 10, GroupName = "4. Volume Profile Levels")]
        public bool ShowWeeklyVwap { get; set; }

        [NinjaScriptProperty]
        [Range(50, 99)]
        [Display(Name = "Value Area %", Order = 11, GroupName = "4. Volume Profile Levels")]
        public int ValueAreaPercent { get; set; }

        [XmlIgnore]
        [Display(Name = "Session POC Color", Order = 12, GroupName = "4. Volume Profile Levels")]
        public System.Windows.Media.Brush SessionPocColor { get; set; }

        [Browsable(false)]
        public string SessionPocColorSerializable
        {
            get { return Serialize.BrushToString(SessionPocColor); }
            set { SessionPocColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Session VAH Color", Order = 13, GroupName = "4. Volume Profile Levels")]
        public System.Windows.Media.Brush SessionVahColor { get; set; }

        [Browsable(false)]
        public string SessionVahColorSerializable
        {
            get { return Serialize.BrushToString(SessionVahColor); }
            set { SessionVahColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Session VAL Color", Order = 14, GroupName = "4. Volume Profile Levels")]
        public System.Windows.Media.Brush SessionValColor { get; set; }

        [Browsable(false)]
        public string SessionValColorSerializable
        {
            get { return Serialize.BrushToString(SessionValColor); }
            set { SessionValColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Session VWAP Color", Order = 15, GroupName = "4. Volume Profile Levels")]
        public System.Windows.Media.Brush SessionVwapColor { get; set; }

        [Browsable(false)]
        public string SessionVwapColorSerializable
        {
            get { return Serialize.BrushToString(SessionVwapColor); }
            set { SessionVwapColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Weekly POC Color", Order = 16, GroupName = "4. Volume Profile Levels")]
        public System.Windows.Media.Brush WeeklyPocColor { get; set; }

        [Browsable(false)]
        public string WeeklyPocColorSerializable
        {
            get { return Serialize.BrushToString(WeeklyPocColor); }
            set { WeeklyPocColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Weekly VAH Color", Order = 17, GroupName = "4. Volume Profile Levels")]
        public System.Windows.Media.Brush WeeklyVahColor { get; set; }

        [Browsable(false)]
        public string WeeklyVahColorSerializable
        {
            get { return Serialize.BrushToString(WeeklyVahColor); }
            set { WeeklyVahColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Weekly VAL Color", Order = 18, GroupName = "4. Volume Profile Levels")]
        public System.Windows.Media.Brush WeeklyValColor { get; set; }

        [Browsable(false)]
        public string WeeklyValColorSerializable
        {
            get { return Serialize.BrushToString(WeeklyValColor); }
            set { WeeklyValColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Weekly VWAP Color", Order = 19, GroupName = "4. Volume Profile Levels")]
        public System.Windows.Media.Brush WeeklyVwapColor { get; set; }

        [Browsable(false)]
        public string WeeklyVwapColorSerializable
        {
            get { return Serialize.BrushToString(WeeklyVwapColor); }
            set { WeeklyVwapColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Profile Line Width", Order = 20, GroupName = "4. Volume Profile Levels")]
        public int ProfileLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Profile Line Style", Order = 21, GroupName = "4. Volume Profile Levels")]
        public DashStyleHelper ProfileLineStyle { get; set; }
        #endregion
    }
}
