using System.Text;
using System.Text.Json;

namespace MightyCode.Core;

/// <summary>One hunk of Claude Code's <c>structuredPatch</c>: lines are <c>" ctx"</c>, <c>"-old"</c>, <c>"+new"</c> (and <c>"\ No newline…"</c>).</summary>
public sealed record FilePatchHunk(int OldStart, int OldLines, int NewStart, int NewLines, IReadOnlyList<string> Lines);

/// <summary>
/// What one Edit, Write or MultiEdit result says about the file before it (M/MightyCore/FilePatch.swift FileEdit): its
/// <c>Original</c> text when the CLI sent it, <c>Created</c> when the tool made the file, and the <c>Patch</c> the tool
/// applied (with no original, to be undone against the file on disk; with one, to follow the run's later edits of the file).
/// No original, not created and no patch: the original is unknown.
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
    private const char ByteOrderMark = '\uFEFF';

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

    /// <summary>A text without the byte order mark it may start with (the files are read without one).</summary>
    public static string WithoutByteOrderMark(string text) => text.Length > 0 && text[0] == ByteOrderMark ? text[1..] : text;

    /// <summary>The same hunks the other way round: undoing them on the file before the patch gives the file after it.</summary>
    public static IReadOnlyList<FilePatchHunk> Inverted(IReadOnlyList<FilePatchHunk> hunks) =>
        hunks.Select(h => new FilePatchHunk(h.NewStart, h.NewLines, h.OldStart, h.OldLines,
            h.Lines.Select(l => l.Length == 0 ? l : l[0] == '+' ? "-" + l[1..] : l[0] == '-' ? "+" + l[1..] : l).ToList())).ToList();

    /// <summary>
    /// The file before the patch, from <paramref name="current"/> (the file after it). Each hunk's new side (its context
    /// and added lines) must stand exactly at its <c>newStart</c>, in order, a trailing CR aside, and its old side must then
    /// start at its <c>oldStart</c> in the result. A removed line comes back with the line break of the hunk's nearest context
    /// line (before it on a tie; with none, the file's: CRLF when it has one). A <c>\</c> line (jsdiff's "No newline at end of
    /// file") after a removed line restores it without a break; after a context or added line, that line on disk must have
    /// none. A byte order mark before line 1 is read past, on disk and in the patch, and kept. A count that disagrees with the
    /// lines, a line without a break before the end, or any mismatch: null.
    /// </summary>
    public static string? ReverseApply(string current, IReadOnlyList<FilePatchHunk> hunks)
    {
        var mark = current.Length > 0 && current[0] == ByteOrderMark;
        if (mark) current = current[1..];
        var lines = new List<(string Body, string Ending)>();
        for (var start = 0; start < current.Length;)
        {
            var end = current.IndexOf('\n', start);
            if (end < 0) { lines.Add((current[start..], "")); break; }
            var crlf = end > start && current[end - 1] == '\r';
            lines.Add((current[start..(crlf ? end - 1 : end)], crlf ? "\r\n" : "\n")); start = end + 1;
        }
        var ending = current.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var output = new StringBuilder(current.Length + 1);
        if (mark) output.Append(ByteOrderMark);
        var written = 0; var closed = false;
        bool Emit(string body, string end)
        {
            // Only a file's last line goes without a break.
            if (closed) return false;
            output.Append(body).Append(end); written++; closed = end.Length == 0;
            return true;
        }
        var cursor = 0;
        foreach (var hunk in hunks)
        {
            var count = hunk.Lines.Count;
            int newSide = 0, oldSide = 0;
            for (var i = 0; i < count; i++)
            {
                var line = hunk.Lines[i];
                if (Marker(hunk.Lines, i)) continue;
                if (line.Length == 0 || line[0] is not (' ' or '+' or '-')) return null;
                if (line[0] != '-') newSide++;
                if (line[0] != '+') oldSide++;
            }
            if (newSide != hunk.NewLines || oldSide != hunk.OldLines) return null;
            // A hunk with no new lines has no context either: the file was emptied, so it stands at the end of what is left.
            var at = hunk.NewLines == 0 ? Math.Min(hunk.NewStart, lines.Count) : hunk.NewStart - 1;
            if (at < cursor || at + hunk.NewLines > lines.Count) return null;
            for (var i = cursor; i < at; i++) if (!Emit(lines[i].Body, lines[i].Ending)) return null;
            // The old side starts at oldStart (with no old lines: after line oldStart, or at the top written as 0 or 1).
            if (hunk.OldLines > 0 ? written != hunk.OldStart - 1 : written != hunk.OldStart && written != hunk.OldStart - 1) return null;
            // Where each new-side line stands on disk, and the nearest context line (with a break) before and after each line.
            var disk = new int[count]; var before = new int[count]; var after = new int[count];
            for (int i = 0, position = at, seen = -1; i < count; i++)
            {
                disk[i] = hunk.Lines[i][0] is ' ' or '+' ? position++ : -1;
                before[i] = seen;
                if (hunk.Lines[i][0] == ' ' && lines[disk[i]].Ending.Length > 0) seen = i;
            }
            for (int i = count - 1, seen = -1; i >= 0; i--)
            {
                after[i] = seen;
                if (hunk.Lines[i][0] == ' ' && lines[disk[i]].Ending.Length > 0) seen = i;
            }
            int oldNumber = hunk.OldStart;
            for (var i = 0; i < count; i++)
            {
                var line = hunk.Lines[i];
                if (line[0] == '\\') continue;
                var bare = i + 1 < count && Marker(hunk.Lines, i + 1);
                if (line[0] == '-')
                {
                    var near = before[i] >= 0 && (after[i] < 0 || i - before[i] <= after[i] - i) ? before[i] : after[i];
                    if (!Emit(Text(line, oldNumber), bare ? "" : near >= 0 ? lines[disk[near]].Ending : ending)) return null;
                    oldNumber++;
                    continue;
                }
                var (body, end) = lines[disk[i]];
                if (body != Text(line, disk[i] + 1) || bare && end.Length > 0) return null;
                if (line[0] == ' ') { if (!Emit(body, end)) return null; oldNumber++; }
            }
            cursor = at + hunk.NewLines;
        }
        for (var i = cursor; i < lines.Count; i++) if (!Emit(lines[i].Body, lines[i].Ending)) return null;
        return output.ToString();
    }

    /// <summary>A <c>\</c> line right after a line of a side (not first, not after another marker).</summary>
    private static bool Marker(IReadOnlyList<string> lines, int index) =>
        index > 0 && lines[index] is ['\\', ..] && lines[index - 1] is [' ' or '+' or '-', ..];

    /// <summary>A patch line's text, a trailing CR aside; line 1's without the byte order mark the file is read past.</summary>
    private static string Text(string line, int number)
    {
        var text = line.EndsWith('\r') ? line[1..^1] : line[1..];
        return number == 1 ? WithoutByteOrderMark(text) : text;
    }
}
