using System.Globalization;

namespace MightyCode.Core;

/// <summary>
/// The advisor's band beside a run in the flow: the model a Claude session asks for advice while it works (Claude Code's experimental advisor tool),
/// each time it was asked, by whom, where in the caller's work and how it ended. It reads the run's own record (<see cref="GraphAdvisor"/>,
/// <see cref="MightyGraphRun.ConfiguredAdvisor"/>) and says only what the app saw: the advisor's reply usually goes to Claude encrypted, and then the
/// band says so and shows no text (macOS FlowAdvisor).
/// </summary>
public static class FlowAdvisor
{
    /// <summary>The band's node suffix.</summary>
    public const string Suffix = "advisor";
    public const double Width = 248;
    /// <summary>Between the band and the leftmost block beside it.</summary>
    public const double Gap = 28;
    public const double HeaderHeight = DesignMetrics.Layout.BlockHead;
    public const double RowHeight = 38;
    public const double EarlierHeight = 18;
    public const double NoteHeight = 32;
    public const double FooterHeight = 24;
    /// <summary>A band never stands shorter than its header, the line that counts earlier consultations, one row, its note and its totals: its newest
    /// consultation always shows.</summary>
    public const double MinimumHeight = HeaderHeight + EarlierHeight + RowHeight + NoteHeight + FooterHeight;

    /// <summary>What the advisor is doing in a run that still goes on.</summary>
    public enum State { OnCall, Consulting }
    /// <summary>Where in the caller's work a consultation stands, as what the app saw: the caller had changed no file yet, it was in the middle of its
    /// work, or nothing but its answer followed.</summary>
    public enum Position { BeforeChange, During, LastStep }

    /// <summary>One consultation's row. <c>Caller</c>: the main session's word, or the sub-agent's name. <c>CallerNodeID</c>: the diagram block that asked.
    /// <c>Seconds</c>: how long the reply took once it came. <c>Text</c>: the advice, only when the CLI gave it as text.</summary>
    public sealed record Row(string Id, FlowRole Role, string Caller, FlowInk Ink, string CallerNodeID, Position Position, DateTimeOffset? Started, double? Seconds,
        string State, string Outcome, string? Text)
    {
        /// <summary>"답 직전 마지막 단계 · 조언 전달 · 내용 비표시".</summary>
        public string Line => Words(Position) + " · " + Outcome;
    }

    /// <summary>The band. <c>Model</c>: as a turn's result named it, else the one the request was started with, marked as asked for. <c>Advising</c>: null
    /// once the run and everything it started ended. <c>Earlier</c>: consultations seen before the ones kept. <c>Calls</c>: every consultation seen.</summary>
    public sealed record Band(string? Model, State? Advising, IReadOnlyList<Row> Rows, int Earlier, int Calls, GraphTokenUsage? Usage, bool HidesText)
    {
        /// <summary>"조언자 · Opus 5.5 · 검토 중".</summary>
        public string Header => string.Join(" · ", new[] { Locale.Get("graph.identity.role.advisor"), Model, Advising is { } state ? Words(state) : null }.OfType<string>());
        /// <summary>"호출 3 · 읽은 토큰 34K": what the advisor read is its input, cached or not.</summary>
        public string Totals
        {
            get
            {
                var parts = new List<string> { Locale.Get("graph.advisor.totals.calls", new Dictionary<string, string> { ["count"] = Calls.ToString(CultureInfo.InvariantCulture) }) };
                if (Usage is { } usage) parts.Add(Locale.Get("graph.advisor.totals.tokens", new Dictionary<string, string> { ["tokens"] = GraphTokenUsage.Compact(usage.InputTokens + usage.CacheReadTokens + usage.CacheCreationTokens) }));
                return string.Join(" · ", parts);
            }
        }
        public string? Note => HidesText ? Locale.Get("graph.advisor.note.hidden") : null;
        /// <summary>What a band without a consultation says in its rows' place: not asked yet while the run goes on, not asked once it ended.</summary>
        public string? Empty => Calls != 0 ? null : Advising is null ? Locale.Get("graph.advisor.none") : Locale.Get("graph.advisor.empty");
        public bool Equals(Band? other) => other is not null && Model == other.Model && Advising == other.Advising && Rows.SequenceEqual(other.Rows) && Earlier == other.Earlier
            && Calls == other.Calls && Equals(Usage, other.Usage) && HidesText == other.HidesText;
        public override int GetHashCode() => HashCode.Combine(Model, Advising, Rows.Count, Calls);
    }

