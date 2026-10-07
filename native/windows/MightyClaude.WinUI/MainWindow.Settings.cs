using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Storage.Pickers;

namespace MightyClaude.WinUI;

// A section of the Settings screen, bound to its controls.
// Title is the heading shown to the user (used as an automation ID in smoke).
// Build is called each time Settings opens so the controls reflect current state.
// The order and the titles come from SettingsSections (Core); this record only
// carries the builder WinUI supplies for a registered slot.
internal sealed record SettingsSection(string Title, Func<StackPanel> Build);

public sealed partial class MainWindow
{
    // Last CLI update results — empty until a run completes or smoke injects a fixture.
    // Rebuilt into the CLI update section each time Settings opens.
    internal IReadOnlyList<CliUpdateResult> lastCliUpdateResults = [];

    // The sections Windows shows, in the macOS slot order.
    //
    // The order and the titles are not decided here: SettingsSections.Windows
    // (Core) is the registration point, so a Mac-side check can prove the order
    // without building WinUI. This method only binds each registered slot to the
    // builder that supplies its controls. A later feature gives its slot a title
    // in Core and adds one arm to this switch; no other section is touched.
    internal List<SettingsSection> GetSettingsSections() =>
    [
        .. SettingsSections.Windows.Select(slot => new SettingsSection(slot.WindowsTitle!, BuilderFor(slot.Id))),
    ];

    private Func<StackPanel> BuilderFor(string slotId) => slotId switch
    {
        SettingsSections.Display => BuildDisplaySection,
        SettingsSections.Styles => BuildStylesSection,
        SettingsSections.MobileRemote => BuildMobileRemoteSection,
        SettingsSections.Companion => BuildCompanionSection,
        SettingsSections.PhaseModels => BuildPhaseModelsSection,
        SettingsSections.Components => BuildComponentsSection,
        SettingsSections.CliUpdate => BuildCliUpdateSectionFromState,
        SettingsSections.Providers => BuildProvidersSection,
        SettingsSections.CliAccounts => BuildCliAccountsSectionFromState,
        SettingsSections.ClaudeMods => BuildClaudeModsSection,
        SettingsSections.AppUpdate => BuildAppUpdateSectionFromState,
        SettingsSections.AppInfo => BuildAppInfoSection,
        _ => throw new InvalidOperationException("no Settings builder registered for slot " + slotId),
    };

    /// <summary>One box of a settings tab: its heading, the builder of its rows, and whether the 베타 capsule follows the heading.</summary>
    internal sealed record SettingsGroup(string Title, Func<StackPanel> Build, bool Beta = false);

    /// <summary>
    /// Each tab's boxes in the Mac's order (M/SettingsViews.swift:218-322). A registered slot keeps the
    /// heading Core gives it; the three boxes the Mac draws as sections of their own inside a slot's tab
    /// (agent links, the toolkit, screen view and control) take their heading from the catalogue.
    /// </summary>
    internal IReadOnlyList<SettingsGroup> SettingsGroups(string category)
    {
        SettingsGroup Slot(string id) => new(SettingsSections.Windows.First(slot => slot.Id == id).WindowsTitle!, BuilderFor(id));
        return category switch
        {
            "general" => [Slot(SettingsSections.Display), new(Locale.Get("agentTerminal.urlOpen.settingTitle"), BuildAgentLinksSection)],
            "models" => [Slot(SettingsSections.PhaseModels)],
            "styles" => [Slot(SettingsSections.Styles)],
            "tools" => [Slot(SettingsSections.Components), new(Locale.Get("settings.toolkit.sectionTitle"), BuildToolkitSection)],
            "cli" => [Slot(SettingsSections.Providers), Slot(SettingsSections.CliAccounts), Slot(SettingsSections.ClaudeMods), Slot(SettingsSections.CliUpdate)],
            "mobile" => [Slot(SettingsSections.MobileRemote), new(Locale.Get("settings.screenShare.sectionTitle"), BuildScreenShareSection, Beta: true)],
            "companion" => [Slot(SettingsSections.Companion)],
            "about" => [Slot(SettingsSections.AppUpdate), Slot(SettingsSections.AppInfo)],
            _ => [],
        };
    }

    private Task OpenSettings() => Act(ShowCategorizedSettingsAsync);

    // 화면 (M/SettingsViews.swift:221-251): the theme and the language as segmented pickers, the language
    // note, then the status-line and browser switches, each with its 11pt explanation under the label, and
    // between them the background work line's switch.
    private StackPanel BuildDisplaySection()
    {
        var rows = new StackPanel();
        var themeLabel = Locale.Get("settings.display.themeLabel");
        var theme = SettingsSegmented(themeLabel, "settings-theme",
            [("dark", Locale.Get("settings.display.themeDarkWindows")), ("light", Locale.Get("settings.display.themeLightWindows"))],
            service.Snapshot.Theme == "light" ? "light" : "dark",
            value => Act(async () => { await service.UpdateAsync(s => s with { Theme = value }); Render(); }));
        SettingsRow(rows, SettingsLabeled(SettingsText(themeLabel), theme, share: true));
        // The sidebar's own theme button may change the theme while this window is open; the picker follows it.
        rows.ActualThemeChanged += (_, _) => ShowSegment(theme, service.Snapshot.Theme == "light" ? "light" : "dark");

        // Language picker — takes effect on the next app start.
        var languageLabel = Locale.Get("settings.display.languageLabel");
        var language = SettingsSegmented(languageLabel, "settings-language",
            Locale.PickerChoices.Select(choice => (choice.Value, Locale.Get(choice.LabelKey))).ToList(),
            Locale.Languages.Contains(service.Snapshot.LanguagePreference) ? service.Snapshot.LanguagePreference : "system",
            value => Act(async () => await service.UpdateAsync(s => s with { LanguagePreference = value })));
        SettingsRow(rows, SettingsLabeled(SettingsText(languageLabel), language, share: true));
        SettingsRow(rows, SettingsText(Locale.Get("settings.display.languageRestartNote"), 11, DesignToken.Ink2));

        var statusLineLabel = Locale.Get("settings.display.statusLineToggle");
        var statusLineToggle = SettingsSwitch(statusLineLabel, service.Snapshot.StatusLineEnabled, "settings-status-line");
        void StatusLineToggled() => _ = Act(() => SetStatusLineEnabled(statusLineToggle.IsChecked == true));
        statusLineToggle.Checked += (_, _) => StatusLineToggled(); statusLineToggle.Unchecked += (_, _) => StatusLineToggled();
        SettingsRow(rows, SettingsLabeled(SettingsTitled(statusLineLabel, Locale.Get("settings.display.statusLineDescription")), statusLineToggle, top: true));

        // The background work line above the composer; the line's eye button and the pane's … menu set the same value.
        var backgroundWorkLabel = Locale.Get("settings.display.backgroundWorkToggle");
        var backgroundWorkToggle = SettingsSwitch(backgroundWorkLabel, ShowsBackgroundWork, "settings-background-work");
        void BackgroundWorkToggled() => _ = Act(() => SetShowsBackgroundWork(backgroundWorkToggle.IsChecked == true));
        backgroundWorkToggle.Checked += (_, _) => BackgroundWorkToggled(); backgroundWorkToggle.Unchecked += (_, _) => BackgroundWorkToggled();
        SettingsRow(rows, SettingsLabeled(SettingsText(backgroundWorkLabel), backgroundWorkToggle));

        // Browser engine toggle — opt-in, off by default; restart required to apply.
        var browserLabel = Locale.Get("settings.display.browserToggle");
        var browserToggle = SettingsSwitch(browserLabel, service.Snapshot.BrowserEngineEnabled, "settings-browser-engine");
        void BrowserToggled() => _ = Act(async () => await service.UpdateAsync(s => s with { BrowserEngineEnabled = browserToggle.IsChecked == true }));
        browserToggle.Checked += (_, _) => BrowserToggled(); browserToggle.Unchecked += (_, _) => BrowserToggled();
        SettingsRow(rows, SettingsLabeled(SettingsTitled(browserLabel, Locale.Get("windows.settings.browserDescription")), browserToggle, top: true));
        return rows;
    }

