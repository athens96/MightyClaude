using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using Line = Microsoft.UI.Xaml.Shapes.Line;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

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
        private readonly TextBlock graphTotal = new() { FontSize = DesignMetrics.Type.Small, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        private Button? zoomOutButton, zoomResetButton, zoomInButton;
        private Border? graphViewport;
        /// <summary>The Win2D dots under the canvas, following its pan and zoom (M/MightyGraphView.swift:995-1015).</summary>
        private MightyDotGrid? graphDotGrid;
        /// <summary>The draft card's dashed accent edge, kept across redraws (M/MightyGraphView.swift:416).</summary>
        private Rectangle? graphDraftOutline;
        /// <summary>The edges as last drawn, each with the block it runs into, for the design smoke.</summary>
        private readonly List<(string Target, Line Line, Microsoft.UI.Xaml.Shapes.Polyline Head)> graphEdgeLines = [];
        /// <summary>The running blocks' activity capsules (their vertical scales), by block, and the one pane timer that waves them all.</summary>
        private readonly Dictionary<string, ScaleTransform[]> graphActivitySets = [];
        private DispatcherTimer? graphActivityTimer;
        private double graphZoom = MightyGraphViewModel.ZoomDefault;
        private string? graphSelection;
        private readonly Dictionary<string, ScrollViewer> graphBodies = [];
        private readonly Dictionary<string, AgentTranscript> graphTranscripts = [];
        private bool graphDeferredDraw;
        private readonly Dictionary<string, Border> graphCards = [];
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
        private string? graphResultFilesRunId, graphResultFilesLastRunId, graphAimedRunId, graphAimedResultId;
        private bool graphResultFilesClosedByHand;
        // The newest result card's drag (macOS MightyGraphView.resize): the layout
        // it started on carries the pane limit and window fit it is kept within.
        private MightyGraphLayout? graphLayout, graphResizeLayout;
        private string? graphLatestResultId;
        private GraphBlockSize? graphLiveResultSize;
        private Button? graphFitResultButton;
        private bool graphResizing;
        private Windows.Foundation.Point graphResizeOrigin;
        private (double W, double H) graphResizeStart;
        // A new result right above the composer (macOS MightyGraphView reveal):
        // the rule is Core's; the pane's own runs, running state, draft and
        // viewport as last drawn are what it compares. Null progress is a view
        // just shown, whose runs were not watched finishing.
        private MightyGraphCamera.ResultReveal graphReveal = new();
        private List<MightyGraphCamera.ResultReveal.RunProgress>? graphRunProgress;
        private bool graphWasRunning, graphDraftWasEmpty = true;
        private (double W, double H)? graphLastViewport;
        private string? graphRevealPendingId;
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
            var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            modeDefaultButton = ModePill(MightyGraphViewModel.LocaleKeyDefault, "default", options, out modeDefaultChip);
            modeMightyButton = ModePill(MightyGraphViewModel.LocaleKeyMighty, "mighty", options, out modeMightyChip);
            modeSwitch = new Border { Child = options, Padding = new Thickness(2), CornerRadius = new CornerRadius(DesignMetrics.Radius.Row), Background = owner.brushes.SegmentTrack, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(modeSwitch, "mighty-mode-switch-" + id);
            paneHeaderControls.Children.Insert(0, modeSwitch);
            modeSwitch.SizeChanged += (_, _) => QueuePaneHeaderLayout();
            QueuePaneHeaderLayout();

            graphHost = new Grid { RowSpacing = 0, Visibility = Visibility.Collapsed };
            graphHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            graphHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            graphHost.Children.Add(BuildGraphToolbar());

            graphCanvas.RenderTransform = new TransformGroup { Children = { graphScale, graphPan } };
            // The page surface edge to edge (no radius) with the dot grid under the blocks.
            var dots = graphDotGrid = new MightyDotGrid(owner.brushes.Brush(DesignToken.Line));
            var surface = new Grid(); surface.Children.Add(dots.View); surface.Children.Add(graphCanvas);
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
            graphViewport.PointerCaptureLost += (_, _) => { graphDragging = false; if (graphResizing) EndResultResize(MightyGraphLayout.ResizePhase.Cancelled, null); };
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
            graphViewport.Loaded += (_, _) => UpdateActivityTimer();
            graphViewport.Unloaded += (sender, _) => { if (!((FrameworkElement)sender).IsLoaded) graphActivityTimer?.Stop(); };
            Grid.SetRow(graphViewport, 1); graphHost.Children.Add(graphViewport);
            BuildMightyTimeline();
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
        /// The Mighty bar (M/MightyGraphView.swift:175-201): padding h12 v10 over a <c>line</c> rule; the
        /// style title 12 bold <c>ink</c> over the 10pt <c>ink2</c> summary; the Diagram | Timeline switch and
        /// the zoom − / NN% (38 wide) / + in <c>ink2</c>. A narrow pane moves the controls under the summary.
        /// </summary>
        private Grid BuildGraphToolbar()
        {
            var b = owner.brushes;
            var bar = new Grid { ColumnSpacing = 6, Padding = new Thickness(12, 10, 12, 10), BorderThickness = new Thickness(0, 0, 0, DesignMetrics.Stroke.Line), BorderBrush = b.Brush(DesignToken.Line) };
            bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            graphStyleHeader.Foreground = b.Brush(DesignToken.Ink);
            graphTotal.Foreground = b.Brush(DesignToken.Ink2);
            AutomationProperties.SetAutomationId(graphTotal, "mighty-tokens-" + id);
            var summary = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            summary.Children.Add(graphStyleHeader); summary.Children.Add(graphTotal); bar.Children.Add(summary);
            var zoom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            // Segoe Fluent Icons ZoomOut / ZoomIn for the Mac's minus / plus magnifying glasses.
            zoomOutButton = ZoomButton(new FontIcon { Glyph = "\uE71F", FontSize = 12 }, Locale.Get(MightyGraphViewModel.LocaleKeyZoomOut), "mighty-zoom-out-" + id, () => SetGraphZoom(MightyGraphViewModel.ZoomOut(graphZoom)));
            zoomResetButton = ZoomButton(new TextBlock { Text = MightyGraphViewModel.ZoomLabel(graphZoom), FontSize = DesignMetrics.Type.Pill, HorizontalAlignment = HorizontalAlignment.Center }, Locale.Get(MightyGraphViewModel.LocaleKeyZoomReset), "mighty-zoom-reset-" + id, () => SetGraphZoom(MightyGraphViewModel.ZoomDefault));
            zoomResetButton.Width = 38; zoomResetButton.Padding = new Thickness(0);
            zoomInButton = ZoomButton(new FontIcon { Glyph = "\uE8A3", FontSize = 12 }, Locale.Get(MightyGraphViewModel.LocaleKeyZoomIn), "mighty-zoom-in-" + id, () => SetGraphZoom(MightyGraphViewModel.ZoomIn(graphZoom)));
            zoom.Children.Add(zoomOutButton); zoom.Children.Add(zoomResetButton); zoom.Children.Add(zoomInButton);
            graphZoomControls = zoom;
            var controls = new PillWrapPanel();
            controls.Children.Add(BuildGraphPresentationSwitch()); controls.Children.Add(zoom);
            Grid.SetColumn(controls, 1); bar.Children.Add(controls);
            void FitToolbar(double width)
            {
                TraceGraphSmoke($"toolbar-fit:{width:F2}");
                var narrow = width < 540;
                Grid.SetColumnSpan(summary, narrow ? 2 : 1);
                Grid.SetRow(controls, narrow ? 1 : 0); Grid.SetColumn(controls, narrow ? 0 : 1); Grid.SetColumnSpan(controls, narrow ? 2 : 1);
                // The bar's own padding is inside the width it reports.
                controls.MaxWidth = Math.Max(1, width - bar.Padding.Left - bar.Padding.Right); controls.Margin = new Thickness(0, narrow ? 4 : 0, 0, 0);
            }
            bar.SizeChanged += (_, args) => FitToolbar(args.NewSize.Width);
            FitToolbar(Container.ActualWidth > 0 ? Container.ActualWidth : 500);
            graphToolbar = bar;
            return bar;
        }

        /// <summary>A zoom control: plain, 22 tall, <c>ink2</c> (<c>ink3</c> while disabled), the subtle wash under the pointer.</summary>
        private Button ZoomButton(UIElement content, string name, string automationId, Action act)
        {
            var button = new Button { Content = content, MinWidth = 0, MinHeight = 0, Height = 22, Padding = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            owner.PaintPlainButton(button, owner.brushes.Transparent, owner.brushes.Subtle, ink: owner.brushes.Brush(DesignToken.Ink2), disabledInk: owner.brushes.Brush(DesignToken.Ink3));
            AutomationProperties.SetAutomationId(button, automationId); AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, name);
            button.Click += (_, _) => act();
            return button;
        }

        /// <summary>
        /// One side of the Default | Mighty switch (M/SessionPaneView.swift:306-324): the symbol and its word,
        /// 11 semibold, height 20, padding h8. The button draws no fill in any state; the chosen side's
        /// chip is the shadow-casting shape under it (<see cref="PaintModeOption"/>).
        /// </summary>
        private Button ModePill(string localeKey, string mode, Panel options, out Rectangle chip)
        {
            var text = Locale.Get(localeKey);
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            // Segoe Fluent Icons: AlignLeft for Default (text.alignleft), Relationship for Mighty (point.3.connected).
            content.Children.Add(new FontIcon { Glyph = mode == "mighty" ? "\uF003" : "\uE8E4", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = text, FontSize = DesignMetrics.Type.Pill, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = content, MinWidth = 0, MinHeight = 0, Height = 20, Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), BorderThickness = new Thickness(0) };
            owner.PaintPlainButton(button, owner.brushes.Transparent, owner.brushes.Transparent);
            AutomationProperties.SetAutomationId(button, "mighty-mode-" + mode + "-" + id); AutomationProperties.SetName(button, text); ToolTipService.SetToolTip(button, text);
            button.Click += async (_, _) => await SetAgentViewMode(mode);
            chip = CardShadow.Caster(DesignMetrics.Radius.Segment, CardShadow.SegmentChip, owner.brushes.SegmentOn); chip.Visibility = Visibility.Collapsed;
            var cell = new Grid(); cell.Children.Add(chip); cell.Children.Add(button); options.Children.Add(cell);
            return button;
        }

        /// <summary>The chosen side shows its chip and <c>ink</c>; the other keeps the muted <c>ink2</c>.</summary>
        private void PaintModeOption(Button button, Rectangle chip, bool selected)
        {
            chip.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            var ink = owner.brushes.Brush(selected ? DesignToken.Ink : DesignToken.Ink2);
            foreach (var part in ((Panel)button.Content).Children)
                if (part is TextBlock words) words.Foreground = ink; else if (part is IconElement icon) icon.Foreground = ink;
        }

        /// <summary>Whether the switch shows its words; a narrow header shows its symbols only.</summary>
        private bool ModeWordsShown => modeDefaultButton?.Content is Panel { Children: [_, TextBlock { Visibility: Visibility.Visible }] };

        private void ShowModeWords(bool shown)
        {
            foreach (var button in new[] { modeDefaultButton, modeMightyButton })
                if (button?.Content is Panel { Children: [_, TextBlock words] }) words.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        }

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
            var timeline = mighty && MightyTimeline.Mode(pane) == "timeline";
            RefreshGraphPresentationSwitch(timeline);
            if (graphViewport is not null) graphViewport.Visibility = timeline ? Visibility.Collapsed : Visibility.Visible;
            if (mighty && !timeline && diagramWasHidden) graphDotGrid?.Redraw();
            if (timelineScroll is not null) timelineScroll.Visibility = timeline ? Visibility.Visible : Visibility.Collapsed;
            if (mighty && timeline) DrawMightyTimeline(pane);
            else if (mighty) DrawGraph(pane);
            // Hidden, the diagram watches nothing: a result that finishes meanwhile is not revealed.
            else { graphReveal = new(); graphRunProgress = null; graphRevealPendingId = null; }
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
            var latest = MightyGraphBlockModel.LatestCompletedRun(runs);
            List<ResultFiles.ResultFile> files = latest is null ? [] : MightyGraphBlockModel.FilesFor(latest, Workspace.Path);
            // A new result clears a previous manual close; only one panel is open.
            if (latest?.Id != graphResultFilesLastRunId) { graphResultFilesClosedByHand = false; graphResultFilesLastRunId = latest?.Id; }
            graphResultFilesRunId = MightyGraphViewModel.NextResultFilesRunID(graphResultFilesRunId, graphResultFilesClosedByHand, latest?.Id, files.Count > 0);

            var viewport = graphViewport is { ActualWidth: > 0 } v ? ((double W, double H)?)(v.ActualWidth, v.ActualHeight) : null;
            // The newest result card takes its saved size (or the window fit) kept
            // within the pane at this zoom, so it shrinks with the pane and grows
            // back to the saved size when the pane does. Under that cap it is as
            // tall as its measured content; a drag in progress shows its own size.
            var latestResultId = MightyGraphLayout.LatestResultID(runs);
            double? resultContentHeight = graphLiveResultSize is null && latestResultId is not null && graphResultHeights.TryGetValue(latestResultId, out var measured) ? measured : null;
            var layout = MightyGraphViewModel.CanvasLayout(runs, pane.Draft, pane.Status == "running", graphExpanded, graphResultFilesRunId, viewport,
                graphZoom, graphLiveResultSize ?? pane.GraphResultSize, older.Count, ShowsHistoryBlock(pane, retained), resultContentHeight, pane.GraphBlockSizes);
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

            var live = blocks.Select(block => block.Id).ToHashSet();
            foreach (var stale in graphCards.Keys.Where(key => !live.Contains(key)).ToArray())
            {
                graphCards.Remove(stale); graphCardViews.Remove(stale); graphBodies.Remove(stale); graphTranscripts.Remove(stale);
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
            if (graphOriginY is { } previousOrigin && previousOrigin != layout.OriginY) graphPan.Y += (layout.OriginY - previousOrigin) * graphZoom;
            graphOriginY = layout.OriginY;
            // Edges (M/MightyGraphView.swift:372-397): ink2 × 0.45 at 1.5pt, run blue at 2pt into a running
            // block, each ending in an arrowhead (±4, −6) drawn as a polyline so the edge count stays one line each.
            var running = blocks.Where(block => MightyGraphBlockModel.Tone(block) == DesignTone.Run).Select(block => block.Id).ToHashSet();
            graphEdgeLines.Clear();
            foreach (var edge in layout.Edges)
            {
                if (!frames.TryGetValue(edge.Source, out var from) || !frames.TryGetValue(edge.Target, out var to)) continue;
                var intoRunning = running.Contains(edge.Target);
                var stroke = intoRunning ? owner.brushes.Brush(DesignToken.Run) : owner.brushes.Brush(DesignToken.Ink2, DesignMetrics.Opacity.Edge);
                var width = intoRunning ? DesignMetrics.Stroke.Active : DesignMetrics.Stroke.Focus;
                var (startX, startY, endX, endY) = (from.X + from.W / 2, from.Y + from.H, to.X + to.W / 2, to.Y);
                // The head's two barbs sit 6 back along the edge and 4 to either side of it (the Mac's ±4, −6 on a downward edge).
                var length = Math.Max(1e-9, Math.Sqrt((endX - startX) * (endX - startX) + (endY - startY) * (endY - startY)));
                var (alongX, alongY) = ((endX - startX) / length, (endY - startY) / length);
                var line = new Line
                {
                    X1 = startX, Y1 = startY, X2 = endX, Y2 = endY,
                    Stroke = stroke, StrokeThickness = width, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false,
                };
                var head = new Microsoft.UI.Xaml.Shapes.Polyline
                {
                    Points = new PointCollection { new(endX - 6 * alongX + 4 * alongY, endY - 6 * alongY - 4 * alongX), new(endX, endY), new(endX - 6 * alongX - 4 * alongY, endY - 6 * alongY + 4 * alongX) },
                    Stroke = stroke, StrokeThickness = width, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false,
                };
                desired.Add(line); desired.Add(head); graphEdgeLines.Add((edge.Target, line, head));
            }
            foreach (var block in blocks)
            {
                var card = BuildGraphCard(block, files, pane);
                Canvas.SetLeft(card, frames[block.Id].X); Canvas.SetTop(card, frames[block.Id].Y);
                card.Width = frames[block.Id].W; card.Height = frames[block.Id].H;
                graphCards[block.Id] = card; graphBlockKinds[block.Id] = block.Kind; desired.Add(card);
                AddActivityOutline(block, frames[block.Id], desired);
                if (block.Kind == "draft") desired.Add(DraftOutline(frames[block.Id]));
                if (LoadedFromRecord(block.Id, older)) ToolTipService.SetToolTip(card, Locale.Get("graph.history.tag"));
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
            var wanted = desired.ToHashSet();
            for (var i = graphCanvas.Children.Count - 1; i >= 0; i--)
                if (!wanted.Contains(graphCanvas.Children[i])) graphCanvas.Children.RemoveAt(i);
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
            ReaimGraphCamera(runs, layout, frames, viewport, ObserveResultReveal(pane, retained, viewport));
            AutoLoadGraphHistory(pane, retained);
            UpdateActivityTimer();
        }

        /// One observation for the reveal rule: the pane's own runs (not those
        /// read back from the record), its running state, its draft and the
        /// viewport. Returns the result card to place above the composer now.
        private string? ObserveResultReveal(RunSession pane, IReadOnlyList<MightyGraphRun> own, (double W, double H)? viewport)
        {
            var running = pane.Status == "running";
            var draftEmpty = string.IsNullOrEmpty(pane.Draft);
            var current = own.Select(r => new MightyGraphCamera.ResultReveal.RunProgress(r.Id, MightyGraphLayout.Finished(r))).ToList();
            var shown = graphRunProgress is not null;
            graphReveal.PaneRunning(shown && graphWasRunning, running);
            if (shown) graphReveal.DraftChanged(graphDraftWasEmpty, draftEmpty);
            var (holding, awaiting) = (graphReveal.HoldingID, graphReveal.AwaitingMeasure);
            var place = graphReveal.RunsChanged(graphRunProgress ?? current, current, run => MightyGraphBlockSize.NodeId(run, "result"), graphResultHeights.ContainsKey);
            // An answer that is never drawn is never measured: place it at its cap.
            if (graphReveal.AwaitingMeasure && graphReveal.HoldingID is { } held && (!awaiting || held != holding)) RevealAfterTimeout(held);
            if (graphRevealPendingId is { } pending && pending == graphReveal.HoldingID) place ??= pending;
            graphRevealPendingId = null;
            // A result still held above the composer stays there when the pane resizes.
            if (place is null && shown && graphLastViewport is not null && viewport is not null && viewport != graphLastViewport) place = graphReveal.ViewportChanged();
            graphRunProgress = current; graphWasRunning = running; graphDraftWasEmpty = draftEmpty; graphLastViewport = viewport;
            return place;
        }

        private void RevealAfterTimeout(string nodeId) =>
            _ = Task.Delay(300).ContinueWith(_ => Container.DispatcherQueue.TryEnqueue(() =>
            {
                if (!QueuePaneAlive || graphHost?.Visibility != Visibility.Visible ||
                    owner.service.Snapshot.Sessions.FirstOrDefault(p => p.Id == id) is not { } current) return;
                if (graphReveal.MeasureTimedOut(nodeId) is not { } place) return;
                graphRevealPendingId = place;
                RefreshMightyView(current);
            }), TaskScheduler.Default);

        /// The newest result card's content measured a new height: the card fits
        /// it, and a card held above the composer is placed again at that height.
        private void ResultMeasured(string nodeId, double height)
        {
            TraceGraphSmoke($"result-measured:{height:F2}:previous={graphResultHeights.GetValueOrDefault(nodeId, -1):F2}");
            if (graphResizing || graphLiveResultSize is not null || !double.IsFinite(height)) return;
            if (graphResultHeights.TryGetValue(nodeId, out var known) && Math.Abs(known - height) <= 0.5) return;
            graphResultHeights.Clear(); graphResultHeights[nodeId] = height;
            if (graphReveal.ContentMeasured(nodeId) is { } place) graphRevealPendingId = place;
            QueueGraphRefresh();
        }

        /// The user's own scroll, drag or zoom, or any other re-aim: the result
        /// above the composer is no longer followed.
        private void CancelResultReveal() { graphReveal.Cancel(); graphRevealPendingId = null; }

        /// A new request or a new result re-aims the camera the way macOS does;
        /// <paramref name="reveal"/>, a result of the pane's own that just
        /// finished, goes right above the composer instead.
        private void ReaimGraphCamera(IReadOnlyList<MightyGraphRun> runs, MightyGraphLayout layout, Dictionary<string, GraphRect> frames, (double W, double H)? viewport, string? reveal)
        {
            if (viewport is not { } size) return;
            var newestRunId = runs.Count > 0 ? runs[^1].Id : null;
            var latestResultId = MightyGraphLayout.LatestResultID(runs);
            if (reveal is not null && frames.TryGetValue(reveal, out var revealed))
            {
                graphAimedRunId = newestRunId; graphAimedResultId = latestResultId;
                var place = MightyGraphCamera.CameraOffset(revealed, size, graphZoom, alignTop: false, alignBottom: true);
                graphPan.X = place.X; graphPan.Y = place.Y;
                return;
            }
            if (newestRunId == graphAimedRunId && latestResultId == graphAimedResultId) return;
            var newResult = latestResultId is not null && latestResultId != graphAimedResultId;
            graphAimedRunId = newestRunId; graphAimedResultId = latestResultId;
            // The held result waits for its height, so it lands once, not twice.
            if (latestResultId is not null && graphReveal.HoldingID == latestResultId) return;
            var ids = layout.Nodes.Select(n => n.Id).ToHashSet();
            var anchor = newResult && graphSelection is null
                ? MightyGraphCamera.Anchor.Reaim(latestResultId!, true)
                : MightyGraphCamera.ReaimAnchor(newestRunId, graphSelection, ids);
            if (anchor.NodeID is not { } target || !frames.TryGetValue(target, out var frame)) return;
            CancelResultReveal();
            var camera = MightyGraphCamera.CameraOffset(frame, size, graphZoom, anchor.AlignTop);
            graphPan.X = camera.X; graphPan.Y = camera.Y;
        }

        /// <summary>A block's inner inset: 12, the draft 16 (M/MightyGraphView.swift:404-420, 487-554).</summary>
        private static double BlockInset(string kind) => kind == "draft" ? 16 : 12;

        /// <summary>
        /// One block on the D card (M/MightyGraphActivityView.swift:142-146): <c>card</c>, a 1pt <c>line</c>
        /// edge, radius 12, nothing inset, so the header strip and the request band run edge to edge.
        /// Under the 38pt header: the request on its tint × 0.055 band, then the transcript inset 12.
        /// The files panel keeps a 12pt padding of its own.
        /// </summary>
        private Border BuildGraphCard(MightyGraphBlock block, IReadOnlyList<ResultFiles.ResultFile> files, RunSession pane)
        {
            var b = owner.brushes;
            var inset = BlockInset(block.Kind);
            if (!graphCardViews.TryGetValue(block.Id, out var view))
            {
                var card = new Border
                {
                    CornerRadius = new CornerRadius(DesignMetrics.Radius.Block), BorderThickness = new Thickness(DesignMetrics.Stroke.Line),
                    BorderBrush = b.Brush(DesignToken.Line), Background = b.Brush(DesignToken.Card),
                    Padding = new Thickness(block.Kind == "resultFiles" ? 12 : 0), Tag = block.Id,
                };
                view = new GraphCardView { Card = card }; graphCardViews[block.Id] = view;
                AutomationProperties.SetAutomationId(card, "mighty-node-" + block.Id);
                card.PointerPressed += (_, args) => { SelectGraphBlock(block.Id); graphViewport?.Focus(FocusState.Pointer); args.Handled = true; };
                if (block.Kind != "resultFiles")
                {
                    view.Body = new Grid();
                    view.Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    view.Body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    view.Content = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
                    view.Request = new TextBlock { FontSize = DesignMetrics.Type.Pill, TextWrapping = TextWrapping.Wrap, Foreground = b.Brush(DesignToken.Ink) };
                    // The draft shows its text plainly; every other block's request sits on its tinted band over a line.
                    view.RequestBand = new Border
                    {
                        Child = view.Request, Visibility = Visibility.Collapsed,
                        Padding = block.Kind == "draft" ? new Thickness(inset, 0, inset, inset) : new Thickness(inset, 8, inset, 8),
                        BorderThickness = new Thickness(0, 0, 0, block.Kind == "draft" ? 0 : DesignMetrics.Stroke.Line), BorderBrush = b.Brush(DesignToken.Line),
                    };
                    view.Content.Children.Add(view.RequestBand);
                    var captured = view;
                    view.Content.SizeChanged += (_, _) => QueueGraphCardMeasure(block.Id, captured);
                    card.Loaded += (_, _) => QueueGraphCardMeasure(block.Id, captured);
                    var scroll = new ScrollViewer { Content = view.Content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollMode = ScrollMode.Enabled };
                    graphBodies[block.Id] = scroll; Grid.SetRow(scroll, 1); view.Body.Children.Add(scroll);
                    if (block.Kind is "request" or "result" or "agent" or "draft") view.Body.Children.Add(BuildResultResizeGrip(block.Id));
                    card.Child = view.Body;
                }
            }
            // Frame is deliberately excluded. Resizing or camera movement must
            // never tear down a RichEditBox while native text services load it.
            var look = block.Kind == "request" ? RequestStyleLook(block.Request) : (Icon: "", Tint: "");
            var fingerprint = JsonSerializer.Serialize(new
            {
                block.Kind, block.Title, block.Request, block.Entries, block.State, block.Status, block.Indicator,
                block.Capsule, block.CapsuleHelp, block.ResultFilesRunId,
                Files = block.Kind is "result" or "resultFiles" ? files : [],
                FilesOpen = graphResultFilesRunId, Selected = graphSelection == block.Id,
                Latest = graphLatestResultId == block.Id, SavedResult = pane.GraphResultSize is not null,
                CustomSize = pane.GraphBlockSizes?.ContainsKey(block.Id) == true, Expanded = graphExpanded.Contains(block.Id),
                // No theme: every colour is a shared brush recoloured in place, and the transcript takes a
                // theme change through RethemeMightyTranscripts, so a toggle rebuilds nothing.
                StyleIcon = look.Icon, StyleTint = look.Tint, Language = Locale.LanguagePreference,
            }, Wire.Json);
            if (view.Fingerprint == fingerprint)
            {
                if (block.Id == graphLatestResultId) graphFitResultButton = view.FitButton;
                return view.Card;
            }
            view.Fingerprint = fingerprint;
            AutomationProperties.SetName(view.Card, block.Title);
            if (block.Kind == "resultFiles") { view.Card.Child = BuildResultFilesPanel(block, files); return view.Card; }
            var header = BuildGraphCardHeader(block, files, view);
            if (view.Header is not null) view.Body!.Children.Remove(view.Header);
            view.Header = header; view.Body!.Children.Insert(0, header);
            view.FitButton = block.Id == graphLatestResultId && pane.GraphResultSize is not null ? graphFitResultButton : null;
            var currentView = view;
            header.SizeChanged += (_, _) => QueueGraphCardMeasure(block.Id, currentView);
            view.Request!.Text = block.Request;
            view.RequestBand!.Visibility = block.Request.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (block.Kind != "draft") view.RequestBand.Background = b.Brush(BlockTint(block, look.Tint), DesignMetrics.Opacity.InputPreview);
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
                transcript.View.Margin = new Thickness(inset, 8, inset, inset);
                view.Transcript = transcript; graphTranscripts[block.Id] = transcript;
                view.Content!.Children.Add(transcript.View);
            }
            if (view.Transcript is { } existing)
            {
                existing.View.Visibility = block.Entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                existing.Update(pane with { Logs = [.. block.Entries], Kind = "claude" }, !owner.DarkTheme, owner.pictures, Workspace.Path);
            }
            return view.Card;
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
            "question" => DesignToken.QuestionText, "agent" => DesignToken.AgentText,
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
                // The card insets nothing and its rows have no spacing: only its own edge (1pt, 2pt when selected) is added.
                ResultMeasured(nodeId, view.Card.BorderThickness.Top + view.Card.BorderThickness.Bottom + view.Header.ActualHeight + view.Content.ActualHeight);
            })) view.MeasureQueued = false;
        }

        /// <summary>
        /// A block's 38pt header (M/MightyGraphView.swift:483-546): padding h12, spacing 7, over a 1pt
        /// <c>line</c>. The title is 12 bold <c>ink</c> after the request's style icon in its tint; on the
        /// right, the selected block's scroll hint, the activity mark, the status pill (the draft's plain
        /// state word), the usage capsule on <c>cardRaised</c>, the fit and files buttons and the size
        /// controls in <c>ink2</c>. The result's header is the outcome strip instead: <c>heroFill</c> of its
        /// tone with every word in <c>heroInk</c> and no pill (Core gives a result no usage capsule).
        /// </summary>
        private Grid BuildGraphCardHeader(MightyGraphBlock block, IReadOnlyList<ResultFiles.ResultFile> files, GraphCardView view)
        {
            var b = owner.brushes;
            var strip = block.Kind == "result";
            var plain = block.Kind == "draft";
            var tone = MightyGraphBlockModel.Tone(block);
            var ink = strip ? b.FillInk(tone) : b.Brush(DesignToken.Ink);
            var quiet = strip ? b.FillInk(tone) : b.Brush(DesignToken.Ink2);
            var inset = BlockInset(block.Kind);
            var divider = !strip && !plain;
            var header = new Grid
            {
                ColumnSpacing = 7, Padding = new Thickness(inset, 0, inset, 0),
                Height = DesignMetrics.Layout.BlockHead + (divider ? DesignMetrics.Stroke.Line : 0),
                BorderThickness = new Thickness(0, 0, 0, divider ? DesignMetrics.Stroke.Line : 0), BorderBrush = b.Brush(DesignToken.Line),
            };
            if (strip)
            {
                // The strip fills the card's top inside its 1pt edge, so its corners follow the card's.
                var corner = DesignMetrics.Radius.Block - DesignMetrics.Stroke.Line;
                header.Background = b.Fill(tone); header.CornerRadius = new CornerRadius(corner, corner, 0, 0);
            }
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            // A request block's title ends with its agent's name; its mark goes before it.
            var title = ProviderMarkView.Labelled(block.Title, MightyGraphBlockModel.TitleProvider(block, Session.Provider), DesignMetrics.Type.Block, plain ? FontWeights.SemiBold : FontWeights.Bold);
            PaintWords(title, ink);
            graphTitles[block.Id] = title;
            var look = block.Kind == "request" ? RequestStyleLook(block.Request) : (Icon: "", Tint: "");
            FrameworkElement? glyph = block.Kind switch
            {
                "request" => new TextBlock { Text = look.Icon, FontSize = 11, Foreground = b.Brush(BlockTint(block, look.Tint)), VerticalAlignment = VerticalAlignment.Center },
                // Segoe Fluent Icons CheckMark / Warning / Stop for the Mac's checkmark.seal / exclamationmark.triangle / stop.circle.
                "result" => new FontIcon { Glyph = tone == DesignTone.Err ? "\uE7BA" : tone == DesignTone.Stop ? "\uE71A" : "\uE73E", FontSize = 11, Foreground = ink, VerticalAlignment = VerticalAlignment.Center },
                // Segoe Fluent Icons Edit for the draft's square.and.pencil (M/MightyGraphView.swift:407).
                "draft" => new FontIcon { Glyph = "\uE70F", FontSize = 11, Foreground = ink, VerticalAlignment = VerticalAlignment.Center },
                _ => null,
            };
            if (glyph is not null)
            {
                var labelled = new Grid { ColumnSpacing = 7 }; labelled.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); labelled.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                labelled.Children.Add(glyph);
                Grid.SetColumn(title, 1); labelled.Children.Add(title); header.Children.Add(labelled);
            }
            else header.Children.Add(title);

            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            if (graphSelection == block.Id)
                right.Children.Add(new TextBlock { Text = Locale.Get(MightyGraphViewModel.LocaleKeyBlockScrolling), FontSize = DesignMetrics.Type.Badge, FontWeight = FontWeights.Medium, Foreground = strip ? ink : b.Brush(DesignToken.Accent), VerticalAlignment = VerticalAlignment.Center });
            view.Activity = null; view.StatePill = null; view.Capsule = null; graphActivitySets.Remove(block.Id);
            if (!strip && BuildIndicator(block.Indicator, view, block.Id) is { } indicator) right.Children.Add(indicator);
            if (plain && block.State.Length > 0) right.Children.Add(new TextBlock { Text = block.State, FontSize = DesignMetrics.Type.Pill, Foreground = quiet, VerticalAlignment = VerticalAlignment.Center });
            else if (!strip && block.State.Length > 0) right.Children.Add(view.StatePill = StatusPill(block.State, tone, 18));
            if (block.Capsule is { } capsule)
            {
                var pill = new Border
                {
                    CornerRadius = new CornerRadius(9), Padding = new Thickness(6, 2, 6, 2), VerticalAlignment = VerticalAlignment.Center,
                    Background = b.Brush(DesignToken.CardRaised),
                    Child = new TextBlock { Text = capsule, FontSize = DesignMetrics.Type.Small, FontFamily = new FontFamily(DesignMetrics.Font.Mono), Foreground = quiet },
                };
                AutomationProperties.SetAutomationId(pill, "mighty-tokens-" + block.Id);
                AutomationProperties.SetName(pill, block.CapsuleHelp);
                ToolTipService.SetToolTip(pill, block.CapsuleHelp);
                right.Children.Add(view.Capsule = pill);
            }
            // A saved size is in force: offer the way back to the window fit.
            if (block.Kind == "result" && block.Id == graphLatestResultId && Session.GraphResultSize is not null)
            {
                var label = Locale.Get(MightyGraphViewModel.LocaleKeyResultFitToWindow);
                var fit = HeaderButton(new TextBlock { Text = label, FontSize = DesignMetrics.Type.Small, FontWeight = strip ? FontWeights.Bold : FontWeights.Normal }, strip ? ink : b.Brush(DesignToken.Accent));
                AutomationProperties.SetAutomationId(fit, "mighty-fit-result-" + block.Id);
                AutomationProperties.SetName(fit, label);
                ToolTipService.SetToolTip(fit, label);
                fit.Click += async (_, _) => await FitResultToWindow();
                right.Children.Add(fit); graphFitResultButton = fit;
            }
            if (block.ResultFilesRunId is { } runId && files.Count > 0)
            {
                var open = graphResultFilesRunId == runId;
                var text = Locale.Get(open ? MightyGraphViewModel.LocaleKeyResultFilesClose : MightyGraphViewModel.LocaleKeyResultFilesOpen);
                var toggle = HeaderButton(new TextBlock { Text = "▤ " + files.Count, FontSize = DesignMetrics.Type.Small }, strip ? ink : b.Brush(open ? DesignToken.Accent : DesignToken.Ink2));
                AutomationProperties.SetAutomationId(toggle, "mighty-result-files-toggle-" + block.Id);
                AutomationProperties.SetName(toggle, Locale.Get(MightyGraphViewModel.LocaleKeyResultFilesCount, new Dictionary<string, string> { ["count"] = files.Count.ToString() }));
                ToolTipService.SetToolTip(toggle, text);
                toggle.Click += (_, _) => ToggleResultFiles(runId);
                right.Children.Add(toggle);
            }
            AddGraphBlockSizeControls(right, block, quiet);
            Grid.SetColumn(right, 1); header.Children.Add(right);
            return header;
        }

        /// <summary>Every word of a title line (one text, or the head and name around an agent mark) in one ink.</summary>
        private static void PaintWords(FrameworkElement line, Brush ink, FontFamily? family = null)
        {
            IEnumerable<TextBlock> all = line switch { TextBlock single => [single], Panel panel => panel.Children.OfType<TextBlock>(), _ => [] };
            foreach (var words in all)
            {
                words.Foreground = ink;
                if (family is not null) words.FontFamily = family;
            }
        }

        /// <summary>A plain header button (the Mac's <c>.plain</c>): no fill at rest, the subtle wash under the pointer, in <paramref name="ink"/>.</summary>
        private Button HeaderButton(UIElement content, Brush ink)
        {
            var button = new Button { Content = content, MinWidth = 0, MinHeight = 0, Height = 22, Padding = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            owner.PaintPlainButton(button, owner.brushes.Transparent, owner.brushes.Subtle, ink: ink);
            return button;
        }

        /// <summary>
        /// A status as a pill (M/MightyGraphActivityView.swift:125-138): 10 bold in the tone's
        /// <c>text</c> ink on its <c>soft</c> tint, padding h7, <paramref name="height"/> tall
        /// (18 on a block, 19 on a timeline row, 20 on a timeline header), a capsule.
        /// </summary>
        private Border StatusPill(string text, DesignTone tone, double height) => new()
        {
            Height = height, CornerRadius = new CornerRadius(height / 2), Padding = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = owner.brushes.Soft(tone),
            Child = new TextBlock { Text = text, FontSize = DesignMetrics.Type.Small, FontWeight = FontWeights.Bold, Foreground = owner.brushes.Text(tone), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
        };

        /// <summary>
        /// The running block's activity mark (M/MightyGraphActivityView.swift:46-77), in an 18×14 box:
        /// four <c>run</c> capsules in a wave while it runs; a still <c>run</c> bolt with Windows
        /// animations off; the <c>waitText</c> pause while it waits; nothing once it finished.
        /// Core makes the choice.
        /// </summary>
        private FrameworkElement? BuildIndicator(string indicator, GraphCardView view, string blockId)
        {
            FrameworkElement? mark = indicator switch
            {
                "animating" => view.Activity = ActivityBars(blockId),
                // Segoe Fluent Icons LightningBolt for the Mac's bolt.fill.
                "static" => new FontIcon { Glyph = "\uE945", FontSize = 10, Foreground = owner.brushes.Brush(DesignToken.Run) },
                "waiting" => CircledPause(),
                _ => null,
            };
            if (mark is null) return null;
            mark.Width = MightyGraphActivity.BarBoxWidth; mark.Height = MightyGraphActivity.BarBoxHeight;
            mark.VerticalAlignment = VerticalAlignment.Center; mark.IsHitTestVisible = false;
            // The status pill beside it carries the state.
            AutomationProperties.SetAccessibilityView(mark, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            return mark;
        }

        /// <summary>
        /// The Mac's pause.circle (Segoe Fluent Icons has no circled pause): a 1pt <c>waitText</c> ring 12
        /// across round the Pause glyph, centred in the 18×14 box.
        /// </summary>
        private Grid CircledPause()
        {
            var ink = owner.brushes.Brush(DesignToken.WaitText);
            var mark = new Grid();
            mark.Children.Add(new Ellipse { Width = 12, Height = 12, Stroke = ink, StrokeThickness = DesignMetrics.Stroke.Line, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            mark.Children.Add(new FontIcon { Glyph = "\uE769", FontSize = 6, Foreground = ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            return mark;
        }

        /// <summary>
        /// The four capsules (Core <see cref="MightyGraphActivity.BarHeight"/>): 3 wide, 2 apart, each drawn
        /// <see cref="MightyGraphActivity.BarTallest"/> tall, centred in the box and scaled down about its
        /// middle. The pane's one timer (<see cref="UpdateActivityTimer"/>) moves only the scales, so a tick
        /// costs no layout, and the card around them never sees the clock.
        /// </summary>
        private Canvas ActivityBars(string blockId)
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
            graphActivitySets[blockId] = scales;
            return box;
        }

        /// <summary>The wave's phase now, from the system clock, so every running block waves in step.</summary>
        private static double ActivityPhase() => MightyGraphActivity.Phase(TimeSpan.FromMilliseconds(Environment.TickCount64), true);

        private static void WaveActivity(ScaleTransform[] scales, double phase)
        {
            for (var i = 0; i < scales.Length; i++) scales[i].ScaleY = MightyGraphActivity.BarHeight(i, phase) / MightyGraphActivity.BarTallest;
        }

        /// <summary>
        /// Runs the pane's one 24 fps capsule timer only while the diagram shows (its host and viewport
        /// visible and loaded) and at least one running block has capsules; stops it otherwise.
        /// </summary>
        private void UpdateActivityTimer()
        {
            var wave = graphActivitySets.Count > 0 && graphHost?.Visibility == Visibility.Visible && graphViewport is { Visibility: Visibility.Visible, IsLoaded: true };
            if (!wave) { graphActivityTimer?.Stop(); return; }
            if (graphActivityTimer is null)
            {
                graphActivityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1 / MightyGraphActivity.BarFramesPerSecond) };
                graphActivityTimer.Tick += (_, _) =>
                {
                    var phase = ActivityPhase();
                    foreach (var scales in graphActivitySets.Values) WaveActivity(scales, phase);
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
                // Rectangle strokes sit inside their bounds: the halo is the 4pt band outside the card.
                var band = 2 * MightyGraphActivity.LineWidth;
                glow = new Rectangle
                {
                    Width = frame.W + 2 * band, Height = frame.H + 2 * band,
                    RadiusX = MightyGraphActivity.CornerRadius + band, RadiusY = MightyGraphActivity.CornerRadius + band,
                    Stroke = owner.brushes.Brush(halo), StrokeThickness = band, IsHitTestVisible = false,
                };
                Canvas.SetLeft(glow, frame.X - band); Canvas.SetTop(glow, frame.Y - band);
                desired.Insert(Math.Max(0, desired.Count - 1), glow);
            }
            var line = new Rectangle
            {
                Width = frame.W, Height = frame.H,
                RadiusX = MightyGraphActivity.CornerRadius - 1, RadiusY = MightyGraphActivity.CornerRadius - 1,
                Stroke = owner.brushes.Brush(MightyGraphActivity.StrokeToken(outline)), StrokeThickness = MightyGraphActivity.LineWidth,
                StrokeDashCap = PenLineCap.Flat, IsHitTestVisible = false,
            };
            AutomationProperties.SetAutomationId(line, "mighty-outline-" + block.Id);
            AutomationProperties.SetAccessibilityView(line, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            if (outline == MightyGraphActivity.Marching)
            {
                // XAML dash lengths and offsets count in stroke widths.
                var dash = MightyGraphActivity.DashForCard(frame.W, frame.H);
                var dashes = new DoubleCollection();
                foreach (var length in DesignMetrics.Dash.InStrokeUnits(dash, MightyGraphActivity.LineWidth)) dashes.Add(length);
                line.StrokeDashArray = dashes;
                var offsets = DesignMetrics.Dash.InStrokeUnits([MightyGraphActivity.DashOffset(0, dash), MightyGraphActivity.DashOffset(1, dash)], MightyGraphActivity.LineWidth);
                var march = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = offsets[0], To = offsets[1],
                    Duration = new Duration(MightyGraphActivity.Period),
                    RepeatBehavior = Microsoft.UI.Xaml.Media.Animation.RepeatBehavior.Forever,
                    EnableDependentAnimation = true,
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(march, line);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(march, "StrokeDashOffset");
                var story = new Microsoft.UI.Xaml.Media.Animation.Storyboard(); story.Children.Add(march);
                line.Loaded += (_, _) => story.Begin(); line.Unloaded += (_, _) => story.Stop();
                stop = story.Stop;
            }
            Canvas.SetLeft(line, frame.X); Canvas.SetTop(line, frame.Y);
            desired.Add(line);
            graphOutlines[block.Id] = (outline, line);
            graphOutlineViews[block.Id] = (fingerprint, glow, line, stop);
        }

        /// <summary>
        /// The draft's dashed edge (M/MightyGraphView.swift:416): <c>accent</c> × 0.6 at 1.5pt in
        /// [5, 4] pt dashes, which WinUI counts in stroke widths. One line, moved with the draft.
        /// </summary>
        private Rectangle DraftOutline(GraphRect frame)
        {
            if (graphDraftOutline is null)
            {
                var dashes = new DoubleCollection();
                foreach (var length in DesignMetrics.Dash.InStrokeUnits(DesignMetrics.Dash.Draft, DesignMetrics.Dash.DraftWidth)) dashes.Add(length);
                graphDraftOutline = new Rectangle
                {
                    RadiusX = DesignMetrics.Radius.Block - DesignMetrics.Dash.DraftWidth / 2, RadiusY = DesignMetrics.Radius.Block - DesignMetrics.Dash.DraftWidth / 2,
                    Stroke = owner.brushes.Brush(DesignToken.Accent, DesignMetrics.Opacity.Draft), StrokeThickness = DesignMetrics.Dash.DraftWidth,
                    StrokeDashArray = dashes, StrokeDashCap = PenLineCap.Flat, IsHitTestVisible = false,
                };
                AutomationProperties.SetAccessibilityView(graphDraftOutline, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            }
            graphDraftOutline.Width = frame.W; graphDraftOutline.Height = frame.H;
            Canvas.SetLeft(graphDraftOutline, frame.X); Canvas.SetTop(graphDraftOutline, frame.Y);
            return graphDraftOutline;
        }

        // ── result files panel ────────────────────────────────────────────────

        private StackPanel BuildResultFilesPanel(MightyGraphBlock block, IReadOnlyList<ResultFiles.ResultFile> files)
        {
            var panel = new StackPanel { Spacing = 5 };
            AutomationProperties.SetAutomationId(panel, "mighty-result-files-" + block.Id);
            var head = new Grid { ColumnSpacing = 6 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock { Text = Locale.Get(MightyGraphViewModel.LocaleKeyResultFilesTitle) + " " + files.Count, FontSize = DesignMetrics.Type.Block, FontWeight = FontWeights.SemiBold, Foreground = owner.brushes.Brush(DesignToken.Ink) });
            var close = HeaderButton(new TextBlock { Text = Locale.Get(MightyGraphViewModel.LocaleKeyResultFilesClose), FontSize = DesignMetrics.Type.Small }, owner.brushes.Brush(DesignToken.Ink2));
            AutomationProperties.SetAutomationId(close, "mighty-result-files-close-" + block.Id);
            AutomationProperties.SetName(close, Locale.Get(MightyGraphViewModel.LocaleKeyResultFilesClose));
            close.Click += (_, _) => CloseResultFiles();
            Grid.SetColumn(close, 1); head.Children.Add(close);
            panel.Children.Add(head);
            var list = new StackPanel { Spacing = 2 };
            foreach (var file in files.Take(ResultFiles.MaximumResultFiles))
            {
                var row = HeaderButton(new TextBlock { Text = file.Path + (file.Line is { } line ? ":" + line : ""), FontSize = DesignMetrics.Type.Pill, TextTrimming = TextTrimming.CharacterEllipsis }, owner.brushes.Brush(DesignToken.Ink));
                row.HorizontalAlignment = HorizontalAlignment.Stretch; row.HorizontalContentAlignment = HorizontalAlignment.Left; row.Height = 24;
                AutomationProperties.SetName(row, file.Path);
                row.Click += async (_, _) => await OpenReferencePreview(file.Path, file.Line);
                list.Children.Add(row);
            }
            panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 280 });
            return panel;
        }

        /// Both transcript paths and result-list entries open the same in-pane viewer.
        private Task OpenResultFile(string path) =>
            OpenReferencePreview(path);

        private void ToggleResultFiles(string runId)
        {
            if (graphResultFilesRunId == runId) { CloseResultFiles(); return; }
            graphResultFilesRunId = runId; graphResultFilesClosedByHand = false; Refresh();
        }

        /// A manual close sticks until the next result.
        private void CloseResultFiles() { graphResultFilesRunId = null; graphResultFilesClosedByHand = true; RefreshMightyView(Session); }

        // ── the newest result card's size ─────────────────────────────────────

        /// The corner grip of the newest result card. The viewport captures the
        /// pointer, since every redraw replaces the card and its grip.
        private Border BuildResultResizeGrip(string nodeId)
        {
            var grip = new Border
            {
                // In the card's corner, 2pt inside its edge (the card insets nothing, so the grip does).
                Width = 16, Height = 16, Margin = new Thickness(0, 0, 2, 2), Background = owner.brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock { Text = "◢", FontSize = 10, Foreground = owner.brushes.Brush(DesignToken.Ink3), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false },
            };
            Grid.SetRowSpan(grip, 2);
            AutomationProperties.SetAutomationId(grip, "mighty-resize-result-" + nodeId);
            grip.PointerPressed += (_, args) =>
            {
                if (graphViewport is null || !graphCards.TryGetValue(nodeId, out var card)) return;
                graphViewport.Focus(FocusState.Pointer);
                graphResizing = true; graphDragging = false; CancelResultReveal();
                graphResizeOrigin = args.GetCurrentPoint(graphViewport).Position;
                graphResizeStart = (card.Width, card.Height);
                graphResizeLayout = graphLayout; graphResizeNodeId = nodeId;
                graphViewport.CapturePointer(args.Pointer);
                args.Handled = true;
            };
            grip.DoubleTapped += async (_, args) => { args.Handled = true; await ResetGraphBlockSize(nodeId); };
            AutomationProperties.SetName(grip, Locale.Get("graph.block.resize"));
            return grip;
        }

        /// The size under the cursor, in diagram coordinates.
        private (double W, double H) DraggedResultSize(Windows.Foundation.Point point)
        {
            var scale = graphZoom > 0 ? graphZoom : 1;
            return (graphResizeStart.W + (point.X - graphResizeOrigin.X) / scale, graphResizeStart.H + (point.Y - graphResizeOrigin.Y) / scale);
        }

        /// Live: the card follows the cursor up to the pane's edge and stops there.
        private void MoveResultResize(Windows.Foundation.Point point)
        {
            if (graphResizeNodeId != graphLatestResultId)
            {
                var raw = DraggedResultSize(point);
                if (new GraphBlockSize(raw.W, raw.H).Normalized is { } custom && graphResizeNodeId is { } node && graphCards.TryGetValue(node, out var changed))
                { changed.Width = custom.Width; changed.Height = custom.Height; }
                return;
            }
            var drag = MightyGraphLayout.ResultDrag(DraggedResultSize(point), true, true, MightyGraphLayout.ResizePhase.Live,
                Session.GraphResultSize, graphResizeLayout?.ResultLimit, graphResizeLayout?.ResultWindowFit);
            if (drag.Live is not { } live || graphLatestResultId is null || !graphCards.TryGetValue(graphLatestResultId, out var card)) return;
            graphLiveResultSize = live;
            card.Width = live.Width; card.Height = live.Height;
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
            if (graphResizeNodeId is { } nodeId && nodeId != graphLatestResultId)
            {
                graphResizeNodeId = null; graphResizeLayout = null;
                if (phase != MightyGraphLayout.ResizePhase.Cancelled)
                    _ = owner.Act(async () => { await Change(p => GraphBlockPreferences.Set(p, nodeId, new(dragged.W, dragged.H))); RefreshMightyView(Session); });
                else RefreshMightyView(Session);
                return;
            }
            graphResizeNodeId = null;
            var drag = MightyGraphLayout.ResultDrag(dragged, sides.Horizontal, sides.Vertical, phase,
                Session.GraphResultSize, graphResizeLayout?.ResultLimit, graphResizeLayout?.ResultWindowFit);
            graphResizeLayout = null; graphLiveResultSize = null;
            if (drag.Save is { } save) _ = owner.Act(async () => { await Change(p => p with { GraphResultSize = save }); RefreshMightyView(Session); });
            else RefreshMightyView(Session);
        }

        /// Clears the saved size: the newest result card fits the window again.
        private Task FitResultToWindow() =>
            owner.Act(async () => { await Change(p => p with { GraphResultSize = null }); RefreshMightyView(Session); });

        // ── zoom, pan and selection ───────────────────────────────────────────

        internal void SetGraphZoom(double value)
        {
            var changed = graphZoom != value;
            graphZoom = value; graphScale.ScaleX = graphScale.ScaleY = value;
            if (zoomResetButton is not null) ((TextBlock)zoomResetButton.Content).Text = MightyGraphViewModel.ZoomLabel(value);
            if (zoomOutButton is not null) zoomOutButton.IsEnabled = !MightyGraphViewModel.ZoomOutDisabled(value);
            if (zoomInButton is not null) zoomInButton.IsEnabled = !MightyGraphViewModel.ZoomInDisabled(value);
            // The newest result card's pane limit is in diagram coordinates at this zoom.
            if (changed) CancelResultReveal();
            if (changed && graphHost?.Visibility == Visibility.Visible) RefreshMightyView(Session);
        }

        internal double GraphZoom => graphZoom;
        internal string? GraphSelection => graphSelection;
        internal int GraphBlockCount => graphCards.Count;

        /// The wheel scrolls the selected block's body; with nothing selected it
        /// pans the canvas up and down, and left and right for a horizontal
        /// wheel, a tilt or Shift+wheel. Either way it never reaches the outer page.
        private void OnGraphWheel(object sender, PointerRoutedEventArgs args)
        {
            var properties = args.GetCurrentPoint(graphViewport).Properties;
            var shift = (args.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0;
            var (panX, panY) = MightyGraphViewModel.WheelPan(properties.MouseWheelDelta, properties.IsHorizontalMouseWheel, shift);
            if (MightyGraphViewModel.WheelScrollsBlock(graphSelection) && graphSelection is { } selected && graphBodies.TryGetValue(selected, out var body))
            {
                // The block body only scrolls up and down.
                if (panY != 0) body.ChangeView(null, Math.Max(0, body.VerticalOffset - panY), null, true);
            }
            else UserPan(panX, panY);
            args.Handled = true;
        }

        /// The user's own wheel pan: it ends a result held above the composer,
        /// and only a move towards the top loads more.
        private void UserPan(double dx, double dy)
        {
            CancelResultReveal();
            graphPan.X += dx; graphPan.Y += dy;
            if (dy > 0) LoadOlderAtTop();
        }

        private void OnGraphPointerPressed(object sender, PointerRoutedEventArgs args)
        {
            // Only the empty background reaches here: a card handles its own press.
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
            if (!graphDragging) return;
            var point = args.GetCurrentPoint(graphViewport).Position;
            var previousY = graphPan.Y;
            // A click alone keeps a result held above the composer; a move does not.
            if (point != graphDragOrigin) CancelResultReveal();
            graphPan.X = graphDragPanX + (point.X - graphDragOrigin.X);
            graphPan.Y = graphDragPanY + (point.Y - graphDragOrigin.Y);
            // Only the user's own move towards the top loads more.
            if (graphPan.Y > previousY) LoadOlderAtTop();
            args.Handled = true;
        }

        private void OnGraphPointerReleased(object sender, PointerRoutedEventArgs args)
        {
            if (graphResizing) EndResultResize(MightyGraphLayout.ResizePhase.Finished, args.GetCurrentPoint(graphViewport).Position);
            graphDragging = false; graphViewport?.ReleasePointerCapture(args.Pointer);
        }

        internal void SelectGraphBlock(string? nodeId)
        {
            graphSelection = MightyGraphViewModel.ApplySelection(graphSelection, nodeId);
            ApplyGraphSelectionStyle();
        }

        internal void ClearGraphSelection() => SelectGraphBlock(null);

        /// <summary>The selected block's edge is 2pt <c>accent</c>; the others keep 1pt <c>line</c>, the draft only its dashes.</summary>
        private void ApplyGraphSelectionStyle()
        {
            foreach (var (nodeId, card) in graphCards)
            {
                var selected = nodeId == graphSelection;
                var draft = graphBlockKinds.GetValueOrDefault(nodeId) == "draft";
                card.BorderThickness = new Thickness(selected ? DesignMetrics.Stroke.Active : draft ? 0 : DesignMetrics.Stroke.Line);
                card.BorderBrush = owner.brushes.Brush(selected ? DesignToken.Accent : DesignToken.Line);
            }
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
        internal string? GraphRevealHoldingForSmoke => graphReveal.HoldingID;
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
            var edges = graphCanvas.Children.OfType<Line>().Count();
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
