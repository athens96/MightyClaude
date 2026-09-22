using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace MightyClaude.Core;

/// <summary>
/// Reads the shared locale files — locales/ko.json and locales/en.json at the
/// repository root. MightyClaude.Core.csproj embeds those two files themselves
/// (not copies of them), so the catalogue cannot drift from the one source and
/// is found wherever the process runs from.
/// Language preference is read from <see cref="LanguagePreference"/>.
/// Missing keys fall back to Korean, then to the key string itself.
/// </summary>
public static class Locale
{
    /// <summary>Saved preference: "system", "ko", or "en". Default "system".</summary>
    public static string LanguagePreference { get; set; } = "system";

    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> _catalogues = new();
    private static readonly object _lock = new();

    /// <summary>
    /// The shared file for <paramref name="language"/> ("ko" or "en"), parsed.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Catalogue(string language)
    {
        lock (_lock)
        {
            if (_catalogues.TryGetValue(language, out var cached)) return cached;
            var name = "MightyClaude.Core.Locales." + language + ".json";
            using var stream = typeof(Locale).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException(name + " is not embedded in MightyClaude.Core");
            var parsed = (IReadOnlyDictionary<string, string>)JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
            _catalogues[language] = parsed;
            return parsed;
        }
    }

    /// The language to read: the saved preference, or the OS language when the
    /// preference is "system" — Korean only when the OS language is Korean.
    private static string ChosenLanguage() => LanguagePreference switch
    {
        "ko" => "ko",
        "en" => "en",
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko" ? "ko" : "en",
    };

    /// <summary>
    /// Returns the value for <paramref name="key"/> in the current language,
    /// falling back to Korean, then to <paramref name="key"/> itself.
    /// Replaces <c>{placeholder}</c> tokens from <paramref name="subs"/>.
    /// </summary>
    public static string Get(string key, IReadOnlyDictionary<string, string>? subs = null)
    {
        Catalogue(ChosenLanguage()).TryGetValue(key, out var value);
        if (value == null) Catalogue("ko").TryGetValue(key, out value);
        value ??= key;
        if (subs == null) return value;
        foreach (var (name, replacement) in subs)
            value = value.Replace("{" + name + "}", replacement);
        return value;
    }

    /// <summary>Clears cached catalogues; call after language preference changes.</summary>
    public static void ResetCache()
    {
        lock (_lock) { _catalogues.Clear(); }
    }
}
