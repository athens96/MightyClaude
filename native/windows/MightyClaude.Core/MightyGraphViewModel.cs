namespace MightyClaude.Core;

/// <summary>
/// View-model decisions for the per-pane Mighty graph view on Windows.
/// WinUI binds to this; no layout or tracker logic is duplicated here.
/// All decisions mirror the macOS MightyGraphView / MightyGraphInteraction behaviour.
/// </summary>
public static class MightyGraphViewModel
{
    // ── zoom ─────────────────────────────────────────────────────────────────

    public const double ZoomMin = 0.5;
    public const double ZoomMax = 1.5;
    public const double ZoomStep = 0.1;
    public const double ZoomDefault = 1.0;

    /// <summary>All discrete zoom levels from min to max in ZoomStep increments.</summary>
    public static double[] ZoomLevels()
    {
        var levels = new List<double>();
        for (var z = ZoomMin; z <= ZoomMax + 0.0001; z += ZoomStep)
            levels.Add(Math.Round(z, 1));
        return levels.ToArray();
    }

    /// <summary>Zoom in one step, clamped to ZoomMax.</summary>
    public static double ZoomIn(double current) =>
        Math.Min(ZoomMax, Math.Round(current + ZoomStep, 1));

    /// <summary>Zoom out one step, clamped to ZoomMin.</summary>
    public static double ZoomOut(double current) =>
        Math.Max(ZoomMin, Math.Round(current - ZoomStep, 1));

    /// <summary>True when zoom-in should be disabled (already at max).</summary>
    public static bool ZoomInDisabled(double zoom) => zoom >= ZoomMax;

    /// <summary>True when zoom-out should be disabled (already at min).</summary>
    public static bool ZoomOutDisabled(double zoom) => zoom <= ZoomMin;

    /// <summary>
    /// The pan after the zoom went from <paramref name="previous"/> to <paramref name="zoom"/>: what stood in the middle of
    /// the viewport stays there (macOS MightyGraphCanvas <c>onChange(of: zoom)</c>). A broken zoom leaves the pan as it is.
    /// </summary>
    public static (double X, double Y) ZoomAnchored((double X, double Y) pan, (double W, double H) viewport, double previous, double zoom)
    {
        if (!double.IsFinite(previous) || previous <= 0 || !double.IsFinite(zoom) || zoom <= 0) return pan;
        var (centreX, centreY) = (viewport.W / 2, viewport.H / 2);
        return (centreX - (centreX - pan.X) * zoom / previous, centreY - (centreY - pan.Y) * zoom / previous);
    }

    /// <summary>Formatted zoom label for the percent button, e.g. "100%".</summary>
    public static string ZoomLabel(double zoom) => ((int)Math.Round(zoom * 100)) + "%";

    // ── pane eligibility ──────────────────────────────────────────────────────

    /// <summary>
    /// True when the pane header should show the Default / Mighty mode switch.
    /// Matches macOS: kind == "claude" and provider is in Providers.
    /// </summary>
    public static bool ShowsModeSwitch(RunSession session) =>
        session.Kind == "claude" && MightyGraphSupport.Providers.Contains(session.Provider);

    // ── mode switch ───────────────────────────────────────────────────────────

    /// <summary>
    /// Applies a view-mode switch to the session. Switching never restarts the
    /// run, never clears the composer draft, and never changes GraphRuns.
    /// </summary>
    public static RunSession ApplyViewMode(RunSession session, string mode)
    {
        if (mode is not ("default" or "mighty")) return session;
        return session with { AgentViewMode = mode };
    }

    // ── canvas blocks ─────────────────────────────────────────────────────────

