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

    /// <summary>Formatted zoom label for the percent button, e.g. "100%".</summary>
    public static string ZoomLabel(double zoom) => ((int)Math.Round(zoom * 100)) + "%";

    // ── pane eligibility ──────────────────────────────────────────────────────

    /// <summary>
    /// True when the pane header should show the 기본 / 마이티 mode switch.
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
        (double W, double H)? viewport = null)
        => MightyGraphLayout.Make(graphRuns, draft, running, expanded, resultFilesRunID, viewport);

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

    // ── result files panel ────────────────────────────────────────────────────

    /// <summary>
    /// Determines which run's result-files panel should be open after a new layout
    /// has been produced.  Rules match macOS MightyGraphResultFilesView:
    /// - A new completed run with files auto-opens its panel (unless manually closed).
    /// - A manual close sticks until the next new result.
    /// - Only one panel is open per graph at a time.
    /// </summary>
    public static string? NextResultFilesRunID(
        string? currentRunID,
        bool manuallyClosed,
        string? latestCompletedRunID,
        bool latestHasFiles)
    {
        if (latestCompletedRunID is null) return null;
        // A brand-new result with files always auto-opens (manuallyClosed was for a previous run).
        if (latestCompletedRunID != currentRunID && latestHasFiles)
            return latestCompletedRunID;
        // Same run: manual close sticks; otherwise keep the panel open.
        if (latestCompletedRunID == currentRunID)
            return manuallyClosed ? null : currentRunID;
        return null;
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
    public static string LocaleKeyBlockScrolling => "graph.block.scrolling";
}

/// <summary>
/// One canvas block as the Mighty view draws it: the layout node plus the copy,
/// state, request and output text, the usage capsule and its tooltip, and the
/// running/waiting indicator choice. WinUI renders these and decides nothing.
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
    string? ResultFilesRunId);

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

    /// <summary>The main request block's title: `요청 N · Claude` (style prefixes are out of scope).</summary>
    public static string RequestTitle(int ordinal, string providerLabel) =>
        Locale.Get("graph.block.requestTitle", new Dictionary<string, string> { ["ordinal"] = ordinal.ToString(), ["provider"] = providerLabel });

    /// <summary>The result block's title, by how the run ended.</summary>
    public static string ResultTitle(string runStatus) => runStatus switch
    {
        "error" or "failed" => Locale.Get("graph.block.resultError"),
        "stopped" or "cancelled" or "interrupted" => Locale.Get("graph.block.resultStopped"),
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
                case "request" when run is not null:
                    blocks.Add(new(node.Id, "request", node.Frame, RequestTitle(runIndex + 1, providerLabel),
                        StateLabel(run.Status), run.Input,
                        run.RootEntries.Where(e => e.Kind != "user").ToList(),
                        ModelUsageFormat.BlockCapsule(run.Usage, run.ResponseRecords ?? [], run.NodeModelLabel, catalog),
                        Help(run.Usage, run.ResponseRecords ?? [], Locale.Get("graph.block.blockUsageLabel"), catalog),
                        MightyGraphViewModel.BlockIndicator(run.Status, animationsEnabled), null));
                    break;
                case "agent" when run is not null && run.Agents.FirstOrDefault(a => a.Id == agentId) is { } agent:
                    blocks.Add(new(node.Id, MightyGraphSupport.BlockKind(agent), node.Frame, MightyGraphSupport.BlockTitle(agent),
                        StateLabel(agent.Status), agent.Input,
                        agent.Entries.Where(e => e.Kind != "user").ToList(),
                        ModelUsageFormat.BlockCapsule(agent.Usage, agent.ResponseRecords ?? [], null, catalog),
                        Help(agent.Usage, agent.ResponseRecords ?? [], Locale.Get("graph.block.blockUsageLabel"), catalog),
                        MightyGraphViewModel.BlockIndicator(agent.Status, animationsEnabled), null));
                    break;
                case "result" when run is not null:
                    blocks.Add(new(node.Id, "result", node.Frame, ResultTitle(run.Status),
                        StateLabel(ResultState(run.Status)), "", run.ResultEntries.Where(e => e.Kind != "user").ToList(),
                        ModelUsageFormat.BlockCapsule(run.TotalUsage, [], null, catalog),
                        Help(run.TotalUsage, [], Locale.Get("graph.block.totalUsageLabel"), catalog),
                        "none", run.Status == "completed" ? run.Id : null));
                    break;
                case "resultFiles" when run is not null:
                    blocks.Add(new(node.Id, "resultFiles", node.Frame, Locale.Get("graph.resultFiles.title"),
                        "", "", [], null, "", "none", run.Id));
                    break;
            }
        }
        return blocks;
    }

    private static string Help(GraphTokenUsage? usage, IReadOnlyList<GraphResponseRecord> records, string usageLabel, IReadOnlyList<ModelOption>? catalog)
        => records.Count == 0
            ? usage is null ? "" : usageLabel + " · " + usage.Detail
            : ModelUsageFormat.BlockCapsuleHelp(records, catalog);

    /// <summary>Which run (and, for an agent node, which agent) a layout node belongs to.</summary>
    private static (int RunIndex, string? AgentId) Locate(MightyGraphLayout.Node node, IReadOnlyList<MightyGraphRun> runs)
    {
        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            if (node.Id == MightyGraphLayout.NodeID(run, "request") && node.Kind == "request") return (i, null);
            if (node.Id == MightyGraphLayout.NodeID(run, "result") && node.Kind == "result") return (i, null);
            if (node.Id == MightyGraphLayout.NodeID(run, "result-files") && node.Kind == "resultFiles") return (i, null);
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
        int Kind(string kind) => runs.Sum(r => r.Agents.Count(a => MightyGraphSupport.BlockKind(a) == kind));
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
