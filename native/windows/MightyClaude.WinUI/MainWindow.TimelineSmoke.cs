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
                Require(rows.Length == 2 && timelineHistoryCard is not null, "only newest group opens initially; same history affordance remains");
                var resultId = MightyGraphLayout.NodeID(latest, "result"); var requestId = MightyGraphLayout.NodeID(latest, "request");
                Invoke(FindButton("mighty-timeline-row-" + requestId));
                await WaitUI(() => timelineOpen.Contains(requestId) && timelineTranscripts.ContainsKey(requestId));
                Require(timelineTranscripts[requestId].OpenReference is not null && timelineTranscripts[requestId].OpenImage is not null, "expanded timeline detail shares transcript actions");
                var unchanged = timelineGroups[latest.Id].View; RefreshMightyView(Session);
                Require(ReferenceEquals(unchanged, timelineGroups[latest.Id].View), "identical refresh preserves native selection and focus");
                Invoke(FindButton("mighty-timeline-result-more-" + resultId));
                await WaitUI(() => timelineFull.Contains(resultId));
                Require(double.IsPositiveInfinity(timelineTranscripts[resultId].View.MaxHeight), "show-all removes preview height cap");
                var files = FindButton("mighty-timeline-result-files-" + resultId);
                await WaitUI(() => files.IsLoaded && files.ActualWidth > 0 && files.ActualHeight > 0);
                files.Flyout!.ShowAt(files);
                var item = ((MenuFlyout)files.Flyout).Items.OfType<MenuFlyoutItem>().Single();
                await WaitUI(() => item.IsLoaded && item.ActualWidth > 0 && item.ActualHeight > 0);
                var itemPeer = new MenuFlyoutItemAutomationPeer(item);
                ((IInvokeProvider)itemPeer.GetPattern(PatternInterface.Invoke)).Invoke();
                await WaitUI(() => referenceTarget?.RelativePath == "docs/mighty-note.md" && referenceBody?.Child is not ProgressRing);
                Require(referencePanel?.Visibility == Visibility.Visible, "timeline result file opens the same inline reference bubble");
                files.Flyout.Hide(); CloseReferencePreview();
                captures.Add(await owner.CaptureSmoke(Path.Combine(owner.options.ProfileDirectory!, "smoke-mighty-timeline.png")));
                Invoke(FindButton("mighty-timeline-request-" + old.Id));
                await WaitUI(() => VisualChildren(timelineScroll!).OfType<Button>().Count(b => AutomationProperties.GetAutomationId(b).StartsWith("mighty-timeline-row-", StringComparison.Ordinal)) == 3);
                Invoke(diagramButton!);
                await WaitUI(() => Session.GraphViewMode == "diagram" && graphViewport?.Visibility == Visibility.Visible);
                Require(graphZoom == zoom && Session.Draft == original.Draft && ReadGraphForSmoke().Blocks >= 5, "returning to diagram retains blocks, zoom and draft");
                var width = Container.Width; var horizontal = Container.HorizontalAlignment;
                try
                {
                    Container.Width = 320; Container.HorizontalAlignment = HorizontalAlignment.Left; owner.root.UpdateLayout(); await Task.Delay(80); owner.root.UpdateLayout();
                    // Design stage 4: the header stays one 34pt line at any width (the Mac's); a narrow pane
                    // shows the Default | Mighty switch as icons instead of moving it to a second row.
                    await WaitUI(() => HeaderFitsSmoke(true), "320px pane header controls must remain fully inside its one 34pt line, the switch as icons only");
                    Require(graphToolbar is { ActualHeight: > 45 }, "320px pane moves toolbar controls below summary");
                    foreach (var control in new FrameworkElement[] { diagramButton!, timelineButton!, zoomOutButton!, zoomResetButton!, zoomInButton! })
                    {
                        var edge = control.TransformToVisual(graphToolbar!).TransformPoint(new Windows.Foundation.Point(control.ActualWidth, 0));
                        Require(edge.X <= graphToolbar!.ActualWidth + 1, "narrow toolbar control remains inside pane: " + AutomationProperties.GetAutomationId(control));
                    }
                    await SettleDesktopCapture(Container);
                    captures.Add(await owner.CaptureSmoke(Path.Combine(owner.options.ProfileDirectory!, "smoke-mighty-toolbar-320.png")));
                }
                finally { Container.Width = width; Container.HorizontalAlignment = horizontal; owner.root.UpdateLayout(); }
                await WaitUI(() => paneHeader is { ActualWidth: >= NarrowHeader } && HeaderFitsSmoke(false), "Growing the pane must show the switch words again with every header control inside the line");
                return new() { ["modePersists"] = true, ["groupAndRowExpansion"] = true, ["sameTranscriptActions"] = true, ["resultFilesOpenSharedPreview"] = true, ["unchangedRefreshKeepsControls"] = true, ["diagramAndDraftPreserved"] = true, ["historyAffordance"] = true, ["narrowToolbar"] = true, ["screenshots"] = captures };
            }
            finally
            {
                CloseReferencePreview();
                await owner.Act(async () => { await Change(p => p with { GraphRuns = original.GraphRuns, AgentViewMode = original.AgentViewMode, GraphViewMode = original.GraphViewMode }); Refresh(); });
            }
        }
    }
}
