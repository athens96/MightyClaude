using System.Globalization;

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
    public static string RunID(string original, int index) => original + Marker + index.ToString(CultureInfo.InvariantCulture);
    public static bool IsNoticeRun(string runID) => runID.Contains(Marker, StringComparison.Ordinal);
    /// <summary>What opened a notice run; it names the run's kind while the run holds no copied block.</summary>
    public enum Opener { Work, Message, Delegation }
    /// <summary>The longest name a notice run's title keeps; a longer one is cut to one less and "…".</summary>
    public const int NameLimit = 32;
    /// <summary>A notice run's heading when it has no title of its own (older saves), where a request has "요청 n".</summary>
    public static string GenericTitle => Locale.Get("graph.background.noticeRun");
    /// <summary>The kind a notice run's title opens with: finished work, a sub-agent's message, or a delegation notice.</summary>
    public static string Kind(Opener opener) => opener switch
    {
        Opener.Message => Locale.Get("graph.background.messageRun"),
        Opener.Delegation => Locale.Get("graph.block.delegationNotice"),
        _ => Locale.Get("graph.background.noticeRun"),
    };
    /// <summary>The line breaks a name's first line ends at (macOS <c>CharacterSet.newlines</c>).</summary>
    private static readonly char[] LineBreaks = ['\n', '\v', '\f', '\r', '\u0085', '\u2028', '\u2029'];
    /// <summary>"&lt;kind&gt; · &lt;name&gt;", "&lt;kind&gt; · &lt;first&gt; 외 &lt;n&gt;개", or "&lt;kind&gt;" alone. Each name keeps its first line, cut to
    /// <see cref="NameLimit"/> characters; equal names are counted, not merged (macOS BackgroundNoticeRuns.title(kind:names:)).</summary>
    public static string Title(string kind, IReadOnlyList<string> names)
    {
        var lines = names.Select(n => n.Split(LineBreaks, 2)[0].Trim()).Where(n => n.Length > 0).ToList();
        if (lines.Count == 0) return kind;
        var first = new StringInfo(lines[0]);
        var shortName = first.LengthInTextElements > NameLimit ? first.SubstringByTextElements(0, NameLimit - 1) + "…" : lines[0];
        var name = lines.Count == 1 ? shortName : Locale.Get("graph.background.noticeRunMore", new Dictionary<string, string> { ["first"] = shortName, ["count"] = (lines.Count - 1).ToString(CultureInfo.InvariantCulture) });
        return Locale.Get("graph.background.noticeRunTitle", new Dictionary<string, string> { ["kind"] = kind, ["name"] = name });
    }
    /// <summary>A notice run's heading: its own title, or the generic one (older saves).</summary>
    public static string Heading(MightyGraphRun run) => Heading(run.NoticeTitle);
    /// <summary>The heading for a carried notice title (<see cref="MightyTimeline.Group"/>'s, <see cref="MightyStructure.Item"/>'s): the title
    /// trimmed, or the generic one when it is absent or blank (macOS BackgroundNoticeRuns.heading(title:)).</summary>
    public static string Heading(string? title) => string.IsNullOrWhiteSpace(title) ? GenericTitle : title.Trim();
    /// <summary>The symbol in front of a notice run's heading: the task block's.</summary>
    public const string Icon = "terminal";
    /// <summary>The 실행 block's title: "실행 · 요청 n", or plain "실행" in a notice run, which is no request.</summary>
    public static string ExecutionTitle(bool notice, int ordinal) => notice ? Locale.Get("graph.block.execution")
        : Locale.Get("graph.block.executionOf", new Dictionary<string, string> { ["n"] = ordinal.ToString(CultureInfo.InvariantCulture) });

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
