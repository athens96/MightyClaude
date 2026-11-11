using System.Globalization;

namespace MightyCode.Core;

/// <summary>
/// The line on the branch from a block to the sub-agents it handed work to and the commands it runs in the background: how many, on which model, and
/// how many of them still run. It stands between the block and its fan-out row (<see cref="MightyGraphLayout.Make"/> <c>fanOut</c>) (macOS FlowDelegation).
/// </summary>
public static class FlowDelegation
{
    /// <summary>The line's node in the diagram: as wide as its longest wording needs, one line tall.</summary>
    public const double Width = 340, Height = 24;
    /// <summary>The key of the block every request starts from: its 실행 block (the main session). A sub-agent's key is its id.</summary>
    public const string MainKey = "main";

    /// <summary>One part of a row of both kinds: how many of each, in that role's ink ("하위 에이전트 1", "작업 2").</summary>
    public sealed record Part(FlowRole Role, string Text);

    /// <summary>What the line says. <c>Head</c>: "하위 에이전트 3개에 위임", "작업 2개 실행", or "3개 병렬" for a row of both. <c>Model</c>: the one model all the
    /// sub-agents share ("Haiku 5.5", or "Haiku · 요청" while none has answered), or how many models they are on ("모델 2종"); null when none names a model
    /// and in a row that holds commands. <c>Progress</c>: "2/3 실행 중" while any of them runs.</summary>
    public sealed record Line(int Count, int Running, string Head, IReadOnlyList<Part> Parts, string? Model, string? Progress)
    {
        /// <summary>The whole line, as a screen reader reads it.</summary>
        public string Text => string.Join(" · ", new[] { Head }.Concat(Parts.Select(p => p.Text)).Concat(new[] { Model, Progress }.OfType<string>()));
        public bool Equals(Line? other) => other is not null && Count == other.Count && Running == other.Running && Head == other.Head && Parts.SequenceEqual(other.Parts)
            && Model == other.Model && Progress == other.Progress;
        public override int GetHashCode() => HashCode.Combine(Count, Running, Head, Model, Progress);
    }

