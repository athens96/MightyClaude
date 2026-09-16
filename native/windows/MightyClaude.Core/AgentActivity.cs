using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

[JsonConverter(typeof(AgentActivityConverter))]
public sealed record AgentActivity(string Id, string Provider, string Kind, string State, string Summary, string? ToolName = null, string? Output = null, double? DurationMs = null);

public static class ActivitySupport
{
    public const int MaximumMessageBytes = 131072, MaximumSummaryBytes = 1000, MaximumOutputBytes = 8192;
    public const double MaximumDurationMs = 30d * 24 * 60 * 60 * 1000;
    public static readonly string[] Kinds = ["turn", "tool", "command", "read", "edit", "search", "web", "agent"];
    public static readonly string[] States = ["running", "waiting", "completed", "error", "stopped"];
    public static bool Terminal(string state) => state is "completed" or "error" or "stopped";
    public static bool ValidDuration(double value) => double.IsFinite(value) && value is >= 0 and <= MaximumDurationMs;
    public static string PrefixUtf8(string value, int maximumBytes)
    {
        var bytes = 0; var result = new StringBuilder();
        foreach (var rune in value.EnumerateRunes()) { if (bytes + rune.Utf8SequenceLength > maximumBytes) break; result.Append(rune); bytes += rune.Utf8SequenceLength; }
        return result.ToString();
    }
    public static string Clean(string? value, int maximumBytes, bool singleLine = false)
    {
        var plain = Regex.Replace(value ?? "", @"\x1b(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1b\\))", "");
        var result = new StringBuilder(); var bytes = 0;
        foreach (var rune in plain.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control && rune.Value is not (9 or 10 or 13)) continue;
            var item = singleLine && Rune.IsWhiteSpace(rune) ? new Rune(' ') : rune;
            if (bytes + item.Utf8SequenceLength > maximumBytes) break;
            result.Append(item); bytes += item.Utf8SequenceLength;
        }
        return result.ToString().Trim();
    }
    public static AgentActivity? Normalize(AgentActivity? value, bool restoring = false)
    {
        if (value is null || !Wire.Identifier(value.Id) || !Wire.Providers.Contains(value.Provider) || !Kinds.Contains(value.Kind) || !States.Contains(value.State)) return null;
        string? Optional(string? text, int bytes, bool line = false) { var clean = Clean(text, bytes, line); return clean.Length == 0 ? null : clean; }
        return value with { Summary = Clean(value.Summary, MaximumSummaryBytes, true), ToolName = Optional(value.ToolName, 160, true), Output = Optional(value.Output, MaximumOutputBytes), DurationMs = value.DurationMs is double duration && ValidDuration(duration) && value.Kind != "turn" && Terminal(value.State) ? duration : null, State = restoring && value.State is "running" or "waiting" ? "stopped" : value.State };
    }
    public static string? DurationLabel(AgentActivity value)
    {
        if (value.Kind == "turn" || !Terminal(value.State) || value.DurationMs is not double ms || !ValidDuration(ms)) return null;
        if (ms < 1) return ms == 0 ? "0ms" : "<1ms";
        if (ms < 1000) return $"{(long)ms}ms";
        if (ms < 60000) return (Math.Floor(ms / 100) / 10).ToString("F1", CultureInfo.InvariantCulture) + "초";
        var seconds = (long)(ms / 1000); return seconds % 60 == 0 ? $"{seconds / 60}분" : $"{seconds / 60}분 {seconds % 60}초";
    }
    internal static string Id(string ns, string key) => "activity-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ns + "|" + key)))[..48].ToLowerInvariant();
    internal static string Kind(string tool) => tool.ToLowerInvariant() switch
    {
        "bash" or "run_shell_command" or "command_execution" or "exec_command" or "shell" => "command",
        "read" or "read_file" or "read_many_files" or "list_directory" or "ls" => "read",
        "edit" or "write" or "write_file" or "replace" or "apply_patch" or "file_change" or "notebookedit" => "edit",
        "grep" or "glob" or "search_file_content" or "glob_search" => "search",
        "websearch" or "webfetch" or "web_search" or "google_web_search" or "web_fetch" => "web",
        "agent" or "task" or "spawn_agent" or "delegate_to_agent" => "agent", _ => "tool"
    };
    internal static string Summary(string tool, JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String && input.GetString() is { Length: <= 65536 } text)
        { try { using var json = JsonDocument.Parse(text); return Summary(tool, json.RootElement); } catch (JsonException) { } }
        foreach (var key in new[] { "command", "file_path", "absolute_path", "path", "pattern", "query", "url", "description", "target_file", "filename", "glob" })
        {
            var value = MetadataJson.Property(input, key);
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } selected) return Clean(selected, MaximumSummaryBytes, true);
            if (value.ValueKind == JsonValueKind.Array) { var parts = value.EnumerateArray().Take(8).Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()).ToArray(); if (parts.Length > 0) return Clean(string.Join(' ', parts), MaximumSummaryBytes, true); }
        }
        var changes = MetadataJson.Property(input, "changes");
        if (changes.ValueKind == JsonValueKind.Array)
        {
            var paths = changes.EnumerateArray().Take(12).Where(c => c.Text("path") is not null).Select(c => (c.Text("kind") is { } kind ? kind + " " : "") + c.Text("path")).ToArray();
            if (paths.Length > 0) return Clean(string.Join(" · ", paths), MaximumSummaryBytes, true);
        }
        return Clean(tool, MaximumSummaryBytes, true);
    }
    internal static string? Output(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return Clean(value.GetString(), MaximumOutputBytes);
        if (value.ValueKind == JsonValueKind.Array) return Clean(string.Join('\n', value.EnumerateArray().Take(32).Where(b => b.Text("type") == "text").Select(b => b.Text("text"))), MaximumOutputBytes);
        if (value.Text("message") is { } message) return Clean(message, MaximumOutputBytes);
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty("content", out var content) ? Output(content) : null;
    }
}

