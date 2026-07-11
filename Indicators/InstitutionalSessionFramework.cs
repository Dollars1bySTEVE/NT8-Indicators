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

		/// <summary>
		/// Represents a signal event with confirmation score.
		/// </summary>
		private class SignalEvent
		{
			public DateTime EventTime { get; set; }
			public int BarIndex { get; set; }
			public double Price { get; set; }
			public int Score { get; set; } // 0-3
			public bool IsBullish { get; set; }
			public string Reason { get; set; }
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

		// ── Signal/Alert state ───────────────────────────────────────────
		private int lastAlertBar = -999;
		private List<SignalEvent> recentSignals;

		// ── Rendering state ─────────────────────────────────────────────
		private SharpDX.Direct2D1.SolidColorBrush dxAsiaSessionBrush;
		private SharpDX.Direct2D1.SolidColorBrush dxLondonSessionBrush;
		private SharpDX.Direct2D1.SolidColorBrush dxNYSessionBrush;
		private SharpDX.Direct2D1.SolidColorBrush dxPOCBrush;
		private SharpDX.Direct2D1.SolidColorBrush dxBullishBrush;
		private SharpDX.Direct2D1.SolidColorBrush dxBearishBrush;
		private SharpDX.Direct2D1.SolidColorBrush dxLabelBrush;
		private SharpDX.Direct2D1.SolidColorBrush dxPanelBgBrush;
		private SharpDX.Direct2D1.SolidColorBrush dxHistoricalBrush;
		private SharpDX.Direct2D1.StrokeStyle dxDashStrokeStyle;
		private SharpDX.DirectWrite.TextFormat dxTextFormat;
		private SharpDX.DirectWrite.TextFormat dxSmallTextFormat;
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

		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "Minimum Alert Score (0-3)", Order = 5, GroupName = "07. Alerts & Notifications")]
		public int MinimumAlertScore { get; set; }

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

		[NinjaScriptProperty]
		[Range(1, 3)]
		[Display(Name = "Pivot Line Width", Order = 4, GroupName = "09. Appearance — Fonts & Labels")]
		public int HistoricalLineWidth { get; set; }

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
				MinimumAlertScore = 2; // Require score 2 or higher

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
				HistoricalLineWidth = 1;

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
				recentSignals = new List<SignalEvent>();

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

			// ── Step 8: Calculate confirmation score + generate signals ──
			CalculateConfirmationScore();

			// ── Step 9: Trigger alerts if threshold met ──────────────────
			TriggerAlerts();
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

		#region Core Logic — Confirmation Scoring

		/// <summary>
		/// Calculates confirmation score (0-3) based on POC touch, DOM clusters, and BOS/CHoCH.
		/// Score 0: No confirmation
		/// Score 1: One confirmation factor
		/// Score 2: Two confirmation factors  
		/// Score 3: All three factors (or BOS/CHoCH + high conviction)
		/// </summary>
		private void CalculateConfirmationScore()
		{
			if (currentSessionData == null || !currentSessionData.POC.IsCalculated) return;

			int score = 0;
			string reason = "";

			// ── Factor 1: POC Touch (wick rejection at POC) ──────────────
			if (ShowPOCLine && CheckPOCTouchWithWickRejection())
			{
				score++;
				reason += "POC-Touch ";
			}

			// ── Factor 2: DOM Cluster (strong resistance/support) ───────
			if (CheckDOMCluster())
			{
				score++;
				reason += "DOM-Cluster ";
			}

			// ── Factor 3: BOS/CHoCH (smart money structure break) ────────
			if (ShowSmartMoneyEvents && CheckRecentSmartMoneyEvent())
			{
				score++;
				reason += "BOS/CHoCH ";
			}

			// Record signal if score meets threshold
			if (score >= MinimumAlertScore)
			{
				bool isBullish = Close[0] > Open[0];
				recentSignals.Add(new SignalEvent
				{
					EventTime = Time[0],
					BarIndex = CurrentBar,
					Price = Close[0],
					Score = score,
					IsBullish = isBullish,
					Reason = reason
				});

				// Prune old signals (keep last 100)
				if (recentSignals.Count > 100)
					recentSignals.RemoveAt(0);
			}
		}

		/// <summary>
		/// Checks if price touched POC with wick rejection (score +1).
		/// </summary>
		private bool CheckPOCTouchWithWickRejection()
		{
			double pocPrice = currentSessionData.POC.POCPrice;
			double tolerance = TickSize * 2; // Within 2 ticks of POC

			// Check if current bar wicked into POC
			bool touchedPOC = (High[0] >= pocPrice - tolerance && High[0] <= pocPrice + tolerance) ||
							  (Low[0] >= pocPrice - tolerance && Low[0] <= pocPrice + tolerance);

			if (!touchedPOC) return false;

			// Check for wick rejection (close away from POC)
			bool rejection = Math.Abs(Close[0] - pocPrice) > TickSize * 3;
			return rejection;
		}

		/// <summary>
		/// Checks for strong DOM clustering at current price (placeholder).
		/// Real implementation requires OnMarketDepth() integration.
		/// </summary>
		private bool CheckDOMCluster()
		{
			// Placeholder: would check bid/ask depth
			// For now, return false (requires Level 2 implementation)
			return false;
		}

		/// <summary>
		/// Checks if recent BOS/CHoCH event aligns with current price action.
		/// </summary>
		private bool CheckRecentSmartMoneyEvent()
		{
			if (detectedEvents.Count == 0) return false;

			// Check if latest event is recent (within last 5 bars)
			var recentEvent = detectedEvents.Last();
			if (CurrentBar - recentEvent.EndBar > 5) return false;

			// Check if price is in alignment with event direction
			bool aligned = (recentEvent.Direction == SmartMoneyEvent.EventDirection.Bullish && Close[0] > recentEvent.Price) ||
						   (recentEvent.Direction == SmartMoneyEvent.EventDirection.Bearish && Close[0] < recentEvent.Price);

			return aligned;
		}

		#endregion

		#region Core Logic — Alert Triggering

		/// <summary>
		/// Triggers alerts based on confirmation score and user preferences.
		/// </summary>
		private void TriggerAlerts()
		{
			if (SelectedAlertTrigger == AlertTrigger.None) return;
			if (recentSignals.Count == 0) return;

			SignalEvent lastSignal = recentSignals.Last();

			// Check cooldown to prevent alert spam
			if (CurrentBar - lastAlertBar < AlertCooldownBars) return;

			// Determine if alert should fire based on trigger type
			bool shouldAlert = false;
			switch (SelectedAlertTrigger)
			{
				case AlertTrigger.POCTouch:
					shouldAlert = lastSignal.Reason.Contains("POC-Touch");
					break;
				case AlertTrigger.DOMCluster:
					shouldAlert = lastSignal.Reason.Contains("DOM-Cluster");
					break;
				case AlertTrigger.BOSCHoCH:
					shouldAlert = lastSignal.Reason.Contains("BOS/CHoCH");
					break;
				case AlertTrigger.AllThree:
					shouldAlert = lastSignal.Score >= MinimumAlertScore;
					break;
			}

			if (shouldAlert && EnableAudioAlerts)
			{
				try
				{
					PlaySound(AlertSoundFile);
					lastAlertBar = CurrentBar;

					// Log alert
					string direction = lastSignal.IsBullish ? "BULLISH" : "BEARISH";
					Log(string.Format("ALERT: {0} Signal (Score: {1}) - {2}", direction, lastSignal.Score, lastSignal.Reason), LogLevel.Information);
				}
				catch (Exception ex)
				{
					Log("Error triggering alert: " + ex.Message, LogLevel.Error);
				}
			}
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
				dxHistoricalBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, ToColor4(LabelTextColor, HistoricalSessionOpacity / 100f));

				// Create dash stroke style for historical lines
				SharpDX.Direct2D1.StrokeStyleProperties strokeProps = new SharpDX.Direct2D1.StrokeStyleProperties();
				strokeProps.DashStyle = SharpDX.Direct2D1.DashStyle.Dash;
				dxDashStrokeStyle = new SharpDX.Direct2D1.StrokeStyle(renderTarget.Factory, strokeProps);

				dxTextFormat = new SharpDX.DirectWrite.TextFormat(
					NinjaTrader.Core.Globals.DirectWriteFactory,
					"Arial",
					SharpDX.DirectWrite.FontWeight.Normal,
					SharpDX.DirectWrite.FontStyle.Normal,
					SharpDX.DirectWrite.FontStretch.Normal,
					LabelFontSize);

				dxSmallTextFormat = new SharpDX.DirectWrite.TextFormat(
					NinjaTrader.Core.Globals.DirectWriteFactory,
					"Arial",
					SharpDX.DirectWrite.FontWeight.Normal,
					SharpDX.DirectWrite.FontStyle.Normal,
					SharpDX.DirectWrite.FontStretch.Normal,
					LabelFontSize - 2);

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
			if (dxHistoricalBrush != null) { dxHistoricalBrush.Dispose(); dxHistoricalBrush = null; }
			if (dxDashStrokeStyle != null) { dxDashStrokeStyle.Dispose(); dxDashStrokeStyle = null; }
			if (dxTextFormat != null) { dxTextFormat.Dispose(); dxTextFormat = null; }
			if (dxSmallTextFormat != null) { dxSmallTextFormat.Dispose(); dxSmallTextFormat = null; }
			dxResourcesCreated = false;
		}

		public override void OnRenderTargetChanged()
		{
			DisposeSharpDXResources();
		}

		#endregion

		#region GPU Rendering — OnRender

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);

			SharpDX.Direct2D1.RenderTarget renderTarget = RenderTarget;
			if (renderTarget == null || activeSessions == null || activeSessions.Count == 0)
				return;

			if (!dxResourcesCreated)
				CreateSharpDXResources(renderTarget);

			int firstBar = ChartBars.FromIndex;
			int lastBar = ChartBars.ToIndex;

			// ── Render historical sessions first (background layer) ──────
			if (ShowHistoricalSessions)
				RenderHistoricalSessions(renderTarget, chartControl, chartScale, firstBar, lastBar);

			// ── Render current session elements ──────────────────────────
			if (currentSessionData != null)
			{
				// Opening range box
				if (ShowOpeningRangeBox && currentSessionData.OpeningRange.IsComplete)
					RenderOpeningRangeBox(renderTarget, chartControl, chartScale, currentSessionData);

				// POC line + label
				if (ShowPOCLine && currentSessionData.POC.IsCalculated)
					RenderPOCLine(renderTarget, chartControl, chartScale, currentSessionData);

				// Pivot levels (session high/low)
				if (ShowPivotPoints)
					RenderPivotLevels(renderTarget, chartControl, chartScale, currentSessionData);

				// BOS/CHoCH markers
				if (ShowSmartMoneyEvents && detectedEvents.Count > 0)
					RenderSmartMoneyEvents(renderTarget, chartControl, chartScale);

				// Signal arrows (high confidence levels)
				RenderSignalArrows(renderTarget, chartControl, chartScale);
			}

			// ── Render dashboard panel (top layer) ────────────────────────
			if (ShowDashboard && currentSessionData != null)
				RenderDashboard(renderTarget, chartControl, chartScale, currentSessionData);
		}

		#endregion

		#region Rendering — Opening Range Box

		private void RenderOpeningRangeBox(SharpDX.Direct2D1.RenderTarget rt, ChartControl chartControl, 
			ChartScale chartScale, SessionData sessionData)
		{
			OpeningRange orng = sessionData.OpeningRange;
			if (orng.StartBar <= 0 || orng.EndBar <= 0) return;

			float xStart = chartControl.GetXByBarIndex(ChartBars, orng.StartBar);
			float xEnd = chartControl.GetXByBarIndex(ChartBars, orng.EndBar);
			float yHigh = chartScale.GetYByValue(orng.HighPrice);
			float yLow = chartScale.GetYByValue(orng.LowPrice);

			SharpDX.Direct2D1.SolidColorBrush boxBrush = GetSessionBrush(sessionData.SessionType);
			if (boxBrush != null)
			{
				rt.FillRectangle(new SharpDX.RectangleF(xStart, yHigh, xEnd - xStart, yLow - yHigh), boxBrush);
			}

			// Label
			if (ShowSessionLabels)
			{
				string label = string.Format("{0} OR", sessionData.SessionType.ToString().Substring(0, 1));
				SharpDX.DirectWrite.TextLayout layout = new SharpDX.DirectWrite.TextLayout(
					NinjaTrader.Core.Globals.DirectWriteFactory, label, dxSmallTextFormat, 100f, 20f);
				rt.DrawTextLayout(new SharpDX.Vector2(xStart + 2, yHigh - 18), layout, dxLabelBrush);
				layout.Dispose();
			}
		}

		#endregion

		#region Rendering — POC Line

		private void RenderPOCLine(SharpDX.Direct2D1.RenderTarget rt, ChartControl chartControl,
			ChartScale chartScale, SessionData sessionData)
		{
			if (sessionData.POC.POCPrice <= 0) return;

			float yPOC = chartScale.GetYByValue(sessionData.POC.POCPrice);
			float xStart = chartControl.GetXByBarIndex(ChartBars, Math.Max(sessionData.StartBar, ChartBars.FromIndex));
			float xEnd = chartControl.GetXByBarIndex(ChartBars, Math.Min(sessionData.EndBar > 0 ? sessionData.EndBar : CurrentBar, ChartBars.ToIndex));

			// POC line
			rt.DrawLine(new SharpDX.Vector2(xStart, yPOC), new SharpDX.Vector2(xEnd, yPOC), 
				dxPOCBrush, 2, dxDashStrokeStyle);

			// Label
			if (ShowPOCLabel)
			{
				string label = string.Format("POC: {0:F2}", sessionData.POC.POCPrice);
				SharpDX.DirectWrite.TextLayout layout = new SharpDX.DirectWrite.TextLayout(
					NinjaTrader.Core.Globals.DirectWriteFactory, label, dxSmallTextFormat, 150f, 20f);
				rt.DrawTextLayout(new SharpDX.Vector2(xEnd + 4, yPOC - 10), layout, dxLabelBrush);
				layout.Dispose();
			}
		}

		#endregion

		#region Rendering — Pivot Levels

		private void RenderPivotLevels(SharpDX.Direct2D1.RenderTarget rt, ChartControl chartControl,
			ChartScale chartScale, SessionData sessionData)
		{
			// Session High
			float yHigh = chartScale.GetYByValue(sessionData.HighPrice);
			float xStart = chartControl.GetXByBarIndex(ChartBars, Math.Max(sessionData.StartBar, ChartBars.FromIndex));
			float xEnd = chartControl.GetXByBarIndex(ChartBars, Math.Min(sessionData.EndBar > 0 ? sessionData.EndBar : CurrentBar, ChartBars.ToIndex));

			rt.DrawLine(new SharpDX.Vector2(xStart, yHigh), new SharpDX.Vector2(xEnd, yHigh),
				dxBullishBrush, PivotLineWidth);

			// Session Low
			float yLow = chartScale.GetYByValue(sessionData.LowPrice);
			rt.DrawLine(new SharpDX.Vector2(xStart, yLow), new SharpDX.Vector2(xEnd, yLow),
				dxBearishBrush, PivotLineWidth);
		}

		#endregion

		#region Rendering — Smart Money Events

		private void RenderSmartMoneyEvents(SharpDX.Direct2D1.RenderTarget rt, ChartControl chartControl,
			ChartScale chartScale)
		{
			foreach (var evt in detectedEvents.TakeLast(5)) // Show last 5 events only
			{
				float yPrice = chartScale.GetYByValue(evt.Price);
				float xStart = chartControl.GetXByBarIndex(ChartBars, evt.StartBar);
				float xEnd = chartControl.GetXByBarIndex(ChartBars, evt.EndBar);

				SharpDX.Direct2D1.SolidColorBrush eventBrush = evt.Direction == SmartMoneyEvent.EventDirection.Bullish 
					? dxBullishBrush : dxBearishBrush;

				// Draw event marker
				rt.DrawLine(new SharpDX.Vector2(xStart, yPrice), new SharpDX.Vector2(xEnd, yPrice),
					eventBrush, 1, dxDashStrokeStyle);

				// Label
				string label = evt.Type == SmartMoneyEvent.EventType.BOS ? "BOS" : "CHoCH";
				SharpDX.DirectWrite.TextLayout layout = new SharpDX.DirectWrite.TextLayout(
					NinjaTrader.Core.Globals.DirectWriteFactory, label, dxSmallTextFormat, 50f, 15f);
				rt.DrawTextLayout(new SharpDX.Vector2(xEnd + 2, yPrice - 12), layout, eventBrush);
				layout.Dispose();
			}
		}

		#endregion

		#region Rendering — Signal Arrows

		private void RenderSignalArrows(SharpDX.Direct2D1.RenderTarget rt, ChartControl chartControl,
			ChartScale chartScale)
		{
			foreach (var signal in recentSignals.TakeLast(3)) // Show last 3 signals
			{
				float xSignal = chartControl.GetXByBarIndex(ChartBars, signal.BarIndex);
				float ySignal = chartScale.GetYByValue(signal.Price);

				SharpDX.Direct2D1.SolidColorBrush arrowBrush = signal.IsBullish ? dxBullishBrush : dxBearishBrush;

				// Draw arrow (simple circle marker for now)
				SharpDX.RectangleF arrowRect = new SharpDX.RectangleF(xSignal - 8, ySignal - 8, 16, 16);
				rt.DrawRectangle(arrowRect, arrowBrush, 2);

				// Score label
				string scoreLabel = string.Format("S{0}", signal.Score);
				SharpDX.DirectWrite.TextLayout layout = new SharpDX.DirectWrite.TextLayout(
					NinjaTrader.Core.Globals.DirectWriteFactory, scoreLabel, dxSmallTextFormat, 30f, 15f);
				rt.DrawTextLayout(new SharpDX.Vector2(xSignal - 6, ySignal - 6), layout, arrowBrush);
				layout.Dispose();
			}
		}

		#endregion

		#region Rendering — Historical Sessions

		private void RenderHistoricalSessions(SharpDX.Direct2D1.RenderTarget rt, ChartControl chartControl,
			ChartScale chartScale, int firstBar, int lastBar)
		{
			if (historicalSessions == null || historicalSessions.Count == 0) return;

			int sessionsDrawn = 0;
			foreach (var sessionList in historicalSessions.Values)
			{
				foreach (var historicalSession in sessionList.Reverse<SessionData>())
				{
					if (sessionsDrawn >= LookbackDays) return;

					// Filter based on day label option
					if (!ShouldShowHistoricalSession(historicalSession)) continue;

					if (historicalSession.EndBar < firstBar || historicalSession.StartBar > lastBar)
						continue;

					// Draw POC line for historical session (faded)
					if (historicalSession.POC.IsCalculated)
					{
						float yPOC = chartScale.GetYByValue(historicalSession.POC.POCPrice);
						float xStart = chartControl.GetXByBarIndex(ChartBars, Math.Max(historicalSession.StartBar, firstBar));
						float xEnd = chartControl.GetXByBarIndex(ChartBars, Math.Min(historicalSession.EndBar, lastBar));

						rt.DrawLine(new SharpDX.Vector2(xStart, yPOC), new SharpDX.Vector2(xEnd, yPOC),
							dxHistoricalBrush, HistoricalLineWidth, dxDashStrokeStyle);

						// Day label
						if (HistoricalDayLabels != DayLabelOption.HiddenAll)
						{
							string dayName = historicalSession.SessionDate.ToString("ddd");
							SharpDX.DirectWrite.TextLayout layout = new SharpDX.DirectWrite.TextLayout(
								NinjaTrader.Core.Globals.DirectWriteFactory, dayName, dxSmallTextFormat, 40f, 15f);
							rt.DrawTextLayout(new SharpDX.Vector2(xStart + 2, yPOC + 2), layout, dxHistoricalBrush);
							layout.Dispose();
						}
					}

					sessionsDrawn++;
				}
			}
		}

		private bool ShouldShowHistoricalSession(SessionData session)
		{
			if (HistoricalDayLabels == DayLabelOption.HiddenAll) return false;
			if (HistoricalDayLabels == DayLabelOption.CurrentDayOnly)
				return session.SessionDate == DateTime.Today;

			int daysDiff = (int)(DateTime.Today - session.SessionDate).TotalDays;
			if (HistoricalDayLabels == DayLabelOption.Last7Days)
				return daysDiff >= 0 && daysDiff <= 7;
			if (HistoricalDayLabels == DayLabelOption.Last14Days)
				return daysDiff >= 0 && daysDiff <= 14;

			return false;
		}

		#endregion

		#region Rendering — Dashboard Panel

		private void RenderDashboard(SharpDX.Direct2D1.RenderTarget rt, ChartControl chartControl,
			ChartScale chartScale, SessionData sessionData)
		{
			StringBuilder sb = new StringBuilder();
			sb.AppendLine("=== INSTITUTIONAL SESSION FRAMEWORK ===");
			sb.AppendLine();
			sb.AppendFormat("Session: {0}\n", sessionData.SessionType);
			sb.AppendFormat("OHLC: {0:F2} / {1:F2} / {2:F2} / {3:F2}\n",
				sessionData.OpenPrice, sessionData.HighPrice, sessionData.LowPrice, sessionData.ClosePrice);
			sb.AppendFormat("Range: {0:F2} | Volume: {1:F0}\n",
				sessionData.HighPrice - sessionData.LowPrice, sessionData.TotalVolume);

			if (sessionData.POC.IsCalculated)
				sb.AppendFormat("POC: {0:F2} (Vol: {1:F0})\n", sessionData.POC.POCPrice, sessionData.POC.POCVolume);

			if (sessionData.OpeningRange.IsComplete)
				sb.AppendFormat("OR: {0:F2} - {1:F2}\n", sessionData.OpeningRange.LowPrice, sessionData.OpeningRange.HighPrice);

			if (recentSignals.Count > 0)
			{
				SignalEvent lastSignal = recentSignals.Last();
				sb.AppendFormat("Last Signal: Score {0} ({1})\n", lastSignal.Score, lastSignal.Reason.Trim());
			}

			string dashboardText = sb.ToString();
			SharpDX.DirectWrite.TextLayout layout = new SharpDX.DirectWrite.TextLayout(
				NinjaTrader.Core.Globals.DirectWriteFactory,
				dashboardText, dxTextFormat, 300f, 250f);

			float textWidth = layout.Metrics.Width + 16f;
			float textHeight = layout.Metrics.Height + 16f;
			float padding = 8f;
			float margin = 10f;

			float panelX = GetDashboardX(chartControl, textWidth, margin);
			float panelY = GetDashboardY(chartControl, textHeight, margin);

			// Panel background
			rt.FillRectangle(new SharpDX.RectangleF(panelX, panelY, textWidth, textHeight), dxPanelBgBrush);

			// Panel border
			rt.DrawRectangle(new SharpDX.RectangleF(panelX, panelY, textWidth, textHeight), dxLabelBrush, 1);

			// Text
			rt.DrawTextLayout(new SharpDX.Vector2(panelX + padding, panelY + padding), layout, dxLabelBrush);
			layout.Dispose();
		}

		private float GetDashboardX(ChartControl chartControl, float panelWidth, float margin)
		{
			switch (DashboardPosition)
			{
				case TextPosition.TopRight:
				case TextPosition.BottomRight:
					return (float)chartControl.Width - panelWidth - margin;
				default:
					return margin;
			}
		}

		private float GetDashboardY(ChartControl chartControl, float panelHeight, float margin)
		{
			switch (DashboardPosition)
			{
				case TextPosition.BottomLeft:
				case TextPosition.BottomRight:
					return (float)chartControl.Height - panelHeight - margin;
				default:
					return margin;
			}
		}

		#endregion

		#region Helper Methods

		private SharpDX.Direct2D1.SolidColorBrush GetSessionBrush(SessionType sessionType)
		{
			switch (sessionType)
			{
				case SessionType.Asia: return dxAsiaSessionBrush;
				case SessionType.London: return dxLondonSessionBrush;
				case SessionType.NewYork: return dxNYSessionBrush;
				default: return dxLabelBrush;
			}
		}

		private SharpDX.Color4 ToColor4(System.Windows.Media.Brush wpfBrush, float alpha)
		{
			System.Windows.Media.Color c = ((System.Windows.Media.SolidColorBrush)wpfBrush).Color;
			return new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, alpha);
		}

		private string BuildSessionKey(DateTime date, SessionType sessionType)
		{
			return string.Format("{0:yyyyMMdd}_{1}", date, sessionType);
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
