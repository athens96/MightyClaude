using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MightyClaude.Core;

[JsonConverter(typeof(AgentRunTimingConverter))]
public sealed record AgentRunTiming(DateTimeOffset StartedAt, DateTimeOffset LastObservedAt, DateTimeOffset? FinishedAt = null, bool IsApproximate = false)
{
    public static AgentRunTiming Begin(DateTimeOffset? at = null) { var now = at ?? DateTimeOffset.UtcNow; return new(now, now); }
    public bool IsValid => LastObservedAt >= StartedAt && (FinishedAt is null || FinishedAt >= LastObservedAt);
    public AgentRunTiming Observe(DateTimeOffset? at = null) => FinishedAt is not null ? this : this with { LastObservedAt = new[] { StartedAt, LastObservedAt, at ?? DateTimeOffset.UtcNow }.Max() };
    public AgentRunTiming Finish(DateTimeOffset? at = null) { if (FinishedAt is not null) return this; var value = Observe(at); return value with { FinishedAt = value.LastObservedAt }; }
    public AgentRunTiming Interrupt() => FinishedAt is not null ? this : this with { FinishedAt = LastObservedAt, IsApproximate = true };
    public double Elapsed(DateTimeOffset? at = null) => IsValid ? Math.Max(0, ((FinishedAt ?? at ?? DateTimeOffset.UtcNow) - StartedAt).TotalSeconds) : 0;
    public string Label(DateTimeOffset? at = null)
    {
        var seconds = (long)Elapsed(at); var label = seconds >= 3600 ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}" : $"{seconds / 60:00}:{seconds % 60:00}";
        return IsApproximate ? "약 " + label : label;
    }
    public static AgentRunTiming? Infer(IEnumerable<LogEntry> logs)
    {
        var entries = logs.ToList(); var index = entries.FindLastIndex(e => e.Kind == "user");
        if (index < 0 || Parse(entries[index].Timestamp) is not { } start) return null;
        var dates = entries.Skip(index + 1).Where(e => e.Kind is "assistant" or "output" or "error" || e.Activity is not null).Select(e => Parse(e.Timestamp)).Where(d => d > start).ToArray();
        return dates.Length == 0 ? null : new(start, dates.Max()!.Value, dates.Max(), true);
    }
    internal static DateTimeOffset? Parse(string? value) => value is { Length: > 0 and <= 80 } && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : null;
}
public sealed class AgentRunTimingConverter : JsonConverter<AgentRunTiming>
{
    public override AgentRunTiming? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader); var v = document.RootElement;
        if (AgentRunTiming.Parse(v.Text("startedAt")) is not { } start) return null;
        var end = AgentRunTiming.Parse(v.Text("finishedAt")); var observed = AgentRunTiming.Parse(v.Text("lastObservedAt")) ?? end ?? start;
        if (MetadataJson.Property(v, "finishedAt").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) && end is null || MetadataJson.Property(v, "lastObservedAt").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) && AgentRunTiming.Parse(v.Text("lastObservedAt")) is null) return null;
        var value = new AgentRunTiming(start, observed, end, MetadataJson.Flag(v, "isApproximate")); return value.IsValid ? value : null;
    }
    public override void Write(Utf8JsonWriter writer, AgentRunTiming value, JsonSerializerOptions options) => MetadataJson.Write(writer, options, ("startedAt", value.StartedAt.ToString("O")), ("lastObservedAt", value.LastObservedAt.ToString("O")), ("finishedAt", value.FinishedAt?.ToString("O")), ("isApproximate", value.IsApproximate));
}
