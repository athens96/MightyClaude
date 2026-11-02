namespace MightyClaude.Core;

/// <summary>One line of a hunk: <c>" "</c> context, <c>"+"</c> added, <c>"-"</c> removed. <c>NoNewline</c> marks the file's last line when it has no line break.</summary>
public sealed record FileDiffLine(string Op, string Text)
{
    public bool? NoNewline { get; init; }
}

/// <summary>A unified-diff hunk. A side with no lines starts at the line before it (0 at the top of the file), as git writes it.</summary>
public sealed record FileDiffHunk(int OldStart, int OldLines, int NewStart, int NewLines, IReadOnlyList<FileDiffLine> Lines);

/// <summary>
/// What <see cref="FileDiff.Compute"/> found. <c>Added</c> and <c>Removed</c> count the whole change even when <c>Truncated</c>
/// cut the hunks; <c>Unchanged</c> is true (and the hunks empty) when both sides are the same text once CRLF reads as LF.
/// </summary>
public sealed record FileDiffResult(IReadOnlyList<FileDiffHunk> Hunks, int Added, int Removed, bool Truncated, bool Unchanged);

/// <summary>
/// The line diff of the source preview's 변경사항 view (M/MightyCore/FileDiff.swift, native/contracts/fixtures/file-diff.json):
/// CRLF compared as LF; lines compared by their exact characters (no Unicode normalization) and their line break; Myers'
/// shortest edit script after the common start and end are set aside, each run of changes written removals first; 3 lines
/// of context, hunks whose changes stand at most 6 equal lines apart merged. Each side is at most 1 MiB (UTF-8) and 20,000
/// lines and holds no NUL, else there is no diff (null); an edit script longer than <see cref="MaximumEditDistance"/> reads
/// as the whole middle replaced. At most 5,000 hunk lines are written, then <c>Truncated</c>.
/// </summary>
public static class FileDiff
{
    public const int Context = 3;
    public const int MaximumSideBytes = 1_048_576;
    public const int MaximumSideLines = 20_000;
    public const int MaximumOutputLines = 5_000;
    /// <summary>Past this many edits Myers' trace (its square in memory) is not kept: the middle is shown as removed then added.</summary>
    public const int MaximumEditDistance = 2_000;

    /// <summary>A line as compared: its text and whether a line break ends it.</summary>
    private readonly record struct Line(string Text, bool Terminated);

    /// <summary>The lines of <paramref name="text"/> after CRLF becomes LF; a final line break opens no further line.</summary>
    private static List<Line> Lines(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = new List<Line>();
        var start = 0;
        while (start < normalized.Length)
        {
            var end = normalized.IndexOf('\n', start);
            if (end < 0) { lines.Add(new(normalized[start..], false)); break; }
            lines.Add(new(normalized[start..end], true)); start = end + 1;
        }
        return lines;
    }