    /// <summary>The sub-agents block <paramref name="key"/> of <paramref name="run"/> delegated to and the commands it runs in the background: the ones the
    /// flow draws right under it, in launch order. A copy in a notice run is no delegation, and neither is any other block.</summary>
    public static List<MightyGraphAgent> Children(string key, MightyGraphRun run)
    {
        if (run.NoticeRun == true) return [];
        var agents = run.FlowAgents;
        var ids = agents.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        return Enumerable.Range(0, agents.Count).OrderBy(run.FlowOrder).Select(index => agents[index]).Where(agent =>
        {
            if (!(agent.IsSubagent || agent.IsTask) || agent.SourceBlockId is not null) return false;
            // The layout's own rule for what hangs off the main block: no parent, an unknown one, or itself.
            if (key == MainKey) return agent.ParentID is not { } parent || !ids.Contains(parent) || parent == agent.Id;
            return agent.ParentID == key && agent.Id != key;
        }).ToList();
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The line for <paramref name="children"/>, null when there are none. <paramref name="definitions"/> are the custom agents their types name
    /// (<see cref="AgentDefinitions"/>), whose model stands in for a call that asked for none. A row of sub-agents alone says whom it delegated to and on
    /// which model; a row of commands alone how many it runs; a row of both how many run side by side, and how many of each.</summary>
    public static Line? Of(IReadOnlyList<MightyGraphAgent> children, IReadOnlyDictionary<string, AgentDefinition>? definitions = null)
    {
        if (children.Count == 0) return null;
        var running = children.Count(c => !MightyGraphLayout.Terminal(c.Status));
        var progress = running > 0 ? Locale.Get("graph.delegate.running", new Dictionary<string, string> { ["running"] = N(running), ["total"] = N(children.Count) }) : null;
        var tasks = children.Count(c => c.IsTask);
        var agents = children.Count - tasks;
        if (tasks == 0)
        {
            var head = children.Count == 1 ? Locale.Get("graph.delegate.one") : Locale.Get("graph.delegate.many", new Dictionary<string, string> { ["count"] = N(children.Count) });
            return new(children.Count, running, head, [], Model(children, definitions ?? new Dictionary<string, AgentDefinition>()), progress);
        }
        if (agents == 0)
        {
            var head = tasks == 1 ? Locale.Get("graph.delegate.task") : Locale.Get("graph.delegate.tasks", new Dictionary<string, string> { ["count"] = N(tasks) });
            return new(children.Count, running, head, [], null, progress);
        }
        return new(children.Count, running, Locale.Get("graph.delegate.parallel", new Dictionary<string, string> { ["count"] = N(children.Count) }),
            [new(FlowRole.Subagent, Locale.Get("graph.delegate.partAgents", new Dictionary<string, string> { ["count"] = N(agents) })),
             new(FlowRole.Task, Locale.Get("graph.delegate.partTasks", new Dictionary<string, string> { ["count"] = N(tasks) }))],
            null, progress);
    }

    /// <summary>The model the sub-agents share, or how many they are on. A model the CLI named (a block's own responses, the launch's resolved model) is
    /// read as it is; an alias that was only asked for counts as the same model as a named one of its family ("haiku" beside "Haiku 5.5"), and stands
    /// marked as requested when nothing was named yet.</summary>
    internal static string? Model(IReadOnlyList<MightyGraphAgent> children, IReadOnlyDictionary<string, AgentDefinition> definitions)
    {
        List<string> named = [], asked = [];
        foreach (var agent in children)
        {
            if ((agent.ResponseRecords?.Select(r => r.Model).FirstOrDefault(m => !string.IsNullOrEmpty(m)) ?? agent.Identity?.ResolvedModel) is { } id)
            {
                var label = ModelUsageFormat.ShortName(id, versioned: true);
                if (!named.Contains(label)) named.Add(label);
            }
            else if ((agent.Identity?.RequestedModel ?? (agent.Identity?.Type is { } type ? definitions.GetValueOrDefault(type)?.Model : null)) is { } alias && alias != "inherit")
            {
                var label = ModelLabel.Text(alias);
                if (!asked.Contains(label)) asked.Add(label);
            }
        }
        var unmatched = asked.Where(family => !named.Any(model => model == family || model.StartsWith(family + " ", StringComparison.Ordinal))).ToList();
        return (named.Count + unmatched.Count) switch
        {
            0 => null,
            1 => named.FirstOrDefault() ?? unmatched[0] + " " + Locale.Get("graph.nodeModel.requestedSuffix"),
            var count => Locale.Get("graph.delegate.models", new Dictionary<string, string> { ["count"] = N(count) }),
        };
    }
}

/// <summary>
/// The node at a request's result position before its result is there: what is true of the way back to the main session at that moment. Once the
/// result block exists (the 알림 대기 block included) the branches end in it and this node is gone (macOS FlowReturn).
/// </summary>
public static class FlowReturn
{
    public const double Width = 340, Height = 34;

    /// <summary>The run's sub-agents, at any depth, and its background commands, and how many of them have not ended; the same two of the sub-agents alone
    /// and of the commands alone. <c>Text</c>: "하위 에이전트 2/3 실행 중 · 보고를 기다리는 중", or "병렬 2/3 실행 중 · 보고를 기다리는 중" when commands are among
    /// them, while any runs; "메인으로 복귀 · 검토 중" once all ended and the main session's turn goes on.</summary>
    public sealed record State(int Total, int Running, int Subagents, int SubagentsRunning, int Tasks, int TasksRunning, string Text)
    {
        public bool Waiting => Running > 0;
    }

    /// <summary>The state of <paramref name="run"/>'s way back, null for a run that delegated to no sub-agent and runs no command in the background, and
    /// for a notice run.</summary>
    public static State? Of(MightyGraphRun run)
    {
        if (run.NoticeRun == true) return null;
        var blocks = run.FlowAgents.Where(a => (a.IsSubagent || a.IsTask) && a.SourceBlockId is null).ToList();
        if (blocks.Count == 0) return null;
        var live = blocks.Where(a => !MightyGraphLayout.Terminal(a.Status)).ToList();
        var tasks = blocks.Count(a => a.IsTask);
        var tasksRunning = live.Count(a => a.IsTask);
        var counts = new Dictionary<string, string> { ["running"] = live.Count.ToString(CultureInfo.InvariantCulture), ["total"] = blocks.Count.ToString(CultureInfo.InvariantCulture) };
        var text = live.Count == 0 ? Locale.Get("graph.return.reviewing") : tasks > 0 ? Locale.Get("graph.return.parallel", counts) : Locale.Get("graph.return.waiting", counts);
        return new(blocks.Count, live.Count, blocks.Count - tasks, live.Count - tasksRunning, tasks, tasksRunning, text);
    }
}
