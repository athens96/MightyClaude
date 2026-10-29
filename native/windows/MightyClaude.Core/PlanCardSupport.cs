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
        // The newest request's: a notice run after it is no request (BackgroundNoticeRuns).
        if (!showsDiagram || request is not { CanAnswerPlan: true, State: "pending" } || MightyGraphSupport.NewestRequestRun(runs) is not { } last) return null;
        return MightyGraphLayout.Finished(last) ? null : last.Id;
    }

    /// <summary>
    /// The diagram draws the plan, so the composer area (the slot over the composer) shows only its answers
    /// (macOS <c>PlanCardSupport.composerShowsPlanActions</c>); otherwise the whole card docks there.
    /// </summary>
    public static bool ComposerShowsPlanActions(ToolPermissionRequest? request, bool showsDiagram, IReadOnlyList<MightyGraphRun> runs) =>
        DiagramPlanRunID(request, showsDiagram, runs) is not null;

    /// <summary>Where the composer shows a pending plan's answers (M/PlanCardSupport.swift:40-47).</summary>
    public enum ComposerPlaces
    {
        /// <summary>The whole card, plan and answers, docked above the composer: the diagram does not draw the plan.</summary>
        Card,
        /// <summary>Only the answers, in the style's panel, where a style shows its choices.</summary>
        PanelActions,
        /// <summary>Only the answers, docked above the composer (no style).</summary>
        BarActions,
    }

    /// <summary>
    /// One place for each pending plan, so its answers never show twice (M/PlanCardSupport.swift:58-62): the whole card
    /// while the diagram does not draw the plan, else only its answers — in the style's panel under a style, docked above
    /// the composer without one. Null for anything but an answerable pending plan.
    /// </summary>
    public static ComposerPlaces? ComposerPlace(ToolPermissionRequest? request, bool showsDiagram, IReadOnlyList<MightyGraphRun> runs, bool guided)
    {
        if (request is not { CanAnswerPlan: true, State: "pending", Plan: not null }) return null;
        if (!ComposerShowsPlanActions(request, showsDiagram, runs)) return ComposerPlaces.Card;
        return guided ? ComposerPlaces.PanelActions : ComposerPlaces.BarActions;
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
    /// One row of the background strip (M/PlanCardSupport.swift BackgroundStripRow): the task list's row, and the kind words
    /// the diagram's block uses (null: the row keeps the task list's own words) with, for a background run, its last output line.
    /// Opened, the row shows <c>Command</c> and <c>TailLines</c>. <c>Id</c> is the CLI's task id (null when the rows no longer line up
    /// with the tasks); <c>Run</c> says a background run (a command), a background sub-agent (false), or null when neither is known;
    /// <c>Command</c> is the command a run runs, or the first line of a sub-agent's prompt, from its graph block; <c>TailLines</c> a run's
    /// last output lines, at most <see cref="BackgroundOutputTail.MaxLines"/>.
    /// </summary>
    public sealed record BackgroundStripRow(StylePresentation.TaskRow Task, string? KindText = null, string? Tail = null, string? Id = null, bool? Run = null,
        string? Command = null, IReadOnlyList<string>? TailLines = null)
    {
        /// <summary>Whether opening the row shows anything more.</summary>
        public bool Opens => Command is not null || TailLines is { Count: > 0 };
        /// <summary>Kind · status · elapsed; kind words already say waiting or how it ended.</summary>
        public string Detail(DateTimeOffset now) => string.Join(" · ", (KindText is { } kind
            ? new[] { kind, Task.Elapsed(now) }
            : new[] { Task.KindTitle, Task.StatusTitle, Task.Elapsed(now) }).Where(s => s.Length > 0));
    }

    /// <summary>
    /// The strip's rows, in <see cref="BackgroundTasks"/>' order (M/PlanCardSupport.swift backgroundStripRows). Each task meets
    /// its graph block in <paramref name="run"/> (the pane's latest request) by tool use id: a background sub-agent or a
    /// background run takes its kind from the block, and a run its last line of <paramref name="output"/> (the host's
    /// background output, by block id). Without a block the CLI's kind decides and there is no output line; an <c>other</c>
    /// task keeps its words.
    /// </summary>
    public static IReadOnlyList<BackgroundStripRow> BackgroundStripRows(BackgroundWork? work, MightyGraphRun? run, IReadOnlyDictionary<string, IReadOnlyList<string>>? output = null)
    {
        if (work is null) return [];
        var tasks = BackgroundTasks(work);
        // The task list's order (StyleStateEngine): running work first, each group in the order it started.
        var sources = work.Tasks.Where(t => t.Status == "running").Concat(work.Tasks.Where(t => t.Status != "running")).ToList();
        // Should the task list ever order or cut its rows another way, the rows keep its own words rather than wear another task's.
        var matches = tasks.Count == Math.Min(sources.Count, StyleStateEngine.MaximumTaskListItems)
            && tasks.Select((task, index) => (task, source: sources[index])).All(pair =>
                (pair.source.Kind is "agent" or "shell" ? pair.source.Kind : "other") == pair.task.Kind && (pair.source.Status == "running") == pair.task.Running);
        if (!matches) return tasks.Select(task => new BackgroundStripRow(task)).ToList();
        var runId = run is null ? null : run.SourceRunID ?? run.Id;
        var blocks = new Dictionary<string, MightyGraphAgent>(StringComparer.Ordinal);
        foreach (var agent in run?.Agents ?? []) blocks.TryAdd(agent.Id, agent);
        return tasks.Select((task, index) =>
        {
            var source = sources[index];
            var block = source.ToolUseId is { } tool && runId is not null ? blocks.GetValueOrDefault(ExecutionGraphSupport.AgentNodeID(runId, tool)) : null;
            bool isRun;
            if (block is not null && (block.IsTask || block.IsBackgroundAgent)) isRun = block.IsTask;
            else if (task.Kind is "agent" or "shell") isRun = task.Kind == "shell";
            else return new BackgroundStripRow(task, Id: source.Id);
            IReadOnlyList<string> lines = isRun && block is not null && output?.GetValueOrDefault(block.Id) is { Count: > 0 } all ? all.TakeLast(BackgroundOutputTail.MaxLines).ToList() : [];
            return new BackgroundStripRow(task, MightyGraphSupport.BackgroundLabel(isRun, task.Status), lines.Count > 0 ? lines[^1] : null,
                source.Id, isRun, block is null ? null : StripCommand(block, isRun), lines);
        }).ToList();
    }

    /// <summary>What an opened strip row shows the work doing: a run's whole command, a sub-agent's first prompt line (M/PlanCardSupport.swift stripCommand).</summary>
    internal static string? StripCommand(MightyGraphAgent block, bool run)
    {
        var text = run ? MightyGraphSupport.ShownInput(block).Trim()
            : block.Input.Split('\n').Select(line => line.Trim('\r', ' ', '\t')).FirstOrDefault(line => line.Length > 0) ?? "";
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// The phone's <c>backgroundWork</c> (M/PlanCardSupport.swift mobileBackgroundWork): the strip's running rows, each with its command and
    /// output lines; never a path. A task that is neither a sub-agent nor a command (the CLI's <c>other</c> kind, with no graph block to tell)
    /// is left out: the wire names only those two kinds. Null when nothing of them runs.
    /// </summary>
    public static List<MobileBackgroundWork>? MobileBackgroundWork(BackgroundWork? work, MightyGraphRun? run, IReadOnlyDictionary<string, IReadOnlyList<string>>? output = null)
    {
        if (work is null || work.Running.Count == 0) return null;
        var rows = new Dictionary<string, BackgroundStripRow>(StringComparer.Ordinal);
        foreach (var row in BackgroundStripRows(work, run, output)) if (row.Id is { } id) rows.TryAdd(id, row);
        var items = new List<MobileBackgroundWork>();
        foreach (var task in work.Running)
        {
            var row = rows.GetValueOrDefault(task.Id);
            if ((row?.Run ?? (task.Kind == "shell" ? true : task.Kind == "agent" ? false : null)) is not { } isRun) continue;
            var title = ActivitySupport.Clean(row?.Task.Text ?? task.Description, Core.MobileBackgroundWork.MaximumTitle, true);
            var command = row?.Command is { } text ? ActivitySupport.Clean(text, Core.MobileBackgroundWork.MaximumCommand) : null;
            items.Add(new(task.Id, isRun ? "run" : "agent", title, "running", task.StartedAt, string.IsNullOrEmpty(command) ? null : command,
                row?.TailLines is { Count: > 0 } lines ? [.. lines] : null));
        }
        return items.Count == 0 ? null : items;
    }

    /// <summary>
    /// The expandable background list outside a style (M/PlanCardSupport.swift showsBackgroundStrip): in any view once
    /// the turn is over, and in the Mighty view all along, while background work runs or after it ended with its report.
    /// A style that draws its own task list keeps it. It stays until the next request starts a run and clears the work;
    /// nothing hides it.
    /// </summary>
    public static bool ShowsBackgroundStrip(BackgroundWork? work, bool mighty, bool styleDrawsTasks) =>
        !styleDrawsTasks && work is not null && (work.TurnEnded || mighty) && work.Tasks.Any(t => t.Status == "running" || NoticeDelivered(t.Status));

    /// <summary>A task that ended with the CLI's own end report (completed, failed, stopped); an <c>unknown</c> one ended with the process.</summary>
    public static bool NoticeDelivered(string status) => status is "completed" or "failed" or "stopped";

    /// <summary>
    /// The strip's tasks that no longer run (ended with a report, or with the process): the current run's, so what ended
    /// since the request that started it (M/PlanCardSupport.swift backgroundFinished).
    /// </summary>
    public static int BackgroundFinished(BackgroundWork work) => work.Tasks.Count - work.Running.Count;

    /// <summary>The strip's header counts, done then running (plan.background.doneCount · runningCount), over every task it holds, not only the rows it lists (M/PlanCardSupport.swift backgroundCounts).</summary>
    public static string BackgroundCounts(BackgroundWork work) =>
        Locale.Get("plan.background.doneCount", new Dictionary<string, string> { ["count"] = BackgroundFinished(work).ToString(CultureInfo.InvariantCulture) }) + " · "
        + Locale.Get("plan.background.runningCount", new Dictionary<string, string> { ["count"] = work.Running.Count.ToString(CultureInfo.InvariantCulture) });

    /// <summary>
    /// The phone's <c>backgroundDone</c> (M/PlanCardSupport.swift mobileBackgroundDone): the finished count whenever the Mighty
    /// view's strip shows, 0 included (its presence tells the phone the host counts); null otherwise.
    /// </summary>
    public static int? MobileBackgroundDone(BackgroundWork? work) => work is not null && ShowsBackgroundStrip(work, true, false) ? BackgroundFinished(work) : null;

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
