using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        internal async Task<Dictionary<string, object?>> RunTimelineSmoke()
        {
            var original = Session; var zoom = graphZoom; var captures = new List<string>();
            var old = new MightyGraphRun { Id = "timeline-smoke-old", Input = "\uC774\uC804 \uC694\uCCAD", Status = "completed", FinalOutput = "\uC774\uC804 \uACB0\uACFC" };
            var latest = new MightyGraphRun { Id = "timeline-smoke-new", Input = "\uD55C\uAE00 \uC694\uCCAD\uACFC \uD558\uC704 \uC5D0\uC774\uC804\uD2B8", Status = "completed", FinalOutput = string.Join("\n", Enumerable.Range(1, 12).Select(i => "\uACB0\uACFC " + i)) + "\n[\uCC38\uC870](docs/mighty-note.md)",
                RootEntries = [new("timeline-root", "assistant", "\uD655\uC778\uD588\uC2B5\uB2C8\uB2E4. docs/mighty-note.md", Wire.Now())],
                Agents = [new() { Id = "timeline-child", Title = "\uD558\uC704 \uC5D0\uC774\uC804\uD2B8", Status = "completed", Entries = [new("timeline-child-answer", "assistant", "\uD558\uC704 \uACB0\uACFC", Wire.Now())] }] };
            MightyGraphSupport.RefreshResult(old); MightyGraphSupport.RefreshResult(latest);
            try
            {
                await SetGraphRunsForSmoke([old, latest]); await SetAgentViewMode("mighty"); await SetGraphPresentation("diagram");
                void Invoke(Button button)
                {
                    var peer = new ButtonAutomationPeer(button);
                    ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                }
                Button FindButton(string automationId) => VisualChildren(timelineScroll!).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == automationId);
                Invoke(timelineButton!);
                await WaitUI(() => Session.GraphViewMode == "timeline" && timelineScroll?.Visibility == Visibility.Visible && timelineGroups.Count == 2);
                owner.root.UpdateLayout();
                Require(graphViewport?.Visibility == Visibility.Collapsed && graphZoomControls?.Visibility == Visibility.Collapsed, "timeline replaces canvas and zoom only");
                Require(Session.Draft == original.Draft && graphZoom == zoom, "timeline switch preserves composer draft and diagram camera zoom");
                var rows = VisualChildren(timelineScroll!).OfType<Button>().Where(b => AutomationProperties.GetAutomationId(b).StartsWith("mighty-timeline-row-", StringComparison.Ordinal)).ToArray();
                // The newest request's rows: the request, its 실행 block and the sub-agent.
                Require(rows.Length == 3 && timelineHistoryCard is not null, "only newest group opens initially; same history affordance remains");
                var resultId = MightyGraphLayout.NodeID(latest, "result"); var requestId = MightyGraphLayout.NodeID(latest, "request"); var workId = MightyGraphLayout.NodeID(latest, MightyGraphLayout.WorkSuffix);
                // The 실행 row opens to the main agent's records; the request row has no status pill and opens to its input alone.
                Invoke(FindButton("mighty-timeline-row-" + workId));
                await WaitUI(() => timelineOpen.Contains(workId) && timelineTranscripts.ContainsKey(workId));
                Require(timelineTranscripts[workId].OpenReference is not null && timelineTranscripts[workId].OpenImage is not null, "expanded timeline detail shares transcript actions");
                Require(timelineRows[requestId].Pill is null && timelineRows[workId].Pill is not null, "the request row has no status pill and its execution row has one");
                owner.root.UpdateLayout();
                Invoke(FindButton("mighty-timeline-row-" + requestId));
                await WaitUI(() => timelineOpen.Contains(requestId) && VisualChildren(timelineScroll!).OfType<StackPanel>().Any(detail => AutomationProperties.GetAutomationId(detail) == "mighty-timeline-detail-" + requestId));
                Require(!timelineTranscripts.ContainsKey(requestId), "the request row opens to its input alone, with no transcript");
                var unchanged = timelineGroups[latest.Id].View; RefreshMightyView(Session);
                Require(ReferenceEquals(unchanged, timelineGroups[latest.Id].View), "identical refresh preserves native selection and focus");
                // The result shows its answer as plain words, eight lines until "show all" opens it (M/MightyGraphTimelineView.swift:216, 234-236).
                Require(timelineResults[resultId].Body is { MaxLines: TimelineResultLines }, "the folded timeline result shows eight lines of its answer");
                Invoke(FindButton("mighty-timeline-result-more-" + resultId));
                await WaitUI(() => timelineFull.Contains(resultId));
                Require(timelineResults[resultId].Body is { MaxLines: 0 }, "show-all removes the preview's line cap");
                var files = FindButton("mighty-timeline-result-files-" + resultId);
                await WaitUI(() => files.IsLoaded && files.ActualWidth > 0 && files.ActualHeight > 0);
                files.Flyout!.ShowAt(files);
                var item = ((MenuFlyout)files.Flyout).Items.OfType<MenuFlyoutItem>().Single();
                await WaitUI(() => item.IsLoaded && item.ActualWidth > 0 && item.ActualHeight > 0);
                var itemPeer = new MenuFlyoutItemAutomationPeer(item);
                ((IInvokeProvider)itemPeer.GetPattern(PatternInterface.Invoke)).Invoke();
                await WaitUI(() => referenceTarget?.RelativePath == "docs/mighty-note.md" && referenceBody?.Child is not ProgressRing);
                Require(referencePanel?.Visibility == Visibility.Visible, "timeline result file opens the same inline reference bubble");
                files.Flyout.Hide();
                // The bubble stands 12 inside the timeline, on the right, its tail towards the content (M/MightyGraphReferenceBubble.swift:33-45).
                owner.root.UpdateLayout();
                Require(referencePanel is { HorizontalAlignment: HorizontalAlignment.Right, ActualWidth: > ReferenceMinimumWidth } && referencePanel.Margin.Right == ReferenceMargin && referenceCard is { Margin.Left: ReferenceTail },
                    "the reference bubble docks on the right, 12 from the edge, its tail on the left");
                await SettleDesktopCapture(Container);
                captures.Add(await owner.CaptureSmoke(Path.Combine(owner.options.ProfileDirectory!, "smoke-mighty-reference.png")));
                CloseReferencePreview();
                captures.Add(await owner.CaptureSmoke(Path.Combine(owner.options.ProfileDirectory!, "smoke-mighty-timeline.png")));
                Invoke(FindButton("mighty-timeline-request-" + old.Id));
                await WaitUI(() => VisualChildren(timelineScroll!).OfType<Button>().Count(b => AutomationProperties.GetAutomationId(b).StartsWith("mighty-timeline-row-", StringComparison.Ordinal)) == 5);
                Invoke(diagramButton!);
                await WaitUI(() => Session.GraphViewMode == "diagram" && graphViewport?.Visibility == Visibility.Visible);
                Require(graphZoom == zoom && Session.Draft == original.Draft && ReadGraphForSmoke().Blocks >= 5, "returning to diagram retains blocks, zoom and draft");
                // The board is the switch's third choice (M/MightyGraphTimelineView.swift:17-19): one request at a time, ◀ and the list to
                // move between them, the badge of a newer one while an older one is shown, and a card's small button back to its block in
                // the diagram (M/MightyStructureView.swift; the Mac smoke drives the same steps, M/MightyStructureInteractionDiagnostics.swift).
                Button[] StructureButtons(string prefix) => VisualChildren(structureView!).OfType<Button>().Where(b => AutomationProperties.GetAutomationId(b).StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                var agentId = MightyGraphLayout.NodeID(latest, "agent:timeline-child"); var listOpenedBy = new List<string>();
                // The title's click opens the list; if it has not within 1.5 s, the flyout is shown as the button shows it, and which drove each step is recorded (as the Mac's smoke does).
                async Task OpenStructureList(int count)
                {
                    Invoke(structureListButton!);
                    try { await WaitUI(() => structureListFlyout?.IsOpen == true, "the title's click opens the list", 1.5); listOpenedBy.Add("click"); }
                    catch (TimeoutException) { structureListFlyout!.ShowAt(structureListButton!); listOpenedBy.Add("ShowAt"); }
                    await WaitUI(() => structureListFlyout?.IsOpen == true && structureList?.Children.Count == count, "the list holds every request: " + count);
                }
                Button StructureEntry(string run) => structureList!.Children.OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "mighty-structure-entry-" + run);
                Invoke(structureButton!);
                await WaitUI(() => Session.GraphViewMode == "structure" && structureView?.Visibility == Visibility.Visible && structureBoardId == latest.Id && structureCards.ContainsKey("result:" + latest.Id),
                    "the board opens on the newest request");
                Require(graphViewport?.Visibility == Visibility.Collapsed && timelineScroll?.Visibility == Visibility.Collapsed && graphZoomControls?.Visibility == Visibility.Collapsed, "the board replaces canvas, timeline and zoom");
                Require(Session.Draft == original.Draft && graphZoom == zoom, "the board switch preserves composer draft and diagram camera zoom");
                Require(structureColumns.Keys.SequenceEqual(["request", "execution", "result"]) && new[] { "request:", "execution:", "result:" }.All(part => structureCards.ContainsKey(part + latest.Id)) && structureCards.ContainsKey("agent:" + agentId),
                    "a free request's board holds its request, its run with the sub-agent under it, and its result");
                Require(structurePrevious is { IsEnabled: true } && structureNext is { IsEnabled: false } && structureBadge?.Visibility == Visibility.Collapsed && structurePosition?.Text == Locale.Get("graph.structure.board.request", new Dictionary<string, string> { ["n"] = "2", ["count"] = "2" }),
                    "on the newest of two requests the previous button is on, the next off, no badge, and the title says 2 / 2");
                // The previous button shows the request before, kept by its id.
                Invoke(structurePrevious!);
                await WaitUI(() => structureBoardId == old.Id && structureNavigation.Chosen == old.Id && structureCards.ContainsKey("request:" + old.Id) && !structureCards.ContainsKey("request:" + latest.Id) && structureBadge?.Visibility == Visibility.Collapsed,
                    "the previous button shows the request before, with no badge");
                // The title opens the list; an entry shows its board and closes the list.
                await OpenStructureList(2);
                Invoke(StructureEntry(latest.Id));
                await WaitUI(() => structureListFlyout?.IsOpen == false && structureBoardId == latest.Id && structureNavigation.Chosen is null, "a list entry shows its board and closes the list");
                // A new request while an older board is shown leaves it there and shows the badge, whose click shows the newest.
                Invoke(structurePrevious!);
                await WaitUI(() => structureBoardId == old.Id, "the previous button shows the older request again");
                var newer = new MightyGraphRun { Id = "timeline-smoke-newer", Input = "\uC0C8 \uC694\uCCAD", Status = "running" };
                await SetGraphRunsForSmoke([old, latest, newer]); RefreshMightyView(Session);
                await WaitUI(() => structureBoards.Count == 3 && structureBoardId == old.Id && structureBadge?.Visibility == Visibility.Visible && structureBadgeWords?.Text == Locale.Get("graph.structure.board.newRequest"),
                    "a new request leaves the older board shown and shows the badge");
                Invoke(structureBadge!);
                await WaitUI(() => structureBoardId == newer.Id && structureNavigation.Chosen is null && structureBadge?.Visibility == Visibility.Collapsed, "the badge shows the newest request");
                // The running request wears the diagram's running marks (M/MightyStructureView.swift:285, 619, 628): the capsules beside the
                // board's status and its 실행 card's, waved by the pane's one timer, and the border marching round that card in its fitted 9/7 dashes.
                AnimationsEnabledOverride = true; RefreshMightyView(Session);
                var running = "execution:" + newer.Id;
                await WaitUI(() => structureActivitySets.ContainsKey(StructureHeaderMarks) && structureActivitySets.ContainsKey(running) && graphActivityTimer?.IsEnabled == true
                    && structureOutlines.TryGetValue(running, out var edge) && edge.Kind == MightyGraphActivity.Marching && edge.March?.GetCurrentState() == Microsoft.UI.Xaml.Media.Animation.ClockState.Active
                    && edge.Line.StrokeDashArray?.ToArray() is [var dash, var gap] && Math.Abs(dash / gap - 9.0 / 7) < 1e-6,
                    "a running request's board shows the capsules beside its status and its run card's, and the marching border round that card");
                AnimationsEnabledOverride = null; RefreshMightyView(Session);
                // Each column scrolls on its own under a header that stays put, the page sideways only, and on the newest board each
                // column follows its own end (M/MightyStructureView.swift:426-530; the Mac's smoke moves a column by drag, trackpad
                // and wheel, M/MightyStructureInteractionDiagnostics.swift). Many sub-agents make the 실행 column taller than the pane.
                MightyGraphAgent Helper(int index) => new() { Id = "timeline-smoke-helper-" + index, Title = "\uD558\uC704 \uC5D0\uC774\uC804\uD2B8 " + index, Status = "completed",
                    Entries = [new("timeline-smoke-helper-answer-" + index, "assistant", "\uD558\uC704 \uACB0\uACFC " + index, Wire.Now())] };
                MightyGraphRun Busy(int helpers) => new() { Id = newer.Id, Input = newer.Input, Status = "running", Agents = [.. Enumerable.Range(0, helpers).Select(Helper)] };
                ScrollViewer ColumnScroll(string column) => structureColumns[column].Scroll;
                bool AtEnd(string column) => ColumnScroll(column) is var scroll && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 2;
                await SetGraphRunsForSmoke([old, latest, Busy(24)]); RefreshMightyView(Session);
                await WaitUI(() => structureBoardId == newer.Id && structureColumns.TryGetValue("execution", out var place) && place.Cards.Children.Count > 24 && place.Scroll.ScrollableHeight > 300
                    && structureFollow.Following("execution") && AtEnd("execution"), "the execution column taller than the pane follows its own end");
                Require(structureScroll is { VerticalScrollMode: ScrollMode.Disabled, ScrollableHeight: < 1 }, "the board's page scrolls sideways only");
                var others = new[] { "request", "result" }.ToDictionary(column => column, column => ColumnScroll(column).VerticalOffset);
                var executionColumn = structureColumns["execution"]; var header = executionColumn.Header; var firstCard = (FrameworkElement)executionColumn.Cards.Children[0];
                Windows.Foundation.Point Where(FrameworkElement part) => part.TransformToVisual(structureView!).TransformPoint(default);
                var headerAt = Where(header); var cardAt = Where(firstCard); var pageX = structureScroll!.HorizontalOffset; var start = executionColumn.Scroll.VerticalOffset;
                // A Page Up in the 실행 column, as its key would: the column alone moves up, away from its end, and stops following.
                NoteStructureInput(executionColumn, -1); executionColumn.Scroll.ChangeView(null, start - 200, null, true);
                await WaitUI(() => Math.Abs(executionColumn.Scroll.VerticalOffset - (start - 200)) < 2 && !structureFollow.Following("execution"), "a move up the execution column stops its follow");
                owner.root.UpdateLayout();
                Require(Math.Abs(Where(header).Y - headerAt.Y) < 0.5 && Math.Abs(Where(firstCard).Y - cardAt.Y - 200) < 2, "the column's header stays put while its cards move");
                Require(others.All(pair => Math.Abs(ColumnScroll(pair.Key).VerticalOffset - pair.Value) < 0.5) && Math.Abs(structureScroll.HorizontalOffset - pageX) < 0.5
                    && structureFollow.Following("request") && structureFollow.Following("result"), "the other columns and the page stay where they were, and keep following");
                // More sub-agents under 실행: the reader up the column is not pulled back.
                var reader = executionColumn.Scroll.VerticalOffset; var tall = executionColumn.Scroll.ScrollableHeight; var cards = executionColumn.Cards.Children.Count;
                await SetGraphRunsForSmoke([old, latest, Busy(28)]); RefreshMightyView(Session);
                await WaitUI(() => executionColumn.Cards.Children.Count == cards + 4 && executionColumn.Scroll.ScrollableHeight > tall + 10, "the execution column grows while its reader is up it");
                await Task.Delay(TimeSpan.FromSeconds(MightyStructurePan.ReleaseGrace + 0.1)); owner.root.UpdateLayout();
                Require(Math.Abs(executionColumn.Scroll.VerticalOffset - reader) < 2 && !structureFollow.Following("execution"), "a column its reader moved stays where they left it as it grows");
                // Back to the request with the sub-agent through the list, and its result's own button shows that block in the diagram.
                await OpenStructureList(3);
                Invoke(StructureEntry(latest.Id));
                await WaitUI(() => structureBoardId == latest.Id && StructureButtons("mighty-structure-reveal-" + resultId).Length == 1, "the list shows the request with the sub-agent again");
                // An older board opens with every column at its top, following nothing.
                await WaitUI(() => structureColumns.Values.All(column => column.Scroll.VerticalOffset < 1 && !structureFollow.Following(column.Id)), "the older board's columns open at their tops and follow nothing");
                Require(VisualChildren(structureView!).OfType<Border>().Any(card => AutomationProperties.GetAutomationId(card) == "mighty-structure-node-" + agentId) && StructureButtons("mighty-structure-reveal-" + requestId).Length == 1,
                    "its board shows the sub-agent's card and the request's reveal button");
                // The result's own button, so the selection the timeline row left on the request cannot pass for it.
                Require(graphSelection != resultId, "the result is not selected yet");
                Invoke(StructureButtons("mighty-structure-reveal-" + resultId).Single());
                await WaitUI(() => Session.GraphViewMode == "diagram" && graphViewport?.Visibility == Visibility.Visible && graphSelection == resultId, "a card's reveal button shows its own block in the diagram");
                var width = Container.Width; var horizontal = Container.HorizontalAlignment;
                try
                {
                    Container.Width = 320; Container.HorizontalAlignment = HorizontalAlignment.Left; owner.root.UpdateLayout(); await Task.Delay(80); owner.root.UpdateLayout();
                    // Design stage 4: the header stays one Layout.PaneHeader line at any width (the Mac's); a narrow pane
                    // shows the Default | Mighty switch as icons instead of moving it to a second row.
                    await WaitUI(() => HeaderFitsSmoke(true), "320px pane header controls must remain fully inside its one header line, the switch as icons only");
                    // The Mighty bar is one row on the Mac at any width (M/MightyGraphView.swift:175-201): a pane this narrow keeps
                    // it one row over its rule (the vertical inset, the Layout.HitTarget switch in its Spacing.Xxs track, the inset), by showing the Diagram | Timeline switch as symbols only.
                    var barHeight = 2 * DesignMetrics.Inset.GraphBarV + DesignMetrics.Layout.HitTarget + 2 * DesignMetrics.Spacing.Xxs + DesignMetrics.Stroke.Line;
                    await WaitUI(() => graphToolbar is { ActualHeight: var tall } && Math.Abs(tall - barHeight) < .5 && !ViewWordsShown, "320px pane keeps the Mighty bar one row, its view switch as symbols only");
                    foreach (var control in new FrameworkElement[] { diagramButton!, timelineButton!, structureButton!, zoomOutButton!, zoomResetButton!, zoomInButton! })
                    {
                        var edge = control.TransformToVisual(graphToolbar!).TransformPoint(new Windows.Foundation.Point(control.ActualWidth, 0));
                        Require(edge.X <= graphToolbar!.ActualWidth + 1, "narrow toolbar control remains inside pane: " + AutomationProperties.GetAutomationId(control));
                    }
                    await SettleDesktopCapture(Container);
                    captures.Add(await owner.CaptureSmoke(Path.Combine(owner.options.ProfileDirectory!, "smoke-mighty-toolbar-320.png")));
                }
                finally { Container.Width = width; Container.HorizontalAlignment = horizontal; owner.root.UpdateLayout(); }
                await WaitUI(() => paneHeader is { ActualWidth: >= NarrowHeader } && HeaderFitsSmoke(false), "Growing the pane must show the switch words again with every header control inside the line");
                await WaitUI(() => ViewWordsShown, "Growing the pane must show the Diagram | Timeline switch's words again");
                return new() { ["modePersists"] = true, ["groupAndRowExpansion"] = true, ["sameTranscriptActions"] = true, ["resultFilesOpenSharedPreview"] = true, ["unchangedRefreshKeepsControls"] = true, ["diagramAndDraftPreserved"] = true, ["structureBoardAndReveal"] = true, ["structureColumnScrollsOnItsOwn"] = true, ["structureListOpenedBy"] = string.Join(",", listOpenedBy), ["historyAffordance"] = true, ["narrowToolbar"] = true, ["screenshots"] = captures };
            }
            finally
            {
                AnimationsEnabledOverride = null; CloseReferencePreview();
                // The board the smoke left shown is not the pane's: the restored pane opens on its newest.
                structureNavigation.ShowNewest(structureBoards);
                await owner.Act(async () => { await Change(p => p with { GraphRuns = original.GraphRuns, AgentViewMode = original.AgentViewMode, GraphViewMode = original.GraphViewMode }); Refresh(); });
            }
        }
    }
}
