using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

/// <summary>
/// The web smoke's live steps: what the app does beside the page, checked in the web shell as it runs for a user. Settings forms
/// the page reads (built by the same builders, read off their own controls), the setup screen, the plugin browsers over fake
/// readers, remote workspaces' settings half over loopback, the mobile direct rows over a recording firewall runner, the
/// automatic and manual update holds through a pane's never-shown composer, delegation's close paths, the companion overlay (a
/// native window in both front ends), screen sharing's capture, transport and tap marker (no focus taken, no shared input), the
/// real Windows notifier, the pictures' Windows codecs, and a locale-key leak scan over every Settings section. Each step is
/// reported under its own key and in <c>live</c>; a failed step records its error and the rest still run.
/// </summary>
public sealed partial class MainWindow
{
    private async Task<bool> RunWebLiveSmoke(Workspace workspace, Dictionary<string, object?> result, Action save)
    {
        var directory = options.ProfileDirectory ?? throw new InvalidOperationException("smoke profile missing");
        var live = new Dictionary<string, object?>(); result["live"] = live;
        // The smoke's fixture providers, as every CLI were there: no CLI is read for a model list or a sign-in.
        var heldRuntime = runtime;
        runtime = new("win32", "smoke", true, "fixture", ProviderCatalog.Fallback("claude"), Wire.Providers.Select(p => new ProviderRuntime(p, ProviderCatalog.Name(p), true, "fixture", "isolated fixture", ProviderCatalog.Fallback(p), ProviderCatalog.Capabilities(p))).ToList(), null);
        async Task Step(string key, Func<Task<object?>> run)
        {
            options.TraceStartup("smoke:" + key + ":running");
            try { result[key] = await run(); live[key] = true; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { live[key] = false; result[key + "Error"] = ex.ToString(); }
            options.TraceStartup("smoke:" + key + ":" + (live[key] is true ? "passed" : "failed"));
            save();
        }
        await Step(SettingsSectionsSmokeOutcome.ResultKey, async () => await RunSettingsSectionsSmoke());
        await Step("phaseModelsSection", () => Task.FromResult<object?>(RunPhaseModelsSectionSmoke()));
        await Step("phaseModelsActions", async () => await RunPhaseModelsActionsSmoke());
        var componentsLeakStrings = new List<string>();
        await Step("componentsSection", () => Task.FromResult<object?>(RunComponentsSectionSmoke(result, componentsLeakStrings)));
        await Step("setupTools", async () => await RunSetupSmoke());
        await Step("mobileDirect", async () => await RunMobileDirectSmoke());
        await Step(AccountUsageSmokeOutcome.ResultKey, async () => await RunAccountUsageSmoke());
        await Step(AppUpdateSmokeOutcome.ResultKey, async () => await RunAppUpdateSectionSmoke());
        await Step(ClaudePluginSmokeOutcome.ResultKey, async () => await RunClaudePluginSmoke());
        await Step(CodexPluginSmokeOutcome.ResultKey, async () => await RunCodexPluginSmoke());
        await Step(PluginMarketplaceSmokeOutcome.ResultKey, async () => await RunPluginMarketplaceSmoke());
        await Step("automaticUpdates", async () => await RunAutomaticUpdatesSmoke(workspace));
        await Step("remoteWorkspaces", async () => await RunRemoteWorkspacesSmoke(workspace));
        await Step("companion", async () => await RunCompanionSmoke());
        await Step("screenCapture", async () => await WindowsScreenCapture.SmokeAsync());
        await Step("screenTransport", async () => await WindowsScreenTransportSmoke.RunAsync(root, directory));
        await Step("screenTapMarkerNoFocus", () => { Require(ScreenShareTapMarkerOverlay.SmokeNoFocus(), "Screen tap marker must preserve focus and pass through input."); return Task.FromResult<object?>(true); });
        await Step(CompletionNotificationSmokeOutcome.ResultKey, async () => await RunCompletionNotificationSmoke());
        await Step("pictureDecode", async () => await RunPictureDecodeSmoke());
        await Step("delegation", async () => await RunDelegationSmoke());
        await Step("localeKeyLeakScan", () => Task.FromResult<object?>(RunSettingsLeakScan(componentsLeakStrings)));
        runtime = heldRuntime;
        return live.Values.All(value => value is true);
    }

    /// <summary>The phase-model section's own controls, hosted unseen under the page so they load, changed as a person would (CheckPhaseModelsSettingsActions).</summary>
    private async Task<Dictionary<string, object?>> RunPhaseModelsActionsSmoke()
    {
        var host = new Border { Child = BuildPhaseModelsSection(), Width = 540, Opacity = 0, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetRowSpan(host, 3); Grid.SetColumnSpan(host, 2); root.Children.Insert(0, host);
        try
        {
            await WaitUI(() => host.IsLoaded);
            await CheckPhaseModelsSettingsActions(host);
            return new() { ["selectionsAndRegistrationPersisted"] = true };
        }
        finally { root.Children.Remove(host); }
    }

    /// <summary>
    /// The locale-key leak scan over what Settings shows: every registered section and the boxes a tab draws beside them, the
    /// phase-model section's pickers and the components section with its run result table filled in. No raw key may show.
    /// </summary>
    private Dictionary<string, object?> RunSettingsLeakScan(List<string> componentsLeakStrings)
    {
        var leakStrings = new List<string>();
        var sections = GetSettingsSections();
        var panel = new StackPanel { Spacing = 0, MinWidth = 420, MaxWidth = 540 };
        foreach (var section in sections) panel.Children.Add(BuildSectionContainer(section.Title, section.Build()));
        foreach (var extra in SettingsNavigation.Available.SelectMany(tab => SettingsGroups(tab.Id)).Where(box => sections.All(slot => slot.Title != box.Title)))
            panel.Children.Add(BuildSectionContainer(extra.Title, extra.Build(), extra.Beta));
        CollectVisibleStrings(panel, leakStrings);
        // The phase-model section's ComboBox headers and items are not in the visual tree directly, so they are added on their own.
        leakStrings.AddRange(PhaseModelSectionTexts(BuildPhaseModelsSection(new(), PhaseModelSection.SmokeFixtureTools)));
        leakStrings.AddRange(componentsLeakStrings);
        var keyLeaks = LocaleKeyLeak.Detect(leakStrings, Locale.Catalogue("ko").Keys.ToList());
        Require(keyLeaks.Count == 0, "locale keys show as text: " + string.Join(", ", keyLeaks));
        return new() { ["scanned"] = leakStrings.Count, ["leaks"] = keyLeaks };
    }
}