    /// <summary>
    /// The canonical block list for the canvas is produced by MightyGraphLayout.Make
    /// over the pane's GraphRuns plus draft and running state.
    /// </summary>
    public static MightyGraphLayout CanvasLayout(
        IReadOnlyList<MightyGraphRun> graphRuns,
        string draft,
        bool running,
        IReadOnlySet<string> expanded,
        string? resultFilesRunID = null,
        (double W, double H)? viewport = null,
        double? zoom = null,
        GraphBlockSize? sharedResultSize = null,
        int retainedStart = 0,
        bool history = false,
        double? resultContentHeight = null,
        IReadOnlyDictionary<string, GraphBlockSize>? blockSizes = null,
        string? planRunID = null,
        IReadOnlyList<(string RunID, string RecordID)>? planRecords = null,
        GraphBlockSize? planSize = null,
        bool splitsExecution = false,
        IReadOnlyList<(string RunID, string Step, int Count)>? galleries = null,
        IReadOnlyList<(string RunID, string Key)>? executions = null)
        => MightyGraphLayout.Make(graphRuns, draft, running, expanded, resultFilesRunID, viewport, zoom: zoom, sharedResultSize: sharedResultSize, retainedStart: retainedStart, history: history, resultContentHeight: resultContentHeight, blockSizes: blockSizes, planRunID: planRunID, planRecords: planRecords, planSize: planSize, splitsExecution: splitsExecution, galleries: galleries, executions: executions);

    // ── block capsule ─────────────────────────────────────────────────────────

    /// <summary>Token + model capsule text for a block header.</summary>
    public static string? BlockCapsule(GraphTokenUsage? usage, IReadOnlyList<GraphResponseRecord> records,
        string? nodeModelLabel, IReadOnlyList<ModelOption>? catalog = null)
        => ModelUsageFormat.BlockCapsule(usage, records, nodeModelLabel, catalog);

    /// <summary>Tooltip text for the capsule (one line per model).</summary>
    public static string BlockCapsuleHelp(IReadOnlyList<GraphResponseRecord> records,
        IReadOnlyList<ModelOption>? catalog = null)
        => ModelUsageFormat.BlockCapsuleHelp(records, catalog);

    // ── selection and wheel routing ───────────────────────────────────────────

    /// <summary>
    /// Given a click target node ID and the current selection, returns the new
    /// selection state. Clicking a selected block keeps it selected (the caller
    /// routes the wheel to the block body). Clicking a different block selects it.
    /// Clicking null (empty canvas background) clears the selection.
    /// </summary>
    public static string? ApplySelection(string? currentSelection, string? clickedNodeID)
        => clickedNodeID;

    /// <summary>
    /// True when the mouse wheel (or Escape) should scroll the selected block's
    /// body rather than panning the canvas. Matches macOS interaction.
    /// </summary>
    public static bool WheelScrollsBlock(string? selectedNodeID) => selectedNodeID is not null;

    /// <summary>
    /// The block whose body a wheel turn scrolls, or null when it pans the canvas (macOS MightyGraphInteraction
    /// <c>handle(.scrollWheel)</c>): the block under the pointer, when it is the selected one or an attachment beside
    /// the flow, which scrolls at once. The selected block does not take the wheel while the pointer is elsewhere.
    /// </summary>
    public static string? WheelScrollTarget(string? selectedNodeID, string? hoveredNodeID, bool hoveredIsAuxiliary) =>
        hoveredNodeID is not null && (hoveredNodeID == selectedNodeID || hoveredIsAuxiliary) ? hoveredNodeID : null;

    /// <summary>
    /// How far one wheel event pans the canvas camera. A vertical wheel moves
    /// it up and down; a horizontal wheel or tilt, and Shift with the vertical
    /// wheel (the Windows convention), move it left and right — the macOS
    /// wheel/trackpad pan in both directions. A tilt to the right (positive
    /// delta) and Shift with the wheel turned down (negative delta) both bring
    /// content from the right into view.
    /// </summary>
    public static (double X, double Y) WheelPan(double delta, bool horizontalWheel, bool shift)
    {
        if (horizontalWheel) return (-delta, 0);
        if (shift) return (delta, 0);
        return (0, delta);
    }

    // ── result files panel ────────────────────────────────────────────────────

    /// <summary>
    /// Which result's file list is open beside its card (macOS MightyGraphResultFilesModel). Every result card
    /// carries its own request's files and opens its own list; one list is open per graph. The newest result
    /// that names files opens by itself, once: closed or changed by hand it stays as the user left it until a
    /// newer result names files.
    /// </summary>
    public sealed class ResultFilesPanel
    {
        public string? SelectedRunID { get; private set; }
        private string? lastAutoRunID;

