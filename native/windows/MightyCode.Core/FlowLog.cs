using System.Globalization;

namespace MightyCode.Core;

/// <summary>
/// The flow's session log: the last few things that happened in the pane's newest activity (the newest request and Claude's replies to its
/// background work after it), each with its time, who did it and what it was, under one status line. It reads the very runs the timeline draws
/// (<see cref="MightyTimeline"/>) and their blocks' own records: there is no second event source. A row is something the app saw happen at a time it
/// recorded, never a reconstruction (macOS FlowLog).
/// </summary>
public static class FlowLog
{
    /// <summary>Who a row is about: the main session, or a sub-agent (by its block's id).</summary>
    public sealed record Actor(FlowRole Role, string? AgentId = null)
    {
        public static readonly Actor Lead = new(FlowRole.Lead);
        public static Actor Subagent(string id) => new(FlowRole.Subagent, id);
    }

    /// <summary>One row. <c>Who</c>: the actor in words (the main session's model, a sub-agent's name). <c>Ink</c>: its role's, or a custom
    /// agent's own colour. <c>NodeID</c>: the diagram block the row is about.</summary>
    public sealed record Row(string Id, DateTimeOffset Time, Actor Actor, string Who, FlowInk Ink, string What, string NodeID);

    /// <summary>The rows the strip shows when it is open.</summary>
    public const int VisibleRows = 5;
    /// <summary>The most a row's words hold; the view cuts what does not fit its width.</summary>
    internal const int LineBytes = 240;

    /// <summary>The runs of the pane's newest activity: its newest request and the notice runs after it, by index into <paramref name="runs"/>.</summary>
    public static (int Start, int End) Activity(IReadOnlyList<MightyGraphRun> runs) =>
        runs.Count == 0 ? (0, 0) : (MightyGraphSupport.NewestRequestIndex(runs) ?? runs.Count - 1, runs.Count);

    private delegate Row Make(DateTimeOffset time);

