using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MightyCode.Core;

/// <summary>
/// Tokens the engine reported for one block. Each assistant message carries the
/// usage of its own API call; a block's figure is the sum over its messages.
/// Port of macOS MightyCore/ExecutionGraph.swift GraphTokenUsage.
/// </summary>
public sealed record GraphTokenUsage(
    [property: JsonPropertyName("inputTokens")] long InputTokens = 0,
    [property: JsonPropertyName("outputTokens")] long OutputTokens = 0,
    [property: JsonPropertyName("cacheReadTokens")] long CacheReadTokens = 0,
    [property: JsonPropertyName("cacheCreationTokens")] long CacheCreationTokens = 0)
{
    public const long MaximumTokens = 1_000_000_000_000L;
    [JsonIgnore] public long Total => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
    [JsonIgnore] public bool IsEmpty => Total == 0;
    /// <summary>nil unless every counter is in range and the total is positive.</summary>
    [JsonIgnore] public GraphTokenUsage? Normalized
    {
        get
        {
            foreach (var value in new[] { InputTokens, OutputTokens, CacheReadTokens, CacheCreationTokens })
                if (value < 0 || value > MaximumTokens) return null;
            return Total > 0 ? this : null;
        }
    }
    public static GraphTokenUsage operator +(GraphTokenUsage a, GraphTokenUsage b) =>
        new(a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens, a.CacheReadTokens + b.CacheReadTokens, a.CacheCreationTokens + b.CacheCreationTokens);
    public static GraphTokenUsage operator -(GraphTokenUsage a, GraphTokenUsage b) =>
        new(Math.Max(0, a.InputTokens - b.InputTokens), Math.Max(0, a.OutputTokens - b.OutputTokens),
            Math.Max(0, a.CacheReadTokens - b.CacheReadTokens), Math.Max(0, a.CacheCreationTokens - b.CacheCreationTokens));

    /// <summary>The `usage` object of a CLI stream message. Missing counters are zero;
    /// a missing or empty object is no observation at all.</summary>
    public static GraphTokenUsage? Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        long Count(string key)
        {
            var item = MetadataJson.Property(value, key);
            if (item.ValueKind != JsonValueKind.Number) return 0;
            if (item.TryGetInt64(out var integer)) return integer;
            return item.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0 && number <= MaximumTokens ? (long)number : 0;
        }
        return new GraphTokenUsage(Count("input_tokens"), Count("output_tokens"), Count("cache_read_input_tokens"), Count("cache_creation_input_tokens")).Normalized;
    }

    public static string Compact(long tokens)
    {
        if (tokens < 1_000) return tokens.ToString(CultureInfo.InvariantCulture);
        if (tokens < 1_000_000) return (tokens / 1_000d).ToString(tokens < 10_000 ? "F1" : "F0", CultureInfo.InvariantCulture) + "K";
        return (tokens / 1_000_000d).ToString(tokens < 10_000_000 ? "F2" : "F1", CultureInfo.InvariantCulture) + "M";
    }
    [JsonIgnore] public string Summary => Locale.Get("graph.usage.summary", new Dictionary<string, string> { ["total"] = Compact(Total) });
    [JsonIgnore] public string Detail
    {
        get
        {
            var parts = new List<string>
            {
                Locale.Get("graph.usage.input", new Dictionary<string, string> { ["value"] = Compact(InputTokens) }),
                Locale.Get("graph.usage.output", new Dictionary<string, string> { ["value"] = Compact(OutputTokens) }),
            };
            if (CacheReadTokens > 0) parts.Add(Locale.Get("graph.usage.cacheRead", new Dictionary<string, string> { ["value"] = Compact(CacheReadTokens) }));
            if (CacheCreationTokens > 0) parts.Add(Locale.Get("graph.usage.cacheCreation", new Dictionary<string, string> { ["value"] = Compact(CacheCreationTokens) }));
            return string.Join(" · ", parts) + " · " + Locale.Get("graph.usage.total", new Dictionary<string, string> { ["value"] = Compact(Total) });
        }
    }
}