    // 에이전트 링크 열기 (M/SettingsViews.swift:256-270): where this workspace opens the links an agent asks for.
    private StackPanel BuildAgentLinksSection()
    {
        var rows = new StackPanel(); var workspace = service.Snapshot.ActiveWorkspaceId;
        var title = Locale.Get("agentTerminal.urlOpen.settingTitle");
        var picker = SettingsSegmented(title, "settings-web-open-choice",
            [("ask", Locale.Get("agentTerminal.urlOpen.settingAsk")), ("inApp", Locale.Get("agentTerminal.urlOpen.settingInApp")), ("external", Locale.Get("agentTerminal.urlOpen.settingExternal"))],
            service.Snapshot.AgentWebOpenChoices?.GetValueOrDefault(workspace ?? "") switch { "inApp" => "inApp", "external" => "external", _ => "ask" },
            value => workspace is null ? Task.CompletedTask : Act(() => service.UpdateAsync(state =>
            {
                var choices = new Dictionary<string, string>(state.AgentWebOpenChoices ?? []);
                if (value == "ask") choices.Remove(workspace); else choices[workspace] = value;
                return state with { AgentWebOpenChoices = choices };
            })), enabled: workspace is not null);
        SettingsRow(rows, SettingsLabeled(SettingsText(title), picker, share: true));
        return rows;
    }

    // Claude Mods (M/SettingsViews.swift:299-304): the status sentence, then the minimum version beside its label.
    private StackPanel BuildClaudeModsSection()
    {
        var rows = new StackPanel();
        var mods = runtime?.Mods;
        SettingsRow(rows, SettingsText(mods?.Detail ?? Locale.Get("window.status.checkingRuntime"), 12, DesignToken.Ink2, selectable: true));
        if (mods is not null)
            SettingsRow(rows, SettingsLabeled(SettingsText(Locale.Get("settings.claudeMods.compatLabel")), SettingsText(mods.MinimumVersion, 13, DesignToken.Ink2, selectable: true)));
        return rows;
    }

    // CLI updates — delegates to the parameterised builder so smoke can inject fixture results.
    private StackPanel BuildCliUpdateSectionFromState() => BuildCliUpdateSection(lastCliUpdateResults);

    // Called by both OpenSettings (via BuildCliUpdateSectionFromState) and the smoke check.
    // M/CLIUpdateSettingsView.swift:8-74: the two switches, each over its explanation, the progress or
    // last-run words beside the update button, then one row per result.
    internal StackPanel BuildCliUpdateSection(IReadOnlyList<CliUpdateResult> results)
    {
        var rows = new StackPanel();
        var toggle = SettingsSwitch(CliUpdateStrings.AutoUpdateToggle, service.Snapshot.AutoUpdateCLIs == true, "cli-auto-update");
        async void AutoUpdateToggled() => await service.UpdateAsync(s => s with { AutoUpdateCLIs = toggle.IsChecked == true });
        toggle.Checked += (_, _) => AutoUpdateToggled(); toggle.Unchecked += (_, _) => AutoUpdateToggled();
        SettingsRow(rows, SettingsLabeled(SettingsText(CliUpdateStrings.AutoUpdateToggle), toggle));
        SettingsRow(rows, SettingsText(CliUpdateStrings.SectionDescription, 11, DesignToken.Ink2));
        var pluginsLabel = Locale.Get("settings.cliUpdate.autoUpdatePluginsToggle");
        var pluginsToggle = SettingsSwitch(pluginsLabel, service.Snapshot.AutoUpdatePlugins != false, "plugin-auto-update");
        async void PluginsToggled() => await service.UpdateAsync(s => s with { AutoUpdatePlugins = pluginsToggle.IsChecked == true });
        pluginsToggle.Checked += (_, _) => PluginsToggled(); pluginsToggle.Unchecked += (_, _) => PluginsToggled();
        SettingsRow(rows, SettingsLabeled(SettingsText(pluginsLabel), pluginsToggle));
        SettingsRow(rows, SettingsText(Locale.Get("settings.cliUpdate.autoUpdatePluginsDescription"), 11, DesignToken.Ink2));
        var updateProgress = SettingsText("", 11, DesignToken.Ink2);
        AutomationProperties.SetAutomationId(updateProgress, "cli-update-progress");

        // The update-now button — reflects coordinator state live.
        var updateButton = SettingsPush(new Button
        {
            Content = AnyCliUpdateRunning ? CliUpdateStrings.UpdatingButton : CliUpdateStrings.UpdateButton,
            IsEnabled = !AnyCliUpdateRunning && !pluginOperations.IsRunning,
        });
        AutomationProperties.SetAutomationId(updateButton, "cli-update-start");
        updateButton.Click += (_, _) => { if (!AnyCliUpdateRunning && !pluginOperations.IsRunning) coordinator.Start(); };
        SettingsRow(rows, SettingsLabeled(updateProgress, updateButton));

        // Result rows: rebuilt on each StateChanged while the section is visible.
        const string cliResults = "cli-results", pluginResultRows = "plugin-results";
        StackPanel ResultRow(string glyph, DesignToken ink, string title, string? provider, string? change, string detail, string id)
        {
            var row = new StackPanel { Spacing = 4 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            head.Children.Add(SettingsSymbol(glyph, 12, ink)); head.Children.Add(SettingsText(title, 11, medium: true));
            if (provider is not null && ProviderCatalog.IsBeta(provider)) head.Children.Add(BetaBadgeView.Create(brushes));
            row.Children.Add(head);
            if (change is not null) row.Children.Add(SettingsText(change, 10, DesignToken.Ink2, mono: true));
            if (!string.IsNullOrEmpty(detail)) row.Children.Add(SettingsText(detail, 11, DesignToken.Ink2, selectable: true));
            AutomationProperties.SetAutomationId(row, id);
            return row;
        }
        // checkmark.circle.fill for a run that went through, exclamationmark.circle (waitText) for a failed one, info.circle for the rest.
        static (string Glyph, DesignToken Ink) Mark(string status) => status switch
        {
            "updated" or "current" => ("", DesignToken.Ink2), "failed" => ("", DesignToken.WaitText), _ => ("", DesignToken.Ink2),
        };
        void ShowResults(IReadOnlyList<CliUpdateResult> shown) => ReplaceSettingsRows(rows, cliResults, shown.Select(result =>
        {
            var (glyph, ink) = Mark(result.Status);
            return (FrameworkElement)ResultRow(glyph, ink, CliUpdateService.ResultRow(result), result.Provider, CliUpdateService.VersionChange(result), result.Detail, ResultRowAutomationId(result));
        }), rows.Children.OfType<Border>().FirstOrDefault(row => Equals(row.Tag, pluginResultRows)));
        ShowResults(results);
        void RefreshBackgroundUpdates()
        {
            updateButton.Content = AnyCliUpdateRunning ? CliUpdateStrings.UpdatingButton : CliUpdateStrings.UpdateButton;
            updateButton.IsEnabled = !AnyCliUpdateRunning && !pluginOperations.IsRunning;
            updateProgress.Text = automaticallyUpdatingProvider is { } provider
                ? Locale.Get(automaticallyUpdatingPlugins ? "settings.cliUpdate.progressPluginsTemplate" : "settings.cliUpdate.progressProviderTemplate", new Dictionary<string, string> { ["provider"] = ProviderCatalog.Name(provider) })
                : AnyCliUpdateRunning ? CliUpdateStrings.ProgressInspecting
                : (automaticUpdateFinishedAt is { } autoFinished && (coordinator.FinishedAt is not { } manualFinished || autoFinished > manualFinished) ? autoFinished : coordinator.FinishedAt) is { } finished
                    ? Locale.Get("settings.cliUpdate.lastRunTemplate", new Dictionary<string, string> { ["time"] = finished.ToLocalTime().ToString("t") }) : "";
            // While something runs the words are the progress (primary ink); otherwise the quiet last-run time.
            updateProgress.Foreground = brushes.Brush(AnyCliUpdateRunning ? DesignToken.Ink : DesignToken.Ink2);
            ReplaceSettingsRows(rows, pluginResultRows, Wire.Providers.Where(pluginUpdateResults.ContainsKey).Select(providerName =>
            {
                var result = pluginUpdateResults[providerName];
                var status = result.Status == "succeeded" ? "updated" : result.Status; var (glyph, ink) = Mark(status);
                var title = Locale.Get("settings.cliUpdate.pluginRowTemplate", new Dictionary<string, string> { ["provider"] = ProviderCatalog.Name(providerName), ["status"] = CliUpdateStrings.StatusLabel(status) });
                return (FrameworkElement)ResultRow(glyph, ink, title, providerName, null, result.Detail, "plugin-update-result-" + providerName);
            }));
            if (automaticUpdateRunning) ShowResults(lastCliUpdateResults);
        }
        RefreshBackgroundUpdates();
        void CoordinatorChanged() => DispatcherQueue.TryEnqueue(() =>
        {
            if (closing || !rows.IsLoaded) return;
            RefreshBackgroundUpdates();
            var live = coordinator.Results;
            if (live.Count > 0) ShowResults(live);
        });
        rows.Loaded += (_, _) => { AutomaticUpdatesChanged += RefreshBackgroundUpdates; coordinator.StateChanged += CoordinatorChanged; RefreshBackgroundUpdates(); };
        rows.Unloaded += (_, _) => { AutomaticUpdatesChanged -= RefreshBackgroundUpdates; coordinator.StateChanged -= CoordinatorChanged; };

        return rows;
    }

    // One id per row. The status is part of it because a run can report the same
    // provider twice (for example updated then failed on a retry).
    internal const string ResultRowIdPrefix = "cli-update-result-";
    private static string ResultRowAutomationId(CliUpdateResult result) =>
        ResultRowIdPrefix + result.Provider + "-" + result.Status;

    // Components — CLI rows (claude/codex/gemini) then the toolkit sub-section.
    // Layout mirrors ComponentsSettingsSection + ToolkitSettingsSection on macOS.
    // No Korean literal is typed here — all copy comes from the locale catalogue.

    private ToolkitStore? toolkitStore;
    private bool toolkitRunning;
    private IReadOnlyList<ToolkitRunItem>? toolkitRunResults;
    private StackPanel? componentsCliPanel;
    private StackPanel? toolkitListPanel;
    private StackPanel? toolkitResultsPanel;
    private Button? toolkitInstallButton;

    private string StateDirectory =>
        options.ProfileDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MightyClaudeNative");

    private ToolkitStore GetToolkitStore() =>
        toolkitStore ??= new ToolkitStore(StateDirectory);

    private static ToolkitProbeContext LiveProbeContext() => new()
    {
        HomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        PathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries),
        LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    };

