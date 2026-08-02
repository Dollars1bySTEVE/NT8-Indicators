#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// ============================================================
// LiveProbabilityZones — Phase 1 (Base Version)
// Auto-drawn forward-probability zones with live % updates
//
// Author  : Dollars1bySTEVE
// Version : 1.0  (2026-08-02)
// Phase   : 1 of 2  — ATR projection + barrier-touch probability
//           No L2 dependency. Phase 2 adds DOM/order-flow layer.
//
// See LiveProbabilityZones.md for full design doc & Phase 2 plan.
// ============================================================

namespace NinjaTrader.NinjaScript.Indicators
{
    public class LiveProbabilityZones : Indicator
    {
        // ── private state ─────────────────────────────────────────
        private double   _sessionOpen;
        private double   _dailyATR;
        private bool     _sessionInitialised;

        // Zone price levels (4 above, 4 below)
        private double[] _zoneLevels;
        private const int ZoneCount = 8;   // indices 0-3 = above, 4-7 = below

        // Session timing
        private DateTime _sessionStart;
        private DateTime _sessionEnd;
        private SessionIterator _sessionIterator;

        // ── colour constants ──────────────────────────────────────
        private static readonly Brush ColGrey   = Brushes.DimGray;
        private static readonly Brush ColGold   = Brushes.Goldenrod;
        private static readonly Brush ColGreen  = Brushes.LimeGreen;
        private static readonly Brush ColRed    = Brushes.Crimson;
        private static readonly Brush ColWhite  = Brushes.White;

        // ── initialise ───────────────────────────────────────────
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "Auto-drawn forward-probability zones. Phase 1 — ATR + barrier-touch math. See LiveProbabilityZones.md for full design.";
                Name                     = "LiveProbabilityZones";
                Calculate                = Calculate.OnPriceChange;
                IsOverlay                = true;
                IsAutoScale              = false;
                DrawOnPricePanel         = true;
                DisplayInDataBox         = false;
                ScaleJustification       = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                MaximumBarsLookBack      = MaximumBarsLookBack.Infinite;