/// <summary>
/// Per-response attribution record for a graph node: the model that produced
/// the response, the tokens it used, and the tool_use IDs it called.
/// </summary>
public sealed record GraphResponseRecord(
    [property: JsonPropertyName("responseId")] string ResponseId,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("usage")] GraphTokenUsage Usage,
    [property: JsonPropertyName("activityIds")] List<string> ActivityIds,
    [property: JsonPropertyName("markedAsConfigured")] bool MarkedAsConfigured = false)
{
    /// Structural equality: the synthesized record comparison would compare
    /// ActivityIds by reference (macOS GraphResponseRecord: Equatable).
    public bool Equals(GraphResponseRecord? other) =>
        other is not null && ResponseId == other.ResponseId && Model == other.Model && Equals(Usage, other.Usage)
        && ActivityIds.SequenceEqual(other.ActivityIds) && MarkedAsConfigured == other.MarkedAsConfigured;
    public override int GetHashCode() => HashCode.Combine(ResponseId, Model, Usage, ActivityIds.Count, MarkedAsConfigured);
}

/// <summary>
/// Who a sub-agent block is, as the CLI said it: the Agent call's <c>subagent_type</c>, <c>model</c> and <c>isolation</c>, and the
/// model the CLI resolved when it launched the agent. Every part is optional; a block without any carries none (older graph events
/// and saves, Codex and Antigravity sub-agents). <see cref="FlowRoles"/> reads it for the flow diagram (macOS GraphAgentIdentity).
/// </summary>
public sealed record GraphAgentIdentity(
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("requestedModel")] string? RequestedModel = null,
    [property: JsonPropertyName("resolvedModel")] string? ResolvedModel = null,
    [property: JsonPropertyName("isolation")] string? Isolation = null)
{
    /// <summary>The Agent call's type for a fork of the calling session.</summary>
    public const string ForkType = "fork";
    public static readonly string[] Isolations = ["worktree", "remote"];
    /// <summary>A fork inherits its caller's whole conversation and model.</summary>
    [JsonIgnore] public bool IsFork => Type == ForkType;
    [JsonIgnore] public bool IsEmpty => Type is null && RequestedModel is null && ResolvedModel is null && Isolation is null;
    /// <summary>Bounded for the wire and for saves: a type on one line, model names the CLI could name, a known isolation. Null when nothing is left.</summary>
    // Internal: a record prints its public members, and each normalized value has one of its own.
    [JsonIgnore] internal GraphAgentIdentity? Normalized
    {
        get
        {
            static string? Model(string? value) => Wire.Model(value) ? value : null;
            var type = Type is null ? null : ActivitySupport.Clean(Type, 160, true) is { Length: > 0 } clean ? clean : null;
            var value = new GraphAgentIdentity(type, Model(RequestedModel), Model(ResolvedModel), Isolation is not null && Isolations.Contains(Isolation) ? Isolation : null);
            return value.IsEmpty ? null : value;
        }
    }
    [JsonIgnore] internal int ByteCount => Bytes(Type) + Bytes(RequestedModel) + Bytes(ResolvedModel) + Bytes(Isolation);
    private static int Bytes(string? value) => value is null ? 0 : Encoding.UTF8.GetByteCount(value);
}

/// <summary>
/// The figures a sub-agent block, or a background command's (a task block), keeps of its own run, for the flow diagram's compact card
/// (<see cref="FlowCard"/>): when the app saw it start and end, how many tools it called so far, and how a command exited. Each is what was
/// observed, never an estimate; a block without any carries none (macOS GraphAgentFigures).
/// </summary>
public sealed record GraphAgentFigures(
    [property: JsonPropertyName("startedAt")] string? StartedAt = null,
    [property: JsonPropertyName("endedAt")] string? EndedAt = null,
    [property: JsonPropertyName("toolCalls")] int? ToolCalls = null,
    [property: JsonPropertyName("exitCode")] int? ExitCode = null)
{
    public const int MaximumToolCalls = 1_000_000;
    [JsonIgnore] public bool IsEmpty => StartedAt is null && EndedAt is null && ToolCalls is null && ExitCode is null;
    /// <summary>A timestamp the app can read back, a count in range; null when nothing is left.</summary>
    [JsonIgnore] internal GraphAgentFigures? Normalized
    {
        get
        {
            var value = new GraphAgentFigures(GraphTime.Kept(StartedAt), GraphTime.Kept(EndedAt), ToolCalls is >= 0 and <= MaximumToolCalls ? ToolCalls : null, ExitCode);
            return value.IsEmpty ? null : value;
        }
    }
    [JsonIgnore] public DateTimeOffset? Started => AgentRunTiming.Parse(StartedAt);
    [JsonIgnore] public DateTimeOffset? Ended => AgentRunTiming.Parse(EndedAt);
}

