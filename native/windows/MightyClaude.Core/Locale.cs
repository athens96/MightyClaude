using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace MightyClaude.Core;

/// <summary>
/// Loads locales/ko.json and locales/en.json and provides keyed lookups.
/// Language preference is read from <see cref="LanguagePreference"/>.
/// Missing keys fall back to Korean, then to the key string itself.
/// </summary>
public static class Locale
{
    /// <summary>Saved preference: "system", "ko", or "en". Default "system".</summary>
    public static string LanguagePreference { get; set; } = "system";

    private static IReadOnlyDictionary<string, string>? _ko;
    private static IReadOnlyDictionary<string, string>? _en;
    private static readonly object _lock = new();

    private static IReadOnlyDictionary<string, string> LoadFile(string lang)
    {
        // Try alongside the executable first, then working directory.
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Locales", $"{lang}.json"),
            Path.Combine("locales", $"{lang}.json"),
        };
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = File.ReadAllText(path);
                var doc = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (doc != null) return doc;
            }
            catch { /* fall through */ }
        }
        return new Dictionary<string, string>();
    }

    private static (IReadOnlyDictionary<string, string> Chosen, IReadOnlyDictionary<string, string> Korean) Catalogs()
    {
        lock (_lock)
        {
            _ko ??= LoadFile("ko");
            _en ??= LoadFile("en");
        }
        var lang = LanguagePreference switch
        {
            "ko" => "ko",
            "en" => "en",
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko" ? "ko" : "en",
        };
        return (lang == "ko" ? _ko! : _en!, _ko!);
    }

    /// <summary>
    /// Returns the value for <paramref name="key"/> in the current language,
    /// falling back to Korean, then to <paramref name="key"/> itself.
    /// Replaces <c>{placeholder}</c> tokens from <paramref name="subs"/>.
    /// </summary>
    public static string Get(string key, IReadOnlyDictionary<string, string>? subs = null)
    {
        var (chosen, korean) = Catalogs();
        chosen.TryGetValue(key, out var value);
        if (value == null) korean.TryGetValue(key, out value);
        value ??= key;
        if (subs == null) return value;
        foreach (var (k, v) in subs)
            value = value.Replace($"{{{k}}}", v);
        return value;
    }

    /// <summary>Clears cached catalogs; call after language preference changes.</summary>
    public static void ResetCache()
    {
        lock (_lock) { _ko = null; _en = null; }
    }
}
