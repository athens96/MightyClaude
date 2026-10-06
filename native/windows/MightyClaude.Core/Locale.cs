using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace MightyClaude.Core;

/// <summary>
/// Reads the shared locale files — locales/{ko,en,zh,ja}.json at the repository
/// root. MightyClaude.Core.csproj embeds those files themselves (not copies of
/// them), so the catalogue cannot drift from the one source and is found
/// wherever the process runs from.
/// Language preference is read from <see cref="LanguagePreference"/>.
/// ko and en carry every key; zh and ja are translations that may miss some.
/// A missing key follows <see cref="LookupOrder"/>, then falls back to the key itself.
/// </summary>
public static class Locale
{
    /// <summary>Saved preference: "system", "ko", "en", "zh" or "ja". Default "system".</summary>
    public static string LanguagePreference { get; set; } = "system";

    /// <summary>The languages the app has a copy of.</summary>
    public static readonly IReadOnlyList<string> Languages = ["ko", "en", "zh", "ja"];

    /// <summary>
    /// The Settings picker's choices in the order it shows them, each with the key of its label.
    /// A language is named in its own language (the same words in every catalogue); only "system" is translated.
    /// </summary>
    public static readonly IReadOnlyList<(string Value, string LabelKey)> PickerChoices =
    [
        ("system", "settings.display.languageSystem"),
        ("ko", "settings.display.languageKorean"),
        ("en", "settings.display.languageEnglish"),
        ("zh", "settings.display.languageChinese"),
        ("ja", "settings.display.languageJapanese"),
    ];

    // ko and en keep their own rule (ko alone; en then ko); a translation falls back to en, then ko.
    // Built once, so a lookup allocates nothing.
    private static readonly string[] KoreanOrder = ["ko"], EnglishOrder = ["en", "ko"];
    private static readonly Dictionary<string, string[]> Orders = Languages.ToDictionary(
        language => language,
        language => language switch { "ko" => KoreanOrder, "en" => EnglishOrder, _ => new[] { language, "en", "ko" } });

    private static Func<string> _displayLanguage = () => CultureInfo.CurrentUICulture.Name;
    private static Func<IReadOnlyList<string>> _preferredLanguages = () => [];
    private static volatile string? _systemLanguage;

    /// <summary>The Windows display language, which "system" checks before <see cref="PreferredLanguages"/>.</summary>
    public static Func<string> DisplayLanguage
    {
        get => _displayLanguage;
        set { _displayLanguage = value; _systemLanguage = null; }
    }

    /// <summary>
    /// The user's further preferred languages, most preferred first, walked after the display language.
    /// Core has none; the app sets the user's Windows language list at start.
    /// </summary>
    public static Func<IReadOnlyList<string>> PreferredLanguages
    {
        get => _preferredLanguages;
        set { _preferredLanguages = value; _systemLanguage = null; }
    }

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _catalogues = new();

    /// <summary>
    /// The shared file for <paramref name="language"/> (one of <see cref="Languages"/>), parsed once.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Catalogue(string language) => _catalogues.GetOrAdd(language, static language =>
    {
        var name = "MightyClaude.Core.Locales." + language + ".json";
        using var stream = typeof(Locale).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(name + " is not embedded in MightyClaude.Core");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    });

    /// <summary>
    /// The language an OS language tag reads, or null when the app has no copy of it.
    /// Every Chinese tag reads the Simplified copy for now, zh-Hant, zh-TW and zh-HK included.
    /// </summary>
    public static string? LanguageForTag(string? tag)
    {
        var primary = (tag ?? "").Split('-', '_')[0].ToLowerInvariant();
        return Languages.Contains(primary) ? primary : null;
    }

    /// <summary>
    /// What "system" reads: the first of <paramref name="preferred"/> the app has a copy of with at
    /// least one key (<paramref name="hasKeys"/>), so an untranslated catalogue is passed over for the
    /// next preferred language; English when none qualifies.
    /// </summary>
    public static string ResolvedSystem(IEnumerable<string> preferred, Func<string, bool>? hasKeys = null)
    {
        foreach (var tag in preferred)
            if (LanguageForTag(tag) is { } language && (hasKeys?.Invoke(language) ?? true)) return language;
        return "en";
    }

    /// <summary>
    /// The language to read: the saved preference, or for "system" the display language and then the
    /// further preferred languages, each taken only when the app has a catalogue with keys for it.
    /// </summary>
    public static string ChosenLanguage()
    {
        if (Orders.ContainsKey(LanguagePreference)) return LanguagePreference;
        // Computing it twice in a race gives the same answer, so the cache needs no lock.
        return _systemLanguage ??= ResolvedSystem([_displayLanguage(), .. _preferredLanguages()], language => Catalogue(language).Count > 0);
    }

    /// <summary>The catalogues a lookup reads, in order, before it gives back the key itself.</summary>
    public static IReadOnlyList<string> LookupOrder(string language) =>
        Orders.TryGetValue(language, out var order) ? order : EnglishOrder;

    /// <summary>
    /// Returns the value for <paramref name="key"/> in the current language, then in each of its
    /// fallbacks (<see cref="LookupOrder"/>), then <paramref name="key"/> itself.
    /// Replaces <c>{placeholder}</c> tokens from <paramref name="subs"/>.
    /// </summary>
    public static string Get(string key, IReadOnlyDictionary<string, string>? subs = null)
    {
        string? value = null;
        foreach (var language in Orders.TryGetValue(ChosenLanguage(), out var order) ? order : EnglishOrder)
            if (Catalogue(language).TryGetValue(key, out value)) break;
        value ??= key;
        if (subs == null) return value;
        foreach (var (name, replacement) in subs)
            value = value.Replace("{" + name + "}", replacement);
        return value;
    }

    /// <summary>Clears cached catalogues and the system language; call after language preference changes.</summary>
    public static void ResetCache()
    {
        _catalogues.Clear();
        _systemLanguage = null;
    }
}