/// <summary>A time a graph record keeps: one the app can read back, at most 80 bytes (macOS AgentRunTiming.parseTimestamp in the normalizers).</summary>
internal static class GraphTime
{
    internal static string? Kept(string? value) => value is not null && Encoding.UTF8.GetByteCount(value) <= 80 && AgentRunTiming.Parse(value) is not null ? value : null;
}

/// <summary>
/// One consultation of the advisor (Claude Code's server-side advisor tool, <c>claude --advisor &lt;model&gt;</c>), as the stream showed it and
/// nothing more: the advisor's reply usually goes to Claude encrypted, and then there is no text here to show (macOS GraphAdvisorCall).
/// </summary>
/// <param name="Id">The server tool call's id (<c>srvtoolu_…</c>).</param>
/// <param name="Caller">The block that asked: a sub-agent block's id; null is the run's main session.</param>
/// <param name="State"><c>consulting</c> until its result; then <c>done</c>, <c>declined</c> (a refusal), <c>error</c> (<paramref name="ErrorCode"/>) or <c>stopped</c> (the turn or the process ended first).</param>
/// <param name="Redacted">The reply came encrypted (<c>advisor_redacted_result</c>): it went to Claude and there is no text to show.</param>
/// <param name="Text">The advice, when the CLI gave it as text (<c>advisor_result</c>), bounded.</param>
/// <param name="BeforeChange">When it asked, the caller had called no file-changing tool yet (Edit, Write, MultiEdit, NotebookEdit).</param>
/// <param name="Followed">The caller called a tool again after it (another consultation counts): it was not the caller's last step.</param>
public sealed record GraphAdvisorCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("caller")] string? Caller = null,
    [property: JsonPropertyName("startedAt")] string? StartedAt = null,
    [property: JsonPropertyName("endedAt")] string? EndedAt = null,
    [property: JsonPropertyName("state")] string State = "consulting",
    [property: JsonPropertyName("redacted")] bool? Redacted = null,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("errorCode")] string? ErrorCode = null,
    [property: JsonPropertyName("beforeChange")] bool? BeforeChange = null,
    [property: JsonPropertyName("followed")] bool? Followed = null)
{
    [JsonIgnore] public DateTimeOffset? Started => AgentRunTiming.Parse(StartedAt);
    [JsonIgnore] public DateTimeOffset? Ended => AgentRunTiming.Parse(EndedAt);
    [JsonIgnore] internal int ByteCount => Bytes(Id) + Bytes(Caller) + Bytes(StartedAt) + Bytes(EndedAt) + Bytes(State) + Bytes(Text) + Bytes(ErrorCode);
    private static int Bytes(string? value) => value is null ? 0 : Encoding.UTF8.GetByteCount(value);
}

