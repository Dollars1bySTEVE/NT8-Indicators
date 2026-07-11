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
		private DateTime currentSessionStartTime = DateTime.MinValue;

		// ── Opening range tracking ───────────────────────────────────────
		private DateTime openingRangeStartTime = DateTime.MinValue;
		private bool openingRangeComplete = false;
		private int openingRangeStartBar = -1;

		// ── POC calculation state ────────────────────────────────────────
		private Dictionary<double, double> priceVolumeBins; // price level -> cumulative volume
		private double pocCalculationResolution = 0.25; // tick clustering resolution

		// ── Smart Money state ────────────────────────────────────────────
		private List<SmartMoneyEvent> detectedEvents;
		private List<double> swingHighs;
		private List<double> swingLows;
		private int lastSwingDetectionBar = -1;

		// ── DOM/Level 2 state ────────────────────────────────────────────
		private DateTime lastDOMPoll = DateTime.MinValue;
		private const double DOMPollThrottleSeconds = 1.0;

		// ── Alert state ──────────────────────────────────────────────────
		private int lastAlertBar = -999;

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

			// ── Step 1: Detect session boundaries ─────────────────────────
			DetectSessionBoundaries();

			// ── Step 2: Update current session OHLC ──────────────────────
			UpdateCurrentSessionOHLC();

			// ── Step 3: Track opening range ──────────────────────────────
			TrackOpeningRange();

			// ── Step 4: Calculate POC ───────────────────────────────────
			CalculatePOC();

			// ── Step 5: Detect Smart Money events (BOS/CHoCH) ────────────
			DetectSmartMoneyEvents();

			// ── Step 6: Poll Level 2 DOM (throttled) ─────────────────────
			PollDOMClusters();

			// ── Step 7: Manage historical rolling data ───────────────────
			ManageHistoricalSessions();
		}

		#endregion

		#region Core Logic — Session Detection

		/// <summary>
		/// Detects session boundaries using SessionIterator and maintains active session data.
		/// Sessions: Asia (5 PM - 2 AM), London (3 AM - 12 PM), NY (9:30 AM - 4 PM)
		/// </summary>
		private void DetectSessionBoundaries()
		{
			DateTime currentTime = Time[0];
			TimeSpan currentTimeOfDay = currentTime.TimeOfDay;

			// Determine which session we're currently in based on time of day (ET)
			SessionType detectedSession = GetCurrentSession(currentTimeOfDay);

			// Check if session has changed
			if (lastDetectedSessionType != detectedSession || currentSessionStartTime == DateTime.MinValue)
			{
				// Session transition detected or first initialization
				if (currentSessionData != null && !currentSessionData.IsComplete)
				{
					// Finalize previous session
					currentSessionData.IsComplete = true;
					currentSessionData.EndTime = Time[1];
					currentSessionData.EndBar = CurrentBar - 1;

					// Store in active sessions
					string sessionKey = BuildSessionKey(currentSessionData.SessionDate, currentSessionData.SessionType);
					if (!activeSessions.ContainsKey(sessionKey))
						activeSessions[sessionKey] = currentSessionData;

					// Add to historical tracking
					AddToHistoricalSessions(currentSessionData);
				}

				// Create new session
				currentSessionData = new SessionData
				{
					SessionType = detectedSession,
					SessionDate = currentTime.Date,
					StartTime = currentTime,
					StartBar = CurrentBar,
					OpenPrice = Open[0],
					HighPrice = High[0],
					LowPrice = Low[0],
					IsComplete = false,
					IsHistorical = false
				};

				lastDetectedSessionType = detectedSession;
				currentSessionStartTime = currentTime;
				openingRangeStartTime = currentTime;
				openingRangeStartBar = CurrentBar;
				openingRangeComplete = false;
				priceVolumeBins.Clear();
			}
		}

		/// <summary>
		/// Determines the current session based on time of day (Eastern Time).
		/// </summary>
		private SessionType GetCurrentSession(TimeSpan timeOfDay)
		{
			// Globex sessions in ET:
			// Asia:   5 PM (17:00) - 2 AM (02:00) next day
			// London: 3 AM (03:00) - 12 PM (12:00)
			// NY:     9:30 AM (09:30) - 4 PM (16:00)

			if (timeOfDay >= new TimeSpan(17, 0, 0) || timeOfDay < new TimeSpan(2, 0, 0))
				return SessionType.Asia;
			else if (timeOfDay >= new TimeSpan(3, 0, 0) && timeOfDay < new TimeSpan(12, 0, 0))
				return SessionType.London;
			else if (timeOfDay >= new TimeSpan(9, 30, 0) && timeOfDay < new TimeSpan(16, 0, 0))
				return SessionType.NewYork;
			else
				// Gap time: 2 AM - 3 AM or 12 PM - 9:30 AM or 4 PM - 5 PM
				return lastDetectedSessionType; // Return last known session
		}

		#endregion

		#region Core Logic — Update Session OHLC

		/// <summary>
		/// Updates the current session's High, Low, Close and accumulates volume metrics.
		/// </summary>
		private void UpdateCurrentSessionOHLC()
		{
			if (currentSessionData == null) return;

			currentSessionData.HighPrice = Math.Max(currentSessionData.HighPrice, High[0]);
			currentSessionData.LowPrice = Math.Min(currentSessionData.LowPrice, Low[0]);
			currentSessionData.ClosePrice = Close[0];

			currentSessionData.TotalVolume += Volume[0];
			if (Close[0] >= Open[0])
				currentSessionData.UpVolume += Volume[0];
			else
				currentSessionData.DownVolume += Volume[0];
		}

		#endregion

		#region Core Logic — Opening Range Tracking

		/// <summary>
		/// Tracks the opening range (first N minutes) of the current session.
		/// </summary>
		private void TrackOpeningRange()
		{
			if (currentSessionData == null || openingRangeComplete) return;

			OpeningRange orng = currentSessionData.OpeningRange;

			// Initialize opening range if not set
			if (orng.StartBar == 0)
			{
				orng.StartBar = openingRangeStartBar;
				orng.StartTime = openingRangeStartTime;
				orng.OpenPrice = Open[0];
				orng.HighPrice = High[0];
				orng.LowPrice = Low[0];
			}

			// Update high/low
			orng.HighPrice = Math.Max(orng.HighPrice, High[0]);
			orng.LowPrice = Math.Min(orng.LowPrice, Low[0]);

			// Check if opening range duration is complete
			double secondsElapsed = (Time[0] - openingRangeStartTime).TotalSeconds;
			if (secondsElapsed >= OpeningRangeDurationSeconds)
			{
				orng.EndBar = CurrentBar;
				orng.IsComplete = true;
				openingRangeComplete = true;
			}
		}

		#endregion

		#region Core Logic — POC Calculation

		/// <summary>
		/// Calculates Point of Control using volume-weighted price clustering.
		/// Standard institutional method: bin prices by resolution, find highest volume level.
		/// </summary>
		private void CalculatePOC()
		{
			if (currentSessionData == null) return;

			// Bin the current bar's price/volume into clusters
			double binPrice = Math.Round(Close[0] / pocCalculationResolution) * pocCalculationResolution;

			if (!priceVolumeBins.ContainsKey(binPrice))
				priceVolumeBins[binPrice] = 0;

			priceVolumeBins[binPrice] += Volume[0];

			// Find the price level with highest cumulative volume (POC)
			double pocPrice = binPrice;
			double maxVolume = priceVolumeBins[binPrice];

			foreach (var kvp in priceVolumeBins)
			{
				if (kvp.Value > maxVolume)
				{
					maxVolume = kvp.Value;
					pocPrice = kvp.Key;
				}
			}

			// Update current session POC
			currentSessionData.POC.POCPrice = pocPrice;
			currentSessionData.POC.POCVolume = maxVolume;
			currentSessionData.POC.IsCalculated = true;
			currentSessionData.POC.BarIndex = CurrentBar;

			// Clean up old bins (keep only recent ones to prevent memory bloat)
			if (priceVolumeBins.Count > 1000)
			{
				var oldestEntries = priceVolumeBins.OrderBy(x => x.Key).Take(100).Select(x => x.Key).ToList();
				foreach (var key in oldestEntries)
					priceVolumeBins.Remove(key);
			}
		}

		#endregion

		#region Core Logic — Smart Money (BOS/CHoCH) Detection

		/// <summary>
		/// Detects swing points and identifies Break of Structure (BOS) and Change of Character (CHoCH) events.
		/// </summary>
		private void DetectSmartMoneyEvents()
		{
			if (!ShowSmartMoneyEvents || CurrentBar < SwingStrength * 2 + 1) return;

			// ── Detect swing points (highs and lows) ─────────────────────
			DetectSwingPoints();

			// ── Analyze swing sequences for BOS/CHoCH ────────────────────
			if (swingHighs.Count >= 2 || swingLows.Count >= 2)
			{
				AnalyzeStructureBreaks();
			}
		}

		/// <summary>
		/// Identifies swing highs and lows based on SwingStrength parameter.
		/// </summary>
		private void DetectSwingPoints()
		{
			if (CurrentBar <= SwingStrength)
				return;

			int lookbackBar = CurrentBar - SwingStrength;

			// Check for swing high
			double potentialSwingHigh = High[SwingStrength];
			bool isSwingHigh = true;

			for (int i = 1; i <= SwingStrength; i++)
			{
				if (High[i] >= potentialSwingHigh || High[SwingStrength - i] >= potentialSwingHigh)
				{
					isSwingHigh = false;
					break;
				}
			}

			if (isSwingHigh && (swingHighs.Count == 0 || Math.Abs(swingHighs.Last() - potentialSwingHigh) > TickSize))
			{
				swingHighs.Add(potentialSwingHigh);
				lastSwingDetectionBar = CurrentBar;
			}

			// Check for swing low
			double potentialSwingLow = Low[SwingStrength];
			bool isSwingLow = true;

			for (int i = 1; i <= SwingStrength; i++)
			{
				if (Low[i] <= potentialSwingLow || Low[SwingStrength - i] <= potentialSwingLow)
				{
					isSwingLow = false;
					break;
				}
			}

			if (isSwingLow && (swingLows.Count == 0 || Math.Abs(swingLows.Last() - potentialSwingLow) > TickSize))
			{
				swingLows.Add(potentialSwingLow);
				lastSwingDetectionBar = CurrentBar;
			}

			// Prune old swings (keep last 20)
			if (swingHighs.Count > 20) swingHighs.RemoveAt(0);
			if (swingLows.Count > 20) swingLows.RemoveAt(0);
		}

		/// <summary>
		/// Analyzes swing sequences to detect BOS (Break of Structure) and CHoCH (Change of Character).
		/// </summary>
		private void AnalyzeStructureBreaks()
		{
			// Simplified BOS/CHoCH detection:
			// BOS = Price breaks prior swing high/low in same trend direction
			// CHoCH = Price breaks prior swing + trend reverses

			if (swingHighs.Count < 2 && swingLows.Count < 2) return;

			double lastHighSwing = swingHighs.Count >= 1 ? swingHighs.Last() : 0;
			double lastLowSwing = swingLows.Count >= 1 ? swingLows.Last() : 0;

			// Check for bullish BOS: price closes above last swing high with confirmation
			bool bullishBOS = Close[0] > lastHighSwing && Close[1] <= lastHighSwing;
			if (bullishBOS && HasConfirmedBreakAbove(lastHighSwing))
			{
				detectedEvents.Add(new SmartMoneyEvent
				{
					Type = SmartMoneyEvent.EventType.BOS,
					Direction = SmartMoneyEvent.EventDirection.Bullish,
					Price = lastHighSwing,
					StartBar = CurrentBar - ConfirmationBars,
					EndBar = CurrentBar,
					EventTime = Time[0],
					ConfirmationScore = 2
				});
			}

			// Check for bearish BOS: price closes below last swing low with confirmation
			bool bearishBOS = Close[0] < lastLowSwing && Close[1] >= lastLowSwing;
			if (bearishBOS && HasConfirmedBreakBelow(lastLowSwing))
			{
				detectedEvents.Add(new SmartMoneyEvent
				{
					Type = SmartMoneyEvent.EventType.BOS,
					Direction = SmartMoneyEvent.EventDirection.Bearish,
					Price = lastLowSwing,
					StartBar = CurrentBar - ConfirmationBars,
					EndBar = CurrentBar,
					EventTime = Time[0],
					ConfirmationScore = 2
				});
			}

			// Prune old events (keep last 50)
			if (detectedEvents.Count > 50)
				detectedEvents.RemoveAt(0);
		}

		private bool HasConfirmedBreakAbove(double price)
		{
			int consecutive = 0;
			for (int i = 0; i < ConfirmationBars && i < CurrentBar; i++)
			{
				if (Close[i] > price) consecutive++;
			}
			return consecutive >= ConfirmationBars;
		}

		private bool HasConfirmedBreakBelow(double price)
		{
			int consecutive = 0;
			for (int i = 0; i < ConfirmationBars && i < CurrentBar; i++)
			{
				if (Close[i] < price) consecutive++;
			}
			return consecutive >= ConfirmationBars;
		}

		#endregion

		#region Core Logic — DOM Polling (Level 2)

		/// <summary>
		/// Throttled polling of Level 2 DOM data at key price levels.
		/// Placeholder: Real implementation requires OnMarketDepth() subscription.
		/// </summary>
		private void PollDOMClusters()
		{
			if (!EnableAudioAlerts || (DateTime.Now - lastDOMPoll).TotalSeconds < DOMPollThrottleSeconds)
				return;

			lastDOMPoll = DateTime.Now;

			// Placeholder: DOM integration would go here
			// Real implementation:
			// - Subscribe to OnMarketDepth() in the indicator
			// - Cache bid/ask depth at POC, opening range boundaries, etc.
			// - Use for confirmation scoring
		}

		#endregion

		#region Core Logic — Historical Sessions Management

		/// <summary>
		/// Manages rolling 14-day historical session data storage.
		/// </summary>
		private void ManageHistoricalSessions()
		{
			// Historical data is added when sessions complete (in DetectSessionBoundaries)
		}

		/// <summary>
		/// Adds completed session to historical rolling storage (14-day max per session type).
		/// </summary>
		private void AddToHistoricalSessions(SessionData sessionData)
		{
			string historyKey = sessionData.SessionType.ToString();

			if (!historicalSessions.ContainsKey(historyKey))
				historicalSessions[historyKey] = new List<SessionData>();

			historicalSessions[historyKey].Add(sessionData);

			// Maintain rolling 14-day window
			if (historicalSessions[historyKey].Count > LookbackDays)
				historicalSessions[historyKey].RemoveAt(0);
		}

		/// <summary>
		/// Builds a session key for storage (e.g., "20260711_Asia").
		/// </summary>
		private string BuildSessionKey(DateTime date, SessionType sessionType)
		{
			return string.Format("{0:yyyyMMdd}_{1}", date, sessionType);
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

		#region Rendering (Placeholder for Phase 3)

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