    /// <summary>The diff of <paramref name="oldText"/> to <paramref name="newText"/>, or null when either side is over the caps or holds a NUL.</summary>
    public static FileDiffResult? Compute(string oldText, string newText)
    {
        if (!Fits(oldText) || !Fits(newText)) return null;
        var a = Lines(oldText); var b = Lines(newText);
        if (a.Count > MaximumSideLines || b.Count > MaximumSideLines) return null;
        // Every distinct line (text and break) gets a number; Myers compares numbers.
        var ids = new Dictionary<Line, int>();
        int Id(Line line) { if (!ids.TryGetValue(line, out var id)) ids[line] = id = ids.Count; return id; }
        var x = a.Select(Id).ToArray(); var y = b.Select(Id).ToArray();
        if (x.AsSpan().SequenceEqual(y)) return new([], 0, 0, false, true);
        var ops = Script(x, y);
        // Removals before additions in each run of changes.
        var ordered = new List<(char Op, int Old, int New)>(ops.Count);
        for (var i = 0; i < ops.Count;)
        {
            if (ops[i].Op == ' ') { ordered.Add(ops[i]); i++; continue; }
            var end = i; while (end < ops.Count && ops[end].Op != ' ') end++;
            for (var j = i; j < end; j++) if (ops[j].Op == '-') ordered.Add(ops[j]);
            for (var j = i; j < end; j++) if (ops[j].Op == '+') ordered.Add(ops[j]);
            i = end;
        }
        var added = ordered.Count(o => o.Op == '+'); var removed = ordered.Count(o => o.Op == '-');
        // Change runs grouped into hunks: a gap of at most 2 × context equal lines joins two.
        var groups = new List<(int Start, int End)>();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Op == ' ') continue;
            var end = i; while (end + 1 < ordered.Count && ordered[end + 1].Op != ' ') end++;
            if (groups.Count > 0 && i - groups[^1].End - 1 <= 2 * Context) groups[^1] = (groups[^1].Start, end);
            else groups.Add((i, end));
            i = end;
        }
        var hunks = new List<FileDiffHunk>(); var written = 0; var truncated = false;
        foreach (var (first, last) in groups)
        {
            var start = Math.Max(0, first - Context); var stop = Math.Min(ordered.Count - 1, last + Context);
            var count = stop - start + 1;
            if (written + count > MaximumOutputLines) { count = MaximumOutputLines - written; truncated = true; }
            if (count <= 0) break;
            int oldBefore = 0, newBefore = 0;
            for (var i = 0; i < start; i++) { if (ordered[i].Op != '+') oldBefore++; if (ordered[i].Op != '-') newBefore++; }
            var lines = new List<FileDiffLine>(count); int oldLines = 0, newLines = 0;
            for (var i = start; i < start + count; i++)
            {
                var (op, oldIndex, newIndex) = ordered[i];
                var line = op == '+' ? b[newIndex] : a[oldIndex];
                if (op != '+') oldLines++; if (op != '-') newLines++;
                lines.Add(line.Terminated ? new(op.ToString(), line.Text) : new FileDiffLine(op.ToString(), line.Text) { NoNewline = true });
            }
            hunks.Add(new(oldLines == 0 ? oldBefore : oldBefore + 1, oldLines, newLines == 0 ? newBefore : newBefore + 1, newLines, lines));
            written += count;
            if (truncated) break;
        }
        return new(hunks, added, removed, truncated, false);
    }

    /// <summary>The first <paramref name="lines"/> hunk lines of <paramref name="hunks"/>, a hunk cut short counting only the lines it keeps (a reply too big for its frame).</summary>
    public static IReadOnlyList<FileDiffHunk> Cut(IReadOnlyList<FileDiffHunk> hunks, int lines)
    {
        var kept = new List<FileDiffHunk>();
        foreach (var hunk in hunks)
        {
            if (lines <= 0) break;
            if (hunk.Lines.Count <= lines) { kept.Add(hunk); lines -= hunk.Lines.Count; continue; }
            var part = hunk.Lines.Take(lines).ToList();
            int oldLines = part.Count(l => l.Op != "+"), newLines = part.Count(l => l.Op != "-");
            // A start of a side with no lines is the line before it, as for a whole hunk.
            int oldStart = hunk.OldLines == 0 ? hunk.OldStart : hunk.OldStart - 1, newStart = hunk.NewLines == 0 ? hunk.NewStart : hunk.NewStart - 1;
            kept.Add(new(oldLines == 0 ? oldStart : oldStart + 1, oldLines, newLines == 0 ? newStart : newStart + 1, newLines, part));
            break;
        }
        return kept;
    }

    private static bool Fits(string text) => !text.Contains('\0') && System.Text.Encoding.UTF8.GetByteCount(text) <= MaximumSideBytes;

    /// <summary>The edit script in order: <c>' '</c> (both indexes), <c>'-'</c> (old index), <c>'+'</c> (new index).</summary>
    private static List<(char Op, int Old, int New)> Script(int[] a, int[] b)
    {
        var prefix = 0; while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        var suffix = 0; while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;
        var ops = new List<(char, int, int)>(a.Length + b.Length);
        for (var i = 0; i < prefix; i++) ops.Add((' ', i, i));
        ops.AddRange(Middle(a, prefix, a.Length - suffix, b, prefix, b.Length - suffix));
        for (var i = suffix; i > 0; i--) ops.Add((' ', a.Length - i, b.Length - i));
        return ops;
    }

    /// <summary>
    /// The furthest x reachable on diagonal k at step d from step d-1's row (<paramref name="previous"/>, indexed k + d - 1,
    /// -1 where unreached): down from k+1 (an addition) or right from k-1 (a removal), the one that reaches further, down on a
    /// tie; never off the grid. Null when neither move is possible.
    /// </summary>
    private static (int X, bool Down)? Step(int[] previous, int d, int k, int n, int m)
    {
        int? down = null, right = null;
        if (k + 1 <= d - 1 && previous[k + 1 + d - 1] is var fromAbove and >= 0 && fromAbove - k <= m) down = fromAbove;
        if (k - 1 >= -(d - 1) && previous[k - 1 + d - 1] is var fromLeft and >= 0 && fromLeft + 1 <= n) right = fromLeft + 1;
        if (down is null && right is null) return null;
        return right is { } r && (down is not { } dd || r > dd) ? (r, false) : (down!.Value, true);
    }

    private static List<(char, int, int)> Middle(int[] a, int aStart, int aEnd, int[] b, int bStart, int bEnd)
    {
        int n = aEnd - aStart, m = bEnd - bStart;
        var result = new List<(char, int, int)>(n + m);
        if (n == 0 || m == 0)
        {
            for (var i = 0; i < n; i++) result.Add(('-', aStart + i, -1));
            for (var j = 0; j < m; j++) result.Add(('+', -1, bStart + j));
            return result;
        }
        var trace = new List<int[]>();
        int endD = -1, endK = 0;
        for (var d = 0; d <= Math.Min(n + m, MaximumEditDistance) && endD < 0; d++)
        {
            var row = new int[2 * d + 1]; Array.Fill(row, -1);
            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (d == 0) x = 0;
                else if (Step(trace[d - 1], d, k, n, m) is { } step) x = step.X;
                else continue;
                var y = x - k;
                if (y < 0 || y > m) continue;
                while (x < n && y < m && a[aStart + x] == b[bStart + y]) { x++; y++; }
                row[k + d] = x;
                if (x == n && y == m) { endD = d; endK = k; break; }
            }
            trace.Add(row);
        }
        if (endD < 0)
        {
            for (var i = 0; i < n; i++) result.Add(('-', aStart + i, -1));
            for (var j = 0; j < m; j++) result.Add(('+', -1, bStart + j));
            return result;
        }
        // Walk back from (n, m): each step is a snake of equal lines after one edit.
        var reversed = new List<(char, int, int)>(n + m);
        int cx = n, cy = m, ck = endK;
        for (var d = endD; d > 0; d--)
        {
            var (_, down) = Step(trace[d - 1], d, ck, n, m)!.Value;
            var pk = down ? ck + 1 : ck - 1; var px = trace[d - 1][pk + d - 1]; var py = px - pk;
            var sx = down ? px : px + 1; var sy = down ? py + 1 : py;
            while (cx > sx && cy > sy) { cx--; cy--; reversed.Add((' ', aStart + cx, bStart + cy)); }
            reversed.Add(down ? ('+', -1, bStart + py) : ('-', aStart + px, -1));
            cx = px; cy = py; ck = pk;
        }
        while (cx > 0 && cy > 0) { cx--; cy--; reversed.Add((' ', aStart + cx, bStart + cy)); }
        reversed.Reverse();
        return reversed;
    }
}
