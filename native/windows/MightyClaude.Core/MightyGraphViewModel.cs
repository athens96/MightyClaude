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
