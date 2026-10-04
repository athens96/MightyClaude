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
        private readonly TextBlock graphStyleHeader = new() { FontSize = DesignMetrics.Type.Block, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
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
        /// <summary>The Diagram | Timeline track (<c>track</c>, r8, padding 2).</summary>
        private StackPanel? graphViewSwitch;
        // The timeline's parts as last built, by request (heads) or block (rows, results), for the design smoke.
        private readonly Dictionary<string, (FrameworkElement Title, FontIcon Chevron, TextBlock? Prompt, TextBlock Tally, Border Pill)> timelineHeads = [];
        private readonly Dictionary<string, (Border Card, Border Node, Border Pill)> timelineRows = [];
        private readonly Dictionary<string, (Border Card, Grid Head, TextBlock Title, TextBlock Caption)> timelineResults = [];

        /// <summary>
        /// The Diagram | Timeline switch in the Mighty bar (M/MightyGraphTimelineView.swift:10-42): a
        /// <c>track</c> rail, radius 8, padding 2; each option 22 tall, padding h9, radius 6, 11pt.
        /// </summary>
        private FrameworkElement BuildGraphPresentationSwitch()
        {
            var switcher = graphViewSwitch = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Padding = new Thickness(2), Background = owner.brushes.Brush(DesignToken.Track), CornerRadius = new CornerRadius(DesignMetrics.Radius.Row), VerticalAlignment = VerticalAlignment.Center };
            diagramButton = ViewOption(Locale.Get("graph.view.diagram"), "mighty-view-diagram-" + id, () => _ = SetGraphPresentation("diagram"));
            timelineButton = ViewOption(Locale.Get("graph.view.timeline"), "mighty-view-timeline-" + id, () => _ = SetGraphPresentation("timeline"));
            switcher.Children.Add(diagramButton); switcher.Children.Add(timelineButton);
            AutomationProperties.SetName(switcher, Locale.Get("graph.view.switch"));
            return switcher;
        }

        /// <summary>
        /// One view option: the button itself is plain (no fill at rest, the subtle wash under the pointer,
        /// written once); its chip, a Border filling it with padding h9, carries the chosen side's <c>card</c>
        /// and the words their ink (<see cref="RefreshGraphPresentationSwitch"/>), so choosing never rewrites resources.
        /// </summary>
        private Button ViewOption(string text, string automationId, Action act)
        {
            var chip = new Border { Child = new TextBlock { Text = text, FontSize = DesignMetrics.Type.Pill, VerticalAlignment = VerticalAlignment.Center }, Padding = new Thickness(9, 0, 9, 0), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment) };
            var button = new Button { Content = chip, MinWidth = 0, MinHeight = 0, Height = 22, Padding = new Thickness(0), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            owner.PaintPlainButton(button, owner.brushes.Transparent, owner.brushes.Subtle);
            AutomationProperties.SetAutomationId(button, automationId); AutomationProperties.SetName(button, text);
            ToolTipService.SetToolTip(button, text);
            button.Click += (_, _) => act();
            return button;
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
                var chip = (Border)button.Content; var words = (TextBlock)chip.Child;
                words.Text = text;
                // The chosen side is a card chip in bold ink; the other stays clear, semibold ink2, the subtle wash under the pointer.
                words.FontWeight = selected ? FontWeights.Bold : FontWeights.SemiBold;
                var b = owner.brushes;
                chip.Background = selected ? b.Brush(DesignToken.Card) : b.Transparent; words.Foreground = b.Brush(selected ? DesignToken.Ink : DesignToken.Ink2);
                AutomationProperties.SetName(button, text);
            }
        }

        private void BuildMightyTimeline()
        {
            // The list on cardRaised (M/MightyGraphView.swift:686), so the white row cards stand on it.
            timelineScroll = new ScrollViewer { Content = timelineStack, Visibility = Visibility.Collapsed, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, IsTabStop = true, Background = owner.brushes.Brush(DesignToken.CardRaised) };
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
                    var fingerprint = JsonSerializer.Serialize(new { run, group.Ordinal, older = group.RunIndex < older.Count, open = MightyTimeline.GroupOpen(group, runs.Count, timelineFlipped), rows = group.Rows.Where(r => timelineOpen.Contains(r.NodeId)).Select(r => r.NodeId), full = group.Result is { } result && timelineFull.Contains(result.NodeId), title = GraphRequestTitle(run, group.Ordinal), language = Locale.LanguagePreference, catalog });
                    if (!timelineGroups.TryGetValue(group.RunId, out var known) || known.Fingerprint != fingerprint)
                    {
                        var view = BuildTimelineGroup(group, run, pane, group.RunIndex < older.Count, MightyTimeline.GroupOpen(group, runs.Count, timelineFlipped));
                        view.Tag = group.RunId; timelineGroups[group.RunId] = known = (fingerprint, view);
                    }
                    desired.Add(known.View);
                }
                if (groups.Count == 0) desired.Add(new TextBlock { Text = Locale.Get("graph.timeline.empty"), FontSize = DesignMetrics.Type.Block, Foreground = owner.brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
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
                // The parts the smoke reads follow the groups: only those still drawn are kept.
                var drawnRows = groups.Where(g => MightyTimeline.GroupOpen(g, runs.Count, timelineFlipped)).SelectMany(g => g.Rows.Select(r => r.NodeId)).ToHashSet();
                var drawnResults = groups.Select(g => g.Result?.NodeId).OfType<string>().ToHashSet();
                foreach (var key in timelineHeads.Keys.Where(key => !timelineGroups.ContainsKey(key)).ToArray()) timelineHeads.Remove(key);
                foreach (var key in timelineRows.Keys.Where(key => !drawnRows.Contains(key)).ToArray()) timelineRows.Remove(key);
                foreach (var key in timelineResults.Keys.Where(key => !drawnResults.Contains(key)).ToArray()) timelineResults.Remove(key);
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

        /// <summary>
        /// One request on the timeline (M/MightyGraphView.swift:701-760): its header button, the rows
        /// while it is open, and its result card. The header line is the chevron (10 bold <c>ink2</c>),
        /// "Request N · [mark] Codex" in the heading font (decision Q1: Segoe UI Variable Display Bold,
        /// 16) in <c>ink</c>, a clock for a request read back from the record, and the 20pt status
        /// pill; under it, indented 19, the request (12 <c>ink2</c>, two lines) and the block tally
        /// (11 semibold <c>ink2</c>).
        /// </summary>
        private StackPanel BuildTimelineGroup(MightyTimeline.Group group, MightyGraphRun run, RunSession pane, bool older, bool open)
        {
            var b = owner.brushes; var ink2 = b.Brush(DesignToken.Ink2);
            var stack = new StackPanel { Spacing = 8 };
            var header = new StackPanel { Spacing = 2 };
            var head = TimelineHeading();
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            // Segoe Fluent Icons ChevronDown / ChevronRight for the Mac's chevron.down / chevron.right.
            var chevron = new FontIcon { Glyph = open ? "\uE70D" : "\uE76C", FontSize = 10, FontWeight = FontWeights.Bold, Width = 11, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center };
            line.Children.Add(chevron);
            var title = ProviderMarkView.Labelled(GraphRequestTitle(run, group.Ordinal), ProviderMark.MarkedProvider(pane.Provider), DesignMetrics.Type.Timeline, FontWeights.Bold);
            PaintWords(title, b.Brush(DesignToken.Ink), new FontFamily(DesignMetrics.Font.Heading));
            line.Children.Add(title);
            if (older)
            {
                // Segoe Fluent Icons History for the Mac's clock.arrow.circlepath: read back from the session record.
                var clock = new FontIcon { Glyph = "\uE81C", FontSize = 11, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(clock, Locale.Get("graph.history.tag")); AutomationProperties.SetName(clock, Locale.Get("graph.history.tag"));
                line.Children.Add(clock);
            }
            head.Children.Add(line);
            var status = StatusPill(MightyGraphBlockModel.StateLabel(group.Status), StatusGlyph.Tone(group.Status), 20); status.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(status, 1); head.Children.Add(status); header.Children.Add(head);
            var promptText = string.Join(" ", group.Input.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
            TextBlock? prompt = null;
            if (promptText.Length > 0) header.Children.Add(prompt = new TextBlock { Text = promptText, FontSize = DesignMetrics.Type.Block, Foreground = ink2, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(19, 0, 0, 0) });
            var tally = new TextBlock { Text = Locale.Get("phone.blocks.tally", new Dictionary<string, string> { ["total"] = group.Tally.Total.ToString(), ["settled"] = group.Tally.Settled.ToString() }), FontSize = DesignMetrics.Type.Pill, FontWeight = FontWeights.SemiBold, Foreground = ink2, Margin = new Thickness(19, 0, 0, 0) };
            header.Children.Add(tally);
            timelineHeads[group.RunId] = (title, chevron, prompt, tally, status);
            var button = TimelineButton(header, "mighty-timeline-request-" + group.RunId, () => { ToggleTimelineSet(timelineFlipped, group.RunId); RefreshMightyView(Session); });
            AutomationProperties.SetName(button, GraphRequestTitle(run, group.Ordinal)); ToolTipService.SetToolTip(button, Locale.Get(open ? "phone.blocks.collapse" : "phone.blocks.expand")); stack.Children.Add(button);
            if (open) for (var i = 0; i < group.Rows.Count; i++) stack.Children.Add(BuildTimelineRow(group.Rows[i], i, group, run, pane));
            if (group.Result is { } result) stack.Children.Add(BuildTimelineResult(result, group, run, pane));
            return stack;
        }

        /// <summary>The ink a timeline row's icon and request band take: the request's style tint, a sub-agent its kind's ink.</summary>
        private DesignToken RowTint(MightyTimeline.Row row, MightyGraphRun run) => row.AgentIndex is null ? TintToken(RequestStyleLook(run.Input).Tint) : row.Kind switch
        {
            "task" => DesignToken.TaskText, "steer" => DesignToken.SteerText, "compact" => DesignToken.CompactText,
            "question" => DesignToken.QuestionText, _ => DesignToken.AgentText,
        };

        /// <summary>
        /// One block on the timeline (M/MightyGraphTimelineView.swift:146-216): a <c>card</c> with radius 14
        /// and a 1pt <c>line</c> edge (2pt <c>run</c> while it runs, 2pt <c>wait</c> while it waits), padding
        /// h11 v7; the title 13 bold <c>ink</c>, "kind · meta" 11 <c>ink2</c> after its icon in the block's
        /// tint, the latest step 10.8 mono <c>ink2</c>, the 19pt status pill. Open, the request on its tint ×
        /// 0.055 band and the transcript follow a 1pt <c>line</c>. The rail and node stand to its left.
        /// </summary>
        private FrameworkElement BuildTimelineRow(MightyTimeline.Row row, int index, MightyTimeline.Group group, MightyGraphRun run, RunSession pane)
        {
            var b = owner.brushes; var ink2 = b.Brush(DesignToken.Ink2); var tint = RowTint(row, run);
            var grid = new Grid { ColumnSpacing = 8 }; grid.ColumnDefinitions.Add(new() { Width = new GridLength(32) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var look = row.AgentIndex is null ? RequestStyleLook(run.Input) : (Icon: row.Kind switch { "task" => "✓", "steer" => "↪", "compact" => "⇥", "question" => "?", _ => "◇" }, Tint: "accent");
            var marker = TimelineMarker(row, index, group.Rows, look.Icon, out var node);
            grid.Children.Add(marker);
            var body = new StackPanel(); var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = row.Title, FontSize = DesignMetrics.Type.Title, FontWeight = FontWeights.Bold, Foreground = b.Brush(DesignToken.Ink), MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis });
            var agent = row.AgentIndex is { } n ? run.Agents[n] : null;
            var capsule = ModelUsageFormat.BlockCapsule(agent?.Usage ?? (agent is null ? run.Usage : null), agent?.ResponseRecords ?? (agent is null ? run.ResponseRecords : null) ?? [], agent is null ? run.NodeModelLabel : null, owner.Runtime(pane.Provider)?.ModelCatalog?.Models, versioned: true);
            var meta = new Grid { ColumnSpacing = 4 }; meta.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); meta.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            meta.Children.Add(new TextBlock { Text = look.Icon, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = b.Brush(tint), VerticalAlignment = VerticalAlignment.Center });
            var description = new TextBlock { Text = string.Join(" · ", new[] { MightyTimeline.KindLabel(row.Kind), capsule, MightyTimeline.DurationLabel(row.DurationMs) }.Where(s => s is { Length: > 0 })), FontSize = DesignMetrics.Type.Pill, Foreground = ink2, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(description, 1); meta.Children.Add(description); text.Children.Add(meta);
            if (row.Latest is { } latest) text.Children.Add(new TextBlock { Text = latest, FontSize = 10.8, FontFamily = new FontFamily(DesignMetrics.Font.Mono), Foreground = ink2, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
            var header = TimelineHeading(); header.Children.Add(text);
            var pill = StatusPill(MightyGraphBlockModel.StateLabel(row.Status), row.Node.Tone, 19); pill.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(pill, 1); header.Children.Add(pill);
            var button = TimelineButton(header, "mighty-timeline-row-" + row.NodeId, () => ToggleTimelineRow(row.NodeId));
            button.Padding = new Thickness(11, 7, 11, 7); AutomationProperties.SetName(button, row.Title + ", " + MightyGraphBlockModel.StateLabel(row.Status));
            ToolTipService.SetToolTip(button, Locale.Get(timelineOpen.Contains(row.NodeId) ? "phone.blocks.collapse" : "phone.blocks.expand")); body.Children.Add(button);
            if (timelineOpen.Contains(row.NodeId))
            {
                var detail = new StackPanel(); AutomationProperties.SetAutomationId(detail, "mighty-timeline-detail-" + row.NodeId);
                var input = agent?.Input ?? run.Input;
                if (input.Length > 0)
                    detail.Children.Add(new Border
                    {
                        Background = b.Brush(tint, DesignMetrics.Opacity.InputPreview), Padding = new Thickness(11, 7, 11, 7),
                        BorderThickness = new Thickness(0, 0, 0, DesignMetrics.Stroke.Line), BorderBrush = b.Brush(DesignToken.Line),
                        Child = new TextBlock { Text = input, FontSize = DesignMetrics.Type.Pill, Foreground = b.Brush(DesignToken.Ink), MaxLines = 6, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                    });
                var entries = (agent?.Entries ?? run.RootEntries).Where(e => e.Kind != "user").ToList();
                if (entries.Count == 0) detail.Children.Add(new TextBlock { Text = Locale.Get("phone.blocks.nothing"), FontSize = DesignMetrics.Type.Block, Foreground = ink2, Margin = new Thickness(11) });
                else detail.Children.Add(TimelineTranscript(row.NodeId, entries, pane, 260));
                body.Children.Add(new Border { Height = DesignMetrics.Stroke.Line, Background = b.Brush(DesignToken.Line) }); body.Children.Add(detail);
            }
            var edge = row.Node.Tone switch { DesignTone.Run => DesignToken.Run, DesignTone.Wait => DesignToken.Wait, _ => DesignToken.Line };
            var card = new Border
            {
                Child = body, CornerRadius = new CornerRadius(DesignMetrics.Radius.TimelineRow), Background = b.Brush(DesignToken.Card),
                BorderThickness = new Thickness(edge == DesignToken.Line ? DesignMetrics.Stroke.Line : DesignMetrics.Stroke.Active), BorderBrush = b.Brush(edge),
            };
            timelineRows[row.NodeId] = (card, node, pill);
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

        /// <summary>
        /// The card under a finished request (M/MightyGraphTimelineView.swift:219-268): a <c>card</c> with
        /// radius 16, its head strip <c>heroFill</c> of the outcome (padding h13 v6) carrying in <c>heroInk</c>
        /// the outcome icon (11 bold), the title (12 heavy), the files menu and the caption (10.5 semibold
        /// mono); under it the answer, and "show all" (12 bold <c>accent</c>) when it folds.
        /// </summary>
        private FrameworkElement BuildTimelineResult(MightyTimeline.Result result, MightyTimeline.Group group, MightyGraphRun run, RunSession pane)
        {
            var b = owner.brushes; var ink = b.FillInk(result.Tone);
            var body = new StackPanel();
            var radius = DesignMetrics.Radius.Composer;
            var header = TimelineHeading(); header.Padding = new Thickness(13, 6, 13, 6); header.Background = b.Fill(result.Tone);
            // The strip fills the card's top, so its corners follow the card's (all four when there is no answer under it).
            header.CornerRadius = result.Text is null ? new CornerRadius(radius) : new CornerRadius(radius, radius, 0, 0);
            var named = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            // Segoe Fluent Icons CheckMark / Warning / Stop for the Mac's checkmark.seal / exclamationmark.triangle / stop.circle.
            named.Children.Add(new FontIcon { Glyph = result.Tone == DesignTone.Err ? "\uE7BA" : result.Tone == DesignTone.Stop ? "\uE71A" : "\uE73E", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = ink, VerticalAlignment = VerticalAlignment.Center });
            var title = new TextBlock { Text = MightyGraphBlockModel.ResultTitle(run.Status), FontSize = DesignMetrics.Type.Block, FontWeight = FontWeights.ExtraBold, Foreground = ink, VerticalAlignment = VerticalAlignment.Center };
            named.Children.Add(title); header.Children.Add(named);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var files = run.Status == "completed" ? MightyGraphBlockModel.FilesFor(run, Workspace.Path) : [];
            if (files.Count > 0)
            {
                var menu = new MenuFlyout();
                foreach (var file in files)
                {
                    var item = new MenuFlyoutItem { Text = file.Path + (file.Line is { } line ? ":" + line : "") };
                    item.Click += async (_, _) => await OpenReferencePreview(file.Path, file.Line); menu.Items.Add(item);
                }
                var fileButton = new Button { Content = new TextBlock { Text = "▤ " + files.Count, FontSize = DesignMetrics.Type.Small }, Flyout = menu, MinWidth = 0, MinHeight = 22, Padding = new Thickness(5, 0, 5, 0), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
                owner.PaintPlainButton(fileButton, b.Transparent, b.Subtle, ink: ink);
                AutomationProperties.SetAutomationId(fileButton, "mighty-timeline-result-files-" + result.NodeId);
                AutomationProperties.SetName(fileButton, Locale.Get("graph.resultFiles.countLabel", new Dictionary<string, string> { ["count"] = files.Count.ToString() }));
                actions.Children.Add(fileButton);
            }
            var caption = new TextBlock
            {
                Text = Locale.Get("graph.timeline.requestOrdinal", new Dictionary<string, string> { ["n"] = group.Ordinal.ToString() }) + (run.TotalUsage is { } usage ? " · " + GraphTokenUsage.Compact(usage.Total) : ""),
                FontSize = 10.5, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily(DesignMetrics.Font.Mono), VerticalAlignment = VerticalAlignment.Center, Foreground = ink,
            };
            actions.Children.Add(caption);
            Grid.SetColumn(actions, 1); header.Children.Add(actions); body.Children.Add(header);
            if (result.Text is { } answer)
            {
                var content = new StackPanel();
                content.Children.Add(TimelineTranscript(result.NodeId, [run.ResultEntries.Last(e => e.Kind == "assistant" && e.Text.Length > 0)], pane, maxHeight: timelineFull.Contains(result.NodeId) ? null : 180));
                if (MightyTimeline.Folds(answer))
                {
                    var label = Locale.Get(timelineFull.Contains(result.NodeId) ? "phone.blocks.resultLess" : "phone.blocks.resultMore");
                    var toggle = new Button { Content = new TextBlock { Text = label, FontSize = DesignMetrics.Type.Block, FontWeight = FontWeights.Bold }, MinWidth = 0, MinHeight = 0, Height = 24, Padding = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(7, 0, 0, 8) };
                    owner.PaintPlainButton(toggle, b.Transparent, b.Subtle, ink: b.Brush(DesignToken.Accent));
                    AutomationProperties.SetAutomationId(toggle, "mighty-timeline-result-more-" + result.NodeId); AutomationProperties.SetName(toggle, label); ToolTipService.SetToolTip(toggle, label);
                    toggle.Click += (_, _) => { ToggleTimelineSet(timelineFull, result.NodeId); RefreshMightyView(Session); };
                    content.Children.Add(toggle);
                }
                body.Children.Add(content);
            }
            var card = new Border { Child = body, CornerRadius = new CornerRadius(radius), Background = b.Brush(DesignToken.Card), Margin = new Thickness(40, 0, 0, 0) };
            AutomationProperties.SetAutomationId(card, "mighty-timeline-result-" + result.NodeId);
            timelineResults[result.NodeId] = (card, header, title, caption);
            return card;
        }

        private static Grid TimelineHeading()
        {
            var grid = new Grid { ColumnSpacing = 8 }; grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); return grid;
        }

        /// <summary>A timeline header or row as one plain button: no fill at rest, the subtle wash under the pointer.</summary>
        private Button TimelineButton(UIElement content, string automationId, Action act)
        {
            var button = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(2), BorderThickness = new Thickness(0) };
            owner.PaintPlainButton(button, owner.brushes.Transparent, owner.brushes.Subtle);
            AutomationProperties.SetAutomationId(button, automationId); button.Click += (_, _) => act(); return button;
        }

        /// <summary>
        /// The rail and node beside one row (M/MightyGraphTimelineView.swift:69-144): 3pt rails in the tone's
        /// <c>heroFill</c> (<c>track</c> where the work above has not settled), a 24pt node in <c>heroFill</c>
        /// with a 3pt <c>cardRaised</c> ring and its icon (9 bold) in <c>heroInk</c>. A running node pulses:
        /// a disc of its colour grows to 1.95× and fades from 0.55 to nothing, ease-out, every 1.6 s; with
        /// Windows animations off it holds still as two soft rings.
        /// </summary>
        private FrameworkElement TimelineMarker(MightyTimeline.Row row, int index, IReadOnlyList<MightyTimeline.Row> rows, string icon, out Border node)
        {
            var b = owner.brushes;
            var marker = new Grid { Width = 32, IsHitTestVisible = false };
            Brush Rail(DesignTone? tone) => tone is { } t ? b.Fill(t) : b.Brush(DesignToken.Track);
            if (index > 0) marker.Children.Add(new Border { Width = DesignMetrics.Stroke.Rail, Height = 21, VerticalAlignment = VerticalAlignment.Top, Background = Rail(MightyTimeline.RailAbove(rows, index)) });
            if (index < rows.Count - 1) marker.Children.Add(new Border { Width = DesignMetrics.Stroke.Rail, Margin = new Thickness(0, 21, 0, -8), Background = Rail(row.Node.Rail) });
            var fill = b.Fill(row.Node.Tone);
            if (row.Node.Ring)
            {
                if (AnimationsEnabled)
                {
                    var scale = new ScaleTransform();
                    var ring = new Ellipse { Width = 24, Height = 24, Fill = fill, Opacity = .55, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 9, 0, 0), RenderTransformOrigin = new Point(.5, .5), RenderTransform = scale };
                    var story = new Storyboard();
                    foreach (var (target, property, from, to) in new (DependencyObject, string, double, double)[] { (scale, "ScaleX", 1, 1.95), (scale, "ScaleY", 1, 1.95), (ring, "Opacity", .55, 0) })
                    {
                        var grow = new DoubleAnimation { From = from, To = to, Duration = new Duration(TimeSpan.FromSeconds(1.6)), RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                        Storyboard.SetTarget(grow, target); Storyboard.SetTargetProperty(grow, property); story.Children.Add(grow);
                    }
                    ring.Loaded += (_, _) => story.Begin(); ring.Unloaded += (sender, _) => { if (!((FrameworkElement)sender).IsLoaded) story.Stop(); };
                    marker.Children.Add(ring);
                }
                else
                {
                    // Centred on the node (its centre is 21 down): 42 at 0.10, 32 at 0.28.
                    marker.Children.Add(new Ellipse { Width = 42, Height = 42, Fill = fill, Opacity = .10, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-5, 0, -5, 0) });
                    marker.Children.Add(new Ellipse { Width = 32, Height = 32, Fill = fill, Opacity = .28, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0) });
                }
            }
            node = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = fill, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 9, 0, 0),
                BorderThickness = new Thickness(DesignMetrics.Stroke.Rail), BorderBrush = b.Brush(DesignToken.CardRaised),
                Child = new TextBlock { Text = icon, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = b.FillInk(row.Node.Tone), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            marker.Children.Add(node);
            AutomationProperties.SetAccessibilityView(marker, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw); return marker;
        }
    }
}
