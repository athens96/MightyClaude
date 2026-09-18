using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MightyClaude.Core;

public sealed record SessionRateLimit(string Kind, double? PercentUsed = null, string? ResetsAt = null);
[JsonConverter(typeof(SessionUsageConverter))]
public sealed record SessionUsage
{
    public string Provider { get; init; } = "claude";
    public string Source { get; init; } = "claude.stream-json";
    public string TokenScope { get; init; } = "run";
    public string? Model { get; init; }
    public string? ProviderSessionId { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? CacheReadTokens { get; init; }
    public long? CacheWriteTokens { get; init; }
    public long? ReasoningTokens { get; init; }
    public long? TotalTokens { get; init; }
    public long? ContextUsedTokens { get; init; }
    public long? ContextWindowTokens { get; init; }
    public double? CostUSD { get; init; }
    public string? CostScope { get; init; }
    public List<SessionRateLimit>? RateLimits { get; init; }
    public string? RateLimitsUpdatedAt { get; init; }
    public string UpdatedAt { get; init; } = Wire.Now();
    [JsonIgnore] public double? ContextPercent => ContextUsedTokens is >= 0 && ContextWindowTokens is > 0 ? Math.Clamp((double)ContextUsedTokens.Value / ContextWindowTokens.Value * 100, 0, 100) : null;
}
public static class SessionUsageSupport
{
    public const long MaximumTokens = 9_000_000_000_000;
    public static bool Scope(string? value) => value is "response" or "run" or "session";
    internal static long? Count(long? value) => value is >= 0 and <= MaximumTokens ? value : null;
    internal static long? Count(JsonElement value, string key) => Count(MetadataJson.Integer(value, key));
    internal static long? Sum(IEnumerable<long?> values) { var all = values.ToArray(); return all.Length == 0 || all.Any(v => v is null) ? null : Count(all.Sum(v => v!.Value)); }
    internal static double? Cost(double? value) => value is double number && double.IsFinite(number) && number is >= 0 and <= 1_000_000_000 ? number : null;
    public static SessionUsage? Normalize(SessionUsage? value)
    {
        if (value is null || !Wire.Providers.Contains(value.Provider) || !Scope(value.TokenScope) || string.IsNullOrEmpty(value.Source) || value.Source != ActivitySupport.Clean(value.Source, 80, true) || AgentRunTiming.Parse(value.UpdatedAt) is null) return null;
        var input = Count(value.InputTokens); var output = Count(value.OutputTokens); var cost = Cost(value.CostUSD);
        var limits = value.RateLimits?.Where(v => v is not null && !string.IsNullOrEmpty(v.Kind) && v.Kind == ActivitySupport.Clean(v.Kind, 80, true)).Take(16).DistinctBy(v => v.Kind).Select(v => v with { PercentUsed = v.PercentUsed is double p && double.IsFinite(p) && p >= 0 && p <= (v.Kind == "spend_limit" ? 1_000_000 : 100) ? p : null, ResetsAt = AgentRunTiming.Parse(v.ResetsAt) is not null ? v.ResetsAt : null }).Where(v => v.PercentUsed is not null || v.ResetsAt is not null).ToList();
        return value with { Model = Wire.Model(value.Model) ? value.Model : null, ProviderSessionId = Wire.Identifier(value.ProviderSessionId) ? value.ProviderSessionId : null, InputTokens = input, OutputTokens = output, CacheReadTokens = Count(value.CacheReadTokens) is { } read && (input is null || read <= input) ? read : null, CacheWriteTokens = Count(value.CacheWriteTokens) is { } write && (input is null || write <= input) ? write : null, ReasoningTokens = Count(value.ReasoningTokens) is { } reason && (output is null || reason <= output) ? reason : null, TotalTokens = Count(value.TotalTokens), ContextUsedTokens = Count(value.ContextUsedTokens), ContextWindowTokens = value.ContextWindowTokens is > 0 and <= 1_000_000_000 ? value.ContextWindowTokens : null, CostUSD = cost, CostScope = cost is not null && Scope(value.CostScope) ? value.CostScope : null, RateLimits = limits, RateLimitsUpdatedAt = AgentRunTiming.Parse(value.RateLimitsUpdatedAt) is not null ? value.RateLimitsUpdatedAt : null };
    }
}
public sealed class SessionUsageConverter : JsonConverter<SessionUsage>
{
    public override SessionUsage? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader); var v = document.RootElement;
        var rawLimits = MetadataJson.Property(v, "rateLimits");
        var limits = rawLimits.ValueKind == JsonValueKind.Array ? rawLimits.EnumerateArray().Take(16).Select(r => new SessionRateLimit(r.Text("kind") ?? "", MetadataJson.Number(r, "percentUsed"), r.Text("resetsAt"))).ToList() : null;
        return SessionUsageSupport.Normalize(new() { Provider = v.Text("provider") ?? "", Source = v.Text("source") ?? "", TokenScope = v.Text("tokenScope") ?? "", Model = v.Text("model"), ProviderSessionId = v.Text("providerSessionId"), InputTokens = MetadataJson.Integer(v, "inputTokens"), OutputTokens = MetadataJson.Integer(v, "outputTokens"), CacheReadTokens = MetadataJson.Integer(v, "cacheReadTokens"), CacheWriteTokens = MetadataJson.Integer(v, "cacheWriteTokens"), ReasoningTokens = MetadataJson.Integer(v, "reasoningTokens"), TotalTokens = MetadataJson.Integer(v, "totalTokens"), ContextUsedTokens = MetadataJson.Integer(v, "contextUsedTokens"), ContextWindowTokens = MetadataJson.Integer(v, "contextWindowTokens"), CostUSD = MetadataJson.Number(v, "costUSD"), CostScope = v.Text("costScope"), RateLimits = limits, RateLimitsUpdatedAt = v.Text("rateLimitsUpdatedAt"), UpdatedAt = v.Text("updatedAt") ?? "" });
    }
    public override void Write(Utf8JsonWriter writer, SessionUsage value, JsonSerializerOptions options) => MetadataJson.Write(writer, options, ("provider", value.Provider), ("source", value.Source), ("tokenScope", value.TokenScope), ("model", value.Model), ("providerSessionId", value.ProviderSessionId), ("inputTokens", value.InputTokens), ("outputTokens", value.OutputTokens), ("cacheReadTokens", value.CacheReadTokens), ("cacheWriteTokens", value.CacheWriteTokens), ("reasoningTokens", value.ReasoningTokens), ("totalTokens", value.TotalTokens), ("contextUsedTokens", value.ContextUsedTokens), ("contextWindowTokens", value.ContextWindowTokens), ("costUSD", value.CostUSD), ("costScope", value.CostScope), ("rateLimits", value.RateLimits), ("rateLimitsUpdatedAt", value.RateLimitsUpdatedAt), ("updatedAt", value.UpdatedAt));
}

