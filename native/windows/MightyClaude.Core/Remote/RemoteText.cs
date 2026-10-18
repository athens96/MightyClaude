using System.Globalization;
using System.Text;

namespace MightyClaude.Core;

/// <summary>
/// Remote-workspace text counted as the Mac counts it: in Characters (Swift's String.count and prefix), which are extended
/// grapheme clusters, the same text elements StringInfo reads. A Swift String can hold no lone surrogate, so none is kept here
/// either, at any length.
/// </summary>
public static class RemoteText
{
    /// <summary>The first <paramref name="max"/> characters (the Mac's <c>String(value.prefix(max))</c>), never a part of one.</summary>
    public static string Cut(string? value, int max = 300)
    {
        var text = WholeCharacters(value);
        var info = new StringInfo(text);
        return info.LengthInTextElements <= max ? text : info.SubstringByTextElements(0, max);
    }

    /// <summary>How many characters the Mac counts (<c>value.count</c>).</summary>
    public static int Count(string? value) => new StringInfo(WholeCharacters(value)).LengthInTextElements;

    /// <summary>The text without its lone surrogates.</summary>
    private static string WholeCharacters(string? value)
    {
        var text = value ?? "";
        if (!text.Any(char.IsSurrogate)) return text;
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { builder.Append(text, i, 2); i++; }
            else if (!char.IsSurrogate(text[i])) builder.Append(text[i]);
        }
        return builder.ToString();
    }
}
