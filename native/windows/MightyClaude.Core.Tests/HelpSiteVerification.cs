using System.Text.Json;
using MightyClaude.Core;

// native/contracts/fixtures/help-site.json is the help address macOS (HelpSiteTests) and the
// phone (help-site.test.ts) are held to; these checks hold the Windows port to the same bytes.
internal static class HelpSiteVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static readonly Lazy<JsonElement> Fixture = new(() =>
    {
        const string name = "MightyClaude.Core.Tests.HelpSite.json";
        using var stream = typeof(HelpSiteVerification).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(name + " is not embedded in MightyClaude.Core.Tests");
        return JsonDocument.Parse(stream).RootElement.Clone();
    });

    internal static Task EveryLanguageOpensItsFolderOfTheSharedBase()
    {
        var fixture = Fixture.Value;
        Check(HelpSite.Base == fixture.GetProperty("base").GetString(), $"help base: expected '{fixture.GetProperty("base").GetString()}', got '{HelpSite.Base}'");
        var cases = fixture.GetProperty("cases").EnumerateArray().ToList();
        Check(cases.Select(c => c.GetProperty("language").GetString()).SequenceEqual(Locale.Languages), "the fixture must name every app language in order: ko, en, zh, ja");
        foreach (var item in cases)
        {
            var language = item.GetProperty("language").GetString()!;
            var url = item.GetProperty("url").GetString();
            Check(HelpSite.Url(language) == url, $"help address of '{language}': expected '{url}', got '{HelpSite.Url(language)}'");
        }
        Check(HelpSite.Url("system") == HelpSite.Url("en"), "a language the app has no copy of must read the English guide");
        return Task.CompletedTask;
    }

    internal static Task TheCurrentGuideFollowsTheChosenLanguage()
    {
        var saved = Locale.LanguagePreference;
        try
        {
            foreach (var language in Locale.Languages)
            {
                Locale.LanguagePreference = language; Locale.ResetCache();
                Check(HelpSite.Current() == HelpSite.Url(language), $"the guide for '{language}' must be its own folder; got '{HelpSite.Current()}'");
            }
            // "system" opens the folder of the language it resolves to, never a "system" folder.
            Locale.LanguagePreference = "system"; Locale.ResetCache();
            Check(HelpSite.Current() == HelpSite.Url(Locale.ChosenLanguage()) && Locale.Languages.Contains(Locale.ChosenLanguage()), $"the system guide must be one of the app's languages; got '{HelpSite.Current()}'");
        }
        finally { Locale.LanguagePreference = saved; Locale.ResetCache(); }
        return Task.CompletedTask;
    }
}
