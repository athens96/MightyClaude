using MightyClaude.Core;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        /// <summary>
        /// Smoke key <c>modelLabel</c>: the composer's model button and every row of its picker
        /// read through <see cref="ModelLabel"/> (macOS chip and picker), while each row still
        /// changes the model to its untouched value. Read-only: the pane's saved model and usage
        /// are not changed.
        /// </summary>
        internal Dictionary<string, object?> RunModelLabelSmoke()
        {
            var pane = Session;
            var catalog = owner.Runtime(pane.Provider)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider);
            RefreshMenus(pane, catalog);
            var shown = PillText(model).Text;
            var selection = ModelLabel.Selection(pane, catalog);
            Require(shown == selection, $"the model button reads '{shown}', not '{selection}'");
            var items = (model.Flyout as MenuFlyout)?.Items.OfType<MenuFlyoutItem>().ToList() ?? [];
            var rows = items.Select(item => item.Text).ToList();
            var options = ModelLabel.PickerOptions(pane, catalog);
            Require(options.All(option => rows.Contains(option.DisplayName)), "the model picker rows differ from ModelLabel.PickerOptions: " + string.Join(", ", rows));
            // The Mac's menu (M/SessionPaneView.swift:326-350): the providers under their header, then the models under theirs, one check in each.
            var providers = Wire.Providers.Select(value => ProviderCatalog.BetaLabel(value, ProviderMark.Label(value))).ToList();
            Require(rows.Count > providers.Count && rows[0] == Locale.Get("composer.label.runner") && rows.Skip(1).Take(providers.Count).SequenceEqual(providers) && rows.Contains(Locale.Get("composer.label.model")),
                "the model menu must list the providers under their header before the models: " + string.Join(", ", rows));
            var marked = items.OfType<ToggleMenuFlyoutItem>().Where(item => item.IsChecked).Select(item => item.Text).ToList();
            Require(marked.Count == 2 && marked[0] == ProviderCatalog.BetaLabel(pane.Provider, ProviderMark.Label(pane.Provider)) && marked[1] == options.First(option => option.Value == pane.Model).DisplayName,
                "the model menu must check the current provider and the current model: " + string.Join(", ", marked));
            Require(options.Take(catalog.Models.Count).Select(option => option.Value).SequenceEqual(catalog.Models.Select(option => option.Value)), "the picker changed a model value");
            var reported = pane with { Model = "opus", SessionUsage = new SessionUsage { Provider = pane.Provider, Model = "claude-opus-5-5", SelectedModel = "opus" } };
            var versioned = ModelLabel.Selection(reported, catalog);
            Require(pane.Provider != "claude" || versioned == "Opus 5.5", "an alias the CLI reported as claude-opus-5-5 does not read Opus 5.5: " + versioned);
            return new()
            {
                ["button"] = shown,
                ["rows"] = rows,
                ["reportedAlias"] = versioned,
                ["valuesUnchanged"] = true,
            };
        }
    }
}
