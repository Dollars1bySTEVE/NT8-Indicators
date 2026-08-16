// 
// DrawingToolTilePro.cs
// Based on NinjaTrader's Drawing Tool Tile
// Custom Mod: Opacity, Expand/Collapse Toggle, Built-in Freehand Pen (with
//             color/width/style settings) using a transparent overlay canvas,
//             Trashcan (remove all drawings + pen strokes), and geometry-icon
//             rendering fix for custom drawing tools.
//
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Gui;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using System.Windows.Data;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Chart;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
	[TypeConverter("NinjaTrader.NinjaScript.Indicators.DrawingToolTileProTypeConverter")]
	[CategoryOrder(typeof(Custom.Resource), "NinjaScriptParameters", 1)]
	[CategoryOrder(typeof(Resource), "PropertyCategoryDataSeries", 2)]
	[CategoryOrder(typeof(Resource), "NinjaScriptSetup", 3)]
	[CategoryOrder(typeof(Custom.Resource), "NinjaScriptDrawingTools", 4)]
	[CategoryOrder(typeof(Custom.Resource), "NinjaScriptIndicatorVisualGroup", 5)]
	[CategoryExpanded(typeof(Custom.Resource), "NinjaScriptDrawingTools", false)]
	public class DrawingToolTilePro : Indicator
	{
		private		Border		b;
		private		Grid		grid;
		private		Border		tileHolder;
		private		Thickness	margin;
		private		bool		subscribedToSize;
		private		Point		startPoint;
		private		bool		isDragging;

		// -- Built-in pen state --
		private		bool									penMode;
		private		Button									penBtn;
		private		Canvas									penOverlay;
		private		List<List<Tuple<DateTime, double>>>		strokes			= new List<List<Tuple<DateTime, double>>>();
		private		List<Tuple<DateTime, double>>			currentStroke;
		private		Brush									penBtnDefaultBg;

		protected override void OnBarUpdate()
		{
			if (!subscribedToSize && ChartPanel != null)
			{
				subscribedToSize = true;

				ChartPanel.SizeChanged += (_, _) =>
				{
					if (grid == null || ChartPanel == null)
						return;
					if (grid.Margin.Left + grid.ActualWidth > ChartPanel.ActualWidth || grid.Margin.Top + grid.ActualHeight > ChartPanel.ActualHeight)
					{
						double left	= Math.Max(0, Math.Min(grid.Margin.Left, ChartPanel.ActualWidth - grid.ActualWidth));
						double top	= Math.Max(0, Math.Min(grid.Margin.Top, ChartPanel.ActualHeight - grid.ActualHeight));
						grid.Margin	= new Thickness(left, top, 0, 0);
						Left		= left;
						Top			= top;
					}
				};
			}
		}

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name							= "Drawing Tool Tile Pro";
				Description						= "Drawing tool tile with adjustable opacity, expand/collapse toggle, built-in freehand pen, and trashcan button.";
				IsOverlay						= true;
				IsChartOnly						= true;
				DisplayInDataBox				= false;
				PaintPriceMarkers				= false;
				IsSuspendedWhileInactive		= true;
				SelectedTypes					= new XElement("SelectedTypes");

				foreach (Type type in new[]
				{
					typeof(DrawingTools.Ellipse), typeof(DrawingTools.ExtendedLine),
					typeof(DrawingTools.FibonacciExtensions), typeof(DrawingTools.FibonacciRetracements),
					typeof(DrawingTools.HorizontalLine), typeof(DrawingTools.Line),
					typeof(DrawingTools.Ray), typeof(DrawingTools.Rectangle), typeof(DrawingTools.Text), typeof(DrawingTools.VerticalLine)
				})
				{
					XElement	el				= new(type.FullName ?? "");
					el.Add(new XAttribute("Assembly", "NinjaTrader.Custom"));
					SelectedTypes.Add(el);
				}
				Left			= 5;
				Top				= -1;
				NumberOfRows	= 5;
				
				BackgroundOpacity = 80;
				IsExpanded        = true;
				PenStroke         = new Gui.Stroke(Brushes.DodgerBlue, DashStyleHelper.Solid, 2f);
			}
			else if (State == State.Historical)
			{
				if (IsVisible && ChartControl != null)
				{
					if (ChartPanel.IsDelayedButtonVisible && Left < ChartPanel.DelayedButtonWidth)
						Left = ChartPanel.DelayedButtonWidth;
					if (Top < 0)
						Top = 25;

					ChartControl.Dispatcher.InvokeAsync(() => { if (State < State.Terminated) UserControlCollection.Add(CreateControl()); });
				}
			}
			else if (State == State.Terminated)
			{
				// Make sure the overlay never outlives the indicator
				if (ChartControl != null && penOverlay != null)
					ChartControl.Dispatcher.InvokeAsync(() =>
					{
						if (penOverlay != null)
						{
							UserControlCollection.Remove(penOverlay);
							penOverlay = null;
						}
					});
			}
		}

		// -- Pen helpers ------------------------------------------------------

		private ChartScale GetPenScale()
		{
			foreach (ChartScale s in ChartPanel.Scales)
				if (s.ScaleJustification == ScaleJustification.Right)
					return s;
			foreach (ChartScale s in ChartPanel.Scales)
				return s;
			return null;
		}

		private void AddPenPoint(Point wpfPoint)
		{
			if (currentStroke == null || ChartControl == null || ChartPanel == null)
				return;

			ChartScale scale = GetPenScale();
			if (scale == null)
				return;

			int px = ChartingExtensions.ConvertToHorizontalPixels(wpfPoint.X, ChartControl.PresentationSource);
			int py = ChartingExtensions.ConvertToVerticalPixels(wpfPoint.Y, ChartControl.PresentationSource);

			DateTime	time	= ChartControl.GetTimeByX(px);
			double		price	= scale.GetValueByY(py);

			currentStroke.Add(new Tuple<DateTime, double>(time, price));
		}

		private void CreatePenOverlay()
		{
			if (penOverlay != null)
				return;

			// Transparent canvas that catches drawing input ONLY while pen mode is on.
			// It is removed from the tree entirely when pen mode is off, so it can
			// never interfere with the chart, the tile, or NT's own input handling.
			penOverlay = new Canvas
			{
				Background	= Brushes.Transparent,	// transparent but hit-testable
				Cursor		= System.Windows.Input.Cursors.Pen,
				IsHitTestVisible = true
			};

			penOverlay.MouseLeftButtonDown += (s, e) =>
			{
				currentStroke = new List<Tuple<DateTime, double>>();
				AddPenPoint(e.GetPosition(ChartPanel));
				penOverlay.CaptureMouse();
				e.Handled = true;
			};

			penOverlay.MouseMove += (s, e) =>
			{
				if (currentStroke == null)
					return;
				AddPenPoint(e.GetPosition(ChartPanel));
				ForceRefresh();
				e.Handled = true;
			};

			penOverlay.MouseLeftButtonUp += (s, e) =>
			{
				FinishStroke();
				if (penOverlay != null && penOverlay.IsMouseCaptured)
					penOverlay.ReleaseMouseCapture();
				e.Handled = true;
			};

			// Safety net: if capture is lost for ANY reason, finish the stroke cleanly.
			penOverlay.LostMouseCapture += (s, e) => FinishStroke();

			UserControlCollection.Add(penOverlay);

			// Keep the tile above the overlay so it stays clickable in pen mode
			System.Windows.Controls.Panel.SetZIndex(penOverlay, 0);
			if (grid != null)
				System.Windows.Controls.Panel.SetZIndex(grid, 1);
		}

		private void RemovePenOverlay()
		{
			if (penOverlay == null)
				return;

			FinishStroke();
			if (penOverlay.IsMouseCaptured)
				penOverlay.ReleaseMouseCapture();
			UserControlCollection.Remove(penOverlay);
			penOverlay = null;
		}

		private void FinishStroke()
		{
			if (currentStroke != null && currentStroke.Count > 1)
				strokes.Add(currentStroke);
			currentStroke = null;
			ForceRefresh();
		}

		private void SetPenMode(bool on)
		{
			penMode = on;

			if (penBtn != null)
			{
				if (penBtnDefaultBg == null)
					penBtnDefaultBg = penBtn.Background;
				penBtn.Background = on ? new SolidColorBrush(Color.FromArgb(120, 30, 144, 255)) : penBtnDefaultBg;
			}

			if (on)
				CreatePenOverlay();
			else
				RemovePenOverlay();
		}

		// ----------------------------------------------------------------------

		private FrameworkElement CreateControl()
		{
			if (grid != null)
				return grid;

			grid = new Grid { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(Left, Top, 0, 0) };

			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength() });
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength() });
			grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength() });

			Brush baseBrush = Application.Current.FindResource("BackgroundMainWindow") as Brush ?? Brushes.White;
			SolidColorBrush background = new SolidColorBrush(baseBrush is SolidColorBrush scb ? scb.Color : Colors.Black)
			{
				Opacity = Math.Max(0.0, Math.Min(1.0, BackgroundOpacity / 100.0))
			};

			Brush borderBrush = Application.Current.FindResource("BorderThinBrush") as Brush ?? Brushes.Black;

			Grid g = new();
			g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
			g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
			g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });

			for (int r = 0; r < g.RowDefinitions.Count; r++)
			{
				System.Windows.Shapes.Ellipse e = new()
				{
					Width				= 3,
					Height				= 3,
					HorizontalAlignment	= HorizontalAlignment.Center,
					VerticalAlignment	= VerticalAlignment.Center,
					Fill				= borderBrush
				};
				Grid.SetRow(e, r);
				g.Children.Add(e);
			}
			
			b = new Border
			{
				VerticalAlignment	= VerticalAlignment.Top,
				BorderThickness		= new Thickness(0, 1, 1, 1),
				BorderBrush			= borderBrush,
				Background			= background,
				Width				= 12,
				Height				= 24,
				Cursor				= System.Windows.Input.Cursors.Hand,
				Child				= g
			};

			b.MouseDown += (_, e) =>
			{
				startPoint = e.GetPosition(ChartPanel);
				margin     = grid.Margin;
				isDragging = false;

				if (e.ClickCount > 1)
				{
					b.ReleaseMouseCapture();
					ChartControl.OnIndicatorsHotKey(this, null);
				}
				else
					b.CaptureMouse();
			};

			b.MouseUp += (_, _) => 
			{ 
				b.ReleaseMouseCapture(); 
				
				if (!isDragging && tileHolder != null)
				{
					tileHolder.Visibility = tileHolder.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
				}
			};

			b.MouseMove += (_, e) =>
			{
				if (!b.IsMouseCaptured || grid == null || ChartPanel == null)
					return;

				Point newPoint = e.GetPosition(ChartPanel);
				
				if (Math.Abs(newPoint.X - startPoint.X) > 2 || Math.Abs(newPoint.Y - startPoint.Y) > 2)
					isDragging = true;

				if (margin.Left + (newPoint.X - startPoint.X) < 0 || margin.Left + (newPoint.X - startPoint.X) > ChartPanel.ActualWidth - grid.ActualWidth 
					|| margin.Top + (newPoint.Y - startPoint.Y) < 0 || margin.Top + (newPoint.Y - startPoint.Y) > ChartPanel.ActualHeight - grid.ActualHeight)
				{
					ChartControl.InitDragDrop(this);
					return;
				}

				grid.Margin = new Thickness {
					Left = Math.Max(0, Math.Min(margin.Left + (newPoint.X - startPoint.X), ChartPanel.ActualWidth - grid.ActualWidth)),
					Top  = Math.Max(0, Math.Min(margin.Top  + (newPoint.Y - startPoint.Y), ChartPanel.ActualHeight - grid.ActualHeight))
				};

				if (ChartPanel.IsDelayedButtonVisible && grid.Margin.Left <= ChartPanel.DelayedButtonWidth && grid.Margin.Top <= ChartPanel.DelayedButtonHeight)
				{
					if (ChartPanel.DelayedButtonWidth - grid.Margin.Left >= ChartPanel.DelayedButtonHeight - grid.Margin.Top)
						grid.Margin = new Thickness(grid.Margin.Left, ChartPanel.DelayedButtonHeight, 0, 0);
					else
						grid.Margin = new Thickness(ChartPanel.DelayedButtonWidth, grid.Margin.Top, 0, 0);
				}

				Left = grid.Margin.Left;
				Top  = grid.Margin.Top;
			};

			Grid.SetColumn(b, 1);
			grid.Children.Add(b);

			Grid			contentGrid		= new();
			List<XElement>	elements		= SortElements(XElement.Parse(SelectedTypes.ToString()));
			int				column			= 0;
			int				count			= 0;
			FontFamily		fontFamily		= Application.Current.Resources["IconsFamily"] as FontFamily;
			Style			style			= Application.Current.Resources["LinkButtonStyle"] as Style;

			while (count < elements.Count)
			{
				if (contentGrid.ColumnDefinitions.Count <= column)
					contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star)});
				for (int j = 0; j < NumberOfRows && count < elements.Count; j++)
				{
					if (contentGrid.RowDefinitions.Count <= j)
						contentGrid.RowDefinitions.Add(new RowDefinition {Height = new GridLength(1, GridUnitType.Auto)});
					XElement element = elements[count];
					try
					{
						if (Core.Globals.AssemblyRegistry[element.Attribute("Assembly").Value].CreateInstance(element.Name.ToString()) is DrawingTools.DrawingTool { DisplayOnChartsMenus: true } dt)
						{
							// Custom tools may return a Geometry as their Icon; wrap it in a Path
							// so WPF renders the shape instead of calling ToString() on it.
							object iconContent = dt.Icon ?? Gui.Tools.Icons.DrawPencil;
							if (iconContent is Geometry geo)
								iconContent = new System.Windows.Shapes.Path
								{
									Data    = geo,
									Fill    = Application.Current.FindResource("FontControlBrush") as Brush ?? Brushes.LightGray,
									Stretch = System.Windows.Media.Stretch.Uniform,
									Width   = 16,
									Height  = 16
								};

							Button bb = new()
							{
								Content		= iconContent,
								ToolTip		= dt.DisplayName,
								Style		= style,
								FontFamily	= fontFamily,
								FontSize	= 16,
								FontStyle	= FontStyles.Normal,
								Margin		= new Thickness(3),
								Padding		= new Thickness(3)
							};

							Grid.SetRow(bb, j);
							Grid.SetColumn(bb, column);

							bb.Click += (_, _) =>
							{
								SetPenMode(false);
								ChartControl?.TryStartDrawing(dt.GetType().FullName);
							};

							contentGrid.Children.Add(bb);
							count++;
						}
						else
						{
							elements.RemoveAt(j);
							j--;
						}
					}
					catch (Exception e)
					{
						elements.RemoveAt(j);
						j--;
						Cbi.Log.Process(typeof(Custom.Resource), "NinjaScriptTileError", new object[] { element.Name.ToString(), e }, LogLevel.Error, LogCategories.NinjaScript);
					}
				}
				column++;
			}

			// -- Pen + Trashcan buttons: placed inline, continuing the tool flow --
			Action<Button> placeInline = btn =>
			{
				int col = count / NumberOfRows;
				int row = count % NumberOfRows;
				while (contentGrid.ColumnDefinitions.Count <= col)
					contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
				while (contentGrid.RowDefinitions.Count <= row)
					contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
				Grid.SetRow(btn, row);
				Grid.SetColumn(btn, col);
				contentGrid.Children.Add(btn);
				count++;
			};

			// Pen toggle button
			penBtn = new Button
			{
				Content    = "\u270F",   // pencil glyph (U+270F)
				ToolTip    = "Freehand pen - click to toggle draw mode (set color/width/style in indicator properties)",
				Style      = style,
				FontSize   = 16,
				FontStyle  = FontStyles.Normal,
				Margin     = new Thickness(3),
				Padding    = new Thickness(3)
			};
			penBtn.Click += (_, _) => SetPenMode(!penMode);

			// Trashcan button
			Button trashBtn = new()
			{
				Content    = "\uD83D\uDDD1", // trashcan glyph (U+1F5D1)
				ToolTip    = "Remove ALL drawings and pen strokes from this chart",
				Style      = style,
				FontSize   = 16,
				FontStyle  = FontStyles.Normal,
				Margin     = new Thickness(3),
				Padding    = new Thickness(3)
			};

			trashBtn.Click += (_, _) =>
			{
				if (ChartControl == null)
					return;

				ChartControl.Dispatcher.InvokeAsync(() =>
				{
					// 1) Clear all built-in pen strokes (guaranteed - we own these)
					strokes.Clear();
					currentStroke = null;

					// 2) Remove all user-drawn drawing tools on this chart (all panels)
					foreach (ChartPanel panel in ChartControl.ChartPanels)
					{
						List<DrawingTools.DrawingTool> toRemove = new();
						foreach (object obj in panel.ChartObjects)
						{
							DrawingTools.DrawingTool dtObj = obj as DrawingTools.DrawingTool;
							if (dtObj == null || dtObj.IsLocked)
								continue;
							if (dtObj.IsUserDrawn)
								toRemove.Add(dtObj);
						}

						foreach (DrawingTools.DrawingTool dtObj in toRemove)
						{
							panel.ChartObjects.Remove(dtObj);
							dtObj.Dispose();
						}
					}

					ForceRefresh();
					ChartControl.InvalidateVisual();
				});
			};

			placeInline(penBtn);
			placeInline(trashBtn);

			tileHolder = new()
			{
				Cursor				= System.Windows.Input.Cursors.Arrow,
				Background			= background,
				BorderThickness		= new Thickness ((double)(Application.Current.FindResource("BorderThinThickness") ?? 1)),
				BorderBrush			= Application.Current.FindResource("BorderThinBrush")as Brush,
				Child				= contentGrid,
				Visibility          = IsExpanded ? Visibility.Visible : Visibility.Collapsed
			};

			grid.Children.Add(tileHolder);

			if (IsVisibleOnlyFocused)
			{
				Binding binding = new("IsActive") { Source = ChartControl.OwnerChart, Converter = Application.Current.FindResource("BoolToVisConverter") as IValueConverter};
				grid.SetBinding(UIElement.VisibilityProperty, binding);
			}

			return grid;
		}

		public override void CopyTo(NinjaScript ninjaScript)
		{
			if (ninjaScript is DrawingToolTilePro dti)
			{
				dti.Left	= Left;
				dti.Top		= Top;
			}
			base.CopyTo(ninjaScript);
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			// Render all pen strokes (and the in-progress stroke)
			if (strokes.Count == 0 && currentStroke == null)
				return;
			if (PenStroke == null)
				return;

			PenStroke.RenderTarget		= RenderTarget;
			RenderTarget.AntialiasMode	= SharpDX.Direct2D1.AntialiasMode.PerPrimitive;

			RenderStroke(chartControl, chartScale, currentStroke);
			foreach (List<Tuple<DateTime, double>> stroke in strokes)
				RenderStroke(chartControl, chartScale, stroke);
		}

		private void RenderStroke(ChartControl chartControl, ChartScale chartScale, List<Tuple<DateTime, double>> stroke)
		{
			if (stroke == null || stroke.Count < 2)
				return;

			for (int i = 0; i < stroke.Count - 1; i++)
			{
				float x1 = chartControl.GetXByTime(stroke[i].Item1);
				float y1 = chartScale.GetYByValue(stroke[i].Item2);
				float x2 = chartControl.GetXByTime(stroke[i + 1].Item1);
				float y2 = chartScale.GetYByValue(stroke[i + 1].Item2);

				RenderTarget.DrawLine(
					new SharpDX.Vector2(x1, y1),
					new SharpDX.Vector2(x2, y2),
					PenStroke.BrushDX,
					PenStroke.Width,
					PenStroke.StrokeStyle);
			}
		}

		private List<XElement> SortElements(XElement elements)
		{
			string[] ordered =	{
									typeof(DrawingTools.Ruler)					.FullName,
									typeof(DrawingTools.RiskReward)				.FullName,
									typeof(DrawingTools.RegionHighlightX)		.FullName,
									typeof(DrawingTools.RegionHighlightY)		.FullName,
									typeof(DrawingTools.Line)					.FullName,
									typeof(DrawingTools.Ray)					.FullName,
									typeof(DrawingTools.ExtendedLine)			.FullName,
									typeof(DrawingTools.ArrowLine)				.FullName,
									typeof(DrawingTools.HorizontalLine)			.FullName,
									typeof(DrawingTools.VerticalLine)			.FullName,
									typeof(DrawingTools.PathTool)				.FullName,
									typeof(DrawingTools.FibonacciRetracements)	.FullName,
									typeof(DrawingTools.FibonacciExtensions)	.FullName,
									typeof(DrawingTools.FibonacciTimeExtensions).FullName,
									typeof(DrawingTools.FibonacciCircle)		.FullName,
									typeof(DrawingTools.AndrewsPitchfork)		.FullName,
									typeof(DrawingTools.GannFan)				.FullName,
									typeof(DrawingTools.RegressionChannel)		.FullName,
									typeof(DrawingTools.TrendChannel)			.FullName,
									typeof(DrawingTools.TimeCycles)				.FullName,
									typeof(DrawingTools.Ellipse)				.FullName,
									typeof(DrawingTools.Rectangle)				.FullName,
									typeof(DrawingTools.Triangle)				.FullName,
									typeof(DrawingTools.Polygon)				.FullName,
									"NinjaTrader.NinjaScript.DrawingTools.OrderFlowVolumeProfile",
									"NinjaTrader.NinjaScript.DrawingTools.OrderFlowVWAPDrawingTool",
									typeof(DrawingTools.Arc)					.FullName,
									typeof(DrawingTools.Text)					.FullName,
									typeof(DrawingTools.ArrowUp)				.FullName,
									typeof(DrawingTools.ArrowDown)				.FullName,
									typeof(DrawingTools.Diamond)				.FullName,
									typeof(DrawingTools.Dot)					.FullName,
									typeof(DrawingTools.Square)					.FullName,
									typeof(DrawingTools.TriangleUp)				.FullName,
									typeof(DrawingTools.TriangleDown)			.FullName
								};

			List<XElement> ret = new();
			foreach (string s in ordered)
			{
				XElement c = elements.Element(s);
				if (c != null)
				{
					ret.Add(XElement.Parse(c.ToString()));
					c.Remove();
				}
			}

			ret.AddRange(elements.Elements());

			return ret;
		}

		#region Properties

		[Browsable(false)]
		public double Top { get; set; }
		[Browsable(false)]
		public double Left { get; set; }

		[Display(ResourceType = typeof(Custom.Resource), Name = "NinjaScriptIsVisibleOnlyFocused", GroupName = "NinjaScriptIndicatorVisualGroup", Order = 499)]
		public bool IsVisibleOnlyFocused { get; set; }

		public XElement SelectedTypes { get; set; }
		[Range(1, int.MaxValue)]
		[Display(ResourceType = typeof(Custom.Resource), Name = "NinjaScriptNumberOfRows", GroupName = "NinjaScriptParameters", Order = 0)]
		public int NumberOfRows { get; set; }

		[Range(0, 100)]
		[Display(Name = "Background Opacity (%)", GroupName = "NinjaScriptParameters", Order = 1)]
		public double BackgroundOpacity { get; set; }

		[Display(Name = "Start Expanded", GroupName = "NinjaScriptParameters", Order = 2)]
		public bool IsExpanded { get; set; }

		[Display(Name = "Pen Stroke", Description = "Color, width, and dash style of the freehand pen", GroupName = "NinjaScriptParameters", Order = 3)]
		public Gui.Stroke PenStroke { get; set; }

		#endregion
	}

	public class DrawingToolTileProPropertyDescriptor : PropertyDescriptor
	{
		private readonly int		order;
		private readonly Type		type;

		public override AttributeCollection Attributes
		{
			get
			{
				Attribute[] attr	= new Attribute[1];
				attr[0]				= new DisplayAttribute { Name = DisplayName, GroupName = Custom.Resource.NinjaScriptDrawingTools, Order = order };

				return new AttributeCollection(attr);
			}
		}

		public DrawingToolTileProPropertyDescriptor(Type type, string displayName, int order) : base(type.FullName ?? "", null)
		{
			Name					= type.FullName ?? "";
			DisplayName				= displayName;
			this.order				= order;
			this.type				= type;
		}

		public	override	Type	ComponentType => typeof(DrawingToolTilePro);

		public	override	string	DisplayName { get; }

		public	override	bool	IsReadOnly => false;

		public	override	string	Name { get; }

		public	override	Type	PropertyType => typeof(bool);

		public	override	bool	CanResetValue(object component) => true;
		public	override	bool	ShouldSerializeValue(object component) => true;

		public	override	object	GetValue(object component) => (component as DrawingToolTilePro)?.SelectedTypes.Element(Name) != null;

		public override void ResetValue(object component) { }

		public override void SetValue(object component, object value)
		{
			if (component is not DrawingToolTilePro c)
				return;
			bool val = (bool) value;

			if (val && c.SelectedTypes.Element(Name) == null)
			{
				XElement toAdd = new(Name);
				toAdd.Add(new XAttribute("Assembly", Core.Globals.AssemblyRegistry.IsNinjaTraderCustomAssembly(type) ? "NinjaTrader.Custom" : type.Assembly.GetName().Name));
				c.SelectedTypes.Add(toAdd);
			}
			else if(!val && c.SelectedTypes.Element(Name) != null)
				c.SelectedTypes.Element(Name)?.Remove();
		}
	}

	public class DrawingToolTileProTypeConverter : TypeConverter
	{
		public override bool GetPropertiesSupported(ITypeDescriptorContext context) { return true; }

		public override PropertyDescriptorCollection GetProperties(ITypeDescriptorContext context, object component, Attribute[] attrs)
		{
			TypeConverter					tc								= component is IndicatorBase ? TypeDescriptor.GetConverter(typeof(IndicatorBase)) : TypeDescriptor.GetConverter(typeof(DrawingTools.DrawingTool));
			PropertyDescriptorCollection	propertyDescriptorCollection	= tc.GetProperties(context, component, attrs);

			if (propertyDescriptorCollection == null) 
				return null;

			PropertyDescriptorCollection properties	= new(null);

			foreach (PropertyDescriptor pd in propertyDescriptorCollection)
			{
				if (!pd.IsBrowsable || pd.IsReadOnly) continue;

				if (pd.Name is "IsAutoScale" or "DisplayInDataBox" or "MaximumBarsLookBack" or "Calculate" or "PaintPriceMarkers" or "Displacement" or "ScaleJustification")
					continue;

				if (pd.Name == "SelectedTypes")
				{
					int i = 1;
					foreach (Type type in Core.Globals.AssemblyRegistry.GetDerivedTypes(typeof(DrawingTools.DrawingTool)))
					{
						if (type.Assembly.CreateInstance(type.FullName ?? "") is not DrawingTools.DrawingTool { DisplayOnChartsMenus: true } tool)
							continue;
						DrawingToolTileProPropertyDescriptor descriptor = new(type, tool.Name, i);
						properties.Add(descriptor);
						i++;
					}
					continue;
				}

				properties.Add(pd);
			}
			return properties;
		}
	}
}
