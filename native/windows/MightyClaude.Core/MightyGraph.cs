using System.Text;
using System.Text.Json.Serialization;

namespace MightyClaude.Core;

public sealed class MightyGraphAgent
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("parentID")] public string? ParentID { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("input")] public string Input { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "running";
    [JsonPropertyName("entries")] public List<LogEntry> Entries { get; set; } = [];
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("usage")] public GraphTokenUsage? Usage { get; set; }
    [JsonPropertyName("activityGeneration")] public int? ActivityGeneration { get; set; }
    [JsonPropertyName("responseRecords")] public List<GraphResponseRecord>? ResponseRecords { get; set; }
    /// <summary>Started in the background (a background sub-agent; every task block). See <see cref="ExecutionGraphNode.Background"/>.</summary>
    [JsonPropertyName("background")] public bool? Background { get; set; }
    /// <summary>A task block's output file: read for its last lines, never shown or sent to the phone.</summary>
    [JsonPropertyName("outputFile")] public string? OutputFile { get; set; }
    /// <summary>Claude's answer in the turn this block's notice opened alone.</summary>
    [JsonPropertyName("noticeReply")] public string? NoticeReply { get; set; }

    [JsonIgnore] public bool IsTask => Kind == "task";
    /// <summary>A sub-agent started with <c>run_in_background</c> (macOS MightyGraphAgent.isBackgroundAgent).</summary>
    [JsonIgnore] public bool IsBackgroundAgent => Kind is null && Background == true;

    /// A private copy: the runs of one session snapshot must never be mutated
    /// through another snapshot that still refers to them.
    public MightyGraphAgent Copy() => new()
    {
        Id = Id, ParentID = ParentID, Title = Title, Input = Input, Status = Status, Entries = [.. Entries],
        Kind = Kind, Usage = Usage, ActivityGeneration = ActivityGeneration,
        ResponseRecords = ResponseRecords is null ? null : [.. ResponseRecords],
        Background = Background, OutputFile = OutputFile, NoticeReply = NoticeReply,
    };
}

public sealed class MightyGraphRun
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("input")] public string Input { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "running";
    [JsonPropertyName("rootEntries")] public List<LogEntry> RootEntries { get; set; } = [];
    [JsonPropertyName("agents")] public List<MightyGraphAgent> Agents { get; set; } = [];
    [JsonPropertyName("resultEntries")] public List<LogEntry> ResultEntries { get; set; } = [];
    [JsonPropertyName("sourceRunID")] public string? SourceRunID { get; set; }
    [JsonPropertyName("finalOutput")] public string? FinalOutput { get; set; }
    [JsonPropertyName("usage")] public GraphTokenUsage? Usage { get; set; }
    [JsonPropertyName("responseRecords")] public List<GraphResponseRecord>? ResponseRecords { get; set; }
    [JsonPropertyName("provider")] public string? Provider { get; set; }
    [JsonPropertyName("nodeModelLabel")] public string? NodeModelLabel { get; set; }
    [JsonPropertyName("configuredModel")] public string? ConfiguredModel { get; set; }

    public MightyGraphRun Copy() => new()
    {
        Id = Id, Input = Input, Status = Status, RootEntries = [.. RootEntries], Agents = [.. Agents.Select(a => a.Copy())],
        ResultEntries = [.. ResultEntries], SourceRunID = SourceRunID, FinalOutput = FinalOutput, Usage = Usage,
        ResponseRecords = ResponseRecords is null ? null : [.. ResponseRecords], Provider = Provider,
        NodeModelLabel = NodeModelLabel, ConfiguredModel = ConfiguredModel,
    };
    /// Tokens of the main block alone; this adds every child block.
    [JsonIgnore] public GraphTokenUsage? TotalUsage
    {
        get
        {
            var sum = Agents.Aggregate(Usage ?? new GraphTokenUsage(), (total, agent) => total + (agent.Usage ?? new GraphTokenUsage()));
            return sum.IsEmpty ? null : sum;
        }
    }
    [JsonIgnore] public bool Settled => MightyGraphSupport.Terminal(Status) && Agents.All(a => MightyGraphSupport.Terminal(a.Status));
}

