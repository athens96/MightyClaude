using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using Point = Windows.Foundation.Point;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>
    /// The per-pane Mighty view: the pane header mode switch plus the
    /// canvas of blocks and edges. Every decision — which panes show the switch,
    /// the block list, titles, capsule and tooltip, zoom steps, selection and
    /// wheel routing, the result-files panel rules and the reduced-motion
    /// indicator — comes from MightyClaude.Core. This file only draws them.
    /// </summary>
    private sealed partial class PaneView
    {
        // ── pane header switch ────────────────────────────────────────────────

        private Button? modeDefaultButton, modeMightyButton;
        /// <summary>The Default | Mighty track (segmentTrack, r8, padding 2) and each side's chip (segmentOn, r6, shadow 0.12).</summary>
        private Border? modeSwitch;
        private Rectangle? modeDefaultChip, modeMightyChip;

        // ── canvas ────────────────────────────────────────────────────────────

        private Grid? graphHost;
        private readonly Canvas graphCanvas = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        private readonly TranslateTransform graphPan = new();
        private readonly ScaleTransform graphScale = new() { ScaleX = 1, ScaleY = 1 };
        private readonly TextBlock graphTotal = new() { TextSize = DesignMetrics.Type.Small, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        private Button? zoomOutButton, zoomResetButton, zoomInButton;
        private Border? graphViewport;
        /// <summary>The viewport's surface, which shows the resize cursor over a block's border (M/MightyGraphInteraction.swift:483-497).</summary>
        private GraphSurface? graphSurface;
        private ResizeEdges graphHoverEdges;
        /// <summary>The Win2D dots under the canvas, following its pan and zoom (M/MightyGraphView.swift:995-1015).</summary>
        private MightyDotGrid? graphDotGrid;
        /// <summary>The draft card's dashed accent edge, kept across redraws (M/MightyGraphView.swift:416).</summary>
        private Rectangle? graphDraftOutline;
        /// <summary>The selected block's 2pt accent edge, drawn over its card (M/MightyGraphView.swift:920-922).</summary>
        private Rectangle? graphSelectionOutline;
        /// <summary>
        /// The edges, as the Mac strokes them (M/MightyGraphView.swift:372-398): one path for the quiet ones and one for
        /// those into a running block, so two routes that share a stretch never darken it; and each edge as last
        /// drawn (the block it runs into, its route and its arrow head) for the design smoke.
        /// </summary>
        private readonly ShapePath graphQuietEdges = NewEdgeStroke(), graphLiveEdges = NewEdgeStroke();
        private readonly List<(string Target, bool Live, Point[] Route, Point[] Head)> graphEdgeRoutes = [];
        /// <summary>The running blocks' activity capsules (their vertical scales), by block, and the one pane timer that waves them all.</summary>
        private readonly Dictionary<string, ScaleTransform[]> graphActivitySets = [];
        private DispatcherTimer? graphActivityTimer;
        private double graphZoom = MightyGraphViewModel.ZoomDefault;
        private string? graphSelection;
        private readonly Dictionary<string, ScrollViewer> graphBodies = [];
        private readonly Dictionary<string, AgentTranscript> graphTranscripts = [];
        private bool graphDeferredDraw;
        private readonly Dictionary<string, Border> graphCards = [];
        /// <summary>The strip inside each resizable block's border, over its card (<see cref="GraphBand"/>).</summary>
        private readonly Dictionary<string, Border> graphBands = [];
        /// <summary>The pending plan's block in the diagram, and the plan the camera last went to.</summary>
        private string? graphPlanNodeId, graphAimedPlanKey;

        private sealed class GraphCardView
        {
            internal required Border Card;
            internal Grid? Body, Header;
            internal StackPanel? Content;
            internal TextBlock? Request;
            internal AgentTranscript? Transcript;
            internal Button? FitButton;
            /// <summary>The request's band under the header (tint × 0.055) and, rebuilt with the header, its parts the smoke reads.</summary>
            internal Border? RequestBand;
            internal Border? StatePill, Capsule;
            internal Canvas? Activity;
            /// <summary>The header's symbol, the selected block's scroll hint and the size control, rebuilt with the header.</summary>
            internal FrameworkElement? Symbol;
            internal TextBlock? ScrollHint;
            internal Button? Expand;
            /// <summary>The words an answerless block shows in place of its transcript (M/MightyGraphView.swift:557-560).</summary>
            internal TextBlock? Placeholder;
            /// <summary>The corner handle and its two arrows (M/MightyGraphView.swift:929-942).</summary>
            internal Border? Grip;
            internal FrameworkElement? GripSymbol;
            /// <summary>The draft's own parts (M/MightyGraphView.swift:404-420): its symbol, title, state word and preview.</summary>
            internal FrameworkElement? DraftSymbol;
            internal TextBlock? DraftTitle, DraftState;
            internal string Fingerprint = "";
            internal bool MeasureQueued;
        }
        private readonly Dictionary<string, GraphCardView> graphCardViews = [];
        private readonly Dictionary<string, (string Fingerprint, Rectangle? Halo, Rectangle Line, Action? Stop)> graphOutlineViews = [];
        private readonly Dictionary<string, (string Kind, Rectangle Line)> graphOutlines = [];
        private readonly Dictionary<string, string> graphBlockKinds = [];
        // Each card's header title as drawn, so the smoke can read which carry an agent mark.
        private readonly Dictionary<string, FrameworkElement> graphTitles = [];
        private bool graphDragging, graphDrawing, graphRefreshQueued;
        internal bool GraphDiagnosticsForSmoke;
        private int graphSmokeTraceCount;
        private void TraceGraphSmoke(string step)
        {
            // Fixture-only geometry/lifecycle events; never record transcript,
            // path, provider output, credentials or environment contents.
            if (owner.options.SmokeTest && GraphDiagnosticsForSmoke && graphSmokeTraceCount++ < 160)
                owner.options.TraceStartup("smoke:mightyGraph:event:" + step);
        }
        private Windows.Foundation.Point graphDragOrigin;
        private double graphDragPanX, graphDragPanY;
        /// <summary>
        /// The open result-files list (Core's rule) and each of the pane's own results' files, resolved once per result
        /// text and workspace (M/MightyGraphResultFilesView.swift:20-31).
        /// </summary>
        private readonly MightyGraphViewModel.ResultFilesPanel graphResultFiles = new();
        private readonly Dictionary<string, (int Key, List<ResultFiles.ResultFile> Files)> graphResultFilesByRun = [];
        private readonly HashSet<(string RunId, int Key)> graphResultFilesLooking = [];
        private string? graphResultFilesRunId => graphResultFiles.SelectedRunID;
        // The newest result card's drag (macOS MightyGraphView.resize): the layout
        // it started on carries the pane limit and window fit it is kept within.
        private MightyGraphLayout? graphLayout, graphResizeLayout;
        private string? graphLatestResultId;
        private GraphBlockSize? graphLiveResultSize;
        /// <summary>The plan block's size only while its drag is in progress, as <see cref="graphLiveResultSize"/>.</summary>
        private GraphBlockSize? graphLivePlanSize;
        private Button? graphFitResultButton;
        private bool graphResizing;
        private Windows.Foundation.Point graphResizeOrigin;
        private (double W, double H) graphResizeStart;
        /// <summary>
        /// The sides a drag moves (the corner handle's are the right and the bottom) and where the card stood when it began;
        /// and the corner across from them, as a point of the viewport, which the camera keeps where it was once the layout
        /// carries the new size (M/MightyGraphInteraction.swift:305-336).
        /// </summary>
        private ResizeEdges graphResizeEdges = ResizeEdges.BottomRight;
        private (double X, double Y) graphResizeCardOrigin;
        private (string NodeID, (double X, double Y) Anchor, (double X, double Y) Unit)? graphResizeAnchor, graphResizePin;
        // The follow camera (M/MightyCore/MightyGraphFollow.swift): the rule is Core's. The run ids as last drawn (null before
        // the first pass), the block it keeps above the composer, a camera target that has not landed yet (a null block is
        // the followed one) and the time of the user's last own move are what the pass after each layout gives it.
        private readonly MightyGraphFollow.Watch graphFollow = new();
        private List<string>? graphRunIds;
        private string? graphFollowTargetId;
        private (string? NodeID, bool AlignTop)? graphAim;
        private long graphUserMoveAt = long.MinValue / 2;
        private bool graphCameraPassQueued;
        /// <summary>
        /// The net under every camera rule (M/MightyCore/MightyGraphCamera.swift:245-288): a layout pass that left no block on
        /// screen under a camera nobody moved. A verdict withheld while the user was busy is asked for again, a few times.
        /// </summary>
        private readonly MightyGraphCamera.StrandedWatch graphStranded = new();
        private int graphStrandedRetries;
        private const int GraphStrandedRetries = 6;
        private IReadOnlyList<MightyGraphRun>? graphDrawnRuns;
        /// <summary>Wheel turns and the tail of a drag still belong to the user (M/MightyGraphInteraction.swift:244).</summary>
        private const int GraphUserMoveGrace = 500;
        // The newest result card's natural height (chrome, header and answer),
        // measured as drawn; the layout fits the card to it under its cap.
        private readonly Dictionary<string, double> graphResultHeights = [];

        /// Windows animation setting; the smoke overrides it to prove the static
        /// indicator. Null means "ask the system".
        internal static bool? AnimationsEnabledOverride;
        private static bool AnimationsEnabled =>
            AnimationsEnabledOverride ?? new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

        // ── construction ──────────────────────────────────────────────────────

        private bool graphAttached;

        /// <summary>
        /// Schedules the Mighty view. The pane's own grid and header only exist
        /// once the pane is in the visual tree, so the composer anchor's Loaded
        /// event is the first moment both can be reached; a pane that never
        /// shows the switch builds nothing at all.
        /// </summary>
        internal void AttachMightyView(FrameworkElement anchor)
        {
            if (owner.closing || owner.service.Snapshot.Sessions.FirstOrDefault(p => p.Id == id) is not { } pane ||
                !MightyGraphViewModel.ShowsModeSwitch(pane)) return;
            anchor.Loaded += (_, _) => BuildMightyView();
        }

        /// <summary>Builds the view now instead of waiting for the anchor to load.</summary>
        internal void EnsureMightyView() => BuildMightyView();

        private void BuildMightyView()
        {
            // Loaded can already be queued when a pane is closed. Never build
            // native controls or subscribe the removed view to live run events.
            if (graphAttached || !QueuePaneAlive || owner.service.Snapshot.Sessions.FirstOrDefault(p => p.Id == id) is not { } pane ||
                !MightyGraphViewModel.ShowsModeSwitch(pane) || Container.Child is not Grid grid || paneHeader is null) return;
            graphAttached = true;

            // The switch leads the header's controls (M/SessionPaneView.swift:251-256).
            var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xxs };
            modeDefaultButton = ModePill(MightyGraphViewModel.LocaleKeyDefault, "default", options, out modeDefaultChip);
            modeMightyButton = ModePill(MightyGraphViewModel.LocaleKeyMighty, "mighty", options, out modeMightyChip);
            modeSwitch = new Border { Child = options, Padding = new Thickness(DesignMetrics.Spacing.Xxs), CornerRadius = new CornerRadius(DesignMetrics.Radius.Row), Background = owner.brushes.SegmentTrack, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(modeSwitch, "mighty-mode-switch-" + id);
            paneHeaderControls.Children.Insert(0, modeSwitch);
            modeSwitch.SizeChanged += (_, _) => QueuePaneHeaderLayout();
            QueuePaneHeaderLayout();

            // The Mighty bar starts right under the header's rule (M/MightyGraphView.swift:175): the pane grid's row gap is taken back.
            graphHost = new Grid { RowSpacing = 0, Visibility = Visibility.Collapsed, Margin = new Thickness(0, -grid.RowSpacing, 0, 0) };
            graphHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            graphHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            graphHost.Children.Add(BuildGraphToolbar());

            graphCanvas.RenderTransform = new TransformGroup { Children = { graphScale, graphPan } };
            // The page surface edge to edge (no radius) with the dot grid under the blocks.
            var dots = graphDotGrid = new MightyDotGrid(owner.brushes.Brush(DesignToken.Line));
            var surface = graphSurface = new GraphSurface(); surface.Children.Add(dots.View); surface.Children.Add(graphCanvas);
            graphViewport = new Border { Child = surface, Background = owner.brushes.Brush(DesignToken.Page) };
            // Every camera move (pan, zoom, a re-aim, older requests loaded above) moves the dots with it.
            void FollowCamera() => dots.Follow(graphScale.ScaleX, graphPan.X, graphPan.Y);
            graphPan.RegisterPropertyChangedCallback(TranslateTransform.XProperty, (_, _) => FollowCamera());
            graphPan.RegisterPropertyChangedCallback(TranslateTransform.YProperty, (_, _) => FollowCamera());
            graphScale.RegisterPropertyChangedCallback(ScaleTransform.ScaleXProperty, (_, _) => FollowCamera());
            FollowCamera();
            // The canvas is larger than the pane; clip it so a panned block never
            // paints over the composer or the neighbouring pane.
            graphViewport.SizeChanged += (_, args) =>
            {
                TraceGraphSmoke($"viewport-size:{args.NewSize.Width:F2}x{args.NewSize.Height:F2}");
                graphViewport.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, args.NewSize.Width, args.NewSize.Height) };
                dots.Fit(args.NewSize.Width, args.NewSize.Height);
                QueueGraphRefresh();
            };
            AutomationProperties.SetAutomationId(graphViewport, "mighty-graph-" + id);
            AutomationProperties.SetName(graphViewport, Locale.Get(MightyGraphViewModel.LocaleKeyMighty));
            graphViewport.PointerWheelChanged += OnGraphWheel;
            graphViewport.PointerPressed += OnGraphPointerPressed;
            graphViewport.PointerMoved += OnGraphPointerMoved;
            graphViewport.PointerReleased += OnGraphPointerReleased;
            graphViewport.PointerCaptureLost += (_, _) =>
            {
                EndGraphDrag();
                if (graphResizing) { EndResultResize(MightyGraphLayout.ResizePhase.Cancelled, null); ShowGraphResizeCursor(ResizeEdges.None); }
            };
            // A captured resize keeps its cursor wherever the pointer goes.
            graphViewport.PointerExited += (_, _) => { if (!graphResizing) ShowGraphResizeCursor(ResizeEdges.None); };
            graphViewport.KeyDown += (_, args) =>
            {
                if (args.Key != Windows.System.VirtualKey.Escape) return;
                // Escape abandons a result drag first; the card goes back as it was.
                if (graphResizing) EndResultResize(MightyGraphLayout.ResizePhase.Cancelled, null);
                else ClearGraphSelection();
                args.Handled = true;
            };
            graphViewport.IsTabStop = true;
            // A pane moved to a hidden tab stops waving its capsules, and starts again when it shows.
            graphViewport.Loaded += (_, _) => { UpdateActivityTimer(); ScheduleOuroboros(); };
            graphViewport.Unloaded += (sender, _) => { if (!((FrameworkElement)sender).IsLoaded) { graphActivityTimer?.Stop(); StopOuroboros(); } };
            Grid.SetRow(graphViewport, 1); graphHost.Children.Add(graphViewport);
            BuildMightyTimeline(); BuildMightyStructure();
            Grid.SetRow(graphHost, 1); grid.Children.Add(graphHost);

            // A finished request records a graph run on this session; redraw the
            // canvas when one arrives, and drop the handler once the pane is gone.
            owner.service.RunEventReceived += OnGraphRunEvent;
            SetGraphZoom(graphZoom);
            RefreshMightyView(pane);
        }

        private void OnGraphRunEvent(RunEvent value)
        {
            if (value.SessionId != id) return;
            Container.DispatcherQueue.TryEnqueue(() =>
            {
                if (!QueuePaneAlive || owner.service.Snapshot.Sessions.FirstOrDefault(p => p.Id == id) is not { } current)
                {
                    owner.service.RunEventReceived -= OnGraphRunEvent;
                    return;
                }
                if (graphHost?.Visibility == Visibility.Visible) RefreshMightyView(current);
            });
        }

        /// <summary>
        /// The Mighty bar (M/MightyGraphView.swift:175-201): one row, padding <c>Inset.GraphBarH</c> by <c>Inset.GraphBarV</c>, its parts <c>Spacing.Md</c> apart, over a
        /// <c>line</c> rule. From the left: the Diagram | Timeline switch, the style title 12 bold <c>ink</c> (and
        /// the badge of a style that is not built in), then the 10pt <c>ink2</c> summary, which gives way first;
        /// at the right, on the diagram only, the zoom controls in <c>ink2</c>: out, NN% (38 wide) and in. A pane
        /// too narrow for the row keeps it one row (<see cref="FitGraphToolbar"/>).
        /// </summary>
        private Grid BuildGraphToolbar()
        {
            var b = owner.brushes;
            var bar = new Grid { ColumnSpacing = DesignMetrics.Spacing.Md, Padding = new Thickness(DesignMetrics.Inset.GraphBarH, DesignMetrics.Inset.GraphBarV, DesignMetrics.Inset.GraphBarH, DesignMetrics.Inset.GraphBarV), BorderThickness = new Thickness(0, 0, 0, DesignMetrics.Stroke.Line), BorderBrush = b.Brush(DesignToken.Line) };
            foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) bar.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            graphStyleHeader.Foreground = b.Brush(DesignToken.Ink);
            graphTotal.Foreground = b.Brush(DesignToken.Ink2);
            AutomationProperties.SetAutomationId(graphTotal, "mighty-tokens-" + id);
            var lead = graphToolbarLead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Md, VerticalAlignment = VerticalAlignment.Center };
            lead.Children.Add(BuildGraphPresentationSwitch()); lead.Children.Add(graphStyleHeader); lead.Children.Add(graphStyleBadge = BuildStyleSourceBadge());
            bar.Children.Add(lead);
            Grid.SetColumn(graphTotal, 1); bar.Children.Add(graphTotal);
            // The Mac's 13pt magnifying glasses stand Spacing.Md from the percentage. Each sits in a 22-wide button here, 4.5 clear
            // of it on either side, so the buttons are Spacing.Xs apart and the row reaches Spacing.Xs into the bar's padding:
            // the last glass ends where the Mac's does.
            var zoom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xs, Margin = new Thickness(0, 0, -DesignMetrics.Spacing.Xs, 0), VerticalAlignment = VerticalAlignment.Center };
            zoomOutButton = ZoomButton(MightySymbols.Create("minus.magnifyingglass", 13, null), Locale.Get(MightyGraphViewModel.LocaleKeyZoomOut), "mighty-zoom-out-" + id, () => SetGraphZoom(MightyGraphViewModel.ZoomOut(graphZoom)));
            // The Mac's Text takes the body font: 13, its digits of one width.
            var percent = new TextBlock { Text = MightyGraphViewModel.ZoomLabel(graphZoom), TextSize = DesignMetrics.Type.Body, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(percent, FontNumeralAlignment.Tabular);
            zoomResetButton = ZoomButton(percent, Locale.Get(MightyGraphViewModel.LocaleKeyZoomReset), "mighty-zoom-reset-" + id, () => SetGraphZoom(MightyGraphViewModel.ZoomDefault));
            zoomResetButton.Width = 38;
            zoomInButton = ZoomButton(MightySymbols.Create("plus.magnifyingglass", 13, null), Locale.Get(MightyGraphViewModel.LocaleKeyZoomIn), "mighty-zoom-in-" + id, () => SetGraphZoom(MightyGraphViewModel.ZoomIn(graphZoom)));
            zoom.Children.Add(zoomOutButton); zoom.Children.Add(zoomResetButton); zoom.Children.Add(zoomInButton);
            graphZoomControls = zoom;
            Grid.SetColumn(zoom, 2); bar.Children.Add(zoom);
            bar.SizeChanged += (_, args) => { TraceGraphSmoke($"toolbar-fit:{args.NewSize.Width:F2}"); FitGraphToolbar(); };
            graphToolbar = bar;
            return bar;
        }

        /// <summary>A zoom control: plain, 22 square (the percentage 38 wide), <c>ink2</c> (<c>ink3</c> while disabled), the subtle wash under the pointer.</summary>
        private Button ZoomButton(UIElement content, string name, string automationId, Action act)
        {
            var button = new Button { Content = content, MinWidth = 0, MinHeight = 0, Width = 22, Height = 22, Padding = new Thickness(0), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            owner.PaintPlainButton(button, owner.brushes.Transparent, owner.brushes.Subtle, ink: owner.brushes.Brush(DesignToken.Ink2), disabledInk: owner.brushes.Brush(DesignToken.Ink3));
            AutomationProperties.SetAutomationId(button, automationId); AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, name);
            button.Click += (_, _) => act();
            return button;
        }

        /// <summary>
        /// One side of the Default | Mighty switch (M/SessionPaneView.swift:306-324): the symbol and its word,
        /// 11 semibold, height 20, padding h8. The Mac's Label sets the symbol in a slot about 15 wide and
        /// the word 8 after it (the word starts 23pt behind the padding). The button draws no fill in any
        /// state; the chosen side's chip is the shadow-casting shape under it (<see cref="PaintModeOption"/>).
        /// </summary>
        private Button ModePill(string localeKey, string mode, Panel options, out Rectangle chip)
        {
            var text = Locale.Get(localeKey);
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Md, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(ModeSymbol(mode == "mighty"));
            content.Children.Add(new TextBlock { Text = text, TextSize = DesignMetrics.Type.Pill, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = content, MinWidth = 0, MinHeight = 0, Height = 20, Padding = new Thickness(DesignMetrics.Spacing.Md, 0, DesignMetrics.Spacing.Md, 0), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), BorderThickness = new Thickness(0) };
            owner.PaintPlainButton(button, owner.brushes.Transparent, owner.brushes.Transparent);
            AutomationProperties.SetAutomationId(button, "mighty-mode-" + mode + "-" + id); AutomationProperties.SetName(button, text); ToolTipService.SetToolTip(button, text);
            button.Click += async (_, _) => await SetAgentViewMode(mode);
            var caster = chip = CardShadow.Caster(DesignMetrics.Radius.Segment, CardShadow.SegmentChip, owner.brushes.SegmentOn); chip.Visibility = Visibility.Collapsed;
            // A caster's shadow is drawn over the caster itself (it would grey the chip by its 0.12), so the chip's
            // own face lies on top and only the shadow's rim shows; the face comes and goes with the caster.
            var face = new Rectangle { RadiusX = DesignMetrics.Radius.Segment, RadiusY = DesignMetrics.Radius.Segment, Fill = owner.brushes.SegmentOn, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            AutomationProperties.SetAccessibilityView(face, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            caster.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => face.Visibility = caster.Visibility);
            var cell = new Grid(); cell.Children.Add(chip); cell.Children.Add(face); cell.Children.Add(button); options.Children.Add(cell);
            return button;
        }

        /// <summary>
        /// The switch's two symbols, drawn (Segoe Fluent Icons has neither shape) in the Mac's 15×12 slot:
        /// <c>text.alignleft</c>, four lines left-aligned, long and short in turn, 11 wide; and
        /// <c>point.3.connected.trianglepath.dotted</c>, three small rings at a triangle's corners joined
        /// by dotted lines. Filled by <see cref="PaintModeOption"/>.
        /// </summary>
        private static Microsoft.UI.Xaml.Shapes.Path ModeSymbol(bool mighty)
        {
            var shape = new GeometryGroup { FillRule = FillRule.EvenOdd };
            void Dot(double x, double y, double radius) => shape.Children.Add(new EllipseGeometry { Center = new Windows.Foundation.Point(x, y), RadiusX = radius, RadiusY = radius });
            if (mighty)
            {
                // Rings and dots stand on whole points, so they stay sharp where one point is one pixel.
                foreach (var (x, y) in new[] { (3.5, 3.5), (11.5, 3.5), (7.5, 9.5) }) { Dot(x, y, 1.75); Dot(x, y, .6); }
                foreach (var (x, y) in new[] { (6.5, 3.5), (8.5, 3.5), (5.5, 6.5), (9.5, 6.5) }) Dot(x, y, .55);
            }
            else
                for (var row = 0; row < 4; row++) shape.Children.Add(new RectangleGeometry { Rect = new Windows.Foundation.Rect(2, 1 + row * 3, row % 2 == 0 ? 11 : 7, 1.3) });
            var symbol = new Microsoft.UI.Xaml.Shapes.Path { Data = shape, Width = 15, Height = 12, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAccessibilityView(symbol, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            return symbol;
        }

        /// <summary>The chosen side shows its chip and <c>ink</c>; the other keeps the muted <c>ink2</c>.</summary>
        private void PaintModeOption(Button button, Rectangle chip, bool selected)
        {
            chip.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            var ink = owner.brushes.Brush(selected ? DesignToken.Ink : DesignToken.Ink2);
            foreach (var part in ((Panel)button.Content).Children)
                if (part is TextBlock words) words.Foreground = ink; else if (part is Microsoft.UI.Xaml.Shapes.Shape symbol) symbol.Fill = ink;
        }

        /// <summary>Whether the switch shows its words; it does at every width, as on the Mac (the design smoke holds it to that).</summary>
        private bool ModeWordsShown => modeDefaultButton?.Content is Panel { Children: [_, TextBlock { Visibility: Visibility.Visible }] };

        /// Saves the pane's view mode. The run keeps running and the composer
        /// draft is never touched — only AgentViewMode changes, for this pane.
        internal Task SetAgentViewMode(string mode) =>
            owner.Act(async () => { await Change(p => MightyGraphViewModel.ApplyViewMode(p, mode)); Refresh(); RefreshMightyView(Session); });

        // ── refresh ───────────────────────────────────────────────────────────

        /// <summary>Redraws only while the canvas is the visible view.</summary>
        internal void RefreshDraftBlock()
        {
            if (graphHost?.Visibility == Visibility.Visible) RefreshMightyView(Session);
        }

        /// <summary>Draws the pane in whichever view mode it is saved in.</summary>
        internal void RefreshMightyView(RunSession pane)
        {
            if (graphHost is null || modeDefaultButton is null || modeMightyButton is null) return;
            var mighty = pane.AgentViewMode == "mighty";
            PaintModeOption(modeDefaultButton, modeDefaultChip!, !mighty); PaintModeOption(modeMightyButton, modeMightyChip!, mighty);
            // A diagram shown again draws its dots once: a theme or camera change while it was hidden left them stale.
            var diagramWasHidden = graphHost.Visibility != Visibility.Visible || graphViewport?.Visibility != Visibility.Visible;
            graphHost.Visibility = mighty ? Visibility.Visible : Visibility.Collapsed;
            output.View.Visibility = mighty ? Visibility.Collapsed : Visibility.Visible;
            var presentation = mighty ? MightyTimeline.Mode(pane) : "diagram";
            var timeline = presentation == "timeline"; var structure = presentation == "structure";
            RefreshGraphPresentationSwitch(presentation);
            if (graphViewport is not null) graphViewport.Visibility = timeline || structure ? Visibility.Collapsed : Visibility.Visible;
            if (mighty && !timeline && !structure && diagramWasHidden) graphDotGrid?.Redraw();
            if (timelineScroll is not null) timelineScroll.Visibility = timeline ? Visibility.Visible : Visibility.Collapsed;
            ShowMightyStructure(structure);
            // The diagram's executions are read only while it shows; a read on its way stops (M/MightyGraphView.swift:320-321).
            if (!mighty || timeline || structure) StopOuroboros();
            // The run-id rules are watched in every view, so a send from here is followed when the diagram shows again.
            if (mighty && (timeline || structure)) ObserveGraphRuns(pane);
            if (mighty && timeline) DrawMightyTimeline(pane);
            else if (mighty && structure) DrawMightyStructure(pane);
            else if (mighty)
            {
                // Back to the diagram, a following camera shows the newest block again (M/MightyGraphView.swift:317-319).
                if (diagramWasHidden && graphRunIds is not null && graphFollow.Following) PublishGraphFollow();
                DrawGraph(pane);
            }
            // The view changed: a waiting plan is docked or drawn by the same rule, and answered plans show
            // as the strip only outside the diagram (MainWindow.PlanCard.cs).
            RedecidePlanPlace(); RefreshPlanHistory(Session); RefreshBackgroundWork(Session);
            UpdateActivityTimer();
        }

        /// Rebuilds the canvas from the pane's recorded graph runs. The block and
        /// edge list, every string and the indicator choice all come from Core.
        private void DrawGraph(RunSession pane)
        {
            TraceGraphSmoke($"draw-enter:reentrant={graphDrawing}:cards={graphCards.Count}");
            // Drawing resizes the canvas, which can raise SizeChanged again.
            if (graphDrawing) return;
            if (graphTranscripts.Values.Any(transcript => transcript.IsSelecting)) { graphDeferredDraw = true; return; }
            graphDrawing = true;
            try { DrawGraphCore(pane); } finally { graphDrawing = false; TraceGraphSmoke("draw-exit"); }
        }

        private void QueueGraphRefresh()
        {
            if (graphRefreshQueued || !QueuePaneAlive) return;
            graphRefreshQueued = true;
            if (!Container.DispatcherQueue.TryEnqueue(() =>
            {
                graphRefreshQueued = false;
                TraceGraphSmoke("queued-refresh");
                if (QueuePaneAlive && graphHost?.Visibility == Visibility.Visible &&
                    owner.service.Snapshot.Sessions.FirstOrDefault(p => p.Id == id) is { } current)
                    RefreshMightyView(current);
            })) graphRefreshQueued = false;
        }

        private void DrawGraphCore(RunSession pane)
        {
            // Older requests loaded from the session record sit above the retained
            // ones (macOS SessionPaneView: history.runs + retained).
            var retained = (IReadOnlyList<MightyGraphRun>)(pane.GraphRuns ?? []);
            var older = GraphHistoryRuns(pane, retained);
            var runs = (IReadOnlyList<MightyGraphRun>)[.. older, .. retained];
            // Each result card names its own request's files; the newest that names any opens its list by itself, once.
            var files = ResolveResultFiles(retained);
            graphResultFiles.Publish(retained.Where(run => files.ContainsKey(run.Id)).Select(run => (run.Id, files[run.Id].Count > 0)).ToList());

            // A viewport with no height yet is none: an aim waits for one it can land in.
            var viewport = GraphViewportSize;
            // A plan waiting for an answer is drawn where the request's result will go (MainWindow.PlanCard.cs).
            var planRunId = PlanCardSupport.DiagramPlanRunID(PendingPlan, true, runs);
            // Background executions the pane's agents started outlive the request that started them. Requests read back from the
            // session record are history: their executions are not polled again (M/MightyGraphView.swift:178-182).
            var executions = graphExecutionLinks = OuroborosExecutionLinks.Extract(retained);
            WatchOuroboros();
            var layout = GraphCanvasLayout(pane, older, retained, viewport, executions);
            graphDrawnRuns = runs;
            graphPlanNodeId = planRunId is null ? null : MightyGraphBlockSize.NodeId(planRunId, MightyGraphLayout.PlanSuffix);
            graphLayout = layout; graphLatestResultId = MightyGraphLayout.LatestResultID(runs);
            TraceGraphSmoke($"layout:nodes={layout.Nodes.Count}:size={layout.Size.W:F2}x{layout.Size.H:F2}");
            var catalog = owner.Runtime(pane.Provider)?.ModelCatalog?.Models;
            // "Request N · Claude": the short name, as macOS ProviderOptions.label, so its mark can go before it.
            var blocks = MightyGraphBlockModel.Blocks(layout, runs, pane.Draft, ProviderMark.Label(pane.Provider), AnimationsEnabled, catalog);
            for (var i = 0; i < runs.Count; i++)
            {
                var nodeId = MightyGraphLayout.NodeID(runs[i], "request");
                var at = blocks.FindIndex(block => block.Id == nodeId);
                if (at >= 0) blocks[at] = blocks[at] with { Title = GraphRequestTitle(runs[i], i + 1) };
            }
            for (var i = 0; i < blocks.Count; i++)
                if (blocks[i].Kind == MightyGraphLayout.ExecutionKind) blocks[i] = ExecutionBlock(blocks[i]);

            var live = blocks.Select(block => block.Id).ToHashSet();
            foreach (var stale in graphCards.Keys.Where(key => !live.Contains(key)).ToArray())
            {
                graphCards.Remove(stale); graphCardViews.Remove(stale); graphBodies.Remove(stale); graphTranscripts.Remove(stale); graphBands.Remove(stale);
                graphOutlines.Remove(stale);
                if (graphOutlineViews.Remove(stale, out var oldOutline)) oldOutline.Stop?.Invoke();
                graphBlockKinds.Remove(stale); graphTitles.Remove(stale); graphActivitySets.Remove(stale);
            }
            graphFitResultButton = null;
            var desired = new List<UIElement>();
            graphCanvas.Width = Math.Max(1, layout.Size.W); graphCanvas.Height = Math.Max(1, layout.Size.H);
            var frames = new Dictionary<string, GraphRect>();
            foreach (var block in blocks)
            {
                var frame = block.Frame with { X = block.Frame.X - layout.OriginX, Y = block.Frame.Y - layout.OriginY };
                frames[block.Id] = frame;
            }
            // Loading older requests grows the canvas upward: shift the pan by as
            // much so nothing already on screen moves.
            if (graphOriginY is { } previousOrigin && previousOrigin != layout.OriginY) ShiftGraphPan(0, (layout.OriginY - previousOrigin) * graphZoom);
            graphOriginY = layout.OriginY;
            // A tree wider than a request card moves the canvas's leading edge the same way (M/MightyCore/MightyGraphCamera.swift:33-42).
            if (graphOriginX is { } previousLeading && previousLeading != layout.OriginX) ShiftGraphPan((layout.OriginX - previousLeading) * graphZoom, 0);
            graphOriginX = layout.OriginX;
            // Edges (M/MightyGraphView.swift:372-398, M/MightyCore/MightyGraphLayout.swift:212-218): each leaves its block's
            // bottom centre, turns once on the way (26 from the block it joins, or from the one it leaves) and enters
            // the next block's top centre under an arrow head 4 to either side and 6 back.
            var running = blocks.Where(block => MightyGraphBlockModel.Tone(block) == DesignTone.Run).Select(block => block.Id).ToHashSet();
            graphEdgeRoutes.Clear();
            foreach (var edge in layout.Edges)
            {
                if (!frames.TryGetValue(edge.Source, out var from) || !frames.TryGetValue(edge.Target, out var to)) continue;
                var (start, end) = (new Point(from.MidX, from.MaxY), new Point(to.MidX, to.Y));
                var clearance = Math.Min(26, Math.Max(0, (end.Y - start.Y) / 2));
                var middle = edge.Joins ? end.Y - clearance : start.Y + clearance;
                graphEdgeRoutes.Add((edge.Target, running.Contains(edge.Target), [start, new(start.X, middle), new(end.X, middle), end], [new(end.X - 4, end.Y - 6), end, new(end.X + 4, end.Y - 6)]));
            }
            // The quiet edges are one ink2 stroke at 0.45 and 1.5pt; those into a running block one run stroke at 2pt.
            StrokeGraphEdges(graphQuietEdges, false, owner.brushes.Brush(DesignToken.Ink2, DesignMetrics.Opacity.Edge), DesignMetrics.Stroke.Focus);
            StrokeGraphEdges(graphLiveEdges, true, owner.brushes.Brush(DesignToken.Run), DesignMetrics.Stroke.Active);
            desired.Add(graphQuietEdges); desired.Add(graphLiveEdges);
            foreach (var block in blocks)
            {
                // Only a request block and its 실행 block say where they were read from (M/MightyGraphView.swift:507, 516).
                var fromRecord = LoadedFromRecord(block.Id, older);
                var card = BuildGraphCard(block, block.ResultFilesRunId is { } filesRun && files.TryGetValue(filesRun, out var named) ? named : [], pane, fromRecord && block.Kind is "request" or MightyGraphLayout.WorkKind);
                Canvas.SetLeft(card, frames[block.Id].X); Canvas.SetTop(card, frames[block.Id].Y);
                card.Width = frames[block.Id].W; card.Height = frames[block.Id].H;
                graphCards[block.Id] = card; graphBlockKinds[block.Id] = block.Kind; desired.Add(card);
                AddActivityOutline(block, frames[block.Id], desired);
                if (MightyGraphLayout.IsResizable(block.Kind)) desired.Add(GraphBand(block.Id, frames[block.Id]));
                if (block.Kind == "draft") desired.Add(DraftOutline(frames[block.Id]));
                if (fromRecord) ToolTipService.SetToolTip(card, Locale.Get("graph.history.tag"));
            }
            if (layout.Nodes.FirstOrDefault(n => n.Kind == "history") is { } history)
            {
                var card = BuildHistoryCard(pane, retained, older.Count, history.Frame);
                Canvas.SetLeft(card, history.Frame.X - layout.OriginX); Canvas.SetTop(card, history.Frame.Y - layout.OriginY);
                desired.Add(card);
            }
            // Keep every retained native document attached to its original
            // content panel, including between the first Measure and Loaded.
            // Geometry changes reorder z-layers, never remove/re-add cards.
            // The selection outline is not one of them: ApplyGraphSelectionStyle puts it on the canvas and takes it off.
            var wanted = desired.ToHashSet();
            for (var i = graphCanvas.Children.Count - 1; i >= 0; i--)
                if (!wanted.Contains(graphCanvas.Children[i]) && !ReferenceEquals(graphCanvas.Children[i], graphSelectionOutline)) graphCanvas.Children.RemoveAt(i);
            var attached = graphCanvas.Children.ToHashSet();
            for (var i = 0; i < desired.Count; i++)
            {
                Canvas.SetZIndex(desired[i], i);
                if (!attached.Contains(desired[i])) graphCanvas.Children.Add(desired[i]);
            }
            TraceGraphSmoke($"canvas-reconciled:retained={attached.Count}:desired={desired.Count}");
            if (graphLatestResultId is { } latestId && graphCardViews.TryGetValue(latestId, out var latestView)) QueueGraphCardMeasure(latestId, latestView);
            // The total counts the retained requests; loaded ones are named apart.
            graphTotal.Text = MightyGraphBlockModel.ToolbarSummary(retained)
                + (older.Count > 0 ? " · " + Locale.Get("graph.history.headerLoaded", new Dictionary<string, string> { ["count"] = older.Count.ToString() }) : "");
            var help = MightyGraphBlockModel.ToolbarHelp(retained);
            ToolTipService.SetToolTip(graphTotal, help); AutomationProperties.SetHelpText(graphTotal, help);
            ApplyGraphSelectionStyle();
            AimGraphCamera(runs, layout, viewport);
            AutoLoadGraphHistory(pane, retained);
            UpdateActivityTimer();
        }

        /// <summary>
        /// The diagram's own layout of the pane (M/MightyGraphView.swift:144): each request over its 실행 block, with the answered
        /// plans, pictures and executions hung beside the flow. The diagram draws it, and the 구조 view and the run-id rules ask it
        /// which blocks there are.
        /// </summary>
        private MightyGraphLayout GraphCanvasLayout(RunSession pane, IReadOnlyList<MightyGraphRun> older, IReadOnlyList<MightyGraphRun> retained, (double W, double H)? viewport, IReadOnlyList<OuroborosExecutionLink> executions)
        {
            var runs = (IReadOnlyList<MightyGraphRun>)[.. older, .. retained];
            // The newest result card takes its saved size (or the window fit) kept
            // within the pane at this zoom, so it shrinks with the pane and grows
            // back to the saved size when the pane does. Under that cap it is as
            // tall as its measured content; a drag in progress shows its own size.
            var latestResultId = MightyGraphLayout.LatestResultID(runs);
            double? resultContentHeight = graphLiveResultSize is null && latestResultId is not null && graphResultHeights.TryGetValue(latestResultId, out var measured) ? measured : null;
            // The run that keeps its place while loaded history grows above it and trimmed runs move into it (M/MightyGraphView.swift:167-169):
            // a trim that hands its runs to the history moves no block under the reader.
            var pinnedStart = older.Count > 0 && graphHistory.PinnedRunID is { } pinned && runs.Select(run => run.Id).ToList().IndexOf(pinned) is >= 0 and var kept ? kept : older.Count;
            return MightyGraphViewModel.CanvasLayout(runs, pane.Draft, pane.Status == "running", graphExpanded, graphResultFilesRunId, viewport,
                graphZoom, graphLiveResultSize ?? pane.GraphResultSize, pinnedStart, ShowsHistoryBlock(pane, retained), resultContentHeight, GraphBlockSizes(pane),
                PlanCardSupport.DiagramPlanRunID(PendingPlan, true, runs), PlanCardSupport.DiagramRecords(pane.PlanHistory, runs), graphLivePlanSize ?? pane.GraphPlanSize, splitsExecution: true,
                // The pictures a step produced hang beside it (M/MightyGraphView.swift:141); requests read back from the record never change.
                galleries: MightyGraphImages.Galleries(runs, Workspace.Path, older.Count), executions: executions.Select(link => (link.RunID, link.Key)).ToList());
        }

        /// <summary>A stroke that carries edges: round caps and joins, under every card, taking no input.</summary>
        private static ShapePath NewEdgeStroke()
        {
            var stroke = new ShapePath { StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
            AutomationProperties.SetAccessibilityView(stroke, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            return stroke;
        }

        /// <summary>Gives a stroke every quiet edge, or every edge into a running block: each edge's route and its arrow head, two open figures.</summary>
        private void StrokeGraphEdges(ShapePath stroke, bool live, Brush ink, double width)
        {
            var geometry = new PathGeometry();
            foreach (var edge in graphEdgeRoutes.Where(e => e.Live == live))
                foreach (var points in new[] { edge.Route, edge.Head })
                {
                    var figure = new PathFigure { StartPoint = points[0], IsFilled = false };
                    foreach (var point in points.Skip(1)) figure.Segments.Add(new LineSegment { Point = point });
                    geometry.Figures.Add(figure);
                }
            stroke.Data = geometry; stroke.Stroke = ink; stroke.StrokeThickness = width;
        }

        /// The newest result card's content measured a new height: the card fits
        /// it, and a following camera keeps its bottom where it was.
        private void ResultMeasured(string nodeId, double height)
        {
            TraceGraphSmoke($"result-measured:{height:F2}:previous={graphResultHeights.GetValueOrDefault(nodeId, -1):F2}");
            if (graphResizing || graphLiveResultSize is not null || !double.IsFinite(height)) return;
            if (graphResultHeights.TryGetValue(nodeId, out var known) && Math.Abs(known - height) <= 0.5) return;
            graphResultHeights.Clear(); graphResultHeights[nodeId] = height;
            QueueGraphRefresh();
        }

        /// <summary>
        /// The camera in node coordinates, as Core's rules have it. The canvas is drawn from the diagram's own
        /// leading edge and top, so the pan carries that origin (M/MightyCore/MightyGraphCamera.swift:33-42).
        /// </summary>
        private (double X, double Y) GraphCamera => (graphPan.X - (graphLayout?.OriginX ?? 0) * graphZoom, graphPan.Y - (graphLayout?.OriginY ?? 0) * graphZoom);
        private void SetGraphCamera((double X, double Y) camera)
        {
            if (!double.IsFinite(camera.X) || !double.IsFinite(camera.Y)) return;
            ShiftGraphPan(camera.X + (graphLayout?.OriginX ?? 0) * graphZoom - graphPan.X, camera.Y + (graphLayout?.OriginY ?? 0) * graphZoom - graphPan.Y);
        }

        /// <summary>
        /// Moves the pan other than by the background drag itself (a camera rule, the canvas's origin, a zoom, the wheel). A drag in
        /// progress sets the pan from where it began, so its base moves as much: the next pointer move neither undoes this nor jumps.
        /// </summary>
        private void ShiftGraphPan(double dx, double dy)
        {
            graphPan.X += dx; graphPan.Y += dy;
            if (graphDragging) { graphDragPanX += dx; graphDragPanY += dy; }
        }
        private (double W, double H)? GraphViewportSize => graphViewport is { ActualWidth: > 0, ActualHeight: > 0 } v ? (v.ActualWidth, v.ActualHeight) : null;
        private GraphRect? GraphNodeFrame(string? nodeId) => nodeId is null ? null : graphLayout?.Nodes.FirstOrDefault(n => n.Id == nodeId)?.Frame;

        /// <summary>Every re-aim the app makes (M/MightyGraphView.swift:341-347): where it lands decides whether the camera follows from there.</summary>
        private void PublishGraphAim(MightyGraphCamera.Anchor anchor)
        {
            if (anchor.NodeID is not { } nodeId) return;
            graphFollow.Stop();
            graphAim = (nodeId, anchor.AlignTop);
        }

        /// <summary>Follow from now on, the newest block right above the composer (M/MightyGraphView.swift:350-354).</summary>
        private void PublishGraphFollow() { graphFollow.Start(); graphAim = (null, false); }

        /// <summary>The user moved the camera, or an aim landed: it follows from here only if it shows the newest block (M/MightyGraphInteraction.swift:506-519).</summary>
        private void JudgeGraphFollow()
        {
            if (GraphViewportSize is { } size) graphFollow.Moved(graphFollowTargetId, GraphNodeFrame(graphFollowTargetId), GraphCamera, size, graphZoom);
        }

        /// <summary>
        /// The camera after a layout pass (M/MightyGraphView.swift:208-210, 276-279, 304-315; M/MightyGraphInteraction.swift:513-543).
        /// The first pass and a new request follow: the newest request's lowest block right above the composer. A plan that
        /// just arrived is aimed at; a trim under a reader who is not following re-aims by Core's rule. A target lands once
        /// there is a viewport and no drag or resize, and then a following camera keeps its block above the composer.
        /// </summary>
        private void AimGraphCamera(IReadOnlyList<MightyGraphRun> runs, MightyGraphLayout layout, (double W, double H)? viewport, bool retry = false)
        {
            var ids = runs.Select(r => r.Id).ToList();
            // A later pass over the diagram as drawn has no runs of its own to observe: those may be older than what another view saw since.
            if (!retry) ObserveGraphRuns(runs, () => layout);
            graphFollowTargetId = MightyGraphFollow.TargetID(layout, runs) ?? MightyGraphCamera.PendingNodeID;
            // A plan waiting for an answer is what the user has to read next: its top at the top of the canvas.
            var planKey = graphPlanNodeId is { } planNode ? planNode + "|" + PendingPlan?.Id : null;
            if (planKey is not null && planKey != graphAimedPlanKey) graphAim = (graphPlanNodeId, true);
            graphAimedPlanKey = planKey;
            if (viewport is not { } size) return;
            ReaimStrandedGraph(layout, ids.LastOrDefault(), size, retry);
            // A drag sets the pan itself on every move: the target waits for the release (EndGraphDrag).
            if (graphAim is { } aim && !graphResizing && !graphDragging)
            {
                var requested = GraphNodeFrame(aim.NodeID);
                // The block that was asked for, or the followed one when it is gone (M/MightyGraphView.swift:1028-1036).
                if ((requested ?? GraphNodeFrame(graphFollowTargetId)) is { } target)
                {
                    // The target owns the camera now; a pin left by a drag must not pull it back.
                    graphAim = null; graphResizePin = null;
                    SetGraphCamera(MightyGraphFollow.CameraOffset(target, size, graphZoom, requested is not null && aim.AlignTop, false, requested is null));
                    if (requested is null) graphFollow.Start(); else JudgeGraphFollow();
                }
            }
            // A finished block drag: once the layout carries the new size, the corner across from the dragged sides is put back where it was.
            if (graphResizePin is { } pin && !graphResizing)
            {
                var camera = GraphCamera;
                if (GraphNodeFrame(pin.NodeID) is not { } frame) graphResizePin = null;
                else if (ResizeEdgeRules.PinnedCamera(pin.Anchor, pin.Unit, frame, graphZoom) is var pinned && (Math.Abs(pinned.X - camera.X) > 0.01 || Math.Abs(pinned.Y - camera.Y) > 0.01))
                {
                    graphResizePin = null;
                    SetGraphCamera(pinned);
                }
            }
            FollowGraphCamera(size);
        }

        /// <summary>
        /// The run-id rules (Core's <see cref="MightyGraphFollow.RunsChanged"/>; M/MightyGraphView.swift:300-315), applied as the runs
        /// change in whichever view shows, so a send from the 구조 view or the timeline is followed first and a block revealed after
        /// it still wins. <paramref name="layout"/> is the diagram's layout of these runs, built only when a trim needs its blocks.
        /// </summary>
        private void ObserveGraphRuns(IReadOnlyList<MightyGraphRun> runs, Func<MightyGraphLayout> layout)
        {
            var ids = runs.Select(r => r.Id).ToList();
            var previous = graphRunIds; graphRunIds = ids;
            switch (MightyGraphFollow.RunsChanged(previous, ids, graphFollow.Following))
            {
                // Before anything aimed it, the camera follows.
                case MightyGraphFollow.RunChange.First: graphAim ??= (null, false); break;
                // A new request is the user's own send: follow it, its 실행 block and everything after it.
                case MightyGraphFollow.RunChange.Follow: PublishGraphFollow(); break;
                case MightyGraphFollow.RunChange.Trim: PublishGraphAim(MightyGraphCamera.TrimAnchor(previous!, ids, graphSelection, layout().Nodes.Select(n => n.Id).ToHashSet())); break;
            }
        }

        /// <summary>The run-id rules for the pane's runs as they stand, from a view that does not draw the diagram.</summary>
        private void ObserveGraphRuns(RunSession pane)
        {
            var retained = (IReadOnlyList<MightyGraphRun>)(pane.GraphRuns ?? []);
            var older = GraphHistoryRuns(pane, retained);
            ObserveGraphRuns([.. older, .. retained], () => GraphCanvasLayout(pane, older, retained, GraphViewportSize, OuroborosExecutionLinks.Extract(retained)));
        }

        /// <summary>
        /// The layout moved every block off screen while the camera stood still: a trim, a folded block, anything else that
        /// shortens the diagram (M/MightyGraphInteraction.swift:337-374, M/MightyGraphView.swift:365-379). Only a settled pass
        /// is judged; the reader goes back to the block they saw most of, and a following camera keeps its own block on screen.
        /// </summary>
        private void ReaimStrandedGraph(MightyGraphLayout layout, string? newestRunId, (double W, double H) size, bool retry)
        {
            if (!retry) graphStrandedRetries = GraphStrandedRetries;
            var settled = graphAim is null && !graphResizing && graphResizePin is null && !graphDragging && Environment.TickCount64 - graphUserMoveAt > GraphUserMoveGrace;
            // The pan carries the canvas's origin: rounded, the same camera reads the same after the origin moved under it.
            var camera = GraphCamera;
            var loss = graphStranded.Observe(layout.Nodes.Select(n => (n.Id, n.Frame)).ToList(), (Math.Round(camera.X, 2), Math.Round(camera.Y, 2)), graphZoom, size, newestRunId, settled);
            if (loss is null)
            {
                // The withheld pass may have been the last one of a burst, and then no later pass would come to judge it.
                if (graphStranded.IsWithholding && graphStrandedRetries-- > 0) QueueGraphCameraPass(GraphUserMoveGrace + 50);
                return;
            }
            if (graphFollow.Following) return;
            var nodes = layout.Nodes.Select(n => n.Id).ToHashSet();
            // Back to the block they were reading, not somewhere they never were.
            if (nodes.Contains(loss.LookedAtNodeID)) { PublishGraphAim(MightyGraphCamera.Anchor.Reaim(loss.LookedAtNodeID, false)); return; }
            var anchor = MightyGraphCamera.ReaimAnchor(newestRunId, graphSelection, nodes);
            // The net fires once per loss, so holding here would leave the blank.
            PublishGraphAim(anchor.NodeID is null && graphFollowTargetId is { } fallback && nodes.Contains(fallback) ? MightyGraphCamera.Anchor.Reaim(fallback, true) : anchor);
        }

        /// <summary>
        /// One follow pass (M/MightyGraphInteraction.swift:520-543): the newest block, a new viewport height or a block settling
        /// to its height moves a following camera. Never during a drag, a resize, a pending target or the user's own scroll:
        /// the pass waits for the user to be done.
        /// </summary>
        private void FollowGraphCamera((double W, double H) size)
        {
            if (!graphFollow.Following || graphResizing || graphDragging || graphAim is not null || graphResizePin is not null) return;
            var quiet = Environment.TickCount64 - graphUserMoveAt;
            if (quiet <= GraphUserMoveGrace) { QueueGraphCameraPass(GraphUserMoveGrace - quiet + 50); return; }
            if (graphFollow.Observe(graphFollowTargetId, GraphNodeFrame(graphFollowTargetId), GraphCamera, size, graphZoom) is { } camera) SetGraphCamera(camera);
        }

        /// <summary>Another camera pass over the diagram as drawn, once the user has been still for the grace.</summary>
        private void QueueGraphCameraPass(long milliseconds)
        {
            if (graphCameraPassQueued) return;
            graphCameraPassQueued = true;
            _ = Task.Delay(TimeSpan.FromMilliseconds(milliseconds)).ContinueWith(_ => Container.DispatcherQueue.TryEnqueue(() =>
            {
                graphCameraPassQueued = false;
                if (QueuePaneAlive && !graphDrawing && graphHost?.Visibility == Visibility.Visible && graphViewport?.Visibility == Visibility.Visible && graphLayout is { } layout && graphDrawnRuns is { } runs)
                    AimGraphCamera(runs, layout, GraphViewportSize, retry: true);
            }), TaskScheduler.Default);
        }

        /// <summary>The user's own move of the camera: nothing follows for the grace, and it follows on only at the newest block.</summary>
        private void UserMovedGraphCamera()
        {
            graphUserMoveAt = Environment.TickCount64; graphResizePin = null;
            JudgeGraphFollow();
        }
        /// <summary>
        /// The 1pt edge a block card draws inside itself. The Mac strokes it over the card's content
        /// (M/MightyGraphActivityView.swift:142-146), so what a block measures from its edge (the 38pt head, the 12 of
        /// its padding) is measured through this edge here.
        /// </summary>
        private const double BlockEdge = DesignMetrics.Stroke.Line;
        /// <summary>The corner handle's backing is <c>card</c> × 0.95 (M/MightyGraphView.swift:934).</summary>
        private const double GripOpacity = 0.95;

        /// <summary>
        /// One block on the D card (M/MightyGraphActivityView.swift:142-146): <c>card</c>, a 1pt <c>line</c>
        /// edge, radius 12, nothing inset, so the header strip and the request band run edge to edge. Under the
        /// 38pt header the request keeps its place on its tint × 0.055 band and the transcript scrolls in what is
        /// left. The draft is its own card (<see cref="BuildDraftCard"/>) with dashes for an edge; the files
        /// panel is <see cref="BuildResultFilesPanel"/>.
        /// </summary>
        private Border BuildGraphCard(MightyGraphBlock block, IReadOnlyList<ResultFiles.ResultFile> files, RunSession pane, bool fromRecord)
        {
            var b = owner.brushes;
            if (!graphCardViews.TryGetValue(block.Id, out var view))
            {
                var card = new Border
                {
                    CornerRadius = new CornerRadius(DesignMetrics.Radius.Block), BorderThickness = new Thickness(block.Kind is "draft" ? 0 : DesignMetrics.Stroke.Line),
                    BorderBrush = b.Brush(DesignToken.Line), Background = b.Brush(DesignToken.Card), Tag = block.Id,
                };
                view = new GraphCardView { Card = card }; graphCardViews[block.Id] = view;
                AutomationProperties.SetAutomationId(card, "mighty-node-" + block.Id);
                card.PointerPressed += (_, args) => PressGraphCard(block.Id, args);
                if (block.Kind == "draft") BuildDraftCard(view, block.Id);
                else if (block.Kind is "plan" or "planRecord")
                {
                    // The plan or the answered plan goes in under the corner handle, as every other block has it.
                    view.Body = new Grid(); view.Body.Children.Add(BuildResultResizeGrip(view, block.Id)); view.Card.Child = view.Body;
                }
                else if (block.Kind == "request") BuildRequestBody(view, block.Id);
                else if (block.Kind is not ("resultFiles" or MightyGraphLayout.ImagesKind or MightyGraphLayout.ExecutionKind)) BuildBlockBody(view, block.Id);
            }
            // Frame is deliberately excluded. Resizing or camera movement must
            // never tear down a RichEditBox while native text services load it.
            var look = block.Kind == "request" ? RequestStyleLook(block.Request) : (Symbol: "", Tint: "");
            var pictures = block.Kind == MightyGraphLayout.ImagesKind ? GraphImageItems(block) : [];
            var fingerprint = JsonSerializer.Serialize(new
            {
                block.Kind, block.Title, block.Request, block.Entries, block.State, block.Status, block.Indicator,
                block.Capsule, block.CapsuleHelp, block.ResultFilesRunId,
                Files = block.Kind is "result" or "resultFiles" ? files : [], Pictures = pictures.Select(picture => picture.Id), Execution = ExecutionState(block),
                FilesOpen = graphResultFilesRunId, FromRecord = fromRecord,
                Latest = graphLatestResultId == block.Id, SavedResult = pane.GraphResultSize is not null,
                SavedPlan = block.Kind == "plan" && pane.GraphPlanSize is not null,
                Fitted = graphLayout?.FittedResultID == block.Id, Expanded = graphExpanded.Contains(block.Id),
                // No theme: every colour is a shared brush recoloured in place, and the transcript takes a
                // theme change through RethemeMightyTranscripts, so a toggle rebuilds nothing. No selection
                // either: it shows through parts already in the card (ApplyGraphSelectionStyle).
                StyleSymbol = look.Symbol, StyleTint = look.Tint, Language = Locale.LanguagePreference,
                Plan = block.Kind == "plan" ? PlanBlockKey : block.Kind == "planRecord" ? PlanRecordBlockKey(block) : "",
            }, Wire.Json);
            if (view.Fingerprint == fingerprint)
            {
                if (block.Id == graphLatestResultId) graphFitResultButton = view.FitButton;
                return view.Card;
            }
            view.Fingerprint = fingerprint;
            AutomationProperties.SetName(view.Card, block.Title);
            if (block.Kind == "resultFiles") { view.Card.Child = BuildResultFilesPanel(block, files); return view.Card; }
            if (block.Kind == MightyGraphLayout.ImagesKind) { view.Card.Child = BuildImagesCard(block, pictures); return view.Card; }
            if (block.Kind == MightyGraphLayout.ExecutionKind) { view.Card.Child = BuildOuroborosCard(block, view); return view.Card; }
            if (block.Kind == "plan")
            {
                // The plan alone, the same for the whole wait: its document is never rebuilt, and its answers
                // wait over the composer (MainWindow.PlanCard.cs). It sits under the corner handle, the body's last child.
                var planBody = view.Body!;
                if (PendingPlan is { } plan)
                {
                    var planCard = PlanCard(plan, PendingCount, PlanPlace.Diagram);
                    if (!planBody.Children.Contains(planCard)) { Detach(planCard); planBody.Children.Insert(0, planCard); }
                }
                else if (planBody.Children.Count > 1) planBody.Children.RemoveAt(0);
                return view.Card;
            }
            if (block.Kind == "planRecord")
            {
                var recordBody = view.Body!;
                if (recordBody.Children.Count > 1) recordBody.Children.RemoveAt(0);
                if (BuildPlanRecordBlock(block) is { } row) recordBody.Children.Insert(0, row);
                return view.Card;
            }
            if (block.Kind == "draft")
            {
                // The draft itself, or the hint in the quiet ink while there is none (M/MightyGraphView.swift:1054-1055).
                var empty = block.Request.Length == 0;
                view.DraftTitle!.Text = block.Title; view.DraftState!.Text = block.State;
                view.Request!.Text = empty ? Locale.Get("graph.block.draftHint") : block.Request;
                view.Request.Foreground = b.Brush(empty ? DesignToken.Ink2 : DesignToken.Ink);
                graphTitles[block.Id] = view.DraftTitle;
                return view.Card;
            }
            var header = BuildGraphCardHeader(block, files, view, look, fromRecord);
            if (view.Header is not null) view.Body!.Children.Remove(view.Header);
            view.Header = header; view.Body!.Children.Insert(0, header);
            view.FitButton = block.Id == graphLatestResultId && pane.GraphResultSize is not null ? graphFitResultButton : null;
            var currentView = view;
            header.SizeChanged += (_, _) => QueueGraphCardMeasure(block.Id, currentView);
            view.Request!.Text = block.Request;
            view.RequestBand!.Visibility = block.Request.Length == 0 && block.Kind != "request" ? Visibility.Collapsed : Visibility.Visible;
            view.RequestBand.Background = b.Brush(BlockTint(block, look.Tint), DesignMetrics.Opacity.InputPreview);
            // The request alone: what was asked. What the agent did is the 실행 block below (M/MightyGraphView.swift:499-507).
            if (block.Kind == "request") return view.Card;
            if (block.Entries.Count > 0 && view.Transcript is null)
            {
                var transcript = new AgentTranscript { OpenReference = OpenReferencePreview, OpenImage = OpenTranscriptImage };
                if (owner.options.SmokeTest)
                {
                    transcript.View.Loaded += (_, _) => TraceGraphSmoke("transcript-loaded");
                    transcript.View.Unloaded += (_, _) => TraceGraphSmoke("transcript-unloaded");
                }
                transcript.SelectionEnded = () =>
                {
                    if (!graphDeferredDraw) return;
                    graphDeferredDraw = false;
                    QueueGraphRefresh();
                };
                transcript.View.MinHeight = 0;
                // The sides are the box's own padding; the space over the first line and under the last is written into the document.
                transcript.View.Margin = new Thickness(0); transcript.View.Padding = new Thickness(TranscriptInset.Left, 0, TranscriptInset.Right, 0);
                transcript.TopInset = TranscriptInset.Top; transcript.BottomInset = TranscriptInset.Bottom;
                view.Transcript = transcript; graphTranscripts[block.Id] = transcript;
                view.Content!.Children.Add(transcript.View);
            }
            if (view.Transcript is { } existing)
            {
                existing.View.Visibility = block.Entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                existing.Update(pane with { Logs = [.. block.Entries], Kind = "claude" }, !owner.DarkTheme, owner.pictures, Workspace.Path);
            }
            // A block with no answer says so: it has none, or it is still waiting for one (M/MightyGraphView.swift:557-560).
            view.Placeholder!.Visibility = block.Entries.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            view.Placeholder.Text = MightyGraphLayout.Terminal(block.Status) ? Locale.Get("graph.block.noResponse") : Locale.Get("graph.block.waitingResponse");
            return view.Card;
        }

        /// <summary>
        /// A block transcript's own inset (M/AgentTranscriptView.swift:35, 168, 372-380): <c>Inset.Transcript</c> from the card's
        /// sides and under the header, and at the bottom the 22pt corner handle more, so it never covers the last line.
        /// </summary>
        private static Thickness TranscriptInset => new(DesignMetrics.Inset.Transcript - BlockEdge, DesignMetrics.Inset.Transcript, DesignMetrics.Inset.Transcript - BlockEdge, DesignMetrics.Inset.Transcript + 22);

        /// <summary>
        /// What a block holds under its header (M/MightyGraphView.swift:551-564, 1025-1042): the request, which keeps
        /// its place on its tint × 0.055 band (padded by <c>Inset.GraphBlockBody</c>, 11pt, 14 to 64 tall and scrolling within,
        /// over a <c>line</c> rule), then the transcript, which scrolls in what is left, or the words that stand for one
        /// (12 <c>ink2</c>, padding <c>Inset.GraphBlockBodyH</c>); and the corner handle over them.
        /// </summary>
        private void BuildBlockBody(GraphCardView view, string blockId)
        {
            var b = owner.brushes;
            view.Body = new Grid();
            foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) }) view.Body.RowDefinitions.Add(new RowDefinition { Height = height });
            view.Request = new TextBlock { TextSize = DesignMetrics.Type.Pill, TextFont = TextArea.Transcript, TextWrapping = TextWrapping.Wrap, Foreground = b.Brush(DesignToken.Ink), MinHeight = 14 };
            view.RequestBand = new Border
            {
                Visibility = Visibility.Collapsed, Padding = new Thickness(DesignMetrics.Inset.GraphBlockBodyH - BlockEdge, DesignMetrics.Inset.GraphBlockBodyV, DesignMetrics.Inset.GraphBlockBodyH - BlockEdge, DesignMetrics.Inset.GraphBlockBodyV),
                BorderThickness = new Thickness(0, 0, 0, DesignMetrics.Stroke.Line), BorderBrush = b.Brush(DesignToken.Line),
                Child = new ScrollViewer { Content = view.Request, MaxHeight = 64, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled },
            };
            Grid.SetRow(view.RequestBand, 1); view.Body.Children.Add(view.RequestBand);
            view.Content = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            view.Placeholder = new TextBlock { TextSize = DesignMetrics.Type.Block, Foreground = b.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(DesignMetrics.Inset.GraphBlockBodyH - BlockEdge, DesignMetrics.Inset.GraphBlockBodyH, DesignMetrics.Inset.GraphBlockBodyH - BlockEdge, DesignMetrics.Inset.GraphBlockBodyH), Visibility = Visibility.Collapsed };
            view.Content.Children.Add(view.Placeholder);
            view.Content.SizeChanged += (_, _) => QueueGraphCardMeasure(blockId, view);
            view.Card.Loaded += (_, _) => QueueGraphCardMeasure(blockId, view);
            var scroll = new ScrollViewer { Content = view.Content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollMode = ScrollMode.Enabled };
            graphBodies[blockId] = scroll; Grid.SetRow(scroll, 2); view.Body.Children.Add(scroll);
            view.Body.Children.Add(BuildResultResizeGrip(view, blockId));
            view.Card.Child = view.Body;
        }

        /// <summary>
        /// What a request block holds under its header (M/MightyGraphView.swift:655-667): the request alone, on its tint × 0.055 band,
        /// which fills the card (padded by <c>Inset.GraphBlockBody</c>) down to its bottom corners and scrolls a long request in
        /// place; and the corner handle over it. No rule under it and nothing after it: the 실행 block below holds what the agent did.
        /// </summary>
        private void BuildRequestBody(GraphCardView view, string blockId)
        {
            var b = owner.brushes;
            view.Body = new Grid();
            foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star) }) view.Body.RowDefinitions.Add(new RowDefinition { Height = height });
            view.Request = new TextBlock { TextSize = DesignMetrics.Type.Pill, TextFont = TextArea.Transcript, TextWrapping = TextWrapping.Wrap, Foreground = b.Brush(DesignToken.Ink), MinHeight = 14 };
            var scroll = new ScrollViewer { Content = view.Request, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
            view.RequestBand = new Border
            {
                Padding = new Thickness(DesignMetrics.Inset.GraphBlockBodyH - BlockEdge, DesignMetrics.Inset.GraphBlockBodyV, DesignMetrics.Inset.GraphBlockBodyH - BlockEdge, DesignMetrics.Inset.GraphBlockBodyV),
                CornerRadius = new CornerRadius(0, 0, DesignMetrics.Radius.Block - BlockEdge, DesignMetrics.Radius.Block - BlockEdge), Child = scroll,
            };
            graphBodies[blockId] = scroll; Grid.SetRow(view.RequestBand, 1); view.Body.Children.Add(view.RequestBand);
            view.Body.Children.Add(BuildResultResizeGrip(view, blockId));
            view.Card.Child = view.Body;
        }

        /// <summary>
        /// The draft (M/MightyGraphView.swift:404-420, 1048-1063): padding <c>Spacing.Lg</c>, its parts <c>Spacing.Md</c> apart. "Next request" 12
        /// semibold <c>ink</c> after its pencil, the state word 11 <c>ink2</c> at the right; under them the draft, 12pt
        /// in at most four lines. No header rule, no size control, only the corner handle.
        /// </summary>
        private void BuildDraftCard(GraphCardView view, string blockId)
        {
            var b = owner.brushes; var ink = b.Brush(DesignToken.Ink);
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(view.DraftSymbol = MightySymbols.Create("square.and.pencil", 12, ink));
            label.Children.Add(view.DraftTitle = new TextBlock { TextSize = DesignMetrics.Type.Block, FontWeight = FontWeights.SemiBold, Foreground = ink, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            view.DraftState = new TextBlock { TextSize = DesignMetrics.Type.Pill, Foreground = b.Brush(DesignToken.Ink2), VerticalAlignment = VerticalAlignment.Center };
            var head = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(label); Grid.SetColumn(view.DraftState, 1); head.Children.Add(view.DraftState);
            view.Request = new TextBlock { TextSize = DesignMetrics.Type.Block, TextFont = TextArea.Transcript, TextWrapping = TextWrapping.Wrap, MaxLines = 4, TextTrimming = TextTrimming.CharacterEllipsis };
            var parts = new StackPanel { Spacing = DesignMetrics.Spacing.Md, Margin = new Thickness(DesignMetrics.Spacing.Lg) };
            parts.Children.Add(head); parts.Children.Add(view.Request);
            view.Body = new Grid(); view.Body.Children.Add(parts);
            view.Body.Children.Add(BuildResultResizeGrip(view, blockId));
            view.Card.Child = view.Body;
        }

        /// <summary>
        /// A theme change reaches the Mighty transcripts (diagram cards and timeline rows) in place; the
        /// cards, rows and their brushes stay as they are. Called from <see cref="Refresh"/>.
        /// </summary>
        internal void RethemeMightyTranscripts(bool light)
        {
            foreach (var transcript in graphTranscripts.Values.Concat(timelineTranscripts.Values)) transcript.Retheme(light);
        }

        /// <summary>
        /// The ink a block's icon and request band take (macOS <c>Palette.tint</c> and
        /// <c>agentPresentation</c>): a request its style's tint, a sub-agent its kind's ink.
        /// </summary>
        private static DesignToken BlockTint(MightyGraphBlock block, string requestTint) => block.Kind switch
        {
            "request" => TintToken(requestTint),
            "task" => DesignToken.TaskText, "steer" => DesignToken.SteerText, "compact" => DesignToken.CompactText,
            "question" => DesignToken.QuestionText, "agent" or MightyGraphLayout.WorkKind => DesignToken.AgentText,
            _ => DesignToken.Accent,
        };

        /// <summary>A style tint's token (M/GuidedActionChip.swift:8-20); "secondary" is the quiet ink.</summary>
        private static DesignToken TintToken(string? tint) => tint switch
        {
            "purple" => DesignToken.AgentText, "teal" => DesignToken.TaskText, "indigo" => DesignToken.QuestionText,
            "mint" => DesignToken.CompactText, "orange" => DesignToken.SteerText, "green" => DesignToken.DoneText,
            "red" => DesignToken.ErrText, "secondary" => DesignToken.Ink2, _ => DesignToken.Accent,
        };

        private void QueueGraphCardMeasure(string nodeId, GraphCardView view)
        {
            if (nodeId != graphLatestResultId || view.Content is null || view.Header is null || view.MeasureQueued) return;
            TraceGraphSmoke($"result-measure-queued:header={view.Header.ActualHeight:F2}:content={view.Content.ActualHeight:F2}");
            view.MeasureQueued = true;
            if (!Container.DispatcherQueue.TryEnqueue(() =>
            {
                view.MeasureQueued = false;
                TraceGraphSmoke($"result-measure-callback:loaded={view.Card.IsLoaded}:latest={nodeId == graphLatestResultId}");
                if (!QueuePaneAlive || nodeId != graphLatestResultId || !view.Card.IsLoaded || graphViewport?.Visibility != Visibility.Visible ||
                    !ReferenceEquals(graphCards.GetValueOrDefault(nodeId), view.Card) || view.Header.ActualHeight <= 0 ||
                    view.Content.Children.Any(child => child.Visibility == Visibility.Visible) && view.Content.ActualHeight <= 0) return;
                // The card insets nothing and its rows have no spacing: only its own 1pt edge is added.
                ResultMeasured(nodeId, view.Card.BorderThickness.Top + view.Card.BorderThickness.Bottom + view.Header.ActualHeight + view.Content.ActualHeight);
            })) view.MeasureQueued = false;
        }

        /// <summary>
        /// The symbol that heads a sub-agent's block, by kind (M/MightyGraphView.swift:91-100): a mid-run request, a
        /// context compaction, a question, a background task, or a sub-agent; and the 실행 block's two gears.
        /// </summary>
        private static string BlockSymbol(string kind) => kind switch
        {
            "steer" => "text.bubble", "compact" => "arrow.down.right.and.arrow.up.left", "question" => "questionmark.bubble.fill",
            "task" => "terminal", MightyGraphLayout.WorkKind => ExecutionSymbol, _ => "person.crop.square.filled.and.at.rectangle",
        };

        /// <summary>The 실행 block's glyph, in the diagram and on its timeline row (M/MightyGraphView.swift:577).</summary>
        private const string ExecutionSymbol = "gearshape.2";

        /// <summary>
        /// A block's <c>Layout.BlockHead</c> header (M/MightyGraphView.swift:483-550): padding <c>Inset.GraphBlockBodyH</c>, its parts <c>Spacing.Sm</c> apart, over a 1pt
        /// <c>line</c>. From the left: the block's symbol (13, in its tint), the title 12 bold <c>ink</c> (a
        /// request's with its agent's mark before the name) and the clock of a request read back from the session
        /// record. At the right: the selected block's scroll hint, the activity mark, the status pill, the usage
        /// capsule on <c>cardRaised</c>, the result's files toggle and "fit to window", and the expand control in
        /// <c>ink2</c>. The result's header is the outcome strip instead: <c>heroFill</c> of its tone with every
        /// word and symbol in <c>heroInk</c>, no pill and no rule (Core gives a result no usage capsule).
        /// </summary>
        private Grid BuildGraphCardHeader(MightyGraphBlock block, IReadOnlyList<ResultFiles.ResultFile> files, GraphCardView view, (string Symbol, string Tint) look, bool fromRecord)
        {
            var b = owner.brushes;
            var strip = block.Kind == "result";
            var tone = MightyGraphBlockModel.Tone(block);
            var ink = strip ? b.FillInk(tone) : b.Brush(DesignToken.Ink);
            var quiet = strip ? b.FillInk(tone) : b.Brush(DesignToken.Ink2);
            var header = new Grid
            {
                ColumnSpacing = DesignMetrics.Spacing.Sm, Padding = new Thickness(DesignMetrics.Inset.GraphBlockBodyH - BlockEdge, 0, DesignMetrics.Inset.GraphBlockBodyH - BlockEdge, 0),
                // Layout.BlockHead from the card's top, the card's own edge being the first point of it; a plain header ends in its rule.
                Height = DesignMetrics.Layout.BlockHead - BlockEdge + (strip ? 0 : DesignMetrics.Stroke.Line),
                BorderThickness = new Thickness(0, 0, 0, strip ? 0 : DesignMetrics.Stroke.Line), BorderBrush = b.Brush(DesignToken.Line),
            };
            if (strip)
            {
                // The Mac draws the strip as a shape of the card's own radius, round at all four corners
                // (docs/design-system/crops/result-card-*.webp); here it lies inside the card's 1pt edge.
                header.Background = b.Fill(tone); header.CornerRadius = new CornerRadius(DesignMetrics.Radius.Block - BlockEdge);
            }
            foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) header.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            var symbol = block.Kind switch
            {
                "request" => look.Symbol,
                "result" => tone == DesignTone.Err ? "exclamationmark.triangle" : tone == DesignTone.Stop ? "stop.circle" : "checkmark.seal",
                _ => BlockSymbol(block.Kind),
            };
            header.Children.Add(view.Symbol = MightySymbols.Create(symbol, 13, strip ? ink : b.Brush(BlockTint(block, look.Tint)), cut: b.Brush(DesignToken.Card)));
            // An SF Symbol's frame is wider than its drawing: the Mac's title starts 36 from the card's edge (docs/design-system/crops/result-card-*.webp).
            view.Symbol.Margin = new Thickness(0, 0, DesignMetrics.Spacing.Xxs, 0);
            // A request block's title ends with its agent's name; its mark goes before it.
            var title = ProviderMarkView.Labelled(block.Title, MightyGraphBlockModel.TitleProvider(block, Session.Provider), DesignMetrics.Type.Block, FontWeights.Bold);
            PaintWords(title, ink);
            graphTitles[block.Id] = title;
            // The Mac's Spacer(minLength: 3) stands between the title and what follows: 7 + 3 + 7 at the least.
            FrameworkElement named = title;
            if (fromRecord)
            {
                // Read back from the CLI's own session record, not kept by the app: a clock right after the title.
                var clock = MightySymbols.Create("clock.arrow.circlepath", 10, quiet);
                clock.IsHitTestVisible = true;
                ToolTipService.SetToolTip(clock, Locale.Get("graph.history.tag"));
                AutomationProperties.SetName(clock, Locale.Get("graph.history.tag")); AutomationProperties.SetAutomationId(clock, "mighty-record-" + block.Id);
                AutomationProperties.SetAccessibilityView(clock, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Content);
                var tagged = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm, VerticalAlignment = VerticalAlignment.Center };
                tagged.Children.Add(title); tagged.Children.Add(clock);
                named = tagged;
            }
            named.Margin = new Thickness(0, 0, DesignMetrics.Spacing.Md, 0);
            Grid.SetColumn(named, 1); header.Children.Add(named);

            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(view.ScrollHint = new TextBlock
            {
                Text = Locale.Get(MightyGraphViewModel.LocaleKeyBlockScrolling), TextSize = DesignMetrics.Type.Badge, FontWeight = FontWeights.Medium, Foreground = strip ? ink : b.Brush(DesignToken.Accent),
                VerticalAlignment = VerticalAlignment.Center, Visibility = graphSelection == block.Id ? Visibility.Visible : Visibility.Collapsed,
            });
            AutomationProperties.SetAutomationId(view.ScrollHint, "mighty-block-scroll-" + block.Id);
            view.Activity = null; view.StatePill = null; view.Capsule = null; view.Expand = null; graphActivitySets.Remove(block.Id);
            if (!strip && BuildIndicator(block.Indicator, view, block.Id) is { } indicator) right.Children.Add(indicator);
            if (!strip && block.State.Length > 0) right.Children.Add(view.StatePill = StatusPill(block.State, tone, 18));
            if (block.Capsule is { } capsule)
            {
                var pill = new Border
                {
                    CornerRadius = new CornerRadius(9), Padding = new Thickness(DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xxs, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xxs), VerticalAlignment = VerticalAlignment.Center,
                    Background = b.Brush(DesignToken.CardRaised),
                    Child = new TextBlock { Text = capsule, TextSize = DesignMetrics.Type.Small, TextFont = TextArea.Code, Foreground = quiet },
                };
                AutomationProperties.SetAutomationId(pill, "mighty-tokens-" + block.Id);
                AutomationProperties.SetName(pill, block.CapsuleHelp);
                ToolTipService.SetToolTip(pill, block.CapsuleHelp);
                right.Children.Add(view.Capsule = pill);
            }
            if (block.ResultFilesRunId is { } runId && files.Count > 0)
            {
                // The two documents and how many the result names; filled while their panel is open (M/MightyGraphView.swift:512-523).
                var open = graphResultFilesRunId == runId;
                var text = Locale.Get(open ? MightyGraphViewModel.LocaleKeyResultFilesClose : MightyGraphViewModel.LocaleKeyResultFilesOpen);
                var filesInk = strip ? ink : b.Brush(open ? DesignToken.Accent : DesignToken.Ink2);
                var face = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xxs, VerticalAlignment = VerticalAlignment.Center };
                face.Children.Add(MightySymbols.Create(open ? "doc.on.doc.fill" : "doc.on.doc", 13, filesInk));
                var count = new TextBlock { Text = files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), TextSize = DesignMetrics.Type.Small, Foreground = filesInk, VerticalAlignment = VerticalAlignment.Center };
                Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(count, FontNumeralAlignment.Tabular);
                face.Children.Add(count);
                var toggle = HeaderButton(face, filesInk);
                AutomationProperties.SetAutomationId(toggle, "mighty-result-files-toggle-" + block.Id);
                AutomationProperties.SetName(toggle, Locale.Get(MightyGraphViewModel.LocaleKeyResultFilesCount, new Dictionary<string, string> { ["count"] = files.Count.ToString() }));
                ToolTipService.SetToolTip(toggle, text);
                toggle.Click += (_, _) => ToggleResultFiles(runId);
                right.Children.Add(toggle);
            }
            // A saved size is in force: offer the way back to the window fit.
            if (block.Kind == "result" && block.Id == graphLatestResultId && Session.GraphResultSize is not null)
            {
                var label = Locale.Get(MightyGraphViewModel.LocaleKeyResultFitToWindow);
                var fit = HeaderButton(new TextBlock { Text = label, TextSize = DesignMetrics.Type.Small, FontWeight = strip ? FontWeights.Bold : FontWeights.Normal }, strip ? ink : b.Brush(DesignToken.Accent));
                AutomationProperties.SetAutomationId(fit, "mighty-fit-result-" + block.Id);
                AutomationProperties.SetName(fit, label);
                ToolTipService.SetToolTip(fit, label);
                fit.Click += async (_, _) => await FitResultToWindow();
                right.Children.Add(fit); graphFitResultButton = fit;
            }
            AddGraphBlockSizeControls(right, block, quiet, view);
            Grid.SetColumn(right, 2); header.Children.Add(right);
            return header;
        }

        /// <summary>Every word of a title line (one text, or the head and name around an agent mark) in one ink.</summary>
        private static void PaintWords(FrameworkElement line, Brush ink, TextArea? font = null)
        {
            IEnumerable<TextBlock> all = line switch { TextBlock single => [single], Panel panel => panel.Children.OfType<TextBlock>(), _ => [] };
            foreach (var words in all)
            {
                words.Foreground = ink;
                if (font is { } area) words.TextFont = area;
            }
        }

        /// <summary>
        /// A plain header button (the Mac's <c>.plain</c>): no fill at rest, the subtle wash under the pointer, in
        /// <paramref name="ink"/>. The Mac's is as wide as what it shows, so this one takes only that much room in
        /// its row: the <c>Spacing.Xs</c> the wash reaches round the symbol or word is given back by its margin.
        /// </summary>
        private Button HeaderButton(UIElement content, Brush ink)
        {
            const double reach = DesignMetrics.Spacing.Xs;
            var button = new Button
            {
                Content = content, MinWidth = 0, MinHeight = 0, Height = 22, Padding = new Thickness(reach, 0, reach, 0), Margin = new Thickness(-reach, 0, -reach, 0),
                CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
            };
            owner.PaintPlainButton(button, owner.brushes.Transparent, owner.brushes.Subtle, ink: ink);
            return button;
        }

        /// <summary>
        /// The running block's activity mark (M/MightyGraphActivityView.swift:46-77), in an 18×14 box: four
        /// <c>run</c> capsules in a wave while it runs; a still <c>run</c> bolt (10) with Windows animations off;
        /// the circled <c>waitText</c> pause (12) while it waits; nothing once it finished. Core makes the choice. The 보드
        /// view passes its own <paramref name="sets"/> and no card view.
        /// </summary>
        private FrameworkElement? BuildIndicator(string indicator, GraphCardView? view, string blockId, Dictionary<string, ScaleTransform[]>? sets = null)
        {
            FrameworkElement Boxed(FrameworkElement symbol)
            {
                symbol.HorizontalAlignment = HorizontalAlignment.Center;
                var box = new Grid(); box.Children.Add(symbol);
                return box;
            }
            FrameworkElement? mark = indicator switch
            {
                "animating" => ActivityBars(blockId, sets ?? graphActivitySets),
                "static" => Boxed(MightySymbols.Create("bolt.fill", 10, owner.brushes.Brush(DesignToken.Run))),
                "waiting" => Boxed(MightySymbols.Create("pause.circle", 12, owner.brushes.Brush(DesignToken.WaitText))),
                _ => null,
            };
            if (mark is null) return null;
            if (view is not null) view.Activity = mark as Canvas;
            mark.Width = MightyGraphActivity.BarBoxWidth; mark.Height = MightyGraphActivity.BarBoxHeight;
            mark.VerticalAlignment = VerticalAlignment.Center; mark.IsHitTestVisible = false;
            // The status pill beside it carries the state.
            AutomationProperties.SetAccessibilityView(mark, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            return mark;
        }

        /// <summary>
        /// A status as a pill (M/MightyGraphActivityView.swift:125-138): 10 bold in the tone's
        /// <c>text</c> ink on its <c>soft</c> tint, padding h7, <paramref name="height"/> tall
        /// (18 on a block, 19 on a timeline row, 20 on a timeline header), a capsule.
        /// </summary>
        private Border StatusPill(string text, DesignTone tone, double height) => new()
        {
            Height = height, CornerRadius = new CornerRadius(height / 2), Padding = new Thickness(DesignMetrics.Spacing.Sm, 0, DesignMetrics.Spacing.Sm, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = owner.brushes.Soft(tone),
            Child = new TextBlock { Text = text, TextSize = DesignMetrics.Type.Small, FontWeight = FontWeights.Bold, Foreground = owner.brushes.Text(tone), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
        };

        /// <summary>
        /// The four capsules (Core <see cref="MightyGraphActivity.BarHeight"/>): 3 wide, 2 apart, each drawn
        /// <see cref="MightyGraphActivity.BarTallest"/> tall, centred in the box and scaled down about its
        /// middle. The pane's one timer (<see cref="UpdateActivityTimer"/>) moves only the scales, so a tick
        /// costs no layout, and the card around them never sees the clock.
        /// </summary>
        private Canvas ActivityBars(string blockId, Dictionary<string, ScaleTransform[]> sets)
        {
            var box = new Canvas();
            var scales = new ScaleTransform[MightyGraphActivity.Bars];
            for (var i = 0; i < scales.Length; i++)
            {
                scales[i] = new ScaleTransform();
                var bar = new Border
                {
                    Width = MightyGraphActivity.BarWidth, Height = MightyGraphActivity.BarTallest, CornerRadius = new CornerRadius(MightyGraphActivity.BarWidth / 2),
                    Background = owner.brushes.Brush(DesignToken.Run), RenderTransformOrigin = new Windows.Foundation.Point(.5, .5), RenderTransform = scales[i],
                };
                Canvas.SetLeft(bar, i * (MightyGraphActivity.BarWidth + MightyGraphActivity.BarGap));
                Canvas.SetTop(bar, (MightyGraphActivity.BarBoxHeight - MightyGraphActivity.BarTallest) / 2);
                box.Children.Add(bar);
            }
            WaveActivity(scales, ActivityPhase());
            sets[blockId] = scales;
            return box;
        }

        /// <summary>The wave's phase now, from the system clock, so every running block waves in step.</summary>
        private static double ActivityPhase() => MightyGraphActivity.Phase(TimeSpan.FromMilliseconds(Environment.TickCount64), true);

        private static void WaveActivity(ScaleTransform[] scales, double phase)
        {
            for (var i = 0; i < scales.Length; i++) scales[i].ScaleY = MightyGraphActivity.BarHeight(i, phase) / MightyGraphActivity.BarTallest;
        }

        /// <summary>
        /// Runs the pane's one 24 fps capsule timer only while the diagram or the 보드 view shows (its host and view
        /// visible and loaded) and at least one running block or card of it has capsules; stops it otherwise.
        /// </summary>
        private void UpdateActivityTimer()
        {
            var shown = graphHost?.Visibility == Visibility.Visible;
            var wave = shown && (graphActivitySets.Count > 0 && graphViewport is { Visibility: Visibility.Visible, IsLoaded: true }
                || structureActivitySets.Count > 0 && structureView is { Visibility: Visibility.Visible, IsLoaded: true });
            if (!wave) { graphActivityTimer?.Stop(); return; }
            if (graphActivityTimer is null)
            {
                graphActivityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1 / MightyGraphActivity.BarFramesPerSecond) };
                graphActivityTimer.Tick += (_, _) =>
                {
                    var phase = ActivityPhase();
                    foreach (var scales in (structureShown ? structureActivitySets : graphActivitySets).Values) WaveActivity(scales, phase);
                };
            }
            if (!graphActivityTimer.IsEnabled) graphActivityTimer.Start();
        }

        /// <summary>
        /// The edge of a block in motion, drawn over its card (macOS MightyGraphActivityOutline):
        /// a running block's 2pt <c>run</c> line of 9/7 dashes walks one period per 1.6 s round the
        /// card, 1pt inside it at radius 11, over the 4pt <c>runSoft</c> halo outside it; still and
        /// solid with Windows animations off; a waiting block keeps a still 2pt <c>wait</c> line. The
        /// brushes are the window's shared ones, so a theme change recolours the same lines. Only the
        /// line's dash offset is animated: the card, its transcript and the layout never see the clock.
        /// </summary>
        private void AddActivityOutline(MightyGraphBlock block, GraphRect frame, List<UIElement> desired)
        {
            var outline = block.Outline;
            var fingerprint = $"{outline}|{frame.W:R}|{frame.H:R}";
            if (graphOutlineViews.TryGetValue(block.Id, out var old) && old.Fingerprint == fingerprint)
            {
                if (old.Halo is { } haloView)
                {
                    Canvas.SetLeft(haloView, frame.X - 2 * MightyGraphActivity.LineWidth); Canvas.SetTop(haloView, frame.Y - 2 * MightyGraphActivity.LineWidth);
                    desired.Insert(Math.Max(0, desired.Count - 1), haloView);
                }
                Canvas.SetLeft(old.Line, frame.X); Canvas.SetTop(old.Line, frame.Y); desired.Add(old.Line);
                return;
            }
            if (graphOutlineViews.Remove(block.Id, out var removed)) removed.Stop?.Invoke();
            graphOutlines.Remove(block.Id);
            if (outline == MightyGraphActivity.None) return;
            Rectangle? glow = null; Action? stop = null;
            if (MightyGraphActivity.HaloToken(outline) is { } halo)
            {
                var band = 2 * MightyGraphActivity.LineWidth;
                glow = ActivityHalo(halo, MightyGraphActivity.CornerRadius);
                glow.Width = frame.W + 2 * band; glow.Height = frame.H + 2 * band;
                Canvas.SetLeft(glow, frame.X - band); Canvas.SetTop(glow, frame.Y - band);
                desired.Insert(Math.Max(0, desired.Count - 1), glow);
            }
            var (line, story) = ActivityLine(outline, MightyGraphActivity.CornerRadius);
            line.Width = frame.W; line.Height = frame.H;
            AutomationProperties.SetAutomationId(line, "mighty-outline-" + block.Id);
            if (story is not null)
            {
                FitActivityMarch(line, story, frame.W, frame.H, MightyGraphActivity.CornerRadius);
                line.Loaded += (_, _) => story.Begin(); line.Unloaded += (_, _) => story.Stop();
                stop = story.Stop;
            }
            Canvas.SetLeft(line, frame.X); Canvas.SetTop(line, frame.Y);
            desired.Add(line);
            graphOutlines[block.Id] = (outline, line);
            graphOutlineViews[block.Id] = (fingerprint, glow, line, stop);
        }

        /// <summary>
        /// The soft halo outside a running card of radius <paramref name="corner"/>. Rectangle strokes sit inside their bounds: the
        /// halo is the 4pt band outside the card, its bounds the card grown by the band, its middle line the card's outline 2
        /// further out, radius <paramref name="corner"/> + 2 (M/MightyGraphActivityView.swift:112).
        /// </summary>
        private Rectangle ActivityHalo(DesignToken halo, double corner)
        {
            var band = 2 * MightyGraphActivity.LineWidth;
            return new Rectangle { RadiusX = corner + band / 2, RadiusY = corner + band / 2, Stroke = owner.brushes.Brush(halo), StrokeThickness = band, IsHitTestVisible = false };
        }

        /// <summary>
        /// The outline's 2pt line in its token, 1pt inside a card of radius <paramref name="corner"/> at that radius less 1, its
        /// dashes flat-capped; marching, the storyboard that walks them too, which <see cref="FitActivityMarch"/> fits to the card.
        /// </summary>
        private (Rectangle Line, Microsoft.UI.Xaml.Media.Animation.Storyboard? March) ActivityLine(string outline, double corner)
        {
            var line = new Rectangle
            {
                RadiusX = corner - 1, RadiusY = corner - 1,
                Stroke = owner.brushes.Brush(MightyGraphActivity.StrokeToken(outline)), StrokeThickness = MightyGraphActivity.LineWidth,
                StrokeDashCap = PenLineCap.Flat, IsHitTestVisible = false,
            };
            AutomationProperties.SetAccessibilityView(line, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            if (outline != MightyGraphActivity.Marching) return (line, null);
            var march = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                Duration = new Duration(MightyGraphActivity.Period),
                RepeatBehavior = Microsoft.UI.Xaml.Media.Animation.RepeatBehavior.Forever,
                EnableDependentAnimation = true,
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(march, line);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(march, "StrokeDashOffset");
            var story = new Microsoft.UI.Xaml.Media.Animation.Storyboard(); story.Children.Add(march);
            return (line, story);
        }

        /// <summary>
        /// The march fitted to a card of this size and radius: its 9/7 dashes and the offset that walks them one period per cycle,
        /// both in stroke widths as XAML counts them. Set on a stopped storyboard.
        /// </summary>
        private static void FitActivityMarch(Rectangle line, Microsoft.UI.Xaml.Media.Animation.Storyboard story, double width, double height, double corner)
        {
            var dash = MightyGraphActivity.DashForCard(width, height, corner);
            var dashes = new DoubleCollection();
            foreach (var length in DesignMetrics.Dash.InStrokeUnits(dash, MightyGraphActivity.LineWidth)) dashes.Add(length);
            line.StrokeDashArray = dashes;
            var offsets = DesignMetrics.Dash.InStrokeUnits([MightyGraphActivity.DashOffset(0, dash), MightyGraphActivity.DashOffset(1, dash)], MightyGraphActivity.LineWidth);
            var march = (Microsoft.UI.Xaml.Media.Animation.DoubleAnimation)story.Children[0];
            march.From = offsets[0]; march.To = offsets[1];
        }

        /// <summary>
        /// The draft's dashed edge (M/MightyGraphView.swift:419): <c>accent</c> × 0.6 at 1.5pt in
        /// [5, 4] pt dashes, which WinUI counts in stroke widths, stroked on the card's own radius-12 outline
        /// (half the line inside it, half outside). One line, moved with the draft.
        /// </summary>
        private Rectangle DraftOutline(GraphRect frame)
        {
            const double half = DesignMetrics.Dash.DraftWidth / 2;
            if (graphDraftOutline is null)
            {
                var dashes = new DoubleCollection();
                foreach (var length in DesignMetrics.Dash.InStrokeUnits(DesignMetrics.Dash.Draft, DesignMetrics.Dash.DraftWidth)) dashes.Add(length);
                graphDraftOutline = new Rectangle
                {
                    RadiusX = DesignMetrics.Radius.Block, RadiusY = DesignMetrics.Radius.Block,
                    Stroke = owner.brushes.Brush(DesignToken.Accent, DesignMetrics.Opacity.Draft), StrokeThickness = DesignMetrics.Dash.DraftWidth,
                    StrokeDashArray = dashes, StrokeDashCap = PenLineCap.Flat, IsHitTestVisible = false,
                };
                AutomationProperties.SetAccessibilityView(graphDraftOutline, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            }
            graphDraftOutline.Width = frame.W + 2 * half; graphDraftOutline.Height = frame.H + 2 * half;
            Canvas.SetLeft(graphDraftOutline, frame.X - half); Canvas.SetTop(graphDraftOutline, frame.Y - half);
            return graphDraftOutline;
        }

        // ── result files panel ────────────────────────────────────────────────

        /// <summary>
        /// The files a result names, beside its card (M/MightyGraphResultFilesView.swift:79-127). A <c>Layout.BlockHead</c> head,
        /// padding <c>Inset.GraphBlockBodyH</c>, its parts <c>Spacing.Sm</c> apart, over a <c>line</c> rule: the two documents in <c>accent</c>, the title
        /// 12 semibold, the count 10 <c>ink2</c> and the close cross in <c>ink2</c>. Under it the files, <c>Spacing.Xs</c> apart
        /// and <c>Spacing.Sm</c> from the card's edge: each a <c>cardRaised</c> row, radius 6, padding <c>Spacing.Sm</c>, the document 12 in
        /// <c>accent</c>, the name 12 medium over the path 10 mono <c>ink2</c> in at most two lines.
        /// </summary>
        private Grid BuildResultFilesPanel(MightyGraphBlock block, IReadOnlyList<ResultFiles.ResultFile> files)
        {
            var b = owner.brushes; var accent = b.Brush(DesignToken.Accent); var ink2 = b.Brush(DesignToken.Ink2);
            var panel = new Grid();
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            AutomationProperties.SetAutomationId(panel, "mighty-result-files-" + block.Id);
            var head = new Grid
            {
                ColumnSpacing = DesignMetrics.Spacing.Sm, Padding = new Thickness(DesignMetrics.Inset.GraphBlockBodyH - BlockEdge, 0, DesignMetrics.Inset.GraphBlockBodyH - BlockEdge, 0), Height = DesignMetrics.Layout.BlockHead - BlockEdge + DesignMetrics.Stroke.Line,
                BorderThickness = new Thickness(0, 0, 0, DesignMetrics.Stroke.Line), BorderBrush = b.Brush(DesignToken.Line),
            };
            foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) head.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            head.Children.Add(MightySymbols.Create("doc.on.doc", 13, accent));
            var title = new TextBlock { Text = Locale.Get(MightyGraphViewModel.LocaleKeyResultFilesTitle), TextSize = DesignMetrics.Type.Block, FontWeight = FontWeights.SemiBold, Foreground = b.Brush(DesignToken.Ink), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(title, 1); head.Children.Add(title);
            var count = new TextBlock { Text = files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), TextSize = DesignMetrics.Type.Small, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(count, 2); head.Children.Add(count);
            var closeName = Locale.Get(MightyGraphViewModel.LocaleKeyResultFilesClose);
            var close = HeaderButton(MightySymbols.Create("xmark", 13, ink2), ink2);
            AutomationProperties.SetAutomationId(close, "mighty-result-files-close-" + block.Id);
            AutomationProperties.SetName(close, closeName); ToolTipService.SetToolTip(close, closeName);
            close.Click += (_, _) => CloseResultFiles();
            Grid.SetColumn(close, 3); head.Children.Add(close);
            panel.Children.Add(head);
            var list = new StackPanel { Spacing = DesignMetrics.Spacing.Xs, Padding = new Thickness(DesignMetrics.Spacing.Sm - BlockEdge, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Sm - BlockEdge, DesignMetrics.Spacing.Sm + DesignMetrics.Spacing.Lg) };
            foreach (var file in files.Take(ResultFiles.MaximumResultFiles))
            {
                var face = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
                face.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); face.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var document = MightySymbols.Create("doc.text", 12, accent); document.VerticalAlignment = VerticalAlignment.Top; document.Margin = new Thickness(0, DesignMetrics.Spacing.Xxs, 0, 0);
                face.Children.Add(document);
                var words = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
                words.Children.Add(new TextBlock { Text = Path.GetFileName(file.Path), TextSize = DesignMetrics.Type.Block, FontWeight = FontWeights.Medium, Foreground = b.Brush(DesignToken.Ink), TextTrimming = TextTrimming.CharacterEllipsis });
                words.Children.Add(new TextBlock { Text = file.Path + (file.Line is { } line ? ":" + line : ""), TextSize = DesignMetrics.Type.Small, TextFont = TextArea.Code, Foreground = ink2, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(words, 1); face.Children.Add(words);
                var row = new Button { Content = face, MinWidth = 0, MinHeight = 0, Padding = new Thickness(DesignMetrics.Spacing.Sm), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                owner.PaintPlainButton(row, b.Brush(DesignToken.CardRaised), b.Brush(DesignToken.Track));
                AutomationProperties.SetAutomationId(row, "mighty-result-file-" + block.Id + "-" + file.Path);
                AutomationProperties.SetName(row, file.Path); ToolTipService.SetToolTip(row, file.Path);
                row.Click += async (_, _) => await OpenReferencePreview(file.Path, file.Line);
                list.Children.Add(row);
            }
            var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
            AutomationProperties.SetAutomationId(scroll, "mighty-result-files-scroll-" + block.Id);
            // An attachment beside the flow: the wheel scrolls it at once, with no selection (M/MightyGraphInteraction.swift:715).
            graphBodies[block.Id] = scroll;
            Grid.SetRow(scroll, 1); panel.Children.Add(scroll);
            return panel;
        }

        /// <summary>
        /// The files each of the pane's own completed requests names in its result (requests read back from the session
        /// record are not looked through), kept until that result's text or the workspace changes. A result not looked
        /// through yet is looked through off the UI thread, and the diagram draws again once it is (M/MightyGraphView.swift:288-290).
        /// </summary>
        private Dictionary<string, List<ResultFiles.ResultFile>> ResolveResultFiles(IReadOnlyList<MightyGraphRun> own)
        {
            var resolved = new Dictionary<string, List<ResultFiles.ResultFile>>();
            var finished = new HashSet<string>(StringComparer.Ordinal);
            var missing = new List<(MightyGraphRun Run, int Key)>();
            foreach (var run in own.Where(run => run.Status == "completed" && MightyGraphLayout.Finished(run)))
            {
                finished.Add(run.Id);
                var key = HashCode.Combine(Workspace.Path, run.ResultEntries.Count, run.ResultEntries.Sum(entry => (long)entry.Text.Length), run.ResultEntries.LastOrDefault()?.Id, run.FinalOutput?.Length);
                if (graphResultFilesByRun.TryGetValue(run.Id, out var kept) && kept.Key == key) resolved[run.Id] = kept.Files;
                else if (graphResultFilesLooking.Add((run.Id, key))) missing.Add((run.Copy(), key));
            }
            foreach (var stale in graphResultFilesByRun.Keys.Where(runId => !finished.Contains(runId)).ToArray()) graphResultFilesByRun.Remove(stale);
            if (missing.Count > 0) _ = LookThroughResultFiles(missing, Workspace.Path);
            return resolved;
        }

        /// <summary>Looks results through for the files they name, off the UI thread, and draws again with what was found.</summary>
        private async Task LookThroughResultFiles(List<(MightyGraphRun Run, int Key)> runs, string root)
        {
            List<(string Id, int Key, List<ResultFiles.ResultFile> Files)> found;
            try { found = await Task.Run(() => runs.Select(run => (run.Run.Id, run.Key, MightyGraphBlockModel.FilesFor(run.Run, root))).ToList()); }
            // A result that cannot be looked through names nothing, rather than being looked through again on every draw.
            catch (Exception ex) when (ex is not OutOfMemoryException) { found = [.. runs.Select(run => (run.Run.Id, run.Key, new List<ResultFiles.ResultFile>()))]; }
            foreach (var (runId, key, files) in found) { graphResultFilesLooking.Remove((runId, key)); graphResultFilesByRun[runId] = (key, files); }
            if (QueuePaneAlive) QueueGraphRefresh();
        }

        /// Both transcript paths and result-list entries open the same in-pane viewer.
        private Task OpenResultFile(string path) =>
            OpenReferencePreview(path);

        private void ToggleResultFiles(string runId)
        {
            graphResultFiles.Toggle(runId, graphResultFilesByRun.TryGetValue(runId, out var named) && named.Files.Count > 0);
            RefreshMightyView(Session);
        }

        /// A manual close sticks until a newer result names files.
        private void CloseResultFiles() { graphResultFiles.Close(); RefreshMightyView(Session); }

        // ── the newest result card's size ─────────────────────────────────────

        /// <summary>
        /// A block's corner handle (M/MightyGraphView.swift:928-942): the two resize arrows, 10 semibold in
        /// <c>ink2</c> (<c>accent</c> on the selected block), in a 22 square of <c>card</c> × 0.95, radius 5, 2
        /// from the card's corner. Dragging it sizes the block, as dragging a band along any of its four borders
        /// does (<see cref="GraphResizeTarget"/>); its menu and a double click put the size back.
        /// The viewport captures the pointer, so the drag goes on when the pointer leaves the handle.
        /// </summary>
        private Border BuildResultResizeGrip(GraphCardView view, string nodeId)
        {
            var b = owner.brushes;
            view.GripSymbol = MightySymbols.Create("arrow.up.left.and.arrow.down.right", 10, b.Brush(DesignToken.Ink2), MightySymbols.Weight.Semibold);
            view.GripSymbol.HorizontalAlignment = HorizontalAlignment.Center;
            // 2 from the card's corner, which the card's own edge is the first part of.
            var inset = DesignMetrics.Spacing.Xxs - view.Card.BorderThickness.Right;
            var grip = view.Grip = new Border
            {
                Width = 22, Height = 22, Margin = new Thickness(0, 0, inset, inset), CornerRadius = new CornerRadius(DesignMetrics.Radius.FileRow), Background = b.Brush(DesignToken.Card, GripOpacity),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Child = view.GripSymbol,
            };
            Grid.SetRowSpan(grip, 3);
            var reset = new MenuFlyoutItem { Text = Locale.Get("graph.block.resetSize") };
            AutomationProperties.SetAutomationId(reset, "mighty-reset-" + nodeId);
            reset.Click += async (_, _) => await ResetGraphBlockSize(nodeId);
            var menu = new MenuFlyout(); menu.Items.Add(reset); grip.ContextFlyout = menu;
            ToolTipService.SetToolTip(grip, Locale.Get("graph.block.resize"));
            AutomationProperties.SetAutomationId(grip, "mighty-resize-result-" + nodeId);
            // Only the left button drags; the right one leaves the press to the handle's menu.
            grip.PointerPressed += (_, args) => { if (LeftPress(args)) BeginGraphResize(nodeId, ResizeEdges.BottomRight, args); };
            grip.DoubleTapped += async (_, args) => { args.Handled = true; await ResetGraphBlockSize(nodeId); };
            AutomationProperties.SetName(grip, Locale.Get("graph.block.resize"));
            return grip;
        }

        /// <summary>
        /// The block and sides a press at a point of the viewport would resize (M/MightyGraphInteraction.swift:375-394):
        /// the corner handle first, then a band along each border, 6pt outside it at 100% and 2pt inside.
        /// </summary>
        private (string Id, GraphRect Frame, ResizeEdges Edges)? GraphResizeTarget(Windows.Foundation.Point point)
        {
            if (graphLayout is not { } layout || graphResizing) return null;
            var camera = GraphCamera;
            var frames = layout.Nodes.Where(n => MightyGraphLayout.IsResizable(n.Kind) && graphCards.ContainsKey(n.Id))
                .Select(n => (n.Id, new GraphRect(n.Frame.X * graphZoom + camera.X, n.Frame.Y * graphZoom + camera.Y, n.Frame.W * graphZoom, n.Frame.H * graphZoom))).ToList();
            return ResizeEdgeRules.Target((point.X, point.Y), frames, graphZoom);
        }

        /// <summary>A press on the corner handle or on a border's band: the block is selected and its drag begins (M/MightyGraphInteraction.swift:410-423).</summary>
        private void BeginGraphResize(string nodeId, ResizeEdges edges, PointerRoutedEventArgs args)
        {
            if (graphViewport is null || !graphCards.TryGetValue(nodeId, out var card)) return;
            SelectGraphBlock(nodeId);
            graphViewport.Focus(FocusState.Pointer);
            // Nothing moves for the drag; released, the camera follows on only if the newest block is still in view.
            graphResizing = true; graphDragging = false; graphResizePin = null; graphFollow.UserChangedLayout();
            graphResizeOrigin = args.GetCurrentPoint(graphViewport).Position;
            graphResizeStart = (card.Width, card.Height); graphResizeEdges = edges;
            graphResizeCardOrigin = (Canvas.GetLeft(card), Canvas.GetTop(card));
            var (unit, camera) = (edges.PinnedUnit(), GraphCamera);
            graphResizeAnchor = GraphNodeFrame(nodeId) is { } frame
                ? (nodeId, (camera.X + (frame.X + unit.X * frame.W) * graphZoom, camera.Y + (frame.Y + unit.Y * frame.H) * graphZoom), unit) : null;
            graphResizeLayout = graphLayout; graphResizeNodeId = nodeId;
            graphViewport.CapturePointer(args.Pointer);
            args.Handled = true;
        }

        /// <summary>A press of the left button, a pen tip or a touch: the only press that drags a side (M/MightyGraphInteraction.swift:695).</summary>
        private bool LeftPress(PointerRoutedEventArgs args) => args.GetCurrentPoint(graphViewport).Properties.IsLeftButtonPressed;

        /// <summary>
        /// A press on a block: the thin strip inside a border, or a neighbour's band reaching over this card, resizes
        /// (M/MightyGraphInteraction.swift:697-701); anywhere else selects the block.
        /// </summary>
        private void PressGraphCard(string nodeId, PointerRoutedEventArgs args)
        {
            if (graphViewport is not null && LeftPress(args) && GraphResizeTarget(args.GetCurrentPoint(graphViewport).Position) is { } target) { BeginGraphResize(target.Id, target.Edges, args); return; }
            SelectGraphBlock(nodeId); graphViewport?.Focus(FocusState.Pointer); args.Handled = true;
        }

        /// <summary>
        /// The strip inside a resizable block's border (<see cref="ResizeEdgeRules.BandInside"/> on screen), laid over the card as the
        /// Mac's four edge bands lie over it, so the body's scroller under the right edge cannot take a press meant for that side.
        /// Only its edge takes input: every other press goes through to the card.
        /// </summary>
        private Border GraphBand(string nodeId, GraphRect frame)
        {
            if (!graphBands.TryGetValue(nodeId, out var band))
            {
                band = graphBands[nodeId] = new Border { BorderBrush = owner.brushes.Transparent, CornerRadius = new CornerRadius(DesignMetrics.Radius.Block) };
                AutomationProperties.SetAccessibilityView(band, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                band.PointerPressed += (_, args) => PressGraphCard(nodeId, args);
            }
            band.BorderThickness = new Thickness(ResizeEdgeRules.BandInside / graphZoom);
            band.Width = frame.W; band.Height = frame.H;
            Canvas.SetLeft(band, frame.X); Canvas.SetTop(band, frame.Y);
            return band;
        }

        /// <summary>The resize cursor of the sides under the pointer, and the arrow again once it leaves them (M/MightyGraphInteraction.swift:395-409).</summary>
        private void ShowGraphResizeCursor(ResizeEdges edges)
        {
            if (edges == graphHoverEdges) return;
            graphHoverEdges = edges;
            var rising = edges is (ResizeEdges.Left | ResizeEdges.Bottom) or (ResizeEdges.Right | ResizeEdges.Top);
            graphSurface?.ShowCursor(edges == ResizeEdges.None ? null
                : edges.Horizontal() && edges.Vertical() ? rising ? Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest : Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast
                : edges.Horizontal() ? Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast : Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth);
        }

        /// The size under the cursor, in diagram coordinates: the dragged sides follow the pointer, never below the least
        /// the block's kind takes (<see cref="MightyGraphLayout.MinimumBlockSize"/>) nor above the most a block may be.
        private (double W, double H) DraggedResultSize(Windows.Foundation.Point point) =>
            graphResizeEdges.Size(graphResizeStart, (point.X - graphResizeOrigin.X, point.Y - graphResizeOrigin.Y), graphZoom,
                MightyGraphLayout.MinimumBlockSize(graphResizeNodeId is { } node ? graphBlockKinds.GetValueOrDefault(node) ?? "" : ""),
                (MightyGraphBlockSize.MaximumWidth, MightyGraphBlockSize.MaximumHeight));

        /// A dragged left or top side moves that side: the card's far corner stays where it was.
        private void PlaceResizedCard(Border card)
        {
            if (graphResizeEdges.HasFlag(ResizeEdges.Left)) Canvas.SetLeft(card, graphResizeCardOrigin.X + graphResizeStart.W - card.Width);
            if (graphResizeEdges.HasFlag(ResizeEdges.Top)) Canvas.SetTop(card, graphResizeCardOrigin.Y + graphResizeStart.H - card.Height);
            // The selection's outline follows the card it is drawn on.
            ApplyGraphSelectionStyle();
        }

        /// The drag's last redraw: the layout carries the new size by now, so the far corner's pin has done its work.
        private void SettleGraphResize()
        {
            RefreshMightyView(Session);
            if (graphResizePin is null) return;
            graphResizePin = null; QueueGraphRefresh();
        }

        /// Live: the card follows the cursor up to the pane's edge and stops there.
        private void MoveResultResize(Windows.Foundation.Point point)
        {
            if (graphResizeNodeId is { } planNode && planNode == graphPlanNodeId)
            {
                // The pane's one plan size, shown and saved by the newest result's rules.
                var planDrag = MightyGraphLayout.ResultDrag(DraggedResultSize(point), true, true, MightyGraphLayout.ResizePhase.Live,
                    Session.GraphPlanSize, graphResizeLayout?.PlanLimit, graphResizeLayout?.PlanWindowFit);
                if (planDrag.Live is not { } livePlan || !graphCards.TryGetValue(planNode, out var planCard)) return;
                graphLivePlanSize = livePlan;
                planCard.Width = livePlan.Width; planCard.Height = livePlan.Height;
                PlaceResizedCard(planCard);
                return;
            }
            if (graphResizeNodeId != graphLatestResultId)
            {
                var raw = DraggedResultSize(point);
                if (graphResizeNodeId is { } node && new GraphBlockSize(raw.W, raw.H).NormalizedFor(node) is { } custom && graphCards.TryGetValue(node, out var changed))
                { changed.Width = custom.Width; changed.Height = custom.Height; PlaceResizedCard(changed); }
                return;
            }
            var drag = MightyGraphLayout.ResultDrag(DraggedResultSize(point), true, true, MightyGraphLayout.ResizePhase.Live,
                Session.GraphResultSize, graphResizeLayout?.ResultLimit, graphResizeLayout?.ResultWindowFit);
            if (drag.Live is not { } live || graphLatestResultId is null || !graphCards.TryGetValue(graphLatestResultId, out var card)) return;
            graphLiveResultSize = live;
            card.Width = live.Width; card.Height = live.Height;
            PlaceResizedCard(card);
        }

        /// Released or abandoned. A release saves the new remembered maximum
        /// (only the sides moved inside the pane); a cancel saves nothing.
        private void EndResultResize(MightyGraphLayout.ResizePhase phase, Windows.Foundation.Point? point)
        {
            if (!graphResizing) return;
            graphResizing = false;
            // The card started at the size it showed (shorter than the saved
            // maximum when it fits a short answer): a side the pointer did not
            // move keeps the maximum, and a release that moved nothing saves nothing.
            var dragged = point is { } at ? DraggedResultSize(at) : graphResizeStart;
            var sides = MightyGraphLayout.ResultDragSides(graphResizeStart, dragged);
            if (sides is (false, false)) phase = MightyGraphLayout.ResizePhase.Cancelled;
            // A drag that changed the block keeps the corner across from the dragged sides where it was.
            graphResizePin = phase == MightyGraphLayout.ResizePhase.Cancelled ? null : graphResizeAnchor;
            graphResizeAnchor = null;
            if (graphResizeNodeId is { } planNode && planNode == graphPlanNodeId)
            {
                var planDrag = MightyGraphLayout.ResultDrag(dragged, sides.Horizontal, sides.Vertical, phase,
                    Session.GraphPlanSize, graphResizeLayout?.PlanLimit, graphResizeLayout?.PlanWindowFit);
                graphResizeNodeId = null; graphResizeLayout = null; graphLivePlanSize = null;
                if (planDrag.Save is { } savePlan) _ = owner.Act(async () => { await Change(p => p with { GraphPlanSize = savePlan }); SettleGraphResize(); });
                else SettleGraphResize();
                return;
            }
            if (graphResizeNodeId is { } nodeId && nodeId != graphLatestResultId)
            {
                graphResizeNodeId = null; graphResizeLayout = null;
                if (phase != MightyGraphLayout.ResizePhase.Cancelled) _ = SaveGraphBlockSize(nodeId, new(dragged.W, dragged.H));
                else SettleGraphResize();
                return;
            }
            graphResizeNodeId = null;
            var drag = MightyGraphLayout.ResultDrag(dragged, sides.Horizontal, sides.Vertical, phase,
                Session.GraphResultSize, graphResizeLayout?.ResultLimit, graphResizeLayout?.ResultWindowFit);
            graphResizeLayout = null; graphLiveResultSize = null;
            if (drag.Save is { } save) _ = owner.Act(async () => { await Change(p => p with { GraphResultSize = save }); SettleGraphResize(); });
            else SettleGraphResize();
        }

        /// Clears the saved size: the newest result card fits the window again.
        private Task FitResultToWindow() =>
            owner.Act(async () => { await Change(p => p with { GraphResultSize = null }); RefreshMightyView(Session); });

        /// The plan block's 창에 맞춤: its saved size cleared, back to the window fit.
        private Task FitPlanToWindow() =>
            owner.Act(async () => { graphLivePlanSize = null; await Change(p => p with { GraphPlanSize = null }); RefreshMightyView(Session); });

        // ── zoom, pan and selection ───────────────────────────────────────────

        /// <summary>
        /// A new zoom keeps the middle of the viewport where it is (M/MightyGraphView.swift:1108-1113), and it is the user's own
        /// move away: nothing follows until they come back to the newest block (M/MightyGraphInteraction.swift:308-309).
        /// </summary>
        internal void SetGraphZoom(double value)
        {
            var changed = graphZoom != value;
            if (changed && graphLayout is not null)
            {
                graphFollow.Stop();
                if (GraphViewportSize is { } size)
                {
                    var pan = MightyGraphViewModel.ZoomAnchored((graphPan.X, graphPan.Y), size, graphZoom, value);
                    ShiftGraphPan(pan.X - graphPan.X, pan.Y - graphPan.Y);
                }
            }
            graphZoom = value; graphScale.ScaleX = graphScale.ScaleY = value;
            if (zoomResetButton is not null) ((TextBlock)zoomResetButton.Content).Text = MightyGraphViewModel.ZoomLabel(value);
            if (zoomOutButton is not null) zoomOutButton.IsEnabled = !MightyGraphViewModel.ZoomOutDisabled(value);
            if (zoomInButton is not null) zoomInButton.IsEnabled = !MightyGraphViewModel.ZoomInDisabled(value);
            // The newest result card's pane limit is in diagram coordinates at this zoom.
            if (changed && graphHost?.Visibility == Visibility.Visible) RefreshMightyView(Session);
        }

        internal double GraphZoom => graphZoom;
        internal string? GraphSelection => graphSelection;
        internal int GraphBlockCount => graphCards.Count;

        /// The wheel scrolls the selected block's body while the pointer is over that
        /// block, and a block attached beside the flow at once (M/MightyGraphInteraction.swift:713-722);
        /// anywhere else it pans the canvas up and down, and left and right for a horizontal
        /// wheel, a tilt or Shift+wheel. Either way it never reaches the outer page.
        private void OnGraphWheel(object sender, PointerRoutedEventArgs args)
        {
            args.Handled = true;
            // A wheel turned during a block drag does nothing (M/MightyGraphInteraction.swift:616).
            if (graphResizing) return;
            var pointer = args.GetCurrentPoint(graphViewport);
            var properties = pointer.Properties;
            var shift = (args.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0;
            var (panX, panY) = MightyGraphViewModel.WheelPan(properties.MouseWheelDelta, properties.IsHorizontalMouseWheel, shift);
            // The selected block scrolls only while the pointer is over it; a block attached beside the flow scrolls at once.
            var camera = GraphCamera;
            var hovered = graphLayout?.NodeAt((pointer.Position.X - camera.X) / graphZoom, (pointer.Position.Y - camera.Y) / graphZoom);
            if (MightyGraphViewModel.WheelScrollTarget(graphSelection, hovered?.Id, hovered is not null && MightyGraphLayout.IsAuxiliary(hovered.Kind)) is { } target)
            {
                // The block body only scrolls up and down; a block with nothing to scroll still takes the wheel.
                if (panY != 0 && graphBodies.TryGetValue(target, out var body)) body.ChangeView(null, Math.Max(0, body.VerticalOffset - panY), null, true);
            }
            else UserPan(panX, panY);
        }

        /// The user's own wheel pan: a move away from the newest block stops the
        /// camera following, and only a move towards the top loads more.
        private void UserPan(double dx, double dy)
        {
            ShiftGraphPan(dx, dy);
            if (dx != 0 || dy != 0) UserMovedGraphCamera();
            if (dy > 0) LoadOlderAtTop();
        }

        private void OnGraphPointerPressed(object sender, PointerRoutedEventArgs args)
        {
            // Only the empty background reaches here: a card handles its own press. Just outside a block's border it is that side's band.
            if (LeftPress(args) && GraphResizeTarget(args.GetCurrentPoint(graphViewport).Position) is { } target) { BeginGraphResize(target.Id, target.Edges, args); return; }
            ClearGraphSelection();
            // Take keyboard focus so Esc clears a selection after a mouse click.
            graphViewport?.Focus(FocusState.Pointer);
            graphDragging = true; graphDragOrigin = args.GetCurrentPoint(graphViewport).Position;
            graphDragPanX = graphPan.X; graphDragPanY = graphPan.Y;
            graphViewport?.CapturePointer(args.Pointer);
            args.Handled = true;
        }

        private void OnGraphPointerMoved(object sender, PointerRoutedEventArgs args)
        {
            if (graphResizing) { MoveResultResize(args.GetCurrentPoint(graphViewport).Position); args.Handled = true; return; }
            var point = args.GetCurrentPoint(graphViewport).Position;
            if (!graphDragging) { ShowGraphResizeCursor(GraphResizeTarget(point)?.Edges ?? ResizeEdges.None); return; }
            var previousY = graphPan.Y;
            var (panX, panY) = (graphDragPanX + (point.X - graphDragOrigin.X), graphDragPanY + (point.Y - graphDragOrigin.Y));
            // A click alone moves nothing: only a move is judged.
            var moved = panX != graphPan.X || panY != graphPan.Y;
            graphPan.X = panX; graphPan.Y = panY;
            if (moved) UserMovedGraphCamera();
            // Only the user's own move towards the top loads more.
            if (graphPan.Y > previousY) LoadOlderAtTop();
            args.Handled = true;
        }

        private void OnGraphPointerReleased(object sender, PointerRoutedEventArgs args)
        {
            var point = args.GetCurrentPoint(graphViewport).Position;
            if (graphResizing) EndResultResize(MightyGraphLayout.ResizePhase.Finished, point);
            EndGraphDrag(); graphViewport?.ReleasePointerCapture(args.Pointer);
            // Released, the cursor is that of the sides under the pointer again, or the arrow away from them.
            ShowGraphResizeCursor(GraphResizeTarget(point)?.Edges ?? ResizeEdges.None);
        }

        /// <summary>
        /// The background drag ends. What it held back (a target that waited for the release, a follow pass) gets its pass once the
        /// user has been still for the grace (M/MightyGraphInteraction.swift:244).
        /// </summary>
        private void EndGraphDrag()
        {
            if (!graphDragging) return;
            graphDragging = false;
            QueueGraphCameraPass(GraphUserMoveGrace + 50);
        }

        internal void SelectGraphBlock(string? nodeId)
        {
            graphSelection = MightyGraphViewModel.ApplySelection(graphSelection, nodeId);
            ApplyGraphSelectionStyle();
        }

        internal void ClearGraphSelection() => SelectGraphBlock(null);

        /// <summary>The selection outline lies over every card and outline of the diagram.</summary>
        private const int SelectionZIndex = 100000;

        /// <summary>
        /// The selected block (M/MightyGraphView.swift:498, 917-924, 932): a 2pt <c>accent</c> line stroked on its
        /// card's outline, a point inside it and a point outside, so nothing in the card moves; its header shows
        /// the scroll hint and its corner handle turns <c>accent</c>. Every other card keeps its own 1pt
        /// <c>line</c> edge (the draft only its dashes).
        /// </summary>
        private void ApplyGraphSelectionStyle()
        {
            var b = owner.brushes;
            foreach (var (nodeId, view) in graphCardViews)
            {
                var selected = nodeId == graphSelection;
                if (view.ScrollHint is { } hint) hint.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
                if (view.GripSymbol is { } arrows) MightySymbols.Paint(arrows, b.Brush(selected ? DesignToken.Accent : DesignToken.Ink2));
            }
            if (graphSelection is { } id && graphCards.TryGetValue(id, out var card) && card.Width > 0 && card.Height > 0)
            {
                const double half = DesignMetrics.Stroke.Active / 2;
                var outline = graphSelectionOutline ??= new Rectangle
                {
                    RadiusX = DesignMetrics.Radius.Block, RadiusY = DesignMetrics.Radius.Block, Stroke = b.Brush(DesignToken.Accent), StrokeThickness = DesignMetrics.Stroke.Active, IsHitTestVisible = false,
                };
                outline.Width = card.Width + 2 * half; outline.Height = card.Height + 2 * half;
                Canvas.SetLeft(outline, Canvas.GetLeft(card) - half); Canvas.SetTop(outline, Canvas.GetTop(card) - half); Canvas.SetZIndex(outline, SelectionZIndex);
                AutomationProperties.SetAutomationId(outline, "mighty-selected-" + id);
                AutomationProperties.SetAccessibilityView(outline, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                if (!graphCanvas.Children.Contains(outline)) graphCanvas.Children.Add(outline);
            }
            else if (graphSelectionOutline is { } stale) graphCanvas.Children.Remove(stale);
        }

        // ── smoke accessors ───────────────────────────────────────────────────

        internal IReadOnlyList<string> GraphBlockIds => graphCards.Keys.ToList();
        /// Each drawn activity outline: its kind, its dash in stroke widths and whether it moves.
        internal IReadOnlyDictionary<string, (string Kind, double[] Dash)> GraphOutlinesForSmoke =>
            graphOutlines.ToDictionary(p => p.Key, p => (p.Value.Kind, p.Value.Line.StrokeDashArray?.ToArray() ?? Array.Empty<double>()));
        internal Canvas GraphCanvas => graphCanvas;
        internal string GraphTotalText => graphTotal.Text;
        internal (double X, double Y) GraphPan => (graphPan.X, graphPan.Y);
        internal string? GraphResultFilesRunId => graphResultFilesRunId;
        internal RunSession SessionForSmoke => Session;
        internal Dictionary<string, (object Card, object? Document, object? Parent)> GraphNativeViewsForSmoke() =>
            graphCardViews.ToDictionary(pair => pair.Key, pair => ((object)pair.Value.Card, (object?)pair.Value.Transcript?.View, (object?)pair.Value.Transcript?.View.Parent));

        /// The result box smoke: the viewport pinned to a size (NaN lets the pane
        /// decide again), the saved size set or cleared, and what was drawn.
        internal void SetGraphViewportSizeForSmoke(double width, double height)
        {
            if (graphViewport is null) return;
            graphViewport.Width = width; graphViewport.Height = height;
            graphViewport.HorizontalAlignment = double.IsNaN(width) ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            graphViewport.VerticalAlignment = double.IsNaN(height) ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        }
        internal (double W, double H) GraphViewportSizeForSmoke => graphViewport is null ? (0, 0) : (graphViewport.ActualWidth, graphViewport.ActualHeight);
        internal Task SetGraphResultSizeForSmoke(GraphBlockSize? size) =>
            owner.Act(async () => { await Change(p => p with { GraphResultSize = size }); RefreshMightyView(Session); });
        internal (double W, double H)? GraphCardSizeForSmoke(string nodeId) =>
            graphCards.TryGetValue(nodeId, out var card) ? (card.Width, card.Height) : null;
        internal bool GraphFitResultButtonShownForSmoke => graphFitResultButton is not null;
        /// The result reveal smoke: the newest result's measured content height,
        /// the card's frame on the canvas, the held card, the pane's running state
        /// as a real request sets it, and the user's own pan.
        internal double? GraphResultContentHeightForSmoke(string nodeId) => graphResultHeights.TryGetValue(nodeId, out var height) ? height : null;
        internal GraphRect? GraphCardFrameForSmoke(string nodeId) =>
            graphCards.TryGetValue(nodeId, out var card) ? new GraphRect(Canvas.GetLeft(card), Canvas.GetTop(card), card.Width, card.Height) : null;
        internal bool GraphFollowingForSmoke => graphFollow.Following;
        internal Task SetPaneStatusForSmoke(string status) =>
            owner.Act(async () => { await Change(p => p with { Status = status }); RefreshMightyView(Session); });
        internal void PanGraphForSmoke(double dy) => UserPan(0, dy);
        internal Task FitResultToWindowForSmoke() => FitResultToWindow();

        internal Task SetGraphRunsForSmoke(List<MightyGraphRun> runs) =>
            owner.Act(async () => { await Change(p => p with { GraphRuns = runs }); Refresh(); });

        /// What the canvas actually drew: the blocks and edges on screen, their
        /// distinct kinds, and the files the open result-files panel lists.
        internal (int Blocks, int Edges, List<string> Kinds, int ResultFiles) ReadGraphForSmoke()
        {
            var edges = graphEdgeRoutes.Count;
            var kinds = graphBlockKinds.Values.Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
            var latest = MightyGraphBlockModel.LatestCompletedRun(Session.GraphRuns ?? []);
            var files = latest is null ? 0 : MightyGraphBlockModel.FilesFor(latest, Workspace.Path).Count;
            return (graphCards.Count, edges, kinds, files);
        }

        /// Each drawn card's kind and the provider whose mark its header carries (null for none).
        internal List<(string Id, string Kind, string? Mark)> GraphTitleMarksForSmoke() =>
            graphTitles.Select(pair => (pair.Key, graphBlockKinds.GetValueOrDefault(pair.Key) ?? "", ProviderMarkView.LabelledProvider(pair.Value))).ToList();

        /// Header strings of every card; they are not reachable from the canvas
        /// tree walk once a card body scrolls, so the leak scan gets them directly.
        internal List<string> GraphHeaderTextsForSmoke()
        {
            var texts = new List<string>();
            foreach (var card in graphCards.Values) CollectVisibleStrings(card, texts);
            return texts;
        }
    }
}

/// <summary>
/// The Mighty diagram's surface. A block's border shows the cursor of the sides a drag there would move; the cursor
/// is a protected property, so it lives on this parent and the blocks inherit it.
/// </summary>
internal sealed partial class GraphSurface : Grid
{
    internal void ShowCursor(Microsoft.UI.Input.InputSystemCursorShape? shape) =>
        ProtectedCursor = shape is { } value ? Microsoft.UI.Input.InputSystemCursor.Create(value) : null;
}
