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
            Require(shown == selection + " ⌄", $"the model button reads '{shown}', not '{selection} ⌄'");
            var rows = (model.Flyout as MenuFlyout)?.Items.OfType<MenuFlyoutItem>().Select(item => item.Text.Replace("✓  ", "")).ToList() ?? [];
            var options = ModelLabel.PickerOptions(pane, catalog);
            Require(options.All(option => rows.Contains(option.DisplayName)), "the model picker rows differ from ModelLabel.PickerOptions: " + string.Join(", ", rows));
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