        /// <summary>The toggle in a result card's header; a result that names no file has none.</summary>
        public void Toggle(string runID, bool hasFiles)
        {
            if (hasFiles) SelectedRunID = SelectedRunID == runID ? null : runID;
        }

        public void Close() => SelectedRunID = null;

        /// <summary>
        /// The lists as resolved for this pass: the pane's own completed requests in order, each with whether
        /// its result names files. An open list whose result no longer names any closes.
        /// </summary>
        public void Publish(IReadOnlyList<(string RunID, bool HasFiles)> sources)
        {
            if (SelectedRunID is { } selected && !sources.Any(source => source.RunID == selected && source.HasFiles)) SelectedRunID = null;
            for (var index = sources.Count - 1; index >= 0; index--)
            {
                if (!sources[index].HasFiles) continue;
                if (sources[index].RunID != lastAutoRunID) SelectedRunID = lastAutoRunID = sources[index].RunID;
                return;
            }
        }
    }
    // ── running indicators ────────────────────────────────────────────────────

    /// <summary>
    /// Indicator style for a block given its status and the system animations setting.
    /// Matches macOS MightyGraphActivityView / reduced-motion behaviour:
    /// - running + animations on → "animating" (moving bar + flowing border)
    /// - running + animations off → "static" (highlighted border, no motion)
    /// - waiting → "waiting" (static pause mark)
    /// - everything else → "none"
    /// </summary>
    public static string BlockIndicator(string blockStatus, bool animationsEnabled) => blockStatus switch
    {
        "running" => animationsEnabled ? "animating" : "static",
        "waiting" => "waiting",
        _ => "none",
    };

    // ── locale keys (used by WinUI to look up the right strings) ─────────────

    public static string LocaleKeyDefault => "graph.view.default";
    public static string LocaleKeyMighty => "graph.view.mighty";
    public static string LocaleKeyZoomOut => "graph.zoom.out";
    public static string LocaleKeyZoomReset => "graph.zoom.reset";
    public static string LocaleKeyZoomIn => "graph.zoom.in";
    public static string LocaleKeyResultFilesTitle => "graph.resultFiles.title";
    public static string LocaleKeyResultFilesClose => "graph.resultFiles.closeButton";
    public static string LocaleKeyResultFilesOpen => "graph.resultFiles.openButton";
    public static string LocaleKeyResultFilesCount => "graph.resultFiles.countLabel";
    public static string LocaleKeyBlockScrolling => "graph.block.scrolling";
    public static string LocaleKeyResultFitToWindow => "graph.result.fitToWindow";
}

/// <summary>
/// One canvas block as the Mighty view draws it: the layout node plus the copy,
/// state, request and output text, the usage capsule and its tooltip, the
/// running/waiting indicator choice, the card's activity outline
/// (<see cref="MightyGraphActivity"/>) and the status its colours follow (empty for the
/// draft, the files panel and a request over its 실행 block). <c>Awaiting</c>: a result whose request awaits the notices of background work it
/// started (<see cref="BackgroundNoticeRuns.AwaitingNotice"/>): the 알림 대기 block, waiting in status with a marching outline and an hourglass,
/// its work's rows and progress under the answer so far, as on the Mac. WinUI renders these and decides nothing.
/// </summary>
public sealed record MightyGraphBlock(
    string Id,
    string Kind,
    GraphRect Frame,
    string Title,
    string State,
    string Request,
    IReadOnlyList<LogEntry> Entries,
    string? Capsule,
    string CapsuleHelp,
    string Indicator,
    string? ResultFilesRunId,
    string Outline = MightyGraphActivity.None,
    string Status = "",
    string? RecordId = null,
    BackgroundNoticeRuns.Awaiting? Awaiting = null);

public static class MightyGraphBlockModel
{
    // ── state and title copy ──────────────────────────────────────────────────

