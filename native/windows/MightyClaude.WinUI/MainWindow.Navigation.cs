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
        var rename = new MenuFlyoutItem { Text = RenameStrings.MenuEntry };
        rename.Click += async (_, _) => await RenameWorkspace(id);
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => rename.IsEnabled = !dialogOpen;
        menu.Items.Add(rename);
        menu.Items.Add(MenuItem("목록에서 제거", () => Act(async () => { await service.RemoveWorkspaceAsync(id); Render(); })));
        return menu;
    }
    private MenuFlyout SessionMenu(string id)
    {
        var rename = new MenuFlyoutItem { Text = RenameStrings.MenuEntry };
        rename.Click += async (_, _) => await RenameSession(id);
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => rename.IsEnabled = !dialogOpen;
        menu.Items.Add(rename);
        menu.Items.Add(MenuItem("집중 보기 / 돌아가기", () => Act(async () => { await SelectLayoutSession(id); await ApplyLayoutPreset(LayoutMode(service.Snapshot, service.Snapshot.ActiveWorkspaceId) == "focus" ? "custom" : "focus"); })));
        menu.Items.Add(MenuItem("닫기", () => CloseSession(id)));
        return menu;
    }
    private Task CloseSession(string id) => Act(async () => { await service.StopAsync(id); await service.UpdateAsync(s => s with { Sessions = s.Sessions.Where(p => p.Id != id).ToList() }); Render(); });
    /// <summary>
    /// The rename dialog of RenameViews.swift: the current name selected, the macOS captions under
    /// the field and 저장 disabled while the name is invalid. Every literal comes from RenameStrings
    /// and every rule from RenameSupport, so Windows and macOS accept and refuse the same names.
    /// </summary>
    private async Task<string?> AskName(string heading, string hint, string current)
    {
        var field = new TextBox { Header = RenameStrings.FieldLabel, Text = current, MinWidth = 300 };
        var hintText = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
        var errors = new StackPanel { Spacing = 2 };
        var content = new StackPanel { Spacing = 8, Children = { field, hintText, errors } };
        var dialog = new ContentDialog
        {
            Title = heading,
            Content = content,
            PrimaryButtonText = RenameStrings.ButtonSave,
            CloseButtonText = RenameStrings.ButtonCancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root.XamlRoot,
        };
        void Validate()
        {
            errors.Children.Clear();
            foreach (var message in RenameSupport.Messages(field.Text))
                errors.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Colors.Red) });
            dialog.IsPrimaryButtonEnabled = RenameSupport.IsValid(field.Text);
        }
        field.TextChanged += (_, _) => Validate();
        Validate();
        dialog.Opened += (_, _) => { field.Focus(FocusState.Programmatic); field.SelectAll(); };
        dialogOpen = true;
        try
        {
            var result = smokeAskName is { } driver
                ? await driver(dialog, field, errors)
                : await dialog.ShowAsync();
            return result == ContentDialogResult.Primary ? RenameSupport.DisplayName(field.Text) : null;
        }
        finally { dialogOpen = false; }
    }
    private Task RenameWorkspace(string id) => Act(async () =>
    {
        if (service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == id) is not { } workspace) return;
        if (await AskName(RenameStrings.HeadingWorkspace, RenameStrings.HintWorkspace, workspace.Name) is not { } name) return;
        await service.RenameWorkspaceAsync(id, name);
        Render();
    });
    private Task RenameSession(string id) => Act(async () =>
    {
        if (service.Snapshot.Sessions.FirstOrDefault(p => p.Id == id) is not { } session) return;
        if (await AskName(RenameStrings.HeadingSession, RenameStrings.HintSession, session.Title) is not { } name) return;
        await service.RenameSessionAsync(id, name);
        Render();
    });
}
