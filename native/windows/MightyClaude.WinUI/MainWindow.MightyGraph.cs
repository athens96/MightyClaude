using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using Line = Microsoft.UI.Xaml.Shapes.Line;
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
        private StackPanel? modeSwitch;

        // ── canvas ────────────────────────────────────────────────────────────

        private Grid? graphHost;
        private readonly Canvas graphCanvas = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        private readonly TranslateTransform graphPan = new();
        private readonly ScaleTransform graphScale = new() { ScaleX = 1, ScaleY = 1 };
        private readonly TextBlock graphTotal = new() { FontSize = 10, Opacity = .7, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        private Button? zoomOutButton, zoomResetButton, zoomInButton;
        private Border? graphViewport;
        private double graphZoom = MightyGraphViewModel.ZoomDefault;
        private string? graphSelection;
        private readonly Dictionary<string, ScrollViewer> graphBodies = [];
        private readonly Dictionary<string, AgentTranscript> graphTranscripts = [];
        private bool graphDeferredDraw;
        private readonly Dictionary<string, Border> graphCards = [];
        private readonly Dictionary<string, (string Kind, Rectangle Line)> graphOutlines = [];
        private readonly Dictionary<string, string> graphBlockKinds = [];
        // Each card's header title as drawn, so the smoke can read which carry an agent mark.
        private readonly Dictionary<string, FrameworkElement> graphTitles = [];
        private bool graphDragging, graphDrawing;
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
        private const double GraphCardChrome = 12 * 2 + 1 * 2 + 6;

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
            if (!MightyGraphViewModel.ShowsModeSwitch(Session)) return;
            anchor.Loaded += (_, _) => BuildMightyView();
        }

        /// <summary>Builds the view now instead of waiting for the anchor to load.</summary>
        internal void EnsureMightyView() => BuildMightyView();

        private void BuildMightyView()
        {
            if (graphAttached || Container.Child is not Grid grid) return;
            if (grid.Children.OfType<Grid>().FirstOrDefault(child => Grid.GetRow(child) == 0) is not { } header) return;
            graphAttached = true;

            modeDefaultButton = ModePill(MightyGraphViewModel.LocaleKeyDefault, "default");
            modeMightyButton = ModePill(MightyGraphViewModel.LocaleKeyMighty, "mighty");
            modeSwitch = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            modeSwitch.Children.Add(modeDefaultButton); modeSwitch.Children.Add(modeMightyButton);
            Grid.SetColumn(modeSwitch, 1); header.Children.Add(modeSwitch);

            graphHost = new Grid { RowSpacing = 0, Visibility = Visibility.Collapsed };
            graphHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            graphHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            graphHost.Children.Add(BuildGraphToolbar());

            graphCanvas.RenderTransform = new TransformGroup { Children = { graphScale, graphPan } };
            graphViewport = new Border { Child = graphCanvas, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(10, 135, 135, 135)), CornerRadius = new CornerRadius(8) };
            // The canvas is larger than the pane; clip it so a panned block never
            // paints over the composer or the neighbouring pane.
            graphViewport.SizeChanged += (_, args) =>
            {
                graphViewport.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, args.NewSize.Width, args.NewSize.Height) };
                if (graphHost?.Visibility == Visibility.Visible) RefreshMightyView(Session);
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
            Grid.SetRow(graphViewport, 1); graphHost.Children.Add(graphViewport);
            BuildMightyTimeline();
            Grid.SetRow(graphHost, 1); grid.Children.Add(graphHost);

            // A finished request records a graph run on this session; redraw the
            // canvas when one arrives, and drop the handler once the pane is gone.
            owner.service.RunEventReceived += OnGraphRunEvent;
            SetGraphZoom(graphZoom);
            RefreshMightyView(Session);
        }

        private void OnGraphRunEvent(RunEvent value)
        {
            if (value.SessionId != id) return;
            Container.DispatcherQueue.TryEnqueue(() =>
            {
                if (!owner.views.ContainsKey(id)) { owner.service.RunEventReceived -= OnGraphRunEvent; return; }
                if (graphHost?.Visibility == Visibility.Visible) RefreshMightyView(Session);
            });
        }

        private Grid BuildGraphToolbar()
        {
            var bar = new Grid { ColumnSpacing = 6, Padding = new Thickness(2, 0, 2, 6) };
            bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            AutomationProperties.SetAutomationId(graphTotal, "mighty-tokens-" + id);
            var summary = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            summary.Children.Add(graphStyleHeader); summary.Children.Add(graphTotal); bar.Children.Add(summary);
            var zoom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            zoomOutButton = ZoomPill(Locale.Get(MightyGraphViewModel.LocaleKeyZoomOut), "mighty-zoom-out-" + id, () => SetGraphZoom(MightyGraphViewModel.ZoomOut(graphZoom)));
            zoomResetButton = ZoomPill(MightyGraphViewModel.ZoomLabel(graphZoom), "mighty-zoom-reset-" + id, () => SetGraphZoom(MightyGraphViewModel.ZoomDefault));
            ToolTipService.SetToolTip(zoomResetButton, Locale.Get(MightyGraphViewModel.LocaleKeyZoomReset));
            AutomationProperties.SetName(zoomResetButton, Locale.Get(MightyGraphViewModel.LocaleKeyZoomReset));
            zoomInButton = ZoomPill(Locale.Get(MightyGraphViewModel.LocaleKeyZoomIn), "mighty-zoom-in-" + id, () => SetGraphZoom(MightyGraphViewModel.ZoomIn(graphZoom)));
            zoom.Children.Add(zoomOutButton); zoom.Children.Add(zoomResetButton); zoom.Children.Add(zoomInButton);
            graphZoomControls = zoom;
            var controls = new PillWrapPanel();
            controls.Children.Add(BuildGraphPresentationSwitch()); controls.Children.Add(zoom);
            Grid.SetColumn(controls, 1); bar.Children.Add(controls);
            void FitToolbar(double width)
            {
                var narrow = width < 540;
                Grid.SetColumnSpan(summary, narrow ? 2 : 1);
                Grid.SetRow(controls, narrow ? 1 : 0); Grid.SetColumn(controls, narrow ? 0 : 1); Grid.SetColumnSpan(controls, narrow ? 2 : 1);
                controls.MaxWidth = Math.Max(1, width - 4); controls.Margin = new Thickness(0, narrow ? 4 : 0, 0, 0);
            }
            bar.SizeChanged += (_, args) => FitToolbar(args.NewSize.Width);
            FitToolbar(Container.ActualWidth > 0 ? Container.ActualWidth : 500);
            graphToolbar = bar;
            return bar;
        }

        private static Button ZoomPill(string text, string automationId, Action act)
        {
            var button = new Button { Content = new TextBlock { Text = text, FontSize = 11 }, MinWidth = 0, MinHeight = 0, Height = 26, Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
            AutomationProperties.SetAutomationId(button, automationId); AutomationProperties.SetName(button, text);
            ToolTipService.SetToolTip(button, text);
            button.Click += (_, _) => act();
            return button;
        }

        private Button ModePill(string localeKey, string mode)
        {
            var text = Locale.Get(localeKey);
            var button = new Button { Content = new TextBlock { Text = text, FontSize = 11 }, MinWidth = 0, MinHeight = 0, Height = 26, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
            AutomationProperties.SetAutomationId(button, "mighty-mode-" + mode + "-" + id); AutomationProperties.SetName(button, text);
            button.Click += async (_, _) => await SetAgentViewMode(mode);
            return button;
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
            ((TextBlock)modeDefaultButton.Content).FontWeight = mighty ? FontWeights.Normal : FontWeights.SemiBold;
            ((TextBlock)modeMightyButton.Content).FontWeight = mighty ? FontWeights.SemiBold : FontWeights.Normal;
            graphHost.Visibility = mighty ? Visibility.Visible : Visibility.Collapsed;
            output.View.Visibility = mighty ? Visibility.Collapsed : Visibility.Visible;
            var timeline = mighty && MightyTimeline.Mode(pane) == "timeline";
            RefreshGraphPresentationSwitch(timeline);
            if (graphViewport is not null) graphViewport.Visibility = timeline ? Visibility.Collapsed : Visibility.Visible;
            if (timelineScroll is not null) timelineScroll.Visibility = timeline ? Visibility.Visible : Visibility.Collapsed;
            if (mighty && timeline) DrawMightyTimeline(pane);
            else if (mighty) DrawGraph(pane);
            // Hidden, the diagram watches nothing: a result that finishes meanwhile is not revealed.
            else { graphReveal = new(); graphRunProgress = null; graphRevealPendingId = null; }
        }

        /// Rebuilds the canvas from the pane's recorded graph runs. The block and
        /// edge list, every string and the indicator choice all come from Core.
        private void DrawGraph(RunSession pane)
        {
            // Drawing resizes the canvas, which can raise SizeChanged again.
            if (graphDrawing) return;
            if (graphTranscripts.Values.Any(transcript => transcript.IsSelecting)) { graphDeferredDraw = true; return; }
            graphDrawing = true;
            try { DrawGraphCore(pane); } finally { graphDrawing = false; }
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
            var catalog = owner.Runtime(pane.Provider)?.ModelCatalog?.Models;
            // `요청 N · Claude`: the short name, as macOS ProviderOptions.label, so its mark can go before it.
            var blocks = MightyGraphBlockModel.Blocks(layout, runs, pane.Draft, ProviderMark.Label(pane.Provider), AnimationsEnabled, catalog);
            for (var i = 0; i < runs.Count; i++)
            {
                var nodeId = MightyGraphLayout.NodeID(runs[i], "request");
                var at = blocks.FindIndex(block => block.Id == nodeId);
                if (at >= 0) blocks[at] = blocks[at] with { Title = GraphRequestTitle(runs[i], i + 1) };
            }

            foreach (var transcript in graphTranscripts.Values)
                if (transcript.View.Parent is Panel parent) parent.Children.Remove(transcript.View);
            foreach (var stale in graphTranscripts.Keys.Where(key => !blocks.Any(block => block.Id == key)).ToArray()) graphTranscripts.Remove(stale);
            graphCanvas.Children.Clear(); graphBodies.Clear(); graphCards.Clear(); graphOutlines.Clear(); graphBlockKinds.Clear(); graphTitles.Clear(); graphFitResultButton = null;
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
            foreach (var edge in layout.Edges)
            {
                if (!frames.TryGetValue(edge.Source, out var from) || !frames.TryGetValue(edge.Target, out var to)) continue;
                graphCanvas.Children.Add(new Line
                {
                    X1 = from.X + from.W / 2, Y1 = from.Y + from.H, X2 = to.X + to.W / 2, Y2 = to.Y,
                    Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(150, 100, 149, 237)), StrokeThickness = 1.5, IsHitTestVisible = false,
                });
            }
            foreach (var block in blocks)
            {
                var card = BuildGraphCard(block, files, pane);
                Canvas.SetLeft(card, frames[block.Id].X); Canvas.SetTop(card, frames[block.Id].Y);
                card.Width = frames[block.Id].W; card.Height = frames[block.Id].H;
                graphCards[block.Id] = card; graphBlockKinds[block.Id] = block.Kind; graphCanvas.Children.Add(card);
                AddActivityOutline(block, frames[block.Id]);
                if (LoadedFromRecord(block.Id, older)) ToolTipService.SetToolTip(card, Locale.Get("graph.history.tag"));
            }
            if (layout.Nodes.FirstOrDefault(n => n.Kind == "history") is { } history)
            {
                var card = BuildHistoryCard(pane, retained, older.Count, history.Frame);
                Canvas.SetLeft(card, history.Frame.X - layout.OriginX); Canvas.SetTop(card, history.Frame.Y - layout.OriginY);
                graphCanvas.Children.Add(card);
            }
            // The total counts the retained requests; loaded ones are named apart.
            graphTotal.Text = MightyGraphBlockModel.ToolbarSummary(retained)
                + (older.Count > 0 ? " · " + Locale.Get("graph.history.headerLoaded", new Dictionary<string, string> { ["count"] = older.Count.ToString() }) : "");
            var help = MightyGraphBlockModel.ToolbarHelp(retained);
            ToolTipService.SetToolTip(graphTotal, help); AutomationProperties.SetHelpText(graphTotal, help);
            ApplyGraphSelectionStyle();
            ReaimGraphCamera(runs, layout, frames, viewport, ObserveResultReveal(pane, retained, viewport));
            AutoLoadGraphHistory(pane, retained);
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
                if (graphReveal.MeasureTimedOut(nodeId) is not { } place) return;
                graphRevealPendingId = place;
                if (graphHost?.Visibility == Visibility.Visible) RefreshMightyView(Session);
            }), TaskScheduler.Default);

        /// The newest result card's content measured a new height: the card fits
        /// it, and a card held above the composer is placed again at that height.
        private void ResultMeasured(string nodeId, double height)
        {
            if (graphResizing || graphLiveResultSize is not null || !double.IsFinite(height)) return;
            if (graphResultHeights.TryGetValue(nodeId, out var known) && Math.Abs(known - height) <= 0.5) return;
            graphResultHeights.Clear(); graphResultHeights[nodeId] = height;
            if (graphReveal.ContentMeasured(nodeId) is { } place) graphRevealPendingId = place;
            Container.DispatcherQueue.TryEnqueue(() => { if (graphHost?.Visibility == Visibility.Visible) RefreshMightyView(Session); });
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

        private Border BuildGraphCard(MightyGraphBlock block, IReadOnlyList<ResultFiles.ResultFile> files, RunSession pane)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(80, 135, 135, 135)),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(22, 135, 135, 135)),
                Padding = new Thickness(12), Tag = block.Id,
            };
            AutomationProperties.SetAutomationId(card, "mighty-node-" + block.Id);
            AutomationProperties.SetName(card, block.Title);
            card.PointerPressed += (_, args) => { SelectGraphBlock(block.Id); graphViewport?.Focus(FocusState.Pointer); args.Handled = true; };

            if (block.Kind == "resultFiles") { card.Child = BuildResultFilesPanel(block, files); return card; }
            var resizable = block.Kind == "result" && block.Id == graphLatestResultId;

            var body = new Grid { RowSpacing = 6 };
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var header = BuildGraphCardHeader(block, files);
            body.Children.Add(header);

            // Top-aligned: a stretched child of the scrolling body is arranged at least as tall
            // as the body, so it would report the card's own height and never its content's.
            var content = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
            // The body scrolls, so the content takes its natural height: with the
            // header and the card's chrome that is the newest result's content height
            // (macOS AgentTranscriptView.onContentHeight). Neither depends on the card's
            // height, so fitting the card to it changes nothing measured here.
            if (resizable)
            {
                void Measured(object sender, SizeChangedEventArgs args) => ResultMeasured(block.Id, GraphCardChrome + header.ActualHeight + content.ActualHeight);
                content.SizeChanged += Measured; header.SizeChanged += Measured;
            }
            if (block.Request.Length > 0)
                content.Children.Add(new TextBlock { Text = block.Request, FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = .85 });
            if (block.Entries.Count > 0)
            {
                if (!graphTranscripts.TryGetValue(block.Id, out var transcript))
                {
                    transcript = new AgentTranscript { OpenReference = OpenReferencePreview, OpenImage = OpenTranscriptImage };
                    transcript.SelectionEnded = () => { if (graphDeferredDraw) { graphDeferredDraw = false; Container.DispatcherQueue.TryEnqueue(() => { if (QueuePaneAlive) RefreshMightyView(Session); }); } };
                    graphTranscripts[block.Id] = transcript;
                }
                transcript.View.MinHeight = 0;
                transcript.Update(pane with { Logs = [.. block.Entries], Kind = "claude" }, owner.service.Snapshot.Theme == "light", owner.pictures, Workspace.Path);
                content.Children.Add(transcript.View);
            }
            var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollMode = ScrollMode.Enabled };
            graphBodies[block.Id] = scroll;
            Grid.SetRow(scroll, 1); body.Children.Add(scroll);
            if (block.Kind is "request" or "result" or "agent" or "draft") body.Children.Add(BuildResultResizeGrip(block.Id));
            card.Child = body;
            return card;
        }

        private Grid BuildGraphCardHeader(MightyGraphBlock block, IReadOnlyList<ResultFiles.ResultFile> files)
        {
            var header = new Grid { ColumnSpacing = 6 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            // A request block's title ends with its agent's name; its mark goes before it.
            var title = ProviderMarkView.Labelled(block.Title, MightyGraphBlockModel.TitleProvider(block, Session.Provider), 12, FontWeights.SemiBold);
            graphTitles[block.Id] = title;
            if (block.Kind == "request")
            {
                var look = RequestStyleLook(block.Request);
                var labelled = new Grid { ColumnSpacing = 5 }; labelled.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); labelled.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                labelled.Children.Add(new TextBlock { Text = look.Icon, FontSize = 11, Foreground = new SolidColorBrush(StyleColor(look.Tint)), VerticalAlignment = VerticalAlignment.Center });
                Grid.SetColumn(title, 1); labelled.Children.Add(title); header.Children.Add(labelled);
            }
            else header.Children.Add(title);

            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            if (graphSelection == block.Id)
                right.Children.Add(new TextBlock { Text = Locale.Get(MightyGraphViewModel.LocaleKeyBlockScrolling), FontSize = 10, Opacity = .8 });
            if (BuildIndicator(block.Indicator) is { } indicator) right.Children.Add(indicator);
            if (block.State.Length > 0) right.Children.Add(new TextBlock { Text = block.State, FontSize = 10, Opacity = .65, VerticalAlignment = VerticalAlignment.Center });
            if (block.Capsule is { } capsule)
            {
                var pill = new Border
                {
                    CornerRadius = new CornerRadius(9), Padding = new Thickness(6, 1, 6, 1),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 135, 135, 135)),
                    Child = new TextBlock { Text = capsule, FontSize = 10, FontFamily = new FontFamily("Cascadia Mono"), Opacity = .8 },
                };
                AutomationProperties.SetAutomationId(pill, "mighty-tokens-" + block.Id);
                AutomationProperties.SetName(pill, block.CapsuleHelp);
                ToolTipService.SetToolTip(pill, block.CapsuleHelp);
                right.Children.Add(pill);
            }
            // A saved size is in force: offer the way back to the window fit.
            if (block.Kind == "result" && block.Id == graphLatestResultId && Session.GraphResultSize is not null)
            {
                var label = Locale.Get(MightyGraphViewModel.LocaleKeyResultFitToWindow);
                var fit = new Button { Content = new TextBlock { Text = label, FontSize = 10 }, MinWidth = 0, MinHeight = 0, Height = 22, Padding = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
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
                var toggle = new Button { Content = new TextBlock { Text = "▤ " + files.Count, FontSize = 10 }, MinWidth = 0, MinHeight = 0, Height = 22, Padding = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
                AutomationProperties.SetAutomationId(toggle, "mighty-result-files-toggle-" + block.Id);
                AutomationProperties.SetName(toggle, Locale.Get(MightyGraphViewModel.LocaleKeyResultFilesCount, new Dictionary<string, string> { ["count"] = files.Count.ToString() }));
                ToolTipService.SetToolTip(toggle, text);
                toggle.Click += (_, _) => ToggleResultFiles(runId);
                right.Children.Add(toggle);
            }
            AddGraphBlockSizeControls(right, block);
            Grid.SetColumn(right, 1); header.Children.Add(right);
            return header;
        }

        /// A running block moves, a waiting block shows a static pause mark and a
        /// finished block nothing. With Windows animations off the running block
        /// gets a static indicator instead — Core makes the choice.
        private static FrameworkElement? BuildIndicator(string indicator) => indicator switch
        {
            "animating" => Animated(new Border { Width = 26, Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Colors.CornflowerBlue), VerticalAlignment = VerticalAlignment.Center }),
            "static" => new Border { Width = 26, Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Colors.CornflowerBlue), VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Colors.CornflowerBlue) },
            "waiting" => new TextBlock { Text = "❙❙", FontSize = 9, Opacity = .7, VerticalAlignment = VerticalAlignment.Center },
            _ => null,
        };

        /// <summary>
        /// The edge of a block in motion, drawn over its card (macOS MightyGraphActivityOutline):
        /// a running block's 2pt run-blue line of 9/7 dashes walks one period per 1.6 s
        /// round the card over the soft run halo, still and solid with Windows animations
        /// off; a waiting block keeps a still amber line. Only the line's dash offset is
        /// animated: the card, its transcript and the layout never see the clock.
        /// </summary>
        private void AddActivityOutline(MightyGraphBlock block, GraphRect frame)
        {
            var outline = block.Outline;
            if (outline == MightyGraphActivity.None) return;
            if (MightyGraphActivity.HaloHex(outline, owner.DarkTheme) is { } halo)
            {
                // Rectangle strokes sit inside their bounds: the halo is the 4pt band outside the card.
                var band = 2 * MightyGraphActivity.LineWidth;
                var glow = new Rectangle
                {
                    Width = frame.W + 2 * band, Height = frame.H + 2 * band,
                    RadiusX = MightyGraphActivity.CornerRadius + band, RadiusY = MightyGraphActivity.CornerRadius + band,
                    Stroke = OutlineBrush(halo), StrokeThickness = band, IsHitTestVisible = false,
                };
                Canvas.SetLeft(glow, frame.X - band); Canvas.SetTop(glow, frame.Y - band);
                graphCanvas.Children.Insert(Math.Max(0, graphCanvas.Children.Count - 1), glow);
            }
            var line = new Rectangle
            {
                Width = frame.W, Height = frame.H,
                RadiusX = MightyGraphActivity.CornerRadius - 1, RadiusY = MightyGraphActivity.CornerRadius - 1,
                Stroke = OutlineBrush(MightyGraphActivity.StrokeHex(outline)), StrokeThickness = MightyGraphActivity.LineWidth,
                StrokeDashCap = PenLineCap.Flat, IsHitTestVisible = false,
            };
            AutomationProperties.SetAutomationId(line, "mighty-outline-" + block.Id);
            AutomationProperties.SetAccessibilityView(line, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            if (outline == MightyGraphActivity.Marching)
            {
                // XAML dash lengths and offsets count in stroke widths.
                var dash = MightyGraphActivity.DashForCard(frame.W, frame.H);
                var dashes = new DoubleCollection();
                foreach (var length in dash) dashes.Add(length / MightyGraphActivity.LineWidth);
                line.StrokeDashArray = dashes;
                var march = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = MightyGraphActivity.DashOffset(0, dash) / MightyGraphActivity.LineWidth,
                    To = MightyGraphActivity.DashOffset(1, dash) / MightyGraphActivity.LineWidth,
                    Duration = new Duration(MightyGraphActivity.Period),
                    RepeatBehavior = Microsoft.UI.Xaml.Media.Animation.RepeatBehavior.Forever,
                    EnableDependentAnimation = true,
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(march, line);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(march, "StrokeDashOffset");
                var story = new Microsoft.UI.Xaml.Media.Animation.Storyboard(); story.Children.Add(march);
                line.Loaded += (_, _) => story.Begin(); line.Unloaded += (_, _) => story.Stop();
            }
            Canvas.SetLeft(line, frame.X); Canvas.SetTop(line, frame.Y);
            graphCanvas.Children.Add(line);
            graphOutlines[block.Id] = (outline, line);
        }

        private static SolidColorBrush OutlineBrush(string hex)
        {
            var rgb = Convert.ToInt32(hex[1..], 16);
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        }

        private static Border Animated(Border bar)
        {
            var move = new TranslateTransform(); bar.RenderTransform = move;
            var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { From = -8, To = 8, Duration = new Duration(TimeSpan.FromSeconds(.8)), AutoReverse = true, RepeatBehavior = Microsoft.UI.Xaml.Media.Animation.RepeatBehavior.Forever };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, move);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "X");
            var story = new Microsoft.UI.Xaml.Media.Animation.Storyboard(); story.Children.Add(animation);
            bar.Loaded += (_, _) => story.Begin(); bar.Unloaded += (_, _) => story.Stop();
            return bar;
        }

        // ── result files panel ────────────────────────────────────────────────

        private StackPanel BuildResultFilesPanel(MightyGraphBlock block, IReadOnlyList<ResultFiles.ResultFile> files)
        {
            var panel = new StackPanel { Spacing = 5 };
            AutomationProperties.SetAutomationId(panel, "mighty-result-files-" + block.Id);
            var head = new Grid { ColumnSpacing = 6 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock { Text = Locale.Get(MightyGraphViewModel.LocaleKeyResultFilesTitle) + " " + files.Count, FontSize = 12, FontWeight = FontWeights.SemiBold });
            var close = new Button { Content = new TextBlock { Text = Locale.Get(MightyGraphViewModel.LocaleKeyResultFilesClose), FontSize = 10 }, MinWidth = 0, MinHeight = 0, Height = 22, Padding = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
            AutomationProperties.SetAutomationId(close, "mighty-result-files-close-" + block.Id);
            AutomationProperties.SetName(close, Locale.Get(MightyGraphViewModel.LocaleKeyResultFilesClose));
            close.Click += (_, _) => CloseResultFiles();
            Grid.SetColumn(close, 1); head.Children.Add(close);
            panel.Children.Add(head);
            var list = new StackPanel { Spacing = 2 };
            foreach (var file in files.Take(ResultFiles.MaximumResultFiles))
            {
                var row = new Button { Content = new TextBlock { Text = file.Path + (file.Line is { } line ? ":" + line : ""), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, MinHeight = 0, Height = 24, Padding = new Thickness(6, 0, 6, 0), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
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
                Width = 16, Height = 16, Margin = new Thickness(0, 0, -10, -10), Background = new SolidColorBrush(Colors.Transparent),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock { Text = "◢", FontSize = 10, Opacity = .45, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false },
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

        private void ApplyGraphSelectionStyle()
        {
            foreach (var (nodeId, card) in graphCards)
            {
                var selected = nodeId == graphSelection;
                card.BorderThickness = new Thickness(selected ? 2 : 1);
                card.BorderBrush = new SolidColorBrush(selected ? Colors.CornflowerBlue : Windows.UI.Color.FromArgb(80, 135, 135, 135));
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
