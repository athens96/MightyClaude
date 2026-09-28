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
            var pane = Session; var width = selectors.ActualWidth;
            composerMode = width >= 660 ? 2 : width >= 430 ? 1 : 0;
            if (pane.Kind == "shell") { provider.Visibility = model.Visibility = effort.Visibility = permission.Visibility = fast.Visibility = more.Visibility = Visibility.Collapsed; return; }
            provider.Width = composerMode == 2 ? 90 : 72; provider.Visibility = width >= 250 ? Visibility.Visible : Visibility.Collapsed;
            effort.Width = composerMode == 2 ? 80 : 66; permission.Width = composerMode == 2 ? 110 : 90; fast.Width = 58; more.Width = 32;
            effort.Visibility = composerMode > 0 && (Capabilities.Effort || pane.Settings.Effort != "default") ? Visibility.Visible : Visibility.Collapsed;
            permission.Visibility = composerMode > 0 ? Visibility.Visible : Visibility.Collapsed;
            fast.Visibility = composerMode == 2 && pane.Provider == "codex" && (Capabilities.FastMode || pane.Settings.FastMode) ? Visibility.Visible : Visibility.Collapsed;
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
            foreach (var value in Wire.Providers) providers.Items.Add(Item(ProviderCatalog.Name(value), () => ChangeProvider(value), pane.Provider == value)); menu.Items.Add(providers);
            var catalog = owner.Runtime(pane.Provider)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider);
            var models = new MenuFlyoutSubItem { Text = Locale.Get("composer.label.model") };
            foreach (var value in catalog.Models) models.Items.Add(Item(value.DisplayName, () => ChangeModel(value.Value), value.Value == pane.Model));
            if (!catalog.Models.Any(m => m.Value == pane.Model)) models.Items.Add(Item(pane.Model, () => ChangeModel(pane.Model), true));
            if (pane.Provider != "gemini") models.Items.Add(Item(Locale.Get("composer.model.enterIdMenu"), CustomModel)); menu.Items.Add(models);
            if (caps.Effort || pane.Settings.Effort != "default")
            {
                var strengths = new MenuFlyoutSubItem { Text = Locale.Get("composer.label.effort") };
                var registeredModels = RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot);
                foreach (var value in new[] { "default" }.Concat(ProviderCatalog.Efforts(pane.Provider, pane.Model, catalog, registeredModels))) strengths.Items.Add(Item(value == "default" ? Locale.Get("composer.effort.auto") : value, () => ChangeSettings(s => s with { Effort = value }), value == pane.Settings.Effort));
                menu.Items.Add(strengths);
            }
            var permissions = new MenuFlyoutSubItem { Text = Locale.Get("composer.label.permission") };
            foreach (var value in (caps.PermissionModes ?? []).Where(ProviderCatalog.PermissionModes(pane.Provider).Contains)) permissions.Items.Add(Item(PermissionLabel(pane.Provider, value), () => ChangeSettings(s => s with { PermissionMode = value, NetworkAccess = pane.Provider == "codex" && value == "acceptEdits" && s.NetworkAccess }), pane.Settings.PermissionMode == value, PermissionHelp(pane.Provider, value)));
            menu.Items.Add(permissions);
            if (pane.Provider == "codex" && (caps.FastMode || pane.Settings.FastMode)) menu.Items.Add(Item("Fast", () => ChangeSettings(s => s with { FastMode = !s.FastMode && caps.FastMode }), pane.Settings.FastMode));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        internal void RefreshElapsed()
        {
            if (!owner.service.Snapshot.Sessions.Any(p => p.Id == id)) return;
            elapsed.Text = Session.Kind == "shell" ? "" : Session.RunTiming?.Label() ?? "";
        }
        private Task ShowContext() => owner.Act(async () =>
        {
            var pane = Session; var usage = pane.SessionUsage;
            var content = new StackPanel { Spacing = 9, MinWidth = 300, MaxWidth = 420 };
            void Row(string name, string? value) { if (!string.IsNullOrEmpty(value)) content.Children.Add(new TextBlock { Text = name + "  " + value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 }); }
            Row(Locale.Get("composer.sessionInfo.session"), pane.Title); Row(Locale.Get("composer.label.runner"), ProviderCatalog.Name(pane.Provider)); Row(Locale.Get("composer.label.model"), usage?.Model ?? (pane.Model == "default" ? Locale.Get("composer.model.cliDefault") : pane.Model));
            Row(Locale.Get("composer.sessionInfo.status"), StateLabel(pane.Status)); Row(Locale.Get("composer.sessionInfo.elapsed"), pane.RunTiming?.Label());
            Row(Locale.Get("composer.sessionInfo.context"), usage?.ContextPercent is { } percent ? $"{percent:0.#}% · {usage.ContextUsedTokens:N0} / {usage.ContextWindowTokens:N0} tokens" : Locale.Get("composer.sessionInfo.contextUnavailable"));
            Row(Locale.Get("composer.sessionInfo.input"), usage?.InputTokens?.ToString("N0")); Row(Locale.Get("composer.sessionInfo.output"), usage?.OutputTokens?.ToString("N0"));
            Row(Locale.Get("composer.sessionInfo.cacheRead"), usage?.CacheReadTokens?.ToString("N0")); Row(Locale.Get("composer.sessionInfo.cacheWrite"), usage?.CacheWriteTokens?.ToString("N0"));
            Row(Locale.Get("composer.sessionInfo.reasoning"), usage?.ReasoningTokens?.ToString("N0")); Row(Locale.Get("composer.sessionInfo.cost"), usage?.CostUSD is { } cost ? $"${cost:0.####}" : null);
            Row(Locale.Get("composer.sessionInfo.tokenScope"), usage?.TokenScope); Row(Locale.Get("composer.sessionInfo.costScope"), usage?.CostScope); Row(Locale.Get("composer.sessionInfo.workspace"), Workspace.Name); Row(Locale.Get("composer.sessionInfo.sessionId"), pane.Id);
            if (usage is not null) Row(Locale.Get("composer.sessionInfo.source"), usage.Source + " · " + usage.UpdatedAt);
            content.Children.Add(new TextBlock { Text = Locale.Get("composer.sessionInfo.note"), FontSize = 11, Opacity = .65, TextWrapping = TextWrapping.Wrap });
            await new ContentDialog { Title = Locale.Get("composer.sessionInfo.title"), Content = new ScrollViewer { Content = content, MaxHeight = 440 }, CloseButtonText = Locale.Get("settings.closeButton"), XamlRoot = owner.root.XamlRoot }.ShowAsync();
        });
    }
}
