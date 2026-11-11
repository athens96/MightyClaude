namespace MightyCode.Core;

/// <summary>Who works in a flow diagram: the main session that took the request, the sub-agents it delegates to, the commands it runs in the
/// background, and the advisor a Claude session may consult. One ink per role, on every block's header, on the delegate line, in the session log
/// (macOS FlowRole).</summary>
public enum FlowRole
{
    /// <summary>The pane's own agent: a request's 실행 block.</summary>
    Lead,
    /// <summary>An agent another agent started (Claude's Agent tool, a Codex or Antigravity sub-agent).</summary>
    Subagent,
    /// <summary>A shell command a Claude session runs in the background (<c>run_in_background</c> on Bash): a task block.</summary>
    Task,
    /// <summary>The model a Claude session asks for advice (<c>claude --advisor</c>).</summary>
    Advisor,
}

/// <summary>An ink of the design palette a role, or a custom agent's own colour, is drawn in. The flow has no colour of its own: every case is one of
/// concept D's text-safe inks (macOS FlowInk). <see cref="FlowRoles.Token"/> names the design token each draws with.</summary>
public enum FlowInk { Task, Agent, Steer, Accent, Done, Err, Wait, Question, Neutral }

/// <summary>A small mark on a sub-agent's header: how it runs, beside what it is (macOS FlowMark).</summary>
public enum FlowMark
{
    /// <summary>Started in the background (<c>run_in_background</c>, or the CLI's default).</summary>
    Background,
    /// <summary>A fork of the calling session: its whole conversation and its model.</summary>
    Fork,
    /// <summary>Works in a git worktree of its own (<c>isolation: "worktree"</c>).</summary>
    Worktree,
}

/// <summary>What a flow block's header says about who it is (macOS FlowIdentity).</summary>
/// <param name="Type">The sub-agent's type as the CLI named it (<c>subagent_type</c>); null for the lead and for a block that reported none.</param>
/// <param name="TypeChip">The type as the header's chip words it: null when there is nothing to add to the title (no type, a fork, whose mark says it,
/// or a title that is the type already).</param>
/// <param name="ModelLabel">The model before the block's own responses name the one that answered: the model the CLI resolved at launch, else the
/// requested alias marked as requested (<see cref="GraphModelLabel.AgentModelLabel"/>). Null for the lead.</param>
/// <param name="Effort">The lead's effort when the pane named one ("High"); null while the CLI decides and for sub-agents.</param>
/// <param name="Summary">The whole identity in one line, for the header's tooltip and for a screen reader.</param>
public sealed record FlowIdentity(FlowRole Role, FlowInk Ink, string? Type, string? TypeChip, string? ModelLabel, string? Effort, IReadOnlyList<FlowMark> Marks, string Summary)
{
    public bool Equals(FlowIdentity? other) => other is not null && Role == other.Role && Ink == other.Ink && Type == other.Type && TypeChip == other.TypeChip
        && ModelLabel == other.ModelLabel && Effort == other.Effort && Marks.SequenceEqual(other.Marks) && Summary == other.Summary;
    public override int GetHashCode() => HashCode.Combine(Role, Ink, Type, TypeChip, Summary);
}

public static class FlowRoles
{
    /// <summary>The role's ink: teal for the lead, violet for sub-agents (as their blocks always were), the palette's secondary grey for background
    /// commands (which teal would make read as the main session), pink for the advisor. None of them a state's colour (blue runs, amber waits).</summary>
    public static FlowInk Ink(FlowRole role) => role switch
    {
        FlowRole.Lead => FlowInk.Task,
        FlowRole.Subagent => FlowInk.Agent,
        FlowRole.Task => FlowInk.Neutral,
        _ => FlowInk.Steer,
    };

    /// <summary>The design token (<see cref="DesignTokens"/>) an ink draws with: the web page's CSS variable of the same name.</summary>
    public static string Token(FlowInk ink) => ink switch
    {
        FlowInk.Neutral => "ink2",
        FlowInk.Task => "taskText",
        FlowInk.Agent => "agentText",
        FlowInk.Steer => "steerText",
        FlowInk.Accent => "accent",
        FlowInk.Done => "doneText",
        FlowInk.Err => "errText",
        FlowInk.Wait => "waitText",
        _ => "questionText",
    };

    /// <summary>The colours a Claude Code agent definition may name (<c>color:</c> in its frontmatter).</summary>
    public static readonly string[] AgentColors = ["red", "blue", "green", "yellow", "purple", "orange", "pink", "cyan"];

