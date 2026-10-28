namespace MightyClaude.Core;

/// <summary>
/// Finished background work gets a diagram of its own. When a root-level <c>&lt;task-notification&gt;</c> (or a background
/// sub-agent's hand-back) opens a Claude turn, the graph tracker records that turn as a new run, <c>&lt;original&gt;:notice:&lt;n&gt;</c>:
/// its main block names the finished work and holds Claude's whole reply, and its children are copies of the finished background
/// blocks (<see cref="ExecutionGraphNode.SourceBlockId"/>). The request that started the work keeps its background blocks as data,
/// but no view draws them there (macOS BackgroundNoticeRuns).
/// </summary>
public static class BackgroundNoticeRuns
{
    public const string Marker = ":notice:";
    /// <summary>The run id of the original run's <paramref name="index"/>th notice run (from 1).</summary>
    public static string RunID(string original, int index) => original + Marker + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static bool IsNoticeRun(string runID) => runID.Contains(Marker, StringComparison.Ordinal);
    /// <summary>A notice run's heading, where a request has "요청 n".</summary>
    public static string Title => Locale.Get("graph.background.noticeRun");
    /// <summary>The symbol in front of a notice run's heading: the task block's.</summary>
    public const string Icon = "terminal";
    /// <summary>The 실행 block's title: "실행 · 요청 n", or plain "실행" in a notice run, which is no request.</summary>
    public static string ExecutionTitle(bool notice, int ordinal) => notice ? Locale.Get("graph.block.execution")
        : Locale.Get("graph.block.executionOf", new Dictionary<string, string> { ["n"] = ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    /// <summary>Agents a view draws for a run: background blocks only inside a notice run. Blocks under a hidden background block are
    /// hidden with it. The run's own list when nothing is hidden.</summary>
    public static List<MightyGraphAgent> ShownAgents(MightyGraphRun run)
    {
        if (run.NoticeRun == true || !run.Agents.Any(agent => agent.Background == true)) return run.Agents;
        var hidden = run.Agents.Where(agent => agent.Background == true).Select(agent => agent.Id).ToHashSet(StringComparer.Ordinal);
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var agent in run.Agents)
                if (!hidden.Contains(agent.Id) && agent.ParentID is { } parent && hidden.Contains(parent)) { hidden.Add(agent.Id); grew = true; }
        }
        return run.Agents.Where(agent => !hidden.Contains(agent.Id)).ToList();
    }
}