public static class MightyGraphSupport
{
    /// <summary>AI panes whose runs produce a graph: Claude via stream-json + Mods, Codex via exec JSONL, Antigravity (<c>gemini</c>) via agy's stream-json and its sub-agents' transcripts.</summary>
    public static readonly string[] Providers = ["claude", "codex", "gemini"];

    public static bool Terminal(string status) => status is "completed" or "error" or "stopped";

    public static string BlockKind(MightyGraphAgent agent) =>
        agent.Kind is "task" or "steer" or "compact" or "question" ? agent.Kind : "agent";

    public static string BlockTitle(MightyGraphAgent agent) => BlockKind(agent) switch
    {
        "steer" => IsDelegationNotice(agent.Input) ? Locale.Get("graph.block.delegationNotice") : IsTaskNotice(agent.Input) ? Locale.Get("graph.block.taskNotice") : Locale.Get("graph.block.steer"),
        "compact" => ContextCompaction.Title,
        "question" => string.IsNullOrEmpty(agent.Title) ? Locale.Get("graph.block.question") : agent.Title,
        "task" => string.IsNullOrEmpty(agent.Title) ? Locale.Get("graph.block.task") : agent.Title,
        _ => string.IsNullOrEmpty(agent.Title) ? Locale.Get("graph.block.agent") : agent.Title,
    };

    /// <summary>Delegation hands a child's notice, or a follow-up to a running child, into a running Claude turn as a steer
    /// (<see cref="DelegationCoordinator"/>); its text opens with one of these. Such a block is a 위임 알림, not a 중간 요청
    /// (macOS MightyGraphSupport.delegationNoticePrefixes).</summary>
    public static readonly string[] DelegationNoticePrefixes = ["[Mighty Claude notice ", "[Mighty Claude follow-up "];
    public static bool IsDelegationNotice(string? input) =>
        input is not null && DelegationNoticePrefixes.Any(prefix => input.StartsWith(prefix, StringComparison.Ordinal));
    /// <summary>A steer that delegation sent, not a person.</summary>
    public static bool IsDelegationNotice(MightyGraphAgent agent) => BlockKind(agent) == "steer" && IsDelegationNotice(agent.Input);
    /// <summary>A background task's <c>&lt;task-notification&gt;</c> the tracker shows as a steer under main, its readable line after this
    /// prefix; the next root-level answer is its reply. Such a block is a 작업 알림 (macOS MightyGraphSupport.taskNoticePrefix).</summary>
    public const string TaskNoticePrefix = "[Mighty Claude task notice] ";
    public static bool IsTaskNotice(string? input) => input is not null && input.StartsWith(TaskNoticePrefix, StringComparison.Ordinal);
    /// <summary>A steer the tracker made from a task notification, not a person.</summary>
    public static bool IsTaskNotice(MightyGraphAgent agent) => BlockKind(agent) == "steer" && IsTaskNotice(agent.Input);
    /// <summary>A steer no person sent: a delegation notice or a task notice (macOS MightyGraphAgent.isNotice).</summary>
    public static bool IsNotice(MightyGraphAgent agent) => IsDelegationNotice(agent) || IsTaskNotice(agent);
    /// <summary>What the block shows as its request: a task notice without its prefix (macOS MightyGraphAgent.shownInput).</summary>
    public static string ShownInput(MightyGraphAgent agent) => IsTaskNotice(agent) ? agent.Input[TaskNoticePrefix.Length..] : agent.Input;
    /// <summary>What opens a notice turn's section in a request's result (macOS MightyGraphSupport.noticeSectionMark).</summary>
    public const string NoticeSectionMark = "\n\n---\n\n**";
    /// <summary>The result with a notice turn's answer after it, under a bold line naming the notice; the request's own answer stays first.</summary>
    public static string WithNoticeSection(string? result, string header, string answer) =>
        (string.IsNullOrEmpty(result) ? "**" : result + NoticeSectionMark) + header + "**\n\n" + answer;
    /// <summary>Background tasks and sub-agents still running after the request's own turn ended: it reads completed and waits on them (macOS MightyGraphRun.backgroundWaiting).</summary>
    public static int BackgroundWaiting(MightyGraphRun run) => run.Status == "completed" ? run.Agents.Count(a => (a.IsTask || a.IsBackgroundAgent) && !Terminal(a.Status)) : 0;

