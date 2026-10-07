using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.System;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    internal PhaseModelTools phaseModelTools = new(null, null);
    private StackPanel? phaseModelsPanel;
    private readonly Dictionary<string, PhaseRegistrationDraft> phaseRegistrationDrafts = [];
    private sealed class PhaseRegistrationDraft
    {
        internal string Name = "";
        internal bool SupportsEffort;
        internal HashSet<string> Levels = [];
        internal string? Error;
    }

    private StackPanel BuildPhaseModelsSection()
    {
        phaseModelTools = options.SmokeTest ? PhaseModelSection.SmokeFixtureTools : PhaseModelSection.LoadTools();
        return phaseModelsPanel = BuildPhaseModelsSection(service.Snapshot.PhaseModels ?? new(), phaseModelTools);
    }

    internal StackPanel BuildPhaseModelsSection(PhaseModelsSnapshot config, PhaseModelTools tools)
    {
        var panel = new StackPanel();
        FillPhaseModelsSection(panel, config, tools);
        return panel;
    }

    private ModelCatalog PhaseCatalog(string provider) => Runtime(provider)?.ModelCatalog ?? ProviderCatalog.Fallback(provider);
    private List<RegisteredModelEntry> PhaseRegistered(string provider) => provider == "codex" ? service.Snapshot.ModelDefaults?.Codex.RegisteredModels ?? [] : service.Snapshot.ModelDefaults?.Claude.RegisteredModels ?? [];

    /// <summary>The widths of a phase row's two trailing columns: the model pop-up and the effort pop-up (M/PhaseModelSettingsView.swift:139, 149).</summary>
    internal const double PhaseModelColumn = 220, PhaseEffortColumn = 110;

    /// <summary>A rule between the parts of a provider's block (the Mac's <c>Divider()</c> in the block's stack).</summary>
    private Border PhaseRule() => new() { Height = DesignMetrics.Stroke.Line, Background = brushes.Brush(DesignToken.Line) };

    // M/PhaseModelSettingsView.swift:9-67: the two explanations, then a block per provider — its name
    // (12 medium) and 베타 capsule, the phase rows (label | model pop-up | effort pop-up, in columns),
    // a rule, the alias rows (label | pop-up), a rule, the registered names and the row that adds one.
    private void FillPhaseModelsSection(StackPanel panel, PhaseModelsSnapshot config, PhaseModelTools tools)
    {
        panel.Children.Clear();
        SettingsRow(panel, SettingsText(PhaseModelSection.Description, 11, DesignToken.Ink2));
        SettingsRow(panel, SettingsText(Locale.Get("settings.phaseModels.effortNote"), 11, DesignToken.Ink2));
        foreach (var provider in new[] { "claude", "codex" })
        {
            var block = new StackPanel { Spacing = DesignMetrics.Spacing.Sm, Margin = new Thickness(0, DesignMetrics.Spacing.Xs, 0, DesignMetrics.Spacing.Xs) };
            AutomationProperties.SetAutomationId(block, "phaseModels-provider-" + provider);
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
            header.Children.Add(SettingsText(ProviderMark.Label(provider), 12, medium: true));
            if (ProviderCatalog.IsBeta(provider)) header.Children.Add(BetaBadgeView.Create(brushes));
            block.Children.Add(header);
            foreach (var phase in PhaseModelSection.Phases.Where(p => provider != "claude" || p != Phase.Review))
            {
                var row = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm, MinHeight = SettingsControlHeight };
                row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new() { Width = new GridLength(PhaseModelColumn) });
                row.ColumnDefinitions.Add(new() { Width = new GridLength(PhaseEffortColumn) });
                var state = provider == "claude" ? PhaseModelRouting.ClaudeRowState(phase, config) : PhaseModelRouting.CodexRowState(phase, config);
                var title = PhaseModelSection.PhaseLabel(phase);
                var phaseId = phase.ToString().ToLowerInvariant();
                row.Children.Add(SettingsText(title, 11));
                // A phase with no knob for one of the two says so instead of offering a choice the CLI would ignore.
                FrameworkElement modelCell;
                if (state is not null) modelCell = SettingsPopupFrame(PhaseModelPicker(provider, title, state.Value ?? PhaseModelSection.MixedSentinel, "phaseModels-model-" + provider + "-" + phaseId, value => ApplyProviderPhaseRow(provider, phase, value)));
                else { modelCell = SettingsText(Locale.Get("settings.phaseModels.paneModel"), 11, DesignToken.Ink2); modelCell.HorizontalAlignment = HorizontalAlignment.Right; }
                Grid.SetColumn(modelCell, 1); row.Children.Add(modelCell);
                var effort = (provider, phase) switch { ("claude", Phase.Execution) => config.ClaudeMainEffort ?? "default", ("codex", Phase.Planning) => config.CodexPlanModeReasoningEffort, ("codex", Phase.Execution) => config.CodexMainEffort ?? "default", ("codex", Phase.Subagents) => config.CodexSubagentEffort ?? "default", _ => null };
                FrameworkElement effortCell;
                // The word for a phase with no effort setting is the tertiary ink (M/PhaseModelSettingsView.swift:152).
                if (effort is null) effortCell = SettingsTertiary(Locale.Get("settings.phaseModels.effortUnsupported"));
                else
                {
                    var effortPicker = PhasePicker(title, PhaseModelPreferences.Efforts(provider, PhaseCatalog(provider), effort).Select(v => new PhaseModelVersion(v, v)), effort, "phaseModels-effort-" + provider + "-" + phaseId, value => ApplyPhaseEffort(provider, phase, value));
                    AutomationProperties.SetName(effortPicker, title + " · " + Locale.Get("composer.label.effort"));
                    effortCell = SettingsPopupFrame(effortPicker);
                }
                Grid.SetColumn(effortCell, 2); row.Children.Add(effortCell); block.Children.Add(row);
            }
            block.Children.Add(PhaseRule());
            foreach (var knob in PhaseModelSection.ToolBlocks(config, tools).Single(b => b.Tool == provider).Knobs.Where(k => !k.IsEffort))
            {
                var row = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm, MinHeight = SettingsControlHeight };
                row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(PhaseModelColumn) });
                row.Children.Add(SettingsText(knob.Label, 11, DesignToken.Ink2));
                var picker = SettingsPopupFrame(PhaseModelPicker(provider, knob.Label, knob.Value, "phase-models-knob-" + knob.KnobId, value => ApplyPhaseModelKnob(knob.KnobId, value)));
                Grid.SetColumn(picker, 1); row.Children.Add(picker); block.Children.Add(row);
            }
            block.Children.Add(PhaseRule());
            AddRegisteredModels(block, provider);
            // The Mac's form draws the rule between the two blocks as an empty row of its own (M/PhaseModelSettingsView.swift:18).
            if (panel.Children.Count > 2) SettingsRow(panel, new Border());
            SettingsRow(panel, block);
        }
        if (tools.Error is { } error) SettingsRow(panel, PhaseModelErrorText(error));
    }

    private ComboBox PhaseModelPicker(string provider, string label, string current, string id, Func<string, Task> action) =>
        PhasePicker(label, PhaseModelPreferences.Versions(PhaseCatalog(provider), PhaseRegistered(provider), current), current, id, action);

    // A pop-up as wide as its chosen title, centred in its column as the Mac's (M/PhaseModelSettingsView.swift:135-150).
    private ComboBox PhasePicker(string label, IEnumerable<PhaseModelVersion> values, string current, string id, Func<string, Task> action)
    {
        var picker = SettingsPopup(new ComboBox());
        // The Mac's pop-up title and chevrons stand 4.5 right of the column's centre; a stock ComboBox's words stand 3.5 left of its own.
        picker.Margin = new Thickness(DesignMetrics.Spacing.Lg, 0, 0, 0);
        AutomationProperties.SetAutomationId(picker, id);
        AutomationProperties.SetName(picker, label);
        picker.Items.Add(new ComboBoxItem { Content = PhaseModelSection.DefaultOption, Tag = "default" });
        if (current == PhaseModelSection.MixedSentinel) picker.Items.Add(new ComboBoxItem { Content = PhaseModelSection.MixedLabel, Tag = current });
        foreach (var option in values.Where(v => v.Value is not ("default" or PhaseModelSection.MixedSentinel)).DistinctBy(v => v.Value))
            picker.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Value });
        picker.SelectedIndex = Math.Max(0, picker.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => Equals(i.Tag, current)));
        picker.SelectionChanged += async (_, _) => { if (picker.SelectedItem is ComboBoxItem { Tag: string value } && value != PhaseModelSection.MixedSentinel) await action(value); };
        return picker;
    }

    // M/PhaseModelSettingsView.swift:54-64, 233-285: the title, a row per registered name with its small
    // destructive delete button, then the name field beside its label and the add button, the
    // supports-effort switch, and the level chips that show while it is on.
    private void AddRegisteredModels(StackPanel block, string provider)
    {
        block.Children.Add(SettingsText(Locale.Get("settings.phaseModels.registeredTitle"), 11, medium: true));
        foreach (var entry in PhaseRegistered(provider))
        {
            var row = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            AutomationProperties.SetAutomationId(row, "phaseModels-registered-" + provider + "-" + entry.Name);
            var words = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs, VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(SettingsText(entry.Name, 11, mono: true));
            if (entry.SupportsEffort) words.Children.Add(SettingsText(Locale.Get("settings.phaseModels.supportsEffortLabel"), 10, DesignToken.Ink2));
            row.Children.Add(words);
            var remove = SettingsPush(new Button { Content = Locale.Get("settings.phaseModels.deleteButton") }, SettingsControlSize.Small, destructive: true);
            remove.Click += async (_, _) => await UpdatePhaseRegistered(provider, entries => entries.Where(e => e.Name != entry.Name).ToList());
            Grid.SetColumn(remove, 1); row.Children.Add(remove); block.Children.Add(row);
        }
        if (!phaseRegistrationDrafts.TryGetValue(provider, out var draft)) phaseRegistrationDrafts[provider] = draft = new();
        // The field stands 3 closer to the title than the block's 6 (screens/10-settings-models-*.webp).
        var addPanel = new StackPanel { Spacing = DesignMetrics.Spacing.Xs, Margin = new Thickness(0, -DesignMetrics.Spacing.Xxs, 0, 0) };
        // In the Mac's form a text field's title is its leading label; the field takes the trailing half.
        var fieldLabel = Locale.Get("settings.phaseModels.addPlaceholder");
        var addRow = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
        addRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); addRow.ColumnDefinitions.Add(new() { Width = new GridLength(PhaseNameField) }); addRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        addRow.Children.Add(SettingsText(fieldLabel));
        var input = SettingsField(new TextBox { Text = draft.Name });
        AutomationProperties.SetName(input, fieldLabel);
        var composing = false;
        input.TextCompositionStarted += (_, _) => composing = true;
        input.TextCompositionEnded += (_, _) => composing = false;
        input.LostFocus += (_, _) => composing = false;
        AutomationProperties.SetAutomationId(input, "phaseModels-add-" + provider);
        // WinUI can deliver TextChanged after a following click. Keep the
        // redraw draft synchronized with the value, not the queued text event.
        input.RegisterPropertyChangedCallback(TextBox.TextProperty, (_, _) => draft.Name = input.Text);
        var add = SettingsPush(new Button { Content = Locale.Get("settings.phaseModels.addButton") }, SettingsControlSize.Small);
        AutomationProperties.SetAutomationId(add, "phaseModels-addButton-" + provider);
        var supportsLabel = Locale.Get("settings.phaseModels.supportsEffortLabel");
        var supports = SettingsSwitch(supportsLabel, draft.SupportsEffort, "phaseModels-addEffort-" + provider);
        var levels = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs, Visibility = draft.SupportsEffort ? Visibility.Visible : Visibility.Collapsed };
        levels.Children.Add(SettingsText(Locale.Get("settings.phaseModels.effortLevelsLabel"), 10, DesignToken.Ink2));
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xs };
        foreach (var level in Wire.Efforts)
        {
            var chip = PhaseLevelChip(level, draft.Levels.Contains(level));
            AutomationProperties.SetAutomationId(chip, "phaseModels-addLevel-" + provider + "-" + level);
            chip.RegisterPropertyChangedCallback(ToggleButton.IsCheckedProperty, (_, _) => { if (chip.IsChecked == true) draft.Levels.Add(level); else draft.Levels.Remove(level); }); chips.Children.Add(chip);
        }
        levels.Children.Add(chips);
        supports.RegisterPropertyChangedCallback(ToggleButton.IsCheckedProperty, (_, _) =>
        {
            draft.SupportsEffort = supports.IsChecked == true;
            if (!draft.SupportsEffort) { draft.Levels.Clear(); foreach (var chip in chips.Children.OfType<ToggleButton>()) chip.IsChecked = false; }
            levels.Visibility = draft.SupportsEffort ? Visibility.Visible : Visibility.Collapsed;
        });
        var validation = PhaseModelErrorText(draft.Error ?? "");
        AutomationProperties.SetAutomationId(validation, "phaseModels-error-" + provider);
        async Task Add()
        {
            try
            {
                // The action boundary uses the live form, including a final IME
                // commit or automation edit whose text event is still queued.
                var selectedLevels = chips.Children.OfType<ToggleButton>().Where(chip => chip.IsChecked == true).Select(chip => (string)chip.Tag).ToArray();
                var entry = PhaseModelPreferences.Registration(input.Text, PhaseRegistered(provider), supports.IsChecked == true, selectedLevels);
                await UpdatePhaseRegistered(provider, entries => entries.Any(e => e.Name == entry.Name) ? entries : [.. entries, entry], () => { draft.Name = ""; draft.SupportsEffort = false; draft.Levels.Clear(); draft.Error = null; });
            }
            catch (ArgumentException ex) { validation.Text = draft.Error = ex.Message; validation.Visibility = Visibility.Visible; }
        }
        add.Click += async (_, _) => await Add();
        input.KeyDown += async (_, e) => { if (e.Key == VirtualKey.Enter && !composing) { e.Handled = true; await Add(); } };
        Grid.SetColumn(input, 1); addRow.Children.Add(input); Grid.SetColumn(add, 2); addRow.Children.Add(add);
        addPanel.Children.Add(addRow); addPanel.Children.Add(SettingsLabeled(SettingsText(supportsLabel, 11), supports)); addPanel.Children.Add(levels);
        block.Children.Add(addPanel); block.Children.Add(validation);
    }

    /// <summary>The name field's width in the add row: the trailing half of the row, as the Mac's form gives a text field (screens/10-settings-models-*.webp).</summary>
    internal const double PhaseNameField = 276;

    /// <summary>
    /// One effort level of a name being registered (M/PhaseModelSettingsView.swift:272-280): a mini
    /// prominent chip, full strength when chosen and at 0.4 when not. A ToggleButton whose look is its content.
    /// </summary>
    private ToggleButton PhaseLevelChip(string level, bool chosen)
    {
        var words = new TextBlock { Text = level, FontSize = 9, Foreground = brushes.Brush(DesignToken.OnAccent), VerticalAlignment = VerticalAlignment.Center };
        var face = new Border { Child = words, Height = SettingsMiniHeight, Padding = new Thickness(DesignMetrics.Spacing.Xs, 0, DesignMetrics.Spacing.Xs, 0), CornerRadius = new CornerRadius(3.5), Background = brushes.Brush(DesignToken.Accent) };
        var chip = new ToggleButton { Content = face, Tag = level, IsChecked = chosen, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(3.5) };
        ClearToggleChrome(chip);
        void Paint() => face.Opacity = chip.IsChecked == true ? 1 : 0.4;
        chip.Checked += (_, _) => Paint(); chip.Unchecked += (_, _) => Paint(); Paint();
        AutomationProperties.SetName(chip, level);
        return chip;
    }

    private Task UpdatePhaseRegistered(string provider, Func<List<RegisteredModelEntry>, List<RegisteredModelEntry>> change, Action? accepted = null) => Act(async () =>
    {
        await service.UpdateAsync(snapshot => { var defaults = snapshot.ModelDefaults ?? new(); return snapshot with { ModelDefaults = provider == "codex" ? defaults with { Codex = defaults.Codex with { RegisteredModels = change(defaults.Codex.RegisteredModels) } } : defaults with { Claude = defaults.Claude with { RegisteredModels = change(defaults.Claude.RegisteredModels) } } }; });
        accepted?.Invoke(); RefreshPhaseModelsSection();
        // Existing panes may already select this custom model; their effort
        // menus must reflect newly added/removed capabilities immediately.
        foreach (var pane in views.Values) pane.Refresh();
    });
    private Task ApplyProviderPhaseRow(string provider, Phase phase, string value) => Act(async () => await SavePhaseModelEdit(PhaseModelPreferences.ApplyRow(provider, phase, value, service.Snapshot.PhaseModels ?? new(), phaseModelTools)));
    private Task ApplyPhaseEffort(string provider, Phase phase, string value) => Act(async () => { await service.UpdateAsync(s => s with { PhaseModels = PhaseModelPreferences.SetEffort(s.PhaseModels ?? new(), provider, phase, value) }); RefreshPhaseModelsSection(); });
    internal Task ApplyPhaseModelKnob(string id, string value) => Act(async () => await SavePhaseModelEdit(PhaseModelSection.ApplyKnob(id, value, service.Snapshot.PhaseModels ?? new(), phaseModelTools)));
    private async Task SavePhaseModelEdit(PhaseModelEdit edit)
    {
        phaseModelTools = options.SmokeTest ? phaseModelTools with { OmcAgents = edit.OmcAgents, OuroborosKeys = edit.OuroborosKeys } : PhaseModelSection.SaveTools(phaseModelTools, edit);
        await service.UpdateAsync(snapshot => snapshot with { PhaseModels = edit.Config }); RefreshPhaseModelsSection();
    }
    private void RefreshPhaseModelsSection() { if (phaseModelsPanel is { } panel) FillPhaseModelsSection(panel, service.Snapshot.PhaseModels ?? new(), phaseModelTools); }
    // The refusal in 11pt errText (M/PhaseModelSettingsView.swift:60-64); it takes no room while there is none.
    private TextBlock PhaseModelErrorText(string error)
    {
        var words = SettingsText(error, 11, DesignToken.ErrText);
        words.Visibility = error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        return words;
    }
    /// <summary>Every element of the section, rows and their contents alike (the logical tree, so it reads a section that is not on screen).</summary>
    internal static IEnumerable<FrameworkElement> PhaseModelElements(FrameworkElement element) => SettingsElements(element);
    internal static List<string> PhaseModelSectionTexts(StackPanel panel) => PhaseModelElements(panel).SelectMany(e => e switch { TextBlock t => new[] { t.Text }, ComboBox c => c.Items.OfType<ComboBoxItem>().Select(i => i.Content?.ToString() ?? ""), Button b => new[] { b.Content as string ?? "" }, _ => Array.Empty<string>() }).Where(t => t.Length > 0).ToList();
}