    // 구성 요소 (M/ComponentsSettingsView.swift:11-91): one row per CLI, then the explanation beside the
    // recheck button. The toolkit is the next box (BuildToolkitSection).
    private StackPanel BuildComponentsSection()
    {
        // CLI rows — re-filled by the recheck button; the last row stays under them.
        var rows = componentsCliPanel = new StackPanel();

        // Recheck button — re-probes the runtime without closing the dialog.
        var recheckLabel = Locale.Get("settings.components.recheckButton");
        var checkingLabel = Locale.Get("settings.components.checkingButton");
        var recheckBtn = SettingsPush(new Button { Content = recheckLabel });
        AutomationProperties.SetAutomationId(recheckBtn, "components-refresh");
        recheckBtn.Click += async (_, _) => await Act(async () =>
        {
            recheckBtn.Content = checkingLabel;
            recheckBtn.IsEnabled = false;
            await RefreshRuntime();
            FillComponentCliRows(rows);
            recheckBtn.Content = recheckLabel;
            recheckBtn.IsEnabled = true;
        });
        SettingsRow(rows, SettingsLabeled(SettingsText(Locale.Get("settings.components.sectionDescription"), 11, DesignToken.Ink2), recheckBtn), tag: ComponentsFooterRow);
        FillComponentCliRows(rows);
        return rows;
    }

    private const string ComponentsFooterRow = "components-footer", ComponentRow = "component", ToolkitEntryRow = "toolkit-entry", ToolkitFooterRow = "toolkit-footer", ToolkitResultsRow = "toolkit-results";

    // 내 작업 도구 모음 (M/ComponentsSettingsView.swift:105-214): the file error, one row per tool, the
    // results of the last run, then the explanation beside the add, export, import and install buttons.
    private StackPanel BuildToolkitSection()
    {
        var rows = toolkitListPanel = new StackPanel();

        // Toolkit list — error banner + entry rows.
        var store = GetToolkitStore();
        var (entries, fileError) = store.List();

        if (fileError is not null)
        {
            var banner = new Grid { ColumnSpacing = 6 };
            banner.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); banner.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var mark = SettingsSymbol("", 12, DesignToken.WaitText); mark.VerticalAlignment = VerticalAlignment.Top; mark.Margin = new Thickness(0, 1, 0, 0);
            banner.Children.Add(mark);
            var words = SettingsText(Locale.Get("settings.toolkit.errorBanner"), 11, selectable: true);
            Grid.SetColumn(words, 1); banner.Children.Add(words);
            SettingsRow(rows, banner);
        }

