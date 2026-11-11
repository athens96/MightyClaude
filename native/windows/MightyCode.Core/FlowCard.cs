using System.Globalization;
using System.Text.RegularExpressions;

namespace MightyCode.Core;

/// <summary>
/// A sub-agent, or a command the session runs in the background, as the flow diagram's compact card: who is working, what it is doing right now
/// and how far it is, in four lines. A running sub-agent is a card; one that ran in the foreground becomes its full block when it ends, as it always
/// was, and one that ran in the background stays a card, since its full block is the copy in its notice run (<see cref="BackgroundNoticeRuns"/>). A
/// background command is a card from its launch on, for the same reason. Every figure on it is one the app observed: the time since it saw the agent
/// start, the tool calls it counted, the tokens the CLI reported, the exit code the CLI named. Nothing is estimated and nothing is a percentage: an
/// agent does not say how much of its work is left (macOS FlowCard).
/// </summary>
public static class FlowCard
{
    /// <summary>The card's size in the diagram. It is never dragged: opened, the block has its own saved size.</summary>
    public const double Width = 236, Height = 86;

    /// <summary>Whether the flow draws sub-agent <paramref name="agent"/> as a card. <paramref name="opened"/>: the user opened it into its full block,
    /// which stays open until they fold it back. A copy in a notice run is always the full block, and so is a block that is no sub-agent.</summary>
    public static bool IsCompact(MightyGraphAgent agent, bool opened) => FoldsToCard(agent) && !opened;

    /// <summary>Whether the block has a card to fold back to: a request's own background sub-agent or background command at any time, a foreground
    /// sub-agent while it runs.</summary>
    public static bool FoldsToCard(MightyGraphAgent agent)
    {
        if (agent.SourceBlockId is not null) return false;
        if (agent.IsTask) return true;
        if (!agent.IsSubagent) return false;
        return agent.Background == true || !MightyGraphLayout.Terminal(agent.Status);
    }

    /// <summary>What a card says. <c>Title</c>: the block's title. <c>Identity</c>: "Explore · Haiku 5.5", or a command's "작업 · &lt;command&gt;".
    /// <c>Activity</c>: what it is doing now while it runs, what it answered once it ended (a command's last line). <c>Status</c>/<c>StateWord</c>: its
    /// status bucket and word. <c>Nested</c>: sub-agents under it, which a card counts instead of drawing.</summary>
    public sealed record Content(string Title, string? Identity, string? Activity, string Status, string StateWord, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt,
        int? ToolCalls, long? Tokens, int Nested, IReadOnlyList<FlowMark> Marks, FlowInk Ink, string Summary, bool IsTask = false, int? ExitCode = null)
    {
        public bool Running => !MightyGraphLayout.Terminal(Status);
        public bool Equals(Content? other) => other is not null && Title == other.Title && Identity == other.Identity && Activity == other.Activity && Status == other.Status
            && StateWord == other.StateWord && StartedAt == other.StartedAt && EndedAt == other.EndedAt && ToolCalls == other.ToolCalls && Tokens == other.Tokens
            && Nested == other.Nested && Marks.SequenceEqual(other.Marks) && Ink == other.Ink && Summary == other.Summary && IsTask == other.IsTask && ExitCode == other.ExitCode;
        public override int GetHashCode() => HashCode.Combine(Title, Identity, Activity, Status, Nested, Ink);
    }

    /// <summary>The card of <paramref name="agent"/>, a block of <paramref name="run"/>. <paramref name="definition"/> is the custom agent its type names
    /// (<see cref="AgentDefinitions"/>); <paramref name="output"/> the last lines a background command wrote (<see cref="BackgroundOutputTail"/>), which
    /// the app reads apart.</summary>
    public static Content Of(MightyGraphAgent agent, MightyGraphRun run, AgentDefinition? definition = null, IReadOnlyList<string>? output = null)
    {
        if (FlowRoles.Task(agent) is { } task) return TaskContent(agent, task, output ?? []);
        var who = FlowRoles.Subagent(agent, definition);
        var status = MobileMightySupport.Status(agent.Status);
        var running = !MightyGraphLayout.Terminal(status);
        return new(MightyGraphSupport.BlockTitle(agent), IdentityLine(who, agent), running ? Doing(agent) : Outcome(agent), status, StateWord(status),
            agent.Figures?.Started, agent.Figures?.Ended, agent.Figures?.ToolCalls, agent.Usage is { IsEmpty: false } usage ? usage.Total : null,
            Nested(agent, run), who?.Marks ?? [], who?.Ink ?? FlowRoles.Ink(FlowRole.Subagent), who?.Summary ?? "");
    }

