using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        private void ArrangeComposer()
        {
            // WinUI can finish a layout pass after its pane was removed from
            // the snapshot. A detached editor must not resolve a live session.
            var pane = owner.service.Snapshot.Sessions.FirstOrDefault(s => s.Id == id);
            if (pane is null) return;
            var width = selectors.ActualWidth;
            var capabilities = owner.Runtime(pane.Provider)?.Capabilities ?? ProviderCatalog.Capabilities(pane.Provider);
            composerMode = width >= 660 ? 2 : width >= 430 ? 1 : 0;
            if (pane.Kind == "shell") { provider.Visibility = model.Visibility = effort.Visibility = permission.Visibility = fast.Visibility = more.Visibility = Visibility.Collapsed; return; }
            provider.Width = composerMode == 2 ? 90 : 72; provider.Visibility = width >= 250 ? Visibility.Visible : Visibility.Collapsed;
            effort.Width = composerMode == 2 ? 80 : 66; permission.Width = composerMode == 2 ? 110 : 90; fast.Width = 58; more.Width = 32;
            effort.Visibility = composerMode > 0 && (capabilities.Effort || pane.Settings.Effort != "default") ? Visibility.Visible : Visibility.Collapsed;
            permission.Visibility = composerMode > 0 ? Visibility.Visible : Visibility.Collapsed;
            fast.Visibility = composerMode == 2 && pane.Provider == "codex" && (capabilities.FastMode || pane.Settings.FastMode) ? Visibility.Visible : Visibility.Collapsed;
            model.Visibility = more.Visibility = Visibility.Visible;
        }
        private Task ChangeProvider(string value) => owner.Act(async () =>
        {
            if (Session.Status == "running" || starting || Session.Provider == value) return;
            await Change(p => p with { Provider = value, Title = p.Title == ProviderCatalog.Name(p.Provider) ? ProviderCatalog.Name(value) : p.Title, Model = "default", Settings = new(), ResumeId = null }); Refresh(); input.Focus(FocusState.Programmatic);
        });
        private void AddOverflowSettings(MenuFlyout menu, RunSession pane, ProviderCapabilities caps)
        {
            if (pane.Kind != "claude") return;
            var providers = new MenuFlyoutSubItem { Text = Locale.Get("composer.label.runner") };
            foreach (var value in Wire.Providers) providers.Items.Add(Item(ProviderCatalog.BetaLabel(value, ProviderCatalog.Name(value)), () => ChangeProvider(value), pane.Provider == value)); menu.Items.Add(providers);
            var catalog = owner.Runtime(pane.Provider)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider);
            var models = new MenuFlyoutSubItem { Text = Locale.Get("composer.label.model") };
            models.Items.Add(Item(Locale.Get("composer.model.refresh"), RefreshPaneModels));
            foreach (var value in ModelLabel.PickerOptions(pane, catalog)) models.Items.Add(Item(value.DisplayName, () => ChangeModel(value.Value), value.Value == pane.Model));
            if (pane.Provider != "gemini") models.Items.Add(Item(Locale.Get("composer.model.enterIdMenu"), CustomModel)); menu.Items.Add(models);
            if (caps.Effort || pane.Settings.Effort != "default")
            {
                var strengths = new MenuFlyoutSubItem { Text = Locale.Get("composer.label.effort") };
                var registeredModels = RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot);
                foreach (var value in new[] { "default" }.Concat(ProviderCatalog.Efforts(pane.Provider, pane.Model, catalog, registeredModels))) strengths.Items.Add(Item(value == "default" ? Locale.Get("composer.effort.auto") : value, () => ChangeSettings(s => s with { Effort = value }), value == pane.Settings.Effort));
                menu.Items.Add(strengths);
            }
            var permissions = new MenuFlyoutSubItem { Text = Locale.Get("composer.label.permission") };
            foreach (var value in (caps.PermissionModes ?? []).Where(ProviderCatalog.PermissionModes(pane.Provider).Contains)) permissions.Items.Add(Item(PermissionLabel(pane.Provider, value), () => ChangeSettings(s => s with { PermissionMode = value, NetworkAccess = pane.Provider == "codex" && value is ("acceptEdits" or "onRequest") && s.NetworkAccess }), pane.Settings.PermissionMode == value, PermissionHelp(pane.Provider, value)));
            menu.Items.Add(permissions);
            if (pane.Provider == "codex" && (caps.FastMode || pane.Settings.FastMode)) menu.Items.Add(Item("Fast", () => ChangeSettings(s => s with { FastMode = !s.FastMode && caps.FastMode }), pane.Settings.FastMode));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        /// <summary>The run clock for the pane's session as already read (the 1-second tick reads the snapshot once).</summary>
        internal void RefreshElapsed(RunSession pane)
        {
            elapsed.Text = pane.Kind == "shell" ? "" : pane.RunTiming?.Label() ?? "";
            RefreshSessionInfo();
        }

    }
}