/// <summary>
/// A run's use of the advisor: the consultations the stream showed and what its turns' results said the advisor's own messages cost. Kept on
/// the run's main block (a notice turn's own run for what its main session asked); a run that never consulted it carries none, as do older
/// graph events and saves (macOS GraphAdvisor).
/// </summary>
/// <param name="Model">The advisor's model as a turn's result named it (<c>usage.iterations[]</c> of type <c>advisor_message</c>).</param>
/// <param name="Usage">The tokens of the advisor's own messages, summed over the results of the run's turns.</param>
/// <param name="Calls">The newest consultations, oldest first, at most <see cref="MaximumCalls"/>.</param>
/// <param name="Total">Every consultation seen, when that is more than <paramref name="Calls"/> keeps.</param>
public sealed record GraphAdvisor(
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("usage")] GraphTokenUsage? Usage = null,
    List<GraphAdvisorCall>? Calls = null,
    [property: JsonPropertyName("total")] int? Total = null)
{
    /// <summary>The calls, an empty list when none came: always written, as the Mac's decoder needs the key (its <c>calls</c> is no optional).</summary>
    [JsonPropertyName("calls")] public List<GraphAdvisorCall>? Calls { get; init; } = Calls ?? [];
    public const int MaximumCalls = 32;
    /// <summary>With <see cref="MaximumCalls"/>, a run's advice never takes more than its result may.</summary>
    public const int MaximumTextBytes = 1024;
    public const int MaximumTotal = 1_000_000;
    public static readonly string[] States = ["consulting", "done", "declined", "error", "stopped"];
    /// <summary>The calls, never null (an older or a hand-written record may leave them out).</summary>
    [JsonIgnore] public IReadOnlyList<GraphAdvisorCall> Consultations => Calls ?? [];
    /// <summary>Every consultation seen, kept or not.</summary>
    [JsonIgnore] public int Count => Math.Max(Total ?? 0, Consultations.Count);
    [JsonIgnore] public bool IsEmpty => Model is null && Usage is null && Consultations.Count == 0;
    [JsonIgnore] internal int ByteCount => (Model is null ? 0 : Encoding.UTF8.GetByteCount(Model)) + Consultations.Sum(c => c.ByteCount);

    /// <summary>Bounded for the wire and for saves: the newest calls with ids, states and times the app can read back and a bounded text; null
    /// when nothing is left. <paramref name="restoring"/>: a consultation still out when the app closed never got its result.</summary>
    public GraphAdvisor? Normalized(bool restoring = false)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<GraphAdvisorCall>();
        foreach (var call in Consultations.TakeLast(MaximumCalls))
        {
            if (call is null || !Wire.Identifier(call.Id) || !States.Contains(call.State) || call.Caller is { } caller && !Wire.Identifier(caller) || !seen.Add(call.Id)) continue;
            string? Optional(string? text, int bytes, bool line = false) => text is null ? null : ActivitySupport.Clean(text, bytes, line) is { Length: > 0 } clean ? clean : null;
            kept.Add(call with
            {
                StartedAt = GraphTime.Kept(call.StartedAt), EndedAt = GraphTime.Kept(call.EndedAt),
                State = restoring && call.State == "consulting" ? "stopped" : call.State,
                Redacted = call.Redacted == true ? true : null,
                Text = Optional(call.Text, MaximumTextBytes),
                ErrorCode = Optional(call.ErrorCode, 80, true),
                BeforeChange = call.BeforeChange == true ? true : null,
                Followed = call.Followed == true ? true : null,
            });
        }
        var all = Math.Min(MaximumTotal, Count);
        var value = new GraphAdvisor(Wire.Model(Model) ? Model : null, Usage?.Normalized, kept, all > kept.Count ? all : null);
        return value.IsEmpty ? null : value;
    }

    /// Structural equality (macOS GraphAdvisor: Equatable): the synthesized comparison would compare the calls by reference.
    public bool Equals(GraphAdvisor? other) =>
        other is not null && Model == other.Model && Equals(Usage, other.Usage) && Total == other.Total && Consultations.SequenceEqual(other.Consultations);
    public override int GetHashCode() => HashCode.Combine(Model, Usage, Total, Consultations.Count);
}

