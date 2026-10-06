namespace MightyClaude.Core;

/// <summary>
/// The user guide <c>scripts/help/build.mjs</c> publishes, one folder per language (<c>&lt;base&gt;/&lt;lang&gt;/</c>).
/// The address is pinned by <c>native/contracts/fixtures/help-site.json</c>, which macOS (<c>HelpSite.swift</c>)
/// and the phone (<c>help-site.ts</c>) are held to as well.
/// </summary>
public static class HelpSite
{
    public const string Base = "https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude/help";

    /// <summary>The guide's folder in <paramref name="language"/>; a language the app has no copy of reads English.</summary>
    public static string Url(string language) => Base + "/" + (Locale.Languages.Contains(language) ? language : "en") + "/";

    /// <summary>The guide in the language the app shows now (<see cref="Locale.ChosenLanguage"/>).</summary>
    public static string Current() => Url(Locale.ChosenLanguage());
}