    /// <summary>The kind line under a background block's head: a background sub-agent or a background run, waiting or ended. Null for any other block (macOS MightyGraphSupport.backgroundLabel).</summary>
    public static string? BackgroundLabel(MightyGraphAgent agent) =>
        agent.IsTask || agent.IsBackgroundAgent ? BackgroundLabel(agent.IsTask, agent.Status) : null;

    /// <summary><paramref name="run"/>: a background run (a task block, a <c>shell</c> task), else a background sub-agent. <paramref name="status"/> is a block's or a background task's; one still in motion reads waiting.</summary>
    public static string BackgroundLabel(bool run, string status)
    {
        var word = status switch
        {
            "completed" => Locale.Get("graph.block.taskNotice.completed"),
            "error" or "failed" => Locale.Get("graph.block.taskNotice.failed"),
            "stopped" or "cancelled" or "interrupted" => Locale.Get("graph.block.taskNotice.stopped"),
            "unknown" => Locale.Get("styles.state.taskStatus.unknown"),
            _ => null,
        };
        if (word is null) return Locale.Get(run ? "graph.background.runWaiting" : "graph.background.agentWaiting");
        return Locale.Get(run ? "graph.background.runEnded" : "graph.background.agentEnded", new Dictionary<string, string> { ["status"] = word });
    }

    public static string AgentNodeId(string runId, string agentId) =>
        MightyGraphBlockSize.NodeId(runId, "agent:" + agentId);

    public static string NextState(string previous, string incoming)
    {
        if (previous is "error" or "stopped") return previous;
        if (incoming is "error" or "stopped") return incoming;
        if (previous == "completed") return previous;
        return incoming;
    }

    private static readonly string[] ValidStates = ["idle", "running", "waiting", "completed", "error", "stopped"];
    private static readonly string[] ValidKinds = ["task", "steer", "compact", "question"];

    public const int LiveHistoryLimit = 2 * 1024 * 1024;
    private const int LiveHistoryLowWater = LiveHistoryLimit * 3 / 4;

    private static int LiveBytes(MightyGraphRun run)
    {
        int Bytes(string? v) => v is null ? 0 : Encoding.UTF8.GetByteCount(v);
        int EntryBytes(List<LogEntry> entries) => entries.Sum(e => 512 + Bytes(e.Text) + Bytes(e.Activity?.Output) + Bytes(e.Activity?.Summary));
        return 512 + Bytes(run.Id) + Bytes(run.SourceRunID) + Bytes(run.Input)
            + Bytes(run.FinalOutput) * 3 + EntryBytes(run.RootEntries)
            + run.Agents.Sum(a => 768 + Bytes(a.Id) + Bytes(a.ParentID) + Bytes(a.Title) + Bytes(a.Input) + EntryBytes(a.Entries));
    }

    /// Bytes the runs hold, measured as the live history measures its own
    /// (macOS MightyGraphSupport.liveHistoryBytes).
    public static int LiveHistoryBytes(IEnumerable<MightyGraphRun> runs) => runs.Sum(LiveBytes);

    public static List<MightyGraphRun> BoundedLiveHistory(List<MightyGraphRun> values)
    {
        var sizes = values.Select(LiveBytes).ToList();
        var total = sizes.Sum();
        if (total <= LiveHistoryLimit) return values;
        var kept = values.ToList();
        while (total > LiveHistoryLowWater && kept.Count > 1)
        {
            total -= sizes[0]; sizes.RemoveAt(0); kept.RemoveAt(0);
        }
        if (total <= LiveHistoryLimit) return kept;
        var budget = LiveHistoryLimit;
        return Normalized(kept, restoring: false, budget: ref budget);
    }

