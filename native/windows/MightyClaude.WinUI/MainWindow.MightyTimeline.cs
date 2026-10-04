using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        private StackPanel? graphZoomControls;
        private Grid? graphToolbar;
        private Button? diagramButton, timelineButton;
        private readonly TextBlock graphStyleHeader = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        private ScrollViewer? timelineScroll;
        private readonly StackPanel timelineStack = new() { Spacing = 10, Padding = new Thickness(12, 12, 14, 12) };
        private readonly HashSet<string> timelineFlipped = [], timelineOpen = [], timelineFull = [];
        private readonly Dictionary<string, (string Fingerprint, StackPanel View)> timelineGroups = [];
        private readonly Dictionary<string, AgentTranscript> timelineTranscripts = [];
        private IReadOnlyList<MightyGraphCamera.ResultReveal.RunProgress>? timelineProgress;
        private string? timelineLastRun, timelineHistoryFingerprint;
        private FrameworkElement? timelineHistoryCard;
        private bool timelineDrawing, timelineDeferred, timelineWasRunning, timelineRestoring;
        private int timelineGeneration;
        private bool timelineHistoryArmed;

        private FrameworkElement BuildGraphPresentationSwitch()
        {
            var switcher = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Padding = new Thickness(2), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 135, 135, 135)), CornerRadius = new CornerRadius(8) };
            diagramButton = ZoomPill(Locale.Get("graph.view.diagram"), "mighty-view-diagram-" + id, () => _ = SetGraphPresentation("diagram"));
            timelineButton = ZoomPill(Locale.Get("graph.view.timeline"), "mighty-view-timeline-" + id, () => _ = SetGraphPresentation("timeline"));
            switcher.Children.Add(diagramButton); switcher.Children.Add(timelineButton);
            AutomationProperties.SetName(switcher, Locale.Get("graph.view.switch"));
            return switcher;
        }

        internal Task SetGraphPresentation(string mode) => owner.Act(async () =>
        {
            if (graphResizing) EndResultResize(MightyGraphLayout.ResizePhase.Cancelled, null);
            CancelResultReveal();
            await Change(p => MightyTimeline.ApplyMode(p, mode));
            if (!QueuePaneAlive) return;
            Refresh(); RefreshMightyView(Session);
        });

        private void RefreshGraphPresentationSwitch(bool timeline)
        {
            graphStyleHeader.Text = StylePresentation.Header(activeStyle, activeStyle is null ? null : GuidedPhase(activeStyle), Locale.Get("graph.view.mighty"));
            ToolTipService.SetToolTip(graphStyleHeader, graphStyleHeader.Text);
            if (graphZoomControls is not null) graphZoomControls.Visibility = timeline ? Visibility.Collapsed : Visibility.Visible;
            foreach (var (button, selected, text) in new[] { (diagramButton, !timeline, Locale.Get("graph.view.diagram")), (timelineButton, timeline, Locale.Get("graph.view.timeline")) })
            {
                if (button is null) continue;
                ((TextBlock)button.Content).Text = text;
                ((TextBlock)button.Content).FontWeight = selected ? FontWeights.Bold : FontWeights.Normal;
                button.Background = selected ? TimelineThemeBrush("panel") : new SolidColorBrush(Colors.Transparent);
                AutomationProperties.SetName(button, text);
            }
        }

        private void BuildMightyTimeline()
        {
            timelineScroll = new ScrollViewer { Content = timelineStack, Visibility = Visibility.Collapsed, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, IsTabStop = true };
            AutomationProperties.SetAutomationId(timelineScroll, "mighty-timeline-" + id);
            AutomationProperties.SetName(timelineScroll, Locale.Get("graph.view.timeline"));
            timelineScroll.ViewChanged += (_, args) =>
            {
                var offset = timelineScroll.VerticalOffset;
                if (!timelineRestoring && offset > 2) timelineHistoryArmed = true;
                var reachedTop = !timelineRestoring && timelineHistoryArmed && offset <= 2 && !args.IsIntermediate;
                if (reachedTop) timelineHistoryArmed = false;
                if (reachedTop && timelineScroll.Visibility == Visibility.Visible && graphHistory.Phase == SessionHistoryState.Phases.Idle) LoadOlderGraphHistory();
            };
            Grid.SetRow(timelineScroll, 1); graphHost!.Children.Add(timelineScroll);
        }

        private string GraphRequestTitle(MightyGraphRun run, int ordinal)
        {
            var prefix = activeStyle is null || styleRegistry is null ? null : StylePresentation.RequestPrefix(styleRegistry.Styles, run.Input);
            var provider = ProviderMark.Label(Session.Provider);
            var tail = MightyGraphBlockModel.RequestTitle(ordinal, provider);
            return prefix is { Length: > 0 } ? prefix + " · " + tail : tail;
        }

        private (string Icon, string Tint) RequestStyleLook(string prompt)
        {
            var look = activeStyle is null || styleRegistry is null ? null : StylePresentation.Request(styleRegistry.Styles, prompt);
            var icon = StylePresentation.Icon(look?.Icon);
            return (icon.Length > 0 ? icon : "↗", look?.Tint ?? "accent");
        }

        private void DrawMightyTimeline(RunSession pane)
        {
            if (timelineScroll is null || timelineDrawing) return;
            if (timelineTranscripts.Values.Any(t => t.IsSelecting)) { timelineDeferred = true; return; }
            timelineDrawing = true;
            try
            {
                var retained = (IReadOnlyList<MightyGraphRun>)(pane.GraphRuns ?? []);
                var older = GraphHistoryRuns(pane, retained);
                IReadOnlyList<MightyGraphRun> runs = [.. older, .. retained];
                var groups = MightyTimeline.Groups(runs);
                var current = retained.Select(r => new MightyGraphCamera.ResultReveal.RunProgress(r.Id, MightyGraphLayout.Finished(r))).ToList();
                var follow = timelineProgress is null || runs.LastOrDefault()?.Id != timelineLastRun ||
                    MightyGraphCamera.ResultReveal.FinishedRunID(timelineProgress, current, timelineWasRunning) is not null;
                timelineProgress = current; timelineLastRun = runs.LastOrDefault()?.Id; timelineWasRunning = pane.Status == "running";
                var anchor = timelineStack.Children.OfType<StackPanel>().FirstOrDefault(v => v.Tag is string && TimelineTop(v) + v.ActualHeight >= timelineScroll.VerticalOffset);
                var anchorId = anchor?.Tag as string; var anchorOffset = anchor is null ? 0 : TimelineTop(anchor) - timelineScroll.VerticalOffset;
                var oldOffset = timelineScroll.VerticalOffset;
                var desired = new List<UIElement>();
                var historyKey = JsonSerializer.Serialize(new { pane.ResumeId, pane.Provider, phase = graphHistory.Phase, count = older.Count, retained = retained.FirstOrDefault()?.Id, text = graphHistory.BlockText(older.Count), language = Locale.LanguagePreference });
                if (ShowsHistoryBlock(pane, retained))
                {
                    if (timelineHistoryCard is null || timelineHistoryFingerprint != historyKey)
                    {
                        timelineHistoryFingerprint = historyKey;
                        timelineHistoryCard = BuildHistoryCard(pane, retained, older.Count, new GraphRect(0, 0, MightyGraphLayout.HistoryWidth, MightyGraphLayout.HistoryHeight));
                    }
                    desired.Add(timelineHistoryCard);
                }
                var catalog = owner.Runtime(pane.Provider)?.ModelCatalog?.Models;
                foreach (var group in groups)
                {
                    var run = runs[group.RunIndex];
                    var fingerprint = JsonSerializer.Serialize(new { run, group.Ordinal, older = group.RunIndex < older.Count, open = MightyTimeline.GroupOpen(group, runs.Count, timelineFlipped), rows = group.Rows.Where(r => timelineOpen.Contains(r.NodeId)).Select(r => r.NodeId), full = group.Result is { } result && timelineFull.Contains(result.NodeId), title = GraphRequestTitle(run, group.Ordinal), language = Locale.LanguagePreference, dark = owner.DarkTheme, catalog });
                    if (!timelineGroups.TryGetValue(group.RunId, out var known) || known.Fingerprint != fingerprint)
                    {
                        var view = BuildTimelineGroup(group, run, pane, group.RunIndex < older.Count, MightyTimeline.GroupOpen(group, runs.Count, timelineFlipped));
                        view.Tag = group.RunId; timelineGroups[group.RunId] = known = (fingerprint, view);
                    }
                    desired.Add(known.View);
                }
                if (groups.Count == 0) desired.Add(new TextBlock { Text = Locale.Get("graph.timeline.empty"), FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
                // Reconcile individual cards; unchanged transcripts stay attached and keep
                // native selection, tool expansion and keyboard focus during streaming.
                var changed = timelineStack.Children.Count != desired.Count || !timelineStack.Children.SequenceEqual(desired);
                if (changed)
                {
                    for (var i = timelineStack.Children.Count - 1; i >= 0; i--) if (!desired.Contains(timelineStack.Children[i])) timelineStack.Children.RemoveAt(i);
                    for (var i = 0; i < desired.Count; i++)
                    {
                        if (i < timelineStack.Children.Count && ReferenceEquals(timelineStack.Children[i], desired[i])) continue;
                        timelineStack.Children.Remove(desired[i]); timelineStack.Children.Insert(i, desired[i]);
                    }
                }
                foreach (var key in timelineGroups.Keys.Where(key => !groups.Any(g => g.RunId == key)).ToArray()) timelineGroups.Remove(key);
                var nodes = groups.SelectMany(g => g.Rows.Select(r => r.NodeId).Append(g.Result?.NodeId ?? "")).ToHashSet();
                foreach (var key in timelineTranscripts.Keys.Where(key => !nodes.Contains(key)).ToArray()) timelineTranscripts.Remove(key);
                timelineOpen.IntersectWith(nodes); timelineFull.IntersectWith(nodes); timelineFlipped.IntersectWith(groups.Select(g => g.RunId));
                graphTotal.Text = MightyGraphBlockModel.ToolbarSummary(retained) + (older.Count > 0 ? " · " + Locale.Get("graph.history.headerLoaded", new Dictionary<string, string> { ["count"] = older.Count.ToString() }) : "");
                ToolTipService.SetToolTip(graphTotal, MightyGraphBlockModel.ToolbarHelp(retained));
                if (changed || follow)
                {
                    var generation = ++timelineGeneration; timelineRestoring = true;
                    Container.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (generation != timelineGeneration || timelineScroll is null || !QueuePaneAlive) return;
                        timelineScroll.UpdateLayout();
                        var target = follow ? timelineScroll.ScrollableHeight : anchorId is not null && timelineGroups.TryGetValue(anchorId, out var saved) ? TimelineTop(saved.View) - anchorOffset : oldOffset;
                        timelineScroll.ChangeView(null, Math.Max(0, target), null, true);
                        timelineHistoryArmed = timelineScroll.VerticalOffset > 2;
                        Container.DispatcherQueue.TryEnqueue(() => { if (generation == timelineGeneration) timelineRestoring = false; });
                    });
                }
                AutoLoadGraphHistory(pane, retained);
            }
            finally { timelineDrawing = false; }
        }

        private double TimelineTop(FrameworkElement view) => view.TransformToVisual(timelineStack).TransformPoint(new Point()).Y;
        private static void ToggleTimelineSet(HashSet<string> values, string key) { if (!values.Add(key)) values.Remove(key); }
        private void ToggleTimelineRow(string nodeId)
        {
            ToggleTimelineSet(timelineOpen, nodeId);
            if (timelineOpen.Contains(nodeId)) { graphSelection = nodeId; graphAimedRunId = null; }
            RefreshMightyView(Session);
        }

        private StackPanel BuildTimelineGroup(MightyTimeline.Group group, MightyGraphRun run, RunSession pane, bool older, bool open)
        {
            var stack = new StackPanel { Spacing = 8 };
            var header = new StackPanel { Spacing = 3 };
            var head = TimelineHeading();
            var title = ProviderMarkView.Labelled((open ? "⌄ " : "› ") + GraphRequestTitle(run, group.Ordinal), ProviderMark.MarkedProvider(pane.Provider), 16, FontWeights.SemiBold);
            head.Children.Add(title);
            var status = TimelinePill(group.Status); Grid.SetColumn(status, 1); head.Children.Add(status); header.Children.Add(head);
            if (older) header.Children.Add(new TextBlock { Text = "◷ " + Locale.Get("graph.history.tag"), FontSize = 10, Opacity = .65, Margin = new Thickness(19, 0, 0, 0) });
            var prompt = string.Join(" ", group.Input.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (prompt.Length > 0) header.Children.Add(new TextBlock { Text = prompt, FontSize = 12, Opacity = .7, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(19, 0, 0, 0) });
            header.Children.Add(new TextBlock { Text = Locale.Get("phone.blocks.tally", new Dictionary<string, string> { ["total"] = group.Tally.Total.ToString(), ["settled"] = group.Tally.Settled.ToString() }), FontSize = 11, Opacity = .7, Margin = new Thickness(19, 0, 0, 0) });
            var button = TimelineButton(header, "mighty-timeline-request-" + group.RunId, () => { ToggleTimelineSet(timelineFlipped, group.RunId); RefreshMightyView(Session); });
            AutomationProperties.SetName(button, GraphRequestTitle(run, group.Ordinal)); ToolTipService.SetToolTip(button, Locale.Get(open ? "phone.blocks.collapse" : "phone.blocks.expand")); stack.Children.Add(button);
            if (open) for (var i = 0; i < group.Rows.Count; i++) stack.Children.Add(BuildTimelineRow(group.Rows[i], i, group, run, pane));
            if (group.Result is { } result) stack.Children.Add(BuildTimelineResult(result, group, run, pane));
            return stack;
        }

        private FrameworkElement BuildTimelineRow(MightyTimeline.Row row, int index, MightyTimeline.Group group, MightyGraphRun run, RunSession pane)
        {
            var grid = new Grid { ColumnSpacing = 8 }; grid.ColumnDefinitions.Add(new() { Width = new GridLength(32) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var look = row.AgentIndex is null ? RequestStyleLook(run.Input) : (Icon: row.Kind switch { "task" => "✓", "steer" => "↪", "compact" => "⇥", "question" => "?", _ => "◇" }, Tint: "accent");
            grid.Children.Add(TimelineMarker(row, index, group.Rows, look.Icon));
            var body = new StackPanel(); var text = new StackPanel { Spacing = 3 };
            text.Children.Add(new TextBlock { Text = row.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis });
            var agent = row.AgentIndex is { } n ? run.Agents[n] : null;
            var capsule = ModelUsageFormat.BlockCapsule(agent?.Usage ?? (agent is null ? run.Usage : null), agent?.ResponseRecords ?? (agent is null ? run.ResponseRecords : null) ?? [], agent is null ? run.NodeModelLabel : null, owner.Runtime(pane.Provider)?.ModelCatalog?.Models, versioned: true);
            var meta = new Grid { ColumnSpacing = 4 }; meta.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); meta.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            meta.Children.Add(new TextBlock { Text = look.Icon, FontSize = 10, Foreground = new SolidColorBrush(StyleColor(look.Tint)) });
            var description = new TextBlock { Text = string.Join(" · ", new[] { MightyTimeline.KindLabel(row.Kind), capsule, MightyTimeline.DurationLabel(row.DurationMs) }.Where(s => s is { Length: > 0 })), FontSize = 11, Opacity = .7, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(description, 1); meta.Children.Add(description); text.Children.Add(meta);
            if (row.Latest is { } latest) text.Children.Add(new TextBlock { Text = latest, FontSize = 11, FontFamily = new FontFamily(DesignMetrics.Font.Mono), Opacity = .65, TextTrimming = TextTrimming.CharacterEllipsis });
            var header = TimelineHeading(); header.Children.Add(text); var pill = TimelinePill(row.Status); Grid.SetColumn(pill, 1); header.Children.Add(pill);
            var button = TimelineButton(header, "mighty-timeline-row-" + row.NodeId, () => ToggleTimelineRow(row.NodeId));
            button.Padding = new Thickness(11, 7, 11, 7); AutomationProperties.SetName(button, row.Title + ", " + MightyGraphBlockModel.StateLabel(row.Status));
            ToolTipService.SetToolTip(button, Locale.Get(timelineOpen.Contains(row.NodeId) ? "phone.blocks.collapse" : "phone.blocks.expand")); body.Children.Add(button);
            if (timelineOpen.Contains(row.NodeId))
            {
                var detail = new StackPanel(); AutomationProperties.SetAutomationId(detail, "mighty-timeline-detail-" + row.NodeId);
                var input = agent?.Input ?? run.Input;
                if (input.Length > 0) detail.Children.Add(new TextBlock { Text = input, FontSize = 11, MaxLines = 6, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Margin = new Thickness(11, 7, 11, 7) });
                var entries = (agent?.Entries ?? run.RootEntries).Where(e => e.Kind != "user").ToList();
                if (entries.Count == 0) detail.Children.Add(new TextBlock { Text = Locale.Get("phone.blocks.nothing"), FontSize = 12, Opacity = .7, Margin = new Thickness(11) });
                else detail.Children.Add(TimelineTranscript(row.NodeId, entries, pane, 260));
                body.Children.Add(new Border { Height = 1, Background = TimelineThemeBrush("border") }); body.Children.Add(detail);
            }
            var card = new Border { Child = body, CornerRadius = new CornerRadius(14), Background = TimelineThemeBrush("panel"), BorderThickness = new Thickness(row.Status is "running" or "waiting" ? 2 : 1), BorderBrush = row.Status is "running" or "waiting" ? OutlineBrush(StatusGlyph.GlyphHex(row.Node.Tone, owner.DarkTheme)) : TimelineThemeBrush("border") };
            Grid.SetColumn(card, 1); grid.Children.Add(card); return grid;
        }

        private FrameworkElement TimelineTranscript(string node, List<LogEntry> entries, RunSession pane, double? height = null, double? maxHeight = null)
        {
            if (!timelineTranscripts.TryGetValue(node, out var transcript))
            {
                transcript = new AgentTranscript { OpenReference = OpenReferencePreview, OpenImage = OpenTranscriptImage };
                transcript.SelectionEnded = () => { if (timelineDeferred) { timelineDeferred = false; Container.DispatcherQueue.TryEnqueue(() => { if (QueuePaneAlive) RefreshMightyView(Session); }); } };
                timelineTranscripts[node] = transcript;
            }
            if (transcript.View.Parent is Panel old) old.Children.Remove(transcript.View);
            transcript.View.MinHeight = 0; transcript.View.Height = height ?? double.NaN; transcript.View.MaxHeight = maxHeight ?? double.PositiveInfinity;
            transcript.Update(pane with { Logs = entries, Kind = "claude" }, !owner.DarkTheme, owner.pictures, Workspace.Path);
            return transcript.View;
        }

        private FrameworkElement BuildTimelineResult(MightyTimeline.Result result, MightyTimeline.Group group, MightyGraphRun run, RunSession pane)
        {
            var body = new StackPanel();
            var header = TimelineHeading(); header.Padding = new Thickness(13, 6, 13, 6); header.Background = OutlineBrush(StatusGlyph.GlyphHex(result.Tone, owner.DarkTheme));
            header.Children.Add(new TextBlock { Text = MightyGraphBlockModel.ResultTitle(run.Status), FontSize = 12, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(owner.DarkTheme ? Colors.Black : Colors.White), VerticalAlignment = VerticalAlignment.Center });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var files = run.Status == "completed" ? MightyGraphBlockModel.FilesFor(run, Workspace.Path) : [];
            if (files.Count > 0)
            {
                var menu = new MenuFlyout();
                foreach (var file in files)
                {
                    var item = new MenuFlyoutItem { Text = file.Path + (file.Line is { } line ? ":" + line : "") };
                    item.Click += async (_, _) => await OpenReferencePreview(file.Path, file.Line); menu.Items.Add(item);
                }
                var fileButton = new Button { Content = "▤ " + files.Count, Flyout = menu, MinHeight = 22, Padding = new Thickness(5, 0, 5, 0), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
                AutomationProperties.SetAutomationId(fileButton, "mighty-timeline-result-files-" + result.NodeId);
                AutomationProperties.SetName(fileButton, Locale.Get("graph.resultFiles.countLabel", new Dictionary<string, string> { ["count"] = files.Count.ToString() }));
                actions.Children.Add(fileButton);
            }
            var caption = Locale.Get("graph.timeline.requestOrdinal", new Dictionary<string, string> { ["n"] = group.Ordinal.ToString() }) + (run.TotalUsage is { } usage ? " · " + GraphTokenUsage.Compact(usage.Total) : "");
            actions.Children.Add(new TextBlock { Text = caption, FontSize = 10, FontFamily = new FontFamily(DesignMetrics.Font.Mono), VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(owner.DarkTheme ? Colors.Black : Colors.White) });
            Grid.SetColumn(actions, 1); header.Children.Add(actions); body.Children.Add(header);
            if (result.Text is { } answer)
            {
                var content = new StackPanel();
                content.Children.Add(TimelineTranscript(result.NodeId, [run.ResultEntries.Last(e => e.Kind == "assistant" && e.Text.Length > 0)], pane, maxHeight: timelineFull.Contains(result.NodeId) ? null : 180));
                if (MightyTimeline.Folds(answer))
                {
                    var toggle = ZoomPill(Locale.Get(timelineFull.Contains(result.NodeId) ? "phone.blocks.resultLess" : "phone.blocks.resultMore"), "mighty-timeline-result-more-" + result.NodeId, () => { ToggleTimelineSet(timelineFull, result.NodeId); RefreshMightyView(Session); });
                    toggle.HorizontalAlignment = HorizontalAlignment.Left; toggle.Margin = new Thickness(8, 0, 0, 8); content.Children.Add(toggle);
                }
                body.Children.Add(content);
            }
            var card = new Border { Child = body, CornerRadius = new CornerRadius(16), Background = TimelineThemeBrush("panel"), Margin = new Thickness(40, 0, 0, 0), BorderBrush = TimelineThemeBrush("border"), BorderThickness = new Thickness(1) };
            AutomationProperties.SetAutomationId(card, "mighty-timeline-result-" + result.NodeId); return card;
        }

        private SolidColorBrush TimelineThemeBrush(string kind) => OutlineBrush(kind == "border" ? owner.DarkTheme ? "#45464A" : "#DAD8D2" : owner.DarkTheme ? "#25262B" : "#FFFFFF");

        private static Grid TimelineHeading()
        {
            var grid = new Grid { ColumnSpacing = 8 }; grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); return grid;
        }
        private static Button TimelineButton(UIElement content, string automationId, Action act)
        {
            var button = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(2), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
            AutomationProperties.SetAutomationId(button, automationId); button.Click += (_, _) => act(); return button;
        }
        private Border TimelinePill(string status)
        {
            var brush = OutlineBrush(StatusGlyph.GlyphHex(StatusGlyph.Tone(status), owner.DarkTheme));
            return new Border { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), BorderBrush = brush, Padding = new Thickness(6, 1, 6, 1), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = MightyGraphBlockModel.StateLabel(status), Foreground = brush, FontSize = 10 } };
        }
        private FrameworkElement TimelineMarker(MightyTimeline.Row row, int index, IReadOnlyList<MightyTimeline.Row> rows, string icon)
        {
            var marker = new Grid { Width = 32, IsHitTestVisible = false };
            SolidColorBrush Rail(DesignTone? tone) => tone is { } t ? OutlineBrush(StatusGlyph.GlyphHex(t, owner.DarkTheme)) : TimelineThemeBrush("border");
            if (index > 0) marker.Children.Add(new Border { Width = 3, Height = 21, VerticalAlignment = VerticalAlignment.Top, Background = Rail(MightyTimeline.RailAbove(rows, index)) });
            if (index < rows.Count - 1) marker.Children.Add(new Border { Width = 3, Margin = new Thickness(0, 21, 0, -8), Background = Rail(row.Node.Rail) });
            var tone = OutlineBrush(StatusGlyph.GlyphHex(row.Node.Tone, owner.DarkTheme));
            if (row.Node.Ring)
            {
                var ring = new Ellipse { Width = 32, Height = 32, Fill = tone, Opacity = .2, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0) };
                if (AnimationsEnabled)
                {
                    var fade = new DoubleAnimation { From = .35, To = .08, Duration = new Duration(TimeSpan.FromSeconds(1.6)), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                    Storyboard.SetTarget(fade, ring); Storyboard.SetTargetProperty(fade, "Opacity"); var story = new Storyboard(); story.Children.Add(fade);
                    ring.Loaded += (_, _) => story.Begin(); ring.Unloaded += (_, _) => story.Stop();
                }
                marker.Children.Add(ring);
            }
            marker.Children.Add(new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = tone, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 9, 0, 0), BorderThickness = new Thickness(3), BorderBrush = TimelineThemeBrush("panel"), Child = new TextBlock { Text = icon, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(owner.DarkTheme ? Colors.Black : Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
            AutomationProperties.SetAccessibilityView(marker, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw); return marker;
        }
    }
}