/// <summary>
/// A request-scoped node snapshot. A missing input/output means the engine did
/// not report it; display code must not manufacture a prompt or final answer.
/// </summary>
public sealed record ExecutionGraphNode
{
    /// Convenience constructor used by the tracker (matches Swift memberwise init).
    public ExecutionGraphNode(string id, string runId, string? parentId, string kind, string state, string title)
    { Id = id; RunId = runId; ParentId = parentId; Kind = kind; State = state; Title = title; }
    /// Parameterless constructor required for record `with` expressions and JSON.
    public ExecutionGraphNode() { }

    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("runId")] public string RunId { get; init; } = "";
    [JsonPropertyName("parentId")] public string? ParentId { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = "main";
    [JsonPropertyName("state")] public string State { get; init; } = "running";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("input")] public string? Input { get; init; }
    [JsonPropertyName("output")] public string? Output { get; init; }
    [JsonPropertyName("entries")] public List<LogEntry> Entries { get; init; } = [];
    [JsonPropertyName("updatedAt")] public string UpdatedAt { get; init; } = Wire.Now();
    [JsonPropertyName("usage")] public GraphTokenUsage? Usage { get; init; }
    /// <summary>Explicit new work on the same agent; absent in older graph events.</summary>
    [JsonPropertyName("activityGeneration")] public int? ActivityGeneration { get; init; }
    /// <summary>Per-response attribution; absent in older graph events and graph saves.</summary>
    [JsonPropertyName("responseRecords")] public List<GraphResponseRecord>? ResponseRecords { get; init; }
    /// <summary>Started with <c>run_in_background</c> (a background Agent; every task block); absent otherwise and in older saves.</summary>
    [JsonPropertyName("background")] public bool? Background { get; init; }
    /// <summary>Where a task block's run writes its output. Never shown and never sent to the phone.</summary>
    [JsonPropertyName("outputFile")] public string? OutputFile { get; init; }
    /// <summary>Claude's answer in the turn this block's notice opened, when it was that turn's only notice.</summary>
    [JsonPropertyName("noticeReply")] public string? NoticeReply { get; init; }
    /// <summary>A copy in a notice run (<see cref="BackgroundNoticeRuns"/>): the id of the finished background block it copies.</summary>
    [JsonPropertyName("sourceBlockId")] public string? SourceBlockId { get; init; }
    /// <summary>Who a sub-agent block is (<see cref="GraphAgentIdentity"/>); absent on other blocks, in older graph events and in older saves.</summary>
    [JsonPropertyName("identity")] public GraphAgentIdentity? Identity { get; init; }
    /// <summary>A sub-agent block's start, end and tool calls, a background command's start, end and exit code (<see cref="GraphAgentFigures"/>);
    /// absent on other blocks, in older graph events and in older saves.</summary>
    [JsonPropertyName("figures")] public GraphAgentFigures? Figures { get; init; }
    /// <summary>A run's use of the advisor (<see cref="GraphAdvisor"/>), on its main block; absent on other blocks, in a run that never consulted
    /// it, in older graph events and in older saves.</summary>
    [JsonPropertyName("advisor")] public GraphAdvisor? Advisor { get; init; }

    /// Structural equality (macOS ExecutionGraphNode: Equatable) — the synthesized
    /// record comparison would compare the lists by reference.
    public bool Equals(ExecutionGraphNode? other) =>
        other is not null && Id == other.Id && RunId == other.RunId && ParentId == other.ParentId && Kind == other.Kind
        && State == other.State && Title == other.Title && Input == other.Input && Output == other.Output
        && Equals(Usage, other.Usage) && ActivityGeneration == other.ActivityGeneration
        && Background == other.Background && OutputFile == other.OutputFile && NoticeReply == other.NoticeReply && SourceBlockId == other.SourceBlockId
        && Equals(Identity, other.Identity) && Equals(Figures, other.Figures) && Equals(Advisor, other.Advisor)
        && GraphEquality.Records(ResponseRecords, other.ResponseRecords)
        && GraphEquality.Entries(Entries, other.Entries);
    public override int GetHashCode() => HashCode.Combine(Id, RunId, ParentId, Kind, State, Title, Entries.Count);
}

internal static class GraphEquality
{
    internal static bool Records(List<GraphResponseRecord>? a, List<GraphResponseRecord>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.Count == b.Count && !a.Where((value, index) => !value.Equals(b[index])).Any();
    }
    internal static bool Entries(List<LogEntry> a, List<LogEntry> b) => a.Count == b.Count && a.SequenceEqual(b);
}

public static class ExecutionGraphSupport
{
    public const int MaximumActivityGeneration = 1_000_000;
    public static int? NormalizedGeneration(int? value) => value is int number && number >= 0 && number <= MaximumActivityGeneration ? number : null;
    public const int MaximumNodes = 128;
    public const int MaximumEntries = 80;
    public const int MaximumInputBytes = 16 * 1024;
    public const int MaximumOutputBytes = 32 * 1024;
    public const int MaximumNodeBytes = 64 * 1024;
    public static readonly string[] Kinds = ["main", "agent", "task", "steer", "compact", "question"];
    public static readonly string[] EntryKinds = ["user", "assistant", "system", "output", "error", "image"];

    public static string MainNodeID(string runId) => Identifier(runId, "main");
    public static string AgentNodeID(string runId, string toolUseId) => Identifier(runId, "tool:" + toolUseId);
    public static string Identifier(string runId, string key) =>
        "graph-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId + "|" + key))).ToLowerInvariant()[..48];

    public static bool Terminal(string state) => state is "completed" or "error" or "stopped";

    /// The first <paramref name="count"/> Unicode scalars, as Swift's String.prefix takes them.
    public static string Prefix(string value, int count)
    {
        var result = new StringBuilder(); var taken = 0;
        foreach (var rune in value.EnumerateRunes()) { if (taken >= count) break; result.Append(rune); taken += 1; }
        return result.ToString();
    }

    private static int Bytes(string? value) => value is null ? 0 : Encoding.UTF8.GetByteCount(value);

