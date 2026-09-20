using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, (ProgressRing Ring, TextBlock Status, TextBlock Time)> sessionIndicators = [];
    private readonly Dictionary<string, (ProgressRing Ring, TextBlock Status, TextBlock Time)> tabIndicators = [];
    private static string StateLabel(string state) => state switch { "running" => "실행 중", "completed" => "완료", "error" => "오류", "stopped" => "중지됨", _ => "준비" };
    private FrameworkElement SessionIndicator(RunSession session, bool tab = false)
    {
        var row = new Grid { ColumnSpacing = 6, VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var ring = new ProgressRing { Width = 13, Height = 13, MinWidth = 0, MinHeight = 0, IsActive = session.Status == "running", Visibility = session.Status == "running" ? Visibility.Visible : Visibility.Collapsed };
        row.Children.Add(ring);
        var title = new TextBlock { Text = session.Title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 }; Grid.SetColumn(title, 1); row.Children.Add(title);
        var state = new TextBlock { Text = StateLabel(session.Status), FontSize = 10, Opacity = .65 };
        var elapsed = new TextBlock { Text = session.Kind == "shell" ? "" : session.RunTiming?.Label() ?? "", FontSize = 10, Opacity = .7 };
        var trailing = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center }; if (!tab) trailing.Children.Add(state); trailing.Children.Add(elapsed); Grid.SetColumn(trailing, 2); row.Children.Add(trailing);
        (tab ? tabIndicators : sessionIndicators)[session.Id] = (ring, state, elapsed);
        AutomationProperties.SetName(row, session.Title + ", " + StateLabel(session.Status)); return row;
    }
    private void RefreshRunningIndicators()
    {
        if (closing) return;
        foreach (var session in service.Snapshot.Sessions)
        {
            foreach (var values in new[] { sessionIndicators, tabIndicators })
            {
                if (!values.TryGetValue(session.Id, out var view)) continue;
                view.Ring.IsActive = session.Status == "running"; view.Ring.Visibility = session.Status == "running" ? Visibility.Visible : Visibility.Collapsed;
                view.Status.Text = StateLabel(session.Status); view.Time.Text = session.Kind == "shell" ? "" : session.RunTiming?.Label() ?? "";
            }
            if (views.TryGetValue(session.Id, out var pane)) pane.RefreshElapsed();
        }
    }
    private Task SelectWorkspace(string id) => Act(async () =>
    {
        await service.UpdateAsync(s =>
        {
            var preferred = s.PaneLayoutActiveSessionIds?.GetValueOrDefault(id);
            var selected = s.Sessions.FirstOrDefault(p => p.WorkspaceId == id && p.Id == preferred)?.Id ?? PaneLayout.Groups(EffectiveLayout(s, id)).Select(g => g.SelectedSessionId).FirstOrDefault();
            return s with { ActiveWorkspaceId = id, ActiveSessionId = selected };
        });
        Render(); if (!options.SmokeTest) await RefreshRemoteState();
    });
    private static MenuFlyoutItem MenuItem(string text, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = text }; item.Click += async (_, _) => await action(); return item;
    }
    private MenuFlyout NewSessionMenu(string? groupId = null)
    {
        var menu = new MenuFlyout();
        foreach (var provider in Wire.Providers) menu.Items.Add(MenuItem(ProviderCatalog.Name(provider), () => AddPane("claude", provider, groupId)));
        menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(MenuItem("명령 실행 창", () => AddPane("shell", groupId: groupId)));
        return menu;
    }
    private MenuFlyout WorkspaceMenu(string id)
    {
        var menu = new MenuFlyout(); menu.Items.Add(MenuItem("이름 변경…", () => RenameWorkspace(id)));
        menu.Items.Add(MenuItem("목록에서 제거", () => Act(async () => { await service.RemoveWorkspaceAsync(id); Render(); }))); return menu;
    }
    private MenuFlyout SessionMenu(string id)
    {
        var menu = new MenuFlyout(); menu.Items.Add(MenuItem("이름 변경…", () => RenameSession(id)));
        menu.Items.Add(MenuItem("집중 보기 / 돌아가기", () => Act(async () => { await SelectLayoutSession(id); await ApplyLayoutPreset(LayoutMode(service.Snapshot, service.Snapshot.ActiveWorkspaceId) == "focus" ? "custom" : "focus"); })));
        menu.Items.Add(MenuItem("닫기", () => CloseSession(id))); return menu;
    }
    private Task CloseSession(string id) => Act(async () => { await service.StopAsync(id); await service.UpdateAsync(s => s with { Sessions = s.Sessions.Where(p => p.Id != id).ToList() }); Render(); });
    private async Task<string?> AskName(string title, string current)
    {
        var field = new TextBox { Text = current, MaxLength = 120, MinWidth = 280 };
        var dialog = new ContentDialog { Title = title, Content = field, PrimaryButtonText = "저장", CloseButtonText = "취소", DefaultButton = ContentDialogButton.Primary, XamlRoot = root.XamlRoot };
        dialog.Opened += (_, _) => { field.Focus(FocusState.Programmatic); field.SelectAll(); };
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = string.IsNullOrWhiteSpace(field.Text);
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? field.Text.Trim() : null;
    }
    private Task RenameWorkspace(string id) => Act(async () => { if (service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == id) is not { } workspace) return; if (await AskName("워크스페이스 이름", workspace.Name) is { } name) { await service.UpdateAsync(s => s with { Workspaces = s.Workspaces.Select(w => w.Id == id ? w with { Name = name } : w).ToList() }); Render(); } });
    private Task RenameSession(string id) => Act(async () => { if (service.Snapshot.Sessions.FirstOrDefault(p => p.Id == id) is not { } session) return; if (await AskName("실행 창 이름", session.Title) is { } name) { await service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == id ? p with { Title = name } : p).ToList() }); Render(); } });
}
