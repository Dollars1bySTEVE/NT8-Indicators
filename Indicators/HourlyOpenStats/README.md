# HourlyOpenStats Indicator

## Overview
The HourlyOpenStats indicator provides insights into the opening statistics of a trading instrument on an hourly basis. It calculates various key metrics that help traders to analyze market behavior better.

## Features
- **Hourly Opening Price**: Displays the price at which the asset opened every hour.
- **High and Low Prices**: Tracks the highest and lowest prices recorded within each hour.
- **Close Price Analysis**: Offers closing price data for a better understanding of market trends.
- **Customizable Timeframes**: Allows users to configure the indicator for specific trading sessions or timeframes.

## Installation
1. Clone the repository:
   ```bash
   git clone https://github.com/Dollars1bySTEVE/NT8-Indicators.git
   ```
2. Navigate to the `Indicators/HourlyOpenStats` directory.
3. Follow the setup instructions specific to your trading platform.

## Configuration
To configure the HourlyOpenStats indicator, modify the following settings:
- **TimeFrame**: Specify the desired timeframe (e.g., 1 hour).
- **ShowHighLow**: Set to `true` to visualize high and low prices.
- **Alerts**: Enable alerts for significant price changes or trends.

## Usage Examples
- To add the indicator to a chart, right-click on the chart and select "Add Indicator." Search for `HourlyOpenStats` and include it in your analysis.
- Example configuration settings:
  ```json
  {
      "TimeFrame": "1 Hour",
      "ShowHighLow": true,
      "Alerts": {
          "Enable": true,
          "Threshold": 10
      }
  }
  ```

## Troubleshooting
- **Issue**: Indicator not displaying correctly.
  - **Solution**: Ensure the correct timeframe is set and the indicator is applied to the correct chart.
- **Issue**: Alerts not working.
  - **Solution**: Check alert settings and ensure the trading platform supports alerts for this indicator.

## Conclusion
The HourlyOpenStats indicator is a powerful tool for traders looking to enhance their market analysis by understanding hourly price movements. For further assistance, please refer to the documentation or contact support.