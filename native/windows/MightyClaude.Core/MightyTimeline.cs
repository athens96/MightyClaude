using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MightyClaude.Core;

/// <summary>Mac MightyTimeline: the diagram's requests and blocks in their recorded order.</summary>
public static class MightyTimeline
{
    public sealed record Node(DesignTone Tone, bool Ring, DesignTone? Rail);
    public sealed record Row(string NodeId, int? AgentIndex, string Kind, string Title, string Status,
        Node Node, string? Latest, double? DurationMs);
    public sealed record Tally(int Total, int Settled, int Running);
    public sealed record Result(string NodeId, DesignTone Tone, string? Text);
    public sealed record Group(int RunIndex, string RunId, int Ordinal, string Status, string Input,
        IReadOnlyList<Row> Rows, Tally Tally, Result? Result);

    public static string? NormalizeMode(string? value) => value is "diagram" or "timeline" ? value : null;
    public static string Mode(RunSession session) => NormalizeMode(session.GraphViewMode) ?? "diagram";
    public static RunSession ApplyMode(RunSession session, string value) =>
        session.Kind == "claude" && MightyGraphViewModel.ShowsModeSwitch(session) && NormalizeMode(value) is { } mode
            ? session with { GraphViewMode = mode } : session;
    // The mobile timeline and Mac timeline share these buckets. Unknown/queued/idle
    // are unfinished work, never a fabricated completion.
    public static string Status(string value) => value switch
    {
        "completed" => "completed", "error" or "failed" => "error",
        "stopped" or "cancelled" or "interrupted" => "stopped", "waiting" => "waiting", _ => "running",
    };
    public static Node NodeFor(string status)
    {
        status = Status(status); var tone = StatusGlyph.Tone(status);
        return new(tone, status == "running", status is "completed" or "running" ? tone : null);
    }
    public static DesignTone? RailAbove(IReadOnlyList<Row> rows, int index) => index > 0 && index < rows.Count ? rows[index - 1].Node.Rail : null;
    public static bool GroupOpen(Group group, int count, IReadOnlySet<string> flipped) => (group.RunIndex == count - 1) != flipped.Contains(group.RunId);
    public static bool Folds(string text) => new StringInfo(text).LengthInTextElements > 600 || text.Count(c => c == '\n') >= 8;
    public static List<Group> Groups(IReadOnlyList<MightyGraphRun> runs) => runs.Select(Project).ToList();

    private static Group Project(MightyGraphRun run, int index)
    {
        var rows = new List<Row>();
        void Add(int? agentIndex, string suffix, string kind, string title, string rawStatus, IReadOnlyList<LogEntry> entries)
        {
            var status = Status(rawStatus);
            rows.Add(new(MightyGraphLayout.NodeID(run, suffix), agentIndex, kind, title, status, NodeFor(status),
                status is "running" or "waiting" ? Latest(entries) : null, Duration(entries, status)));
        }
        Add(null, "request", "main", Locale.Get("graph.timeline.requestOrdinal", new Dictionary<string, string> { ["n"] = (index + 1).ToString(CultureInfo.InvariantCulture) }), run.Status, run.RootEntries);
        for (var i = 0; i < run.Agents.Count; i++)
        {
            var agent = run.Agents[i];
            Add(i, "agent:" + agent.Id, MightyGraphSupport.BlockKind(agent), MightyGraphSupport.BlockTitle(agent), agent.Status, agent.Entries);
        }
        Result? result = null;
        if (MightyGraphLayout.Finished(run))
        {
            var tone = StatusGlyph.Tone(Status(run.Status));
            result = new(MightyGraphLayout.NodeID(run, "result"), tone == DesignTone.Run ? DesignTone.Done : tone,
                run.ResultEntries.LastOrDefault(e => e.Kind == "assistant" && e.Text.Length > 0)?.Text);
        }
        return new(index, run.Id, index + 1, Status(run.Status), run.Input, rows,
            new(rows.Count, rows.Count(r => r.Status is "completed" or "error" or "stopped"), rows.Count(r => r.Status == "running")), result);
    }

    private static string? Latest(IReadOnlyList<LogEntry> entries)
    {
        foreach (var entry in entries.Reverse())
        {
            string? text;
            if (entry.Activity is { } activity)
            {
                if (activity.Kind == "turn") continue;
                text = string.IsNullOrEmpty(activity.Summary) ? activity.ToolName : activity.Summary;
            }
            else
            {
                if (entry.Kind is not ("assistant" or "error") || UserQuestionnaire.Parse(entry.Text) is not null) continue;
                text = entry.Text.TrimStart().Split(['\r', '\n'], 2)[0];
            }
            var clean = ActivitySupport.Clean(text, 512, true).Trim();
            if (clean.Length > 0) return clean;
        }
        return null;
    }
    private static double? Duration(IReadOnlyList<LogEntry> entries, string status)
    {
        if (status is not ("completed" or "error" or "stopped") || entries.Count < 2 ||
            !DateTimeOffset.TryParse(entries[0].Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var first) ||
            !DateTimeOffset.TryParse(entries[^1].Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var last)) return null;
        var milliseconds = (last - first).TotalMilliseconds;
        return milliseconds > 0 && ActivitySupport.ValidDuration(milliseconds) ? Math.Round(milliseconds) : null;
    }
    public static string KindLabel(string kind) => kind switch
    {
        "main" => Locale.Get("graph.timeline.kind.request"), "task" => Locale.Get("graph.block.task"),
        "steer" => Locale.Get("graph.block.steer"), "compact" => Locale.Get("graph.block.compact"),
        "question" => Locale.Get("graph.block.question"), _ => Locale.Get("graph.block.agent"),
    };
    public static string? DurationLabel(double? milliseconds)
    {
        if (milliseconds is not { } ms || !ActivitySupport.ValidDuration(ms)) return null;
        if (ms < 1000) return ((int)ms).ToString(CultureInfo.InvariantCulture) + "ms";
        if (ms < 60000) return Locale.Get("run.activity.durationSeconds", new Dictionary<string, string> { ["seconds"] = (Math.Floor(ms / 100) / 10).ToString("F1", CultureInfo.InvariantCulture) });
        var seconds = (long)(ms / 1000);
        return Locale.Get(seconds % 60 == 0 ? "run.activity.durationMinutes" : "run.activity.durationMinutesSeconds", new Dictionary<string, string> { ["minutes"] = (seconds / 60).ToString(CultureInfo.InvariantCulture), ["seconds"] = (seconds % 60).ToString(CultureInfo.InvariantCulture) });
    }
}

internal sealed class MightyGraphViewModeConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return MightyTimeline.NormalizeMode(reader.GetString());
        reader.Skip(); return null;
    }
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (MightyTimeline.NormalizeMode(value) is { } mode) writer.WriteStringValue(mode); else writer.WriteNullValue();
    }
}
