using System.Text;
using System.Text.Json;

namespace MightyClaude.Core;

/// <summary>One hunk of Claude Code's <c>structuredPatch</c>: lines are <c>" ctx"</c>, <c>"-old"</c>, <c>"+new"</c> (and <c>"\ No newline…"</c>).</summary>
public sealed record FilePatchHunk(int OldStart, int OldLines, int NewStart, int NewLines, IReadOnlyList<string> Lines);

/// <summary>
/// What one Edit, Write or MultiEdit result says about the file before it (M/MightyCore/FilePatch.swift FileEdit): its
/// <c>Original</c> text when the CLI sent it, <c>Created</c> when the tool made the file, else the <c>Patch</c> the tool
/// applied, to be undone against the file on disk. None of the three: the original is unknown.
/// </summary>
public sealed record FileEdit(string? Original, bool Created, IReadOnlyList<FilePatchHunk>? Patch)
{
    public static FileEdit Unknown { get; } = new(null, false, null);
}

/// <summary>
/// Undoing a <c>structuredPatch</c> on the file as it is just after the edit (M/MightyCore/FilePatch.swift,
/// native/contracts/fixtures/file-diff.json <c>reversePatch</c>): the result is the file before the edit, or null when the
/// patch does not fit the file exactly.
/// </summary>
public static class FilePatch
{
    public const int MaximumHunks = 10_000;

    /// <summary>The hunks of a <c>structuredPatch</c> array; null when it is not one.</summary>
    public static IReadOnlyList<FilePatchHunk>? Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaximumHunks) return null;
        var hunks = new List<FilePatchHunk>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !Number(item, "oldStart", out var oldStart) || !Number(item, "oldLines", out var oldLines)
                || !Number(item, "newStart", out var newStart) || !Number(item, "newLines", out var newLines)
                || !item.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array) return null;
            var texts = new List<string>();
            foreach (var line in lines.EnumerateArray()) { if (line.ValueKind != JsonValueKind.String) return null; texts.Add(line.GetString()!); }
            hunks.Add(new(oldStart, oldLines, newStart, newLines, texts));
        }
        return hunks;
    }

    private static bool Number(JsonElement item, string name, out int value)
    {
        value = 0;
        return item.TryGetProperty(name, out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out value) && value >= 0;
    }

    private static string Strip(string text) => text.EndsWith('\r') ? text[..^1] : text;

    /// <summary>
    /// The file before the patch, from <paramref name="current"/> (the file after it). Each hunk's new side (its context
    /// and added lines) must stand exactly at its <c>newStart</c>, in order, a trailing CR aside; its removed lines come
    /// back with the file's own line break (CRLF when the file has one). A <c>\</c> line (no newline at end of file), a
    /// count that disagrees with the lines, or any mismatch: null.
    /// </summary>
    public static string? ReverseApply(string current, IReadOnlyList<FilePatchHunk> hunks)
    {
        var lines = new List<(string Body, string Ending)>();
        for (var start = 0; start < current.Length;)
        {
            var end = current.IndexOf('\n', start);
            if (end < 0) { lines.Add((current[start..], "")); break; }
            var crlf = end > start && current[end - 1] == '\r';
            lines.Add((current[start..(crlf ? end - 1 : end)], crlf ? "\r\n" : "\n")); start = end + 1;
        }
        var ending = current.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var output = new StringBuilder(current.Length);
        var cursor = 0;
        foreach (var hunk in hunks)
        {
            int newSide = 0, oldSide = 0;
            foreach (var line in hunk.Lines)
            {
                if (line.Length == 0 || line[0] is not (' ' or '+' or '-')) return null;
                if (line[0] != '-') newSide++;
                if (line[0] != '+') oldSide++;
            }
            if (newSide != hunk.NewLines || oldSide != hunk.OldLines) return null;
            // A hunk with no new lines has no context either: the file was emptied, so it stands at the end of what is left.
            var at = hunk.NewLines == 0 ? Math.Min(hunk.NewStart, lines.Count) : hunk.NewStart - 1;
            if (at < cursor || at + hunk.NewLines > lines.Count) return null;
            for (var i = cursor; i < at; i++) output.Append(lines[i].Body).Append(lines[i].Ending);
            foreach (var line in hunk.Lines)
            {
                var text = Strip(line[1..]);
                if (line[0] == '-') { output.Append(text).Append(ending); continue; }
                if (lines[at].Body != text) return null;
                if (line[0] == ' ') output.Append(lines[at].Body).Append(lines[at].Ending);
                at++;
            }
            cursor = at;
        }
        for (var i = cursor; i < lines.Count; i++) output.Append(lines[i].Body).Append(lines[i].Ending);
        return output.ToString();
    }
}
