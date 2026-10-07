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
        private StackPanel? graphZoomControls, graphToolbarLead;
        private Grid? graphToolbar;
        private Button? diagramButton, timelineButton;
        /// <summary>The badge after the bar's title for a style that is not built in (M/GuidedActionChip.swift:83-93).</summary>
        private Border? graphStyleBadge;
        private readonly TextBlock graphStyleHeader = new() { FontSize = DesignMetrics.Type.Block, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        private ScrollViewer? timelineScroll;
        private readonly StackPanel timelineStack = new() { Spacing = DesignMetrics.Spacing.Md, Padding = new Thickness(DesignMetrics.Inset.GraphBlockBodyH, DesignMetrics.Spacing.Md, DesignMetrics.Inset.GraphBlockBodyH, DesignMetrics.Spacing.Md) };
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
        private readonly Dictionary<string, (Border Card, Grid Head, TextBlock Title, TextBlock Caption, TextBlock? Body)> timelineResults = [];

        /// <summary>
        /// The Diagram | Timeline switch that leads the Mighty bar (M/MightyGraphTimelineView.swift:10-42): a
        /// <c>track</c> rail, radius 8, padding 2, its options 2 apart; each 22 tall, padding h9, radius 6, its
        /// symbol (three joined nodes for the diagram, an indented list for the timeline) before its 11pt word.
        /// </summary>
        private FrameworkElement BuildGraphPresentationSwitch()
        {
            var switcher = graphViewSwitch = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xxs, Padding = new Thickness(DesignMetrics.Spacing.Xxs), Background = owner.brushes.Brush(DesignToken.Track), CornerRadius = new CornerRadius(DesignMetrics.Radius.Row), VerticalAlignment = VerticalAlignment.Center };
            diagramButton = ViewOption(Locale.Get("graph.view.diagram"), "point.3.connected.trianglepath.dotted", "mighty-view-diagram-" + id, () => _ = SetGraphPresentation("diagram"));
            timelineButton = ViewOption(Locale.Get("graph.view.timeline"), "list.bullet.indent", "mighty-view-timeline-" + id, () => _ = SetGraphPresentation("timeline"));
            switcher.Children.Add(diagramButton); switcher.Children.Add(timelineButton);
            AutomationProperties.SetName(switcher, Locale.Get("graph.view.switch"));
            return switcher;
        }

        /// <summary>
        /// A view option's symbol box and its distance from the word: the Mac's Label starts the word 24 after the
        /// option's padding, behind its 11pt symbol (docs/design-system/crops/mighty-bar-*.webp).
        /// </summary>
        private const double ViewSymbolBox = 16, ViewSymbolGap = 8;

        /// <summary>
        /// One view option: the button itself is plain (no fill at rest, the subtle wash under the pointer,
        /// written once); its chip, a Border filling it with padding h9, carries the chosen side's <c>card</c>,
        /// and the symbol and the word their ink (<see cref="RefreshGraphPresentationSwitch"/>), so choosing never
        /// rewrites resources.
        /// </summary>
        private Button ViewOption(string text, string symbol, string automationId, Action act)
        {
            var mark = new Grid { Width = ViewSymbolBox, VerticalAlignment = VerticalAlignment.Center };
            mark.Children.Add(MightySymbols.Create(symbol, DesignMetrics.Type.Pill, owner.brushes.Brush(DesignToken.Ink2), MightySymbols.Weight.Semibold));
            var face = new StackPanel { Orientation = Orientation.Horizontal, Spacing = ViewSymbolGap, VerticalAlignment = VerticalAlignment.Center };
            face.Children.Add(mark); face.Children.Add(new TextBlock { Text = text, FontSize = DesignMetrics.Type.Pill, VerticalAlignment = VerticalAlignment.Center });
            var chip = new Border { Child = face, Padding = new Thickness(DesignMetrics.Spacing.Md, 0, DesignMetrics.Spacing.Md, 0), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment) };
            var button = new Button { Content = chip, MinWidth = 0, MinHeight = 0, Height = 22, Padding = new Thickness(0), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            owner.PaintPlainButton(button, owner.brushes.Transparent, owner.brushes.Subtle);
            AutomationProperties.SetAutomationId(button, automationId); AutomationProperties.SetName(button, text);
            ToolTipService.SetToolTip(button, text);
            button.Click += (_, _) => act();
            return button;
        }

        /// <summary>A view option's chip, its symbol and its word.</summary>
        private static (Border Chip, FrameworkElement Symbol, TextBlock Words) ViewOptionParts(Button option)
        {
            var chip = (Border)option.Content; var face = (StackPanel)chip.Child;
            return (chip, (FrameworkElement)((Grid)face.Children[0]).Children[0], (TextBlock)face.Children[1]);
        }

        /// <summary>Whether the view switch shows its words; a bar too narrow for them shows its symbols only.</summary>
        private bool ViewWordsShown => diagramButton is not null && ViewOptionParts(diagramButton).Words.Visibility == Visibility.Visible;

        private void ShowViewWords(bool shown)
        {
            foreach (var option in new[] { diagramButton, timelineButton })
                if (option is not null) ViewOptionParts(option).Words.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// The badge after the name of a style that is not built in (M/GuidedActionChip.swift:83-93): 9pt
        /// <c>ink2</c> on the subtle wash, a capsule, padding h5 v1. Hidden until a style has a source to name.
        /// </summary>
        private Border BuildStyleSourceBadge() => new()
        {
            Visibility = Visibility.Collapsed, Padding = new Thickness(DesignMetrics.Spacing.Xs, 1, DesignMetrics.Spacing.Xs, 1), CornerRadius = new CornerRadius(8), Background = owner.brushes.Subtle, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { FontSize = DesignMetrics.Type.Badge, Foreground = owner.brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.NoWrap },
        };

        /// <summary>
        /// Keeps the Mighty bar one row at any width, as the Mac's is. The summary has the row's spare room and
        /// trims first; a bar too narrow for the rest shows the view switch as symbols only (the way the header's
        /// Default | Mighty switch does on a narrow pane) and then trims the title. No control ever leaves the bar.
        /// </summary>
        private void FitGraphToolbar()
        {
            if (graphToolbar is not { ActualWidth: > 0 } bar || graphToolbarLead is not { } lead || diagramButton is null) return;
            var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
            var room = bar.ActualWidth - bar.Padding.Left - bar.Padding.Right;
            // Beside the lead: the summary's column gaps and trailing space, and the zoom row while the diagram shows.
            var rest = 2 * bar.ColumnSpacing + graphTotal.Margin.Right;
            if (graphZoomControls is { Visibility: Visibility.Visible } zoom) { zoom.Measure(unbounded); rest += zoom.DesiredSize.Width; }
            graphStyleHeader.MaxWidth = double.PositiveInfinity;
            ShowViewWords(true); lead.Measure(unbounded);
            if (lead.DesiredSize.Width + rest > room) { ShowViewWords(false); lead.Measure(unbounded); }
            var over = lead.DesiredSize.Width + rest - room;
            if (over > 0) graphStyleHeader.MaxWidth = Math.Max(0, graphStyleHeader.DesiredSize.Width - over);
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
            // "Mighty · <style> · <phase>", the style by its own name (M/MightyCore/Styles/StyleSurfaces.swift:22-27); where it
            // came from follows in the badge when it is not built in (M/MightyGraphView.swift:181-183).
            var phase = activeStyle is null ? null : GuidedPhase(activeStyle);
            graphStyleHeader.Text = Locale.Get("graph.view.mighty") + (activeStyle is null ? "" : " · " + activeStyle.Manifest.Name + (phase is null ? "" : " · " + phase.Title));
            ToolTipService.SetToolTip(graphStyleHeader, graphStyleHeader.Text);
            if (graphStyleBadge is { Child: TextBlock source })
            {
                var named = activeStyle is { Source: not "bundled" } style ? StylePresentation.Source(style.Source) : null;
                source.Text = named ?? ""; graphStyleBadge.Visibility = named is null ? Visibility.Collapsed : Visibility.Visible;
                AutomationProperties.SetName(graphStyleBadge, named is null ? "" : Locale.Get("guidedPanel.sourcePrefix") + named);
            }
            if (graphZoomControls is not null) graphZoomControls.Visibility = timeline ? Visibility.Collapsed : Visibility.Visible;
            // The Mac's Spacer(minLength: 8) after the summary: with the row's 10 it stands 28 before the zoom, 18 before the bar's edge.
            graphTotal.Margin = new Thickness(0, 0, timeline ? DesignMetrics.Spacing.Md : DesignMetrics.Spacing.Lg, 0);
            foreach (var (button, selected, text) in new[] { (diagramButton, !timeline, Locale.Get("graph.view.diagram")), (timelineButton, timeline, Locale.Get("graph.view.timeline")) })
            {
                if (button is null) continue;
                var (chip, symbol, words) = ViewOptionParts(button);
                words.Text = text;
                // The chosen side is a card chip in bold ink; the other stays clear, semibold ink2, the subtle wash under the pointer.
                words.FontWeight = selected ? FontWeights.Bold : FontWeights.SemiBold;
                var b = owner.brushes; var ink = b.Brush(selected ? DesignToken.Ink : DesignToken.Ink2);
                chip.Background = selected ? b.Brush(DesignToken.Card) : b.Transparent; words.Foreground = ink; MightySymbols.Paint(symbol, ink);
                AutomationProperties.SetName(button, text);
            }
            FitGraphToolbar();
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

        /// <summary>
        /// The symbol (by its Mac name) and tint a request block takes from the style its prompt belongs to; the
        /// message with its arrow in <c>accent</c> for a plain request (M/MightyGraphView.swift:425, 429,
        /// M/MightyCore/Styles/StyleManifest.swift:228).
        /// </summary>
        private (string Symbol, string Tint) RequestStyleLook(string prompt)
        {
            var look = activeStyle is null || styleRegistry is null ? null : StylePresentation.Request(styleRegistry.Styles, prompt);
            return (MightySymbols.Known(look?.Icon) ? look!.Icon! : "arrow.up.message", look?.Tint ?? "accent");
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
                if (groups.Count == 0) desired.Add(new TextBlock { Text = Locale.Get("graph.timeline.empty"), FontSize = DesignMetrics.Type.Block, Foreground = owner.brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, DesignMetrics.Spacing.Sm, 0, DesignMetrics.Spacing.Sm) });
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

        /// <summary>A request's rows stand <c>Spacing.Xs</c> apart; its header, rows and result <c>Spacing.Sm</c> (M/MightyGraphView.swift:733, 736).</summary>
        private const double TimelineRowGap = DesignMetrics.Spacing.Xs;
        /// <summary>The marker column beside a row, and what follows it <c>Spacing.Md</c> further in (M/MightyGraphTimelineView.swift:80, M/MightyGraphView.swift:743, 807).</summary>
        private const double TimelineMarkerWidth = 32, TimelineMarkerGap = DesignMetrics.Spacing.Md;
        /// <summary>A node is 24 across, two points under the row card's own top padding; the rails meet at its centre (M/MightyGraphTimelineView.swift:81-84).</summary>
        private const double TimelineNode = 24, TimelineNodeTop = DesignMetrics.Inset.GraphBlockBodyV + 2, TimelineNodeCentre = TimelineNodeTop + TimelineNode / 2;
        /// <summary>A row's 13pt title line and its 11pt "kind · meta" line as the Mac lays them out (M/MightyGraphTimelineView.swift:161-168).</summary>
        private const double TimelineTitleLine = 16, TimelineMetaLine = 14;
        /// <summary>The result card's answer: 12.5pt, eight lines until it is opened (M/MightyGraphTimelineView.swift:216, 234-235).</summary>
        private const double TimelineResultBody = 12.5;
        private const int TimelineResultLines = 8;

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
            var stack = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
            var header = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
            var head = TimelineHeading();
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Md, VerticalAlignment = VerticalAlignment.Center };
            var chevron = (FontIcon)MightySymbols.Create(open ? "chevron.down" : "chevron.right", 10, ink2);
            chevron.FontWeight = FontWeights.Bold; chevron.Width = 11;
            line.Children.Add(chevron);
            var title = ProviderMarkView.Labelled(GraphRequestTitle(run, group.Ordinal), ProviderMark.MarkedProvider(pane.Provider), DesignMetrics.Type.Timeline, FontWeights.Bold);
            PaintWords(title, b.Brush(DesignToken.Ink), new FontFamily(DesignMetrics.Font.Heading));
            line.Children.Add(title);
            if (older)
            {
                // Read back from the session record (the Mac's clock.arrow.circlepath).
                var clock = MightySymbols.Create("clock.arrow.circlepath", 11, ink2);
                clock.IsHitTestVisible = true;
                ToolTipService.SetToolTip(clock, Locale.Get("graph.history.tag")); AutomationProperties.SetName(clock, Locale.Get("graph.history.tag"));
                AutomationProperties.SetAccessibilityView(clock, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Content);
                line.Children.Add(clock);
            }
            head.Children.Add(line);
            var status = StatusPill(MightyGraphBlockModel.StateLabel(group.Status), StatusGlyph.Tone(group.Status), 20);
            Grid.SetColumn(status, 1); head.Children.Add(status); header.Children.Add(head);
            var promptText = string.Join(" ", group.Input.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
            TextBlock? prompt = null;
            if (promptText.Length > 0) header.Children.Add(prompt = new TextBlock { Text = promptText, FontSize = DesignMetrics.Type.Block, Foreground = ink2, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(DesignMetrics.Spacing.Lg, 0, 0, 0) });
            var tally = new TextBlock { Text = Locale.Get("phone.blocks.tally", new Dictionary<string, string> { ["total"] = group.Tally.Total.ToString(), ["settled"] = group.Tally.Settled.ToString() }), FontSize = DesignMetrics.Type.Pill, FontWeight = FontWeights.SemiBold, Foreground = ink2, Margin = new Thickness(DesignMetrics.Spacing.Lg, 0, 0, 0) };
            header.Children.Add(tally);
            timelineHeads[group.RunId] = (title, chevron, prompt, tally, status);
            var button = TimelineButton(header, "mighty-timeline-request-" + group.RunId, new Thickness(DesignMetrics.Spacing.Xxs, 0, DesignMetrics.Spacing.Xxs, 0), () => { ToggleTimelineSet(timelineFlipped, group.RunId); RefreshMightyView(Session); });
            AutomationProperties.SetName(button, GraphRequestTitle(run, group.Ordinal)); ToolTipService.SetToolTip(button, Locale.Get(open ? "phone.blocks.collapse" : "phone.blocks.expand")); stack.Children.Add(button);
            if (open)
            {
                var rows = new StackPanel { Spacing = TimelineRowGap };
                for (var i = 0; i < group.Rows.Count; i++) rows.Children.Add(BuildTimelineRow(group.Rows[i], i, group, run, pane));
                stack.Children.Add(rows);
            }
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
        /// One block on the timeline (M/MightyGraphTimelineView.swift:142-204): a <c>card</c> with radius 14
        /// and a 1pt <c>line</c> edge (2pt <c>run</c> while it runs, 2pt <c>wait</c> while it waits), padding
        /// h11 v7 from the card's own edge; the title 13 bold <c>ink</c>, "kind · meta" 11 <c>ink2</c> after its
        /// symbol (9 bold) in the block's tint, the latest step 10.8 mono <c>ink2</c> after the pulsing run dot,
        /// the 19pt status pill. Open, the request on its tint × 0.055 band and the transcript follow a 1pt
        /// <c>line</c>. The rail and node stand to its left.
        /// </summary>
        private FrameworkElement BuildTimelineRow(MightyTimeline.Row row, int index, MightyTimeline.Group group, MightyGraphRun run, RunSession pane)
        {
            var b = owner.brushes; var ink2 = b.Brush(DesignToken.Ink2); var tint = RowTint(row, run);
            var tone = row.Node.Tone; var open = timelineOpen.Contains(row.NodeId);
            var edge = tone switch { DesignTone.Run => DesignToken.Run, DesignTone.Wait => DesignToken.Wait, _ => DesignToken.Line };
            // The Mac strokes the edge over the card's content, so the row's padding is measured through it.
            var line = edge == DesignToken.Line ? DesignMetrics.Stroke.Line : DesignMetrics.Stroke.Active;
            var symbol = row.AgentIndex is null ? RequestStyleLook(run.Input).Symbol : BlockSymbol(row.Kind);
            var grid = new Grid { ColumnSpacing = TimelineMarkerGap }; grid.ColumnDefinitions.Add(new() { Width = new GridLength(TimelineMarkerWidth) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var marker = TimelineMarker(row, index, group.Rows, symbol, out var node);
            grid.Children.Add(marker);
            // The title and the line under it stand on the Mac's line pitch (13 on 16, 11 on 14; Segoe's own are 18 and 15), so a row
            // of the two is 46 high as the Mac's is (v7, 16, 2, 14, v7; docs/design-system/crops/timeline-*.webp).
            var body = new StackPanel(); var text = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
            text.Children.Add(new TextBlock { Text = row.Title, FontSize = DesignMetrics.Type.Title, FontWeight = FontWeights.Bold, Foreground = b.Brush(DesignToken.Ink), MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = TimelineTitleLine, LineStackingStrategy = LineStackingStrategy.BlockLineHeight });
            var agent = row.AgentIndex is { } n ? run.Agents[n] : null;
            var capsule = ModelUsageFormat.BlockCapsule(agent?.Usage ?? (agent is null ? run.Usage : null), agent?.ResponseRecords ?? (agent is null ? run.ResponseRecords : null) ?? [], agent is null ? run.NodeModelLabel : null, owner.Runtime(pane.Provider)?.ModelCatalog?.Models, versioned: true);
            var meta = new Grid { ColumnSpacing = DesignMetrics.Spacing.Xs, Height = TimelineMetaLine }; meta.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); meta.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            meta.Children.Add(MightySymbols.Create(symbol, 9, b.Brush(tint), MightySymbols.Weight.Bold, b.Brush(DesignToken.Card)));
            var description = new TextBlock { Text = string.Join(" · ", new[] { MightyTimeline.KindLabel(row.Kind), capsule, MightyTimeline.DurationLabel(row.DurationMs) }.Where(s => s is { Length: > 0 })), FontSize = DesignMetrics.Type.Pill, Foreground = ink2, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, LineHeight = TimelineMetaLine, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
            Grid.SetColumn(description, 1); meta.Children.Add(description); text.Children.Add(meta);
            if (row.Latest is { } latest)
            {
                var step = new TextBlock { Text = latest, FontSize = 10.8, FontFamily = new FontFamily(DesignMetrics.Font.Mono), Foreground = ink2, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                FrameworkElement doing = step;
                if (tone == DesignTone.Run)
                {
                    var pulsing = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm }; pulsing.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); pulsing.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                    pulsing.Children.Add(MainWindow.PulseDot(owner.brushes, AnimationsEnabled)); Grid.SetColumn(step, 1); pulsing.Children.Add(step);
                    doing = pulsing;
                }
                doing.Margin = new Thickness(0, DesignMetrics.Spacing.Xxs, 0, 0); text.Children.Add(doing);
            }
            var header = TimelineHeading(); header.Children.Add(text);
            var pill = StatusPill(MightyGraphBlockModel.StateLabel(row.Status), tone, 19); pill.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(pill, 1); header.Children.Add(pill);
            var button = TimelineButton(header, "mighty-timeline-row-" + row.NodeId, new Thickness(DesignMetrics.Inset.GraphBlockBodyH - line, DesignMetrics.Inset.GraphBlockBodyV - line, DesignMetrics.Inset.GraphBlockBodyH - line, DesignMetrics.Inset.GraphBlockBodyV - line), () => ToggleTimelineRow(row.NodeId));
            // The wash under the pointer follows the card's inner curve (square below while the block is open).
            var inner = DesignMetrics.Radius.TimelineRow - line;
            button.CornerRadius = open ? new CornerRadius(inner, inner, 0, 0) : new CornerRadius(inner);
            AutomationProperties.SetName(button, row.Title + ", " + MightyGraphBlockModel.StateLabel(row.Status));
            ToolTipService.SetToolTip(button, Locale.Get(open ? "phone.blocks.collapse" : "phone.blocks.expand")); body.Children.Add(button);
            if (open)
            {
                var detail = new StackPanel(); AutomationProperties.SetAutomationId(detail, "mighty-timeline-detail-" + row.NodeId);
                var input = agent?.Input ?? run.Input;
                if (input.Length > 0)
                    detail.Children.Add(new Border
                    {
                        Background = b.Brush(tint, DesignMetrics.Opacity.InputPreview), Padding = new Thickness(DesignMetrics.Inset.GraphBlockBodyH - line, DesignMetrics.Inset.GraphBlockBodyV, DesignMetrics.Inset.GraphBlockBodyH - line, DesignMetrics.Inset.GraphBlockBodyV),
                        BorderThickness = new Thickness(0, 0, 0, DesignMetrics.Stroke.Line), BorderBrush = b.Brush(DesignToken.Line),
                        Child = new TextBlock { Text = input, FontSize = DesignMetrics.Type.Pill, Foreground = b.Brush(DesignToken.Ink), MaxLines = 6, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                    });
                var entries = (agent?.Entries ?? run.RootEntries).Where(e => e.Kind != "user").ToList();
                if (entries.Count == 0) detail.Children.Add(new TextBlock { Text = Locale.Get("phone.blocks.nothing"), FontSize = DesignMetrics.Type.Block, Foreground = ink2, Margin = new Thickness(DesignMetrics.Inset.GraphBlockBodyH - line, DesignMetrics.Inset.GraphBlockBodyH, DesignMetrics.Inset.GraphBlockBodyH - line, DesignMetrics.Inset.GraphBlockBodyH - line) });
                else detail.Children.Add(TimelineTranscript(row.NodeId, entries, pane, 260, new Thickness(DesignMetrics.Inset.Transcript - line, DesignMetrics.Inset.Transcript, DesignMetrics.Inset.Transcript - line, DesignMetrics.Inset.Transcript + 22)));
                body.Children.Add(new Border { Height = DesignMetrics.Stroke.Line, Background = b.Brush(DesignToken.Line) }); body.Children.Add(detail);
            }
            var card = new Border
            {
                Child = body, CornerRadius = new CornerRadius(DesignMetrics.Radius.TimelineRow), Background = b.Brush(DesignToken.Card),
                BorderThickness = new Thickness(line), BorderBrush = b.Brush(edge),
            };
            timelineRows[row.NodeId] = (card, node, pill);
            Grid.SetColumn(card, 1); grid.Children.Add(card); return grid;
        }

        /// <summary>
        /// A block's transcript under its opened row, 260 tall, with the inset a block transcript takes
        /// (M/MightyGraphView.swift:827-828, M/AgentTranscriptView.swift:35, 168, 372-380).
        /// </summary>
        private FrameworkElement TimelineTranscript(string node, List<LogEntry> entries, RunSession pane, double height, Thickness inset)
        {
            if (!timelineTranscripts.TryGetValue(node, out var transcript))
            {
                transcript = new AgentTranscript { OpenReference = OpenReferencePreview, OpenImage = OpenTranscriptImage };
                transcript.SelectionEnded = () => { if (timelineDeferred) { timelineDeferred = false; Container.DispatcherQueue.TryEnqueue(() => { if (QueuePaneAlive) RefreshMightyView(Session); }); } };
                timelineTranscripts[node] = transcript;
            }
            if (transcript.View.Parent is Panel old) old.Children.Remove(transcript.View);
            // The sides are the box's own padding; the space over the first line and under the last is written into the document.
            transcript.View.MinHeight = 0; transcript.View.Height = height; transcript.View.Padding = new Thickness(inset.Left, 0, inset.Right, 0);
            transcript.TopInset = inset.Top; transcript.BottomInset = inset.Bottom;
            transcript.Update(pane with { Logs = entries, Kind = "claude" }, !owner.DarkTheme, owner.pictures, Workspace.Path);
            return transcript.View;
        }

        /// <summary>The symbol on a result's strip, by how the request ended (M/MightyGraphTimelineView.swift:262-268).</summary>
        private static string TimelineResultSymbol(DesignTone tone) => tone switch
        {
            DesignTone.Err => "exclamationmark.triangle.fill", DesignTone.Stop => "stop.circle.fill", _ => "checkmark.seal.fill",
        };

        /// <summary>
        /// The card under a finished request (M/MightyGraphTimelineView.swift:208-269): a <c>card</c> with
        /// radius 16 and no edge, its head strip <c>heroFill</c> of the outcome (padding h13 v6) carrying in
        /// <c>heroInk</c> the outcome's filled symbol (11 bold), the title (12 heavy), the files menu and the
        /// caption (10.5 semibold mono); under it the answer as plain words, 12.5 <c>ink</c>, eight lines until
        /// "show all" (12 bold <c>accent</c>) opens it.
        /// </summary>
        private FrameworkElement BuildTimelineResult(MightyTimeline.Result result, MightyTimeline.Group group, MightyGraphRun run, RunSession pane)
        {
            var b = owner.brushes; var ink = b.FillInk(result.Tone); var fill = b.Fill(result.Tone);
            var body = new StackPanel();
            var radius = DesignMetrics.Radius.Composer;
            // The Mac draws the strip as a shape of the card's own radius, round at every corner
            // (docs/design-system/crops/timeline-*.webp): a strip lower than twice the radius ends in half circles.
            var header = TimelineHeading(); header.Padding = new Thickness(DesignMetrics.Inset.GraphBlockBodyH, DesignMetrics.Inset.GraphBlockBodyV, DesignMetrics.Inset.GraphBlockBodyH, DesignMetrics.Inset.GraphBlockBodyV); header.Background = fill; header.CornerRadius = new CornerRadius(radius);
            var named = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm, VerticalAlignment = VerticalAlignment.Center };
            named.Children.Add(MightySymbols.Create(TimelineResultSymbol(result.Tone), 11, ink, MightySymbols.Weight.Bold, fill));
            var title = new TextBlock { Text = MightyGraphBlockModel.ResultTitle(run.Status), FontSize = DesignMetrics.Type.Block, FontWeight = FontWeights.ExtraBold, Foreground = ink, VerticalAlignment = VerticalAlignment.Center };
            named.Children.Add(title); header.Children.Add(named);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm, VerticalAlignment = VerticalAlignment.Center };
            var files = run.Status == "completed" ? MightyGraphBlockModel.FilesFor(run, Workspace.Path) : [];
            if (files.Count > 0)
            {
                var menu = new MenuFlyout();
                foreach (var file in files)
                {
                    var item = new MenuFlyoutItem { Text = file.Path + (file.Line is { } fileLine ? ":" + fileLine : "") };
                    item.Click += async (_, _) => await OpenReferencePreview(file.Path, file.Line); menu.Items.Add(item);
                }
                // The two documents and how many the answer names (M/MightyGraphView.swift:851-857).
                var face = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xxs, VerticalAlignment = VerticalAlignment.Center };
                face.Children.Add(MightySymbols.Create("doc.on.doc", 13, ink));
                var count = new TextBlock { Text = files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = DesignMetrics.Type.Small, Foreground = ink, VerticalAlignment = VerticalAlignment.Center };
                Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(count, FontNumeralAlignment.Tabular);
                face.Children.Add(count);
                // 18 tall, so the strip around it is the Mac's 30.
                var fileButton = HeaderButton(face, ink); fileButton.Flyout = menu; fileButton.Height = 18;
                AutomationProperties.SetAutomationId(fileButton, "mighty-timeline-result-files-" + result.NodeId);
                AutomationProperties.SetName(fileButton, Locale.Get("graph.resultFiles.countLabel", new Dictionary<string, string> { ["count"] = files.Count.ToString() }));
                ToolTipService.SetToolTip(fileButton, Locale.Get("graph.resultFiles.openButton"));
                actions.Children.Add(fileButton);
            }
            var caption = new TextBlock
            {
                Text = Locale.Get("graph.timeline.requestOrdinal", new Dictionary<string, string> { ["n"] = group.Ordinal.ToString() }) + (run.TotalUsage is { } usage ? " · " + GraphTokenUsage.Compact(usage.Total) : ""),
                FontSize = 10.5, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily(DesignMetrics.Font.Mono), VerticalAlignment = VerticalAlignment.Center, Foreground = ink,
            };
            actions.Children.Add(caption);
            Grid.SetColumn(actions, 1); header.Children.Add(actions); body.Children.Add(header);
            TextBlock? words = null;
            if (result.Text is { } answer)
            {
                var full = timelineFull.Contains(result.NodeId);
                var content = new StackPanel { Spacing = DesignMetrics.Spacing.Xs, Margin = new Thickness(DesignMetrics.Inset.GraphBlockBodyH, DesignMetrics.Spacing.Sm, DesignMetrics.Inset.GraphBlockBodyH, DesignMetrics.Spacing.Md) };
                content.Children.Add(words = new TextBlock
                {
                    Text = answer, FontSize = TimelineResultBody, Foreground = b.Brush(DesignToken.Ink), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                    MaxLines = full ? 0 : TimelineResultLines, TextTrimming = full ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                });
                if (MightyTimeline.Folds(answer))
                {
                    var label = full ? Locale.Get("phone.blocks.resultLess") : Locale.Get("phone.blocks.resultMore");
                    var toggle = HeaderButton(new TextBlock { Text = label, FontSize = DesignMetrics.Type.Block, FontWeight = FontWeights.Bold }, b.Brush(DesignToken.Accent));
                    toggle.HorizontalAlignment = HorizontalAlignment.Left;
                    AutomationProperties.SetAutomationId(toggle, "mighty-timeline-result-more-" + result.NodeId); AutomationProperties.SetName(toggle, label); ToolTipService.SetToolTip(toggle, label);
                    toggle.Click += (_, _) => { ToggleTimelineSet(timelineFull, result.NodeId); RefreshMightyView(Session); };
                    content.Children.Add(toggle);
                }
                body.Children.Add(content);
            }
            var card = new Border { Child = body, CornerRadius = new CornerRadius(radius), Background = b.Brush(DesignToken.Card), Margin = new Thickness(TimelineMarkerWidth + TimelineMarkerGap, 0, 0, 0) };
            AutomationProperties.SetAutomationId(card, "mighty-timeline-result-" + result.NodeId);
            timelineResults[result.NodeId] = (card, header, title, caption, words);
            return card;
        }

        private static Grid TimelineHeading()
        {
            var grid = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm }; grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); return grid;
        }

        /// <summary>A timeline header or row as one plain button: no fill at rest, the subtle wash under the pointer.</summary>
        private Button TimelineButton(UIElement content, string automationId, Thickness padding, Action act)
        {
            var button = new Button { Content = content, MinWidth = 0, MinHeight = 0, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = padding, BorderThickness = new Thickness(0) };
            owner.PaintPlainButton(button, owner.brushes.Transparent, owner.brushes.Subtle);
            AutomationProperties.SetAutomationId(button, automationId); button.Click += (_, _) => act(); return button;
        }

        /// <summary>
        /// The rail and node beside one row (M/MightyGraphTimelineView.swift:68-137): 3pt rails in the tone's
        /// <c>heroFill</c> (<c>track</c> where the work above has not settled), a 24pt node in <c>heroFill</c>
        /// with a 3pt <c>cardRaised</c> ring and the block's symbol (9 bold) in <c>heroInk</c>. A running node
        /// pulses: a disc of its colour grows to 1.95× and fades from 0.55 to nothing, ease-out, every 1.6 s; with
        /// Windows animations off it holds still as two soft rings.
        /// </summary>
        private FrameworkElement TimelineMarker(MightyTimeline.Row row, int index, IReadOnlyList<MightyTimeline.Row> rows, string symbol, out Border node)
        {
            var b = owner.brushes;
            var marker = new Grid { Width = TimelineMarkerWidth, IsHitTestVisible = false };
            Brush Rail(DesignTone? tone) => tone is { } t ? b.Fill(t) : b.Brush(DesignToken.Track);
            if (index > 0) marker.Children.Add(new Border { Width = DesignMetrics.Stroke.Rail, Height = TimelineNodeCentre, VerticalAlignment = VerticalAlignment.Top, Background = Rail(MightyTimeline.RailAbove(rows, index)) });
            // The rail under the node runs on through the gap to the next row.
            if (index < rows.Count - 1) marker.Children.Add(new Border { Width = DesignMetrics.Stroke.Rail, Margin = new Thickness(0, TimelineNodeCentre, 0, -TimelineRowGap), Background = Rail(row.Node.Rail) });
            var fill = b.Fill(row.Node.Tone);
            if (row.Node.Ring)
            {
                if (AnimationsEnabled)
                {
                    var scale = new ScaleTransform();
                    var ring = new Ellipse { Width = TimelineNode, Height = TimelineNode, Fill = fill, Opacity = .55, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, TimelineNodeTop, 0, 0), RenderTransformOrigin = new Point(.5, .5), RenderTransform = scale };
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
                    // Centred on the node (its centre is TimelineNodeCentre down): 42 at 0.10, 32 at 0.28.
                    marker.Children.Add(new Ellipse { Width = 42, Height = 42, Fill = fill, Opacity = .10, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-5, TimelineNodeCentre - 21, -5, 0) });
                    marker.Children.Add(new Ellipse { Width = 32, Height = 32, Fill = fill, Opacity = .28, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, TimelineNodeCentre - 16, 0, 0) });
                }
            }
            var mark = MightySymbols.Create(symbol, 9, b.FillInk(row.Node.Tone), MightySymbols.Weight.Bold, fill);
            mark.HorizontalAlignment = HorizontalAlignment.Center;
            node = new Border
            {
                Width = TimelineNode, Height = TimelineNode, CornerRadius = new CornerRadius(TimelineNode / 2), Background = fill, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, TimelineNodeTop, 0, 0),
                BorderThickness = new Thickness(DesignMetrics.Stroke.Rail), BorderBrush = b.Brush(DesignToken.CardRaised), Child = mark,
            };
            marker.Children.Add(node);
            AutomationProperties.SetAccessibilityView(marker, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw); return marker;
        }

    }
}