        // Results from the last install run.
        toolkitResultsPanel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 4) };
        AutomationProperties.SetAutomationId(toolkitResultsPanel, "settings-toolkit-results");
        SettingsRow(rows, toolkitResultsPanel, tag: ToolkitResultsRow).Visibility = Visibility.Collapsed;
        if (toolkitRunResults is not null) FillToolkitResults(toolkitResultsPanel, toolkitRunResults);

        // Action buttons row.
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var addBtn = SettingsPush(new Button { Content = Locale.Get("settings.toolkit.addButton") }, SettingsControlSize.Small);
        AutomationProperties.SetAutomationId(addBtn, "settings-toolkit-add");
        addBtn.Click += async (_, _) => await Act(() => AddToolkitEntry(store));
        var exportBtn = SettingsPush(new Button { Content = Locale.Get("settings.toolkit.exportButton") }, SettingsControlSize.Small);
        AutomationProperties.SetAutomationId(exportBtn, "settings-toolkit-export");
        exportBtn.Click += async (_, _) => await Act(() => ExportToolkit(store));
        var importBtn = SettingsPush(new Button { Content = Locale.Get("settings.toolkit.importButton") }, SettingsControlSize.Small);
        AutomationProperties.SetAutomationId(importBtn, "settings-toolkit-import");
        importBtn.Click += async (_, _) => await Act(() => ImportToolkit(store));
        toolkitInstallButton = SettingsPush(new Button { Content = Locale.Get("settings.toolkit.installButton"), IsEnabled = !toolkitRunning });
        AutomationProperties.SetAutomationId(toolkitInstallButton, "settings-toolkit-install");
        toolkitInstallButton.Click += async (_, _) => await Act(() => RunToolkitInstall(store));
        btnRow.Children.Add(addBtn);
        btnRow.Children.Add(exportBtn);
        btnRow.Children.Add(importBtn);
        btnRow.Children.Add(toolkitInstallButton);
        SettingsRow(rows, SettingsLabeled(SettingsText(Locale.Get("settings.toolkit.sectionDescription"), 11, DesignToken.Ink2), btnRow), tag: ToolkitFooterRow);

        FillToolkitList(rows, store, entries);
        return rows;
    }

    // Renders one row per CLI provider (claude/codex/gemini), above the box's last row.
    // M/ComponentsSettingsView.swift:42-74: the mark, the 13pt medium name, the 베타 capsule, the version
    // in 10pt mono and the state word at the trailing edge; the 11pt detail; then the row's small buttons.
    private void FillComponentCliRows(StackPanel panel)
    {
        var rt = runtime ?? new RuntimeInfo("win32", "0.0.0", false, null, null, [], null);
        var contents = new List<FrameworkElement>();
        foreach (var row in ComponentSection.SectionRows(rt))
        {
            var rowPanel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 4) };
            AutomationProperties.SetAutomationId(rowPanel, "component-" + row.Id);

            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            name.Children.Add(ProviderMarkView.Create(row.Id, SettingsProviderMark));
            name.Children.Add(SettingsText(row.Title, 13, medium: true));
            if (ProviderCatalog.IsBeta(row.Id)) name.Children.Add(BetaBadgeView.Create(brushes));
            if (row.Version is { } ver) name.Children.Add(SettingsText(ver, 10, DesignToken.Ink2, mono: true));
            header.Children.Add(name);
            var state = SettingsText(ComponentStateLabel(row.State), 10, ComponentStateInk(row.State), medium: true);
            Grid.SetColumn(state, 1); header.Children.Add(state);
            rowPanel.Children.Add(header);
            rowPanel.Children.Add(SettingsText(row.Detail, 11, DesignToken.Ink2, selectable: true));

            if (row.Actions.Count > 0)
            {
                var actionsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                foreach (var action in row.Actions)
                {
                    var actionId = action.Id;
                    var btn = SettingsPush(new Button { Content = action.Title }, SettingsControlSize.Small);
                    AutomationProperties.SetAutomationId(btn, "component-" + row.Id + "-" + actionId);
                    if (actionId == "copy-command" && ComponentSection.InstallCommand(row.Id) is { } cmd)
                        btn.Click += (_, _) => Copy(cmd);
                    actionsPanel.Children.Add(btn);
                }
                rowPanel.Children.Add(actionsPanel);
            }
            contents.Add(rowPanel);
        }
        ReplaceSettingsRows(panel, ComponentRow, contents, panel.Children.OfType<Border>().FirstOrDefault(row => Equals(row.Tag, ComponentsFooterRow)));
    }

    /// <summary>A provider's mark beside a 13pt name: the Mac draws it in a 13 × 1.15 box (M/ProviderIcon.swift:18).</summary>
    private const double SettingsProviderMark = 15;

    /// <summary>The state word's ink (M/ComponentsSettingsView.swift:81-90): done for installed, wait for missing or attention, err for unsupported.</summary>
    private static DesignToken ComponentStateInk(string state) => state switch
    {
        "installed" => DesignToken.DoneText, "missing" or "attention" => DesignToken.WaitText, "unsupported" => DesignToken.ErrText, _ => DesignToken.Ink2,
    };

    private static string ComponentStateLabel(string state) => state switch
    {
        "installed" => Locale.Get("settings.components.statusInstalled"),
        "missing" => Locale.Get("settings.components.statusMissing"),
        "attention" => Locale.Get("settings.components.statusAttention"),
        "unsupported" => Locale.Get("settings.components.statusUnsupported"),
        _ => Locale.Get("settings.components.statusChecking"),
    };

    // Fills the toolkit list with bundled + user entries, above the results and the box's last row.
    // M/ComponentsSettingsView.swift:147-187: the wrench, the 13pt medium name, the bundled or approved
    // chip and the probe's state word at the trailing edge; a user's tool adds its approve and remove buttons.
    private void FillToolkitList(StackPanel panel, ToolkitStore store, IReadOnlyList<ToolkitFileReader.ToolkitFileEntry> entries)
    {
        var contents = new List<FrameworkElement>();
        // A smoke run never looks at the user's own tools: its probe reads the isolated profile only.
        var probe = options.SmokeTest ? new ToolkitProbeContext { HomeDirectory = StateDirectory, PathDirectories = [], LocalAppData = StateDirectory } : LiveProbeContext();
        foreach (var entry in entries)
        {
            var row = new StackPanel { Spacing = 4, Margin = new Thickness(0, 2, 0, 2) };
            AutomationProperties.SetAutomationId(row, "toolkit-entry-" + entry.Id);

            var isBundled = entry.Source == ToolkitFileReader.ToolkitEntrySource.Bundled;
            var approval = isBundled ? null : store.GetApproval(entry);

            var header = new Grid { ColumnSpacing = 6 };
            header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            name.Children.Add(SettingsSymbol("", 12, DesignToken.Ink));
            name.Children.Add(SettingsText(entry.DisplayName, 13, medium: true));
            if (isBundled) name.Children.Add(SettingsCapsule(Locale.Get("settings.toolkit.bundledBadge"), brushes.Brush(DesignToken.Ink2), brushes.Brush(DesignToken.Ink2, ToolkitBadgeTint), 9, 3));
            else if (approval is not null) name.Children.Add(SettingsCapsule(Locale.Get("settings.toolkit.approvedBadge"), brushes.Brush(DesignToken.DoneText), brushes.Brush(DesignToken.DoneSoft), 9, 3));
            header.Children.Add(name);
            // The probe only looks for files; an address it cannot read counts as not installed.
            bool installed;
            try { installed = ToolkitProbe.Probe(entry, approval, probe) == ToolkitProbe.Result.Installed; }
            catch (UriFormatException) { installed = false; }
            var state = SettingsText(Locale.Get(installed ? "settings.toolkit.statusInstalled" : "settings.toolkit.statusMissing"), 10, installed ? DesignToken.DoneText : DesignToken.WaitText, medium: true);
            Grid.SetColumn(state, 1); header.Children.Add(state);
            row.Children.Add(header);

            if (!isBundled)
            {
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                if (approval is null)
                {
                    actions.Children.Add(SettingsText(Locale.Get("settings.toolkit.needsApproval"), 11, DesignToken.Ink2));
                    var entryId = entry.Id;
                    var approveBtn = SettingsPush(new Button { Content = Locale.Get("settings.toolkit.approveButton") }, SettingsControlSize.Mini);
                    AutomationProperties.SetAutomationId(approveBtn, "toolkit-approve-" + entryId);
                    approveBtn.Click += async (_, _) => await Act(async () =>
                    {
                        store.Approve(entryId);
                        var (reloaded, _) = store.List();
                        FillToolkitList(toolkitListPanel!, store, reloaded);
                        await Task.CompletedTask;
                    });
                    actions.Children.Add(approveBtn);
                }
                var removeEntryId = entry.Id;
                var removeBtn = SettingsPush(new Button { Content = Locale.Get("settings.toolkit.removeButton") }, SettingsControlSize.Mini);
                AutomationProperties.SetAutomationId(removeBtn, "toolkit-remove-" + removeEntryId);
                removeBtn.Click += async (_, _) => await Act(async () =>
                {
                    store.Remove(removeEntryId);
                    var (reloaded, _) = store.List();
                    FillToolkitList(toolkitListPanel!, store, reloaded);
                    await Task.CompletedTask;
                });
                actions.Children.Add(removeBtn);
                row.Children.Add(actions);
            }
            contents.Add(row);
        }
        ReplaceSettingsRows(panel, ToolkitEntryRow, contents, panel.Children.OfType<Border>().FirstOrDefault(row => row.Tag is ToolkitResultsRow or ToolkitFooterRow));
    }

    /// <summary>The bundled chip's tint: the secondary ink at 0.15 (M/ComponentsSettingsView.swift:155).</summary>
    private const double ToolkitBadgeTint = 0.15;

    // Fills the result table after a run: one line per tool with its verdict's mark and word
    // (M/ComponentsSettingsView.swift:199-214), in a row of its own that shows once there is a result.
    private void FillToolkitResults(StackPanel panel, IReadOnlyList<ToolkitRunItem> results)
    {
        panel.Children.Clear();
        foreach (var item in results)
        {
            var (label, glyph, ink) = item.RunVerdict switch
            {
                ToolkitRunItem.Verdict.Installed => (Locale.Get("settings.toolkit.verdictInstalled"), "", DesignToken.DoneText),
                ToolkitRunItem.Verdict.Failed => (Locale.Get("settings.toolkit.verdictFailed"), "", DesignToken.ErrText),
                _ => (Locale.Get("settings.toolkit.verdictSkipped"), "", DesignToken.Ink2),
            };
            var line = new Grid { ColumnSpacing = 6 };
            line.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); line.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            line.Children.Add(SettingsSymbol(glyph, 11, ink));
            var name = SettingsText(item.EntryId, 11); Grid.SetColumn(name, 1); line.Children.Add(name);
            var verdict = SettingsText(label, 10, ink, medium: true); Grid.SetColumn(verdict, 2); line.Children.Add(verdict);
            panel.Children.Add(line);
        }
        if (panel.Parent is Border row) row.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // One confirm view that lists every argv before anything runs.
    private async Task RunToolkitInstall(ToolkitStore store)
    {
        if (toolkitRunning) return;
        // Never show or await a modal and never run a real install in smoke mode:
        // the smoke drives ToolkitRunner directly with a fake executor.
        if (options.SmokeTest) return;
        var runner = new ToolkitRunner(store, LiveProbeContext());
        var plan = runner.Plan();
        if (plan.Count == 0) return;

        // Show confirm dialog: list every argv before running anything.
        var argsList = new StackPanel { Spacing = 4 };
        argsList.Children.Add(new TextBlock
        {
            Text = Locale.Get("settings.toolkit.confirmDescription"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6),
        });
        foreach (var item in plan.Where(i => i.Action == ToolkitPlanItem.PlanAction.Run))
            foreach (var argv in item.Commands)
                argsList.Children.Add(new TextBlock
                {
                    Text = string.Join(" ", argv),
                    FontSize = 11,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(DesignMetrics.Font.Mono),
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = .85,
                });

        var confirm = StyledDialog(new ContentDialog
        {
            Title = Locale.Get("settings.toolkit.confirmTitle"),
            Content = new ScrollViewer { Content = argsList, MaxHeight = 240 },
            PrimaryButtonText = Locale.Get("settings.toolkit.confirmInstall"),
            CloseButtonText = Locale.Get("settings.toolkit.cancelButton"),
            XamlRoot = SettingsXamlRoot,
        });
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        toolkitRunning = true;
        if (toolkitInstallButton is not null)
        {
            toolkitInstallButton.Content = Locale.Get("settings.toolkit.installingButton");
            toolkitInstallButton.IsEnabled = false;
        }
        try
        {
            var results = await Task.Run(() => runner.Run(plan, new CliToolkitExecutor()));
            toolkitRunResults = results;
            if (toolkitResultsPanel is not null) FillToolkitResults(toolkitResultsPanel, results);
            var (reloaded, _) = store.List();
            if (toolkitListPanel is not null) FillToolkitList(toolkitListPanel, store, reloaded);
        }
        finally
        {
            toolkitRunning = false;
            if (toolkitInstallButton is not null)
            {
                toolkitInstallButton.Content = Locale.Get("settings.toolkit.installButton");
                toolkitInstallButton.IsEnabled = true;
            }
        }
    }

    // Adds one entry from a JSON file the user picks.
    private async Task AddToolkitEntry(ToolkitStore store)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(settingsWindow ?? this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var json = await Windows.Storage.FileIO.ReadTextAsync(file);
        var parsed = ToolkitFileReader.Parse(json);
        if (parsed.Entries.Count == 0)
        {
            await StyledDialog(new ContentDialog
            {
                Title = "toolkit",
                Content = new TextBlock { Text = Locale.Get("settings.toolkit.errorEntryFileTemplate").Replace("{name}", file.Name), TextWrapping = TextWrapping.Wrap },
                CloseButtonText = Locale.Get("settings.toolkit.cancelButton"),
                XamlRoot = SettingsXamlRoot,
            }).ShowAsync();
            return;
        }
        foreach (var entry in parsed.Entries)
            store.Add(entry);
        var (reloaded, _) = store.List();
        if (toolkitListPanel is not null) FillToolkitList(toolkitListPanel, store, reloaded);
    }

    // Exports every entry, other-OS ones included, without approvals.
    private async Task ExportToolkit(ToolkitStore store)
    {
        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("JSON", [".json"]);
        picker.SuggestedFileName = "toolkit";
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(settingsWindow ?? this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        var export = store.Export();
        await Windows.Storage.FileIO.WriteTextAsync(file, export);
    }

    // Imports an exported array (macOS or Windows) and merges it into the store;
    // entries for the other OS are kept in the file and stay hidden.
    private async Task ImportToolkit(ToolkitStore store)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(settingsWindow ?? this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var json = await Windows.Storage.FileIO.ReadTextAsync(file);
        store.Import(json);
        var (reloaded, _) = store.List();
        if (toolkitListPanel is not null) FillToolkitList(toolkitListPanel, store, reloaded);
    }

    // Runs one toolkit argv synchronously; called from a background thread by ToolkitRunner.Run.
    private sealed class CliToolkitExecutor : IToolkitRunnerExecutor
    {
        private readonly ICliRunner runner = new CliRunner();

        public ToolkitCommandOutput Run(IReadOnlyList<string> argv)
        {
            if (argv.Count == 0) return ToolkitCommandOutput.Success;
            var (binary, args) = (argv[0], argv.Skip(1).ToArray());
            var result = runner.RunAsync(binary, args, TimeSpan.FromMinutes(5)).GetAwaiter().GetResult();
            return result.ExitCode == 0
                ? ToolkitCommandOutput.Success
                : ToolkitCommandOutput.Failure(result.Output + result.ErrorOutput, result.ExitCode);
        }
    }

    // The CLIs on this PC (M/SettingsViews.swift:279-297): one row per CLI — its mark, name and 베타
    // capsule against the ready or needs-setup word, the version in 10pt mono, the detail — then the
    // note beside the recheck button, which probes the runtime again and redraws the rows.
    private StackPanel BuildProvidersSection()
    {
        var rows = new StackPanel();
        const string footerRow = "providers-footer", providerRow = "provider";
        var checkLabel = Locale.Get("settings.providers.checkButton");
        void Fill()
        {
            var contents = new List<FrameworkElement>();
            foreach (var id in Wire.Providers)
            {
                var item = runtime?.Providers.FirstOrDefault(provider => provider.Id == id);
                var row = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 4) };
                AutomationProperties.SetAutomationId(row, "settings-provider-" + id);
                var header = new Grid { ColumnSpacing = 8 };
                header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                name.Children.Add(ProviderMarkView.Create(id, SettingsProviderMark));
                name.Children.Add(SettingsText(item?.Name ?? ProviderCatalog.Name(id), 13, medium: true));
                if (ProviderCatalog.IsBeta(id)) name.Children.Add(BetaBadgeView.Create(brushes));
                header.Children.Add(name);
                // Until the runtime has been read the row says so, in the quiet ink, rather than that the CLI needs setting up.
                var ready = item?.Available == true;
                var state = item is null ? SettingsText(Locale.Get("settings.components.statusChecking"), 10, DesignToken.Ink2)
                    : SettingsText(Locale.Get(ready ? "settings.providers.statusReady" : "settings.providers.statusNeedsSetup"), 10, ready ? DesignToken.DoneText : DesignToken.WaitText);
                Grid.SetColumn(state, 1); header.Children.Add(state);
                row.Children.Add(header);
                if (item?.Version is { Length: > 0 } version) row.Children.Add(SettingsText(version, 10, DesignToken.Ink2, mono: true));
                row.Children.Add(SettingsText(item?.Detail ?? Locale.Get("window.status.checkingRuntime"), 11, DesignToken.Ink2, selectable: true));
                contents.Add(row);
            }
            ReplaceSettingsRows(rows, providerRow, contents, rows.Children.OfType<Border>().FirstOrDefault(row => Equals(row.Tag, footerRow)));
        }
        var check = SettingsPush(new Button { Content = checkLabel });
        AutomationProperties.SetName(check, checkLabel); AutomationProperties.SetAutomationId(check, "settings-providers-check");
        check.Click += async (_, _) =>
        {
            check.Content = Locale.Get("settings.providers.checkingButton"); check.IsEnabled = false;
            try { await RefreshRuntime(); }
            finally { check.Content = checkLabel; check.IsEnabled = true; if (!closing) Fill(); }
        };
        SettingsRow(rows, SettingsLabeled(SettingsText(Locale.Get("settings.providers.loginNote"), 11, DesignToken.Ink2), check), tag: footerRow);
        Fill();
        // The tab's own read of the runtime draws these rows again through this (RefreshVisibleSettingsAccountsAsync).
        rows.Tag = new Action(Fill);
        return rows;
    }

    // CLI accounts — who each CLI is signed in as, and the sign-in / change / sign-out
    // buttons. Every decision (what the row says, whether sign-out may be offered,
    // which command a button runs, how the terminal is started) comes from Core;
    // this method only renders state and forwards the click. No Korean literal is
    // typed here — the copy is CliAccountStrings.
    private StackPanel BuildCliAccountsSectionFromState() =>
        BuildCliAccountsSection(CliAccountProviders.Select(p =>
            accountsCoordinator.Statuses.TryGetValue(p, out var status)
                ? status
                : new CliAccountStatus { Provider = p, Detail = CliAccountStrings.StatusChecking }).ToArray());

    internal static readonly string[] CliAccountProviders = ["claude", "codex", "gemini"];
    internal const string AccountRowIdPrefix = "cli-account-row-";

    // Called by OpenSettings (via BuildCliAccountsSectionFromState) and by the smoke
    // check, which hands it fixture statuses instead of live ones.
    // M/CLIAccountsSettingsView.swift:11-85: one row per CLI, all on one line — its mark in an 18-wide
    // column, the name (12 medium) with the 베타 capsule over the 11pt status line, a spacer, then the ring
    // while the row works, the buttons the state allows, Claude's model reset and Bedrock buttons and the
    // plain refresh mark — and the section's explanation as the last row.
    internal StackPanel BuildCliAccountsSection(IReadOnlyList<CliAccountStatus> statuses)
    {
        var rows = new StackPanel();
        var shown = statuses.ToList();
        // What holds a row while one of its buttons works: its ring turns and its buttons wait (.disabled(busy)).
        // The rows are drawn again whenever any of them finishes, so the ones still working are held again as they are drawn.
        var holds = new Dictionary<string, Action>(); var working = new HashSet<string>();
        void Fill()
        {
            rows.Children.Clear(); holds.Clear();
            foreach (var status in shown) SettingsRow(rows, AccountRow(status));
            SettingsRow(rows, SettingsText(CliAccountStrings.SectionDescription, 11, DesignToken.Ink2));
            // M/CLIAccountsSettingsView.swift: the automatic sign-in switch over its explanation, on unless saved off.
            var autoLoginLabel = Locale.Get("settings.cliAccounts.autoLoginToggle");
            var autoLoginToggle = SettingsSwitch(autoLoginLabel, service.Snapshot.AutoLoginCLIs != false, "cli-auto-login");
            void AutoLoginToggled() => _ = Act(async () => await service.UpdateAsync(s => s with { AutoLoginCLIs = autoLoginToggle.IsChecked == true }));
            autoLoginToggle.Checked += (_, _) => AutoLoginToggled(); autoLoginToggle.Unchecked += (_, _) => AutoLoginToggled();
            SettingsRow(rows, SettingsLabeled(SettingsText(autoLoginLabel), autoLoginToggle));
            SettingsRow(rows, SettingsText(Locale.Get("settings.cliAccounts.autoLoginDescription"), 11, DesignToken.Ink2));
        }
        // The rows again from what the coordinator now knows.
        void Refill()
        {
            for (var i = 0; i < shown.Count; i++) if (accountsCoordinator.Statuses.TryGetValue(shown[i].Provider, out var fresh)) shown[i] = fresh;
            Fill();
        }
        // A button's action, then the rows again.
        Func<Task> Then(string provider, Func<Task> action) => async () =>
        {
            working.Add(provider);
            if (holds.TryGetValue(provider, out var hold)) hold();
            try { await action(); }
            finally { working.Remove(provider); if (!closing) Refill(); }
        };
        // The tab's own read of the statuses draws these rows again through this, in place (RefreshVisibleSettingsAccountsAsync).
        rows.Tag = new Action(Refill);
        Grid AccountRow(CliAccountStatus status)
        {
            var provider = status.Provider;
            var row = new Grid { RowSpacing = 6 };
            AutomationProperties.SetAutomationId(row, AccountRowIdPrefix + provider);
            row.RowDefinitions.Add(new() { Height = GridLength.Auto }); row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var buttons = new List<Button>();
            // The line's parts stand 8 apart, each at its own width. The words and the model reset are the two
            // that give way where the line is too short — the Mac draws "모델 초기화·…" beside a cut account —
            // and the spacer between the words and the ring takes what is left over.
            void Place(FrameworkElement part, GridLength width, double most = double.PositiveInfinity)
            {
                var first = row.ColumnDefinitions.Count == 0;
                row.ColumnDefinitions.Add(new() { Width = width, MaxWidth = first ? most : most + AccountGap });
                if (!first) part.Margin = new Thickness(AccountGap, part.Margin.Top, 0, 0);
                Grid.SetColumn(part, row.ColumnDefinitions.Count - 1); row.Children.Add(part);
            }
            static double Natural(FrameworkElement part) { part.Measure(new(double.PositiveInfinity, double.PositiveInfinity)); return Math.Ceiling(part.DesiredSize.Width) + 1; }
            T Held<T>(T button) where T : Button { button.VerticalAlignment = VerticalAlignment.Top; buttons.Add(button); return button; }

            var mark = ProviderMarkView.Create(provider, 16);
            mark.HorizontalAlignment = HorizontalAlignment.Center; mark.VerticalAlignment = VerticalAlignment.Top; mark.Margin = new Thickness(0, 1, 0, 0);
            Place(mark, new(18));

            var words = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Left };
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            name.Children.Add(SettingsText(CliUpdateService.ProviderLabel(provider), 12, medium: true));
            if (ProviderCatalog.IsBeta(provider)) name.Children.Add(BetaBadgeView.Create(brushes));
            words.Children.Add(name);
            // The summary already reads "account · plan · method", the signed-out word, or the unknown sentence.
            var summary = SettingsText(status.Summary, 11, status.LoggedIn == false ? DesignToken.WaitText : DesignToken.Ink2, selectable: true);
            summary.TextWrapping = TextWrapping.NoWrap; summary.TextTrimming = TextTrimming.CharacterEllipsis;
            AutomationProperties.SetAutomationId(summary, "cli-account-status-" + provider);
            // The line's end may be cut; the pointer shows it whole.
            ToolTipService.SetToolTip(summary, status.Summary);
            words.Children.Add(summary);
            // The detail under the summary: 10pt in the tertiary ink (M/CLIAccountsSettingsView.swift:46).
            // The detail of a signed-in account is the Mac's tertiary line; one that says what went wrong (not signed in, a
            // status that could not be read) has to be read, so it keeps the secondary ink.
            if (status.Detail.Length > 0 && status.Detail != status.Summary) words.Children.Add(status.LoggedIn == true ? SettingsTertiary(status.Detail) : SettingsText(status.Detail, 10, DesignToken.Ink2));
            Place(words, new(AccountGive, GridUnitType.Star), Natural(words));
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });

            // The ring turns while the status is being read (the tab just opened, or nothing is known yet) and while a button of the row works.
            var reading = settingsAccountRefreshes > 0 || status.LoggedIn is null && status.Detail == CliAccountStrings.StatusChecking;
            var ring = new ProgressRing { Width = 16, Height = 16, MinWidth = 0, MinHeight = 0, IsActive = reading, Visibility = reading ? Visibility.Visible : Visibility.Collapsed, Foreground = brushes.Brush(DesignToken.Ink2), VerticalAlignment = VerticalAlignment.Top };
            Place(ring, GridLength.Auto);
            holds[provider] = () => { ring.IsActive = true; ring.Visibility = Visibility.Visible; foreach (var button in buttons) button.IsEnabled = false; };

            if (!status.Installed)
            {
                var missing = SettingsText(CliAccountStrings.StatusNotInstalled, 11, DesignToken.Ink2);
                missing.VerticalAlignment = VerticalAlignment.Top; Place(missing, GridLength.Auto);
            }
            else if (status.LoggedIn == true)
            {
                var change = Held(SettingsPush(SafeButton(CliAccountStrings.ButtonChange, Then(provider, () => StartCliSignIn(provider, CliLoginOption.Account)))));
                AutomationProperties.SetAutomationId(change, "cli-account-change-" + provider); Place(change, GridLength.Auto);
                // Sign-out is offered only where the app can undo the sign-in.
                if (status.CanSignOut) Place(Held(SettingsPush(SafeButton(CliAccountStrings.ButtonLogout, Then(provider, () => ConfirmCliSignOut(provider))))), GridLength.Auto);
            }
            else if (provider == "claude")
            {
                // Claude signs in two ways: the subscription or the API-billed console (the Mac's 로그인 menu).
                var menu = new MenuFlyout();
                foreach (var (title, option) in new[] { (CliAccountStrings.ButtonLoginClaude, CliLoginOption.Account), (CliAccountStrings.ButtonLoginConsole, CliLoginOption.Console) })
                {
                    var item = new MenuFlyoutItem { Text = title };
                    var signIn = Then(provider, () => StartCliSignIn(provider, option));
                    item.Click += async (_, _) => await Act(signIn);
                    menu.Items.Add(item);
                }
                var login = Held(SettingsMenuButton(CliAccountStrings.ButtonLogin, menu));
                AutomationProperties.SetAutomationId(login, "cli-account-login-" + provider); Place(login, GridLength.Auto);
            }
            else
            {
                var login = Held(SettingsPush(SafeButton(CliAccountStrings.ButtonLogin, Then(provider, () => StartCliSignIn(provider, CliLoginOption.Account)))));
                AutomationProperties.SetAutomationId(login, "cli-account-login-" + provider); Place(login, GridLength.Auto);
            }

            Button? resetBedrock = null;
            if (status.Installed && provider == "claude")
            {
                var resetTitle = Locale.Get("settings.cliAccounts.resetModelsButton");
                var resetModels = Held(SettingsPush(SafeButton(resetTitle, Then(provider, ResetClaudeModels))));
                var resetWords = new TextBlock { Text = resetTitle, FontSize = resetModels.FontSize, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
                resetModels.Content = resetWords; resetModels.HorizontalAlignment = HorizontalAlignment.Stretch;
                ToolTipService.SetToolTip(resetModels, Locale.Get("settings.cliAccounts.resetModelsHelp")); AutomationProperties.SetAutomationId(resetModels, "cli-account-reset-models-claude");
                Place(resetModels, new(AccountGive, GridUnitType.Star), Natural(resetWords) + resetModels.Padding.Left + resetModels.Padding.Right + 2 * DesignMetrics.Stroke.Line);
                var bedrock = Held(SettingsPush(SafeButton(Locale.Get("settings.cliAccounts.bedrockButton"), Then(provider, () => StartCliSignIn("claude", CliLoginOption.Bedrock)))));
                ToolTipService.SetToolTip(bedrock, Locale.Get("settings.cliAccounts.bedrockHelp")); AutomationProperties.SetAutomationId(bedrock, "cli-account-bedrock-claude");
                Place(bedrock, GridLength.Auto);
                resetBedrock = Held(SettingsPush(SafeButton(Locale.Get("settings.cliAccounts.resetBedrockButton"), Then(provider, ResetClaudeBedrock))));
                AutomationProperties.SetAutomationId(resetBedrock, "cli-account-reset-bedrock-claude");
            }
            var refresh = Held(SettingsIconButton("", CliAccountStrings.RefreshTooltip, () => Act(Then(provider, () => accountsCoordinator.RefreshAsync([provider])))));
            AutomationProperties.SetAutomationId(refresh, "cli-account-refresh-" + provider); Place(refresh, GridLength.Auto);
            // The symbol stands on the buttons' line a point over their middle (the Mac sets it at the line's top).
            refresh.Margin = new Thickness(refresh.Margin.Left, -1, 0, 0);

            // Windows also resets Claude's Bedrock settings. The Mac has no such button, so it takes the line under the Mac's own.
            if (resetBedrock is not null)
            {
                resetBedrock.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetRow(resetBedrock, 1); Grid.SetColumnSpan(resetBedrock, row.ColumnDefinitions.Count); row.Children.Add(resetBedrock);
            }
            if (working.Contains(provider)) holds[provider]();
            return row;
        }
        Fill();
        return rows;
    }

    /// <summary>The space between the parts of an account's line (M/CLIAccountsSettingsView.swift:36).</summary>
    private const double AccountGap = 8;
    /// <summary>The words' and the model reset's weight against the spacer's 1 on a short line: they take all of it but a thousandth.</summary>
    private const double AccountGive = 1000;
    /// <summary>How many readings of the account statuses are under way for the open CLI tab.</summary>
    private int settingsAccountRefreshes;

    /// <summary>
    /// The Mac's pull-down button in a form (M/CLIAccountsSettingsView.swift:66): its 13pt words, then a
    /// 16pt rounded chip with the chevron that says it opens a menu. A Button with a MenuFlyout.
    /// </summary>
    private Button SettingsMenuButton(string title, MenuFlyout menu)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(new TextBlock { Text = title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(SettingsChip(""));
        var button = new Button { Content = line, Flyout = menu, Height = SettingsControlHeight, MinHeight = 0, MinWidth = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(SettingsControlRadius) };
        PaintPlainButton(button, brushes.Transparent, brushes.Transparent, ink: brushes.Brush(DesignToken.Ink), disabledInk: brushes.Brush(DesignToken.Ink3));
        AutomationProperties.SetName(button, title);
        return button;
    }

    // Opens the external sign-in terminal and refreshes the status when it closes.
    // The app never types or receives credentials; it only starts the CLI's own
    // login command. The terminal rule lives in Core (CliAccountTerminal).
    private async Task StartCliSignIn(string provider, CliLoginOption option)
    {
        RequireIdleAccount(provider);
        if (CliAccountSupport.LoginArguments(provider, option) is not { } argv) return;
        accountChanges.TryAdd(provider, 0);
        try
        {
            await accountsCoordinator.StartSignInAsync(argv); await RefreshCliAccounts();
            // A confirmed sign-in from the terminal ends the automatic-start cooldown the way a background one does.
            if (accountsCoordinator.Statuses.TryGetValue(provider, out var status) && status.LoggedIn == true && CliAuthFailure.SignInCanFix(status)) autoLogin.Succeeded(provider, DateTimeOffset.UtcNow);
        }
        finally { accountChanges.TryRemove(provider, out _); }
    }

    // The macOS confirmation: the stored sign-in of that CLI is removed, and this
    // also applies to the CLI used directly in a terminal.
    private async Task ConfirmCliSignOut(string provider)
    {
        RequireIdleAccount(provider);
        var label = CliUpdateService.ProviderLabel(provider);
        var dialog = StyledDialog(new ContentDialog
        {
            Title = CliAccountStrings.ConfirmLogoutTitleTemplate.Replace("{provider}", label),
            Content = new TextBlock
            {
                Text = CliAccountStrings.ConfirmMessageTemplate.Replace("{provider}", label),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = CliAccountStrings.ButtonLogout,
            CloseButtonText = CliAccountStrings.ButtonCancel,
            XamlRoot = SettingsXamlRoot,
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        RequireIdleAccount(provider);
        accountChanges.TryAdd(provider, 0);
        try { await accountsCoordinator.LogoutAsync(provider); await RefreshCliAccounts(); }
        finally { accountChanges.TryRemove(provider, out _); }
    }

    // Read the statuses when the section opens and after a sign-in terminal closes.
    internal async Task RefreshCliAccounts()
    {
        await accountsCoordinator.RefreshAsync(CliAccountProviders);
        if (await ReloadProviderModels()) RefreshEnvironment();
    }

    private int modelRefreshRevision;
    private async Task<bool> ReloadProviderModels()
    {
        var state = service.Snapshot;
        var workspaceId = state.ActiveWorkspaceId;
        var path = state.Workspaces.FirstOrDefault(w => w.Id == workspaceId)?.Path;
        var revision = ++modelRefreshRevision;
        var next = await service.Providers.GetRuntimeAsync(true, path);
        if (closing || revision != modelRefreshRevision || service.Snapshot.ActiveWorkspaceId != workspaceId) return false;
        runtime = next;
        return true;
    }

    private void RequireIdleAccount(string provider)
    {
        if (loginBusy.ContainsKey(provider) || accountChanges.ContainsKey(provider)) throw new InvalidOperationException(Locale.Get("loginRecovery.busy"));
        if (AnyCliUpdateRunning) throw new InvalidOperationException(Locale.Get("loginRecovery.updating"));
        if (service.HasActiveProvider(provider)) throw new InvalidOperationException(Locale.Get("settings.cliAccounts.waitForRuns"));
    }
    private async Task ResetClaudeModels()
    {
        RequireIdleAccount("claude");
        await service.UpdateAsync(snapshot => snapshot with { Sessions = snapshot.Sessions.Select(pane => pane.Kind == "claude" && pane.Provider == "claude" ? pane with { Model = "default", Settings = pane.Settings with { Effort = "default" } } : pane).ToList() });
        await RefreshCliAccounts(); Render();
    }
    private async Task ResetClaudeBedrock()
    {
        RequireIdleAccount("claude");
        var dialog = StyledDialog(new ContentDialog { Title = Locale.Get("settings.cliAccounts.resetBedrockButton"), Content = new TextBlock { Text = Locale.Get("settings.cliAccounts.resetBedrockConfirm"), TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = Locale.Get("settings.cliAccounts.resetBedrockButton"), CloseButtonText = CliAccountStrings.ButtonCancel, XamlRoot = SettingsXamlRoot });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        RequireIdleAccount("claude");
        var environment = await CliEnvironment.RefreshAsync(true);
        if (closing) return;
        RequireIdleAccount("claude");
        BedrockSettings.ResetUserSettings(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), environment);
        await ResetClaudeModels();
    }

    // 앱 정보 (M/SettingsViews.swift:313-320): the version beside its label, the 11pt note (on Windows the
    // usage notes), the state folder's label beside the button that shows it, and its path in 10pt mono.
    private StackPanel BuildAppInfoSection()
    {
        var rows = new StackPanel();
        SettingsRow(rows, SettingsLabeled(SettingsText(Locale.Get("settings.appInfo.versionLabel")), SettingsText(AppVersionText, 13, DesignToken.Ink2, selectable: true)));
        SettingsRow(rows, SettingsText(Locale.Get("settings.appInfo.windowsNote"), 11, DesignToken.Ink2));
        var folder = StateDirectory;
        var show = SettingsPush(SafeButton(Locale.Get("menu.showInExplorer"), async () => await Windows.System.Launcher.LaunchFolderPathAsync(folder)));
        AutomationProperties.SetAutomationId(show, "settings-state-folder");
        SettingsRow(rows, SettingsLabeled(SettingsText(Locale.Get("settings.appInfo.stateLocationLabel")), show));
        SettingsRow(rows, SettingsText(folder, 10, DesignToken.Ink2, mono: true, selectable: true));
        return rows;
    }

    // Smoke: opens the sectioned Settings screen, checks the sections appear in
    // the macOS order with their titles, shows the fixture update results
    // (updated, current, skipped, failed) in the CLI update section, flips the
    // auto-update switch and reads it back from the saved state, then restores it.
    //
    // The order/flip/restore decision lives in SettingsSectionsSmoke (Core), so
    // the same rules this run enforces are proven on the Mac by
    // "settings sections ..." in Core.Tests. Here we only build the real screen
    // and hand Core what it actually rendered.
    internal async Task<SettingsSectionsSmokeOutcome> RunSettingsSectionsSmoke()
    {
        // Build the screen exactly as OpenSettings does.
        var sections = GetSettingsSections();
        var content = new StackPanel { Spacing = 0, MinWidth = 420, MaxWidth = 540 };
        foreach (var section in sections)
            content.Children.Add(BuildSectionContainer(section.Title, section.Build()));

        // Read the headings back off the built tree, not off the registration.
        var renderedTitles = content.Children.OfType<StackPanel>()
            .Select(wrapper => wrapper.Children.OfType<TextBlock>().First().Text)
            .ToArray();

        // Show the fixture results in the CLI update section and read the rows back.
        var cliPanel = BuildCliUpdateSection(SettingsSectionsSmoke.FixtureResults);
        // Each result is a row of the box, in the order shown; its id is on the row's own panel.
        var renderedStatuses = SettingsElements(cliPanel)
            .Select(row => AutomationProperties.GetAutomationId(row))
            .Where(id => id.StartsWith(ResultRowIdPrefix, StringComparison.Ordinal))
            .Select(id => id[(id.LastIndexOf('-') + 1)..])
            .ToArray();

        var outcome = await SettingsSectionsSmoke.RunAsync(
            renderedTitles,
            renderedStatuses,
            () => service.Snapshot.AutoUpdateCLIs,
            value => service.UpdateAsync(s => s with { AutoUpdateCLIs = value }));

        // The toggle the user sees must reflect the restored saved value.
        Require(SettingsElements(cliPanel).OfType<ToggleButton>().Any(toggle => AutomationProperties.GetAutomationId(toggle) == "cli-auto-update"), "the CLI update section must show the auto-update switch");
        return outcome;
    }
}