    /// <summary>A background command's card: its description (else its command), <c>작업 · &lt;the command's first line&gt;</c>, the last line it wrote,
    /// and its state with its exit code and time.</summary>
    private static Content TaskContent(MightyGraphAgent agent, FlowIdentity who, IReadOnlyList<string> output)
    {
        var status = MobileMightySupport.Status(agent.Status);
        var command = Line(FirstLine(MightyGraphSupport.ShownInput(agent))) is { } line ? Locale.Get("graph.card.task", new Dictionary<string, string> { ["command"] = line }) : FlowRoles.RoleName(FlowRole.Task);
        return new(MightyGraphSupport.BlockTitle(agent), command, LastOutput(output), status, StateWord(status), agent.Figures?.Started, agent.Figures?.Ended, null, null,
            0, who.Marks, who.Ink, who.Summary, true, agent.Figures?.ExitCode);
    }

    private static readonly Regex ExitedLine = new(@"^\[exited with code -?[0-9]+\]$");

    /// <summary>The last line a background command wrote, without the line Claude Code adds once it exits (<c>[exited with code 0]</c>): its exit code
    /// is the card's figure.</summary>
    public static string? LastOutput(IReadOnlyList<string> lines)
    {
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            var text = lines[index].Trim(' ', '\t');
            if (text.Length == 0 || ExitedLine.IsMatch(text)) continue;
            return Line(text);
        }
        return null;
    }

    /// <summary>"Explore · Haiku 5.5": a sub-agent's type and its model, whichever is known. The model is the one its own responses name, else the one
    /// the CLI resolved at launch, else the alias that was asked for, marked as requested. A fork has no type to name: its mark says it.</summary>
    public static string? IdentityLine(MightyGraphAgent agent, AgentDefinition? definition = null) => IdentityLine(FlowRoles.Subagent(agent, definition), agent);
    internal static string? IdentityLine(FlowIdentity? who, MightyGraphAgent agent)
    {
        var answered = agent.ResponseRecords?.Select(r => r.Model).FirstOrDefault(m => !string.IsNullOrEmpty(m));
        var model = answered is not null ? ModelUsageFormat.ShortName(answered, versioned: true) : ModelUsageFormat.VersionedNodeLabel(who?.ModelLabel);
        var type = who?.Type is { } named && !who.Marks.Contains(FlowMark.Fork) ? FlowRoles.TypeName(named) : null;
        var parts = new[] { type, model }.OfType<string>().ToList();
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>The most a card's line holds; the view cuts what does not fit its width.</summary>
    internal const int LineBytes = 240;

    /// <summary>What a running agent is doing right now: its latest tool call as the transcript words it (its summary, else its tool's name), else the
    /// last line it wrote, else the first line of what it was asked. The run's own lifecycle rows are not tool calls.</summary>
    public static string? Doing(MightyGraphAgent agent)
    {
        for (var index = agent.Entries.Count - 1; index >= 0; index--)
        {
            if (agent.Entries[index].Activity is not { } activity || activity.Kind == "turn") continue;
            if (Line(activity.Summary.Length == 0 ? activity.ToolName ?? "" : activity.Summary) is { } line) return line;
        }
        for (var index = agent.Entries.Count - 1; index >= 0; index--)
            if (agent.Entries[index].Kind == "assistant" && Line(LastLine(agent.Entries[index].Text)) is { } line) return line;
        return Line(FirstLine(MightyGraphSupport.ShownInput(agent)));
    }

    /// <summary>What an ended agent has to say in one line: the first line of its answer (the last reply its block holds), else of the last thing it
    /// recorded.</summary>
    public static string? Outcome(MightyGraphAgent agent)
    {
        if (agent.Entries.LastOrDefault(e => e.Kind == "assistant" && e.Text.Length > 0)?.Text is { } answer && Line(FirstLine(answer)) is { } first) return first;
        for (var index = agent.Entries.Count - 1; index >= 0; index--)
            if (agent.Entries[index].Activity is null && Line(FirstLine(agent.Entries[index].Text)) is { } line) return line;
        return null;
    }

    internal static string? Line(string raw) => ActivitySupport.Clean(raw, LineBytes, true) is { Length: > 0 } clean ? clean : null;
    private static readonly char[] Breaks = ['\n', '\r', '\v', '\f', '\u0085', '\u2028', '\u2029'];
    internal static string FirstLine(string text) => text.Split(Breaks).FirstOrDefault(line => line.Trim(' ', '\t').Length > 0) ?? "";
    internal static string LastLine(string text) => text.Split(Breaks).LastOrDefault(line => line.Trim(' ', '\t').Length > 0) ?? "";

    /// <summary>A status bucket in the diagram's own words (<c>graph.state.*</c>).</summary>
    public static string StateWord(string status) => Locale.Get(status switch
    {
        "completed" => "graph.state.completed",
        "error" or "failed" => "graph.state.error",
        "stopped" or "cancelled" or "interrupted" => "graph.state.stopped",
        "waiting" => "graph.state.waiting",
        _ => "graph.state.running",
    });

    /// <summary>The sub-agents under <paramref name="agent"/>, at any depth: the "(+N)" a card shows in their place.</summary>
    public static int Nested(MightyGraphAgent agent, MightyGraphRun run)
    {
        var agents = run.FlowAgents;
        var under = new HashSet<string>(StringComparer.Ordinal) { agent.Id };
        var count = 0;
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var candidate in agents)
            {
                if (under.Contains(candidate.Id) || candidate.ParentID is not { } parent || !under.Contains(parent)) continue;
                under.Add(candidate.Id); grew = true;
                if (candidate.IsSubagent) count += 1;
            }
        }
        return count;
    }

    /// <summary>The card's last line before and after its time: its state (and a command's exit code), then the tools and the tokens. The page puts the
    /// time between them, read against its own clock once a second (<see cref="Figures"/> is the whole line).</summary>
    public static (List<string> Before, List<string> After) FigureParts(Content content)
    {
        var before = new List<string> { content.StateWord };
        // A command that ended says how it exited, before how long it took.
        if (content.IsTask && !content.Running && content.ExitCode is { } code) before.Add(Locale.Get("graph.card.exitCode", new Dictionary<string, string> { ["code"] = code.ToString(CultureInfo.InvariantCulture) }));
        var after = new List<string>();
        if (content.ToolCalls is > 0 and var tools) after.Add(Locale.Get("graph.card.tools", new Dictionary<string, string> { ["count"] = tools.ToString(CultureInfo.InvariantCulture) }));
        if (content.Tokens is { } tokens) after.Add(Locale.Get("graph.usage.summary", new Dictionary<string, string> { ["total"] = GraphTokenUsage.Compact(tokens) }));
        return (before, after);
    }

    /// <summary>The card's time: while it runs the time since it started, read against <paramref name="now"/>; once it ended, how long it ran. Null when
    /// the app does not have it.</summary>
    public static string? Elapsed(Content content, DateTimeOffset now)
    {
        if (content.StartedAt is not { } start) return null;
        if (content.Running) return StylePresentation.Elapsed(start, now);
        return content.EndedAt is { } end && end >= start ? StylePresentation.Elapsed(start, end) : null;
    }

    /// <summary>The card's last line: its state, then the figures it has (macOS FlowCard.figures).</summary>
    public static string Figures(Content content, DateTimeOffset now)
    {
        var (before, after) = FigureParts(content);
        var parts = new List<string>(before);
        if (Elapsed(content, now) is { } time) parts.Add(time);
        parts.AddRange(after);
        return string.Join(" · ", parts);
    }

    /// <summary>The node of <paramref name="agent"/>'s full copy in a notice run (<see cref="ExecutionGraphNode.SourceBlockId"/>), the newest when a later
    /// turn copied it again: where a finished background agent's card leads. Null while no notice run holds one.</summary>
    public static string? CopyNodeID(MightyGraphAgent agent, IReadOnlyList<MightyGraphRun> runs)
    {
        for (var index = runs.Count - 1; index >= 0; index--)
        {
            if (runs[index].NoticeRun != true) continue;
            if (runs[index].Agents.LastOrDefault(copy => copy.SourceBlockId == agent.Id) is { } copied) return MightyGraphLayout.NodeID(runs[index], "agent:" + copied.Id);
        }
        return null;
    }
}
