namespace MightyClaude.Core;

/// <summary>Same categories and section membership as macOS SettingsPane.</summary>
public sealed record SettingsCategory(string Id, string Title, IReadOnlyList<string> Sections);
public static class SettingsNavigation
{
    public static IReadOnlyList<SettingsCategory> Categories =>
    [
        new("general", Locale.Get("settings.nav.general"), [SettingsSections.Display]),
        new("models", Locale.Get("settings.nav.models"), [SettingsSections.PhaseModels]),
        new("styles", Locale.Get("settings.nav.styles"), [SettingsSections.Styles]),
        new("tools", Locale.Get("settings.nav.tools"), [SettingsSections.Components]),
        new("cli", Locale.Get("settings.nav.cli"), [SettingsSections.CliUpdate, SettingsSections.CliAccounts, SettingsSections.Providers, SettingsSections.ClaudeMods]),
        new("mobile", Locale.Get("settings.nav.mobile"), [SettingsSections.MobileRemote]),
        new("companion", Locale.Get("settings.nav.companion"), [SettingsSections.Companion]),
        new("about", Locale.Get("settings.nav.about"), [SettingsSections.AppUpdate, SettingsSections.AppInfo]),
    ];
    public static IReadOnlyList<SettingsCategory> Available => Categories.Where(category => category.Sections.Any(id => SettingsSections.Windows.Any(section => section.Id == id))).ToArray();
}
