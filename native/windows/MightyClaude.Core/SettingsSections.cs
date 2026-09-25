using System.Text.Json.Serialization;

namespace MightyClaude.Core;

/// One slot of the Settings screen, in the order macOS shows it
/// (`SettingsViews.swift`, the `Form` body).
///
/// `WindowsTitle` is the Korean heading Windows renders, and null means the
/// feature has not arrived on Windows yet — that section is simply not shown.
/// The record carries no control and no builder, so the order below stays a
/// plain fact that a Mac-side check can read; WinUI supplies the controls.
public sealed record SettingsSectionSlot(string Id, string? WindowsTitle)
{
    public bool OnWindows => WindowsTitle is not null;
}

/// Foundation 3 — the registration point for Settings sections.
///
/// A later feature registers its section by giving its slot a `WindowsTitle`
/// here (one edit, one line) and adding the matching builder in WinUI. No other
/// section is touched, and the macOS order is preserved automatically because
/// `Windows` only filters `MacOrder`.
public static class SettingsSections
{
    // Slot ids, in the macOS order. Ids are stable; titles are not.
    public const string Display = "display";
    public const string RemoteConnection = "remoteConnection";
    public const string PhaseModels = "phaseModels";
    public const string Styles = "styles";
    public const string Components = "components";
    public const string MobileRemote = "mobileRemote";
    public const string CliUpdate = "cliUpdate";
    public const string Companion = "companion";
    public const string Providers = "providers";
    public const string CliAccounts = "cliAccounts";
    public const string ClaudeMods = "claudeMods";
    public const string AppUpdate = "appUpdate";
    public const string AppInfo = "appInfo";

    // OS-bound substitution (recorded in docs/windows-settings-groundwork.md):
    //   "이 PC의 CLI" replaces the macOS "이 Mac의 CLI".
    public static readonly string ProvidersTitle = Locale.Get("settings.providers.sectionTitleWindows");
    public static readonly string DisplayTitle = Locale.Get("settings.display.sectionTitle");
    public static readonly string AppInfoTitle = Locale.Get("settings.appInfo.sectionTitle");
    // 페이즈별 모델은 macOS와 같은 열쇠말을 쓴다 — 두 화면의 제목이 한 곳에서 온다.
    public static readonly string PhaseModelsTitle = PhaseModelSection.SectionTitle;

    /// Every macOS slot, in the macOS order. A null title marks a feature that
    /// Windows does not have yet.
    public static IReadOnlyList<SettingsSectionSlot> MacOrder { get; } =
    [
        new(Display, DisplayTitle),
        new(RemoteConnection, null),
        new(PhaseModels, PhaseModelsTitle),
        new(Styles, null),
        new(Components, ComponentSection.SectionTitle),
        new(MobileRemote, null),
        new(CliUpdate, CliUpdateStrings.SectionTitle),
        new(Companion, null),
        new(Providers, ProvidersTitle),
        new(CliAccounts, CliAccountStrings.SectionTitle),
        new(ClaudeMods, null),
        new(AppUpdate, AppUpdateStrings.SectionTitle),
        new(AppInfo, AppInfoTitle),
    ];

    /// The sections Windows shows, in the macOS order.
    public static IReadOnlyList<SettingsSectionSlot> Windows { get; } =
        [.. MacOrder.Where(slot => slot.OnWindows)];

    /// The headings WinUI must render, in order. The smoke run compares the
    /// titles it actually built against this list.
    public static IReadOnlyList<string> WindowsTitles { get; } =
        [.. Windows.Select(slot => slot.WindowsTitle!)];
}

/// What the GUI smoke run records under the key "settingsSections": the section
/// headings it saw, the value it flipped the CLI auto-update switch to, and the
/// value it put back. Nothing here is a token, path or prompt.
public sealed record SettingsSectionsSmokeOutcome
{
    public const string ResultKey = "settingsSections";

    // The key the CLI update section records under, so a run that shows the
    // sections but not the update results is still visible in the smoke result.
    public const string CliUpdateSectionKey = "cliUpdateSection";

    [JsonPropertyName("sections")] public IReadOnlyList<string> Sections { get; init; } = [];
    [JsonPropertyName(CliUpdateSectionKey)] public IReadOnlyList<string> CliUpdateStatuses { get; init; } = [];
    [JsonPropertyName("toggledTo")] public bool ToggledTo { get; init; }
    [JsonPropertyName("restored")] public bool Restored { get; init; }
}

public static class SettingsSectionsSmoke
{
    /// The four fixture results the CLI update section shows during smoke.
    /// They are fixtures, not a real run: no claude, codex, gemini, npm or
    /// winget process is started to produce them.
    public static IReadOnlyList<CliUpdateResult> FixtureResults { get; } =
    [
        new("claude", "updated", "2.1.270", "2.1.271", "native", CliUpdateStrings.DetailUpdated),
        new("codex", "current", "0.51.0", "0.51.0", "npm", CliUpdateStrings.DetailUnchanged),
        new("gemini", "skipped", null, null, "missing", CliUpdateStrings.DetailMissing),
        new("claude", "failed", "2.1.270", null, "native",
            CliUpdateStrings.DetailFailedExitTemplate.Replace("{code}", "1")),
    ];

    /// Drives the Settings part of the smoke run through callbacks, so the whole
    /// decision — order, flip, read-back, restore — is provable without WinUI.
    ///
    /// `renderedTitles` are the headings the screen actually built, and
    /// `renderedStatuses` the fixture rows it actually showed. Both are compared
    /// against the registration rather than against a copy of it. The switch is
    /// always put back to the value found, even when the read-back fails.
    public static async Task<SettingsSectionsSmokeOutcome> RunAsync(
        IReadOnlyList<string> renderedTitles,
        IReadOnlyList<string> renderedStatuses,
        Func<bool?> readAutoUpdate,
        Func<bool?, Task> writeAutoUpdate)
    {
        if (!renderedTitles.SequenceEqual(SettingsSections.WindowsTitles))
            throw new InvalidOperationException(
                "Settings sections are not in the macOS order: expected " +
                string.Join(", ", SettingsSections.WindowsTitles) +
                " but built " + string.Join(", ", renderedTitles));

        var expectedStatuses = FixtureResults.Select(result => result.Status).ToArray();
        if (!renderedStatuses.SequenceEqual(expectedStatuses))
            throw new InvalidOperationException(
                "CLI update section must show every fixture result: expected " +
                string.Join(", ", expectedStatuses) + " but showed " +
                string.Join(", ", renderedStatuses));

        var original = readAutoUpdate();
        var flipped = original != true;
        try
        {
            await writeAutoUpdate(flipped);
            if (readAutoUpdate() != flipped)
                throw new InvalidOperationException("the CLI auto-update switch did not persist to saved state");
        }
        finally
        {
            await writeAutoUpdate(original);
        }

        if (readAutoUpdate() != original)
            throw new InvalidOperationException("the CLI auto-update switch was not restored");

        return new()
        {
            Sections = [.. renderedTitles],
            CliUpdateStatuses = [.. renderedStatuses],
            ToggledTo = flipped,
            Restored = true,
        };
    }
}
