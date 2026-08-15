#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
#endregion

// NT8 compatibility: custom enums should be declared outside namespace.
public enum PDZEquilibriumMode
{
    SwingFib50,
    MarketProfilePOC,
    VWAP,
    OrderBlockMid
}

public enum PDZL2WallMode
{
    Percentile95,
    Fixed,
    Adaptive
}

public enum PDZDashboardMode
{
    Minimal,
    Standard,
    Full
}

public enum PDZDashboardPosition
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// Premium/Discount zones overlay with configurable equilibrium models,
    /// SharpDX rendering, L2 wall detection, alerts, and dashboard.
    /// </summary>
    public class PremiumDiscountZonesGPU : Indicator
    {
        #region Private types

        private class PdzBookLevel
        {
            public double Price;
            public long Size;
        }

        #endregion

        #region Private fields

        private readonly object _stateLock = new object();

        // Zone state
        private double _rangeHigh;
        private double _rangeLow;
        private double _equilibrium;
        private double _equilibriumHigh;
        private double _equilibriumLow;
        private string _zoneState = "Unknown";
        private string _previousZoneState = "Unknown";

        // VWAP / profile state
        private DateTime _sessionDate = DateTime.MinValue;
        private double _sessionCumPV;
        private double _sessionCumVol;
        private readonly Dictionary<double, double> _sessionProfile = new Dictionary<double, double>();
        private double _lastBarCumVolume;

        // L2 state
        private Dictionary<double, PdzBookLevel> _bidBook;
        private Dictionary<double, PdzBookLevel> _askBook;
        private readonly Queue<long> _bookSamples = new Queue<long>();
        private bool _level2Available;
        private double _wallBidPrice;
        private long _wallBidSize;
        private double _wallAskPrice;
        private long _wallAskSize;

        // Alerts
        private int _lastZoneAlertBar = -999999;
        private int _lastEqAlertBar = -999999;

        // SharpDX resources
        private bool _dxReady;
        private SharpDX.DirectWrite.Factory _dxWriteFactory;
        private SharpDX.DirectWrite.TextFormat _dxLabelFormat;
        private SharpDX.Direct2D1.SolidColorBrush _dxPremiumBrush;
        private SharpDX.Direct2D1.SolidColorBrush _dxDiscountBrush;
        private SharpDX.Direct2D1.SolidColorBrush _dxEquilibriumBrush;
        private SharpDX.Direct2D1.SolidColorBrush _dxPremiumLabelBrush;
        private SharpDX.Direct2D1.SolidColorBrush _dxDiscountLabelBrush;
        private SharpDX.Direct2D1.SolidColorBrush _dxEquilibriumLabelBrush;
        private SharpDX.Direct2D1.SolidColorBrush _dxWallBidBrush;
        private SharpDX.Direct2D1.SolidColorBrush _dxWallAskBrush;
        private string _dxResourceKey = string.Empty;
        private NinjaTrader.Gui.Tools.SimpleFont _dashboardFont;

        #endregion

        #region OnStateChange

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "GPU Premium/Discount zone overlay with configurable equilibrium models, L2 wall awareness, alerts, and dashboard.";
                Name = "PremiumDiscountZonesGPU";
                Calculate = Calculate.OnPriceChange;
                IsOverlay = true;
                IsAutoScale = false;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;

                // Core
                EquilibriumMode = PDZEquilibriumMode.SwingFib50;
                SwingLookback = 100;
                EquilibriumBandPercent = 4.0;
                ProfileBinTicks = 2;
                OrderBlockLookback = 50;

                // Zones & labels
                PremiumColor = Brushes.IndianRed;
                DiscountColor = Brushes.SeaGreen;
                EquilibriumColor = Brushes.Tan;
                PremiumOpacity = 22;
                DiscountOpacity = 22;
                EquilibriumOpacity = 35;
                ShowZoneLabels = true;
                PremiumLabelText = "Premium";
                DiscountLabelText = "Discount";
                EquilibriumLabelText = "Equilibrium";
                ShowPriceInLabel = true;
                ShowRangePctInLabel = true;
                LabelFontSize = 12;

                // L2
                EnableLevel2 = false;
                L2WallMode = PDZL2WallMode.Percentile95;
                FixedWallSize = 300;
                AdaptiveWallMultiplier = 4.0;
                L2SampleSize = 250;
                ShowOrderBookLines = true;
                BidWallColor = Brushes.LimeGreen;
                AskWallColor = Brushes.Crimson;
                WallLineOpacity = 85;
                WallLineThickness = 2;

                // Alerts
                EnableAlerts = false;
                AlertOnZoneEntry = true;
                AlertOnEquilibriumTouch = true;
                AlertCooldownBars = 10;
                ZoneEntrySound = "Alert2.wav";
                EquilibriumTouchSound = "Alert4.wav";

                // Dashboard
                ShowDashboard = true;
                DashboardMode = PDZDashboardMode.Standard;
                DashboardPosition = PDZDashboardPosition.TopRight;

                IsSuspendedWhileInactive = false;
                MaximumBarsLookBack = MaximumBarsLookBack.Infinite;
            }
            else if (State == State.DataLoaded)
            {
                _bidBook = new Dictionary<double, PdzBookLevel>();
                _askBook = new Dictionary<double, PdzBookLevel>();
                _bookSamples.Clear();
                _sessionProfile.Clear();
                _sessionDate = DateTime.MinValue;
                _dashboardFont = new NinjaTrader.Gui.Tools.SimpleFont("Segoe UI", 12);
            }
            else if (State == State.Terminated)
            {
                DisposeDxResources();
            }
        }

        #endregion

        #region Data processing

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 2)
                return;

            UpdateSessionAccumulators();
            UpdateZones();
            UpdateAlerts();
            UpdateDashboard();
        }

        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (!EnableLevel2)
                return;

            if (e == null)
                return;

            bool isBid = e.MarketDataType == MarketDataType.Bid;
            bool isAsk = e.MarketDataType == MarketDataType.Ask;
            if (!isBid && !isAsk)
                return;

            lock (_stateLock)
            {
                _level2Available = true;

                Dictionary<double, PdzBookLevel> book = isBid ? _bidBook : _askBook;
                PdzBookLevel existing;
                book.TryGetValue(e.Price, out existing);

                switch (e.Operation)
                {
                    case Operation.Add:
                    case Operation.Update:
                        if (existing == null)
                        {
                            existing = new PdzBookLevel { Price = e.Price };
                            book[e.Price] = existing;
                        }
                        existing.Size = e.Volume;
                        AddL2Sample(e.Volume);
                        break;

                    case Operation.Remove:
                        if (existing != null)
                        {
                            book.Remove(e.Price);
                        }
                        break;
                }

                DetectWallsLocked();
            }
        }

        private void UpdateSessionAccumulators()
        {
            DateTime barDate = Time[0].Date;
            if (_sessionDate != barDate)
            {
                _sessionDate = barDate;
                _sessionCumPV = 0;
                _sessionCumVol = 0;
                _sessionProfile.Clear();
                _lastBarCumVolume = 0;
            }

            double cumulativeVol = Math.Max(0, Volume[0]);
            double deltaVol = cumulativeVol - _lastBarCumVolume;
            if (IsFirstTickOfBar || deltaVol < 0)
                deltaVol = cumulativeVol;
            _lastBarCumVolume = cumulativeVol;

            if (deltaVol <= 0)
                return;

            double typicalPrice = (High[0] + Low[0] + Close[0]) / 3.0;
            _sessionCumPV += typicalPrice * deltaVol;
            _sessionCumVol += deltaVol;

            double bin = TickSize * Math.Max(1, ProfileBinTicks);
            double key = Math.Round(typicalPrice / bin) * bin;
            double existing;
            _sessionProfile.TryGetValue(key, out existing);
            _sessionProfile[key] = existing + deltaVol;
        }

        private void UpdateZones()
        {
            int lookback = Math.Min(Math.Max(10, SwingLookback), CurrentBar + 1);
            double highest = High[0];
            double lowest = Low[0];

            for (int i = 1; i < lookback; i++)
            {
                if (High[i] > highest) highest = High[i];
                if (Low[i] < lowest) lowest = Low[i];
            }

            _rangeHigh = highest;
            _rangeLow = lowest;

            double eq = CalculateEquilibrium();
            if (double.IsNaN(eq) || double.IsInfinity(eq) || eq <= 0)
                eq = (_rangeHigh + _rangeLow) * 0.5;

            eq = Math.Min(_rangeHigh, Math.Max(_rangeLow, eq));
            _equilibrium = eq;

            double range = Math.Max(TickSize, _rangeHigh - _rangeLow);
            double halfBand = range * Math.Max(0.1, EquilibriumBandPercent) / 100.0 * 0.5;
            _equilibriumHigh = Math.Min(_rangeHigh, _equilibrium + halfBand);
            _equilibriumLow = Math.Max(_rangeLow, _equilibrium - halfBand);

            _previousZoneState = _zoneState;
            if (Close[0] > _equilibriumHigh) _zoneState = "Premium";
            else if (Close[0] < _equilibriumLow) _zoneState = "Discount";
            else _zoneState = "Equilibrium";
        }

        private double CalculateEquilibrium()
        {
            switch (EquilibriumMode)
            {
                case PDZEquilibriumMode.MarketProfilePOC:
                    return CalculatePoc();
                case PDZEquilibriumMode.VWAP:
                    return _sessionCumVol > 0 ? _sessionCumPV / _sessionCumVol : (_rangeHigh + _rangeLow) * 0.5;
                case PDZEquilibriumMode.OrderBlockMid:
                    return CalculateOrderBlockMid();
                default:
                    return (_rangeHigh + _rangeLow) * 0.5;
            }
        }

        private double CalculatePoc()
        {
            if (_sessionProfile.Count == 0)
                return (_rangeHigh + _rangeLow) * 0.5;

            double bestPrice = (_rangeHigh + _rangeLow) * 0.5;
            double bestVol = double.MinValue;
            foreach (var kv in _sessionProfile)
            {
                if (kv.Value > bestVol)
                {
                    bestVol = kv.Value;
                    bestPrice = kv.Key;
                }
            }
            return bestPrice;
        }

        private double CalculateOrderBlockMid()
        {
            int lookback = Math.Min(Math.Max(5, OrderBlockLookback), CurrentBar + 1);
            double bestMid = (_rangeHigh + _rangeLow) * 0.5;
            double bestVolume = -1;

            for (int i = 0; i < lookback; i++)
            {
                double body = Math.Abs(Close[i] - Open[i]);
                double range = High[i] - Low[i];
                if (range <= TickSize)
                    continue;

                // Use large-body, high-volume candles as an order-block proxy.
                double bodyRatio = body / range;
                if (bodyRatio < 0.45)
                    continue;

                if (Volume[i] > bestVolume)
                {
                    bestVolume = Volume[i];
                    bestMid = (High[i] + Low[i]) * 0.5;
                }
            }

            return bestMid;
        }

        private void AddL2Sample(long size)
        {
            if (size <= 0)
                return;

            _bookSamples.Enqueue(size);
            while (_bookSamples.Count > Math.Max(50, L2SampleSize))
                _bookSamples.Dequeue();
        }

        private void DetectWallsLocked()
        {
            DetectWallForBook(_bidBook, true);
            DetectWallForBook(_askBook, false);
        }

        private void DetectWallForBook(Dictionary<double, PdzBookLevel> book, bool isBid)
        {
            if (book == null || book.Count == 0)
            {
                if (isBid) { _wallBidPrice = 0; _wallBidSize = 0; }
                else { _wallAskPrice = 0; _wallAskSize = 0; }
                return;
            }

            double threshold;
            switch (L2WallMode)
            {
                case PDZL2WallMode.Fixed:
                    threshold = Math.Max(1, FixedWallSize);
                    break;
                case PDZL2WallMode.Adaptive:
                    long recalculatedSizeSum = 0;
                    foreach (var kv in book)
                        recalculatedSizeSum += kv.Value.Size;
                    threshold = Math.Max(1.0,
                        (book.Count > 0 ? (double)recalculatedSizeSum / book.Count : 0) * Math.Max(1.0, AdaptiveWallMultiplier));
                    break;
                default:
                    threshold = ComputePercentile95();
                    break;
            }

            double wallPrice = 0;
            long wallSize = 0;
            foreach (var kv in book)
            {
                if (kv.Value.Size >= threshold && kv.Value.Size > wallSize)
                {
                    wallSize = kv.Value.Size;
                    wallPrice = kv.Key;
                }
            }

            if (isBid)
            {
                _wallBidPrice = wallPrice;
                _wallBidSize = wallSize;
            }
            else
            {
                _wallAskPrice = wallPrice;
                _wallAskSize = wallSize;
            }
        }

        private double ComputePercentile95()
        {
            if (_bookSamples.Count == 0)
                return Math.Max(1, FixedWallSize);

            var arr = _bookSamples.ToArray();
            Array.Sort(arr);
            int idx = (int)Math.Round((arr.Length - 1) * 0.95);
            idx = Math.Max(0, Math.Min(arr.Length - 1, idx));
            return Math.Max(1, arr[idx]);
        }

        private void UpdateAlerts()
        {
            if (!EnableAlerts)
                return;

            if (AlertOnZoneEntry && !string.Equals(_zoneState, _previousZoneState, StringComparison.Ordinal)
                && CurrentBar - _lastZoneAlertBar >= Math.Max(1, AlertCooldownBars))
            {
                _lastZoneAlertBar = CurrentBar;
                SafeAlert("PDZ_ZONE_" + CurrentBar,
                    "PDZ zone entry: " + _zoneState,
                    string.IsNullOrWhiteSpace(ZoneEntrySound) ? "Alert2.wav" : ZoneEntrySound);
            }

            if (AlertOnEquilibriumTouch
                && High[0] >= _equilibrium
                && Low[0] <= _equilibrium
                && CurrentBar - _lastEqAlertBar >= Math.Max(1, AlertCooldownBars))
            {
                _lastEqAlertBar = CurrentBar;
                SafeAlert("PDZ_EQ_" + CurrentBar,
                    "PDZ equilibrium touched @ " + Instrument.MasterInstrument.FormatPrice(_equilibrium),
                    string.IsNullOrWhiteSpace(EquilibriumTouchSound) ? "Alert4.wav" : EquilibriumTouchSound);
            }
        }

        private void SafeAlert(string id, string message, string sound)
        {
            try
            {
                Alert(id, Priority.Low, message, sound, 10, Brushes.Black, Brushes.White);
            }
            catch (Exception ex)
            {
                Print("PremiumDiscountZonesGPU alert error: " + ex.Message);
            }
        }

        private void UpdateDashboard()
        {
            if (!ShowDashboard)
                return;

            var sb = new StringBuilder();
            sb.Append("PDZ ");
            sb.Append(EquilibriumMode);
            sb.Append(" | ");
            sb.Append(_zoneState);

            if (DashboardMode != PDZDashboardMode.Minimal)
            {
                sb.Append("\nEq: ").Append(Instrument.MasterInstrument.FormatPrice(_equilibrium));
                sb.Append("  R: ").Append(Instrument.MasterInstrument.FormatPrice(_rangeLow));
                sb.Append(" - ").Append(Instrument.MasterInstrument.FormatPrice(_rangeHigh));

                string l2Status = EnableLevel2
                    ? (_level2Available ? "Live" : "Waiting")
                    : "Off";
                sb.Append("\nL2: ").Append(l2Status).Append(" (Mode: ").Append(L2WallMode).Append(")");
            }

            if (DashboardMode == PDZDashboardMode.Full)
            {
                sb.Append("\nBidWall: ");
                sb.Append(_wallBidSize > 0
                    ? Instrument.MasterInstrument.FormatPrice(_wallBidPrice) + " x " + _wallBidSize
                    : "—");

                sb.Append("\nAskWall: ");
                sb.Append(_wallAskSize > 0
                    ? Instrument.MasterInstrument.FormatPrice(_wallAskPrice) + " x " + _wallAskSize
                    : "—");

                double range = Math.Max(TickSize, _rangeHigh - _rangeLow);
                double eqPct = ((_equilibrium - _rangeLow) / range) * 100.0;
                sb.Append("\nEq%: ").Append(eqPct.ToString("0.0")).Append("%");
            }

            Draw.TextFixed(this,
                "PDZ_DASH",
                sb.ToString(),
                ToTextPosition(DashboardPosition),
                Brushes.White,
                _dashboardFont ?? (_dashboardFont = new NinjaTrader.Gui.Tools.SimpleFont("Segoe UI", 12)),
                Brushes.Transparent,
                Brushes.Transparent,
                0);
        }

        private static TextPosition ToTextPosition(PDZDashboardPosition position)
        {
            switch (position)
            {
                case PDZDashboardPosition.TopLeft: return TextPosition.TopLeft;
                case PDZDashboardPosition.BottomLeft: return TextPosition.BottomLeft;
                case PDZDashboardPosition.BottomRight: return TextPosition.BottomRight;
                default: return TextPosition.TopRight;
            }
        }

        #endregion

        #region Rendering

        public override void OnRenderTargetChanged()
        {
            base.OnRenderTargetChanged();
            _dxReady = false;
            DisposeDxResources();
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (Bars == null || ChartBars == null || RenderTarget == null)
                return;

            string key = BuildDxResourceKey();
            if (!_dxReady || !string.Equals(_dxResourceKey, key, StringComparison.Ordinal))
                CreateDxResources();

            if (!_dxReady)
                return;

            float rtW = RenderTarget.Size.Width;

            // Fill zones first
            DrawZoneFill(chartScale, _rangeHigh, _equilibriumHigh, _dxPremiumBrush);
            DrawZoneFill(chartScale, _equilibriumHigh, _equilibriumLow, _dxEquilibriumBrush);
            DrawZoneFill(chartScale, _equilibriumLow, _rangeLow, _dxDiscountBrush);

            // Optional L2 wall lines
            if (EnableLevel2 && ShowOrderBookLines && _level2Available)
                DrawWallLines(chartScale, rtW);

            // Labels
            if (ShowZoneLabels && _dxLabelFormat != null)
                DrawZoneLabels(chartScale, rtW);
        }

        private void DrawZoneFill(ChartScale cs, double topPrice, double bottomPrice, SharpDX.Direct2D1.SolidColorBrush brush)
        {
            if (brush == null
                || double.IsNaN(topPrice) || double.IsInfinity(topPrice)
                || double.IsNaN(bottomPrice) || double.IsInfinity(bottomPrice))
                return;

            float y1 = cs.GetYByValue(topPrice);
            float y2 = cs.GetYByValue(bottomPrice);
            if (float.IsNaN(y1) || float.IsNaN(y2) || float.IsInfinity(y1) || float.IsInfinity(y2))
                return;

            float top = Math.Min(y1, y2);
            float height = Math.Abs(y2 - y1);
            if (height < 1f)
                return;

            RenderTarget.FillRectangle(new SharpDX.RectangleF(0f, top, RenderTarget.Size.Width, height), brush);
        }

        private void DrawWallLines(ChartScale cs, float rtW)
        {
            double bidPrice;
            long bidSize;
            double askPrice;
            long askSize;

            lock (_stateLock)
            {
                bidPrice = _wallBidPrice;
                bidSize = _wallBidSize;
                askPrice = _wallAskPrice;
                askSize = _wallAskSize;
            }

            if (bidSize > 0 && _dxWallBidBrush != null)
            {
                float y = cs.GetYByValue(bidPrice);
                if (!float.IsNaN(y) && !float.IsInfinity(y))
                {
                    RenderTarget.DrawLine(new SharpDX.Vector2(0, y), new SharpDX.Vector2(rtW, y), _dxWallBidBrush, Math.Max(1, WallLineThickness));
                    if (ShowZoneLabels)
                    {
                        string text = "Bid Wall " + Instrument.MasterInstrument.FormatPrice(bidPrice) + " x " + bidSize;
                        RenderTarget.DrawText(text, _dxLabelFormat, new SharpDX.RectangleF(6f, y - 14f, 320f, 18f), _dxWallBidBrush);
                    }
                }
            }

            if (askSize > 0 && _dxWallAskBrush != null)
            {
                float y = cs.GetYByValue(askPrice);
                if (!float.IsNaN(y) && !float.IsInfinity(y))
                {
                    RenderTarget.DrawLine(new SharpDX.Vector2(0, y), new SharpDX.Vector2(rtW, y), _dxWallAskBrush, Math.Max(1, WallLineThickness));
                    if (ShowZoneLabels)
                    {
                        string text = "Ask Wall " + Instrument.MasterInstrument.FormatPrice(askPrice) + " x " + askSize;
                        RenderTarget.DrawText(text, _dxLabelFormat, new SharpDX.RectangleF(6f, y - 14f, 320f, 18f), _dxWallAskBrush);
                    }
                }
            }
        }

        private void DrawZoneLabels(ChartScale cs, float rtW)
        {
            double range = Math.Max(TickSize, _rangeHigh - _rangeLow);

            string premium = BuildZoneLabel(PremiumLabelText, _equilibriumHigh, ((_rangeHigh - _equilibriumHigh) / range) * 100.0);
            string eq = BuildZoneLabel(EquilibriumLabelText, _equilibrium, ((_equilibriumHigh - _equilibriumLow) / range) * 100.0);
            string discount = BuildZoneLabel(DiscountLabelText, _equilibriumLow, ((_equilibriumLow - _rangeLow) / range) * 100.0);

            float x = Math.Max(4f, rtW - 300f);

            float yPremium = cs.GetYByValue((_rangeHigh + _equilibriumHigh) * 0.5);
            if (!float.IsNaN(yPremium) && !float.IsInfinity(yPremium) && _dxPremiumLabelBrush != null)
                RenderTarget.DrawText(premium, _dxLabelFormat, new SharpDX.RectangleF(x, yPremium - 10f, 292f, 20f), _dxPremiumLabelBrush);

            float yEq = cs.GetYByValue((_equilibriumHigh + _equilibriumLow) * 0.5);
            if (!float.IsNaN(yEq) && !float.IsInfinity(yEq) && _dxEquilibriumLabelBrush != null)
                RenderTarget.DrawText(eq, _dxLabelFormat, new SharpDX.RectangleF(x, yEq - 10f, 292f, 20f), _dxEquilibriumLabelBrush);

            float yDiscount = cs.GetYByValue((_equilibriumLow + _rangeLow) * 0.5);
            if (!float.IsNaN(yDiscount) && !float.IsInfinity(yDiscount) && _dxDiscountLabelBrush != null)
                RenderTarget.DrawText(discount, _dxLabelFormat, new SharpDX.RectangleF(x, yDiscount - 10f, 292f, 20f), _dxDiscountLabelBrush);
        }

        private string BuildZoneLabel(string baseText, double price, double pct)
        {
            var sb = new StringBuilder();
            sb.Append(string.IsNullOrWhiteSpace(baseText) ? "Zone" : baseText.Trim());
            if (ShowPriceInLabel)
                sb.Append(" ").Append(Instrument.MasterInstrument.FormatPrice(price));
            if (ShowRangePctInLabel)
                sb.Append(" (").Append(pct.ToString("0.0")).Append("%)");
            return sb.ToString();
        }

        #endregion

        #region SharpDX resource helpers

        private void CreateDxResources()
        {
            DisposeDxResources();

            try
            {
                if (RenderTarget == null)
                {
                    _dxReady = false;
                    return;
                }

                _dxWriteFactory = new SharpDX.DirectWrite.Factory();
                _dxLabelFormat = new SharpDX.DirectWrite.TextFormat(_dxWriteFactory, "Segoe UI", Math.Max(9f, LabelFontSize));

                _dxPremiumBrush = MakeBrush(RenderTarget, PremiumColor, PremiumOpacity / 100f);
                _dxDiscountBrush = MakeBrush(RenderTarget, DiscountColor, DiscountOpacity / 100f);
                _dxEquilibriumBrush = MakeBrush(RenderTarget, EquilibriumColor, EquilibriumOpacity / 100f);

                _dxPremiumLabelBrush = MakeBrush(RenderTarget, PremiumColor, 1f);
                _dxDiscountLabelBrush = MakeBrush(RenderTarget, DiscountColor, 1f);
                _dxEquilibriumLabelBrush = MakeBrush(RenderTarget, EquilibriumColor, 1f);

                _dxWallBidBrush = MakeBrush(RenderTarget, BidWallColor, WallLineOpacity / 100f);
                _dxWallAskBrush = MakeBrush(RenderTarget, AskWallColor, WallLineOpacity / 100f);

                _dxResourceKey = BuildDxResourceKey();
                _dxReady = true;
            }
            catch (Exception ex)
            {
                Print("PremiumDiscountZonesGPU CreateDxResources error: " + ex.Message);
                _dxReady = false;
                _dxResourceKey = string.Empty;
                DisposeDxResources();
            }
        }

        private string BuildDxResourceKey()
        {
            return string.Concat(
                Serialize.BrushToString(PremiumColor), "|", PremiumOpacity, "|",
                Serialize.BrushToString(DiscountColor), "|", DiscountOpacity, "|",
                Serialize.BrushToString(EquilibriumColor), "|", EquilibriumOpacity, "|",
                Serialize.BrushToString(BidWallColor), "|", Serialize.BrushToString(AskWallColor), "|",
                WallLineOpacity, "|", LabelFontSize);
        }

        private static SharpDX.Direct2D1.SolidColorBrush MakeBrush(SharpDX.Direct2D1.RenderTarget rt, Brush brush, float opacity)
        {
            var scb = brush as SolidColorBrush;
            if (scb != null)
            {
                Color c = scb.Color;
                return new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, Math.Max(0f, Math.Min(1f, opacity))));
            }

            return new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Color4(1f, 1f, 1f, Math.Max(0f, Math.Min(1f, opacity))));
        }

        private void DisposeDxResources()
        {
            DisposeRef(ref _dxWriteFactory);
            DisposeRef(ref _dxLabelFormat);
            DisposeRef(ref _dxPremiumBrush);
            DisposeRef(ref _dxDiscountBrush);
            DisposeRef(ref _dxEquilibriumBrush);
            DisposeRef(ref _dxPremiumLabelBrush);
            DisposeRef(ref _dxDiscountLabelBrush);
            DisposeRef(ref _dxEquilibriumLabelBrush);
            DisposeRef(ref _dxWallBidBrush);
            DisposeRef(ref _dxWallAskBrush);
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

        #region Properties — 1. Core

        [NinjaScriptProperty]
        [Display(Name = "Equilibrium Mode", Order = 1, GroupName = "1. Core")]
        public PDZEquilibriumMode EquilibriumMode { get; set; }

        [NinjaScriptProperty]
        [Range(10, 2000)]
        [Display(Name = "Swing Lookback", Order = 2, GroupName = "1. Core")]
        public int SwingLookback { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 50.0)]
        [Display(Name = "Equilibrium Band %", Order = 3, GroupName = "1. Core",
            Description = "Total equilibrium zone width as percent of current range.")]
        public double EquilibriumBandPercent { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Profile Bin Ticks", Order = 4, GroupName = "1. Core")]
        public int ProfileBinTicks { get; set; }

        [NinjaScriptProperty]
        [Range(5, 500)]
        [Display(Name = "Order Block Lookback", Order = 5, GroupName = "1. Core")]
        public int OrderBlockLookback { get; set; }

        #endregion

        #region Properties — 2. Zones & Labels

        [NinjaScriptProperty]
        [Display(Name = "Premium Color", Order = 1, GroupName = "2. Zones & Labels")]
        [XmlIgnore]
        public Brush PremiumColor { get; set; }

        [Browsable(false)]
        public string PremiumColorSerializable
        {
            get { return Serialize.BrushToString(PremiumColor); }
            set { PremiumColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Discount Color", Order = 2, GroupName = "2. Zones & Labels")]
        [XmlIgnore]
        public Brush DiscountColor { get; set; }

        [Browsable(false)]
        public string DiscountColorSerializable
        {
            get { return Serialize.BrushToString(DiscountColor); }
            set { DiscountColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Equilibrium Color", Order = 3, GroupName = "2. Zones & Labels")]
        [XmlIgnore]
        public Brush EquilibriumColor { get; set; }

        [Browsable(false)]
        public string EquilibriumColorSerializable
        {
            get { return Serialize.BrushToString(EquilibriumColor); }
            set { EquilibriumColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Premium Opacity %", Order = 4, GroupName = "2. Zones & Labels")]
        public int PremiumOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Discount Opacity %", Order = 5, GroupName = "2. Zones & Labels")]
        public int DiscountOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Equilibrium Opacity %", Order = 6, GroupName = "2. Zones & Labels")]
        public int EquilibriumOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Zone Labels", Order = 7, GroupName = "2. Zones & Labels")]
        public bool ShowZoneLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Premium Label Text", Order = 8, GroupName = "2. Zones & Labels")]
        public string PremiumLabelText { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Discount Label Text", Order = 9, GroupName = "2. Zones & Labels")]
        public string DiscountLabelText { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Equilibrium Label Text", Order = 10, GroupName = "2. Zones & Labels")]
        public string EquilibriumLabelText { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Price In Label", Order = 11, GroupName = "2. Zones & Labels")]
        public bool ShowPriceInLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show % of Range In Label", Order = 12, GroupName = "2. Zones & Labels")]
        public bool ShowRangePctInLabel { get; set; }

        [NinjaScriptProperty]
        [Range(9, 26)]
        [Display(Name = "Label Font Size", Order = 13, GroupName = "2. Zones & Labels")]
        public int LabelFontSize { get; set; }

        #endregion

        #region Properties — 3. L2 Order Book

        [NinjaScriptProperty]
        [Display(Name = "Enable Level 2", Order = 1, GroupName = "3. L2 Order Book")]
        public bool EnableLevel2 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Wall Detection Mode", Order = 2, GroupName = "3. L2 Order Book")]
        public PDZL2WallMode L2WallMode { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000000)]
        [Display(Name = "Fixed Wall Size", Order = 3, GroupName = "3. L2 Order Book")]
        public int FixedWallSize { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 50.0)]
        [Display(Name = "Adaptive Multiplier", Order = 4, GroupName = "3. L2 Order Book")]
        public double AdaptiveWallMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(50, 5000)]
        [Display(Name = "L2 Sample Size", Order = 5, GroupName = "3. L2 Order Book")]
        public int L2SampleSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Order Book Lines", Order = 6, GroupName = "3. L2 Order Book")]
        public bool ShowOrderBookLines { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Bid Wall Color", Order = 7, GroupName = "3. L2 Order Book")]
        [XmlIgnore]
        public Brush BidWallColor { get; set; }

        [Browsable(false)]
        public string BidWallColorSerializable
        {
            get { return Serialize.BrushToString(BidWallColor); }
            set { BidWallColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Ask Wall Color", Order = 8, GroupName = "3. L2 Order Book")]
        [XmlIgnore]
        public Brush AskWallColor { get; set; }

        [Browsable(false)]
        public string AskWallColorSerializable
        {
            get { return Serialize.BrushToString(AskWallColor); }
            set { AskWallColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Wall Line Opacity %", Order = 9, GroupName = "3. L2 Order Book")]
        public int WallLineOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 6)]
        [Display(Name = "Wall Line Thickness", Order = 10, GroupName = "3. L2 Order Book")]
        public int WallLineThickness { get; set; }

        #endregion

        #region Properties — 4. Alerts

        [NinjaScriptProperty]
        [Display(Name = "Enable Alerts", Order = 1, GroupName = "4. Alerts")]
        public bool EnableAlerts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert On Zone Entry", Order = 2, GroupName = "4. Alerts")]
        public bool AlertOnZoneEntry { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert On Equilibrium Touch", Order = 3, GroupName = "4. Alerts")]
        public bool AlertOnEquilibriumTouch { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Alert Cooldown (bars)", Order = 4, GroupName = "4. Alerts")]
        public int AlertCooldownBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Zone Entry Sound", Order = 5, GroupName = "4. Alerts")]
        public string ZoneEntrySound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Equilibrium Touch Sound", Order = 6, GroupName = "4. Alerts")]
        public string EquilibriumTouchSound { get; set; }

        #endregion

        #region Properties — 5. Dashboard

        [NinjaScriptProperty]
        [Display(Name = "Show Dashboard", Order = 1, GroupName = "5. Dashboard")]
        public bool ShowDashboard { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Dashboard Mode", Order = 2, GroupName = "5. Dashboard")]
        public PDZDashboardMode DashboardMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Dashboard Position", Order = 3, GroupName = "5. Dashboard")]
        public PDZDashboardPosition DashboardPosition { get; set; }

        #endregion
    }
}