    /// <summary>The last <paramref name="limit"/> rows of the newest activity, oldest first. <paramref name="definitions"/> are the custom agents the
    /// blocks' types name (their colour is the row's ink). Of each block only its
    /// last <paramref name="limit"/> events are taken, read from the end: one that is not among a block's own last few cannot be among the last few of
    /// all. Only those few have their time read.</summary>
    public static List<Row> Rows(IReadOnlyList<MightyGraphRun> runs, int limit = VisibleRows, IReadOnlyDictionary<string, AgentDefinition>? definitions = null)
    {
        if (limit <= 0) return [];
        definitions ??= new Dictionary<string, AgentDefinition>();
        var candidates = new List<(string Stamp, Make Make)>();
        var span = Activity(runs);
        for (var index = span.Start; index < span.End; index++)
        {
            var run = runs[index];
            var leadNode = MightyGraphLayout.NodeID(run, MightyGraphLayout.WorkSuffix);
            // A notice turn is the same session answering: named as the request is when it has no answer of its own yet.
            var leadName = LeadName(run, runs[span.Start]);
            (string, Make) Lead(string id, string stamp, string what, string? node = null) =>
                (stamp, time => new Row(id, time, Actor.Lead, leadName, FlowRoles.Ink(FlowRole.Lead), what, node ?? leadNode));
            if (run.NoticeRun == true)
            {
                // A notice came in: at the first thing its turn recorded, else when the work it reports on ended.
                var arrived = run.RootEntries.FirstOrDefault()?.Timestamp ?? run.Agents.Select(a => a.Figures?.EndedAt).OfType<string>().Max(StringComparer.Ordinal);
                if (arrived is not null && Line(Locale.Get("graph.log.notice", new Dictionary<string, string> { ["title"] = BackgroundNoticeRuns.Heading(run) })) is { } line)
                    candidates.Add(Lead(run.Id + ":notice", arrived, line, MightyGraphLayout.NodeID(run, "request")));
            }
            // A request's own record in one of its steps is that step's (FlowSteps).
            var steps = FlowSteps.Steps(run);
            string NodeOf(LogEntry entry) => steps.Count > 0 && AgentRunTiming.Parse(entry.Timestamp) is { } time && FlowSteps.StepIndex(time, steps) is { } step
                ? MightyGraphLayout.NodeID(run, FlowSteps.NodeSuffix(steps[step])) : leadNode;
            // Read from the end and put back in the order they were recorded.
            var latest = new List<(string, Make)>();
            for (var at = run.RootEntries.Count - 1; at >= 0 && latest.Count < limit; at--)
            {
                var entry = run.RootEntries[at];
                if (LeadWords(entry) is not { } what) continue;
                latest.Add(Lead(run.Id + ":" + entry.Id, entry.Timestamp, what, NodeOf(entry)));
            }
            latest.Reverse();
            candidates.AddRange(latest);
            // The turn's own end: at its result, which stands at its last record. A notice taken into a running turn has no reply of its own and so
            // no end.
            if (MightyGraphSupport.Terminal(run.Status) && !run.QuietNotice && (run.ResultEntries.FirstOrDefault()?.Timestamp ?? run.RootEntries.LastOrDefault()?.Timestamp) is { } ended)
                candidates.Add(Lead(run.Id + ":end", ended, FlowCard.StateWord(MobileMightySupport.Status(run.Status))));
            foreach (var agent in run.FlowAgents)
            {
                if (!agent.IsSubagent || agent.SourceBlockId is not null) continue;
                var node = MightyGraphLayout.NodeID(run, "agent:" + agent.Id);
                var name = MightyGraphSupport.BlockTitle(agent);
                var definition = agent.Identity?.Type is { } type ? definitions.GetValueOrDefault(type) : null;
                var ink = FlowRoles.Ink(FlowRole.Subagent, definition?.Color);
                (string, Make) Note(string id, string stamp, string what) => (stamp, time => new Row(id, time, Actor.Subagent(agent.Id), name, ink, what, node));
                if (agent.Figures?.StartedAt is { } started)
                {
                    var identity = FlowCard.IdentityLine(agent, definition);
                    candidates.Add(Note(node + ":start", started, string.Join(" · ", new[] { Locale.Get("graph.log.started"), identity }.OfType<string>())));
                }
                var calls = new List<(string, Make)>();
                for (var at = agent.Entries.Count - 1; at >= 0 && calls.Count < limit; at--)
                {
                    var entry = agent.Entries[at];
                    if (entry.Activity is not { } activity || activity.Kind == "turn" || Line(activity.Summary.Length == 0 ? activity.ToolName ?? "" : activity.Summary) is not { } what) continue;
                    calls.Add(Note(node + ":" + entry.Id, entry.Timestamp, what));
                }
                calls.Reverse();
                candidates.AddRange(calls);
                if (MightyGraphSupport.Terminal(agent.Status) && agent.Figures?.EndedAt is { } finished)
                    candidates.Add(Note(node + ":end", finished, FlowCard.StateWord(MobileMightySupport.Status(agent.Status))));
            }
        }
        // Each in the order it was read, so two things of one second stay as they were recorded.
        var timed = new List<(DateTimeOffset Time, int Order, Make Make)>();
        for (var order = 0; order < candidates.Count; order++)
            if (AgentRunTiming.Parse(candidates[order].Stamp) is { } time) timed.Add((time, order, candidates[order].Make));
        timed.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Order.CompareTo(b.Order));
        return timed.Skip(Math.Max(0, timed.Count - limit)).Select(t => t.Make(t.Time)).ToList();
    }

    /// <summary>The diagram block a row's click shows: its own, else the nearest block above it that the diagram drew (a block under a folded card is
    /// laid out nowhere), else its request's main session. Null when the diagram drew none of them.</summary>
    public static string? Target(Row row, IReadOnlyList<MightyGraphRun> runs, Func<string, bool> drawn)
    {
        if (drawn(row.NodeID)) return row.NodeID;
        var span = Activity(runs);
        for (var index = span.End - 1; index >= span.Start; index--)
        {
            // The role view draws the 실행 block only until the answer is there: then the main session is its result.
            if (row.NodeID == MightyGraphLayout.NodeID(runs[index], MightyGraphLayout.WorkSuffix))
                return new[] { "result", "request" }.Select(suffix => MightyGraphLayout.NodeID(runs[index], suffix)).FirstOrDefault(drawn);
            if (DrawnNode(row.NodeID, runs[index], drawn) is { } node) return node;
            if (runs[index].FlowAgents.Any(a => MightyGraphLayout.NodeID(runs[index], "agent:" + a.Id) == row.NodeID)) return null;
        }
        return null;
    }

    /// <summary>The block of <paramref name="run"/> that stands for <paramref name="nodeID"/> on the diagram: itself when drawn, else the nearest block above
    /// it that is, else the run's main session. Null when <paramref name="nodeID"/> is no sub-agent block of the run, or none of them is drawn.</summary>
    public static string? DrawnNode(string nodeID, MightyGraphRun run, Func<string, bool> drawn)
    {
        if (drawn(nodeID)) return nodeID;
        var agents = run.FlowAgents;
        if (agents.FirstOrDefault(a => MightyGraphLayout.NodeID(run, "agent:" + a.Id) == nodeID) is not { } own) return null;
        var above = own.ParentID;
        for (var depth = 0; above is not null && depth < 64; depth++)
        {
            var id = above;
            if (agents.FirstOrDefault(a => a.Id == id) is not { } parent) break;
            var node = MightyGraphLayout.NodeID(run, "agent:" + parent.Id);
            if (drawn(node)) return node;
            above = parent.ParentID;
        }
        return new[] { MightyGraphLayout.WorkSuffix, "result", "request" }.Select(suffix => MightyGraphLayout.NodeID(run, suffix)).FirstOrDefault(drawn);
    }

    /// <summary>What a record of the main session's says in the log: a tool call as the transcript words it, a question it asked the user, a context
    /// compaction. Its replies and the run's own lifecycle rows are not events of the log, and a call that starts a sub-agent is that sub-agent's own
    /// first row.</summary>
    internal static string? LeadWords(LogEntry entry)
    {
        if (entry.Activity is { } activity)
        {
            if (activity.Kind == "turn") return null;
            var tool = activity.ToolName ?? "";
            if (tool.ToLowerInvariant() is "agent" or "task") return null;
            var words = activity.Summary.Length == 0 ? tool : activity.Summary;
            return Line(tool == "AskUserQuestion" ? Locale.Get("graph.log.question", new Dictionary<string, string> { ["text"] = words }) : words);
        }
        if (entry.Kind != "system" || !entry.Text.StartsWith(ContextCompaction.Title, StringComparison.Ordinal)) return null;
        return Line(entry.Text);
    }

    private static string? Line(string raw) => ActivitySupport.Clean(raw, LineBytes, true) is { Length: > 0 } clean ? clean : null;

    /// <summary>The main session in words: the model that answered last, else the model the request was set to, else the agent's name.
    /// <paramref name="request"/> is the request a notice run answers for: the same session, whose model names it while the notice turn has no answer of
    /// its own.</summary>
    public static string LeadName(MightyGraphRun run, MightyGraphRun? request = null)
    {
        foreach (var candidate in new[] { run, request }.OfType<MightyGraphRun>())
            if (candidate.ResponseRecords?.LastOrDefault(r => !string.IsNullOrEmpty(r.Model))?.Model is { } model) return ModelUsageFormat.ShortName(model, versioned: true);
        if (ModelUsageFormat.VersionedNodeLabel(run.NodeModelLabel ?? request?.NodeModelLabel) is { } label) return label;
        return ProviderMark.Label(run.Provider ?? "claude");
    }

    /// <summary>A row's time as the strip writes it: hours, minutes and seconds in the reader's own time zone.</summary>
    public static string Clock(DateTimeOffset date, TimeZoneInfo? zone = null) =>
        TimeZoneInfo.ConvertTime(date, zone ?? TimeZoneInfo.Local).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    // ── Status ──

    /// <summary>One part of the status line with the role whose ink it takes.</summary>
    public sealed record Part(FlowRole Role, string Text);

    /// <summary>The strip's header: who is doing what right now, in one line. <c>Lead</c>: "Haiku 5.5 실행 중". <c>Subagents</c>: "하위 에이전트 2/3 실행 중",
    /// or how many ended once nothing of the request's row runs; null when the request delegated to none. <c>Tasks</c>: "작업 2개 실행 중" while background
    /// commands of the request run.</summary>
    public sealed record Status(string Lead, string? Subagents, string? Tasks)
    {
        public string Text => string.Join(" · ", Parts.Select(p => p.Text));
        /// <summary>The line's parts in order, each with the role whose ink it takes.</summary>
        public List<Part> Parts
        {
            get
            {
                var parts = new List<Part> { new(FlowRole.Lead, Lead) };
                if (Subagents is not null) parts.Add(new(FlowRole.Subagent, Subagents));
                if (Tasks is not null) parts.Add(new(FlowRole.Task, Tasks));
                return parts;
            }
        }
    }

    /// <summary>The status of the pane's newest activity, null for a pane without a request. The main session's state is the state of the run the views
    /// follow (<see cref="MightyGraphSupport.FollowRunIndex"/>): the request while it runs or awaits its notices, a notice turn while Claude answers one.
    /// The sub-agents are the newest request's.</summary>
    public static Status? StatusOf(IReadOnlyList<MightyGraphRun> runs)
    {
        if (MightyGraphSupport.NewestRequestIndex(runs) is not { } newest) return null;
        var request = runs[newest];
        var followed = runs[MightyGraphSupport.FollowRunIndex(runs) ?? newest];
        string state;
        if (!MightyGraphSupport.Terminal(followed.Status)) state = FlowCard.StateWord(MobileMightySupport.Status(followed.Status));
        else if (BackgroundNoticeRuns.AwaitingNotice(request, runs) is not null) state = BackgroundNoticeRuns.StateWord;
        else state = FlowCard.StateWord(MobileMightySupport.Status(request.Status));
        string? subagents = null, tasks = null;
        static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
        if (FlowReturn.Of(request) is { } back)
        {
            if (back.SubagentsRunning > 0)
                subagents = Locale.Get("graph.log.subagentsRunning", new Dictionary<string, string> { ["running"] = N(back.SubagentsRunning), ["total"] = N(back.Subagents) });
            // Ended sub-agents are worth a word once nothing else of the row runs.
            else if (back.Subagents > 0 && back.TasksRunning == 0) subagents = Locale.Get("graph.log.subagentsDone", new Dictionary<string, string> { ["total"] = N(back.Subagents) });
            if (back.TasksRunning > 0) tasks = Locale.Get("graph.log.tasksRunning", new Dictionary<string, string> { ["count"] = N(back.TasksRunning) });
        }
        return new(LeadName(followed, request) + " " + state, subagents, tasks);
    }
}
