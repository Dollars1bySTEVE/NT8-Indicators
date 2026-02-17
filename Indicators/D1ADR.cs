using System;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript.Strategies;
using NinjaTrader.NinjaScript.StrategyGenerator;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategy;
using NinjaTrader.NinjaScript.StrategyGenerator;
using NinjaTrader.NinjaScript.Strategy;
using NinjaTrader.NinjaScript.StrategyGenerator;

namespace NinjaTrader.NinjaScript.Strategies
{
    public class D1ADR : Indicator
    {
        // Private variables
        private double averageDailyRange;

        // NinjaScriptProperty definitions
        [NinjaScriptProperty]
        public double MyProperty { get; set; }

        protected override void OnStateChange()
        {
            // Code executed on state change
        }

        protected override void OnBarUpdate()
        {
            // Code executed on bar update
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            // Code executed to render the indicator
        }
    }
}