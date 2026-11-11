namespace MightyCode.Core;

/// <summary>
/// The role view as a phone draws it (docs/mobile-remote.md, "역할 보기"): who each block is, a running sub-agent's or a background command's
/// compact row, the delegate line, the way back and the session log's status line (M/MightyCore/Remote/MobileFlow.swift). Every word
/// is the desktop's own, read with the very rules the flow is drawn by (<see cref="FlowRoles"/>, <see cref="FlowCard"/>, <see cref="FlowDelegation"/>,
/// <see cref="FlowReturn"/>, <see cref="FlowLog"/>); the phone only lays it out and keeps a card's elapsed time against its
/// own clock. Inks travel as design token names (<see cref="FlowRoles.Token"/>), times as milliseconds since 1970. Sent only to a phone that asked
/// for it (<c>roles=1</c>, capability <c>roles</c>); both hosts' tests produce native/contracts/fixtures/mobile-roles.json.
/// </summary>
public static class MobileFlow
{
    /// <summary>What a payload with the role view is built with. <c>FanOut</c>: the pane draws the role view (a Claude pane): a request's background
    /// blocks travel in it as compact rows, with the delegate lines, the way back and the log. Without it a block still says who it is.
    /// <c>Definitions</c> are the custom agents the blocks' types name (<see cref="AgentDefinitions"/>).</summary>
    public sealed record Roles(bool FanOut, IReadOnlyDictionary<string, AgentDefinition>? Definitions = null)
    {
        public IReadOnlyDictionary<string, AgentDefinition> Known => Definitions ?? new Dictionary<string, AgentDefinition>();
        public AgentDefinition? Definition(MightyGraphAgent agent) => agent.Identity?.Type is { } type ? Known.GetValueOrDefault(type) : null;
    }

    /// <summary>A time as the wire carries it: milliseconds since 1970.</summary>
    internal static long? Milliseconds(DateTimeOffset? time) => time?.ToUnixTimeMilliseconds();

    private static List<string>? Marks(IReadOnlyList<FlowMark> marks) => marks.Count == 0 ? null : marks.Select(m => m.ToString().ToLowerInvariant()).ToList();
    private static string Name(FlowRole role) => role.ToString().ToLowerInvariant();

    /// <summary>The main session's identity on a run's <c>main</c> block: its model (<see cref="FlowLog.LeadName"/>) and the pane's effort when it named
    /// one. <paramref name="request"/>: the request a notice run answers for.</summary>
    public static MobileBlockRole Lead(MightyGraphRun run, MightyGraphRun? request)
    {
        var who = FlowRoles.Lead(run);
        var parts = new List<string> { FlowLog.LeadName(run, request) };
        if (who.Effort is { } effort) parts.Add(Locale.Get("composer.effort.menu", new Dictionary<string, string> { ["effort"] = effort }));
        return new(Name(who.Role), FlowRoles.Token(who.Ink), string.Join(" · ", parts), null, who.Summary);
    }

    /// <summary>A child block's identity: a sub-agent's type and model with its marks, or, in the role view, a background command's
    /// <c>작업 · &lt;command&gt;</c>. Null for a block that is neither.</summary>
    public static MobileBlockRole? Role(MightyGraphAgent agent, MightyGraphRun run, Roles roles, IReadOnlyList<string>? output = null)
    {
        if (FlowRoles.Subagent(agent, roles.Definition(agent)) is { } who)
            return new(Name(who.Role), FlowRoles.Token(who.Ink), FlowCard.IdentityLine(who, agent), Marks(who.Marks), who.Summary);
        if (!roles.FanOut || FlowRoles.Task(agent) is not { } task) return null;
        return new(Name(task.Role), FlowRoles.Token(task.Ink), FlowCard.Of(agent, run, null, output ?? []).Identity, Marks(task.Marks), task.Summary);
    }

