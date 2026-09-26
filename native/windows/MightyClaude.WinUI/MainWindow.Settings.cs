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
        SettingsSections.PhaseModels => BuildPhaseModelsSection,
        SettingsSections.Components => BuildComponentsSection,
        SettingsSections.CliUpdate => BuildCliUpdateSectionFromState,
        SettingsSections.Providers => BuildProvidersSection,
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

        // Browser engine toggle — opt-in, off by default; restart required to apply.
        var browserToggle = new ToggleSwitch
        {
            Header = Locale.Get("settings.display.browserToggle"),
            IsOn = service.Snapshot.BrowserEngineEnabled,
        };
        AutomationProperties.SetName(browserToggle, Locale.Get("settings.display.browserToggle"));
        panel.Children.Add(new TextBlock { Text = Locale.Get("settings.display.browserDescription"), TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = .65 });
        browserToggle.Toggled += async (_, _) =>
            await Act(async () => await service.UpdateAsync(s => s with { BrowserEngineEnabled = browserToggle.IsOn }));
        panel.Children.Add(browserToggle);

        return panel;
    }

    // 페이즈별 모델 — 페이즈 묶음 줄 넷 위에 Claude·Codex·omc·Ouroboros의 자세히 줄.
    //
    // 무엇을 그릴지, 한 줄이 어떤 손잡이를 건드리는지, 어떤 묶음이 아예 빠지는지는
    // 전부 Core의 PhaseModelSection이 정한다. 여기서는 그 줄을 그리고 고른 값을
    // 되돌려 줄 뿐이다 — macOS 화면과 같은 열쇠말, 같은 규칙.
    //
    // Claude·Codex 값은 저장 상태(AppSnapshot.PhaseModels)에, omc·Ouroboros 값은
    // 각자의 설정 파일에 들어간다. 파일을 읽거나 쓸 수 없으면 붉은 글로 알리고
    // 파일은 건드리지 않는다.
    internal PhaseModelTools phaseModelTools = new(null, null);

    // 지금 열린 설정 화면의 페이즈별 모델 칸. 값을 고른 뒤 이 칸만 다시 채운다.
    private StackPanel? phaseModelsPanel;

    private StackPanel BuildPhaseModelsSection()
    {
        // 스모크는 실제 사용자 파일을 건드리지 않는다 — 붙박이 값으로 네 묶음을 다 그린다.
        phaseModelTools = options.SmokeTest ? PhaseModelSection.SmokeFixtureTools : PhaseModelSection.LoadTools();
        phaseModelsPanel = BuildPhaseModelsSection(service.Snapshot.PhaseModels ?? new(), phaseModelTools);
        return phaseModelsPanel;
    }

    // 검사와 스모크가 부르는 자리: 도구 상태를 넘겨 실제 화면을 그대로 짓는다.
    internal StackPanel BuildPhaseModelsSection(PhaseModelsSnapshot config, PhaseModelTools tools)
    {
        var panel = new StackPanel { Spacing = 8 };
        FillPhaseModelsSection(panel, config, tools);
        return panel;
    }

    private void FillPhaseModelsSection(StackPanel panel, PhaseModelsSnapshot config, PhaseModelTools tools)
    {
        panel.Children.Clear();
        panel.Children.Add(new TextBlock
        {
            Text = PhaseModelSection.Description,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = .8,
        });

        // 페이즈 줄은 Claude·Codex 손잡이를 함께 건드리므로 두 실행기의 이름을 모두 보여 준다.
        IReadOnlyList<string> summaryValues =
        [
            .. PhaseModelValues(PhaseModelSection.ClaudeTool)
                .Concat(PhaseModelValues(PhaseModelSection.CodexTool))
                .Distinct(StringComparer.Ordinal),
        ];
        foreach (var row in PhaseModelSection.SummaryRows(config, tools))
        {
            var phase = row.Phase;
            panel.Children.Add(PhaseModelPicker(
                row.Label,
                summaryValues,
                row.Value,
                row.Mixed,
                PhaseRowIdPrefix + phase,
                value => ApplyPhaseModelRow(phase, value)));
        }

        foreach (var block in PhaseModelSection.ToolBlocks(config, tools))
        {
            panel.Children.Add(new TextBlock
            {
                Text = block.Label,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = .85,
            });
            foreach (var knob in block.Knobs)
            {
                var id = knob.KnobId;
                panel.Children.Add(PhaseModelPicker(
                    knob.Label,
                    knob.IsEffort ? EffortValues : PhaseModelValues(block.Tool),
                    knob.Value,
                    false,
                    KnobRowIdPrefix + id,
                    value => ApplyPhaseModelKnob(id, value)));
            }
        }

        if (tools.Error is { } error)
            panel.Children.Add(PhaseModelErrorText(error));
    }

    internal const string PhaseRowIdPrefix = "phase-models-row-";
    internal const string KnobRowIdPrefix = "phase-models-knob-";
    private static readonly string[] EffortValues = ["default", "low", "medium", "high"];

    private static TextBlock PhaseModelErrorText(string error)
    {
        var text = new TextBlock
        {
            Text = error,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
        };
        AutomationProperties.SetAutomationId(text, "phase-models-error");
        return text;
    }

    // 고를 수 있는 이름: 그 실행기의 모델 목록에 등록한 이름을 더한 것.
    private IReadOnlyList<string> PhaseModelValues(string tool)
    {
        var provider = tool == PhaseModelSection.CodexTool ? "codex" : "claude";
        var catalog = runtime?.Providers?.FirstOrDefault(item => item.Id == provider)?.ModelCatalog
            ?? ProviderCatalog.Fallback(provider);
        var defaults = service.Snapshot.ModelDefaults;
        var registered = provider == "codex" ? defaults?.Codex.RegisteredModels : defaults?.Claude.RegisteredModels;
        return [.. catalog.Models.Select(model => model.Value)
            .Concat(registered?.Select(entry => entry.Name) ?? [])
            .Distinct(StringComparer.Ordinal)];
    }

    private static ComboBox PhaseModelPicker(
        string label,
        IReadOnlyList<string> values,
        string current,
        bool mixed,
        string automationId,
        Func<string, Task> onPick)
    {
        var box = new ComboBox { Header = label, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(box, automationId);
        box.Items.Add(new ComboBoxItem { Content = PhaseModelSection.DefaultOption, Tag = "default" });
        if (mixed)
            box.Items.Add(new ComboBoxItem { Content = PhaseModelSection.MixedLabel, Tag = PhaseModelSection.MixedSentinel });
        foreach (var value in values)
            if (value != "default")
                box.Items.Add(new ComboBoxItem { Content = value, Tag = value });
        // 목록에 없는 이름이 이미 들어 있으면 그 이름도 보여 준다 — 고른 값을 잃지 않는다.
        if (!mixed && current != "default" && !values.Contains(current))
            box.Items.Add(new ComboBoxItem { Content = current, Tag = current });

        var wanted = mixed ? PhaseModelSection.MixedSentinel : current;
        box.SelectedIndex = Math.Max(0, box.Items
            .OfType<ComboBoxItem>()
            .ToList()
            .FindIndex(item => (string)item.Tag == wanted));

        box.SelectionChanged += async (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem item && (string)item.Tag is var tag &&
                tag != PhaseModelSection.MixedSentinel)
                await onPick(tag);
        };
        return box;
    }

    // 페이즈 줄 하나를 네 도구에 모두 적용하고, 바깥 두 도구는 파일에 쓴다.
    internal Task ApplyPhaseModelRow(Phase phase, string value) => Act(async () =>
    {
        var edit = PhaseModelSection.ApplyPhaseRow(phase, value, service.Snapshot.PhaseModels ?? new(), phaseModelTools);
        await SavePhaseModelEdit(edit);
    });

    // 자세히 줄 하나만 바꾼다.
    internal Task ApplyPhaseModelKnob(string knobId, string value) => Act(async () =>
    {
        var edit = PhaseModelSection.ApplyKnob(knobId, value, service.Snapshot.PhaseModels ?? new(), phaseModelTools);
        await SavePhaseModelEdit(edit);
    });

    // Claude·Codex 값은 저장 상태에, 바뀐 omc·Ouroboros 값만 각자의 파일에 쓴다.
    // 스모크에서는 파일을 쓰지 않는다. 쓴 뒤 칸을 다시 채워 혼합 표시와 오류 글을 맞춘다.
    private async Task SavePhaseModelEdit(PhaseModelEdit edit)
    {
        phaseModelTools = options.SmokeTest
            ? phaseModelTools with { OmcAgents = edit.OmcAgents, OuroborosKeys = edit.OuroborosKeys }
            : PhaseModelSection.SaveTools(phaseModelTools, edit);
        await service.UpdateAsync(snapshot => snapshot with { PhaseModels = edit.Config });
        if (phaseModelsPanel is { } panel)
        {
            var config = edit.Config;
            var tools = phaseModelTools;
            DispatcherQueue.TryEnqueue(() => FillPhaseModelsSection(panel, config, tools));
        }
    }

    // 스모크의 로케일 열쇠말 검사가 읽는 글: 머리글, 제목, 그리고 펼치지 않은
    // 목록 항목까지. ComboBox의 머리글과 항목은 화면 나무에 바로 보이지 않는다.
    internal static List<string> PhaseModelSectionTexts(StackPanel panel)
    {
        var texts = new List<string>();
        foreach (var child in panel.Children)
        {
            if (child is TextBlock text && !string.IsNullOrEmpty(text.Text)) texts.Add(text.Text);
            if (child is ComboBox box)
            {
                if (box.Header is string header && header.Length > 0) texts.Add(header);
                foreach (var item in box.Items.OfType<ComboBoxItem>())
                    if (item.Content is string content && content.Length > 0) texts.Add(content);
            }
        }
        return texts;
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

    // 구성 요소 — CLI rows (claude/codex/gemini) then the toolkit sub-section.
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
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange),
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
            panel.Children.Add(rowPanel);
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
            panel.Children.Add(row);
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
            panel.Children.Add(new TextBlock
            {
                Text = item.EntryId + " — " + label,
                FontSize = 11,
                Opacity = .8,
            });
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
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = .85,
                });

        var confirm = new ContentDialog
        {
            Title = Locale.Get("settings.toolkit.confirmTitle"),
            Content = new ScrollViewer { Content = argsList, MaxHeight = 240 },
            PrimaryButtonText = Locale.Get("settings.toolkit.confirmInstall"),
            CloseButtonText = Locale.Get("settings.toolkit.cancelButton"),
            XamlRoot = root.XamlRoot,
        };
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
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var json = await Windows.Storage.FileIO.ReadTextAsync(file);
        var parsed = ToolkitFileReader.Parse(json);
        if (parsed.Entries.Count == 0)
        {
            await new ContentDialog
            {
                Title = "toolkit",
                Content = new TextBlock { Text = Locale.Get("settings.toolkit.errorEntryFileTemplate").Replace("{name}", file.Name), TextWrapping = TextWrapping.Wrap },
                CloseButtonText = Locale.Get("settings.toolkit.cancelButton"),
                XamlRoot = root.XamlRoot,
            }.ShowAsync();
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
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
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
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
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
