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

    /// <summary>The run id <paramref name="noticeRun"/> is a notice run of: the request whose tracker opened it, or null for a request.</summary>
    public static string? OriginalRunID(string noticeRun) => noticeRun.IndexOf(Marker, StringComparison.Ordinal) is var at and >= 0 ? noticeRun[..at] : null;

    /// <summary>One background task or sub-agent of a request awaiting its notices: its block, title and status (running, or how it ended).</summary>
    public sealed record AwaitedTask(string Id, string Title, string Status)
    {
        /// <summary>Its notice is in: the work ended (and told Claude so), whatever its outcome.</summary>
        public bool Delivered => MightyGraphSupport.Terminal(Status);
        /// <summary>What its row says: 끝나면 알림 while it runs, 알림 전달됨 once it ended, 실패 or 중단됨 when it ended so (macOS AwaitedTask.word).</summary>
        public string Word => Status switch
        {
            "error" => Locale.Get("graph.block.taskNotice.failed"),
            "stopped" => Locale.Get("graph.block.taskNotice.stopped"),
            "completed" => Locale.Get("plan.background.noticeDelivered"),
            _ => Locale.Get("plan.background.awaitingNotice"),
        };
    }
    /// <summary>A request awaiting the notices of the background work it started (<see cref="AwaitingNotice"/>): every such task, in block order.</summary>
    public sealed record Awaiting(IReadOnlyList<AwaitedTask> Tasks)
    {
        public int Delivered => Tasks.Count(task => task.Delivered);
        /// <summary>"알림 {delivered}/{total}".</summary>
        public string Progress => Locale.Get("graph.block.awaitingNotice.progress", new Dictionary<string, string>
            { ["delivered"] = Delivered.ToString(CultureInfo.InvariantCulture), ["total"] = Tasks.Count.ToString(CultureInfo.InvariantCulture) });
        public bool Equals(Awaiting? other) => other is not null && Tasks.SequenceEqual(other.Tasks);
        public override int GetHashCode() => Tasks.Count;
    }
    /// <summary>
    /// The one awaiting-notice rule (macOS BackgroundNoticeRuns.awaitingNotice): a request whose own turn ended completed while background work it
    /// started (its tasks and background sub-agents) still runs, or ended with a notice turn not answered yet: a notice run of this request that
    /// copies the block has not ended. It ends when every notice turn was answered; a failed or stopped request, or one whose process ended
    /// (its blocks and notice runs stopped), is not awaiting. Null otherwise and for a notice run. <paramref name="runs"/> are the pane's runs, for
    /// the notice runs; a run <see cref="MightyGraphRun.Shown"/> made carries what it read of the run it was made from.
    /// </summary>
    public static Awaiting? AwaitingNotice(MightyGraphRun run, IReadOnlyList<MightyGraphRun> runs)
    {
        if (run.ShownBackgroundWaiting is not null) return run.ShownAwaiting;
        if (run.NoticeRun == true || run.Status != "completed") return null;
        var tasks = run.Agents.Where(agent => agent.IsTask || agent.IsBackgroundAgent).ToList();
        if (tasks.Count == 0) return null;
        var answering = runs.Where(notice => notice.NoticeRun == true && !MightyGraphSupport.Terminal(notice.Status) && OriginalRunID(notice.Id) == run.Id)
            .SelectMany(notice => notice.Agents).Select(copy => copy.SourceBlockId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (!tasks.Any(task => !MightyGraphSupport.Terminal(task.Status) || answering.Contains(task.Id))) return null;
        return new(tasks.Select(task => new AwaitedTask(task.Id, MightyGraphSupport.BlockTitle(task), task.Status)).ToList());
    }

    /// <summary>The pane is alive only for the notices its newest request awaits (<see cref="AwaitingNotice"/>): the pet, its status list and the
    /// pane header show it waiting, 알림 대기, instead of running. Its status stays running, so the completion notification still comes once,
    /// when the process ends (macOS BackgroundNoticeRuns.awaitsNotice).</summary>
    public static bool AwaitsNotice(RunSession pane) => pane.Status == "running" && pane.GraphRuns is { } runs
        && MightyGraphSupport.NewestRequestRun(runs) is { } newest && AwaitingNotice(newest, runs) is not null;
    /// <summary>The word for a pane that <see cref="AwaitsNotice"/>.</summary>
    public static string StateWord => Locale.Get("graph.state.awaitingNotice");

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
