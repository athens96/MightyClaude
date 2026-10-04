using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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
        var panel = new StackPanel { Spacing = 12 };
        FillPhaseModelsSection(panel, config, tools);
        return panel;
    }

    private ModelCatalog PhaseCatalog(string provider) => Runtime(provider)?.ModelCatalog ?? ProviderCatalog.Fallback(provider);
    private List<RegisteredModelEntry> PhaseRegistered(string provider) => provider == "codex" ? service.Snapshot.ModelDefaults?.Codex.RegisteredModels ?? [] : service.Snapshot.ModelDefaults?.Claude.RegisteredModels ?? [];
    private static TextBlock PhaseHint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .75 };

    private void FillPhaseModelsSection(StackPanel panel, PhaseModelsSnapshot config, PhaseModelTools tools)
    {
        panel.Children.Clear();
        panel.Children.Add(PhaseHint(PhaseModelSection.Description));
        panel.Children.Add(PhaseHint(Locale.Get("settings.phaseModels.effortNote")));
        foreach (var provider in new[] { "claude", "codex" })
        {
            var block = new StackPanel { Spacing = 8 };
            AutomationProperties.SetAutomationId(block, "phaseModels-provider-" + provider);
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            header.Children.Add(ProviderMarkView.Labelled(ProviderMark.Label(provider), provider, 13, Microsoft.UI.Text.FontWeights.SemiBold));
            if (ProviderCatalog.IsBeta(provider)) header.Children.Add(BetaBadgeView.Create(service.Snapshot.Theme != "light"));
            block.Children.Add(header);
            foreach (var phase in PhaseModelSection.Phases.Where(p => provider != "claude" || p != Phase.Review))
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new() { Width = new GridLength(120) });
                var state = provider == "claude" ? PhaseModelRouting.ClaudeRowState(phase, config) : PhaseModelRouting.CodexRowState(phase, config);
                var title = PhaseModelSection.PhaseLabel(phase);
                var phaseId = phase.ToString().ToLowerInvariant();
                if (state is not null) row.Children.Add(PhaseModelPicker(provider, title, state.Value ?? PhaseModelSection.MixedSentinel, "phaseModels-model-" + provider + "-" + phaseId, value => ApplyProviderPhaseRow(provider, phase, value)));
                else row.Children.Add(PhaseHint(title + " · " + Locale.Get("settings.phaseModels.paneModel")));
                var effort = (provider, phase) switch { ("claude", Phase.Execution) => config.ClaudeMainEffort ?? "default", ("codex", Phase.Planning) => config.CodexPlanModeReasoningEffort, ("codex", Phase.Execution) => config.CodexMainEffort ?? "default", ("codex", Phase.Subagents) => config.CodexSubagentEffort ?? "default", _ => null };
                FrameworkElement effortCell = effort is null ? PhaseHint(Locale.Get("settings.phaseModels.effortUnsupported")) : PhasePicker(title, PhaseModelPreferences.Efforts(provider, PhaseCatalog(provider), effort).Select(v => new PhaseModelVersion(v, v)), effort, "phaseModels-effort-" + provider + "-" + phaseId, value => ApplyPhaseEffort(provider, phase, value));
                if (effort is not null) AutomationProperties.SetName(effortCell, title + " · " + Locale.Get("composer.label.effort"));
                Grid.SetColumn(effortCell, 1); row.Children.Add(effortCell); block.Children.Add(row);
            }
            foreach (var knob in PhaseModelSection.ToolBlocks(config, tools).Single(b => b.Tool == provider).Knobs.Where(k => !k.IsEffort))
                block.Children.Add(PhaseModelPicker(provider, knob.Label, knob.Value, "phase-models-knob-" + knob.KnobId, value => ApplyPhaseModelKnob(knob.KnobId, value)));
            AddRegisteredModels(block, provider);
            panel.Children.Add(block);
        }
        if (tools.Error is { } error) panel.Children.Add(PhaseModelErrorText(error));
    }

    private ComboBox PhaseModelPicker(string provider, string label, string current, string id, Func<string, Task> action) =>
        PhasePicker(label, PhaseModelPreferences.Versions(PhaseCatalog(provider), PhaseRegistered(provider), current), current, id, action);

    private static ComboBox PhasePicker(string label, IEnumerable<PhaseModelVersion> values, string current, string id, Func<string, Task> action)
    {
        var picker = new ComboBox { Header = label, HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
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

    private void AddRegisteredModels(StackPanel block, string provider)
    {
        block.Children.Add(PhaseHint(Locale.Get("settings.phaseModels.registeredTitle")));
        foreach (var entry in PhaseRegistered(provider))
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            AutomationProperties.SetAutomationId(row, "phaseModels-registered-" + provider + "-" + entry.Name);
            row.Children.Add(PhaseHint(entry.Name + (entry.SupportsEffort ? " · " + Locale.Get("settings.phaseModels.supportsEffortLabel") : "")));
            var remove = new Button { Content = Locale.Get("settings.phaseModels.deleteButton") };
            remove.Click += async (_, _) => await UpdatePhaseRegistered(provider, entries => entries.Where(e => e.Name != entry.Name).ToList());
            Grid.SetColumn(remove, 1); row.Children.Add(remove); block.Children.Add(row);
        }
        if (!phaseRegistrationDrafts.TryGetValue(provider, out var draft)) phaseRegistrationDrafts[provider] = draft = new();
        var addRow = new Grid { ColumnSpacing = 8 };
        addRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); addRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var input = new TextBox { PlaceholderText = Locale.Get("settings.phaseModels.addPlaceholder"), Text = draft.Name };
        var composing = false;
        input.TextCompositionStarted += (_, _) => composing = true;
        input.TextCompositionEnded += (_, _) => composing = false;
        input.LostFocus += (_, _) => composing = false;
        AutomationProperties.SetAutomationId(input, "phaseModels-add-" + provider);
        input.TextChanged += (_, _) => draft.Name = input.Text;
        var add = new Button { Content = Locale.Get("settings.phaseModels.addButton") };
        AutomationProperties.SetAutomationId(add, "phaseModels-addButton-" + provider);
        var supports = new CheckBox { Content = Locale.Get("settings.phaseModels.supportsEffortLabel"), IsChecked = draft.SupportsEffort };
        AutomationProperties.SetAutomationId(supports, "phaseModels-addEffort-" + provider);
        var levels = new StackPanel { Spacing = 4, Visibility = draft.SupportsEffort ? Visibility.Visible : Visibility.Collapsed };
        levels.Children.Add(PhaseHint(Locale.Get("settings.phaseModels.effortLevelsLabel")));
        var chips = new PillWrapPanel();
        foreach (var level in Wire.Efforts)
        {
            var chip = new CheckBox { Content = level, IsChecked = draft.Levels.Contains(level) };
            AutomationProperties.SetAutomationId(chip, "phaseModels-addLevel-" + provider + "-" + level);
            chip.Checked += (_, _) => draft.Levels.Add(level); chip.Unchecked += (_, _) => draft.Levels.Remove(level); chips.Children.Add(chip);
        }
        levels.Children.Add(chips);
        supports.Checked += (_, _) => { draft.SupportsEffort = true; levels.Visibility = Visibility.Visible; };
        supports.Unchecked += (_, _) => { draft.SupportsEffort = false; draft.Levels.Clear(); foreach (var chip in chips.Children.OfType<CheckBox>()) chip.IsChecked = false; levels.Visibility = Visibility.Collapsed; };
        var validation = PhaseModelErrorText(draft.Error ?? "");
        AutomationProperties.SetAutomationId(validation, "phaseModels-error-" + provider);
        async Task Add()
        {
            try
            {
                var entry = PhaseModelPreferences.Registration(draft.Name, PhaseRegistered(provider), draft.SupportsEffort, draft.Levels);
                await UpdatePhaseRegistered(provider, entries => entries.Any(e => e.Name == entry.Name) ? entries : [.. entries, entry], () => { draft.Name = ""; draft.SupportsEffort = false; draft.Levels.Clear(); draft.Error = null; });
            }
            catch (ArgumentException ex) { validation.Text = draft.Error = ex.Message; }
        }
        add.Click += async (_, _) => await Add();
        input.KeyDown += async (_, e) => { if (e.Key == VirtualKey.Enter && !composing) { e.Handled = true; await Add(); } };
        addRow.Children.Add(input); Grid.SetColumn(add, 1); addRow.Children.Add(add);
        block.Children.Add(addRow); block.Children.Add(supports); block.Children.Add(levels); block.Children.Add(validation);
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
    private static TextBlock PhaseModelErrorText(string error) => new() { Text = error, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed) };
    internal static IEnumerable<FrameworkElement> PhaseModelElements(FrameworkElement element)
    {
        yield return element;
        if (element is Panel panel) foreach (var child in panel.Children.OfType<FrameworkElement>()) foreach (var descendant in PhaseModelElements(child)) yield return descendant;
    }
    internal static List<string> PhaseModelSectionTexts(StackPanel panel) => PhaseModelElements(panel).SelectMany(e => e switch { TextBlock t => new[] { t.Text }, ComboBox c => c.Items.OfType<ComboBoxItem>().Select(i => i.Content?.ToString() ?? "").Prepend(c.Header?.ToString() ?? ""), Button b => new[] { b.Content?.ToString() ?? "" }, CheckBox c => new[] { c.Content?.ToString() ?? "" }, TextBox t => new[] { t.PlaceholderText }, _ => Array.Empty<string>() }).Where(t => t.Length > 0).ToList();
}
