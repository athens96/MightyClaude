using MightyClaude.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>
    /// The background Ouroboros executions a pane's agents started, each a block beside the 실행 block that started it
    /// (macOS MightyGraphOuroborosView.swift). What a block shows, which start tools name an execution, the loopback-only
    /// dashboard reads and when each is read again are Core's (<see cref="OuroborosExecutionMonitor"/>); this file runs
    /// the reads while the diagram shows and draws the block.
    /// </summary>
    private sealed partial class PaneView
    {
        private OuroborosExecutionMonitor? graphOuroboros;
        private IReadOnlyList<OuroborosExecutionLink> graphExecutionLinks = [];
        private DispatcherTimer? graphOuroborosTimer;
        private bool graphOuroborosReading;
        /// <summary>Cancels the read on its way when the diagram hides or the pane closes, as the Mac's <c>.task(id:)</c> does (M/MightyGraphView.swift:320-321).</summary>
        private CancellationTokenSource? graphOuroborosCancel;

        private static string ExecutionNodeId(OuroborosExecutionLink link) => MightyGraphBlockSize.NodeId(link.RunID, MightyGraphLayout.ExecutionSuffix + link.Key);
        /// <summary>Blocks whose AC list is open: only these read the board stream (M/MightyGraphView.swift:191).</summary>
        private HashSet<string> OpenExecutionLists() => graphExecutionLinks.Where(link => graphExpanded.Contains(ExecutionNodeId(link))).Select(link => link.Key).ToHashSet(StringComparer.Ordinal);

        /// <summary>
        /// The pane's executions as this pass drew them (M/MightyGraphView.swift:321). Their dashboard is read only while
        /// the diagram shows, and never in a smoke run: a block then says its status is being read.
        /// </summary>
        private void WatchOuroboros()
        {
            if (owner.options.SmokeTest || graphExecutionLinks.Count == 0 && graphOuroboros is null) return;
            (graphOuroboros ??= new(new OuroborosDashboardClient())).Track(graphExecutionLinks, OpenExecutionLists(), DateTimeOffset.UtcNow);
            ScheduleOuroboros();
        }

        /// <summary>The next read, when the earliest block is due; none once every block stopped.</summary>
        private void ScheduleOuroboros()
        {
            if (graphOuroboros is not { } monitor || graphOuroborosReading) return;
            if (monitor.NextAt(graphExecutionLinks) is not { } next) { graphOuroborosTimer?.Stop(); return; }
            if (graphOuroborosTimer is null)
            {
                graphOuroborosTimer = new DispatcherTimer();
                graphOuroborosTimer.Tick += (_, _) => { graphOuroborosTimer.Stop(); _ = ReadOuroboros(); };
            }
            graphOuroborosTimer.Stop();
            graphOuroborosTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.2, (next - DateTimeOffset.UtcNow).TotalSeconds));
            graphOuroborosTimer.Start();
        }

        /// <summary>
        /// One read of the blocks that are due. The dashboard reads, a daemon start and the parsing run off the UI thread; what
        /// they found is given to the blocks here. A read that failed is given to them as one that found nothing, so the blocks
        /// say so and polling goes on at its slower pace. A hidden or closed pane reads nothing; it is read again when its
        /// diagram is next drawn.
        /// </summary>
        private bool OuroborosShown => QueuePaneAlive && graphHost?.Visibility == Visibility.Visible && graphViewport is { Visibility: Visibility.Visible, IsLoaded: true };

        private async Task ReadOuroboros()
        {
            if (graphOuroboros is not { } monitor || graphOuroborosReading || !OuroborosShown) return;
            var due = monitor.Due(graphExecutionLinks, DateTimeOffset.UtcNow);
            if (due.Count > 0)
            {
                graphOuroborosReading = true;
                var cancel = graphOuroborosCancel ??= new CancellationTokenSource();
                var read = monitor.Begin(due, OpenExecutionLists());
                try { await Task.Run(() => read.RunAsync(() => DateTimeOffset.UtcNow, cancel.Token)); }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
                finally { graphOuroborosReading = false; }
                if (!cancel.IsCancellationRequested && QueuePaneAlive && monitor.Apply(read, DateTimeOffset.UtcNow)) QueueGraphRefresh();
            }
            if (OuroborosShown) ScheduleOuroboros();
        }

        /// <summary>The diagram hid or the pane closed: no more reads, and the one on its way stops.</summary>
        private void StopOuroboros()
        {
            graphOuroborosTimer?.Stop();
            graphOuroborosCancel?.Cancel(); graphOuroborosCancel = null;
        }

        /// <summary>An execution's block takes its status, its activity mark and its outline from what its dashboard last said (M/MightyGraphOuroborosView.swift:142-152, 201).</summary>
        private MightyGraphBlock ExecutionBlock(MightyGraphBlock block)
        {
            var status = OuroborosDashboard.BlockStatus(block.RecordId is { } key ? graphOuroboros?.Snapshot(key) : null);
            return block with
            {
                Status = status is "starting" or "unknown" ? "" : status, Indicator = MightyGraphViewModel.BlockIndicator(status, AnimationsEnabled),
                Outline = MightyGraphActivity.Outline(status is "running" or "waiting" ? status : "completed", AnimationsEnabled),
            };
        }

        /// <summary>What an execution's block was drawn from: its snapshot and its "open dashboard" click.</summary>
        private object? ExecutionState(MightyGraphBlock block) => block.Kind == MightyGraphLayout.ExecutionKind && block.RecordId is { } key
            ? new { Snapshot = graphOuroboros?.Snapshot(key), Opening = graphOuroboros?.Opening(key) == true, Failed = graphOuroboros?.OpenFailed(key) == true } : null;

        /// <summary>
        /// The block for one background execution (M/MightyGraphOuroborosView.swift:128-325). A <c>Layout.BlockHead</c> head,
        /// padding <c>Spacing.Md</c>, its parts <c>Spacing.Sm</c> apart, over a <c>line</c> rule: the loop in <c>compactText</c>,
        /// the title 12 semibold, the activity mark, the status pill and the AC list's toggle (<c>accent</c> while open). Under
        /// it, <c>Spacing.Md</c> from the edge and <c>Spacing.Sm</c> apart: the goal 12, the AC progress bar over "AC n/m" 11
        /// medium, the four counts as 10pt <c>cardRaised</c> capsules, the phase and activity 11 <c>ink2</c>, a note while
        /// nothing can be read, the AC list when open, and at the bottom the execution's id 9 mono <c>ink2</c> beside
        /// "open dashboard" in <c>accent</c>.
        /// </summary>
        private Grid BuildOuroborosCard(MightyGraphBlock block, GraphCardView view)
        {
            var b = owner.brushes; var ink2 = b.Brush(DesignToken.Ink2); var tint = b.Brush(DesignToken.CompactText);
            var link = graphExecutionLinks.FirstOrDefault(candidate => candidate.Key == block.RecordId);
            var snapshot = block.RecordId is { } key ? graphOuroboros?.Snapshot(key) : null;
            var status = snapshot?.Status ?? OuroborosExecutionStatus.Unknown;
            var expanded = graphExpanded.Contains(block.Id);
            graphBodies.Remove(block.Id);
            var panel = new Grid();
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            AutomationProperties.SetAutomationId(panel, "mighty-ouroboros-" + block.Id);

            var head = new Grid
            {
                ColumnSpacing = DesignMetrics.Spacing.Sm, Padding = new Thickness(DesignMetrics.Spacing.Md - BlockEdge, 0, DesignMetrics.Spacing.Md - BlockEdge, 0), Height = DesignMetrics.Layout.BlockHead - BlockEdge + DesignMetrics.Stroke.Line,
                BorderThickness = new Thickness(0, 0, 0, DesignMetrics.Stroke.Line), BorderBrush = b.Brush(DesignToken.Line),
            };
            foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) head.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            head.Children.Add(MightySymbols.Create("infinity", 13, tint));
            var title = new TextBlock { Text = block.Title, TextSize = DesignMetrics.Type.Block, FontWeight = FontWeights.SemiBold, Foreground = b.Brush(DesignToken.Ink), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(title, 1); head.Children.Add(title); graphTitles[block.Id] = title;
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm, VerticalAlignment = VerticalAlignment.Center };
            view.Activity = null; graphActivitySets.Remove(block.Id);
            if (BuildIndicator(block.Indicator, view, block.Id) is { } indicator) right.Children.Add(indicator);
            var pill = StatusPill(Locale.Get("graph.ouroboros.status." + status.ToString().ToLowerInvariant()), MightyGraphBlockModel.Tone(block), 18);
            AutomationProperties.SetAutomationId(pill, "mighty-ouroboros-status-" + block.Id);
            right.Children.Add(view.StatePill = pill);
            var listName = Locale.Get(expanded ? "graph.ouroboros.hideACs" : "graph.ouroboros.showACs");
            var listInk = b.Brush(expanded ? DesignToken.Accent : DesignToken.Ink2);
            var list = HeaderButton(MightySymbols.Create(expanded ? "list.bullet.indent" : "list.bullet", 13, listInk), listInk);
            AutomationProperties.SetAutomationId(list, "mighty-ouroboros-list-toggle-" + block.Id);
            AutomationProperties.SetName(list, listName); ToolTipService.SetToolTip(list, listName);
            list.Click += (_, _) => ToggleAttachmentFold(block.Id);
            right.Children.Add(list);
            Grid.SetColumn(right, 2); head.Children.Add(right);
            panel.Children.Add(head);

            var body = new Grid { RowSpacing = DesignMetrics.Spacing.Sm, Margin = new Thickness(DesignMetrics.Spacing.Md - BlockEdge, DesignMetrics.Spacing.Md, DesignMetrics.Spacing.Md - BlockEdge, DesignMetrics.Spacing.Md - BlockEdge) };
            void Row(FrameworkElement element, bool fills = false)
            {
                body.RowDefinitions.Add(new RowDefinition { Height = fills ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
                Grid.SetRow(element, body.RowDefinitions.Count - 1); body.Children.Add(element);
            }
            TextBlock Words(string text, double size, Brush ink, int lines) => new() { Text = text, TextSize = size, Foreground = ink, TextWrapping = lines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap, MaxLines = lines, TextTrimming = TextTrimming.CharacterEllipsis };
            if (snapshot?.Goal is { } goal)
            {
                var words = Words(goal, DesignMetrics.Type.Block, b.Brush(DesignToken.Ink), expanded ? 2 : 1);
                AutomationProperties.SetAutomationId(words, "mighty-ouroboros-goal-" + block.Id); ToolTipService.SetToolTip(words, goal);
                Row(words);
            }
            if (snapshot?.Total is > 0 and var total)
            {
                var done = Math.Min(total, snapshot.Completed ?? 0);
                var progress = new StackPanel { Spacing = DesignMetrics.Spacing.Xs };
                progress.Children.Add(new ProgressBar { Minimum = 0, Maximum = total, Value = done, Foreground = status == OuroborosExecutionStatus.Failed ? b.Brush(DesignToken.Err) : tint, Background = b.Brush(DesignToken.Track) });
                var count = Words(Locale.Get("graph.ouroboros.progress", new Dictionary<string, string> { ["completed"] = done.ToString(System.Globalization.CultureInfo.InvariantCulture), ["total"] = total.ToString(System.Globalization.CultureInfo.InvariantCulture) }), DesignMetrics.Type.Pill, b.Brush(DesignToken.Ink), 1);
                count.FontWeight = FontWeights.Medium; Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(count, FontNumeralAlignment.Tabular);
                AutomationProperties.SetAutomationId(count, "mighty-ouroboros-progress-" + block.Id);
                progress.Children.Add(count);
                Row(progress);
            }
            (string Key, int? Value, Brush Ink)[] counts =
            [
                ("pending", snapshot?.Pending, ink2), ("executing", snapshot?.Executing, tint),
                ("completed", snapshot?.Completed, b.Brush(DesignToken.DoneText)), ("failed", snapshot?.Failed, b.Brush(DesignToken.ErrText)),
            ];
            if (counts.Any(value => value.Value is not null))
            {
                var capsules = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
                AutomationProperties.SetAutomationId(capsules, "mighty-ouroboros-counts-" + block.Id);
                foreach (var (name, value, ink) in counts)
                {
                    var words = Words(Locale.Get("graph.ouroboros.count." + name, new Dictionary<string, string> { ["n"] = (value ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture) }), DesignMetrics.Type.Small, value > 0 ? ink : ink2, 1);
                    Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(words, FontNumeralAlignment.Tabular);
                    capsules.Children.Add(new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xxs, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xxs), Background = b.Brush(DesignToken.CardRaised), Child = words });
                }
                Row(capsules);
            }
            if (string.Join(" · ", new[] { snapshot?.Phase, snapshot?.Activity }.OfType<string>()) is { Length: > 0 } line)
            {
                var words = Words(line, DesignMetrics.Type.Pill, ink2, expanded ? 2 : 1); ToolTipService.SetToolTip(words, line);
                Row(words);
            }
            var note = snapshot?.Source switch
            {
                null => "graph.ouroboros.waiting", OuroborosExecutionSnapshot.Sources.Unreachable => "graph.ouroboros.unreachable",
                OuroborosExecutionSnapshot.Sources.Unreadable => "graph.ouroboros.unreadable", OuroborosExecutionSnapshot.Sources.NoExecution => "graph.ouroboros.noExecution", _ => null,
            };
            if (note is not null) Row(Words(Locale.Get(note), DesignMetrics.Type.Pill, ink2, 3));
            Row(expanded ? BuildOuroborosACs(block, snapshot, tint) : new Border(), fills: true);

            var foot = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var failed = block.RecordId is { } failedKey && graphOuroboros?.OpenFailed(failedKey) == true;
            var id = failed ? Words(Locale.Get("graph.ouroboros.openFailed"), DesignMetrics.Type.Small, b.Brush(DesignToken.ErrText), 1)
                : Words(link?.ExecutionID is { } execution ? Locale.Get("graph.ouroboros.executionID", new Dictionary<string, string> { ["id"] = execution })
                    : Locale.Get("graph.ouroboros.jobID", new Dictionary<string, string> { ["id"] = link?.JobID ?? "" }), DesignMetrics.Type.Badge, ink2, 1);
            if (failed) AutomationProperties.SetAutomationId(id, "mighty-ouroboros-open-failed-" + block.Id);
            else { id.TextFont = TextArea.Code; id.IsTextSelectionEnabled = true; }
            id.VerticalAlignment = VerticalAlignment.Center;
            foot.Children.Add(id);
            var accent = b.Brush(DesignToken.Accent);
            var face = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xs, VerticalAlignment = VerticalAlignment.Center };
            face.Children.Add(MightySymbols.Create("globe", 11, accent));
            face.Children.Add(new TextBlock { Text = Locale.Get("graph.ouroboros.openDashboard"), TextSize = DesignMetrics.Type.Pill, Foreground = accent, VerticalAlignment = VerticalAlignment.Center });
            var open = HeaderButton(face, accent);
            open.IsEnabled = link is not null && graphOuroboros?.Opening(link.Key) != true;
            AutomationProperties.SetAutomationId(open, "mighty-ouroboros-dashboard-" + block.Id);
            AutomationProperties.SetName(open, Locale.Get("graph.ouroboros.openDashboard"));
            open.Click += (_, _) => { if (link is not null) _ = OpenOuroborosDashboard(link); };
            Grid.SetColumn(open, 1); foot.Children.Add(open);
            Row(foot);
            Grid.SetRow(body, 1); panel.Children.Add(body);
            return panel;
        }

        /// <summary>
        /// The AC list of an opened block (M/MightyGraphOuroborosView.swift:246-275): each AC a <c>cardRaised</c> row, radius 5,
        /// its state's mark 11 before its title 11 in at most two lines, stepped in <c>Spacing.Lg</c> for each level; the
        /// first 300. While it is being read, or when there is none, a line in <c>ink2</c> says so.
        /// </summary>
        private FrameworkElement BuildOuroborosACs(MightyGraphBlock block, OuroborosExecutionSnapshot? snapshot, Brush tint)
        {
            var b = owner.brushes; var ink2 = b.Brush(DesignToken.Ink2);
            if (snapshot?.Items is not { Count: > 0 } items)
                return new TextBlock { Text = Locale.Get(snapshot?.Items is null ? "graph.ouroboros.loadingACs" : "graph.ouroboros.noACs"), TextSize = DesignMetrics.Type.Pill, Foreground = ink2, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top };
            var rows = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs, Padding = new Thickness(0, 0, 0, DesignMetrics.Spacing.Sm) };
            foreach (var item in items.Take(300))
            {
                var (symbol, ink) = item.Status switch
                {
                    OuroborosACStatus.Pending => ("circle", ink2), OuroborosACStatus.Executing => ("circle.dotted.circle", tint),
                    OuroborosACStatus.Completed => ("checkmark.circle.fill", b.Brush(DesignToken.DoneText)), _ => ("xmark.circle.fill", b.Brush(DesignToken.ErrText)),
                };
                var face = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
                face.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); face.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var mark = MightySymbols.Create(symbol, 11, ink); mark.VerticalAlignment = VerticalAlignment.Top; mark.Margin = new Thickness(0, DesignMetrics.Stroke.Line, 0, 0);
                face.Children.Add(mark);
                var words = new TextBlock { Text = item.Title, TextSize = DesignMetrics.Type.Pill, Foreground = b.Brush(DesignToken.Ink), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
                ToolTipService.SetToolTip(words, item.Title);
                Grid.SetColumn(words, 1); face.Children.Add(words);
                var row = new Border
                {
                    CornerRadius = new CornerRadius(DesignMetrics.Radius.FileRow), Background = b.Brush(DesignToken.CardRaised), Child = face,
                    Padding = new Thickness(DesignMetrics.Spacing.Sm + item.Depth * DesignMetrics.Spacing.Lg, DesignMetrics.Spacing.Xxs, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xxs),
                };
                AutomationProperties.SetName(row, item.Title + ", " + Locale.Get("graph.ouroboros.ac." + item.Status.ToString().ToLowerInvariant()));
                rows.Children.Add(row);
            }
            var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
            AutomationProperties.SetAutomationId(scroll, "mighty-ouroboros-acs-" + block.Id);
            // An attachment beside the flow: the wheel scrolls its list at once.
            graphBodies[block.Id] = scroll;
            return scroll;
        }

        /// <summary>
        /// The "open dashboard" click (M/MightyGraphOuroborosView.swift:106-123): the run's page on the loopback dashboard,
        /// started first when nothing answers, shown in the system's browser. One click at a time per block.
        /// </summary>
        private async Task OpenOuroborosDashboard(OuroborosExecutionLink link)
        {
            if (owner.options.SmokeTest) return;
            var monitor = graphOuroboros ??= new(new OuroborosDashboardClient());
            var opening = monitor.OpenDashboardAsync(link, async url => OuroborosDashboard.Endpoint(url) is not null && await Windows.System.Launcher.LaunchUriAsync(url));
            if (QueuePaneAlive) QueueGraphRefresh();
            await opening;
            if (QueuePaneAlive) QueueGraphRefresh();
        }
    }
}
