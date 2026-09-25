using MightyClaude.Core;

internal static class SettingsSectionsVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    // The Windows sections must appear in the same relative order as the macOS
    // Form body in SettingsViews.swift: 화면 → 페이즈별 모델 → CLI 업데이트 → providers →
    // CLI 계정 → 앱 업데이트 → 앱 정보.
    internal static Task SectionsAppearInMacOrderWithTheirTitles()
    {
        var titles = SettingsSections.WindowsTitles;
        Check(titles.SequenceEqual(new[]
        {
            "화면",
            PhaseModelSection.SectionTitle,
            ComponentSection.SectionTitle,
            CliUpdateStrings.SectionTitle,
            "이 PC의 CLI",
            CliAccountStrings.SectionTitle,
            AppUpdateStrings.SectionTitle,
            "앱 정보",
        }), "Windows section titles are not the macOS order: " + string.Join(", ", titles));

        // The theme, the providers, the completion-notification switch and the
        // CLI update section each own a slot; none of them shares one.
        Check(titles.Distinct().Count() == titles.Count, "section titles must be unique");

        // Relative order must match the macOS slot order, not just the set.
        var windowsIds = SettingsSections.Windows.Select(slot => slot.Id).ToArray();
        var macIds = SettingsSections.MacOrder.Select(slot => slot.Id).ToArray();
        var positions = windowsIds.Select(id => Array.IndexOf(macIds, id)).ToArray();
        Check(positions.SequenceEqual(positions.OrderBy(value => value)),
            "Windows sections must keep the macOS relative order");
        Check(Array.IndexOf(macIds, SettingsSections.CliUpdate) < Array.IndexOf(macIds, SettingsSections.Providers),
            "CLI 업데이트 sits before the provider list on macOS");
        return Task.CompletedTask;
    }

    // Sections whose feature does not exist on Windows yet are not shown.
    internal static Task SectionsLeaveOutFeaturesNotOnWindowsYet()
    {
        var absent = new[]
        {
            SettingsSections.RemoteConnection,
            SettingsSections.Styles,
            SettingsSections.MobileRemote,
            SettingsSections.Companion,
            SettingsSections.ClaudeMods,
        };
        foreach (var id in absent)
        {
            var slot = SettingsSections.MacOrder.Single(s => s.Id == id);
            Check(!slot.OnWindows, id + " has no Windows feature yet and must not be shown");
            Check(!SettingsSections.Windows.Any(s => s.Id == id), id + " must be absent from the Windows list");
        }
        // Every macOS slot is accounted for: shown or deliberately absent.
        Check(SettingsSections.MacOrder.Count == SettingsSections.Windows.Count + absent.Length,
            "every macOS slot must be either shown on Windows or listed as absent");
        return Task.CompletedTask;
    }

    // Registering a later feature is one small edit that leaves the others alone.
    internal static Task RegisteringASectionDoesNotTouchTheOthers()
    {
        var before = SettingsSections.WindowsTitles;

        // Simulate the one-line registration a later feature makes: give an
        // absent slot a title, then filter and order exactly as SettingsSections does.
        var registered = SettingsSections.MacOrder
            .Select(slot => slot.Id == SettingsSections.ClaudeMods ? slot with { WindowsTitle = "Claude Mods" } : slot)
            .Where(slot => slot.OnWindows)
            .Select(slot => slot.WindowsTitle!)
            .ToArray();

        Check(registered.Length == before.Count + 1, "registration must add exactly one section");
        Check(registered.Contains("Claude Mods"), "the registered section must appear");
        // Every previously shown section keeps its title and relative order.
        Check(registered.Where(title => title != "Claude Mods").SequenceEqual(before),
            "registering a section must not reorder or rename the existing sections");
        // It lands in the macOS slot, after CLI 계정.
        Check(Array.IndexOf(registered, "Claude Mods") == Array.IndexOf(registered, CliAccountStrings.SectionTitle) + 1,
            "the registered section must land in its macOS slot");
        // The real catalog is untouched by the simulation.
        Check(SettingsSections.WindowsTitles.SequenceEqual(before), "the catalog must not be mutated");
        return Task.CompletedTask;
    }

    // The smoke run flips the switch, reads it back and puts it back.
    internal static async Task SmokeFlipsTheAutoUpdateSwitchAndRestoresIt()
    {
        foreach (var original in new bool?[] { null, false, true })
        {
            var saved = new AppSnapshot { AutoUpdateCLIs = original };
            var writes = new List<bool?>();

            var outcome = await SettingsSectionsSmoke.RunAsync(
                SettingsSections.WindowsTitles,
                [.. SettingsSectionsSmoke.FixtureResults.Select(result => result.Status)],
                () => saved.AutoUpdateCLIs,
                value => { writes.Add(value); saved = saved with { AutoUpdateCLIs = value }; return Task.CompletedTask; });

            Check(outcome.ToggledTo == (original != true), $"{original}: must flip away from the saved value");
            Check(outcome.Restored, $"{original}: must report the switch restored");
            Check(saved.AutoUpdateCLIs == original, $"{original}: the saved switch must be back to what it was");
            Check(writes.Count == 2, $"{original}: exactly one flip and one restore");
            Check(writes[0] == (original != true) && writes[1] == original, $"{original}: flip then restore");
            Check(saved.Version == 1, $"{original}: the saved state version must stay 1");
            Check(outcome.Sections.SequenceEqual(SettingsSections.WindowsTitles), $"{original}: sections recorded");
        }
    }

    // The section shows updated, current, skipped and failed, and a screen that
    // built the wrong sections or dropped a result row fails the smoke run.
    internal static async Task SmokeShowsEveryFixtureStatusAndRejectsAWrongScreen()
    {
        var statuses = SettingsSectionsSmoke.FixtureResults.Select(result => result.Status).ToArray();
        foreach (var expected in new[] { "updated", "current", "skipped", "failed" })
            Check(statuses.Contains(expected), "the fixture results must cover " + expected);

        // Each fixture row renders through Core copy only.
        foreach (var result in SettingsSectionsSmoke.FixtureResults)
        {
            var row = CliUpdateService.ResultRow(result);
            Check(row.Contains(CliUpdateStrings.StatusLabel(result.Status)), result.Status + " row must carry its macOS label");
            Check(!string.IsNullOrEmpty(result.Detail), result.Status + " row must carry a detail sentence");
            Check(result.Output.Length == 0, result.Status + " fixture must not carry diagnostic output");
        }
        var updated = SettingsSectionsSmoke.FixtureResults.First(result => result.Status == "updated");
        Check(CliUpdateService.VersionChange(updated) == "2.1.270 → 2.1.271", "the updated row must show {before} → {after}");

        var snapshot = new AppSnapshot();
        Func<bool?> read = () => snapshot.AutoUpdateCLIs;
        Func<bool?, Task> write = value => { snapshot = snapshot with { AutoUpdateCLIs = value }; return Task.CompletedTask; };

        // Sections out of order must fail.
        var reordered = SettingsSections.WindowsTitles.Reverse().ToArray();
        var orderFailed = false;
        try { await SettingsSectionsSmoke.RunAsync(reordered, statuses, read, write); }
        catch (InvalidOperationException) { orderFailed = true; }
        Check(orderFailed, "sections out of the macOS order must fail the smoke run");

        // A dropped result row must fail.
        var dropped = statuses.Take(statuses.Length - 1).ToArray();
        var rowsFailed = false;
        try { await SettingsSectionsSmoke.RunAsync(SettingsSections.WindowsTitles, dropped, read, write); }
        catch (InvalidOperationException) { rowsFailed = true; }
        Check(rowsFailed, "a missing fixture result row must fail the smoke run");

        // A failing read-back still leaves the switch restored.
        var original = snapshot.AutoUpdateCLIs;
        var restoreChecked = false;
        try
        {
            await SettingsSectionsSmoke.RunAsync(SettingsSections.WindowsTitles, statuses, read,
                _ => Task.CompletedTask); // a screen that never persists
        }
        catch (InvalidOperationException) { restoreChecked = true; }
        Check(restoreChecked, "a switch that does not persist must fail the smoke run");
        Check(snapshot.AutoUpdateCLIs == original, "a failed smoke run must still leave the switch as it was");
    }
}