    /// <summary>The band of <paramref name="run"/>, null for a run without an advisor: one that was started with none known to be on and never consulted
    /// one.</summary>
    public static Band? Of(MightyGraphRun run, IReadOnlyDictionary<string, AgentDefinition>? definitions = null)
    {
        if (!Shows(run)) return null;
        definitions ??= new Dictionary<string, AgentDefinition>();
        var advisor = run.Advisor ?? new GraphAdvisor();
        var agents = run.FlowAgents;
        var rows = advisor.Consultations.Select(call =>
        {
            var agent = call.Caller is { } id ? agents.FirstOrDefault(a => a.Id == id) : null;
            var ended = agent is not null ? agent.Status == "completed" : call.Caller is null && run.Status == "completed";
            double? seconds = call.Started is { } start && call.Ended is { } end && end >= start ? (end - start).TotalSeconds : null;
            var color = agent?.Identity?.Type is { } type ? definitions.GetValueOrDefault(type)?.Color : null;
            return new Row(call.Id, agent is null ? FlowRole.Lead : FlowRole.Subagent,
                agent is not null ? MightyGraphSupport.BlockTitle(agent) : Locale.Get("graph.identity.role.lead"),
                agent is not null ? FlowRoles.Ink(FlowRole.Subagent, color) : FlowRoles.Ink(FlowRole.Lead),
                MightyGraphLayout.NodeID(run, agent is not null ? "agent:" + agent.Id : MightyGraphLayout.WorkSuffix),
                PositionOf(call, ended), call.Started, call.State == "consulting" ? null : seconds, call.State, Outcome(call), call.Text);
        }).ToList();
        var live = !MightyGraphSupport.Terminal(run.Status) || agents.Any(a => a.IsSubagent && a.SourceBlockId is null && !MightyGraphSupport.Terminal(a.Status));
        var consulting = advisor.Consultations.Any(c => c.State == "consulting");
        return new(ModelUsageFormat.VersionedNodeLabel(GraphModelLabel.AgentModelLabel(advisor.Model, run.ConfiguredAdvisor)),
            consulting ? State.Consulting : live ? State.OnCall : null, rows, Math.Max(0, advisor.Count - advisor.Consultations.Count), advisor.Count,
            advisor.Usage, advisor.Consultations.Any(c => c.State == "done" && c.Text is null));
    }

    /// <summary>A run has a band when its stream showed an advisor at work, or when it is a request that was started with one known to be on. A notice run,
    /// which only inherits what its request was started with, has one once its own turn asked.</summary>
    public static bool Shows(MightyGraphRun run) =>
        (run.Provider ?? "claude") == "claude" && (run.Advisor is not null || run.ConfiguredAdvisor is not null && run.NoticeRun != true);

    /// <summary>Where a consultation stands in its caller's work. <paramref name="callerAnswered"/>: the caller ended with its answer. Nothing but the answer
    /// followed it: its last step. Else, asked before the caller changed any file, or in the middle of the work.</summary>
    public static Position PositionOf(GraphAdvisorCall call, bool callerAnswered)
    {
        if (callerAnswered && call.Followed != true) return Position.LastStep;
        return call.BeforeChange == true ? Position.BeforeChange : Position.During;
    }

    public static string Words(Position position) => Locale.Get(position switch
    {
        Position.BeforeChange => "graph.advisor.position.beforeChange",
        Position.During => "graph.advisor.position.during",
        _ => "graph.advisor.position.lastStep",
    });
    public static string Words(State state) => Locale.Get(state == State.OnCall ? "graph.advisor.state.onCall" : "graph.advisor.state.consulting");

    /// <summary>How a consultation ended, in words. A reply without text went to Claude all the same: nothing is made up for it.</summary>
    public static string Outcome(GraphAdvisorCall call) => call.State switch
    {
        "done" => Locale.Get(call.Text is null ? "graph.advisor.outcome.hidden" : "graph.advisor.outcome.delivered"),
        "declined" => Locale.Get("graph.advisor.outcome.declined"),
        "error" => call.ErrorCode is { } code ? Locale.Get("graph.advisor.outcome.errorCode", new Dictionary<string, string> { ["code"] = code }) : Locale.Get("graph.advisor.outcome.error"),
        "stopped" => Locale.Get("graph.advisor.outcome.stopped"),
        _ => Locale.Get("graph.advisor.state.consulting"),
    };

