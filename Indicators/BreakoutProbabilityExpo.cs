#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    [Gui.CategoryOrder("Settings", 1)]
    [Gui.CategoryOrder("Auto-Scale Settings", 2)]
    [Gui.CategoryOrder("Colors", 3)]
    [Gui.CategoryOrder("Style", 4)]
    [Gui.CategoryOrder("Alerts", 5)]
    public class BreakoutProbabilityExpo : Indicator
    {
        public enum BPEXStepMode { Auto, Manual }
        
        private int[,] totals;
        private double[,] vals;
        private const int MaxLevels = 5;
        private DateTime lastAlertTime = DateTime.MinValue;
        private Brush frozenUpColor, frozenDownColor, frozenLabelUpColor, frozenLabelDownColor;
        private ATR atrIndicator;
        private double calculatedStep;

        [NinjaScriptProperty]
        [Display(Name = "Step Mode", Order = 0, GroupName = "Settings")]
        public BPEXStepMode StepModeSelection { get; set; }

        [NinjaScriptProperty]
        [Range(0.001, double.MaxValue)]
        [Display(Name = "Manual Percentage Step", Order = 1, GroupName = "Settings")]
        public double ManualPercentageStep { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Number of Lines", Order = 2, GroupName = "Settings")]
        public int NumLines { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Hide 0% Lines", Order = 3, GroupName = "Settings")]
        public bool DisableZero { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Statistics Panel", Order = 4, GroupName = "Settings")]
        public bool ShowStats { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Calculate On Each Tick", Order = 5, GroupName = "Settings")]
        public bool CalcOnEachTick { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Bid/Ask Break Logic", Order = 6, GroupName = "Settings")]
        public bool UseBidAskBreaks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Line Length (bars)", Order = 7, GroupName = "Settings")]
        public int LineLengthBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Region Fill", Order = 8, GroupName = "Settings")]
        public bool FillRegions { get; set; }

        [NinjaScriptProperty]
        [Range(5, 100)]
        [Display(Name = "ATR Period", Order = 1, GroupName = "Auto-Scale Settings")]
        public int ATRPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, 2.0)]
        [Display(Name = "ATR Multiplier", Order = 2, GroupName = "Auto-Scale Settings")]
        public double ATRMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(0.001, 1.0)]
        [Display(Name = "Min Step %", Order = 3, GroupName = "Auto-Scale Settings")]
        public double MinStepPercent { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 5.0)]
        [Display(Name = "Max Step %", Order = 4, GroupName = "Auto-Scale Settings")]
        public double MaxStepPercent { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Calculated Step", Order = 5, GroupName = "Auto-Scale Settings")]
        public bool ShowCalculatedStep { get; set; }

        [XmlIgnore]
        [Display(Name = "Bullish Color", Order = 1, GroupName = "Colors")]
        public Brush UpColor { get; set; }
        [Browsable(false)]
        public string UpColorSerialize { get { return Serialize.BrushToString(UpColor); } set { UpColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Bearish Color", Order = 2, GroupName = "Colors")]
        public Brush DownColor { get; set; }
        [Browsable(false)]
        public string DownColorSerialize { get { return Serialize.BrushToString(DownColor); } set { DownColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Region Opacity %", Order = 3, GroupName = "Colors")]
        public int RegionOpacityPct { get; set; }

        [XmlIgnore]
        [Display(Name = "Label Color Up", Order = 4, GroupName = "Colors")]
        public Brush LabelUpColor { get; set; }
        [Browsable(false)]
        public string LabelUpColorSerialize { get { return Serialize.BrushToString(LabelUpColor); } set { LabelUpColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Label Color Down", Order = 5, GroupName = "Colors")]
        public Brush LabelDownColor { get; set; }
        [Browsable(false)]
        public string LabelDownColorSerialize { get { return Serialize.BrushToString(LabelDownColor); } set { LabelDownColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Line Width", Order = 1, GroupName = "Style")]
        public int LineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Line Dash Style", Order = 2, GroupName = "Style")]
        public DashStyleHelper LineDash { get; set; }

        [NinjaScriptProperty]
        [Range(6, 24)]
        [Display(Name = "Label Font Size", Order = 3, GroupName = "Style")]
        public int LabelFontSize { get; set; }

        [NinjaScriptProperty]
        [Range(8, 24)]
        [Display(Name = "Stats Font Size", Order = 4, GroupName = "Style")]
        public int StatsFontSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Alerts", Order = 0, GroupName = "Alerts")]
        public bool EnableAlerts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert Ticker", Order = 1, GroupName = "Alerts")]
        public bool AlertTicker { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert High/Low", Order = 2, GroupName = "Alerts")]
        public bool AlertHiLo { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert Bias", Order = 3, GroupName = "Alerts")]
        public bool AlertBias { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert Percentage", Order = 4, GroupName = "Alerts")]
        public bool AlertPerc { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert Sound", Order = 5, GroupName = "Alerts")]
        public string AlertSound { get; set; }

        [NinjaScriptProperty]
        [Range(0, 3600)]
        [Display(Name = "Alert Rearm Seconds", Order = 6, GroupName = "Alerts")]
        public int AlertRearmSeconds { get; set; }

        private string TagLineUp(int i) => "BPEX_LnUp" + i;
        private string TagLineDn(int i) => "BPEX_LnDn" + i;
        private string TagLblUp(int i) => "BPEX_LbUp" + i;
        private string TagLblDn(int i) => "BPEX_LbDn" + i;
        private string TagRegUp(int i) => "BPEX_RgUp" + i;
        private string TagRegDn(int i) => "BPEX_RgDn" + i;
        private const string TagStats = "BPEX_Stats";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Breakout Probability (Expo) - Port of TradingView indicator by Zeiierman";
                Name = "Breakout Probability (Expo)";
                IsOverlay = true;
                IsSuspendedWhileInactive = true;
                Calculate = Calculate.OnBarClose;
                StepModeSelection = BPEXStepMode.Auto;
                ManualPercentageStep = 0.1;
                NumLines = 5;
                DisableZero = true;
                ShowStats = true;
                CalcOnEachTick = false;
                UseBidAskBreaks = false;
                LineLengthBars = 25;
                FillRegions = true;
                ATRPeriod = 14;
                ATRMultiplier = 0.15;
                MinStepPercent = 0.02;
                MaxStepPercent = 1.0;
                ShowCalculatedStep = true;
                UpColor = Brushes.LimeGreen;
                DownColor = Brushes.Red;
                RegionOpacityPct = 12;
                LabelUpColor = Brushes.LimeGreen;
                LabelDownColor = Brushes.Red;
                LineWidth = 1;
                LineDash = DashStyleHelper.Dot;
                LabelFontSize = 10;
                StatsFontSize = 11;
                EnableAlerts = false;
                AlertTicker = true;
                AlertHiLo = true;
                AlertBias = true;
                AlertPerc = true;
                AlertSound = string.Empty;
                AlertRearmSeconds = 0;
            }
            else if (State == State.Configure)
            {
                if (CalcOnEachTick) Calculate = Calculate.OnEachTick;
            }
            else if (State == State.DataLoaded)
            {
                totals = new int[7, 4];
                vals = new double[5, 4];
                atrIndicator = ATR(ATRPeriod);
                frozenUpColor = CloneAndFreeze(UpColor);
                frozenDownColor = CloneAndFreeze(DownColor);
                frozenLabelUpColor = CloneAndFreeze(LabelUpColor);
                frozenLabelDownColor = CloneAndFreeze(LabelDownColor);
            }
            else if (State == State.Terminated) { ClearAllDrawings(); }
        }

        private double CalculateAutoStep()
        {
            if (CurrentBar < ATRPeriod || atrIndicator == null) return ManualPercentageStep;
            double atr = atrIndicator[0];
            double price = Close[0];
            if (price <= 0 || atr <= 0) return ManualPercentageStep;
            double atrPercent = (atr / price) * 100.0;
            double rawStep = atrPercent * ATRMultiplier;
            return Math.Round(Math.Max(MinStepPercent, Math.Min(MaxStepPercent, rawStep)), 4);
        }

        private double GetCurrentStep()
        {
            if (StepModeSelection == BPEXStepMode.Manual) return ManualPercentageStep;
            calculatedStep = CalculateAutoStep();
            return calculatedStep;
        }

        private Brush CloneAndFreeze(Brush b)
        {
            if (b == null) return Brushes.Transparent;
            Brush c = b.Clone();
            if (c.CanFreeze) c.Freeze();
            return c;
        }

        private Brush WithOpacity(Brush b, int pct)
        {
            if (b == null) return Brushes.Transparent;
            Brush c = b.Clone();
            c.Opacity = pct / 100.0;
            if (c.CanFreeze) c.Freeze();
            return c;
        }

        private void ClearAllDrawings()
        {
            for (int i = 0; i < MaxLevels; i++)
            {
                RemoveDrawObject(TagLineUp(i));
                RemoveDrawObject(TagLineDn(i));
                RemoveDrawObject(TagLblUp(i));
                RemoveDrawObject(TagLblDn(i));
                RemoveDrawObject(TagRegUp(i));
                RemoveDrawObject(TagRegDn(i));
            }
            RemoveDrawObject(TagStats);
        }

        private void UpdatePct(int r, int c, int num, int den)
        {
            vals[r, c] = den > 0 ? Math.Round(100.0 * num / den, 2) : 0.0;
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < ATRPeriod + 2) return;
            bool priorGreen = Close[1] > Open[1];
            bool priorRed = Close[1] < Open[1];
            double stepPercent = GetCurrentStep();
            double step = Close[0] * (stepPercent / 100.0);
            if (priorGreen) totals[5, 0]++;
            if (priorRed) totals[5, 1]++;
            double currHigh = High[0];
            double currLow = Low[0];
            if (UseBidAskBreaks && Calculate == Calculate.OnEachTick)
            {
                double bid = GetCurrentBid();
                double ask = GetCurrentAsk();
                if (!double.IsNaN(bid) && bid > 0) currLow = Math.Min(currLow, bid);
                if (!double.IsNaN(ask) && ask > 0) currHigh = Math.Max(currHigh, ask);
            }
            for (int i = 0; i < MaxLevels; i++)
            {
                double offset = step * i;
                bool brokeHi = currHigh >= High[1] + offset;
                bool brokeLo = currLow <= Low[1] - offset;
                if (priorGreen && brokeHi) { totals[i, 0]++; UpdatePct(i, 0, totals[i, 0], totals[5, 0]); }
                if (priorGreen && brokeLo) { totals[i, 1]++; UpdatePct(i, 1, totals[i, 1], totals[5, 0]); }
                if (priorRed && brokeHi) { totals[i, 2]++; UpdatePct(i, 2, totals[i, 2], totals[5, 1]); }
                if (priorRed && brokeLo) { totals[i, 3]++; UpdatePct(i, 3, totals[i, 3], totals[5, 1]); }
            }
            DrawLevels(priorGreen, step);
            UpdateBacktest(priorGreen, currHigh, currLow);
            if (EnableAlerts) DoAlerts(priorGreen);
            if (ShowStats) DrawStats();
        }

        private void DrawLevels(bool priorGreen, double step)
        {
            double priorHi = High[1];
            double priorLo = Low[1];
            int barsBack = Math.Min(LineLengthBars, CurrentBar);
            SimpleFont font = new SimpleFont("Arial", LabelFontSize);
            for (int i = 0; i < NumLines; i++)
            {
                double pctUp = priorGreen ? vals[i, 0] : vals[i, 2];
                double pctDn = priorGreen ? vals[i, 1] : vals[i, 3];
                double priceUp = priorHi + (step * i);
                double priceDn = priorLo - (step * i);
                bool hideUp = DisableZero && pctUp <= 0;
                bool hideDn = DisableZero && pctDn <= 0;
                if (!hideUp)
                {
                    Draw.Line(this, TagLineUp(i), false, barsBack, priceUp, 0, priceUp, frozenUpColor, LineDash, LineWidth);
                    Draw.Text(this, TagLblUp(i), false, pctUp.ToString("0.00") + "%", 0, priceUp, 0, frozenLabelUpColor, font, TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
                    if (FillRegions && i < NumLines - 1)
                    {
                        double nextPriceUp = priorHi + (step * (i + 1));
                        int opacity = Math.Max(5, RegionOpacityPct - (i * 2));
                        Brush fill = WithOpacity(UpColor, opacity);
                        Draw.Rectangle(this, TagRegUp(i), false, barsBack, nextPriceUp, 0, priceUp, Brushes.Transparent, fill, opacity);
                    }
                }
                else { RemoveDrawObject(TagLineUp(i)); RemoveDrawObject(TagLblUp(i)); RemoveDrawObject(TagRegUp(i)); }
                if (!hideDn)
                {
                    Draw.Line(this, TagLineDn(i), false, barsBack, priceDn, 0, priceDn, frozenDownColor, LineDash, LineWidth);
                    Draw.Text(this, TagLblDn(i), false, pctDn.ToString("0.00") + "%", 0, priceDn, 0, frozenLabelDownColor, font, TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
                    if (FillRegions && i < NumLines - 1)
                    {
                        double nextPriceDn = priorLo - (step * (i + 1));
                        int opacity = Math.Max(5, RegionOpacityPct - (i * 2));
                        Brush fill = WithOpacity(DownColor, opacity);
                        Draw.Rectangle(this, TagRegDn(i), false, barsBack, priceDn, 0, nextPriceDn, Brushes.Transparent, fill, opacity);
                    }
                }
                else { RemoveDrawObject(TagLineDn(i)); RemoveDrawObject(TagLblDn(i)); RemoveDrawObject(TagRegDn(i)); }
            }
        }

        private void DrawStats()
        {
            if (ChartControl == null) return;
            double w = totals[6, 0];
            double l = totals[6, 1];
            double total = w + l;
            double wr = total > 0 ? Math.Round(100.0 * w / total, 2) : 0;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("WIN: " + w);
            sb.AppendLine("LOSS: " + l);
            sb.AppendLine("Profitability: " + wr.ToString("0.00") + "%");
            if (ShowCalculatedStep)
            {
                sb.AppendLine("─────────────");
                sb.AppendLine("Mode: " + StepModeSelection);
                if (StepModeSelection == BPEXStepMode.Auto)
                {
                    string atrVal = (atrIndicator != null && CurrentBar >= ATRPeriod) ? atrIndicator[0].ToString("0.00") : "N/A";
                    sb.AppendLine("ATR(" + ATRPeriod + "): " + atrVal);
                    sb.AppendLine("Step: " + calculatedStep.ToString("0.0000") + "%");
                }
                else { sb.AppendLine("Step: " + ManualPercentageStep.ToString("0.0000") + "%"); }
            }
            Brush textBrush = ChartControl.Properties.ChartText ?? Brushes.White;
            SimpleFont sf = new SimpleFont("Arial", StatsFontSize);
            sf.Bold = true;
            Draw.TextFixed(this, TagStats, sb.ToString(), TextPosition.TopLeft, textBrush, sf, Brushes.Transparent, Brushes.Transparent, 0);
        }

        private void UpdateBacktest(bool priorGreen, double currHigh, double currLow)
        {
            double a1 = vals[0, 0], b1 = vals[0, 1];
            double a2 = vals[0, 2], b2 = vals[0, 3];
            bool biasHigh = priorGreen ? (a1 >= b1) : (a2 >= b2);
            double target = biasHigh ? High[1] : Low[1];
            if (CurrentBar > 1)
            {
                bool win = biasHigh ? (currHigh >= target) : (currLow <= target);
                if (win) totals[6, 0]++; else totals[6, 1]++;
            }
        }

        private void DoAlerts(bool priorGreen)
        {
            if (!AlertTicker && !AlertHiLo && !AlertBias && !AlertPerc) return;
            if (AlertRearmSeconds > 0 && (DateTime.Now - lastAlertTime).TotalSeconds < AlertRearmSeconds) return;
            double a1 = vals[0, 0], b1 = vals[0, 1], a2 = vals[0, 2], b2 = vals[0, 3];
            bool biasHigh = priorGreen ? (a1 >= b1) : (a2 >= b2);
            var parts = new List<string>();
            if (AlertTicker) parts.Add("Ticker: " + Instrument.FullName);
            if (AlertHiLo) parts.Add("Hi: " + High[1].ToString("0.00") + " | Lo: " + Low[1].ToString("0.00"));
            if (AlertBias) parts.Add("Bias: " + (biasHigh ? "BULL" : "BEAR"));
            if (AlertPerc)
            {
                double hi = priorGreen ? a1 : a2;
                double lo = priorGreen ? b1 : b2;
                parts.Add("Up: " + hi.ToString("0.00") + "% | Dn: " + lo.ToString("0.00") + "%");
            }
            string msg = string.Join(" | ", parts);
            string snd = string.IsNullOrEmpty(AlertSound) ? NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav" : AlertSound;
            Alert("BPEX", Priority.Medium, msg, snd, 10, Brushes.White, biasHigh ? Brushes.DarkGreen : Brushes.DarkRed);
            lastAlertTime = DateTime.Now;
        }
    }
}