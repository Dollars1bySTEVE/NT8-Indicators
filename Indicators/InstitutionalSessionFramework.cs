#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

//This namespace holds Indicators in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.Indicators
{
	#region Enums (declared outside indicator class for auto-generated code compatibility)
	
	public enum SessionType
	{
		Asia,
		London,
		NewYork
	}

	public enum FeatureDisplay
	{
		ShowSessionBox,
		ShowPOC,
		ShowOpeningRange,
		ShowPivots,
		ShowVolumeProfile,
		ShowBOSCHoCH,
		ShowHistoricalSessions,
		ShowAlerts
	}

	public enum AlertTrigger
	{
		POCTouch,
		DOMCluster,
		BOSCHoCH,
		AllThree,
		None
	}

	public enum DayLabelOption
	{
		HiddenAll,
		CurrentDayOnly,
		Last7Days,
		Last14Days
	}

	#endregion

	public class InstitutionalSessionFramework : Indicator
	{
		#region Private Classes — Data Structures

		/// <summary>
		/// Represents the opening range (first N minutes) of a session.
		/// </summary>
		private class OpeningRange
		{
			public double OpenPrice { get; set; }
			public double HighPrice { get; set; }
			public double LowPrice { get; set; }
			public int StartBar { get; set; }
			public int EndBar { get; set; }
			public DateTime StartTime { get; set; }
			public bool IsComplete { get; set; }
		}

		/// <summary>
		/// Represents the Point of Control and related volume metrics for a session.
		/// </summary>
		private class POCData
		{
			public double POCPrice { get; set; }
			public double POCVolume { get; set; }
			public double UpVolume { get; set; }
			public double DownVolume { get; set; }
			public double TotalVolume { get; set; }
			public int BarIndex { get; set; }
			public bool IsCalculated { get; set; }
		}

		/// <summary>
		/// Core session data: OHLC, POC, opening range, volume metrics, DOM clusters.
		/// </summary>
		private class SessionData
		{
			public SessionType SessionType { get; set; }
			public DateTime SessionDate { get; set; }
			public double OpenPrice { get; set; }
			public double HighPrice { get; set; }
			public double LowPrice { get; set; }
			public double ClosePrice { get; set; }
			public int StartBar { get; set; }
			public int EndBar { get; set; }
			public DateTime StartTime { get; set; }
			public DateTime EndTime { get; set; }
			
			public POCData POC { get; set; }
			public OpeningRange OpeningRange { get; set; }
			
			public double TotalVolume { get; set; }
			public double UpVolume { get; set; }
			public double DownVolume { get; set; }
			
			public List<double> DOMClusters { get; set; } // DOM bid/ask depth at key levels
			
			public bool IsComplete { get; set; }
			public bool IsHistorical { get; set; }

			public SessionData()
			{
				POC = new POCData();
				OpeningRange = new OpeningRange();
				DOMClusters = new List<double>();
				IsComplete = false;
				IsHistorical = false;
			}
		}

		/// <summary>
		/// Historical rolling statistics for a session type over N days.
		/// </summary>
		private class SessionHistoricalStats
		{
			public double AvgPOC { get; set; }
			public double HighestPOC { get; set; }
			public double LowestPOC { get; set; }
			
			public double AvgOpeningRangeSize { get; set; }
			public double AvgSessionRange { get; set; }
			
			public double AvgVolume { get; set; }
			public double AvgUpVolume { get; set; }
			public double AvgDownVolume { get; set; }
			
			public int SampleCount { get; set; }
		}

		/// <summary>
		/// Represents a Break of Structure (BOS) or Change of Character (CHoCH) event.
		/// </summary>
		private class SmartMoneyEvent
		{
			public enum EventType { BOS, CHoCH }
			public enum EventDirection { Bullish, Bearish }
			
			public EventType Type { get; set; }
			public EventDirection Direction { get; set; }
			public double Price { get; set; }
			public int StartBar { get; set; }
			public int EndBar { get; set; }
			public DateTime EventTime { get; set; }
			public int ConfirmationScore { get; set; } // 0-3
		}

		#endregion

		#region Private Fields — Core State

		// ── Session tracking ─────────────────────────────────────────────
		private SessionIterator storedSession;
		private Dictionary<string, SessionData> activeSessions; // Key: "YYYYMMDD_SessionType"
		private Dictionary<string, List<SessionData>> historicalSessions; // 14-day rolling
		private SessionData currentSessionData;
		private SessionType lastDetectedSessionType = SessionType.Asia;
		private DateTime lastSessionChangeTime = DateTime.MinValue;

		// ── Opening range tracking ───────────────────────────────────────
		private DateTime openingRangeStartTime = DateTime.MinValue;
		private bool openingRangeComplete = false;

		// ── POC calculation state ────────────────────────────────────────
		private Dictionary<double, double> priceVolumeBins; // price level -> cumulative volume
		private double pocCalculationResolution = 0.25; // tick clustering resolution

		// ── Smart Money state ────────────────────────────────────────────
		private List<SmartMoneyEvent> detectedEvents;
		private List<double> swingHighs;
		private List<double> swingLows;

		// ── DOM/Level 2 state ────────────────────────────────────────────
		private DateTime lastDOMPoll = DateTime.MinValue;
		private const double DOMPollThrottleSeconds = 1.0;

		// ── Rendering state ─────────────────────────────────────────────
		private SharpDX.Direct2D1.SolidColorBrush dxAsiaSessionBrush;
		private SharpDX.Direct2D1.SolidColorBrush dxLondonSessionBrush;
		private SharpDX.Direct2D1.SolidColorBrush dxNYSessionBrush;
		private SharpDX.Direct2D1.SolidColorBrush dxPOCBrush;
		private SharpDX.Direct2D1.SolidColorBrush dxBullishBrush;
		private SharpDX.Direct2D1.SolidColorBrush dxBearishBrush;
		private SharpDX.Direct2D1.SolidColorBrush dxLabelBrush;
		private SharpDX.Direct2D1.SolidColorBrush dxPanelBgBrush;
		private SharpDX.DirectWrite.TextFormat dxTextFormat;
		private bool dxResourcesCreated = false;

		#endregion

		#region Properties — 01. Session Configuration

		[NinjaScriptProperty]
		[Display(Name = "Session: Asia Enabled", Order = 1, GroupName = "01. Session Configuration")]
		public bool EnableAsiaSession { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Session: London Enabled", Order = 2, GroupName = "01. Session Configuration")]
		public bool EnableLondonSession { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Session: New York Enabled", Order = 3, GroupName = "01. Session Configuration")]
		public bool EnableNYSession { get; set; }

		#endregion

		#region Properties — 02. Opening Range

		[NinjaScriptProperty]
		[Range(30, 3600)]
		[Display(Name = "Opening Range Duration (seconds)", Order = 1, GroupName = "02. Opening Range")]
		public int OpeningRangeDurationSeconds { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Opening Range Box", Order = 2, GroupName = "02. Opening Range")]
		public bool ShowOpeningRangeBox { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Opening Range Box Opacity %", Order = 3, GroupName = "02. Opening Range")]
		[Range(5, 50)]
		public int OpeningRangeOpacity { get; set; }

		#endregion

		#region Properties — 03. POC Configuration

		[NinjaScriptProperty]
		[Display(Name = "Show POC Line", Order = 1, GroupName = "03. POC Configuration")]
		public bool ShowPOCLine { get; set; }

		[NinjaScriptProperty]
		[Range(0.01, 1.0)]
		[Display(Name = "POC Clustering Resolution (ticks)", Order = 2, GroupName = "03. POC Configuration")]
		public double POCResolutionTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show POC Label", Order = 3, GroupName = "03. POC Configuration")]
		public bool ShowPOCLabel { get; set; }

		#endregion

		#region Properties — 04. Pivots

		[NinjaScriptProperty]
		[Display(Name = "Show Pivot Points", Order = 1, GroupName = "04. Pivots")]
		public bool ShowPivotPoints { get; set; }

		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "Pivot Line Width", Order = 2, GroupName = "04. Pivots")]
		public int PivotLineWidth { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Pivot Line Style", Order = 3, GroupName = "04. Pivots")]
		public DashStyleHelper PivotLineStyle { get; set; }

		#endregion

		#region Properties — 05. Smart Money (BOS/CHoCH)

		[NinjaScriptProperty]
		[Display(Name = "Show BOS/CHoCH", Order = 1, GroupName = "05. Smart Money")]
		public bool ShowSmartMoneyEvents { get; set; }

		[NinjaScriptProperty]
		[Range(1, 20)]
		[Display(Name = "Swing Strength (bars)", Order = 2, GroupName = "05. Smart Money")]
		public int SwingStrength { get; set; }

		[NinjaScriptProperty]
		[Range(1, 10)]
		[Display(Name = "Confirmation Bars", Order = 3, GroupName = "05. Smart Money")]
		public int ConfirmationBars { get; set; }

		#endregion

		#region Properties — 06. Historical Sessions

		[NinjaScriptProperty]
		[Display(Name = "Show Historical Sessions", Order = 1, GroupName = "06. Historical Sessions")]
		public bool ShowHistoricalSessions { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Historical Day Labels", Order = 2, GroupName = "06. Historical Sessions")]
		public DayLabelOption HistoricalDayLabels { get; set; }

		[NinjaScriptProperty]
		[Range(1, 14)]
		[Display(Name = "Lookback Days", Order = 3, GroupName = "06. Historical Sessions")]
		public int LookbackDays { get; set; }

		[NinjaScriptProperty]
		[Range(5, 50)]
		[Display(Name = "Historical Session Opacity %", Order = 4, GroupName = "06. Historical Sessions")]
		public int HistoricalSessionOpacity { get; set; }

		#endregion

		#region Properties — 07. Alerts & Notifications

		[NinjaScriptProperty]
		[Display(Name = "Alert Trigger", Order = 1, GroupName = "07. Alerts & Notifications")]
		public AlertTrigger SelectedAlertTrigger { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Enable Audio Alerts", Order = 2, GroupName = "07. Alerts & Notifications")]
		public bool EnableAudioAlerts { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Sound File Name", Order = 3, GroupName = "07. Alerts & Notifications")]
		public string AlertSoundFile { get; set; }

		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name = "Alert Cooldown (bars)", Order = 4, GroupName = "07. Alerts & Notifications")]
		public int AlertCooldownBars { get; set; }

		#endregion

		#region Properties — 08. Appearance — Colors

		[XmlIgnore]
		[Display(Name = "Asia Session Color", Order = 1, GroupName = "08. Appearance — Colors")]
		public Brush AsiaSessionColor { get; set; }
		[Browsable(false)]
		public string AsiaSessionColorSerialize
		{
			get { return Serialize.BrushToString(AsiaSessionColor); }
			set { AsiaSessionColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "London Session Color", Order = 2, GroupName = "08. Appearance — Colors")]
		public Brush LondonSessionColor { get; set; }
		[Browsable(false)]
		public string LondonSessionColorSerialize
		{
			get { return Serialize.BrushToString(LondonSessionColor); }
			set { LondonSessionColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "New York Session Color", Order = 3, GroupName = "08. Appearance — Colors")]
		public Brush NYSessionColor { get; set; }
		[Browsable(false)]
		public string NYSessionColorSerialize
		{
			get { return Serialize.BrushToString(NYSessionColor); }
			set { NYSessionColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "POC Line Color", Order = 4, GroupName = "08. Appearance — Colors")]
		public Brush POCLineColor { get; set; }
		[Browsable(false)]
		public string POCLineColorSerialize
		{
			get { return Serialize.BrushToString(POCLineColor); }
			set { POCLineColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Bullish Signal Color", Order = 5, GroupName = "08. Appearance — Colors")]
		public Brush BullishSignalColor { get; set; }
		[Browsable(false)]
		public string BullishSignalColorSerialize
		{
			get { return Serialize.BrushToString(BullishSignalColor); }
			set { BullishSignalColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Bearish Signal Color", Order = 6, GroupName = "08. Appearance — Colors")]
		public Brush BearishSignalColor { get; set; }
		[Browsable(false)]
		public string BearishSignalColorSerialize
		{
			get { return Serialize.BrushToString(BearishSignalColor); }
			set { BearishSignalColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Label Text Color", Order = 7, GroupName = "08. Appearance — Colors")]
		public Brush LabelTextColor { get; set; }
		[Browsable(false)]
		public string LabelTextColorSerialize
		{
			get { return Serialize.BrushToString(LabelTextColor); }
			set { LabelTextColor = Serialize.StringToBrush(value); }
		}

		#endregion

		#region Properties — 09. Appearance — Fonts & Labels

		[NinjaScriptProperty]
		[Display(Name = "Label Font Size", Order = 1, GroupName = "09. Appearance — Fonts & Labels")]
		[Range(8, 16)]
		public int LabelFontSize { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Session Labels", Order = 2, GroupName = "09. Appearance — Fonts & Labels")]
		public bool ShowSessionLabels { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Pivot Labels", Order = 3, GroupName = "09. Appearance — Fonts & Labels")]
		public bool ShowPivotLabels { get; set; }

		#endregion

		#region Properties — 10. Dashboard

		[NinjaScriptProperty]
		[Display(Name = "Show Dashboard", Order = 1, GroupName = "10. Dashboard")]
		public bool ShowDashboard { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Dashboard Position", Order = 2, GroupName = "10. Dashboard")]
		public TextPosition DashboardPosition { get; set; }

		[NinjaScriptProperty]
		[Range(8, 20)]
		[Display(Name = "Dashboard Font Size", Order = 3, GroupName = "10. Dashboard")]
		public int DashboardFontSize { get; set; }

		[XmlIgnore]
		[Display(Name = "Dashboard Text Color", Order = 4, GroupName = "10. Dashboard")]
		public Brush DashboardTextColor { get; set; }
		[Browsable(false)]
		public string DashboardTextColorSerialize
		{
			get { return Serialize.BrushToString(DashboardTextColor); }
			set { DashboardTextColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Dashboard Background Color", Order = 5, GroupName = "10. Dashboard")]
		public Brush DashboardBackgroundColor { get; set; }
		[Browsable(false)]
		public string DashboardBackgroundColorSerialize
		{
			get { return Serialize.BrushToString(DashboardBackgroundColor); }
			set { DashboardBackgroundColor = Serialize.StringToBrush(value); }
		}

		[NinjaScriptProperty]
		[Range(50, 100)]
		[Display(Name = "Dashboard Opacity %", Order = 6, GroupName = "10. Dashboard")]
		public int DashboardOpacity { get; set; }

		#endregion

		#region OnStateChange

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Institutional-grade multi-session analysis framework combining session OHLC, " +
					"Point of Control (POC), opening range, pivot points, Smart Money (BOS/CHoCH) detection, " +
					"14-day historical overlay, and Level 2 DOM integration. Full user customization of colors, " +
					"styles, labels, and alert triggers.";
				Name = "InstitutionalSessionFramework";
				Calculate = Calculate.OnBarClose;
				IsOverlay = true;
				DisplayInDataBox = true;
				DrawOnPricePanel = true;
				PaintPriceMarkers = false;
				IsSuspendedWhileInactive = true;

				// ── 01. Session Configuration ────────────────────────────────
				EnableAsiaSession = true;
				EnableLondonSession = true;
				EnableNYSession = true;

				// ── 02. Opening Range ────────────────────────────────────────
				OpeningRangeDurationSeconds = 1800; // 30 minutes default
				ShowOpeningRangeBox = true;
				OpeningRangeOpacity = 20;

				// ── 03. POC Configuration ────────────────────────────────────
				ShowPOCLine = true;
				POCResolutionTicks = 0.25;
				ShowPOCLabel = true;

				// ── 04. Pivots ──────────────────────────────────────────────
				ShowPivotPoints = true;
				PivotLineWidth = 1;
				PivotLineStyle = DashStyleHelper.Dot;

				// ── 05. Smart Money ─────────────────────────────────────────
				ShowSmartMoneyEvents = true;
				SwingStrength = 3;
				ConfirmationBars = 1;

				// ── 06. Historical Sessions ─────────────────────────────────
				ShowHistoricalSessions = true;
				HistoricalDayLabels = DayLabelOption.Last7Days;
				LookbackDays = 14;
				HistoricalSessionOpacity = 15;

				// ── 07. Alerts & Notifications ──────────────────────────────
				SelectedAlertTrigger = AlertTrigger.AllThree;
				EnableAudioAlerts = true;
				AlertSoundFile = "Alert1.wav";
				AlertCooldownBars = 20;

				// ── 08. Appearance — Colors ─────────────────────────────────
				AsiaSessionColor = Brushes.Purple;
				LondonSessionColor = Brushes.Orange;
				NYSessionColor = Brushes.Green;
				POCLineColor = Brushes.Cyan;
				BullishSignalColor = Brushes.Lime;
				BearishSignalColor = Brushes.Red;
				LabelTextColor = Brushes.White;

				// ── 09. Appearance — Fonts & Labels ─────────────────────────
				LabelFontSize = 10;
				ShowSessionLabels = true;
				ShowPivotLabels = true;

				// ── 10. Dashboard ────────────────────────────────────────────
				ShowDashboard = true;
				DashboardPosition = TextPosition.TopLeft;
				DashboardFontSize = 11;
				DashboardTextColor = Brushes.GreenYellow;
				DashboardBackgroundColor = Brushes.Black;
				DashboardOpacity = 85;
			}
			else if (State == State.Configure)
			{
				// Future: Add secondary data series if needed (e.g., daily bars for pivot calculation)
			}
			else if (State == State.DataLoaded)
			{
				// Initialize core data structures
				activeSessions = new Dictionary<string, SessionData>();
				historicalSessions = new Dictionary<string, List<SessionData>>();
				detectedEvents = new List<SmartMoneyEvent>();
				swingHighs = new List<double>();
				swingLows = new List<double>();
				priceVolumeBins = new Dictionary<double, double>();

				// Initialize session iterator for boundary detection
				storedSession = new SessionIterator(Bars);

				// Initialize price volume bins for POC calculation
				pocCalculationResolution = POCResolutionTicks * TickSize;
			}
			else if (State == State.Terminated)
			{
				DisposeSharpDXResources();
			}
		}

		#endregion

		#region OnBarUpdate

		protected override void OnBarUpdate()
		{
			if (CurrentBar < 2) return;

			// Placeholder: Session detection will go here
			// Placeholder: POC calculation will go here
			// Placeholder: Smart Money detection will go here
			// Placeholder: DOM polling will go here
			// Placeholder: Historical tracking will go here
		}

		#endregion

		#region SharpDX Resource Management

		private void CreateSharpDXResources(SharpDX.Direct2D1.RenderTarget renderTarget)
		{
			if (dxResourcesCreated) return;

			try
			{
				dxAsiaSessionBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, ToColor4(AsiaSessionColor, OpeningRangeOpacity / 100f));
				dxLondonSessionBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, ToColor4(LondonSessionColor, OpeningRangeOpacity / 100f));
				dxNYSessionBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, ToColor4(NYSessionColor, OpeningRangeOpacity / 100f));
				dxPOCBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, ToColor4(POCLineColor, 1f));
				dxBullishBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, ToColor4(BullishSignalColor, 1f));
				dxBearishBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, ToColor4(BearishSignalColor, 1f));
				dxLabelBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, ToColor4(LabelTextColor, 1f));
				dxPanelBgBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, new SharpDX.Color4(0f, 0f, 0f, DashboardOpacity / 100f));

				dxTextFormat = new SharpDX.DirectWrite.TextFormat(
					NinjaTrader.Core.Globals.DirectWriteFactory,
					"Arial",
					SharpDX.DirectWrite.FontWeight.Normal,
					SharpDX.DirectWrite.FontStyle.Normal,
					SharpDX.DirectWrite.FontStretch.Normal,
					LabelFontSize);

				dxResourcesCreated = true;
			}
			catch (Exception ex)
			{
				Log("Error creating SharpDX resources: " + ex.Message, LogLevel.Error);
			}
		}

		private void DisposeSharpDXResources()
		{
			if (dxAsiaSessionBrush != null) { dxAsiaSessionBrush.Dispose(); dxAsiaSessionBrush = null; }
			if (dxLondonSessionBrush != null) { dxLondonSessionBrush.Dispose(); dxLondonSessionBrush = null; }
			if (dxNYSessionBrush != null) { dxNYSessionBrush.Dispose(); dxNYSessionBrush = null; }
			if (dxPOCBrush != null) { dxPOCBrush.Dispose(); dxPOCBrush = null; }
			if (dxBullishBrush != null) { dxBullishBrush.Dispose(); dxBullishBrush = null; }
			if (dxBearishBrush != null) { dxBearishBrush.Dispose(); dxBearishBrush = null; }
			if (dxLabelBrush != null) { dxLabelBrush.Dispose(); dxLabelBrush = null; }
			if (dxPanelBgBrush != null) { dxPanelBgBrush.Dispose(); dxPanelBgBrush = null; }
			if (dxTextFormat != null) { dxTextFormat.Dispose(); dxTextFormat = null; }
			dxResourcesCreated = false;
		}

		public override void OnRenderTargetChanged()
		{
			DisposeSharpDXResources();
		}

		#endregion

		#region Rendering (Placeholder)

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);

			// Rendering logic will be implemented in Phase 3
			// - Session boxes
			// - POC lines + labels
			// - Pivot lines + labels
			// - BOS/CHoCH markers
			// - Historical session overlays
			// - Dashboard panel
		}

		#endregion

		#region Helpers

		private SharpDX.Color4 ToColor4(System.Windows.Media.Brush wpfBrush, float alpha)
		{
			System.Windows.Media.Color c = ((System.Windows.Media.SolidColorBrush)wpfBrush).Color;
			return new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, alpha);
		}

		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private InstitutionalSessionFramework[] cacheInstitutionalSessionFramework;
		
		public InstitutionalSessionFramework InstitutionalSessionFramework()
		{
			return InstitutionalSessionFramework(Input);
		}

		public InstitutionalSessionFramework InstitutionalSessionFramework(ISeries<double> input)
		{
			if (cacheInstitutionalSessionFramework != null)
				for (int idx = 0; idx < cacheInstitutionalSessionFramework.Length; idx++)
					if (cacheInstitutionalSessionFramework[idx] != null && cacheInstitutionalSessionFramework[idx].EqualsInput(input))
						return cacheInstitutionalSessionFramework[idx];
			
			return CacheIndicator<InstitutionalSessionFramework>(new InstitutionalSessionFramework(), input, ref cacheInstitutionalSessionFramework);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.InstitutionalSessionFramework InstitutionalSessionFramework()
		{
			return indicator.InstitutionalSessionFramework(Input);
		}

		public Indicators.InstitutionalSessionFramework InstitutionalSessionFramework(ISeries<double> input)
		{
			return indicator.InstitutionalSessionFramework(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.InstitutionalSessionFramework InstitutionalSessionFramework()
		{
			return indicator.InstitutionalSessionFramework(Input);
		}

		public Indicators.InstitutionalSessionFramework InstitutionalSessionFramework(ISeries<double> input)
		{
			return indicator.InstitutionalSessionFramework(input);
		}
	}
}

#endregion