    /// <summary>The rows a band of <paramref name="height"/> shows, the newest last, and how many consultations stand before them: the ones it has no room
    /// for and the ones no longer kept. A line says so in the rows' place when there are any.</summary>
    public static (IReadOnlyList<Row> Rows, int Earlier) Visible(Band band, double height)
    {
        var room = height - HeaderHeight - FooterHeight - (band.HidesText ? NoteHeight : 0);
        var whole = Math.Max(0, (int)(room / RowHeight));
        if (band.Earlier == 0 && band.Rows.Count <= whole) return (band.Rows, 0);
        var shown = Math.Max(0, (int)((room - EarlierHeight) / RowHeight));
        var rows = band.Rows.Skip(Math.Max(0, band.Rows.Count - shown)).ToList();
        return (rows, band.Calls - rows.Count);
    }

    /// <summary>The blocks that consulted the advisor in <paramref name="run"/>, each once, in the order they first asked: where the band's dotted
    /// connectors go.</summary>
    public static List<string> Callers(MightyGraphRun run)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        var agents = run.FlowAgents;
        foreach (var call in run.Advisor?.Consultations ?? [])
        {
            var agent = call.Caller is { } id ? agents.FirstOrDefault(a => a.Id == id) : null;
            var node = MightyGraphLayout.NodeID(run, agent is not null ? "agent:" + agent.Id : MightyGraphLayout.WorkSuffix);
            if (seen.Add(node)) result.Add(node);
        }
        return result;
    }

    // ── The session log ──

    /// <summary>The advisor in words for the log: its role and, when known, its model.</summary>
    public static string Name(MightyGraphRun run)
    {
        var model = ModelUsageFormat.VersionedNodeLabel(GraphModelLabel.AgentModelLabel(run.Advisor?.Model, run.ConfiguredAdvisor));
        return string.Join(" · ", new[] { Locale.Get("graph.identity.role.advisor"), model }.OfType<string>());
    }

    /// <summary>What the advisor did in the pane's newest activity, for the log (<see cref="FlowLog.Rows"/>): each consultation's start and, once it ended,
    /// its end. Of each run only the last <paramref name="limit"/> consultations are read.</summary>
    public static List<FlowLog.AdvisorNote> Notes(IReadOnlyList<MightyGraphRun> runs, int limit = FlowLog.VisibleRows)
    {
        var notes = new List<FlowLog.AdvisorNote>();
        var span = FlowLog.Activity(runs);
        for (var index = span.Start; index < span.End; index++)
        {
            var run = runs[index];
            if (run.Advisor?.Consultations is not { Count: > 0 } calls) continue;
            var who = Name(run);
            var node = MightyGraphLayout.NodeID(run, Suffix);
            var agents = run.FlowAgents;
            foreach (var call in calls.Skip(Math.Max(0, calls.Count - Math.Max(0, limit))))
            {
                var caller = call.Caller is { } id && agents.FirstOrDefault(a => a.Id == id) is { } agent ? MightyGraphSupport.BlockTitle(agent) : Locale.Get("graph.identity.role.lead");
                if (call.StartedAt is { } started)
                    notes.Add(new(node + ":" + call.Id + ":start", started, who, Locale.Get("graph.advisor.log.asked", new Dictionary<string, string> { ["caller"] = caller }), node));
                if (call.State != "consulting" && call.EndedAt is { } ended)
                    notes.Add(new(node + ":" + call.Id + ":end", ended, who, Outcome(call), node));
            }
        }
        return notes;
    }

    /// <summary>The advisor's part of the status line (<see cref="FlowLog.StatusOf"/>): at work, on call while the activity goes on, or how often it was
    /// consulted once it ended. Null for an activity without an advisor.</summary>
    public static FlowLog.AdvisorState? Status(IReadOnlyList<MightyGraphRun> runs)
    {
        var span = FlowLog.Activity(runs);
        var advised = Enumerable.Range(span.Start, span.End - span.Start).Select(index => runs[index]).Where(Shows).ToList();
        if (advised.Count == 0) return null;
        if (advised.Any(run => run.Advisor?.Consultations.Any(c => c.State == "consulting") == true)) return FlowLog.AdvisorState.Consulting;
        var followed = runs[MightyGraphSupport.FollowRunIndex(runs) ?? span.Start];
        var waits = MightyGraphSupport.NewestRequestIndex(runs) is { } newest && BackgroundNoticeRuns.AwaitingNotice(runs[newest], runs) is not null;
        if (!MightyGraphSupport.Terminal(followed.Status) || waits) return FlowLog.AdvisorState.OnCall;
        var count = advised.Sum(run => run.Advisor?.Count ?? 0);
        return count > 0 ? FlowLog.AdvisorState.Consulted(count) : null;
    }
}
