using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MightyClaude.Core;

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

    /// Structural equality (macOS ExecutionGraphNode: Equatable) — the synthesized
    /// record comparison would compare the lists by reference.
    public bool Equals(ExecutionGraphNode? other) =>
        other is not null && Id == other.Id && RunId == other.RunId && ParentId == other.ParentId && Kind == other.Kind
        && State == other.State && Title == other.Title && Input == other.Input && Output == other.Output
        && Equals(Usage, other.Usage) && ActivityGeneration == other.ActivityGeneration
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
    public static readonly string[] EntryKinds = ["user", "assistant", "system", "output", "error"];

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
        };
        if (restoring && !Terminal(result.State)) result = result with { State = "stopped" };
        if (node.Kind == "main") return result with { Entries = [] };

        var remaining = MaximumNodeBytes - Bytes(result.Title) - Bytes(result.Input) - Bytes(result.Output);
        var seen = new HashSet<string>();
        var entries = new List<LogEntry>();
        foreach (var original in node.Entries.TakeLast(MaximumEntries).Reverse())
        {
            var entry = original;
            if (!Wire.Identifier(entry.Id) || !seen.Add(entry.Id) || !EntryKinds.Contains(entry.Kind)) continue;
            if (entry.Provider is { } provider && !Wire.Providers.Contains(provider)) continue;
            if (Bytes(entry.Timestamp) > 80 || AgentRunTiming.Parse(entry.Timestamp) is null || remaining <= 256) continue;
            entry = entry with { Activity = ActivitySupport.Normalize(entry.Activity, restoring) };
            var metadata = Bytes(entry.Id) + Bytes(entry.Timestamp) + Bytes(entry.Kind) + Bytes(entry.Provider);
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