    public static List<MightyGraphRun> Normalized(List<MightyGraphRun> values, bool restoring, ref int budget, string? provider = null)
    {
        int Bytes(string? v) => v is null ? 0 : Encoding.UTF8.GetByteCount(v);
        static string Bounded(string text, int max, ref int b)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length <= max) { b -= bytes.Length; return text; }
            var truncated = bytes[..max];
            while (truncated.Length > 0 && (truncated[^1] & 0x80) != 0 && (truncated[^1] & 0xC0) != 0xC0)
                truncated = truncated[..^1];
            var result = Encoding.UTF8.GetString(truncated);
            b -= truncated.Length;
            return result;
        }

        var ids = new HashSet<string>();
        var result = new List<MightyGraphRun>();
        foreach (var run in values.TakeLast(128).Reverse())
        {
            var agents = run.Agents.Take(128).Where(a => Wire.Identifier(a.Id)).GroupBy(a => a.Id).Select(g => g.First()).ToList();
            var overhead = 512 + Bytes(run.Id) + Bytes(run.SourceRunID)
                + agents.Sum(a => 512 + Bytes(a.Id) + Bytes(a.ParentID));
            if (budget < overhead || !Wire.Identifier(run.Id) || !ids.Add(run.Id)) continue;
            budget -= overhead;

            var r = new MightyGraphRun
            {
                Id = run.Id,
                SourceRunID = run.SourceRunID is not null && Wire.Identifier(run.SourceRunID) ? run.SourceRunID : null,
                Input = Bounded(run.Input, 32_768, ref budget),
                Status = ValidStates.Contains(run.Status) ? run.Status : "stopped",
                Usage = run.Usage?.Normalized,
                NodeModelLabel = run.NodeModelLabel,
                ConfiguredModel = run.ConfiguredModel,
                RootEntries = [],
                ResultEntries = [],
                Agents = [],
                ResponseRecords = run.ResponseRecords,
            };

            var resolvedProvider = provider ?? run.Provider
                ?? run.RootEntries.Select(e => e.Provider).FirstOrDefault(p => p is not null && Wire.Providers.Contains(p)) ?? "claude";
            r.Provider = Wire.Providers.Contains(resolvedProvider) ? resolvedProvider : "claude";
            if (restoring && !Terminal(r.Status)) r.Status = "stopped";

            r.FinalOutput = run.FinalOutput is null ? null
                : Bounded(run.FinalOutput, Math.Min(32_768, Math.Max(0, budget / 3)), ref budget);
            if (r.FinalOutput != null) budget -= Bytes(r.FinalOutput) * 2;
            r.RootEntries = LimitedEntries(run.RootEntries, 131_072, restoring, ref budget);

            var builtAgents = new List<MightyGraphAgent>();
            foreach (var agent in agents)
            {
                var a = new MightyGraphAgent
                {
                    Id = agent.Id,
                    ParentID = agent.ParentID is not null && Wire.Identifier(agent.ParentID) && agent.ParentID != agent.Id ? agent.ParentID : null,
                    Title = Bounded(agent.Title, 240, ref budget),
                    Input = Bounded(agent.Input, 16_384, ref budget),
                    Status = ValidStates.Contains(agent.Status) ? agent.Status : "stopped",
                    Kind = ValidKinds.Contains(agent.Kind ?? "") ? agent.Kind : null,
                    Usage = agent.Usage?.Normalized,
                    ActivityGeneration = ExecutionGraphSupport.NormalizedGeneration(agent.ActivityGeneration),
                    ResponseRecords = agent.ResponseRecords,
                    Background = agent.Background == true ? true : null,
                };
                a.NoticeReply = agent.NoticeReply is { } reply && Bounded(reply, 32_768, ref budget) is { Length: > 0 } bounded ? bounded : null;
                a.OutputFile = agent.OutputFile is { } file && Bytes(file) <= 4096 && file.IndexOfAny(MightyStructure.Newlines) < 0 ? file : null;
                budget -= Bytes(a.OutputFile);
                if (restoring && !Terminal(a.Status)) a.Status = "stopped";
                a.Entries = LimitedEntries(agent.Entries, 65_536, restoring, ref budget);
                builtAgents.Add(a);
            }
            r.Agents = builtAgents;

            BreakCycles(r.Agents);
            r.Provider ??= "claude";
            ApplyProvider(r, r.Provider);
            RefreshResult(r);
            result.Add(r);
        }
        result.Reverse();
        return result;
    }

    /// macOS limitedEntries: one allowance per list, charged to the shared budget.
    private static List<LogEntry> LimitedEntries(List<LogEntry> values, int maximum, bool restoring, ref int budget)
    {
        var allowance = Math.Min(maximum, budget);
        var remaining = allowance;
        var result = Entries(values, restoring, ref remaining);
        budget -= allowance - remaining;
        return result;
    }
    private static List<LogEntry> Entries(List<LogEntry> values, bool restoring, ref int budget)
    {
        var ids = new HashSet<string>();
        var result = new List<LogEntry>();
        foreach (var original in values.TakeLast(100).Reverse())
        {
            var overhead = 256 + Encoding.UTF8.GetByteCount(original.Id) + Encoding.UTF8.GetByteCount(original.Timestamp);
            if (budget < overhead || !Wire.Identifier(original.Id) || !ids.Add(original.Id)
                || original.Kind is not ("user" or "assistant" or "system" or "output" or "error")) continue;
            budget -= overhead;
            var entry = original with { Text = Clip(original.Text, 32_768, ref budget) };
            if (entry.Activity is not null)
            {
                var activity = ActivitySupport.Normalize(entry.Activity, restoring);
                if (activity is not null)
                    activity = activity with
                    {
                        Summary = Clip(activity.Summary, 1_000, ref budget),
                        Output = activity.Output is null ? null : Clip(activity.Output, 8_192, ref budget),
                    };
                entry = entry with { Activity = activity };
            }
            result.Add(entry);
        }
        result.Reverse();
        return result;
    }
    private static string Clip(string text, int maximum, ref int budget)
    {
        var value = ActivitySupport.Clean(text, Math.Min(maximum, Math.Max(0, budget)));
        budget -= Encoding.UTF8.GetByteCount(value);
        return value;
    }

    private static void BreakCycles(List<MightyGraphAgent> agents)
    {
        var ids = agents.Select(a => a.Id).ToHashSet();
        var parents = agents.ToDictionary(a => a.Id, a => a.ParentID);
        foreach (var agent in agents)
        {
            if (agent.ParentID is null) continue;
            var visited = new HashSet<string> { agent.Id };
            var current = agent.ParentID;
            while (current != null)
            {
                if (!ids.Contains(current) || !visited.Add(current)) { agent.ParentID = null; break; }
                parents.TryGetValue(current, out current);
            }
        }
    }

    public static void ApplyProvider(MightyGraphRun run, string provider)
    {
        var p = Wire.Providers.Contains(provider) ? provider : "claude";
        run.Provider = p;
        LogEntry Label(LogEntry e) => e with { Provider = p, Activity = e.Activity is null ? null : e.Activity with { Provider = p } };
        run.RootEntries = run.RootEntries.Select(Label).ToList();
        run.ResultEntries = run.ResultEntries.Select(Label).ToList();
        foreach (var agent in run.Agents)
            agent.Entries = agent.Entries.Select(Label).ToList();
    }

    /// <summary>How many of the main agent's entries a request keeps (M/MightyGraph.swift recordGraph: suffix(100)).</summary>
    public const int MaximumRootEntries = 100;

    /// <summary>
    /// The main agent's entries of one request: <paramref name="earlier"/> with <paramref name="incoming"/> laid over them by id,
    /// the request itself left out, the newest <see cref="MaximumRootEntries"/> kept. The result entry RefreshResult adds is
    /// dropped when the transcript already holds the same answer, so the 실행 block does not say it twice.
    /// </summary>
    public static List<LogEntry> MergeRootEntries(IEnumerable<LogEntry> earlier, IEnumerable<LogEntry> incoming)
    {
        var merged = earlier.Where(e => e.Kind != "user").ToList();
        foreach (var entry in incoming.Where(e => e.Kind != "user"))
        {
            var index = merged.FindIndex(e => e.Id == entry.Id);
            if (index >= 0) merged[index] = entry; else merged.Add(entry);
        }
        merged.RemoveAll(e => e.Id.EndsWith("-result", StringComparison.Ordinal) && merged.Any(o => !ReferenceEquals(o, e) && o.Kind == "assistant" && o.Text == e.Text));
        return merged.TakeLast(MaximumRootEntries).ToList();
    }

    public static void RefreshResult(MightyGraphRun run)
    {
        if (run.Status != "completed" || !run.Agents.All(a => Terminal(a.Status)) || string.IsNullOrEmpty(run.FinalOutput))
        { run.ResultEntries = []; return; }
        var ts = run.ResultEntries.FirstOrDefault()?.Timestamp ?? run.RootEntries.LastOrDefault()?.Timestamp ?? Wire.Now();
        var provider = run.Provider ?? "claude";
        var entry = new LogEntry(run.Id + "-result", "assistant", run.FinalOutput!, ts, provider);
        run.ResultEntries = [entry];
        // A result that goes on with notice sections starts with the request's own answer, which the main block already shows.
        var own = run.FinalOutput!.Split(NoticeSectionMark)[0];
        if (!run.RootEntries.Any(e => e.Kind == "assistant" && (e.Text == run.FinalOutput || e.Text == own)))
        {
            var idx = run.RootEntries.FindIndex(e => e.Id == entry.Id);
            if (idx >= 0) run.RootEntries[idx] = entry; else run.RootEntries.Add(entry);
        }
    }
}