    /// <summary>The short state word under a block header (macOS statusLabel).</summary>
    public static string StateLabel(string status) => status switch
    {
        "completed" => Locale.Get("graph.state.completed"),
        "error" or "failed" => Locale.Get("graph.state.error"),
        "stopped" or "cancelled" or "interrupted" => Locale.Get("graph.state.stopped"),
        "waiting" => Locale.Get("graph.state.waiting"),
        "starting" or "queued" => Locale.Get("graph.state.starting"),
        _ => Locale.Get("graph.state.running"),
    };

    /// <summary>A request's status word; one whose turn ended while its background tasks run says it waits on them (macOS MightyGraphView.runStatusLabel).</summary>
    public static string RunStateLabel(MightyGraphRun run) => MightyGraphSupport.BackgroundWaiting(run) is var count and > 0
        ? Locale.Get("plan.background.status", new Dictionary<string, string> { ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture) }) : StateLabel(run.Status);

    /// <summary>The 실행 block's title (`graph.block.executionOf`, "실행 · 요청 N"): it names its request, so each one reads apart from the others.</summary>
    public static string ExecutionTitle(int ordinal) =>
        Locale.Get("graph.block.executionOf", new Dictionary<string, string> { ["n"] = ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    /// <summary>The app-owned request title tail (`graph.block.requestTitle`, "Request N · Claude"); guided views may prepend an approved style title.</summary>
    public static string RequestTitle(int ordinal, string providerLabel) =>
        Locale.Get("graph.block.requestTitle", new Dictionary<string, string> { ["ordinal"] = ordinal.ToString(), ["provider"] = providerLabel });

    /// <summary>
    /// The provider whose mark goes before the name a block's title ends with, or null: only a
    /// request block's title (<c>Request N · Claude</c>) names its agent (macOS MightyGraphView
    /// <c>transcriptCard(titleProvider:)</c>). Sub-agent, result, draft and file blocks carry none,
    /// and neither does a provider the app has no mark for.
    /// </summary>
    public static string? TitleProvider(MightyGraphBlock block, string paneProvider) =>
        block.Kind == "request" && ProviderMark.SplitTrailingLabel(block.Title, paneProvider) is not null ? ProviderMark.MarkedProvider(paneProvider) : null;

    /// <summary>
    /// The tone a block's status pill, result strip and incoming edge take (macOS
    /// <c>DesignTone(blockStatus:)</c>): anything unfinished that is not waiting runs. A block with
    /// no status (the draft, the files panel, a request over its 실행 block) is idle.
    /// </summary>
    public static DesignTone Tone(MightyGraphBlock block) =>
        block.Status.Length == 0 ? DesignTone.Idle : StatusGlyph.Tone(MightyTimeline.Status(block.Status));

    /// <summary>The result card's title in every view and on the phone: how the request ended, or 알림 대기 중 while it awaits the notices of
    /// background work it started (<paramref name="awaiting"/>, <see cref="BackgroundNoticeRuns.AwaitingNotice"/>) (macOS MightyGraphSupport.resultTitle).</summary>
    public static string ResultTitle(string runStatus, bool awaiting = false) => runStatus switch
    {
        "error" or "failed" => Locale.Get("graph.block.resultError"),
        "stopped" or "cancelled" or "interrupted" => Locale.Get("graph.block.resultStopped"),
        _ when awaiting => Locale.Get("graph.block.awaitingNotice"),
        _ => Locale.Get("graph.block.result"),
    };

    /// <summary>The result block's own state, normalised the way macOS does.</summary>
    public static string ResultState(string runStatus) => runStatus switch
    {
        "error" or "failed" => "error",
        "stopped" or "cancelled" or "interrupted" => "stopped",
        _ => "completed",
    };

    public static string DraftTitle(bool anyRuns) =>
        anyRuns ? Locale.Get("graph.block.nextRequest") : Locale.Get("graph.block.firstRequest");

    public static string DraftState(string draft) =>
        draft.Length == 0 ? Locale.Get("graph.state.draftIdle") : Locale.Get("graph.state.draftTyping");

    // ── the block list ────────────────────────────────────────────────────────

    /// <summary>
    /// Every canvas block, in layout order, for one pane. The layout is the one
    /// MightyGraphLayout.Make produced — this only attaches the copy WinUI draws.
    /// </summary>
    public static List<MightyGraphBlock> Blocks(
        MightyGraphLayout layout,
        IReadOnlyList<MightyGraphRun> runs,
        string draft,
        string providerLabel,
        bool animationsEnabled,
        IReadOnlyList<ModelOption>? catalog = null)
    {
        var blocks = new List<MightyGraphBlock>();
        // Laid out with its 실행 blocks (MightyGraphLayout.Make splitsExecution), a request block holds what was asked alone.
        var split = layout.Nodes.Any(n => n.Kind == MightyGraphLayout.WorkKind);
        // Request numbers: a notice run (BackgroundNoticeRuns) takes none and is titled after the work it reports on.
        var ordinals = MightyGraphSupport.RequestOrdinals(runs);
        string Title(MightyGraphRun run, int runIndex) => run.NoticeRun == true ? BackgroundNoticeRuns.Heading(run) : RequestTitle(ordinals[runIndex], providerLabel);
        foreach (var node in layout.Nodes)
        {
            var (runIndex, agentId) = Locate(node, runs);
            var run = runIndex >= 0 ? runs[runIndex] : null;
            switch (node.Kind)
            {
                case "draft":
                    blocks.Add(new(node.Id, "draft", node.Frame, DraftTitle(runs.Count > 0), DraftState(draft),
                        draft, [], null, "", "none", null));
                    break;
                // The request alone, with no status of its own: the 실행 block under it runs and settles (macOS transcriptCard inputOnly).
                case "request" when run is not null && split:
                    blocks.Add(new(node.Id, "request", node.Frame, Title(run, runIndex), "", run.Input, [], null, "", "none", null));
                    break;
                // The main agent's own work for the request: its replies, tool calls, status and tokens.
                case MightyGraphLayout.WorkKind when run is not null:
                    blocks.Add(new(node.Id, MightyGraphLayout.WorkKind, node.Frame, BackgroundNoticeRuns.ExecutionTitle(run.NoticeRun == true, ordinals[runIndex]),
                        RunStateLabel(run), "",
                        run.RootEntries.Where(e => e.Kind != "user").ToList(),
                        ModelUsageFormat.BlockCapsule(run.Usage, run.ResponseRecords ?? [], run.NodeModelLabel, catalog, versioned: true),
                        Help(run.Usage, run.ResponseRecords ?? [], Locale.Get("graph.block.blockUsageLabel"), catalog),
                        MightyGraphViewModel.BlockIndicator(run.Status, animationsEnabled), null,
                        MightyGraphActivity.Outline(run.Status, animationsEnabled), run.Status));
                    break;
                case "request" when run is not null:
                    blocks.Add(new(node.Id, "request", node.Frame, Title(run, runIndex),
                        RunStateLabel(run), run.Input,
                        run.RootEntries.Where(e => e.Kind != "user").ToList(),
                        ModelUsageFormat.BlockCapsule(run.Usage, run.ResponseRecords ?? [], run.NodeModelLabel, catalog, versioned: true),
                        Help(run.Usage, run.ResponseRecords ?? [], Locale.Get("graph.block.blockUsageLabel"), catalog),
                        MightyGraphViewModel.BlockIndicator(run.Status, animationsEnabled), null,
                        MightyGraphActivity.Outline(run.Status, animationsEnabled), run.Status));
                    break;
                case "agent" when run is not null && run.Agents.FirstOrDefault(a => a.Id == agentId) is { } agent:
                    blocks.Add(new(node.Id, MightyGraphSupport.BlockKind(agent), node.Frame, MightyGraphSupport.BlockTitle(agent),
                        StateLabel(agent.Status), MightyGraphSupport.ShownInput(agent),
                        agent.Entries.Where(e => e.Kind != "user").ToList(),
                        ModelUsageFormat.BlockCapsule(agent.Usage, agent.ResponseRecords ?? [], null, catalog, versioned: true),
                        Help(agent.Usage, agent.ResponseRecords ?? [], Locale.Get("graph.block.blockUsageLabel"), catalog),
                        MightyGraphViewModel.BlockIndicator(agent.Status, animationsEnabled), null,
                        MightyGraphActivity.Outline(agent.Status, animationsEnabled), agent.Status));
                    break;
                case "result" when run is not null:
                    // While the request awaits the notices of background work it started, its result is the 알림 대기 block: the wait tone,
                    // a marching outline, and the work's rows under the answer so far (macOS MightyGraphView awaitingNoticeCard).
                    var awaiting = BackgroundNoticeRuns.AwaitingNotice(run, runs);
                    blocks.Add(new(node.Id, "result", node.Frame, ResultTitle(run.Status, awaiting is not null),
                        awaiting is null ? StateLabel(ResultState(run.Status)) : Locale.Get("graph.state.awaitingNotice"), "", run.ResultEntries.Where(e => e.Kind != "user").ToList(),
                        ModelUsageFormat.BlockCapsule(run.TotalUsage, [], null, catalog, versioned: true),
                        Help(run.TotalUsage, [], Locale.Get("graph.block.totalUsageLabel"), catalog),
                        "none", run.Status == "completed" ? run.Id : null,
                        awaiting is null ? MightyGraphActivity.None : MightyGraphActivity.Outline("running", animationsEnabled), Status: awaiting is null ? ResultState(run.Status) : "waiting",
                        Awaiting: awaiting));
                    break;
                case "plan" when run is not null:
                    blocks.Add(new(node.Id, "plan", node.Frame, Locale.Get("plan.card.title"), StateLabel("waiting"), "", [], null, "", "none", null, Status: "waiting"));
                    break;
                case "planRecord" when run is not null && agentId is not null:
                    blocks.Add(new(node.Id, "planRecord", node.Frame, Locale.Get("plan.card.title"), "", "", [], null, "", "none", null, RecordId: agentId));
                    break;
                case "resultFiles" when run is not null:
                    blocks.Add(new(node.Id, "resultFiles", node.Frame, Locale.Get("graph.resultFiles.title"),
                        "", "", [], null, "", "none", run.Id));
                    break;
                // A step's picture preview and a background execution's block: the step, or the execution's key, in the record slot.
                case MightyGraphLayout.ImagesKind when run is not null && agentId is not null:
                    blocks.Add(new(node.Id, MightyGraphLayout.ImagesKind, node.Frame, Locale.Get("graph.images.title"), "", "", [], null, "", "none", null, RecordId: agentId));
                    break;
                case MightyGraphLayout.ExecutionKind when run is not null && agentId is not null:
                    blocks.Add(new(node.Id, MightyGraphLayout.ExecutionKind, node.Frame, Locale.Get("graph.ouroboros.title"), "", "", [], null, "", "none", null, RecordId: agentId));
                    break;
            }
        }
        return blocks;
    }

    private static string Help(GraphTokenUsage? usage, IReadOnlyList<GraphResponseRecord> records, string usageLabel, IReadOnlyList<ModelOption>? catalog)
        => records.Count == 0
            ? usage is null ? "" : usageLabel + " · " + usage.Detail
            : ModelUsageFormat.BlockCapsuleHelp(records, catalog, versioned: true);

    /// <summary>Which run (and, for an agent node, which agent) a layout node belongs to.</summary>
    private static (int RunIndex, string? AgentId) Locate(MightyGraphLayout.Node node, IReadOnlyList<MightyGraphRun> runs)
    {
        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            if (node.Id == MightyGraphLayout.NodeID(run, "request") && node.Kind == "request") return (i, null);
            if (node.Id == MightyGraphLayout.NodeID(run, MightyGraphLayout.WorkSuffix) && node.Kind == MightyGraphLayout.WorkKind) return (i, null);
            if (node.Id == MightyGraphLayout.NodeID(run, "result") && node.Kind == "result") return (i, null);
            if (node.Id == MightyGraphLayout.NodeID(run, "result-files") && node.Kind == "resultFiles") return (i, null);
            if (node.Id == MightyGraphLayout.NodeID(run, MightyGraphLayout.PlanSuffix) && node.Kind == "plan") return (i, null);
            // An answered plan's block: its record id in the agent slot.
            if (node.Kind == "planRecord" && node.Id.StartsWith(MightyGraphLayout.NodeID(run, MightyGraphLayout.PlanRecordSuffix), StringComparison.Ordinal))
                return (i, node.Id[MightyGraphLayout.NodeID(run, MightyGraphLayout.PlanRecordSuffix).Length..]);
            if (node.Kind == MightyGraphLayout.ImagesKind && node.Id.StartsWith(MightyGraphLayout.NodeID(run, MightyGraphLayout.ImagesSuffix), StringComparison.Ordinal))
                return (i, node.Id[MightyGraphLayout.NodeID(run, MightyGraphLayout.ImagesSuffix).Length..]);
            if (node.Kind == MightyGraphLayout.ExecutionKind && node.Id.StartsWith(MightyGraphLayout.NodeID(run, MightyGraphLayout.ExecutionSuffix), StringComparison.Ordinal))
                return (i, node.Id[MightyGraphLayout.NodeID(run, MightyGraphLayout.ExecutionSuffix).Length..]);
            if (node.Kind != "agent") continue;
            foreach (var agent in run.Agents)
                if (node.Id == MightyGraphLayout.NodeID(run, "agent:" + agent.Id)) return (i, agent.Id);
        }
        return (-1, null);
    }

    // ── toolbar ───────────────────────────────────────────────────────────────

    /// <summary>The pane total line macOS draws in the graph toolbar.</summary>
    public static string ToolbarSummary(IReadOnlyList<MightyGraphRun> runs)
    {
        static Dictionary<string, string> N(int n) => new() { ["n"] = n.ToString() };
        // Only steers a person sent are counted; a delegation or task notice is not.
        int Kind(string kind) => runs.Sum(r => r.Agents.Count(a => MightyGraphSupport.BlockKind(a) == kind && !MightyGraphSupport.IsNotice(a)));
        var parts = new List<string>
        {
            Locale.Get("graph.header.requests", N(runs.Count)),
            Locale.Get("graph.header.agents", N(Kind("agent"))),
        };
        if (Kind("question") > 0) parts.Add(Locale.Get("graph.header.questions", N(Kind("question"))));
        if (Kind("task") > 0) parts.Add(Locale.Get("graph.header.tasks", N(Kind("task"))));
        if (Kind("steer") > 0) parts.Add(Locale.Get("graph.header.steers", N(Kind("steer"))));
        if (Kind("compact") > 0) parts.Add(Locale.Get("graph.header.compactions", N(Kind("compact"))));
        var tokens = Total(runs);
        if (!tokens.IsEmpty) parts.Add(tokens.Summary);
        return string.Join(" · ", parts);
    }

    /// <summary>The tooltip on the toolbar total; empty when nothing was counted.</summary>
    public static string ToolbarHelp(IReadOnlyList<MightyGraphRun> runs)
    {
        var tokens = Total(runs);
        return tokens.IsEmpty ? "" : Locale.Get("graph.header.totalHelp", new Dictionary<string, string> { ["detail"] = tokens.Detail });
    }

    private static GraphTokenUsage Total(IReadOnlyList<MightyGraphRun> runs) =>
        runs.Aggregate(new GraphTokenUsage(), (sum, run) => sum + (run.TotalUsage ?? new GraphTokenUsage()));

    // ── result files ──────────────────────────────────────────────────────────

    /// <summary>The workspace files the given run's final result names.</summary>
    public static List<ResultFiles.ResultFile> FilesFor(MightyGraphRun run, string? workspaceRoot)
    {
        var texts = run.ResultEntries.Select(e => e.Text).ToList();
        if (run.FinalOutput is { Length: > 0 } final) texts.Add(final);
        return ResultFiles.In(texts, workspaceRoot);
    }

    /// <summary>The newest run whose result is complete; null when none finished.</summary>
    public static MightyGraphRun? LatestCompletedRun(IReadOnlyList<MightyGraphRun> runs)
    {
        for (var i = runs.Count - 1; i >= 0; i--)
            if (runs[i].Status == "completed" && MightyGraphLayout.Finished(runs[i])) return runs[i];
        return null;
    }
}
