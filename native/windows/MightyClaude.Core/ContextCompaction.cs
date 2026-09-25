using System.Globalization;
using System.Text.Json;

namespace MightyClaude.Core;

public static class ContextCompaction
{
    public static string Title => Locale.Get("graph.block.compact");

    public static string ClaudeSummary(JsonElement? metadata)
    {
        var fields = metadata is { ValueKind: JsonValueKind.Object } m ? m : default;
        var trigger = fields.ValueKind == JsonValueKind.Object
            && fields.TryGetProperty("trigger", out var trig) && trig.GetString() == "manual"
            ? Locale.Get("graph.compaction.manual")
            : Locale.Get("graph.compaction.auto");
        var parts = new List<string> { trigger };

        var pre = GetTokens(fields, "pre_tokens");
        var post = GetTokens(fields, "post_tokens");
        if (pre != null && post != null)
            parts.Add(Locale.Get("graph.compaction.tokenRange", new Dictionary<string, string> { ["before"] = Format(pre.Value), ["after"] = Format(post.Value) }));
        else if (pre != null)
            parts.Add(Locale.Get("graph.compaction.tokenFrom", new Dictionary<string, string> { ["before"] = Format(pre.Value) }));

        var summarized = GetTokens(fields, "messages_summarized");
        if (summarized is > 0)
            parts.Add(Locale.Get("graph.compaction.messageCount", new Dictionary<string, string> { ["count"] = Format(summarized.Value) }));

        var durationMs = GetTokens(fields, "duration_ms");
        if (durationMs is > 0)
            parts.Add(Duration(durationMs.Value));

        return string.Join(" · ", parts);
    }

    public static string CodexSummary => Locale.Get("graph.compaction.codex");

    private static int? GetTokens(JsonElement el, string key)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= 0) return n;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) && d >= 0 && d <= int.MaxValue) return (int)d;
        return null;
    }

    private static string Format(int value) =>
        value.ToString("N0", new NumberFormatInfo { NumberGroupSeparator = ",", NumberGroupSizes = [3], NumberDecimalDigits = 0 });

    private static string Duration(int milliseconds)
    {
        if (milliseconds < 1000)
            return Locale.Get("graph.compaction.durationMs", new Dictionary<string, string> { ["n"] = milliseconds.ToString(CultureInfo.InvariantCulture) });
        var seconds = ((double)milliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
        if (seconds.EndsWith(".0", StringComparison.Ordinal)) seconds = seconds[..^2];
        return Locale.Get("graph.compaction.durationSeconds", new Dictionary<string, string> { ["n"] = seconds });
    }
}
