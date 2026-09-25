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

    /// A private copy: the runs of one session snapshot must never be mutated
    /// through another snapshot that still refers to them.
    public MightyGraphAgent Copy() => new()
    {
        Id = Id, ParentID = ParentID, Title = Title, Input = Input, Status = Status, Entries = [.. Entries],
        Kind = Kind, Usage = Usage, ActivityGeneration = ActivityGeneration,
        ResponseRecords = ResponseRecords is null ? null : [.. ResponseRecords],
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
    public static readonly string[] Providers = ["claude", "codex"];

    public static bool Terminal(string status) => status is "completed" or "error" or "stopped";

    public static string BlockKind(MightyGraphAgent agent) =>
        agent.Kind is "task" or "steer" or "compact" or "question" ? agent.Kind : "agent";

    public static string BlockTitle(MightyGraphAgent agent) => BlockKind(agent) switch
    {
        "steer" => Locale.Get("graph.block.steer"),
        "compact" => ContextCompaction.Title,
        "question" => string.IsNullOrEmpty(agent.Title) ? Locale.Get("graph.block.question") : agent.Title,
        "task" => string.IsNullOrEmpty(agent.Title) ? Locale.Get("graph.block.task") : agent.Title,
        _ => string.IsNullOrEmpty(agent.Title) ? Locale.Get("graph.block.agent") : agent.Title,
    };

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
                };
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

    public static void RefreshResult(MightyGraphRun run)
    {
        if (run.Status != "completed" || !run.Agents.All(a => Terminal(a.Status)) || string.IsNullOrEmpty(run.FinalOutput))
        { run.ResultEntries = []; return; }
        var ts = run.ResultEntries.FirstOrDefault()?.Timestamp ?? run.RootEntries.LastOrDefault()?.Timestamp ?? Wire.Now();
        var provider = run.Provider ?? "claude";
        var entry = new LogEntry(run.Id + "-result", "assistant", run.FinalOutput!, ts, provider);
        run.ResultEntries = [entry];
        if (!run.RootEntries.Any(e => e.Kind == "assistant" && e.Text == run.FinalOutput))
        {
            var idx = run.RootEntries.FindIndex(e => e.Id == entry.Id);
            if (idx >= 0) run.RootEntries[idx] = entry; else run.RootEntries.Add(entry);
        }
    }
}

public static class MightyGraphBlockSize
{
    public static string NodeId(string runId, string suffix)
    {
        var len = Encoding.UTF8.GetByteCount(runId);
        return $"{len}:{runId}:{suffix}";
    }
}
