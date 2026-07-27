#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class RenkoOBOSBackground : Indicator
    {
        private RSI rsi;
        private Brush redBrush;
        private Brush greenBrush;
        private int _lastOpacity = -1;
        private bool _inOverbought = false;
        private bool _inOversold   = false;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                 = @"Renko OB/OS background zones using RSI with hysteresis. Red = overbought exhaustion, Green = oversold flush.";
                Name                        = "RenkoOBOSBackground";
                Calculate                   = Calculate.OnBarClose;
                IsOverlay                   = true;
                DisplayInDataBox            = false;
                DrawOnPricePanel            = true;
                PaintPriceMarkers           = false;
                IsSuspendedWhileInactive    = true;

                RSIPeriod           = 14;
                RSISmooth           = 3;
                Overbought          = 79;
                OverboughtExit      = 62;
                Oversold            = 22;
                OversoldExit        = 35;
                Opacity             = 28;
            }
            else if (State == State.DataLoaded)
            {
                rsi = RSI(Close, RSIPeriod, RSISmooth);
                BuildBrushes();
            }
        }

        private void BuildBrushes()
        {
            byte alpha = (byte)Math.Round(Opacity * 2.55);
            redBrush   = new SolidColorBrush(Color.FromArgb(alpha, 120, 0, 0));
            redBrush.Freeze();
            greenBrush = new SolidColorBrush(Color.FromArgb(alpha, 0, 90, 0));
            greenBrush.Freeze();
            _lastOpacity = Opacity;
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < RSIPeriod) return;

            if (Oversold >= Overbought || OversoldExit >= OverboughtExit)
            {
                BackBrush = null;
                return;
            }

            if (Opacity != _lastOpacity)
                BuildBrushes();

            double rsival = rsi[0];

            // Overbought hysteresis
            if (!_inOverbought && rsival >= Overbought)
                _inOverbought = true;
            else if (_inOverbought && rsival < OverboughtExit)
                _inOverbought = false;

            // Oversold hysteresis
            if (!_inOversold && rsival <= Oversold)
                _inOversold = true;
            else if (_inOversold && rsival > OversoldExit)
                _inOversold = false;

            if (_inOverbought)
                BackBrush = redBrush;
            else if (_inOversold)
                BackBrush = greenBrush;
            else
                BackBrush = null;
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "RSI Period", Order = 1, GroupName = "Parameters")]
        public int RSIPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "RSI Smooth", Order = 2, GroupName = "Parameters")]
        public int RSISmooth { get; set; }

        [NinjaScriptProperty]
        [Range(60, 95)]
        [Display(Name = "Overbought Entry", Order = 3, GroupName = "Parameters")]
        public int Overbought { get; set; }

        [NinjaScriptProperty]
        [Range(45, 75)]
        [Display(Name = "Overbought Exit", Order = 4, GroupName = "Parameters")]
        public int OverboughtExit { get; set; }

        [NinjaScriptProperty]
        [Range(5, 40)]
        [Display(Name = "Oversold Entry", Order = 5, GroupName = "Parameters")]
        public int Oversold { get; set; }

        [NinjaScriptProperty]
        [Range(25, 55)]
        [Display(Name = "Oversold Exit", Order = 6, GroupName = "Parameters")]
        public int OversoldExit { get; set; }

        [NinjaScriptProperty]
        [Range(10, 60)]
        [Display(Name = "Opacity %", Order = 7, GroupName = "Parameters")]
        public int Opacity { get; set; }
        #endregion
    }
}