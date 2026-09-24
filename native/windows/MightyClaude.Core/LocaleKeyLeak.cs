using System.Collections.Generic;

namespace MightyClaude.Core;

/// <summary>
/// Detects visible strings that are exactly a locale key — a sign that Locale.Get
/// fell back to the raw key instead of finding a translation.
/// </summary>
public static class LocaleKeyLeak
{
    // ASCII whitespace + U+00A0 (NBSP) + U+200B (zero-width space) + U+FEFF (BOM/ZWNBS).
    private static readonly char[] TrimChars =
        [' ', '\t', '\n', '\r', '\f', '\v', ' ', '​', '﻿'];

    /// <summary>
    /// Returns those strings in <paramref name="visibleStrings"/> whose trimmed value is
    /// exactly one of the <paramref name="keys"/>. Dotted-but-not-key text (file names,
    /// model IDs, package specs) is never flagged unless it happens to be an exact key.
    /// Empty or whitespace-only strings are never flagged.
    /// </summary>
    public static IReadOnlyList<string> Detect(
        IEnumerable<string> visibleStrings,
        IReadOnlyCollection<string> keys)
    {
        var keySet = new HashSet<string>(keys, StringComparer.Ordinal);
        var leaks = new List<string>();
        foreach (var s in visibleStrings)
        {
            var trimmed = s.Trim(TrimChars);
            if (trimmed.Length == 0)
                continue;
            if (keySet.Contains(trimmed))
                leaks.Add(s);
        }
        return leaks;
    }
}
