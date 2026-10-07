using System.Globalization;

namespace MightyClaude.Core;

/// <summary>
/// What the plan card and the plan history read (macOS PlanCardSupport): which
/// request is the plan, where the Mighty diagram draws it, what an outcome says.
/// </summary>
public static class PlanCardSupport
{
    /// <summary>The pane's pending plan approval: the first one the run asked.</summary>
    public static ToolPermissionRequest? PendingPlan(IEnumerable<ToolPermissionRequest>? requests) =>
        requests?.FirstOrDefault(r => r.CanAnswerPlan && r.State == "pending" && r.Plan is not null);

    /// <summary>
    /// The diagram request the pending plan card goes under, or null to dock it
    /// above the composer. A Windows plan request carries the pane's id, and a
    /// pane runs one request at a time, so it is the newest request while that
    /// one is still going.
    /// </summary>
    public static string? DiagramPlanRunID(ToolPermissionRequest? request, bool showsDiagram, IReadOnlyList<MightyGraphRun> runs)
    {
        if (!showsDiagram || request is not { CanAnswerPlan: true, State: "pending" } || runs.Count == 0) return null;
        var last = runs[^1];
        return MightyGraphLayout.Finished(last) ? null : last.Id;
    }

    /// <summary>Answered plans whose request the diagram draws, oldest first: the graph run's id and the record's.</summary>
    public static List<(string RunID, string RecordID)> DiagramRecords(IReadOnlyList<PlanRecord>? history, IReadOnlyList<MightyGraphRun> runs)
    {
        var list = new List<(string, string)>();
        foreach (var record in history ?? [])
        {
            if (record.GraphRunId is not { } graph) continue;
            var run = runs.LastOrDefault(r => r.SourceRunID == graph || r.Id == graph);
            if (run is not null) list.Add((run.Id, record.Id));
        }
        return list;
    }

    /// <summary>Answered plans show as blocks beside their requests in the Mighty diagram; everywhere else (default view, timeline) as the strip.</summary>
    public static bool ShowsHistoryStrip(bool mightyDiagram) => !mightyDiagram;

    /// <summary>Words the change request box holds that are still too long to send.</summary>
    public static bool FeedbackTooLong(string? text) => !string.IsNullOrWhiteSpace(text) && !CanSendRevise(text);

    /// <summary>수정 요청 sends only text Core accepts: not empty, within the bound.</summary>
    public static bool CanSendRevise(string? text)
    {
        try { ClaudePlanMode.ValidatedFeedback(text); return true; }
        catch (ArgumentException) { return false; }
    }

    public static string OutcomeTitle(string outcome) => outcome switch
    {
        PlanOutcome.ApprovedAuto => Locale.Get("plan.outcome.approvedAuto"),
        PlanOutcome.ApprovedConfirm => Locale.Get("plan.outcome.approvedConfirm"),
        PlanOutcome.Revised => Locale.Get("plan.outcome.revised"),
        _ => Locale.Get("plan.outcome.cancelled"),
    };

    /// <summary>The pane header's word while the turn is over but background work still runs; null otherwise.</summary>
    public static string? BackgroundStatus(BackgroundWork? work) =>
        work is { WaitingOnBackground: true }
            ? Locale.Get("plan.background.status", new Dictionary<string, string> { ["count"] = work.Running.Count.ToString(CultureInfo.InvariantCulture) })
            : null;

    /// <summary>The pane's background tasks as the plan style's task list draws them (§1.17): running first, at most eight.</summary>
    public static IReadOnlyList<StylePresentation.TaskRow> BackgroundTasks(BackgroundWork? work) =>
        work is { Tasks.Count: > 0 } ? StylePresentation.Tasks(StyleStateEngine.RunState("background", "taskList", "planning", null, work)) : [];

    /// <summary>
    /// The expandable background list outside a style (M/PlanCardSupport.swift showsBackgroundStrip): in any view while
    /// the turn is over and background work runs, and in the Mighty view whenever it runs — unless the style draws its own,
    /// or the user hid the line (<see cref="AppSnapshot.ShowsBackgroundWork"/>).
    /// </summary>
    public static bool ShowsBackgroundStrip(BackgroundWork? work, bool mighty, bool styleDrawsTasks, bool enabled = true) =>
        enabled && !styleDrawsTasks && work is not null && work.Running.Count > 0 && (work.WaitingOnBackground || mighty);

    /// <summary>The strip's folded line: the header's "turn done" word while waiting, else how many still run.</summary>
    public static string BackgroundSummary(BackgroundWork work) =>
        BackgroundStatus(work) ?? Locale.Get("styles.state.backgroundRunning", new Dictionary<string, string> { ["count"] = work.Running.Count.ToString(CultureInfo.InvariantCulture) });

    /// <summary>"HH:mm" in the given zone (local when null) for an ISO 8601 time; the input when unreadable.</summary>
    public static string TimeText(string iso, TimeZoneInfo? zone = null)
    {
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)) return iso;
        return TimeZoneInfo.ConvertTime(value, zone ?? TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    public static string ReceivedText(string iso, TimeZoneInfo? zone = null) =>
        Locale.Get("plan.card.received", new Dictionary<string, string> { ["time"] = TimeText(iso, zone) });

    /// <summary>The first non-empty line of a plan, without Markdown heading marks, for a folded entry.</summary>
    public static string Headline(string plan)
    {
        var line = plan.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return ActivitySupport.Clean(line.TrimStart('#').Trim(), 240, singleLine: true);
    }
}