    /// <summary>
    /// Shared by the authenticated wire boundary and persisted graph history.
    /// The byte budget includes prompts, final output, and all entry metadata.
    /// </summary>
    public static ExecutionGraphNode? Normalized(ExecutionGraphNode node, bool restoring = false)
    {
        if (!Wire.Identifier(node.Id) || !Wire.Identifier(node.RunId)) return null;
        if (node.ParentId is { } parent && (!Wire.Identifier(parent) || parent == node.Id)) return null;
        if (!Kinds.Contains(node.Kind) || !ActivitySupport.States.Contains(node.State)) return null;
        if (Bytes(node.UpdatedAt) > 80 || AgentRunTiming.Parse(node.UpdatedAt) is null) return null;
        var result = node with
        {
            ParentId = node.Kind == "main" ? null : node.ParentId,
            Title = ActivitySupport.Clean(node.Title, 160, true),
            Input = node.Input is null ? null : ActivitySupport.Clean(node.Input, MaximumInputBytes),
            Output = node.Output is null ? null : ActivitySupport.Clean(node.Output, MaximumOutputBytes),
            Usage = node.Usage?.Normalized,
            ActivityGeneration = NormalizedGeneration(node.ActivityGeneration),
            ResponseRecords = node.ResponseRecords,
            Background = node.Background == true ? true : null,
            OutputFile = node.OutputFile is { } file && Bytes(file) <= 4096 && file.IndexOfAny(MightyStructure.Newlines) < 0 ? file : null,
            NoticeReply = node.NoticeReply is null ? null : ActivitySupport.Clean(node.NoticeReply, MaximumOutputBytes),
            SourceBlockId = node.SourceBlockId is { } source && Wire.Identifier(source) ? source : null,
            // Only a sub-agent block has a type, a model of its own and an isolation; a sub-agent's or a task's its figures; a run's main
            // block its consultations of the advisor (macOS).
            Identity = node.Kind == "agent" ? node.Identity?.Normalized : null,
            Figures = node.Kind is "agent" or "task" ? node.Figures?.Normalized : null,
            Advisor = node.Kind == "main" ? node.Advisor?.Normalized(restoring) : null,
        };
        if (restoring && !Terminal(result.State)) result = result with { State = "stopped" };
        if (node.Kind == "main") return result with { Entries = [] };

        var remaining = MaximumNodeBytes - Bytes(result.Title) - Bytes(result.Input) - Bytes(result.Output)
            - Bytes(result.OutputFile) - Bytes(result.NoticeReply) - Bytes(result.SourceBlockId) - (result.Identity?.ByteCount ?? 0);
        var seen = new HashSet<string>();
        var entries = new List<LogEntry>();
        foreach (var original in node.Entries.TakeLast(MaximumEntries).Reverse())
        {
            var entry = original;
            if (!Wire.Identifier(entry.Id) || !seen.Add(entry.Id) || !EntryKinds.Contains(entry.Kind)) continue;
            if (entry.Provider is { } provider && !Wire.Providers.Contains(provider)) continue;
            if (Bytes(entry.Timestamp) > 80 || AgentRunTiming.Parse(entry.Timestamp) is null || remaining <= 256) continue;
            entry = entry with { Activity = ActivitySupport.Normalize(entry.Activity, restoring), Images = entry.Kind == "image" ? AgentImageSupport.Normalized(entry.Images) : null };
            // An entry past the budget is kept without its activity and with its text clipped, as on the Mac (ExecutionGraph.swift).
            var metadata = Bytes(entry.Id) + Bytes(entry.Timestamp) + Bytes(entry.Kind) + Bytes(entry.Provider) + AgentImageSupport.ApproximateBytes(entry.Images);
            var activityBytes = entry.Activity is { } value
                ? Bytes(value.Id) + Bytes(value.Kind) + Bytes(value.State) + Bytes(value.Provider) + Bytes(value.Summary) + Bytes(value.ToolName) + Bytes(value.Output)
                : 0;
            if (metadata + activityBytes >= remaining) { entry = entry with { Activity = null }; activityBytes = 0; }
            var available = Math.Max(0, remaining - metadata - activityBytes);
            entry = entry with { Text = ActivitySupport.Clean(entry.Text, Math.Min(MaximumOutputBytes, available)) };
            remaining -= metadata + activityBytes + Bytes(entry.Text);
            entries.Add(entry);
        }
        entries.Reverse();
        return result with { Entries = entries };
    }
}
