using System.Globalization;
using System.Text;

namespace MightyClaude.Core;

/// <summary>
/// Remote-workspace text counted as the Mac counts it: in Characters (Swift's String.count and prefix), which are extended
/// grapheme clusters, the same text elements StringInfo reads. A Swift String can hold no lone surrogate, so none is kept here
/// either, at any length. Both walk the text only as far as the limit, so a 2 MiB host answer costs no more than its first characters.
/// </summary>
public static class RemoteText
{
    /// <summary>The first <paramref name="max"/> characters (the Mac's <c>String(value.prefix(max))</c>), never a part of one; "" for max 0 or less.</summary>
    public static string Cut(string? value, int max = 300)
    {
        if (max <= 0 || string.IsNullOrEmpty(value)) return "";
        var (end, kept, _) = Walk(value, max);
        return kept?.ToString() ?? (end == value.Length ? value : value[..end]);
    }

    /// <summary>How many characters the Mac counts (<c>value.count</c>), counting no further than <paramref name="limit"/>.</summary>
    public static int Count(string? value, int limit = int.MaxValue) => limit <= 0 || string.IsNullOrEmpty(value) ? 0 : Walk(value, limit).Count;

    /// <summary>
    /// Steps through at most <paramref name="max"/> text elements, skipping lone surrogates. <c>Kept</c> is null while nothing was
    /// skipped (the characters are then <c>value[..End]</c>), else the characters read so far.
    /// </summary>
    private static (int End, StringBuilder? Kept, int Count) Walk(string value, int max)
    {
        var text = value.AsSpan();
        var plain = text.IndexOfAnyInRange('\uD800', '\uDFFF') < 0;
        StringBuilder? kept = null; int i = 0, count = 0;
        while (i < text.Length && count < max)
        {
            var rest = text[i..];
            if (!plain && Lone(rest, 0)) { kept ??= new StringBuilder().Append(text[..i]); i++; continue; }
            var length = StringInfo.GetNextTextElementLength(rest);
            if (!plain) for (var k = 1; k < length; k++) if (Lone(rest, k)) { length = k; break; }
            kept?.Append(rest[..length]);
            i += length; count++;
        }
        return (i, kept, count);
    }

    /// <summary>The surrogate at <paramref name="k"/> has no partner. A low surrogate first in <paramref name="span"/> never has one:
    /// a pair is always read as one element, so the walk never stops between its halves.</summary>
    private static bool Lone(ReadOnlySpan<char> span, int k)
    {
        var c = span[k];
        if (char.IsHighSurrogate(c)) return k + 1 >= span.Length || !char.IsLowSurrogate(span[k + 1]);
        if (char.IsLowSurrogate(c)) return k == 0 || !char.IsHighSurrogate(span[k - 1]);
        return false;
    }
}