    /// <summary>A custom agent's colour as a palette ink; null for a colour Claude Code does not have. Yellow and orange are the palette's two ambers
    /// (<c>waitText</c>, <c>questionText</c>), which hold the same value today.</summary>
    public static FlowInk? Ink(string? color) => color?.Trim(' ', '\t').ToLowerInvariant() switch
    {
        "red" => FlowInk.Err,
        "blue" => FlowInk.Accent,
        "green" => FlowInk.Done,
        "yellow" => FlowInk.Wait,
        "purple" => FlowInk.Agent,
        "orange" => FlowInk.Question,
        "pink" => FlowInk.Steer,
        "cyan" => FlowInk.Task,
        _ => null,
    };

    /// <summary>A custom agent's own colour takes the place of the sub-agent ink; the lead and the advisor keep theirs.</summary>
    public static FlowInk Ink(FlowRole role, string? color) => role == FlowRole.Subagent ? Ink(color) ?? Ink(role) : Ink(role);

    public static string RoleName(FlowRole role) => role switch
    {
        FlowRole.Lead => Locale.Get("graph.identity.role.lead"),
        FlowRole.Subagent => Locale.Get("graph.block.agent"),
        FlowRole.Task => Locale.Get("graph.identity.role.task"),
        _ => Locale.Get("graph.identity.role.advisor"),
    };

    /// <summary>What a mark says on hover, and to a screen reader.</summary>
    public static string Help(FlowMark mark) => Locale.Get(mark switch { FlowMark.Background => "graph.identity.mark.background", FlowMark.Fork => "graph.identity.mark.fork", _ => "graph.identity.mark.worktree" });
    /// <summary>One word for the identity sentence.</summary>
    public static string Word(FlowMark mark) => Locale.Get(mark switch { FlowMark.Background => "graph.identity.word.background", FlowMark.Fork => "graph.identity.word.fork", _ => "graph.identity.word.worktree" });

    /// <summary>An effort level as the composer names it; null for "default" (the CLI decides) and for a word that is no level.</summary>
    public static string? EffortName(string? effort) => effort switch
    {
        "off" => "Off", "minimal" => "Minimal", "low" => "Low", "medium" => "Medium", "high" => "High", "xhigh" => "XHigh", "max" => "Max", _ => null,
    };

    /// <summary>A type as a chip words it: a plugin's agent (<c>plugin:agent</c>) by its own name, any other as it is.</summary>
    public static string TypeName(string type)
    {
        var colon = type.LastIndexOf(':');
        return colon < 0 || colon + 1 >= type.Length ? type : type[(colon + 1)..];
    }

    /// <summary>The marks of a sub-agent block, in a fixed order.</summary>
    public static List<FlowMark> Marks(MightyGraphAgent agent)
    {
        var marks = new List<FlowMark>();
        if (agent.Background == true) marks.Add(FlowMark.Background);
        if (agent.Identity?.IsFork == true) marks.Add(FlowMark.Fork);
        if (agent.Identity?.Isolation == GraphAgentIdentity.Isolations[0]) marks.Add(FlowMark.Worktree);
        return marks;
    }

    /// <summary>The main session's identity on a run's 실행 block: the lead's ink, the pane's effort when it named one. Its model stays the run's own
    /// label and capsule.</summary>
    public static FlowIdentity Lead(MightyGraphRun run)
    {
        var effort = EffortName(run.ConfiguredEffort);
        var parts = new List<string> { RoleName(FlowRole.Lead) };
        if (effort is not null) parts.Add(Locale.Get("composer.effort.menu", new Dictionary<string, string> { ["effort"] = effort }));
        return new(FlowRole.Lead, Ink(FlowRole.Lead), null, null, null, effort, [], string.Join(" · ", parts));
    }

    /// <summary>A sub-agent block's identity; null for a block that is no sub-agent (a background run, a steer, a context compaction, a question).
    /// <paramref name="definition"/> is the custom agent its type names, when one was found (<see cref="AgentDefinitions"/>): its colour takes the
    /// sub-agent ink's place and its model stands in for a call that asked for none.</summary>
    public static FlowIdentity? Subagent(MightyGraphAgent agent, AgentDefinition? definition = null)
    {
        if (!agent.IsSubagent) return null;
        var identity = agent.Identity;
        var type = identity?.Type;
        var marks = Marks(agent);
        var title = MightyGraphSupport.BlockTitle(agent);
        string? chip = null;
        if (type is not null)
        {
            var name = TypeName(type);
            chip = identity?.IsFork == true || type == title || name == title ? null : name;
        }
        var model = GraphModelLabel.AgentModelLabel(identity?.ResolvedModel, identity?.RequestedModel ?? definition?.Model);
        var parts = new List<string> { RoleName(FlowRole.Subagent) };
        if (type is not null && identity?.IsFork != true) parts.Add(type);
        if (identity?.ResolvedModel is { } resolved) parts.Add(MightyCode.Core.ModelLabel.Text(resolved));
        else if ((identity?.RequestedModel ?? definition?.Model) is { } requested && requested != "inherit")
            parts.Add(Locale.Get("graph.identity.requestedModel", new Dictionary<string, string> { ["model"] = MightyCode.Core.ModelLabel.Text(requested) }));
        parts.AddRange(marks.Select(Word));
        return new(FlowRole.Subagent, Ink(FlowRole.Subagent, definition?.Color), type, chip, model, null, marks, string.Join(" · ", parts));
    }