                // Default properties
                ATRPeriod                = 14;
                ATRMultiplier1           = 0.5;
                ATRMultiplier2           = 1.0;
                ATRMultiplier3           = 1.5;
                ATRMultiplier4           = 2.0;
                ZoneOpacity              = 60;
                ShowLabels               = true;
                LabelFontSize            = 10;
                ThresholdGrey            = 35.0;
                ThresholdGold            = 75.0;
            }
            else if (State == State.Configure)
            {
                _zoneLevels = new double[ZoneCount];
            }
            else if (State == State.DataLoaded)
            {
                _sessionIterator = new SessionIterator(Bars);
            }
        }

        // ── per-bar logic ─────────────────────────────────────────
        protected override void OnBarUpdate()
        {
            if (CurrentBar < ATRPeriod + 1) return;

            // Detect session open
            if (Bars.IsFirstBarOfSession)
            {
                _sessionOpen         = Open[0];
                _dailyATR            = ATR(ATRPeriod)[0];
                _sessionInitialised  = true;

                // Cache session window for time-remaining calc
                _sessionIterator.GetNextSession(Time[0], false);
                _sessionStart = _sessionIterator.ActualSessionBegin;
                _sessionEnd   = _sessionIterator.ActualSessionEnd;

                // Set zone price levels
                _zoneLevels[0] = _sessionOpen + (ATRMultiplier1 * _dailyATR);
                _zoneLevels[1] = _sessionOpen + (ATRMultiplier2 * _dailyATR);
                _zoneLevels[2] = _sessionOpen + (ATRMultiplier3 * _dailyATR);
                _zoneLevels[3] = _sessionOpen + (ATRMultiplier4 * _dailyATR);
                _zoneLevels[4] = _sessionOpen - (ATRMultiplier1 * _dailyATR);
                _zoneLevels[5] = _sessionOpen - (ATRMultiplier2 * _dailyATR);
                _zoneLevels[6] = _sessionOpen - (ATRMultiplier3 * _dailyATR);
                _zoneLevels[7] = _sessionOpen - (ATRMultiplier4 * _dailyATR);

                RefreshZones();
            }
        }

        // ── live tick update ──────────────────────────────────────
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (!_sessionInitialised) return;
            if (e.MarketDataType != MarketDataType.Last) return;
            if (CurrentBar < ATRPeriod + 1) return;

            RefreshZones();
        }

        // ── core refresh — called every qualifying tick ───────────
        private void RefreshZones()
        {
            double currentPrice = Close[0];
            double sigma        = RollingVolatility();
            double T            = TimeRemaining();

            for (int i = 0; i < ZoneCount; i++)
            {
                if (_zoneLevels[i] == 0) continue;

                double prob     = TouchProbability(currentPrice, _zoneLevels[i], sigma, T);
                Brush  colour   = ZoneColour(prob, _zoneLevels[i] > currentPrice);
                double halfW    = ZoneHalfWidth();
                int    opacity  = ZoneOpacity;

                string rectTag  = "LPZ_rect_" + i;
                string textTag  = "LPZ_text_" + i;

                // Draw zone band
                Draw.Rectangle(
                    this, rectTag, false,
                    CurrentBar, _zoneLevels[i] + halfW,
                    0,          _zoneLevels[i] - halfW,
                    colour, colour, opacity);

                // Draw % label
                if (ShowLabels)
                {
                    Draw.Text(
                        this, textTag,
                        ((int)Math.Round(prob)).ToString() + "%",
                        0, _zoneLevels[i],
                        ColWhite);
                }
            }
        }

        // ── barrier touch probability (reflection principle) ──────
        //
        //   P = 2 × N( -|ln(X/S)| / (σ√T) )
        //
        //   S = current price
        //   X = zone price
        //   σ = fractional daily volatility (ATR / price)
        //   T = fraction of session remaining [0,1]
        // ─────────────────────────────────────────────────────────
        private double TouchProbability(double S, double X, double sigma, double T)
        {
            if (S <= 0 || X <= 0)      return 0.0;
            if (sigma <= 0 || T <= 0)  return (Math.Abs(S - X) < TickSize) ? 99.0 : 0.0;

            double logRatio = Math.Log(X / S);
            double d        = Math.Abs(logRatio) / (sigma * Math.Sqrt(T));
            double prob     = 2.0 * (1.0 - NormalCDF(d));

            return Math.Max(0.0, Math.Min(prob * 100.0, 99.9));
        }

        // ── cumulative normal distribution (Abramowitz & Stegun) ──
        private double NormalCDF(double x)
        {
            const double a1 =  0.319381530;
            const double a2 = -0.356563782;
            const double a3 =  1.781477937;
            const double a4 = -1.821255978;
            const double a5 =  1.330274429;

            double absX = Math.Abs(x);
            double t    = 1.0 / (1.0 + 0.2316419 * absX);
            double poly = t * (a1 + t * (a2 + t * (a3 + t * (a4 + t * a5))));
            double pdf  = Math.Exp(-0.5 * x * x) / Math.Sqrt(2.0 * Math.PI);
            double cdf  = 1.0 - pdf * poly;

            return x >= 0 ? cdf : 1.0 - cdf;
        }

        // ── rolling volatility σ = ATR / price ───────────────────
        private double RollingVolatility()
        {
            double price = Close[0];
            if (price <= 0) return 0.001;
            double atr = ATR(ATRPeriod)[0];
            return atr / price;
        }

        // ── fraction of session remaining [0.001 → 1.0] ──────────
        private double TimeRemaining()
        {
            DateTime now   = Time[0];
            double   total = (_sessionEnd - _sessionStart).TotalMinutes;
            double   left  = (_sessionEnd - now).TotalMinutes;
            if (total <= 0) return 0.001;
            return Math.Max(left / total, 0.001);
        }

        // ── zone half-width in price points ──────────────────────
        private double ZoneHalfWidth()
        {
            // 5% of ATR per side, clamped to minimum of 2 ticks
            double halfW = _dailyATR * 0.05;
            return Math.Max(halfW, TickSize * 2);
        }

        // ── colour from probability + direction ───────────────────
        private Brush ZoneColour(double prob, bool isAbovePrice)
        {
            if (prob >= ThresholdGold)        return ColGreen;
            if (prob >= ThresholdGrey)        return ColGold;
            if (isAbovePrice && prob < ThresholdGrey) return ColRed;
            return ColGrey;
        }

        // ── properties ───────────────────────────────────────────
        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "ATR Period", GroupName = "Settings", Order = 1)]
        public int ATRPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 5.0)]
        [Display(Name = "ATR Multiplier 1", GroupName = "Settings", Order = 2)]
        public double ATRMultiplier1 { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 5.0)]
        [Display(Name = "ATR Multiplier 2", GroupName = "Settings", Order = 3)]
        public double ATRMultiplier2 { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 5.0)]
        [Display(Name = "ATR Multiplier 3", GroupName = "Settings", Order = 4)]
        public double ATRMultiplier3 { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 5.0)]
        [Display(Name = "ATR Multiplier 4", GroupName = "Settings", Order = 5)]
        public double ATRMultiplier4 { get; set; }

        [NinjaScriptProperty]
        [Range(10, 90)]
        [Display(Name = "Zone Opacity %", GroupName = "Visual", Order = 1)]
        public int ZoneOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show % Labels", GroupName = "Visual", Order = 2)]
        public bool ShowLabels { get; set; }

        [NinjaScriptProperty]
        [Range(6, 24)]
        [Display(Name = "Label Font Size", GroupName = "Visual", Order = 3)]
        public int LabelFontSize { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 60.0)]
        [Display(Name = "Grey → Gold threshold %", GroupName = "Thresholds", Order = 1,
            Description = "Probability % at which zone turns from Grey to Gold")]
        public double ThresholdGrey { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 99.0)]
        [Display(Name = "Gold → Green threshold %", GroupName = "Thresholds", Order = 2,
            Description = "Probability % at which zone turns from Gold to Green")]
        public double ThresholdGold { get; set; }
    }
}