// Optional metadata is independently decoded. Bad measurements must never erase
// an otherwise valid conversation or make an entire remote poll undecodable.
internal static class MetadataJson
{
    internal static JsonElement Property(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var child) ? child : default;
    internal static long? Integer(JsonElement value, string key) { var item = Property(value, key); return item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var number) ? number : null; }
    internal static double? Number(JsonElement value, string key) { var item = Property(value, key); return item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var number) && double.IsFinite(number) ? number : null; }
    internal static bool Flag(JsonElement value, string key) => Property(value, key).ValueKind == JsonValueKind.True;
    internal static void Write(Utf8JsonWriter writer, JsonSerializerOptions options, params (string Key, object? Value)[] fields)
    { writer.WriteStartObject(); foreach (var (key, value) in fields) { if (value is null) continue; writer.WritePropertyName(key); JsonSerializer.Serialize(writer, value, value.GetType(), options); } writer.WriteEndObject(); }
}
public sealed class AgentActivityConverter : JsonConverter<AgentActivity>
{
    public override AgentActivity? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    { using var doc = JsonDocument.ParseValue(ref reader); var v = doc.RootElement; return ActivitySupport.Normalize(new(v.Text("id") ?? "", v.Text("provider") ?? "", v.Text("kind") ?? "", v.Text("state") ?? "", v.Text("summary") ?? "", v.Text("toolName"), v.Text("output"), MetadataJson.Number(v, "durationMs"))); }
    public override void Write(Utf8JsonWriter writer, AgentActivity value, JsonSerializerOptions options) => MetadataJson.Write(writer, options, ("id", value.Id), ("provider", value.Provider), ("kind", value.Kind), ("state", value.State), ("summary", value.Summary), ("toolName", value.ToolName), ("output", value.Output), ("durationMs", value.DurationMs));
}
