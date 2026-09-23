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
            var providers = new MenuFlyoutSubItem { Text = "실행기" };
            foreach (var value in Wire.Providers) providers.Items.Add(Item(ProviderCatalog.Name(value), () => ChangeProvider(value), pane.Provider == value)); menu.Items.Add(providers);
            var catalog = owner.Runtime(pane.Provider, pane.WorkspaceId)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider);
            var models = new MenuFlyoutSubItem { Text = "모델" };
            foreach (var value in catalog.Models) models.Items.Add(Item(value.DisplayName, () => ChangeModel(value.Value), value.Value == pane.Model));
            if (!catalog.Models.Any(m => m.Value == pane.Model)) models.Items.Add(Item(pane.Model, () => ChangeModel(pane.Model), true));
            if (pane.Provider != "gemini") models.Items.Add(Item("모델 ID 입력…", CustomModel)); menu.Items.Add(models);
            if (caps.Effort || pane.Settings.Effort != "default")
            {
                var strengths = new MenuFlyoutSubItem { Text = "추론 강도" };
                var registeredModels = ModelDefaultsResolution.GetProviderRegisteredModels(pane.Provider, Workspace.ModelDefaults, owner.service.Snapshot.ModelDefaults);
                foreach (var value in new[] { "default" }.Concat(ProviderCatalog.Efforts(pane.Provider, pane.Model, catalog, registeredModels))) strengths.Items.Add(Item(value == "default" ? "Auto · CLI 기본값" : value, () => ChangeSettings(s => s with { Effort = value }), value == pane.Settings.Effort));
                menu.Items.Add(strengths);
            }
            var permissions = new MenuFlyoutSubItem { Text = "권한" };
            foreach (var value in (caps.PermissionModes ?? []).Where(ProviderCatalog.PermissionModes(pane.Provider).Contains)) permissions.Items.Add(Item(ModeMenuLabel(pane.Provider, value, Workspace.ModelDefaults, owner.service.Snapshot.ModelDefaults), () => ChangeSettings(s => s with { PermissionMode = value, NetworkAccess = pane.Provider == "codex" && value == "acceptEdits" && s.NetworkAccess }), pane.Settings.PermissionMode == value, PermissionHelp(pane.Provider, value)));
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
            Row("실행 창", pane.Title); Row("실행기", ProviderCatalog.Name(pane.Provider)); Row("모델", usage?.Model ?? (pane.Model == "default" ? "CLI 기본값" : pane.Model));
            Row("상태", StateLabel(pane.Status)); Row("경과 시간", pane.RunTiming?.Label());
            Row("컨텍스트", usage?.ContextPercent is { } percent ? $"{percent:0.#}% · {usage.ContextUsedTokens:N0} / {usage.ContextWindowTokens:N0} tokens" : "— CLI가 현재 컨텍스트 사용량을 제공하지 않았습니다.");
            Row("입력", usage?.InputTokens?.ToString("N0")); Row("출력", usage?.OutputTokens?.ToString("N0"));
            Row("캐시 읽기", usage?.CacheReadTokens?.ToString("N0")); Row("캐시 쓰기", usage?.CacheWriteTokens?.ToString("N0"));
            Row("추론", usage?.ReasoningTokens?.ToString("N0")); Row("비용", usage?.CostUSD is { } cost ? $"${cost:0.####}" : null);
            Row("토큰 범위", usage?.TokenScope); Row("비용 범위", usage?.CostScope); Row("워크스페이스", Workspace.Name); Row("실행 창 ID", pane.Id);
            if (usage is not null) Row("측정 정보", usage.Source + " · " + usage.UpdatedAt);
            content.Children.Add(new TextBlock { Text = "캐시는 입력의 일부이며 추론 토큰은 출력의 일부입니다. 표시된 값만 CLI에서 직접 보고받았습니다.", FontSize = 11, Opacity = .65, TextWrapping = TextWrapping.Wrap });
            await new ContentDialog { Title = "세션 정보", Content = new ScrollViewer { Content = content, MaxHeight = 440 }, CloseButtonText = "닫기", XamlRoot = owner.root.XamlRoot }.ShowAsync();
        });
    }
}
