namespace MightyClaude.Core;

/// <summary>
/// The files pane's find in the source text (M/FilePaneView.swift:384-385, AppKit's find bar with incremental search):
/// matches ignore case, as the find bar's default "contains" does; Enter goes to the next, Shift+Enter to the previous,
/// both wrapping round. Only the first <see cref="MaximumMatches"/> are marked, since each is a highlight the UI thread draws.
/// </summary>
public static class SourceFind
{
    public const int MaximumMatches = 1_000;

    /// <summary>Where <paramref name="query"/> occurs in <paramref name="text"/>, in order, without overlaps, at most <see cref="MaximumMatches"/>.</summary>
    public static IReadOnlyList<int> Matches(string text, string query)
    {
        var found = new List<int>();
        if (query.Length == 0) return found;
        for (var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase); at >= 0 && found.Count < MaximumMatches; at = at + query.Length >= text.Length ? -1 : text.IndexOf(query, at + query.Length, StringComparison.OrdinalIgnoreCase))
            found.Add(at);
        return found;
    }

    /// <summary>
    /// The match to go to from <paramref name="current"/> (an index into <paramref name="matches"/>, or -1 for none yet,
    /// when the first at or after <paramref name="from"/> is taken), forward or back, wrapping round; -1 with no match.
    /// </summary>
    public static int Step(IReadOnlyList<int> matches, int current, bool forward, int from = 0)
    {
        if (matches.Count == 0) return -1;
        if (current < 0 || current >= matches.Count)
        {
            var next = 0;
            while (next < matches.Count && matches[next] < from) next++;
            return forward ? next % matches.Count : (next - 1 + matches.Count) % matches.Count;
        }
        return (current + (forward ? 1 : -1) + matches.Count) % matches.Count;
    }
}
