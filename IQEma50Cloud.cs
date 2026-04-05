        protected override void OnBarUpdate() {
            if (CurrentBar < 100) return;

            double ema    = EMA(50)[0];
            double stdDev = StdDev(Close, 100)[0] / 4.0;

            // EMA50 plot — assign NaN to hide when ShowEma50 is off
            Values[0][0] = ShowEma50 ? ema : double.NaN;

            // Cloud band plots (used as data sources for Draw.Region)
            Values[1][0] = ema + stdDev;
            Values[2][0] = ema - stdDev;

            // Cloud fill between upper and lower band plots
            if (ShowCloud) {
                // Draw region from current bar back through all bars with valid data.
                // Cap at 254 to stay within the TwoHundredFiftySix (0-255 offset) window.
                int barsBack       = Math.Max(0, Math.Min(CurrentBar - 100, 254));
                Brush outlineBrush = ShowCloudBorder ? Ema50Color : Brushes.Transparent;
                Draw.Region(this, "EmaCloud", 0, barsBack,
                    Values[1], Values[2],
                    outlineBrush, CloudFillColor, CloudFillOpacity);
            } else {
                RemoveDrawObject("EmaCloud");
            }

            // Label at the right edge of the EMA line — small and subtle
            if (ShowLabel && ShowEma50) 
                Draw.Text(this, "Ema50Label", false, "50", 0, ema, 8, Ema50Color, 
                    new NinjaTrader.Gui.Tools.SimpleFont("Arial", 9), TextAlignment.Left, 
                    Brushes.Transparent, Brushes.Transparent, 0);
            else
                RemoveDrawObject("Ema50Label");
        }