/// <summary>
/// A user-chosen graph card size in canvas points, independent of zoom; saved
/// as <c>{"width","height"}</c> like macOS <c>MightyGraphBlockSize</c>.
/// </summary>
public sealed record GraphBlockSize(
    [property: JsonPropertyName("width")] double Width,
    [property: JsonPropertyName("height")] double Height)
{
    /// Kept within the drag bounds; null when either side is not a number.
    [JsonIgnore]
    public GraphBlockSize? Normalized => NormalizedAtLeast(MightyGraphBlockSize.MinimumHeight);

    /// <see cref="Normalized"/> for the block <paramref name="nodeId"/> names: an answered plan's block may stay as
    /// short as it folds (macOS <c>MightyGraphBlockSize.normalized(nodeID:)</c>).
    public GraphBlockSize? NormalizedFor(string nodeId) => NormalizedAtLeast(MightyGraphBlockSize.MinimumHeightFor(nodeId));

    private GraphBlockSize? NormalizedAtLeast(double minimumHeight) =>
        double.IsFinite(Width) && double.IsFinite(Height)
            ? new(Math.Min(MightyGraphBlockSize.MaximumWidth, Math.Max(MightyGraphBlockSize.MinimumWidth, Width)),
                  Math.Min(MightyGraphBlockSize.MaximumHeight, Math.Max(minimumHeight, Height)))
            : null;
}

