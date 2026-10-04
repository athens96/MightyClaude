using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, (StatusMark Mark, TextBlock Status, TextBlock Time)> sessionIndicators = [];
    private readonly Dictionary<string, (StatusMark Mark, TextBlock Status, TextBlock Time)> tabIndicators = [];
    /// <summary>A status's word, the same one the glyph names (StatusGlyph.WordKey).</summary>
    private static string StateLabel(string state) => Locale.Get(StatusGlyph.WordKey(StatusGlyph.Tone(state)));
    /// <summary>The pane's tool requests still waiting on the user; they turn its mark into the amber "?".</summary>
    private int PendingRequests(string id) => views.TryGetValue(id, out var pane) ? pane.PendingRequests : 0;
    /// <summary>The theme Render last applied; read per row without copying the snapshot.</summary>
    private bool DarkTheme => darkTheme;
    private bool darkTheme = true;
    /// <summary>
    /// A sidebar row or tab: the status glyph (design A) before the title, then the state word
    /// and the run clock. The glyph replaces the spinning ring; no row is filled with a status colour.
    /// </summary>
    private FrameworkElement SessionIndicator(RunSession session, bool tab = false)
    {
        var row = new Grid { ColumnSpacing = 6, VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var pending = PendingRequests(session.Id); var shown = StatusGlyph.DisplayStatus(session.Status, pending);
        var mark = new StatusMark(); mark.Update(session.Status, session.Kind, pending, DarkTheme);
        row.Children.Add(mark.View);
        var title = new TextBlock { Text = session.Title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        // A Codex or Gemini agent pane carries the 베타 capsule right after its title (macOS paneRow / PaneDockView).
        var titleLine = SessionTitle(session, title, tab); Grid.SetColumn(titleLine, 1); row.Children.Add(titleLine);
        // An automatic agent title is the request cut to 40 characters; hovering shows it whole (macOS titleHelp).
        ToolTipService.SetToolTip(title, PaneTitle.Help(session));
        var state = new TextBlock { Text = StateLabel(shown), FontSize = 10, Opacity = .65 };
        var elapsed = new TextBlock { Text = session.Kind == "shell" ? "" : session.RunTiming?.Label() ?? "", FontSize = 10, Opacity = .7 };
        // Sidebar metadata already contains the elapsed time below the title.
        if (!tab) elapsed.Visibility = Visibility.Collapsed;
        var trailing = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center }; if (!tab) trailing.Children.Add(state); trailing.Children.Add(elapsed); Grid.SetColumn(trailing, 2); row.Children.Add(trailing);
        // An agent's sidebar row names its provider on a muted second line, its mark first
        // (macOS paneMeta: "[mark] Claude"); the glyph and the trailing state span both lines.
        if (!tab && SidebarProviderLine(session) is { } providerLine)
        {
            row.RowDefinitions.Add(new() { Height = GridLength.Auto }); row.RowDefinitions.Add(new() { Height = GridLength.Auto }); row.RowSpacing = 1;
            Grid.SetRowSpan(mark.View, 2); Grid.SetRowSpan(trailing, 2);
            Grid.SetRow(providerLine, 1); Grid.SetColumn(providerLine, 1); row.Children.Add(providerLine);
        }
        (tab ? tabIndicators : sessionIndicators)[session.Id] = (mark, state, elapsed);
        AutomationProperties.SetName(row, session.Title + (ProviderCatalog.ShowsBetaBadge(session) ? ", " + Locale.Get("badge.betaAccessibility") : "") + ", " + StateLabel(shown)); return row;
    }
    private void RefreshRunningIndicators()
    {
        if (closing) return;
        // One snapshot copy per tick: the theme and every session come from it.
        var state = service.Snapshot; var dark = state.Theme != "light";
        foreach (var pair in workspaceStatusCounts) UpdateWorkspaceStatusCounts(pair.Key, pair.Value);
        if (workspaceHeaderId is { } headerWorkspace) UpdateWorkspaceStatusCounts(headerWorkspace, workspaceHeaderCounts);
        foreach (var session in state.Sessions)
        {
            var pending = PendingRequests(session.Id);
            if (sidebarSessionButtons.TryGetValue(session.Id, out var sidebarButton))
                AutomationProperties.SetName(sidebarButton, session.Title + (ProviderCatalog.ShowsBetaBadge(session) ? ", " + Locale.Get("badge.betaAccessibility") : "") + ", " + StateLabel(StatusGlyph.DisplayStatus(session.Status, pending)));
            if (sidebarDetails.TryGetValue(session.Id, out var meta)) meta.Text = WorkDashboard.SidebarDetail(WorkDashboard.MakeCard(session, DashboardAttention(session.Id)), DateTimeOffset.UtcNow);
            foreach (var values in new[] { sessionIndicators, tabIndicators })
            {
                if (!values.TryGetValue(session.Id, out var view)) continue;
                view.Mark.Update(session.Status, session.Kind, pending, dark);
                view.Status.Text = StateLabel(StatusGlyph.DisplayStatus(session.Status, pending)); view.Time.Text = session.Kind == "shell" ? "" : session.RunTiming?.Label() ?? "";
            }
            if (views.TryGetValue(session.Id, out var pane)) { pane.RefreshElapsed(session); pane.RefreshHeaderStatus(session, dark); }
        }
        RenderDashboard();
    }
    private Task SelectWorkspace(string id) => Act(async () =>
    {
        if (!service.Snapshot.Workspaces.Any(workspace => workspace.Id == id)) return;
        HideDashboard();
        await service.UpdateAsync(s =>
        {
            if (!s.Workspaces.Any(workspace => workspace.Id == id)) return s;
            var preferred = s.PaneLayoutActiveSessionIds?.GetValueOrDefault(id);
            var selected = s.Sessions.FirstOrDefault(p => p.WorkspaceId == id && p.Id == preferred)?.Id ?? PaneLayout.Groups(EffectiveLayout(s, id)).Select(g => g.SelectedSessionId).FirstOrDefault();
            return WorkspaceDisclosure.Open(s, id) with { ActiveWorkspaceId = id, ActiveSessionId = selected };
        });
        Render();
        await RefreshRuntime();
    });
    private static MenuFlyoutItem MenuItem(string text, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = text }; item.Click += async (_, _) => await action(); return item;
    }
    private MenuFlyout NewSessionMenu(string? groupId = null)
    {
        // The macOS "창 추가" order (AddPaneMenu.Entries): agents, terminal, browser and files,
        // then 프로젝트 폴더 열기… at the bottom.
        var menu = new MenuFlyout();
        foreach (var entry in AddPaneMenu.Entries())
            menu.Items.Add(entry switch
            {
                AddPaneMenu.Separator => (MenuFlyoutItemBase)new MenuFlyoutSeparator(),
                AddPaneMenu.Shell => MenuItem(Locale.Get("session.newTab.shell"), () => AddPane("shell", groupId: groupId)),
                AddPaneMenu.Browser => MenuItem(Locale.Get("browser.newTab"), () => AddBrowserPane(groupId)),
                AddPaneMenu.Files => OpenFilesMenuItem(),
                AddPaneMenu.OpenProject => OpenProjectMenuItem(),
                _ => AddPaneMenu.AgentProvider(entry) is { } provider
                    ? MenuItem(ProviderCatalog.BetaLabel(provider, ProviderCatalog.Name(provider)), () => AddAgentPane(provider, groupId))
                    : throw new InvalidOperationException("unknown add-pane menu entry " + entry),
            });
        return menu;
    }
    private MenuFlyoutItem OpenProjectMenuItem()
    {
        var item = MenuItem(Locale.Get(AddPaneMenu.OpenProjectKey), PickFolder);
        item.KeyboardAcceleratorTextOverride = AddPaneMenu.OpenFolderShortcut;
        item.Icon = new FontIcon { Glyph = "" };
        AutomationProperties.SetAutomationId(item, "add-pane-open-folder");
        return item;
    }
    /// <summary>
    /// Ctrl+O opens a project folder and Ctrl+N adds a Claude pane at once (macOS ⌘O, ⌘N):
    /// the shortcut never asks 새로 시작 / 이어가기, as on macOS.
    /// </summary>
    private void InitAddPaneShortcuts()
    {
        var open = new KeyboardAccelerator { Key = VirtualKey.O, Modifiers = VirtualKeyModifiers.Control };
        open.Invoked += async (_, args) => { args.Handled = true; if (!dialogOpen) await PickFolder(); };
        var add = new KeyboardAccelerator { Key = VirtualKey.N, Modifiers = VirtualKeyModifiers.Control };
        add.Invoked += async (_, args) => { args.Handled = true; await AddPaneFromShortcut(); };
        root.KeyboardAccelerators.Add(open); root.KeyboardAccelerators.Add(add);
    }
    private async Task AddPaneFromShortcut()
    {
        if (dialogOpen || service.Snapshot.ActiveWorkspaceId is null) return;
        await AddPane("claude", AddPaneMenu.NewPaneShortcutProvider);
    }
    internal bool HasAddPaneShortcuts => new[] { VirtualKey.O, VirtualKey.N }.All(key => root.KeyboardAccelerators.Any(a => a.Key == key && a.Modifiers == VirtualKeyModifiers.Control));
    private MenuFlyout WorkspaceMenu(string id)
    {
        var rename = new MenuFlyoutItem { Text = RenameStrings.MenuEntry };
        rename.Click += async (_, _) => await RenameWorkspace(id);
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => rename.IsEnabled = !dialogOpen;
        menu.Items.Add(rename);
        menu.Items.Add(OpenFilesMenuItem(id));
        menu.Items.Add(MenuItem(Locale.Get("workspace.menu.remove"), () => ConfirmRemoveWorkspace(id)));
        menu.Items.Add(MenuItem(Locale.Get("menu.showInExplorer"), () => Act(() =>
        {
            var workspace = service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == id);
            if (workspace is not null && WorkspaceFiles.RealPath(workspace.Path) is { } path && Directory.Exists(path))
            {
                var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
                start.ArgumentList.Add(path); using var process = System.Diagnostics.Process.Start(start);
            }
            return Task.CompletedTask;
        })));
        return menu;
    }
    private MenuFlyout SessionMenu(string id)
    {
        var rename = new MenuFlyoutItem { Text = RenameStrings.MenuEntry };
        rename.Click += async (_, _) => await RenameSession(id);
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => rename.IsEnabled = !dialogOpen;
        menu.Items.Add(rename);
        menu.Items.Add(MenuItem(Locale.Get("session.menu.focus"), () => Act(async () => { await SelectLayoutSession(id); await ApplyLayoutPreset(LayoutMode(service.Snapshot, service.Snapshot.ActiveWorkspaceId) == "focus" ? "custom" : "focus"); })));
        if (service.Snapshot.Sessions.FirstOrDefault(p => p.Id == id)?.Kind == "shell")
            menu.Items.Add(MenuItem(Locale.Get("terminal.history.title"), () => Act(() => ShowTerminalHistory(id))));
        menu.Items.Add(MenuItem(Locale.Get("session.menu.close"), () => CloseSession(id)));
        return menu;
    }
    private Task CloseSession(string id) => Act(async () => { await service.StopAsync(id); await service.UpdateAsync(s => s with { Sessions = s.Sessions.Where(p => p.Id != id).ToList() }); Render(); });
    /// <summary>
    /// The rename dialog of RenameViews.swift: the current name selected, the macOS captions under
    /// the field and 저장 disabled while the name is invalid. Every literal comes from RenameStrings
    /// and every rule from RenameSupport, so Windows and macOS accept and refuse the same names.
    /// An agent pane also offers 자동 (pane.rename.automatic): its title follows its latest request again.
    /// </summary>
    private async Task<(bool Automatic, string? Name)> RenameDialog(string heading, string hint, string current, bool offerAutomatic = false)
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
        if (offerAutomatic) dialog.SecondaryButtonText = Locale.Get("pane.rename.automatic");
        void Validate()
        {
            errors.Children.Clear();
            foreach (var message in RenameSupport.Messages(field.Text))
                errors.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Colors.Red) });
            dialog.IsPrimaryButtonEnabled = RenameSupport.IsValid(field.Text);
        }
        // TextChanged is raised by the realised text editor, so it stays silent while the dialog is
        // not in the visual tree. Watching the Text property covers typing and programmatic edits alike.
        field.RegisterPropertyChangedCallback(TextBox.TextProperty, (_, _) => Validate());
        Validate();
        dialog.Opened += (_, _) => { field.Focus(FocusState.Programmatic); field.SelectAll(); };
        dialogOpen = true;
        try
        {
            var result = smokeAskName is { } driver
                ? await driver(dialog, field, errors)
                : await dialog.ShowAsync();
            if (offerAutomatic && result == ContentDialogResult.Secondary) return (true, null);
            return (false, result == ContentDialogResult.Primary ? RenameSupport.DisplayName(field.Text) : null);
        }
        finally { dialogOpen = false; }
    }
    private Task RenameWorkspace(string id) => Act(async () =>
    {
        if (service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == id) is not { } workspace) return;
        if ((await RenameDialog(RenameStrings.HeadingWorkspace, RenameStrings.HintWorkspace, workspace.Name)).Name is not { } name) return;
        await service.RenameWorkspaceAsync(id, name);
        Render();
    });
    private Task RenameSession(string id) => Act(async () =>
    {
        if (service.Snapshot.Sessions.FirstOrDefault(p => p.Id == id) is not { } session) return;
        // Renaming fixes the title; 자동 hands it back to the latest request (macOS RenameViews.swift).
        var (automatic, name) = await RenameDialog(RenameStrings.HeadingSession, RenameStrings.HintSession, session.Title, offerAutomatic: session.Kind == "claude");
        if (automatic) await service.SetSessionAutoTitleAsync(id);
        else if (name is not null) await service.RenameSessionAsync(id, name);
        else return;
        Render();
    });
}
