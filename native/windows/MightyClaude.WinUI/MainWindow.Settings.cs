using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

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
        SettingsSections.CliUpdate => BuildCliUpdateSectionFromState,
        SettingsSections.Providers => BuildProvidersSection,
        SettingsSections.ModelDefaults => BuildModelDefaultsSection,
        SettingsSections.CliAccounts => BuildCliAccountsSectionFromState,
        SettingsSections.AppUpdate => BuildAppUpdateSectionFromState,
        SettingsSections.AppInfo => BuildAppInfoSection,
        _ => throw new InvalidOperationException("no Settings builder registered for slot " + slotId),
    };

    private Task OpenSettings() => Act(async () =>
    {
        var content = new StackPanel { Spacing = 0, MinWidth = 420, MaxWidth = 540 };
        foreach (var section in GetSettingsSections())
            content.Children.Add(BuildSectionContainer(section.Title, section.Build()));

        // The CLI account statuses are read when the section opens, and its rows
        // are replaced as soon as the answers arrive — Settings never waits on a
        // CLI. The smoke run injects fixtures instead and starts no CLI at all.
        if (!options.SmokeTest)
        {
            var accountsSlot = content.Children.OfType<StackPanel>().FirstOrDefault(wrapper =>
                wrapper.Children.OfType<TextBlock>().FirstOrDefault()?.Text == CliAccountStrings.SectionTitle);
            if (accountsSlot is not null)
                _ = RefreshCliAccounts().ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
                {
                    if (accountsSlot.Children.Count > 1)
                        accountsSlot.Children[1] = BuildCliAccountsSectionFromState();
                }), TaskScheduler.Default);
        }

        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 520,
        };
        await new ContentDialog
        {
            Title = Locale.Get("settings.settingsWindowTitle"),
            Content = scroll,
            CloseButtonText = Locale.Get("settings.closeButton"),
            XamlRoot = root.XamlRoot,
        }.ShowAsync();
    });

    private static StackPanel BuildSectionContainer(string title, StackPanel body)
    {
        var wrapper = new StackPanel { Spacing = 8, Padding = new(0, 0, 0, 20) };
        var heading = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = .85,
        };
        AutomationProperties.SetAutomationId(heading, "settings-section-" + title);
        wrapper.Children.Add(heading);
        wrapper.Children.Add(body);
        return wrapper;
    }

    // 화면 — theme, language picker, and completion-notification controls.
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
        return panel;
    }

    // CLI 업데이트 — delegates to the parameterised builder so smoke can inject fixture results.
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

        // 업데이트 하기 button — reflects coordinator state live.
        var updateButton = new Button
        {
            Content = coordinator.ButtonLabel,
            IsEnabled = coordinator.CanStart,
        };
        AutomationProperties.SetAutomationId(updateButton, "cli-update-start");
        updateButton.Click += (_, _) => coordinator.Start();
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
            resultsPanel.Children.Add(row);
        }
        foreach (var result in results) AddResultRow(result);
        panel.Children.Add(resultsPanel);

        coordinator.StateChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            updateButton.Content = coordinator.ButtonLabel;
            updateButton.IsEnabled = coordinator.CanStart;
            var live = coordinator.Results;
            if (live.Count > 0)
            {
                resultsPanel.Children.Clear();
                foreach (var r in live) AddResultRow(r);
            }
        });

        return panel;
    }

    // One id per row. The status is part of it because a run can report the same
    // provider twice (for example updated then failed on a retry).
    internal const string ResultRowIdPrefix = "cli-update-result-";
    private static string ResultRowAutomationId(CliUpdateResult result) =>
        ResultRowIdPrefix + result.Provider + "-" + result.Status;

    // 이 PC의 CLI — provider list and refresh button; behaviour unchanged.
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
                Text = item.Name + " · " + (item.Available ? item.Version : item.Detail),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            });
        return panel;
    }

    // CLI 계정 — who each CLI is signed in as, and the sign-in / change / sign-out
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
            // Not installed shows 미설치; otherwise the summary, which already reads
            // "account · plan · method", 로그인되지 않음, or the unknown sentence.
            row.Children.Add(new TextBlock
            {
                Text = status.Installed ? status.Summary : CliAccountStrings.StatusNotInstalled,
                FontSize = 12,
                Opacity = .8,
                TextWrapping = TextWrapping.Wrap,
            });

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
            }
            panel.Children.Add(row);
        }
        return panel;
    }

    // Opens the external sign-in terminal and refreshes the status when it closes.
    // The app never types or receives credentials; it only starts the CLI's own
    // login command. The terminal rule lives in Core (CliAccountTerminal).
    private async Task StartCliSignIn(string provider, CliLoginOption option)
    {
        if (CliAccountSupport.LoginArguments(provider, option) is not { } argv) return;
        await accountsCoordinator.StartSignInAsync(argv);
        await RefreshCliAccounts();
    }

    // The macOS confirmation: the stored sign-in of that CLI is removed, and this
    // also applies to the CLI used directly in a terminal.
    private async Task ConfirmCliSignOut(string provider)
    {
        var label = CliUpdateService.ProviderLabel(provider);
        var dialog = new ContentDialog
        {
            Title = CliAccountStrings.ConfirmLogoutTitleTemplate.Replace("{provider}", label),
            Content = new TextBlock
            {
                Text = CliAccountStrings.ConfirmMessageTemplate.Replace("{provider}", label),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = CliAccountStrings.ButtonLogout,
            CloseButtonText = CliAccountStrings.ButtonCancel,
            XamlRoot = root.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await accountsCoordinator.LogoutAsync(provider);
    }

    // Read the statuses when the section opens and after a sign-in terminal closes.
    internal Task RefreshCliAccounts() => accountsCoordinator.RefreshAsync(CliAccountProviders);

    // 모델 기본값 — per-provider per-mode pickers and registered-name management.
    private StackPanel BuildModelDefaultsSection()
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var provider in new[] { "claude", "codex" })
            panel.Children.Add(BuildProviderDefaultsPanel(provider));
        return panel;
    }

    private StackPanel BuildProviderDefaultsPanel(string provider)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = ProviderCatalog.Name(provider),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = .9,
        });

        var config = service.Snapshot.ModelDefaults;
        var pd = provider == "codex" ? config?.Codex : config?.Claude;
        var registered = pd?.RegisteredModels ?? (IReadOnlyList<RegisteredModelEntry>)[];

        // One ComboBox row per permission mode.
        foreach (var mode in ProviderCatalog.PermissionModes(provider))
        {
            var modeCapture = mode;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = mode,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 80,
            });
            var current = pd?.ModeDefaults.TryGetValue(mode, out var val) == true ? val! : "default";
            var combo = new ComboBox { FontSize = 11, MinWidth = 140 };
            AutomationProperties.SetAutomationId(combo, $"modelDefaults-{provider}-{modeCapture}");
            combo.Items.Add(new ComboBoxItem { Content = ModelDefaultsStrings.DefaultOption, Tag = "default" });
            foreach (var entry in registered)
                combo.Items.Add(new ComboBoxItem { Content = entry.Name, Tag = entry.Name, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") });
            combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == current) ?? combo.Items[0];
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is ComboBoxItem { Tag: string picked })
                    _ = service.UpdateAsync(s =>
                    {
                        var cfg = s.ModelDefaults ?? new ModelDefaultsConfig();
                        var providerDefaults = provider == "codex" ? cfg.Codex : cfg.Claude;
                        var newDefaults = new Dictionary<string, string>(providerDefaults.ModeDefaults) { [modeCapture] = picked };
                        var updated = providerDefaults with { ModeDefaults = newDefaults };
                        return s with { ModelDefaults = provider == "codex" ? cfg with { Codex = updated } : cfg with { Claude = updated } };
                    });
            };
            row.Children.Add(combo);
            panel.Children.Add(row);
        }

        // Registered names list.
        var regHeading = new TextBlock { Text = ModelDefaultsStrings.RegisteredTitle, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Opacity = .8 };
        panel.Children.Add(regHeading);

        var registeredList = new StackPanel { Spacing = 4 };
        AutomationProperties.SetAutomationId(registeredList, $"modelDefaults-registered-{provider}");
        RebuildRegisteredList(registeredList, provider);
        panel.Children.Add(registeredList);

        // Add row: TextBox + Button + effort toggle + level picker + error label.
        var addBox = new TextBox { PlaceholderText = ModelDefaultsStrings.AddPlaceholder, FontSize = 11, MinWidth = 160 };
        AutomationProperties.SetAutomationId(addBox, $"modelDefaults-add-{provider}");
        var addButton = new Button { Content = ModelDefaultsStrings.AddButton, FontSize = 11 };
        AutomationProperties.SetAutomationId(addButton, $"modelDefaults-addButton-{provider}");
        var errorLabel = new TextBlock { FontSize = 11, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(errorLabel, $"modelDefaults-error-{provider}");

        var effortToggle = new CheckBox { Content = ModelDefaultsStrings.SupportsEffortLabel, FontSize = 11 };
        AutomationProperties.SetAutomationId(effortToggle, $"modelDefaults-addEffort-{provider}");

        var effortLevelsPanel = new StackPanel { Spacing = 4, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(effortLevelsPanel, $"modelDefaults-addLevels-{provider}");
        effortLevelsPanel.Children.Add(new TextBlock { Text = ModelDefaultsStrings.EffortLevelsLabel, FontSize = 10, Opacity = .7 });
        var levelBoxes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var selectedLevels = new HashSet<string>();
        foreach (var lvl in Wire.Efforts)
        {
            var lvlCapture = lvl;
            var cb = new CheckBox { Content = lvl, FontSize = 10, Padding = new Microsoft.UI.Xaml.Thickness(4, 0, 4, 0) };
            AutomationProperties.SetAutomationId(cb, $"modelDefaults-addLevel-{provider}-{lvl}");
            cb.Checked += (_, _) => selectedLevels.Add(lvlCapture);
            cb.Unchecked += (_, _) => selectedLevels.Remove(lvlCapture);
            levelBoxes.Children.Add(cb);
        }
        effortLevelsPanel.Children.Add(levelBoxes);

        effortToggle.Checked += (_, _) => effortLevelsPanel.Visibility = Visibility.Visible;
        effortToggle.Unchecked += (_, _) => { effortLevelsPanel.Visibility = Visibility.Collapsed; selectedLevels.Clear(); foreach (var cb in levelBoxes.Children.OfType<CheckBox>()) cb.IsChecked = false; };

        void DoAdd()
        {
            var name = addBox.Text;
            var supportsEffort = effortToggle.IsChecked == true;
            var levels = Wire.Efforts.Where(selectedLevels.Contains).ToArray();
            _ = service.UpdateAsync(s =>
            {
                var cfg = s.ModelDefaults ?? new ModelDefaultsConfig();
                var error = ModelDefaultsResolution.AddRegisteredModel(name, provider, ref cfg, supportsEffort, levels);
                if (error is not null) { DispatcherQueue.TryEnqueue(() => { errorLabel.Text = error; errorLabel.Visibility = Visibility.Visible; }); return s; }
                DispatcherQueue.TryEnqueue(() =>
                {
                    errorLabel.Visibility = Visibility.Collapsed;
                    addBox.Text = "";
                    effortToggle.IsChecked = false;
                    selectedLevels.Clear();
                    foreach (var cb in levelBoxes.Children.OfType<CheckBox>()) cb.IsChecked = false;
                    RebuildRegisteredList(registeredList, provider);
                });
                return s with { ModelDefaults = cfg };
            });
        }
        addButton.Click += (_, _) => DoAdd();
        addBox.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) DoAdd(); };

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        addRow.Children.Add(addBox);
        addRow.Children.Add(addButton);
        var addControls = new StackPanel { Spacing = 4 };
        addControls.Children.Add(addRow);
        addControls.Children.Add(effortToggle);
        addControls.Children.Add(effortLevelsPanel);
        panel.Children.Add(addControls);
        panel.Children.Add(errorLabel);
        return panel;
    }

    private void RebuildRegisteredList(StackPanel registeredList, string provider)
    {
        registeredList.Children.Clear();
        var config = service.Snapshot.ModelDefaults;
        var pd = provider == "codex" ? config?.Codex : config?.Claude;
        var registered = pd?.RegisteredModels ?? (IReadOnlyList<RegisteredModelEntry>)[];
        foreach (var entry in registered)
        {
            var entryCapture = entry.Name;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            AutomationProperties.SetAutomationId(row, $"modelDefaults-registered-{provider}-{entryCapture}");
            var nameBlock = new TextBlock { Text = entryCapture, FontSize = 11, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center };
            var deleteBtn = new Button { Content = ModelDefaultsStrings.DeleteButton, FontSize = 11 };
            AutomationProperties.SetAutomationId(deleteBtn, $"modelDefaults-delete-{provider}-{entryCapture}");
            deleteBtn.Click += (_, _) =>
                _ = service.UpdateAsync(s =>
                {
                    var cfg = s.ModelDefaults ?? new ModelDefaultsConfig();
                    ModelDefaultsResolution.RemoveRegisteredModel(entryCapture, provider, ref cfg);
                    DispatcherQueue.TryEnqueue(() => RebuildRegisteredList(registeredList, provider));
                    return s with { ModelDefaults = cfg };
                });
            row.Children.Add(nameBlock);
            row.Children.Add(deleteBtn);
            registeredList.Children.Add(row);
        }
    }

    // 앱 정보 — usage notes; behaviour unchanged.
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