// One process owns this tracker. Counts replace snapshots; resume totals are
// never summed and cannot stand in for the model's current context occupancy.
internal sealed class SessionUsageTracker
{
    private SessionUsage value;
    private string? emitted;
    private readonly Action<SessionUsage>? callback;
    private long lastModSequence = -1;
    private bool authoritativeContext, resultSeen;
    internal SessionUsageTracker(string provider, Action<SessionUsage>? callback) { value = Initial(provider); this.callback = callback; }
    private static SessionUsage Initial(string provider) => new() { Provider = provider, Source = provider + ".stream-json", TokenScope = provider == "claude" ? "response" : "session" };
    internal void Consume(JsonElement root)
    {
        if (callback is null) return;
        var type = root.Text("type");
        if (value.Provider == "claude") { Claude(root, type); return; }
        if (value.Provider == "codex")
        {
            if (type == "thread.started") { Identity(root.Text("thread_id")); Publish(); }
            var usage = MetadataJson.Property(root, "usage");
            if (type == "turn.completed" && SessionUsageSupport.Count(usage, "input_tokens") is { } input && SessionUsageSupport.Count(usage, "output_tokens") is { } output)
            { value = value with { Source = "codex.exec", TokenScope = "session", InputTokens = input, OutputTokens = output, CacheReadTokens = SessionUsageSupport.Count(usage, "cached_input_tokens"), CacheWriteTokens = SessionUsageSupport.Count(usage, "cache_write_input_tokens"), ReasoningTokens = SessionUsageSupport.Count(usage, "reasoning_output_tokens"), TotalTokens = SessionUsageSupport.Sum([input, output]) }; Publish(); }
        }
        else
        {
            if (type == "init") { Identity(root.Text("session_id")); Model(root.Text("model")); Publish(); }
            var stats = MetadataJson.Property(root, "stats");
            if (type == "result" && SessionUsageSupport.Count(stats, "input_tokens") is { } input && SessionUsageSupport.Count(stats, "output_tokens") is { } output && SessionUsageSupport.Count(stats, "total_tokens") is { } total)
            {
                value = value with { InputTokens = input, OutputTokens = output, TotalTokens = total, CacheReadTokens = SessionUsageSupport.Count(stats, "cached") };
                var models = MetadataJson.Property(stats, "models"); if (models.ValueKind == JsonValueKind.Object && models.EnumerateObject().Count() == 1) Model(models.EnumerateObject().First().Name);
                Publish();
            }
        }
    }
    internal void ConsumeMod(JsonElement root)
    {
        if (value.Provider != "claude" || root.Text("event") != "session.usage" || root.Text("agentId") is not null) return;
        var incoming = MetadataJson.Property(root, "usage").Deserialize<SessionUsage>(Wire.Json);
        if (incoming is null || incoming.Provider != "claude") return;
        Identity(root.Text("claudeSessionId"));
        if (MetadataJson.Integer(root, "sequence") is { } sequence) { if (sequence <= lastModSequence) return; lastModSequence = sequence; }
        authoritativeContext = true;
        value = value with { ContextUsedTokens = incoming.ContextUsedTokens, ContextWindowTokens = incoming.ContextWindowTokens, Model = incoming.Model ?? value.Model, Source = "claude.mods+stream-json" };
        if (incoming.CostUSD is { } cost) value = value with { CostUSD = cost, CostScope = "session" };
        if (incoming.RateLimits is not null) value = value with { RateLimits = incoming.RateLimits, RateLimitsUpdatedAt = incoming.RateLimitsUpdatedAt ?? incoming.UpdatedAt };
        Publish();
    }
    private void Claude(JsonElement root, string? type)
    {
        if (MetadataJson.Property(root, "parent_tool_use_id").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) return;
        Identity(root.Text("session_id"));
        if (type == "system" && root.Text("subtype") == "init") { Model(root.Text("model")); Publish(); }
        if (type == "system" && root.Text("subtype") == "compact_boundary") { value = value with { ContextUsedTokens = null }; authoritativeContext = false; Publish(); }
        var message = MetadataJson.Property(root, "message");
        if (type == "assistant" && message.ValueKind == JsonValueKind.Object)
        {
            var oldModel = value.Model; Model(message.Text("model")); if (oldModel != value.Model) { value = value with { ContextWindowTokens = null }; authoritativeContext = false; }
            var usage = MetadataJson.Property(message, "usage");
            if (usage.ValueKind == JsonValueKind.Object)
            {
                if (ClaudeInput(usage) is not { } input || SessionUsageSupport.Count(usage, "output_tokens") is null) return;
                if (!resultSeen) { ClaudeCounts(usage); value = value with { TokenScope = "response" }; }
                value = value with { ContextUsedTokens = input };
            }
            Publish();
        }
        if (type != "result" || ClaudeStream.IsNotificationResult(root)) return;
        resultSeen = true; value = value with { TokenScope = "run" };
        var models = MetadataJson.Property(root, "modelUsage");
        if (models.ValueKind == JsonValueKind.Object && models.EnumerateObject().Count() is > 0 and <= 128)
        {
            var rows = models.EnumerateObject().ToArray();
            if (rows.All(p => p.Value.ValueKind == JsonValueKind.Object))
            {
                value = value with { InputTokens = SessionUsageSupport.Sum(rows.Select(p => SessionUsageSupport.Sum([SessionUsageSupport.Count(p.Value, "inputTokens"), SessionUsageSupport.Count(p.Value, "cacheReadInputTokens"), SessionUsageSupport.Count(p.Value, "cacheCreationInputTokens")]))), OutputTokens = SessionUsageSupport.Sum(rows.Select(p => SessionUsageSupport.Count(p.Value, "outputTokens"))), CacheReadTokens = SessionUsageSupport.Sum(rows.Select(p => SessionUsageSupport.Count(p.Value, "cacheReadInputTokens"))), CacheWriteTokens = SessionUsageSupport.Sum(rows.Select(p => SessionUsageSupport.Count(p.Value, "cacheCreationInputTokens"))), ReasoningTokens = SessionUsageSupport.Sum(rows.Select(p => SessionUsageSupport.Count(p.Value, "thinkingTokens"))) };
                value = value with { TotalTokens = SessionUsageSupport.Sum([value.InputTokens, value.OutputTokens]) };
                if (value.Model is null && rows.Length == 1) Model(rows[0].Name);
                if (!authoritativeContext && value.Model is not null && models.TryGetProperty(value.Model, out var row)) value = value with { ContextWindowTokens = SessionUsageSupport.Count(row, "contextWindow") };
            }
        }
        else { var usage = MetadataJson.Property(root, "usage"); if (usage.ValueKind == JsonValueKind.Object) ClaudeCounts(usage); }
        if (value.CostScope != "session" && SessionUsageSupport.Cost(MetadataJson.Number(root, "total_cost_usd")) is { } cost) value = value with { CostUSD = cost, CostScope = "run" };
        Publish();
    }
    private static long? ClaudeInput(JsonElement usage) => SessionUsageSupport.Sum([SessionUsageSupport.Count(usage, "input_tokens"), MetadataJson.Property(usage, "cache_read_input_tokens").ValueKind == JsonValueKind.Undefined ? 0 : SessionUsageSupport.Count(usage, "cache_read_input_tokens"), MetadataJson.Property(usage, "cache_creation_input_tokens").ValueKind == JsonValueKind.Undefined ? 0 : SessionUsageSupport.Count(usage, "cache_creation_input_tokens")]);
    private void ClaudeCounts(JsonElement usage)
    { value = value with { InputTokens = ClaudeInput(usage), OutputTokens = SessionUsageSupport.Count(usage, "output_tokens"), CacheReadTokens = SessionUsageSupport.Count(usage, "cache_read_input_tokens"), CacheWriteTokens = SessionUsageSupport.Count(usage, "cache_creation_input_tokens"), ReasoningTokens = null }; value = value with { TotalTokens = SessionUsageSupport.Sum([value.InputTokens, value.OutputTokens]) }; }
    private void Model(string? model) { if (Wire.Model(model)) value = value with { Model = model }; }
    private void Identity(string? id)
    { if (!Wire.Identifier(id)) return; if (value.ProviderSessionId is not null && value.ProviderSessionId != id) { value = Initial(value.Provider); authoritativeContext = resultSeen = false; lastModSequence = -1; } value = value with { ProviderSessionId = id }; }
    private void Publish()
    {
        if (callback is null || SessionUsageSupport.Normalize(value) is not { } clean) return;
        var key = JsonSerializer.Serialize(clean with { UpdatedAt = "" }, Wire.Json);
        if (key == emitted) return; emitted = key; value = clean with { UpdatedAt = Wire.Now() }; callback(value);
    }
}