    /// <summary>The compact row of a request's background sub-agent or command, or of a sub-agent still running (<see cref="FlowCard.FoldsToCard"/>);
    /// null for every other block. <paramref name="window"/> are the runs the payload carries: a finished background block's row leads to its copy in a
    /// notice run among them.</summary>
    public static MobileBlockCard? Card(MightyGraphAgent agent, MightyGraphRun run, IReadOnlyList<MightyGraphRun> window, Roles roles, IReadOnlyList<string>? output = null)
    {
        if (!roles.FanOut || !FlowCard.FoldsToCard(agent)) return null;
        var content = FlowCard.Of(agent, run, roles.Definition(agent), agent.IsTask ? output ?? [] : []);
        var (before, after) = FlowCard.FigureParts(content);
        MobileCopyRef? copy = null;
        if (agent.Background == true && MightyGraphLayout.Terminal(agent.Status))
            for (var index = window.Count - 1; index >= 0 && copy is null; index--)
                if (window[index].NoticeRun == true && window[index].Agents.LastOrDefault(a => a.SourceBlockId == agent.Id) is { } copied) copy = new(window[index].Id, copied.Id);
        return new(content.Activity, before, after, Milliseconds(content.StartedAt), Milliseconds(content.EndedAt), content.Nested > 0 ? content.Nested : null, copy,
            FlowSteps.StepLabel(agent, run));
    }