public static class MightyGraphBlockSize
{
    /// The smallest and largest size a block may be dragged to (macOS values).
    public const double MinimumWidth = 300;
    public const double MinimumHeight = 140;
    public const double MaximumWidth = 1_400;
    public const double MaximumHeight = 1_200;

    public static string NodeId(string runId, string suffix)
    {
        var len = Encoding.UTF8.GetByteCount(runId);
        return $"{len}:{runId}:{suffix}";
    }

    /// <summary>The least height a saved size of <paramref name="nodeId"/> keeps: an answered plan's folded height, else <see cref="MinimumHeight"/>.</summary>
    public static double MinimumHeightFor(string nodeId) =>
        Suffix(nodeId)?.StartsWith(MightyGraphLayout.PlanRecordSuffix, StringComparison.Ordinal) == true ? MightyGraphLayout.PlanRecordHeight(false) : MinimumHeight;

    /// <summary>The suffix of a <see cref="NodeId"/>, read past its length-prefixed run id (UTF-8 bytes, as written).</summary>
    internal static string? Suffix(string nodeId)
    {
        var colon = nodeId.IndexOf(':');
        if (colon <= 0 || !int.TryParse(nodeId.AsSpan(0, colon), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)) return null;
        var bytes = Encoding.UTF8.GetBytes(nodeId);
        var separator = colon + 1 + count;
        if (separator >= bytes.Length || bytes[separator] != (byte)':') return null;
        return Encoding.UTF8.GetString(bytes, separator + 1, bytes.Length - separator - 1);
    }
}
