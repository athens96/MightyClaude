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
        private readonly Dictionary<string, Border> graphCards = [];
        private readonly Dictionary<string, string> graphBlockKinds = [];
        private bool graphDragging, graphDrawing;
        private Windows.Foundation.Point graphDragOrigin;
        private double graphDragPanX, graphDragPanY;
        private string? graphResultFilesRunId, graphResultFilesLastRunId, graphAimedRunId, graphAimedResultId;
        private bool graphResultFilesClosedByHand;

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
            graphViewport.PointerCaptureLost += (_, _) => graphDragging = false;
            graphViewport.KeyDown += (_, args) => { if (args.Key == Windows.System.VirtualKey.Escape) { ClearGraphSelection(); args.Handled = true; } };
            graphViewport.IsTabStop = true;
            Grid.SetRow(graphViewport, 1); graphHost.Children.Add(graphViewport);
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
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            AutomationProperties.SetAutomationId(graphTotal, "mighty-tokens-" + id);
            bar.Children.Add(graphTotal);
            var zoom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            zoomOutButton = ZoomPill(Locale.Get(MightyGraphViewModel.LocaleKeyZoomOut), "mighty-zoom-out-" + id, () => SetGraphZoom(MightyGraphViewModel.ZoomOut(graphZoom)));
            zoomResetButton = ZoomPill(MightyGraphViewModel.ZoomLabel(graphZoom), "mighty-zoom-reset-" + id, () => SetGraphZoom(MightyGraphViewModel.ZoomDefault));
            ToolTipService.SetToolTip(zoomResetButton, Locale.Get(MightyGraphViewModel.LocaleKeyZoomReset));
            AutomationProperties.SetName(zoomResetButton, Locale.Get(MightyGraphViewModel.LocaleKeyZoomReset));
            zoomInButton = ZoomPill(Locale.Get(MightyGraphViewModel.LocaleKeyZoomIn), "mighty-zoom-in-" + id, () => SetGraphZoom(MightyGraphViewModel.ZoomIn(graphZoom)));
            zoom.Children.Add(zoomOutButton); zoom.Children.Add(zoomResetButton); zoom.Children.Add(zoomInButton);
            Grid.SetColumn(zoom, 1); bar.Children.Add(zoom);
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
            if (mighty) DrawGraph(pane);
        }

        /// Rebuilds the canvas from the pane's recorded graph runs. The block and
        /// edge list, every string and the indicator choice all come from Core.
        private void DrawGraph(RunSession pane)
        {
            // Drawing resizes the canvas, which can raise SizeChanged again.
            if (graphDrawing) return;
            graphDrawing = true;
            try { DrawGraphCore(pane); } finally { graphDrawing = false; }
        }

        private void DrawGraphCore(RunSession pane)
        {
            var runs = (IReadOnlyList<MightyGraphRun>)(pane.GraphRuns ?? []);
            var latest = MightyGraphBlockModel.LatestCompletedRun(runs);
            List<ResultFiles.ResultFile> files = latest is null ? [] : MightyGraphBlockModel.FilesFor(latest, Workspace.Path);
            // A new result clears a previous manual close; only one panel is open.
            if (latest?.Id != graphResultFilesLastRunId) { graphResultFilesClosedByHand = false; graphResultFilesLastRunId = latest?.Id; }
            graphResultFilesRunId = MightyGraphViewModel.NextResultFilesRunID(graphResultFilesRunId, graphResultFilesClosedByHand, latest?.Id, files.Count > 0);

            var viewport = graphViewport is { ActualWidth: > 0 } v ? ((double W, double H)?)(v.ActualWidth, v.ActualHeight) : null;
            var layout = MightyGraphViewModel.CanvasLayout(runs, pane.Draft, pane.Status == "running", new HashSet<string>(), graphResultFilesRunId, viewport);
            var catalog = owner.Runtime(pane.Provider, pane.WorkspaceId)?.ModelCatalog?.Models;
            var blocks = MightyGraphBlockModel.Blocks(layout, runs, pane.Draft, ProviderCatalog.Name(pane.Provider), AnimationsEnabled, catalog);

            graphCanvas.Children.Clear(); graphBodies.Clear(); graphCards.Clear(); graphBlockKinds.Clear();
            graphCanvas.Width = Math.Max(1, layout.Size.W); graphCanvas.Height = Math.Max(1, layout.Size.H);
            var frames = new Dictionary<string, GraphRect>();
            foreach (var block in blocks)
            {
                var frame = block.Frame with { X = block.Frame.X - layout.OriginX };
                frames[block.Id] = frame;
            }
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
            }
            graphTotal.Text = MightyGraphBlockModel.ToolbarSummary(runs);
            var help = MightyGraphBlockModel.ToolbarHelp(runs);
            ToolTipService.SetToolTip(graphTotal, help); AutomationProperties.SetHelpText(graphTotal, help);
            ApplyGraphSelectionStyle();
            ReaimGraphCamera(runs, layout, frames, viewport);
        }

        /// A new request or a new result re-aims the camera the way macOS does.
        private void ReaimGraphCamera(IReadOnlyList<MightyGraphRun> runs, MightyGraphLayout layout, Dictionary<string, GraphRect> frames, (double W, double H)? viewport)
        {
            if (viewport is not { } size) return;
            var newestRunId = runs.Count > 0 ? runs[^1].Id : null;
            var latestResultId = MightyGraphLayout.LatestResultID(runs);
            if (newestRunId == graphAimedRunId && latestResultId == graphAimedResultId) return;
            var ids = layout.Nodes.Select(n => n.Id).ToHashSet();
            var anchor = latestResultId is not null && latestResultId != graphAimedResultId && graphSelection is null
                ? MightyGraphCamera.Anchor.Reaim(latestResultId, true)
                : MightyGraphCamera.ReaimAnchor(newestRunId, graphSelection, ids);
            graphAimedRunId = newestRunId; graphAimedResultId = latestResultId;
            if (anchor.NodeID is not { } target || !frames.TryGetValue(target, out var frame)) return;
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
            card.PointerPressed += (_, args) => { SelectGraphBlock(block.Id); args.Handled = true; };

            if (block.Kind == "resultFiles") { card.Child = BuildResultFilesPanel(block, files); return card; }

            var body = new Grid { RowSpacing = 6 };
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.Children.Add(BuildGraphCardHeader(block, files));

            var content = new StackPanel { Spacing = 6 };
            if (block.Request.Length > 0)
                content.Children.Add(new TextBlock { Text = block.Request, FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = .85 });
            if (block.Entries.Count > 0)
            {
                var transcript = new AgentTranscript();
                transcript.View.MinHeight = 0;
                transcript.Update(pane with { Logs = [.. block.Entries], Kind = "claude" }, owner.service.Snapshot.Theme == "light");
                content.Children.Add(transcript.View);
            }
            var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollMode = ScrollMode.Enabled };
            graphBodies[block.Id] = scroll;
            Grid.SetRow(scroll, 1); body.Children.Add(scroll);
            card.Child = body;
            return card;
        }

        private Grid BuildGraphCardHeader(MightyGraphBlock block, IReadOnlyList<ResultFiles.ResultFile> files)
        {
            var header = new Grid { ColumnSpacing = 6 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock { Text = block.Title, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });

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
            if (block.ResultFilesRunId is { } runId && files.Count > 0)
            {
                var open = graphResultFilesRunId == runId;
                var text = open ? Locale.Get(MightyGraphViewModel.LocaleKeyResultFilesClose) : Locale.Get("graph.resultFiles.openButton");
                var toggle = new Button { Content = new TextBlock { Text = "▤ " + files.Count, FontSize = 10 }, MinWidth = 0, MinHeight = 0, Height = 22, Padding = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
                AutomationProperties.SetAutomationId(toggle, "mighty-result-files-toggle-" + block.Id);
                AutomationProperties.SetName(toggle, Locale.Get("graph.resultFiles.countLabel", new Dictionary<string, string> { ["count"] = files.Count.ToString() }));
                ToolTipService.SetToolTip(toggle, text);
                toggle.Click += (_, _) => ToggleResultFiles(runId);
                right.Children.Add(toggle);
            }
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
                row.Click += async (_, _) => await OpenResultFile(file.Path);
                list.Children.Add(row);
            }
            panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 280 });
            return panel;
        }

        /// Opens a result file with the default app. Core resolves the path and
        /// refuses anything outside the workspace, so nothing else can be opened.
        private Task OpenResultFile(string path) =>
            owner.Act(() => { owner.service.OpenResultFile(path, Workspace.Path); return Task.CompletedTask; });

        private void ToggleResultFiles(string runId)
        {
            if (graphResultFilesRunId == runId) { CloseResultFiles(); return; }
            graphResultFilesRunId = runId; graphResultFilesClosedByHand = false; Refresh();
        }

        /// A manual close sticks until the next result.
        private void CloseResultFiles() { graphResultFilesRunId = null; graphResultFilesClosedByHand = true; RefreshMightyView(Session); }

        // ── zoom, pan and selection ───────────────────────────────────────────

        internal void SetGraphZoom(double value)
        {
            graphZoom = value; graphScale.ScaleX = graphScale.ScaleY = value;
            if (zoomResetButton is not null) ((TextBlock)zoomResetButton.Content).Text = MightyGraphViewModel.ZoomLabel(value);
            if (zoomOutButton is not null) zoomOutButton.IsEnabled = !MightyGraphViewModel.ZoomOutDisabled(value);
            if (zoomInButton is not null) zoomInButton.IsEnabled = !MightyGraphViewModel.ZoomInDisabled(value);
        }

        internal double GraphZoom => graphZoom;
        internal string? GraphSelection => graphSelection;
        internal int GraphBlockCount => graphCards.Count;

        /// The wheel scrolls the selected block's body; with nothing selected it
        /// pans the canvas. Either way it never reaches the outer page.
        private void OnGraphWheel(object sender, PointerRoutedEventArgs args)
        {
            var delta = args.GetCurrentPoint(graphViewport).Properties.MouseWheelDelta;
            if (MightyGraphViewModel.WheelScrollsBlock(graphSelection) && graphSelection is { } selected && graphBodies.TryGetValue(selected, out var body))
                body.ChangeView(null, Math.Max(0, body.VerticalOffset - delta), null, true);
            else graphPan.Y += delta;
            args.Handled = true;
        }

        private void OnGraphPointerPressed(object sender, PointerRoutedEventArgs args)
        {
            // Only the empty background reaches here: a card handles its own press.
            ClearGraphSelection();
            graphDragging = true; graphDragOrigin = args.GetCurrentPoint(graphViewport).Position;
            graphDragPanX = graphPan.X; graphDragPanY = graphPan.Y;
            graphViewport?.CapturePointer(args.Pointer);
            args.Handled = true;
        }

        private void OnGraphPointerMoved(object sender, PointerRoutedEventArgs args)
        {
            if (!graphDragging) return;
            var point = args.GetCurrentPoint(graphViewport).Position;
            graphPan.X = graphDragPanX + (point.X - graphDragOrigin.X);
            graphPan.Y = graphDragPanY + (point.Y - graphDragOrigin.Y);
            args.Handled = true;
        }

        private void OnGraphPointerReleased(object sender, PointerRoutedEventArgs args)
        {
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
        internal Canvas GraphCanvas => graphCanvas;
        internal string GraphTotalText => graphTotal.Text;
        internal (double X, double Y) GraphPan => (graphPan.X, graphPan.Y);
        internal string? GraphResultFilesRunId => graphResultFilesRunId;
        internal RunSession SessionForSmoke => Session;

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
