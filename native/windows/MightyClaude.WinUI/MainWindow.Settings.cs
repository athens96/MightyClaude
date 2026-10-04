using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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

    private Task OpenSettings() => Act(ShowCategorizedSettingsAsync);

    // Display — theme, language picker, and completion-notification controls.
    private StackPanel BuildDisplaySection()
    {
        var panel = new StackPanel { Spacing = 8 };
        var theme = new ComboBox { Header = Locale.Get("settings.display.themeLabel"), HorizontalAlignment = HorizontalAlignment.Stretch };
        theme.Items.Add(new ComboBoxItem { Content = Locale.Get("settings.display.themeDarkWindows"), Tag = "dark" });
        theme.Items.Add(new ComboBoxItem { Content = Locale.Get("settings.display.themeLightWindows"), Tag = "light" });
        theme.SelectedIndex = service.Snapshot.Theme == "light" ? 1 : 0;
        theme.SelectionChanged += async (_, _) =>
        {
            if (theme.SelectedItem is ComboBoxItem item)
                await Act(async () => { await service.UpdateAsync(s => s with { Theme = (string)item.Tag }); Render(); });
        };
        panel.Children.Add(theme);

        // Language picker — takes effect on the next app start.
        var language = new ComboBox { Header = Locale.Get("settings.display.languageLabel"), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(language, "settings-language");
        language.Items.Add(new ComboBoxItem { Content = Locale.Get("settings.display.languageSystem"), Tag = "system" });
        language.Items.Add(new ComboBoxItem { Content = Locale.Get("settings.display.languageKorean"), Tag = "ko" });
        language.Items.Add(new ComboBoxItem { Content = Locale.Get("settings.display.languageEnglish"), Tag = "en" });
        var savedLang = service.Snapshot.LanguagePreference;
        language.SelectedIndex = savedLang switch { "ko" => 1, "en" => 2, _ => 0 };
        language.SelectionChanged += async (_, _) =>
        {
            if (language.SelectedItem is ComboBoxItem item)
                await Act(async () => await service.UpdateAsync(s => s with { LanguagePreference = (string)item.Tag }));
        };
        panel.Children.Add(language);

        panel.Children.Add(BuildNotificationSettingsSection());
        var statusLineToggle = new ToggleSwitch
        {
            Header = Locale.Get("settings.display.statusLineToggle"),
            IsOn = service.Snapshot.StatusLineEnabled,
        };
        AutomationProperties.SetAutomationId(statusLineToggle, "settings-status-line");
        statusLineToggle.Toggled += async (_, _) => await Act(() => SetStatusLineEnabled(statusLineToggle.IsOn));
        panel.Children.Add(statusLineToggle);
        panel.Children.Add(new TextBlock { Text = Locale.Get("settings.display.statusLineDescription"), TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = .65 });

        // Browser engine toggle — opt-in, off by default; restart required to apply.
        var browserToggle = new ToggleSwitch
        {
            Header = Locale.Get("settings.display.browserToggle"),
            IsOn = service.Snapshot.BrowserEngineEnabled,
        };
        AutomationProperties.SetName(browserToggle, Locale.Get("settings.display.browserToggle"));
        panel.Children.Add(new TextBlock { Text = Locale.Get("windows.settings.browserDescription"), TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = .65 });
        browserToggle.Toggled += async (_, _) =>
            await Act(async () => await service.UpdateAsync(s => s with { BrowserEngineEnabled = browserToggle.IsOn }));
        panel.Children.Add(browserToggle);
        panel.Children.Add(BuildAgentWebOpenSetting());

        return panel;
    }

    private StackPanel BuildClaudeModsSection()
    {
        var panel = new StackPanel { Spacing = 8 };
        var mods = runtime?.Mods;
        panel.Children.Add(new TextBlock { Text = mods?.Detail ?? Locale.Get("window.status.checkingRuntime"), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        if (mods is not null)
            panel.Children.Add(new TextBlock { Text = Locale.Get("settings.claudeMods.compatLabel") + "  " + mods.MinimumVersion, FontSize = 12, IsTextSelectionEnabled = true });
        return panel;
    }

    // CLI updates — delegates to the parameterised builder so smoke can inject fixture results.
    private StackPanel BuildCliUpdateSectionFromState() => BuildCliUpdateSection(lastCliUpdateResults);

    // Called by both OpenSettings (via BuildCliUpdateSectionFromState) and the smoke check.
    internal StackPanel BuildCliUpdateSection(IReadOnlyList<CliUpdateResult> results)
    {
        var panel = new StackPanel { Spacing = 6 };
        var toggle = new ToggleSwitch
        {
            Header = CliUpdateStrings.AutoUpdateToggle,
            IsOn = service.Snapshot.AutoUpdateCLIs == true,
            OffContent = "",
            OnContent = "",
        };
        AutomationProperties.SetAutomationId(toggle, "cli-auto-update");
        toggle.Toggled += async (_, _) =>
            await service.UpdateAsync(s => s with { AutoUpdateCLIs = toggle.IsOn });
        panel.Children.Add(toggle);
        panel.Children.Add(new TextBlock
        {
            Text = CliUpdateStrings.SectionDescription,
            FontSize = 12,
            Opacity = .7,
            TextWrapping = TextWrapping.Wrap,
        });
        var pluginsToggle = new ToggleSwitch { Header = Locale.Get("settings.cliUpdate.autoUpdatePluginsToggle"), IsOn = service.Snapshot.AutoUpdatePlugins != false, OffContent = "", OnContent = "" };
        AutomationProperties.SetAutomationId(pluginsToggle, "plugin-auto-update");
        pluginsToggle.Toggled += async (_, _) => await service.UpdateAsync(s => s with { AutoUpdatePlugins = pluginsToggle.IsOn });
        panel.Children.Add(pluginsToggle);
        panel.Children.Add(new TextBlock { Text = Locale.Get("settings.cliUpdate.autoUpdatePluginsDescription"), FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap });
        var updateProgress = new TextBlock { FontSize = 11, Opacity = .7, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(updateProgress, "cli-update-progress"); panel.Children.Add(updateProgress);

        // The update-now button — reflects coordinator state live.
        var updateButton = new Button
        {
            Content = AnyCliUpdateRunning ? CliUpdateStrings.UpdatingButton : CliUpdateStrings.UpdateButton,
            IsEnabled = !AnyCliUpdateRunning && !pluginOperations.IsRunning,
        };
        AutomationProperties.SetAutomationId(updateButton, "cli-update-start");
        updateButton.Click += (_, _) => { if (!AnyCliUpdateRunning && !pluginOperations.IsRunning) coordinator.Start(); };
        panel.Children.Add(updateButton);

        // Dynamic results area: rebuilt on each StateChanged while the section is visible.
        var resultsPanel = new StackPanel { Spacing = 6 };
        void AddResultRow(CliUpdateResult result)
        {
            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(new TextBlock
            {
                Text = CliUpdateService.ResultRow(result),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            if (CliUpdateService.VersionChange(result) is { } change)
                row.Children.Add(new TextBlock { Text = change, FontSize = 11, Opacity = .7 });
            if (!string.IsNullOrEmpty(result.Detail))
                row.Children.Add(new TextBlock
                {
                    Text = result.Detail,
                    FontSize = 11,
                    Opacity = .7,
                    TextWrapping = TextWrapping.Wrap,
                });
            AutomationProperties.SetAutomationId(row, ResultRowAutomationId(result));
            resultsPanel.Children.Add(Toned(row));
        }
        foreach (var result in results) AddResultRow(result);
        panel.Children.Add(resultsPanel);
        var pluginResults = new StackPanel { Spacing = 6 }; panel.Children.Add(pluginResults);
        void RefreshBackgroundUpdates()
        {
            updateButton.Content = AnyCliUpdateRunning ? CliUpdateStrings.UpdatingButton : CliUpdateStrings.UpdateButton;
            updateButton.IsEnabled = !AnyCliUpdateRunning && !pluginOperations.IsRunning;
            updateProgress.Text = automaticallyUpdatingProvider is { } provider
                ? Locale.Get(automaticallyUpdatingPlugins ? "settings.cliUpdate.progressPluginsTemplate" : "settings.cliUpdate.progressProviderTemplate", new Dictionary<string, string> { ["provider"] = ProviderCatalog.Name(provider) })
                : AnyCliUpdateRunning ? CliUpdateStrings.ProgressInspecting
                : (automaticUpdateFinishedAt is { } autoFinished && (coordinator.FinishedAt is not { } manualFinished || autoFinished > manualFinished) ? autoFinished : coordinator.FinishedAt) is { } finished
                    ? Locale.Get("settings.cliUpdate.lastRunTemplate", new Dictionary<string, string> { ["time"] = finished.ToLocalTime().ToString("t") }) : "";
            updateProgress.Visibility = updateProgress.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            pluginResults.Children.Clear();
            foreach (var providerName in Wire.Providers)
            {
                if (!pluginUpdateResults.TryGetValue(providerName, out var result)) continue;
                var row = new StackPanel { Spacing = 2 };
                var status = CliUpdateStrings.StatusLabel(result.Status == "succeeded" ? "updated" : result.Status);
                row.Children.Add(new TextBlock { Text = Locale.Get("settings.cliUpdate.pluginRowTemplate", new Dictionary<string, string> { ["provider"] = ProviderCatalog.Name(providerName), ["status"] = status }), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                row.Children.Add(new TextBlock { Text = result.Detail, FontSize = 11, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Opacity = .7 });
                AutomationProperties.SetAutomationId(row, "plugin-update-result-" + providerName); pluginResults.Children.Add(Toned(row));
            }
            if (automaticUpdateRunning) { resultsPanel.Children.Clear(); foreach (var result in lastCliUpdateResults) AddResultRow(result); }
        }
        RefreshBackgroundUpdates();
        void CoordinatorChanged() => DispatcherQueue.TryEnqueue(() =>
        {
            if (closing || !panel.IsLoaded) return;
            RefreshBackgroundUpdates();
            var live = coordinator.Results;
            if (live.Count > 0)
            {
                resultsPanel.Children.Clear();
                foreach (var r in live) AddResultRow(r);
            }
        });
        panel.Loaded += (_, _) => { AutomaticUpdatesChanged += RefreshBackgroundUpdates; coordinator.StateChanged += CoordinatorChanged; RefreshBackgroundUpdates(); };
        panel.Unloaded += (_, _) => { AutomaticUpdatesChanged -= RefreshBackgroundUpdates; coordinator.StateChanged -= CoordinatorChanged; };

        return panel;
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

    private StackPanel BuildComponentsSection()
    {
        var panel = new StackPanel { Spacing = 8 };

        panel.Children.Add(new TextBlock
        {
            Text = Locale.Get("settings.components.sectionDescription"),
            FontSize = 12,
            Opacity = .8,
            TextWrapping = TextWrapping.Wrap,
        });

        // CLI rows — re-filled by the recheck button.
        componentsCliPanel = new StackPanel { Spacing = 6 };
        FillComponentCliRows(componentsCliPanel);
        panel.Children.Add(componentsCliPanel);

        // Recheck button — re-probes the runtime without closing the dialog.
        var recheckLabel = Locale.Get("settings.components.recheckButton");
        var checkingLabel = Locale.Get("settings.components.checkingButton");
        var recheckBtn = new Button { Content = recheckLabel };
        AutomationProperties.SetAutomationId(recheckBtn, "components-refresh");
        recheckBtn.Click += async (_, _) => await Act(async () =>
        {
            recheckBtn.Content = checkingLabel;
            recheckBtn.IsEnabled = false;
            await RefreshRuntime();
            FillComponentCliRows(componentsCliPanel);
            recheckBtn.Content = recheckLabel;
            recheckBtn.IsEnabled = true;
        });
        panel.Children.Add(recheckBtn);

        // Toolkit heading
        panel.Children.Add(new TextBlock
        {
            Text = Locale.Get("settings.toolkit.sectionTitle"),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = .85,
        });

        panel.Children.Add(new TextBlock
        {
            Text = Locale.Get("settings.toolkit.sectionDescription"),
            FontSize = 12,
            Opacity = .8,
            TextWrapping = TextWrapping.Wrap,
        });

        // Toolkit list — error banner + entry rows.
        var store = GetToolkitStore();
        var (entries, fileError) = store.List();

        if (fileError is not null)
            panel.Children.Add(new TextBlock
            {
                Text = Locale.Get("settings.toolkit.errorBanner"),
                FontSize = 12,
                Foreground = brushes.Brush(DesignToken.WaitText),
                TextWrapping = TextWrapping.Wrap,
            });

        toolkitListPanel = new StackPanel { Spacing = 4 };
        FillToolkitList(toolkitListPanel, store, entries);
        panel.Children.Add(toolkitListPanel);

        // Results from the last install run.
        toolkitResultsPanel = new StackPanel { Spacing = 4 };
        if (toolkitRunResults is not null) FillToolkitResults(toolkitResultsPanel, toolkitRunResults);
        panel.Children.Add(toolkitResultsPanel);

        // Action buttons row.
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var addBtn = new Button { Content = Locale.Get("settings.toolkit.addButton") };
        AutomationProperties.SetAutomationId(addBtn, "settings-toolkit-add");
        addBtn.Click += async (_, _) => await Act(() => AddToolkitEntry(store));
        var exportBtn = new Button { Content = Locale.Get("settings.toolkit.exportButton") };
        AutomationProperties.SetAutomationId(exportBtn, "settings-toolkit-export");
        exportBtn.Click += async (_, _) => await Act(() => ExportToolkit(store));
        var importBtn = new Button { Content = Locale.Get("settings.toolkit.importButton") };
        AutomationProperties.SetAutomationId(importBtn, "settings-toolkit-import");
        importBtn.Click += async (_, _) => await Act(() => ImportToolkit(store));
        toolkitInstallButton = new Button { Content = Locale.Get("settings.toolkit.installButton"), IsEnabled = !toolkitRunning };
        AutomationProperties.SetAutomationId(toolkitInstallButton, "settings-toolkit-install");
        toolkitInstallButton.Click += async (_, _) => await Act(() => RunToolkitInstall(store));
        btnRow.Children.Add(addBtn);
        btnRow.Children.Add(exportBtn);
        btnRow.Children.Add(importBtn);
        btnRow.Children.Add(toolkitInstallButton);
        panel.Children.Add(btnRow);

        return panel;
    }

    // Renders one row per CLI provider (claude/codex/gemini).
    private void FillComponentCliRows(StackPanel panel)
    {
        panel.Children.Clear();
        var rt = runtime ?? new RuntimeInfo("win32", "0.0.0", false, null, null, [], null);
        foreach (var row in ComponentSection.SectionRows(rt))
        {
            var rowPanel = new StackPanel { Spacing = 4 };
            AutomationProperties.SetAutomationId(rowPanel, "component-" + row.Id);

            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(new TextBlock
            {
                Text = row.Title,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            if (row.Version is { } ver)
                header.Children.Add(new TextBlock { Text = ver, FontSize = 10, Opacity = .6 });
            header.Children.Add(new TextBlock
            {
                Text = ComponentStateLabel(row.State),
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            rowPanel.Children.Add(header);
            rowPanel.Children.Add(new TextBlock
            {
                Text = row.Detail,
                FontSize = 11,
                Opacity = .7,
                TextWrapping = TextWrapping.Wrap,
            });

            if (row.Actions.Count > 0)
            {
                var actionsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                foreach (var action in row.Actions)
                {
                    var actionId = action.Id;
                    var btn = new Button { Content = action.Title };
                    AutomationProperties.SetAutomationId(btn, "component-" + row.Id + "-" + actionId);
                    if (actionId == "copy-command" && ComponentSection.InstallCommand(row.Id) is { } cmd)
                        btn.Click += (_, _) => Copy(cmd);
                    actionsPanel.Children.Add(btn);
                }
                rowPanel.Children.Add(actionsPanel);
            }
            panel.Children.Add(Toned(rowPanel));
        }
    }

    private static string ComponentStateLabel(string state) => state switch
    {
        "installed" => Locale.Get("settings.components.statusInstalled"),
        "missing" => Locale.Get("settings.components.statusMissing"),
        "attention" => Locale.Get("settings.components.statusAttention"),
        "unsupported" => Locale.Get("settings.components.statusUnsupported"),
        _ => Locale.Get("settings.components.statusChecking"),
    };

    // Fills the toolkit list with bundled + user entries.
    private void FillToolkitList(StackPanel panel, ToolkitStore store, IReadOnlyList<ToolkitFileReader.ToolkitFileEntry> entries)
    {
        panel.Children.Clear();
        foreach (var entry in entries)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 2) };
            AutomationProperties.SetAutomationId(row, "toolkit-entry-" + entry.Id);

            var isBundled = entry.Source == ToolkitFileReader.ToolkitEntrySource.Bundled;
            var approval = isBundled ? null : store.GetApproval(entry);
            var badge = isBundled
                ? Locale.Get("settings.toolkit.bundledBadge")
                : (approval is not null ? Locale.Get("settings.toolkit.approvedBadge") : Locale.Get("settings.toolkit.needsApproval"));

            row.Children.Add(new TextBlock
            {
                Text = entry.DisplayName,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = badge,
                FontSize = 10,
                Opacity = .7,
                VerticalAlignment = VerticalAlignment.Center,
            });

            if (!isBundled)
            {
                if (approval is null)
                {
                    var entryId = entry.Id;
                    var approveBtn = new Button { Content = Locale.Get("settings.toolkit.approveButton"), Padding = new Thickness(8, 4, 8, 4) };
                    AutomationProperties.SetAutomationId(approveBtn, "toolkit-approve-" + entryId);
                    approveBtn.Click += async (_, _) => await Act(async () =>
                    {
                        store.Approve(entryId);
                        var (reloaded, _) = store.List();
                        FillToolkitList(toolkitListPanel!, store, reloaded);
                        await Task.CompletedTask;
                    });
                    row.Children.Add(approveBtn);
                }
                var removeEntryId = entry.Id;
                var removeBtn = new Button { Content = Locale.Get("settings.toolkit.removeButton"), Padding = new Thickness(8, 4, 8, 4) };
                AutomationProperties.SetAutomationId(removeBtn, "toolkit-remove-" + removeEntryId);
                removeBtn.Click += async (_, _) => await Act(async () =>
                {
                    store.Remove(removeEntryId);
                    var (reloaded, _) = store.List();
                    FillToolkitList(toolkitListPanel!, store, reloaded);
                    await Task.CompletedTask;
                });
                row.Children.Add(removeBtn);
            }
            panel.Children.Add(Toned(row));
        }
    }

    // Fills the result table after a run.
    private void FillToolkitResults(StackPanel panel, IReadOnlyList<ToolkitRunItem> results)
    {
        panel.Children.Clear();
        foreach (var item in results)
        {
            var label = item.RunVerdict switch
            {
                ToolkitRunItem.Verdict.Installed => Locale.Get("settings.toolkit.verdictInstalled"),
                ToolkitRunItem.Verdict.Failed => Locale.Get("settings.toolkit.verdictFailed"),
                _ => Locale.Get("settings.toolkit.verdictSkipped"),
            };
            panel.Children.Add(Toned(new TextBlock
            {
                Text = item.EntryId + " — " + label,
                FontSize = 11,
                Opacity = .8,
            }));
        }
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

    // The CLIs on this PC — provider list and refresh button; behaviour unchanged.
    private StackPanel BuildProvidersSection()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = Locale.Get("settings.providers.windowsDescription"),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(Button(Locale.Get("settings.providers.refreshButton"), RefreshRuntime));
        foreach (var item in runtime?.Providers ?? [])
            panel.Children.Add(new TextBlock
            {
                Text = ProviderCatalog.BetaLabel(item.Id, item.Name) + " · " + (item.Available ? item.Version : item.Detail),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            });
        return panel;
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
    internal StackPanel BuildCliAccountsSection(IReadOnlyList<CliAccountStatus> statuses)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = CliAccountStrings.SectionDescription,
            FontSize = 12,
            Opacity = .7,
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (var status in statuses)
        {
            var row = new StackPanel { Spacing = 4 };
            AutomationProperties.SetAutomationId(row, AccountRowIdPrefix + status.Provider);
            row.Children.Add(new TextBlock
            {
                Text = CliUpdateService.ProviderLabel(status.Provider),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            // Not installed shows the not-installed word; otherwise the summary, which already reads
            // "account · plan · method", the signed-out word, or the unknown sentence.
            row.Children.Add(new TextBlock
            {
                Text = status.Installed ? status.Summary : CliAccountStrings.StatusNotInstalled,
                FontSize = 12,
                Opacity = .8,
                TextWrapping = TextWrapping.Wrap,
            });

            if (status.Detail.Length > 0)
                row.Children.Add(new TextBlock { Text = status.Detail, FontSize = 11, Opacity = .7, TextWrapping = TextWrapping.Wrap });
            if (status.Installed)
            {
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                if (status.LoggedIn == true)
                {
                    buttons.Children.Add(SafeButton(CliAccountStrings.ButtonChange,
                        () => StartCliSignIn(status.Provider, CliLoginOption.Account)));
                    // Sign-out is offered only where the app can undo the sign-in.
                    if (status.CanSignOut)
                        buttons.Children.Add(SafeButton(CliAccountStrings.ButtonLogout,
                            () => ConfirmCliSignOut(status.Provider)));
                }
                else if (status.Provider == "claude")
                {
                    // Claude signs in two ways: the subscription or the API-billed console.
                    buttons.Children.Add(SafeButton(CliAccountStrings.ButtonLoginClaude,
                        () => StartCliSignIn(status.Provider, CliLoginOption.Account)));
                    buttons.Children.Add(SafeButton(CliAccountStrings.ButtonLoginConsole,
                        () => StartCliSignIn(status.Provider, CliLoginOption.Console)));
                }
                else
                {
                    buttons.Children.Add(SafeButton(CliAccountStrings.ButtonLogin,
                        () => StartCliSignIn(status.Provider, CliLoginOption.Account)));
                }
                if (buttons.Children.Count > 0) row.Children.Add(buttons);
                if (status.Provider == "claude")
                {
                    var settingsButtons = new StackPanel { Spacing = 6 };
                    settingsButtons.Children.Add(SafeButton(Locale.Get("settings.cliAccounts.bedrockButton"), () => StartCliSignIn("claude", CliLoginOption.Bedrock)));
                    settingsButtons.Children.Add(SafeButton(Locale.Get("settings.cliAccounts.resetModelsButton"), ResetClaudeModels));
                    settingsButtons.Children.Add(SafeButton(Locale.Get("settings.cliAccounts.resetBedrockButton"), ResetClaudeBedrock));
                    row.Children.Add(settingsButtons);
                }
            }
            panel.Children.Add(Toned(row));
        }
        return panel;
    }

    // Opens the external sign-in terminal and refreshes the status when it closes.
    // The app never types or receives credentials; it only starts the CLI's own
    // login command. The terminal rule lives in Core (CliAccountTerminal).
    private async Task StartCliSignIn(string provider, CliLoginOption option)
    {
        RequireIdleAccount(provider);
        if (CliAccountSupport.LoginArguments(provider, option) is not { } argv) return;
        accountChanges.TryAdd(provider, 0);
        try { await accountsCoordinator.StartSignInAsync(argv); await RefreshCliAccounts(); }
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

    // About — usage notes; behaviour unchanged.
    private StackPanel BuildAppInfoSection()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = Locale.Get("settings.appInfo.windowsNote"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = .7,
            FontSize = 12,
        });
        return panel;
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
        // The rows sit inside the nested results panel, so look one level down as well.
        var renderedStatuses = cliPanel.Children.OfType<StackPanel>()
            .SelectMany(child => child.Children.OfType<StackPanel>().Prepend(child))
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
        Require(cliPanel.Children.OfType<ToggleSwitch>().Any(), "the CLI update section must show the auto-update switch");
        return outcome;
    }
}
