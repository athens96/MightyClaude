using System.Text;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

public sealed record NextAction(string Label, string Fill)
{
    public string DisplayLabel => NextActions.Trim(Label.Replace("`", "", StringComparison.Ordinal)).Length == 0 ? Label : Label.Replace("`", "", StringComparison.Ordinal);
}

/// <summary>The same literal breadcrumb contract as macOS and the phone. Suggestions only fill a draft.</summary>
public static class NextActions
{
    public const int Maximum = 4;
    private static readonly char[] Whitespace = [' ', '\t', '\r', '\n', '\u3000'];
    private static readonly string[] Separators = [", \uB610\uB294 ", ", or ", " — \uB610\uB294 ", " — or ", " \uB610\uB294 ", " or "];
    private static readonly string[] ChoiceSuffixes = [" \uC911\uC5D0\uC11C \uC120\uD0DD", " \uC911 \uC120\uD0DD", " \uC911 \uD558\uB098"];
    private static readonly Regex Command = new(@"^(?:ooo(?:[ \t]|\z)|/[A-Za-z][A-Za-z0-9_-]*(?::[A-Za-z0-9_-]+)?(?:[ \t]|\z))", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    internal static string Trim(string text) => text.Trim(Whitespace);

    public static string? BreadcrumbNext(string text)
    {
        if (!text.Contains('◆')) return null;
        foreach (var raw in text.Split('\n').Reverse())
        {
            var line = Trim(raw); if (!line.StartsWith('◆')) continue;
            var found = new[] { "→ next:", "-> next:" }.Select(marker => (Index: line.IndexOf(marker, StringComparison.Ordinal), Marker: marker))
                .Where(value => value.Index >= 0).OrderBy(value => value.Index).FirstOrDefault();
            if (found.Marker is not null) return Trim(line[(found.Index + found.Marker.Length)..]);
        }
        return null;
    }

    public static IReadOnlyList<NextAction> Parse(string text)
    {
        if (BreadcrumbNext(text) is not { Length: > 0 } next) return [];
        var suffix = ChoiceSuffixes.FirstOrDefault(value => next.EndsWith(value, StringComparison.Ordinal));
        if (suffix is not null) next = next[..^suffix.Length];
        IEnumerable<string> options = SplitTopLevel(next, Separators);
        if (suffix is not null) options = options.SelectMany(value => SplitTopLevel(value, [","]));
        return options.Select(value => Trim(StripAlternative(Trim(value)))).Where(value => value.Length > 0)
            .Select(label => new NextAction(label, FillFor(label))).Where(action => Trim(action.Fill).Length > 0).Take(Maximum).ToArray();
    }

    public static (string EntryId, IReadOnlyList<NextAction> Actions)? Latest(IReadOnlyList<LogEntry> entries)
    {
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var entry = entries[index]; if (entry.Kind == "user") return null; if (entry.Kind != "assistant") continue;
            var actions = Parse(entry.Text); return actions.Count == 0 ? null : (entry.Id, actions);
        }
        return null;
    }

    public static (bool ReplacesDraft, string Text) Insertion(string draft, string fill) =>
        Trim(draft).Length == 0 ? (true, fill) : (false, draft.EndsWith('\n') ? fill : "\n" + fill);

    // All syntax characters are BMP; copying UTF-16 code units unchanged also
    // preserves supplementary characters, with no normalization or Unicode folding.
    private static bool Matches(string text, int index, string wanted)
    {
        if (index + wanted.Length > text.Length) return false;
        for (var i = 0; i < wanted.Length; i++)
        {
            var c = text[index + i]; if (c is >= 'A' and <= 'Z') c = (char)(c + 32);
            if (c != wanted[i]) return false;
        }
        return true;
    }
    private static List<string> SplitTopLevel(string text, string[] separators)
    {
        var parts = new List<string>(); var current = new StringBuilder(); var inCode = false; var depth = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var separator = !inCode && depth == 0 ? separators.FirstOrDefault(value => Matches(text, index, value)) : null;
            if (separator is not null) { parts.Add(current.ToString()); current.Clear(); index += separator.Length - 1; continue; }
            var c = text[index];
            if (c == '`') inCode = !inCode;
            else if (!inCode && c == '(') depth++;
            else if (!inCode && c == ')') depth = Math.Max(0, depth - 1);
            current.Append(c);
        }
        parts.Add(current.ToString()); return parts;
    }
    private static string StripAlternative(string text)
    {
        foreach (var word in new[] { "\uB610\uB294", "or" })
            if (text.Length > word.Length && Matches(text, 0, word) && Whitespace.Contains(text[word.Length])) return text[word.Length..];
        return text;
    }
    private static string FillFor(string label)
    {
        var spans = new List<string>(); int? start = null;
        for (var index = 0; index < label.Length; index++)
        {
            if (label[index] != '`') continue;
            if (start is { } opened) { if (index > opened) spans.Add(label[opened..index]); start = null; }
            else start = index + 1;
        }
        foreach (var span in spans) if (Command.IsMatch(Trim(span))) return Trim(span);
        return spans.Count == 1 && Trim(label) == "`" + spans[0] + "`" ? Trim(spans[0]) : label;
    }
}
