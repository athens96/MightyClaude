using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    // Actual settings controls, writing through DesktopService into the isolated
    // native-smoke profile. SmokeTest replaces external tool files with memory.
    private async Task CheckPhaseModelsSettingsActions(FrameworkElement frame)
    {
        Require(options.SmokeTest && options.ProfileDirectory is not null, "Phase settings fixture needs an isolated profile.");
        var original = service.Snapshot; var savedRuntime = runtime; var savedTools = phaseModelTools;
        var savedDrafts = phaseRegistrationDrafts.ToDictionary(p => p.Key, p => p.Value);
        try
        {
            var claudeCatalog = new ModelCatalog("fixture", [new("default", "Default", ""), new("sonnet", "Sonnet", "", "claude-sonnet-fixture", true, ["low", "high", "max"])], "");
            var codexCatalog = new ModelCatalog("fixture", [new("default", "Default", ""), new("codex-fixture", "Codex fixture", "", SupportedEffortLevels: ["high", "xhigh"])], "");
            Require(runtime is not null, "Native phase smoke needs initialized runtime fixture.");
            runtime = runtime! with { Providers = runtime.Providers.Select(p => p with { ModelCatalog = p.Id == "claude" ? claudeCatalog : p.Id == "codex" ? codexCatalog : p.ModelCatalog }).ToList() };
            phaseRegistrationDrafts.Clear(); phaseModelTools = PhaseModelSection.SmokeFixtureTools;
            await service.UpdateAsync(s => s with { PhaseModels = new() { ClaudeMain = "previous-main", CodexReviewModel = "review-unchanged" }, ModelDefaults = new() });
            RefreshPhaseModelsSection(); frame.UpdateLayout();
            ComboBox Picker(string id) => VisualChildren(frame).OfType<ComboBox>().Single(p => AutomationProperties.GetAutomationId(p) == id);
            bool Replaced(ComboBox previous, string id) => VisualChildren(frame).OfType<ComboBox>().Any(p => p.IsLoaded && p.ActualWidth > 0 && AutomationProperties.GetAutomationId(p) == id && !ReferenceEquals(previous, p));
            FrameworkElement Control(string id) => VisualChildren(frame).OfType<FrameworkElement>().Single(p => AutomationProperties.GetAutomationId(p) == id);
            await WaitUI(() => VisualChildren(frame).OfType<ComboBox>().Any(p => p.IsLoaded && p.ActualWidth > 0 && AutomationProperties.GetAutomationId(p) == "phaseModels-model-claude-execution"));
            var execution = Picker("phaseModels-model-claude-execution");
            Require(execution.Items.OfType<ComboBoxItem>().Any(i => Equals(i.Tag, "sonnet")) && execution.Items.OfType<ComboBoxItem>().Any(i => Equals(i.Tag, "claude-sonnet-fixture")), "CLI alias and concrete version must both be selectable.");
            execution.SelectedItem = execution.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, "claude-sonnet-fixture"));
            await WaitUI(() => service.Snapshot.PhaseModels?.ClaudeMain == "claude-sonnet-fixture" && Replaced(execution, "phaseModels-model-claude-execution"));
            Require(service.Snapshot.PhaseModels?.ClaudeSonnetAlias == "claude-sonnet-fixture" && service.Snapshot.PhaseModels.CodexReviewModel == "review-unchanged", "Claude row must persist matching knobs while preserving Codex.");
            var effort = Picker("phaseModels-effort-codex-subagents");
            effort.SelectedItem = effort.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, "xhigh"));
            await WaitUI(() => service.Snapshot.PhaseModels?.CodexSubagentEffort == "xhigh" && Replaced(effort, "phaseModels-effort-codex-subagents"));
            ((TextBox)Control("phaseModels-add-claude")).Text = "registered-phase-fixture";
            ((CheckBox)Control("phaseModels-addEffort-claude")).IsChecked = true;
            ((CheckBox)Control("phaseModels-addLevel-claude-max")).IsChecked = true;
            var add = (Button)Control("phaseModels-addButton-claude");
            frame.UpdateLayout(); await WaitUI(() => add.IsLoaded && add.ActualWidth > 0);
            ((IInvokeProvider)new ButtonAutomationPeer(add).GetPattern(PatternInterface.Invoke)).Invoke();
            await WaitUI(() => service.Snapshot.ModelDefaults?.Claude.RegisteredModels.Any(e => e.Name == "registered-phase-fixture") == true
                && VisualChildren(frame).OfType<FrameworkElement>().Any(e => AutomationProperties.GetAutomationId(e) == "phaseModels-registered-claude-registered-phase-fixture"));
            var registered = service.Snapshot.ModelDefaults!.Claude.RegisteredModels.Single();
            Require(registered.SupportsEffort && registered.SupportedEffortLevels!.SequenceEqual(["max"]) && service.Snapshot.ModelDefaults.Codex.RegisteredModels.Count == 0, "Registration must preserve provider and capabilities.");
            var registrationRow = Control("phaseModels-registered-claude-registered-phase-fixture");
            var remove = VisualChildren(registrationRow).OfType<Button>().Single();
            frame.UpdateLayout(); await WaitUI(() => remove.IsLoaded && remove.ActualWidth > 0);
            ((IInvokeProvider)new ButtonAutomationPeer(remove).GetPattern(PatternInterface.Invoke)).Invoke();
            await WaitUI(() => service.Snapshot.ModelDefaults!.Claude.RegisteredModels.Count == 0 && !VisualChildren(frame).OfType<FrameworkElement>().Any(e => AutomationProperties.GetAutomationId(e) == "phaseModels-registered-claude-registered-phase-fixture"));
            using var persisted = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(options.ProfileDirectory!, "workspace-state.json")));
            Require(persisted.RootElement.GetProperty("phaseModels").GetProperty("codexSubagentEffort").GetString() == "xhigh", "Selections must be persisted to the isolated state file.");
        }
        finally
        {
            runtime = savedRuntime; phaseModelTools = savedTools; phaseRegistrationDrafts.Clear(); foreach (var pair in savedDrafts) phaseRegistrationDrafts[pair.Key] = pair.Value;
            await service.UpdateAsync(s => s with { PhaseModels = original.PhaseModels, ModelDefaults = original.ModelDefaults }); RefreshPhaseModelsSection();
        }
    }
}