    /// <summary>A background command's identity in the role view: the neutral ink, its background mark, and in words its role and the first line of
    /// its command. Null for a block that is no background command.</summary>
    public static FlowIdentity? Task(MightyGraphAgent agent)
    {
        if (!agent.IsTask) return null;
        var marks = Marks(agent);
        var parts = new List<string> { RoleName(FlowRole.Task) };
        parts.AddRange(marks.Select(Word));
        if (FlowCard.Line(FlowCard.FirstLine(MightyGraphSupport.ShownInput(agent))) is { } command) parts.Add(command);
        return new(FlowRole.Task, Ink(FlowRole.Task), null, null, null, null, marks, string.Join(" · ", parts));
    }

    /// <summary>The types the blocks of <paramref name="runs"/> name, for looking their definitions up (<see cref="AgentDefinitions.Load"/>).</summary>
    public static HashSet<string> Types(IEnumerable<MightyGraphRun> runs)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var run in runs) foreach (var agent in run.FlowAgents) if (agent.Identity?.Type is { } type) types.Add(type);
        return types;
    }

    // ── Header room ──

    /// <summary>Which of a header's identity chips fit beside what the header always showed. <c>TypeWidth</c>: the type chip's most width; null
    /// leaves the chip out.</summary>
    public sealed record HeaderFit(double? TypeWidth, bool ShowsEffort, bool ShowsMarks)
    {
        public static readonly HeaderFit None = new(null, false, false);
    }

    /// <summary>The width a short label takes at <paramref name="size"/> points, near enough to decide whether a chip has room: a wide (Hangul, kana,
    /// Han) character a full em, any other a little over half.</summary>
    public static double EstimatedWidth(string text, double size, bool monospaced = false)
    {
        double width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            var wide = value is >= 0x1100 and <= 0x11FF or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF or >= 0xFF00 and <= 0xFFEF;
            width += size * (wide ? 1 : monospaced ? 0.61 : 0.56);
        }
        return width;
    }

    /// <summary>The least a block's title keeps before any identity chip is drawn.</summary>
    public const double TitleFloor = 72;
    /// <summary>A type chip narrower than this says nothing.</summary>
    public const double TypeFloor = 40;
    public const double TypeCeiling = 110;
    public const double MarkWidth = 14;
    public const double ChipPadding = 2 * DesignMetrics.Spacing.Sm;

    /// <summary>What fits in a block header <paramref name="width"/> wide. The title, the status mark, the token capsule and the buttons were there first
    /// and keep their room (the title at least <see cref="TitleFloor"/>, or its own width when it is shorter); what is left goes to the marks, then the
    /// effort, then the type chip, which takes the width given here (its type cut inside it) or is left out. A header at the block's least width that
    /// holds a status and a capsule shows none of them: the identity stays on the icon's tooltip (macOS FlowRoles.headerFit).</summary>
    public static HeaderFit FitHeader(double width, string title, string? status, string? capsule, int buttons, FlowIdentity identity)
    {
        const double gap = DesignMetrics.Spacing.Sm;
        var used = 2 * DesignMetrics.Inset.GraphBlockBodyH + 16 + gap;
        used += Math.Min(TitleFloor, EstimatedWidth(title, 12)) + gap;
        // The activity bars, the status pill and the capsule, each with the gap after it.
        if (status is not null) used += 18 + gap + EstimatedWidth(status, 10) + ChipPadding + gap;
        if (capsule is not null) used += EstimatedWidth(capsule, 10, true) + ChipPadding + gap;
        used += buttons * (16 + gap);
        var free = width - used;
        double? typeWidth = null; bool showsEffort = false, showsMarks = false;
        if (identity.Marks.Count > 0)
        {
            var marks = identity.Marks.Count * MarkWidth + gap;
            if (free >= marks) { showsMarks = true; free -= marks; }
        }
        if (identity.Effort is { } effort)
        {
            var chip = MarkWidth + EstimatedWidth(effort, 10) + gap;
            if (free >= chip) { showsEffort = true; free -= chip; }
        }
        if (identity.TypeChip is { } type && free >= TypeFloor + gap)
        {
            // A little over the estimate, so a type of wide letters is not cut while there is room.
            var wanted = Math.Ceiling(EstimatedWidth(type, 10) * 1.15 + ChipPadding + 4);
            typeWidth = Math.Max(TypeFloor, Math.Min(TypeCeiling, Math.Min(free - gap, wanted)));
        }
        return new(typeWidth, showsEffort, showsMarks);
    }
}
