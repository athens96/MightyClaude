using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>
    /// Older requests of a Mighty diagram, read back from the CLI's own session
    /// record when the user scrolls past the top (macOS AppStore+GraphHistory and
    /// MightyGraphView.historyCard). The rules, the record reading and the replay
    /// live in Core (SessionHistory, SessionHistoryState); this file only runs
    /// the file work off the UI thread and draws the block at the top. Loaded
    /// requests are kept in memory only: never saved, dropped when the pane closes.
    /// </summary>
    private sealed partial class PaneView
    {
        private readonly SessionHistoryState graphHistory = new();
        private CancellationTokenSource? graphHistoryLoad;
        private List<MightyGraphRun> graphHistoryRetained = [];
        private bool graphHistoryAutoLoaded;
        private double? graphOriginY;
        /// The smoke reads its own fixture record instead of the user's.
        internal static string? HistoryHomeOverride;

        /// The loaded runs that still attach above the retained list. A trim moves
        /// the runs it drops into the history; history from another session is not shown.
        private List<MightyGraphRun> GraphHistoryRuns(RunSession pane, IReadOnlyList<MightyGraphRun> retained)
        {
            graphHistory.Follow(graphHistoryRetained, retained, pane.ResumeId, pane.Provider);
            graphHistoryRetained = [.. retained];
            return graphHistory.Connects(retained.FirstOrDefault()?.Id, pane.ResumeId) ? [.. graphHistory.Runs] : [];
        }

        /// A pane with no request and no session to read has no history block.
        private static bool ShowsHistoryBlock(RunSession pane, IReadOnlyList<MightyGraphRun> retained) =>
            SessionHistory.Providers.Contains(pane.Provider) && (retained.Count > 0 || pane.ResumeId is not null);

        private static bool LoadedFromRecord(string nodeId, IReadOnlyList<MightyGraphRun> older) =>
            older.Any(run => nodeId.StartsWith(MightyGraphBlockSize.NodeId(run.Id, ""), StringComparison.Ordinal));

        /// A pane that continues a session but has sent nothing yet loads its
        /// record's latest requests as soon as the diagram shows.
        private void AutoLoadGraphHistory(RunSession pane, IReadOnlyList<MightyGraphRun> retained)
        {
            if (graphHistoryAutoLoaded || retained.Count > 0 || pane.ResumeId is null || !SessionHistory.Providers.Contains(pane.Provider)) return;
            graphHistoryAutoLoaded = true;
            Container.DispatcherQueue.TryEnqueue(LoadOlderGraphHistory);
        }

        /// The user moved the diagram towards its top; once the history block
        /// shows, the next older requests load.
        private void LoadOlderAtTop()
        {
            if (graphLayout?.Nodes.FirstOrDefault(n => n.Kind == "history") is not { } history) return;
            if (MightyGraphCamera.ShowsTop(graphPan.Y - graphLayout.OriginY * graphZoom, graphZoom, history.Frame.Y)) LoadOlderGraphHistory();
        }

        /// The CLI home variables to read records under. A smoke's own history home
        /// (HistoryHomeOverride) stands alone: the runner's CLAUDE_CONFIG_DIR or CODEX_HOME
        /// must never send it to real records.
        internal static Dictionary<string, string> HistoryEnvironment()
        {
            var values = new Dictionary<string, string>();
            if (HistoryHomeOverride is not null) return values;
            foreach (var key in new[] { "CLAUDE_CONFIG_DIR", "CODEX_HOME" })
                if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } value) values[key] = value;
            return values;
        }

        /// Reads the next older chunk off the UI thread and puts its requests above
        /// the ones already shown. A load in flight, or a record whose start is on
        /// screen, does nothing.
        internal void LoadOlderGraphHistory()
        {
            if (!QueuePaneAlive || owner.service.Snapshot.Sessions.FirstOrDefault(p => p.Id == id) is not { } pane) return;
            if (pane.Kind != "claude" || !SessionHistory.Providers.Contains(pane.Provider)) return;
            var retained = pane.GraphRuns ?? [];
            var template = new SessionHistoryRequest(pane.Provider, pane.ResumeId ?? "", Workspace.Path)
            {
                Environment = HistoryEnvironment(),
                Home = HistoryHomeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            var request = graphHistory.Begin(retained.FirstOrDefault()?.Id, pane.ResumeId, SessionHistoryState.AnchorFor(pane), template);
            if (graphHost?.Visibility == Visibility.Visible) RefreshMightyView(pane);
            if (request is null) return;
            var generation = graphHistory.Generation;
            graphHistoryLoad?.Cancel();
            var cancel = graphHistoryLoad = new CancellationTokenSource();
            _ = Task.Run(() =>
            {
                SessionHistoryChunk? chunk = null; Exception? error = null;
                try { chunk = SessionHistory.Load(request, cancel.Token); }
                catch (Exception ex) { error = ex; }
                Container.DispatcherQueue.TryEnqueue(() =>
                {
                    if (cancel.IsCancellationRequested || !QueuePaneAlive || owner.service.Snapshot.Sessions.FirstOrDefault(p => p.Id == id) is not { } current) return;
                    if (ReferenceEquals(graphHistoryLoad, cancel)) graphHistoryLoad = null;
                    var restart = graphHistory.Finish(chunk, error, generation);
                    if (graphHost?.Visibility == Visibility.Visible) RefreshMightyView(current);
                    // The record was replaced under the cursor: start over from its end.
                    if (restart) LoadOlderGraphHistory();
                });
            });
        }

        /// Drops a closing pane's loaded history and stops a load still reading.
        internal void ForgetGraphHistory()
        {
            graphHistoryLoad?.Cancel(); graphHistoryLoad = null;
            graphHistory.Reset();
            // This hook runs only when the pane is removed from the snapshot.
            // Waiting for another event with that closed ID would retain all
            // cached native documents through the service's event delegate.
            owner.service.RunEventReceived -= OnGraphRunEvent;
            CancelResultReveal();
            foreach (var outline in graphOutlineViews.Values) outline.Stop?.Invoke();
            graphDotGrid?.Close(); graphDotGrid = null;
            graphActivityTimer?.Stop(); graphActivitySets.Clear();
            timelineHeads.Clear(); timelineRows.Clear(); timelineResults.Clear();
        }

        /// <summary>
        /// The top of the diagram: loads the previous requests from the session record, shows that it
        /// is doing so, or that the record begins here. A capsule on <c>card</c> with a 1pt <c>line</c>
        /// edge in [4, 3] pt dashes, its words 11pt <c>ink2</c> and the load link <c>accent</c>
        /// (M/MightyGraphView.swift:594-632).
        /// </summary>
        private FrameworkElement BuildHistoryCard(RunSession pane, IReadOnlyList<MightyGraphRun> retained, int loaded, GraphRect frame)
        {
            var b = owner.brushes;
            var state = graphHistory.Connects(retained.FirstOrDefault()?.Id, pane.ResumeId) ? graphHistory : new SessionHistoryState();
            var text = state.BlockText(loaded);
            var label = new TextBlock { Text = text, FontSize = DesignMetrics.Type.Pill, Foreground = b.Brush(DesignToken.Ink2), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var card = new Grid { Width = frame.W, Height = frame.H };
            var dashes = new DoubleCollection();
            foreach (var length in DesignMetrics.Dash.InStrokeUnits([4, 3], DesignMetrics.Stroke.Line)) dashes.Add(length);
            card.Children.Add(new Rectangle
            {
                RadiusX = frame.H / 2, RadiusY = frame.H / 2, StrokeThickness = DesignMetrics.Stroke.Line, StrokeDashArray = dashes,
                Stroke = b.Brush(DesignToken.Line), Fill = b.Brush(DesignToken.Card),
            });
            if (state.BlockActs)
            {
                var button = new HyperlinkButton { Content = label, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 2, 8, 2) };
                label.Foreground = b.Brush(DesignToken.Accent);
                AutomationProperties.SetAutomationId(button, (state.Phase == SessionHistoryState.Phases.Failed ? "mighty-history-retry-" : "mighty-history-load-") + id);
                AutomationProperties.SetName(button, text);
                button.Click += (_, _) => LoadOlderGraphHistory();
                card.Children.Add(button);
            }
            else card.Children.Add(label);
            ToolTipService.SetToolTip(card, Locale.Get("graph.history.help"));
            AutomationProperties.SetAutomationId(card, "mighty-node-" + MightyGraphLayout.HistoryNodeID + "-" + id);
            AutomationProperties.SetName(card, text);
            // A press here is not the start of a background drag.
            card.PointerPressed += (_, args) => args.Handled = true;
            return card;
        }

        // ── smoke accessors ───────────────────────────────────────────────────

        internal SessionHistoryState GraphHistoryForSmoke => graphHistory;
        internal string? GraphHistoryTextForSmoke() =>
            graphCanvas.Children.OfType<Grid>().Where(g => AutomationProperties.GetAutomationId(g).StartsWith("mighty-node-" + MightyGraphLayout.HistoryNodeID, StringComparison.Ordinal))
                .Select(AutomationProperties.GetName).FirstOrDefault();
        internal double GraphOriginYForSmoke => graphLayout?.OriginY ?? 0;
        internal (double X, double Y)? GraphCardPositionForSmoke(string nodeId) =>
            graphCards.TryGetValue(nodeId, out var card) ? (Canvas.GetLeft(card) * graphZoom + graphPan.X, Canvas.GetTop(card) * graphZoom + graphPan.Y) : null;
    }
}