    /// <summary>The progress of request <c>runs[index]</c> (<see cref="FlowSteps.ProgressOf"/>), headed by its request number; null for a notice run and
    /// outside the role view (Mac MobileFlow.progress).</summary>
    public static MobileProgress? Progress(int index, IReadOnlyList<MightyGraphRun> runs, int ordinal, Roles roles)
    {
        if (!roles.FanOut || index < 0 || index >= runs.Count || runs[index].NoticeRun == true) return null;
        var value = FlowSteps.ProgressOf(index, runs, Locale.Get("graph.timeline.requestOrdinal", new Dictionary<string, string> { ["n"] = ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        var rows = value.Rows.Select(row => new MobileProgressRow(row.Index, row.Text, row.State.ToString().ToLowerInvariant(),
            row.StartedAt?.ToUnixTimeMilliseconds(), row.EndedAt?.ToUnixTimeMilliseconds())).ToList();
        return new(value.Title, value.Heading, value.Request, value.StateWord, value.Status, value.Count, rows, value.Hidden,
            value.Hidden > 0 ? Locale.Get("graph.progress.more", new Dictionary<string, string> { ["count"] = value.Hidden.ToString(System.Globalization.CultureInfo.InvariantCulture) }) : null,
            value.Running);
    }

    /// <summary>The line over the rows block <paramref name="key"/> (<see cref="FlowDelegation.MainKey"/>, or a sub-agent's id) delegated to; null when it
    /// delegated to none, and outside the role view.</summary>
    public static MobileDelegateLine? Delegate(string key, MightyGraphRun run, Roles roles)
    {
        if (!roles.FanOut || FlowDelegation.Of(FlowDelegation.Children(key, run), roles.Known) is not { } line) return null;
        return new(line.Head, line.Parts.Count == 0 ? null : line.Parts.Select(p => new MobileInkedText(p.Text, FlowRoles.Token(FlowRoles.Ink(p.Role)))).ToList(),
            line.Model, line.Progress, line.Text);
    }

    /// <summary>The way back at the request's result position, while the result is not there (<see cref="FlowReturn"/>).</summary>
    public static MobileReturn? Returning(MightyGraphRun run, Roles roles) =>
        roles.FanOut && !MightyGraphLayout.HasResult(run) && FlowReturn.Of(run) is { } state ? new(state.Text, state.Waiting) : null;


    /// <summary>The session log strip of a pane's runs (<see cref="FlowLog"/>): its status line in parts and the newest activity's last rows, oldest
    /// first. Null for a pane without a request.</summary>
    public static MobileFlowLog? Log(IReadOnlyList<MightyGraphRun> source, IReadOnlyDictionary<string, AgentDefinition>? definitions = null)
    {
        var runs = MightyGraphSupport.ShownRuns(source);
        if (FlowLog.StatusOf(runs) is not { } status) return null;
        var rows = FlowLog.Rows(runs, definitions: definitions)
            .Select(row => new MobileFlowLogRow(row.Id, row.Time.ToUnixTimeMilliseconds(), row.Who, FlowRoles.Token(row.Ink), row.What)).ToList();
        return new(status.Parts.Select(p => new MobileInkedText(p.Text, FlowRoles.Token(FlowRoles.Ink(p.Role)))).ToList(), rows);
    }
}

/// <summary>A word in its ink (a design token name): a part of the status line or of a delegate line.</summary>
public sealed record MobileInkedText(string Text, string Ink);

/// <summary>Who a block is (<c>MobileBlock.role</c>). <c>Name</c>: lead, subagent or task. <c>Identity</c>: the lead's model and effort, a sub-agent's type
/// and model, a background command's <c>작업 · &lt;command&gt;</c>. <c>Marks</c>: background, fork, worktree. <c>Summary</c>: the whole identity in one line.</summary>
public sealed record MobileBlockRole(string Name, string Ink, string? Identity, IReadOnlyList<string>? Marks, string Summary);

/// <summary>A notice run's copy of a finished background block: where its compact row leads.</summary>
public sealed record MobileCopyRef(string RunId, string BlockId);

/// <summary>The compact row of a sub-agent or a background command (<c>MobileBlock.card</c>, <see cref="FlowCard"/>): what it does now, the figures'
/// words before and after its time, which the phone reads against its own clock from <c>StartedAt</c> (milliseconds since 1970), the sub-agents
/// under it and, once a background block ended, its copy in a notice run.</summary>
public sealed record MobileBlockCard(string? Activity, IReadOnlyList<string> Before, IReadOnlyList<string> After, long? StartedAt, long? EndedAt, int? Nested, MobileCopyRef? Copy,
    string? Step = null);

/// <summary>A request's progress on the phone (<c>MobileMightyRun.progress</c>, <see cref="FlowSteps.Progress"/>): the panel pinned over the session's
/// Mighty view. <c>Count</c>: "2/3", null without a plan; <c>More</c>: the line that counts the steps the rows leave out.</summary>
public sealed record MobileProgress(string Title, string Heading, string Request, string StateWord, string Status, string? Count, IReadOnlyList<MobileProgressRow> Rows,
    int Hidden, string? More, bool Running);

/// <summary>One step of a request's plan: <c>State</c> is done, current, pending, stopped or failed; times in milliseconds since 1970.</summary>
public sealed record MobileProgressRow(int Index, string Text, string State, long? StartedAt, long? EndedAt);

/// <summary>The line over the rows a block delegated to (<c>MobileBlock.delegate</c>, <see cref="FlowDelegation.Line"/>).</summary>
public sealed record MobileDelegateLine(string Head, IReadOnlyList<MobileInkedText>? Parts, string? Model, string? Progress, string Text);

/// <summary>The way back to the main session where a request's result will come (<c>MobileMightyRun.returning</c>, <see cref="FlowReturn"/>).</summary>
public sealed record MobileReturn(string Text, bool Waiting);


/// <summary>The session log strip (<c>MobileMighty.log</c>, <see cref="FlowLog"/>): the status line's parts and the newest activity's last rows.</summary>
public sealed record MobileFlowLog(IReadOnlyList<MobileInkedText> Status, IReadOnlyList<MobileFlowLogRow> Rows);

/// <summary>One row of the session log: when (milliseconds since 1970), who, in whose ink, and what.</summary>
public sealed record MobileFlowLogRow(string Id, long At, string Who, string Ink, string What);
