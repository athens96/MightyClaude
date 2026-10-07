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
    private readonly Dictionary<string, StatusMark> tabIndicators = [];
    /// <summary>A status's word, the same one the glyph names (StatusGlyph.WordKey).</summary>
    private static string StateLabel(string state) => Locale.Get(StatusGlyph.WordKey(StatusGlyph.Tone(state)));
    /// <summary>The pane's tool requests still waiting on the user; they turn its mark into the amber "?".</summary>
    private int PendingRequests(string id) => views.TryGetValue(id, out var pane) ? pane.PendingRequests : 0;
    /// <summary>The theme Render last applied; read per row without copying the snapshot.</summary>
    private bool DarkTheme => darkTheme;
    private bool darkTheme = true;
    /// <summary>
    /// A sidebar row, the Mac's paneRow (M/WorkspaceView.swift:214-277): the status glyph (design A)
    /// at the top, the 12.5pt title (medium once the pane has settled, semibold otherwise) over the
    /// meta line, and on the right only the amber words of what waits on the user ("1 question");
    /// <paramref name="active"/> is the selected row, whose meta line is <c>ink2</c>. No row is
    /// filled with a status colour. Tabs are <see cref="TabIndicator"/>.
    /// </summary>
    private FrameworkElement SessionIndicator(RunSession session, bool active = false)
    {
        var row = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm, VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var pending = PendingRequests(session.Id); var shown = StatusGlyph.DisplayStatus(session.Status, pending);
        var mark = new StatusMark(); mark.Update(session.Status, session.Kind, pending, DarkTheme);
        mark.View.VerticalAlignment = VerticalAlignment.Top; mark.View.Margin = new Thickness(0, 1.5, 0, 0);
        row.Children.Add(mark.View);
        var title = new TextBlock { Text = session.Title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = DesignMetrics.Type.SideRow, FontWeight = SidebarTitleWeight(shown), Foreground = brushes.Brush(DesignToken.Ink), MinHeight = 17 };
        sidebarTitles[session.Id] = title;
        // A Codex or Gemini agent pane carries the beta capsule right after its title (macOS paneRow).
        var titleLine = SessionTitle(session, title, false); Grid.SetColumn(titleLine, 1); row.Children.Add(titleLine);
        // An automatic agent title is the request cut to 40 characters; hovering shows it whole (macOS titleHelp).
        ToolTipService.SetToolTip(title, PaneTitle.Help(session));
        // The Mac's Spacer stands between the words and the title, 8 from each (M/WorkspaceView.swift:222-245).
        var state = new TextBlock { FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = brushes.Brush(DesignToken.WaitText), MinHeight = 17, Margin = new Thickness(DesignMetrics.Spacing.Sm, 0, 0, 0) };
        ShowSidebarWait(state, DashboardAttention(session.Id));
        // Sidebar metadata already contains the elapsed time below the title.
        var elapsed = new TextBlock { Text = session.Kind == "shell" ? "" : session.RunTiming?.Label() ?? "", FontSize = 10, Opacity = .7, Visibility = Visibility.Collapsed };
        var trailing = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Top }; trailing.Children.Add(state); trailing.Children.Add(elapsed); Grid.SetColumn(trailing, 2); row.Children.Add(trailing);
        // An agent's row names its provider on a muted second line, its mark first (macOS paneMeta:
        // "[mark] Claude"); any other pane names its kind there (macOS DashboardText.kindLine). The
        // glyph spans both lines.
        if ((SidebarProviderLine(session, active) ?? SidebarKindLine(session, active)) is { } providerLine)
        {
            row.RowDefinitions.Add(new() { Height = GridLength.Auto }); row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            Grid.SetRowSpan(mark.View, 2);
            Grid.SetRow(providerLine, 1); Grid.SetColumn(providerLine, 1); row.Children.Add(providerLine);
        }
        // The selected row's hairline is an overlay over the row's padding, as on the Mac
        // (M/WorkspaceView.swift:526), so it takes no room from the row at any display scale.
        var edge = new Border
        {
            BorderThickness = new Thickness(DesignMetrics.Stroke.Hairline), BorderBrush = active ? brushes.RowSelectedBorder : brushes.Transparent, CornerRadius = new CornerRadius(DesignMetrics.Radius.Row),
            Margin = new Thickness(-SidebarPaneRowPadding.Left, -SidebarPaneRowPadding.Top, -SidebarPaneRowPadding.Right, -SidebarPaneRowPadding.Bottom), IsHitTestVisible = false,
        };
        AutomationProperties.SetAccessibilityView(edge, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Grid.SetColumnSpan(edge, 3); Grid.SetRowSpan(edge, 2); row.Children.Add(edge);
        sidebarRowEdges[session.Id] = edge;
        sessionIndicators[session.Id] = (mark, state, elapsed);
        AutomationProperties.SetName(row, session.Title + (ProviderCatalog.ShowsBetaBadge(session) ? ", " + Locale.Get("badge.betaAccessibility") : "") + ", " + StateLabel(shown)); return row;
    }
    /// <summary>A sidebar pane row's padding: <c>Spacing.Sm</c> at the sides, <c>Inset.PaneRowV</c> over and under (M/WorkspaceView.swift:260).</summary>
    internal static readonly Thickness SidebarPaneRowPadding = new(DesignMetrics.Spacing.Sm, DesignMetrics.Inset.PaneRowV, DesignMetrics.Spacing.Sm, DesignMetrics.Inset.PaneRowV);
    /// <summary>Each sidebar pane row's hairline overlay, by session id.</summary>
    private readonly Dictionary<string, Border> sidebarRowEdges = [];

    /// <summary>
    /// A tab's content, the Mac's PaneDockTab order (M/PaneDockView.swift:238-250): the 10pt kind
    /// symbol or agent mark, the 11pt title (semibold and <c>ink</c> on the selected tab, else regular
    /// <c>ink2</c>, at most 125 wide), the beta capsule, and last the 12pt status mark, which shows
    /// only for a counted pane that is not idle (<see cref="ShowTabMark"/>). No state word or clock.
    /// </summary>
    private StackPanel TabIndicator(RunSession session, bool selected)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock
        {
            Text = session.Title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 125,
            FontSize = DesignMetrics.Type.Pill, FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = brushes.Brush(selected ? DesignToken.Ink : DesignToken.Ink2),
        };
        ToolTipService.SetToolTip(title, PaneTitle.Help(session));
        tabTitles[session.Id] = title;
        row.Children.Add(TabKindIcon(session, title.Foreground)); row.Children.Add(SessionTitle(session, title, true));
        var pending = PendingRequests(session.Id);
        var mark = new StatusMark(StatusGlyph.TabSize); mark.Update(session.Status, session.Kind, pending, DarkTheme);
        ShowTabMark(mark, session, pending);
        row.Children.Add(mark.View);
        tabIndicators[session.Id] = mark;
        AutomationProperties.SetName(row, session.Title + (ProviderCatalog.ShowsBetaBadge(session) ? ", " + Locale.Get("badge.betaAccessibility") : "") + ", " + StateLabel(StatusGlyph.DisplayStatus(session.Status, pending)));
        return row;
    }

    /// <summary>A tab's mark shows for a counted pane that is not idle; an agent's terminal or browser and the files pane carry none (M/PaneDockView.swift:228-234).</summary>
    private static void ShowTabMark(StatusMark mark, RunSession session, int pending) =>
        mark.View.Visibility = WorkDashboard.IsCounted(session.Kind) && StatusGlyph.Tone(StatusGlyph.DisplayStatus(session.Status, pending)) != DesignTone.Idle ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>
    /// The meta line of a pane that is not an agent's: its kind in 11pt, <c>ink2</c> on the selected
    /// row and <c>sidebarInk2</c> otherwise (macOS DashboardText.kindLine). A Windows terminal pane is
    /// always a local terminal, as every Mac shell pane is outside its smoke run.
    /// </summary>
    private TextBlock SidebarKindLine(RunSession session, bool active)
    {
        var words = session.Kind switch
        {
            "shell" => Locale.Get("dashboard.kind.shell") + " · " + Locale.Get("phone.card.localTerminal"),
            "browser" => Locale.Get("browser.tab.title"),
            AgentIOPaneKind.Terminal => Locale.Get("dashboard.kind.agentTerminal"),
            AgentIOPaneKind.Browser => Locale.Get("dashboard.kind.agentBrowser"),
            FilePaneKind.Kind => Locale.Get("files.pane.title"),
            "claude" => Locale.Get("dashboard.kind.agent"),
            _ => session.Kind,
        };
        var line = new TextBlock { Text = words, FontSize = DesignMetrics.Type.Pill, Foreground = brushes.Brush(active ? DesignToken.Ink2 : DesignToken.SidebarInk2), TextTrimming = TextTrimming.CharacterEllipsis, MinHeight = 15, VerticalAlignment = VerticalAlignment.Center };
        sidebarKindLines[session.Id] = line;
        return line;
    }
    /// <summary>Each tab's title, by session id.</summary>
    private readonly Dictionary<string, TextBlock> tabTitles = [];
    /// <summary>
    /// The 10pt symbol before a tab's title (M/PaneDockView.swift:241-246): a terminal, a globe or a
    /// folder for those panes (<see cref="PaneSymbol"/>), else the agent's mark, which the Mac draws
    /// in a frame 1.15 times its size (M/ProviderIcon.swift:18).
    /// </summary>
    private static FrameworkElement TabKindIcon(RunSession session, Brush ink) =>
        session.Kind is "shell" or "browser" or FilePaneKind.Kind || AgentIOPaneKind.IsAgentIO(session.Kind) ? PaneSymbol(session.Kind, 10, ink) : ProviderMarkView.Create(session.Provider, 10 * 1.15);
    /// <summary>The kind line of each sidebar row that is not an agent's, by session id.</summary>
    private readonly Dictionary<string, TextBlock> sidebarKindLines = [];
    /// <summary>A sidebar title's weight: medium once the pane is done, stopped or idle, semibold while it runs, waits or failed.</summary>
    private static Windows.UI.Text.FontWeight SidebarTitleWeight(string shownStatus) =>
        StatusGlyph.Tone(shownStatus) is DesignTone.Done or DesignTone.Stop or DesignTone.Idle ? Microsoft.UI.Text.FontWeights.Medium : Microsoft.UI.Text.FontWeights.SemiBold;
    /// <summary>The words of what waits on the user (macOS DashboardText.status): questions first, then permissions; null when nothing waits.</summary>
    private static string? SidebarWaitText(WorkDashboard.Attention attention) =>
        attention.Questions > 0 ? Locale.Get("phone.card.questions", new Dictionary<string, string> { ["count"] = attention.Questions.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        : attention.Permissions > 0 ? Locale.Get("phone.card.permissions", new Dictionary<string, string> { ["count"] = attention.Permissions.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        : null;
    /// <summary>Shows a sidebar row's amber waiting words, or collapses them while nothing waits.</summary>
    private static void ShowSidebarWait(TextBlock label, WorkDashboard.Attention attention)
    {
        var text = SidebarWaitText(attention);
        label.Text = text ?? ""; label.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private void RefreshRunningIndicators()
    {
        if (closing) return;
        // One snapshot copy per tick: the theme and every session come from it.
        var state = service.Snapshot; var dark = state.Theme != "light";
        RefreshSidebarStatusCounts(state, dark);
        RefreshWorkspaceHeaderCounts(state, dark); RefreshStatusBar();
        foreach (var session in state.Sessions)
        {
            var pending = PendingRequests(session.Id); var shown = StatusGlyph.DisplayStatus(session.Status, pending);
            if (sidebarSessionButtons.TryGetValue(session.Id, out var sidebarButton))
                AutomationProperties.SetName(sidebarButton, session.Title + (ProviderCatalog.ShowsBetaBadge(session) ? ", " + Locale.Get("badge.betaAccessibility") : "") + ", " + StateLabel(shown));
            if (sidebarDetails.TryGetValue(session.Id, out var meta)) meta.Text = SidebarMeta(session);
            if (sidebarTitles.TryGetValue(session.Id, out var title)) title.FontWeight = SidebarTitleWeight(shown);
            if (sessionIndicators.TryGetValue(session.Id, out var row))
            {
                row.Mark.Update(session.Status, session.Kind, pending, dark);
                ShowSidebarWait(row.Status, DashboardAttention(session.Id));
            }
            if (tabIndicators.TryGetValue(session.Id, out var tab)) { tab.Update(session.Status, session.Kind, pending, dark); ShowTabMark(tab, session, pending); }
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
        // The macOS add-pane order (AddPaneMenu.Entries): agents, terminal, browser and files,
        // then the open-project-folder item at the bottom.
        var menu = new MenuFlyout();
        foreach (var entry in AddPaneMenu.Entries())
            menu.Items.Add(entry switch
            {
                AddPaneMenu.Separator => (MenuFlyoutItemBase)new MenuFlyoutSeparator(),
                AddPaneMenu.Shell => MenuItem(AddPaneTitle(entry)!, () => AddPane("shell", groupId: groupId)),
                AddPaneMenu.Browser => MenuItem(AddPaneTitle(entry)!, () => AddBrowserPane(groupId)),
                AddPaneMenu.Files => OpenFilesMenuItem(),
                AddPaneMenu.OpenProject => OpenProjectMenuItem(),
                _ => AddPaneMenu.AgentProvider(entry) is { } provider
                    ? MenuItem(AddPaneTitle(entry)!, () => AddAgentPane(provider, groupId))
                    : throw new InvalidOperationException("unknown add-pane menu entry " + entry),
            });
        MarkAddPaneMenu(menu, AddPaneMenu.Entries());
        return menu;
    }

    /// <summary>
    /// An add-pane entry's words as the Mac's menu says them (M/WorkspaceView.swift:423-445): "New Claude
    /// pane", "New Codex pane" with its beta suffix, "New terminal", "New browser tab"; null for an entry
    /// that keeps its own words (the files pane, the project folder).
    /// </summary>
    private static string? AddPaneTitle(string entry) => entry switch
    {
        AddPaneMenu.Shell => Locale.Get("workspace.newTerminal"),
        AddPaneMenu.Browser => Locale.Get("browser.newTab"),
        _ => AddPaneMenu.AgentProvider(entry) is { } provider
            ? ProviderCatalog.BetaLabel(provider, Locale.Get("workspace.newAgentPane", new Dictionary<string, string> { ["provider"] = ProviderMark.Label(provider) }))
            : null,
    };

    /// <summary>
    /// The add-pane menu as the Mac draws it (M/WorkspaceView.swift:418-453), for every menu built from
    /// <see cref="AddPaneMenu.Entries"/> (the sidebar's add row, a tab group's +, the dashboard's capsule):
    /// each entry's Mac words (<see cref="AddPaneTitle"/>) and its mark at 12: each agent's own mark, a
    /// terminal, a globe and the files pane's folder; an item that brings its own symbol keeps it.
    /// <paramref name="entries"/> lines up with the menu's items.
    /// </summary>
    private void MarkAddPaneMenu(MenuFlyout menu, IReadOnlyList<string> entries)
    {
        for (var i = 0; i < menu.Items.Count && i < entries.Count; i++)
        {
            if (menu.Items[i] is not MenuFlyoutItem item) continue;
            if (AddPaneTitle(entries[i]) is { } title) item.Text = title;
            if (item.Icon is not null) continue;
            item.Icon = entries[i] switch
            {
                AddPaneMenu.Shell => new FontIcon { Glyph = "\uE756", FontSize = 12 },
                AddPaneMenu.Browser => new FontIcon { Glyph = "\uE774", FontSize = 12 },
                AddPaneMenu.Files => new FontIcon { Glyph = "\uE8B7", FontSize = 12 },
                var entry when AddPaneMenu.AgentProvider(entry) is { } provider => ProviderMarkView.MenuIcon(provider, brushes),
                _ => null,
            };
        }
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
    /// the shortcut never asks start-new or resume, as on macOS.
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
    /// <summary>
    /// A workspace row's menu in the Mac's order (M/WorkspaceView.swift:193-197): Rename…, Remove from
    /// list, Show in File Explorer; then, behind a separator, the Windows-only way into its files pane.
    /// </summary>
    private MenuFlyout WorkspaceMenu(string id)
    {
        var rename = new MenuFlyoutItem { Text = RenameStrings.MenuEntry };
        rename.Click += async (_, _) => await RenameWorkspace(id);
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => rename.IsEnabled = !dialogOpen;
        menu.Items.Add(rename);
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
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(OpenFilesMenuItem(id));
        return menu;
    }
    /// <summary>
    /// A sidebar pane row's menu, the Mac's two items (M/WorkspaceView.swift:259-262): Rename… and Close
    /// pane. Focus view and a terminal's history stay in the pane's own menus (<see cref="SessionMenu(string)"/>).
    /// </summary>
    private MenuFlyout SidebarPaneMenu(string id)
    {
        var rename = new MenuFlyoutItem { Text = RenameStrings.MenuEntry };
        rename.Click += async (_, _) => await RenameSession(id);
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => rename.IsEnabled = !dialogOpen;
        menu.Items.Add(rename);
        menu.Items.Add(MenuItem(Locale.Get("menu.closePane"), () => CloseSession(id)));
        return menu;
    }
    private MenuFlyout SessionMenu(string id) => SessionMenu(id, out _);
    /// <summary>The pane's menu; <paramref name="close"/> is its last item (Close), for callers that add items before it.</summary>
    private MenuFlyout SessionMenu(string id, out MenuFlyoutItem close)
    {
        var rename = new MenuFlyoutItem { Text = RenameStrings.MenuEntry };
        rename.Click += async (_, _) => await RenameSession(id);
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => rename.IsEnabled = !dialogOpen;
        menu.Items.Add(rename);
        menu.Items.Add(MenuItem(Locale.Get("session.menu.focus"), () => Act(async () => { await SelectLayoutSession(id); await ApplyLayoutPreset(LayoutMode(service.Snapshot, service.Snapshot.ActiveWorkspaceId) == "focus" ? "custom" : "focus"); })));
        if (service.Snapshot.Sessions.FirstOrDefault(p => p.Id == id)?.Kind == "shell")
            menu.Items.Add(MenuItem(Locale.Get("terminal.history.title"), () => Act(() => ShowTerminalHistory(id))));
        menu.Items.Add(close = MenuItem(Locale.Get("session.menu.close"), () => CloseSession(id)));
        return menu;
    }
    private Task CloseSession(string id) => Act(async () => { await service.StopAsync(id); await service.UpdateAsync(s => s with { Sessions = s.Sessions.Where(p => p.Id != id).ToList() }); Render(); });
    /// <summary>The rename sheet's width, the spacing of its lines, its caption size and its padding (M/RenameViews.swift:30, 38, 59; SwiftUI's caption is 10pt on macOS).</summary>
    internal const double RenameSheetWidth = 360, RenameSpacing = DesignMetrics.Spacing.Md, RenameCaption = 10, RenameSheetPadding = DesignMetrics.Inset.Sheet + DesignMetrics.Spacing.Xxs;
    /// <summary>
    /// The rename sheet of RenameViews.swift:29-63, 360 wide in <see cref="RenameSheetPadding"/>: the 13pt bold heading, the
    /// name field (its label the placeholder, the current name selected), the caption under it in 10pt
    /// <c>ink2</c>, each refusal in 10pt <c>errText</c> and the sheet's own row of buttons, 14 apart —
    /// Automatic at the leading edge for an agent pane (pane.rename.automatic: its title follows its
    /// latest request again), Cancel and the accent Save at the trailing edge, each as wide as its
    /// words. Save is disabled while the name is invalid; Return in the field saves and Esc cancels.
    /// Every literal comes from RenameStrings and every rule from RenameSupport, so Windows and macOS
    /// accept and refuse the same names.
    /// </summary>
    private async Task<(bool Automatic, string? Name)> RenameDialog(string heading, string hint, string current, bool offerAutomatic = false)
    {
        // One sheet at a time: a rename asked for while Settings or another sheet is open (a tab's double-click, /rename) is not started.
        if (dialogOpen) return (false, null);
        var title = new TextBlock { Text = heading, FontSize = DesignMetrics.Type.Title, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = brushes.Brush(DesignToken.Ink), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        // The field's label is its placeholder, in AppKit's tertiary ink (M/RenameViews.swift:32).
        var field = new TextBox { PlaceholderText = RenameStrings.FieldLabel, Text = current, FontSize = DesignMetrics.Type.Body, PlaceholderForeground = brushes.Tertiary };
        AutomationProperties.SetName(field, RenameStrings.FieldLabel); AutomationProperties.SetAutomationId(field, "rename-name-field");
        var hintText = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, FontSize = RenameCaption, Foreground = brushes.Brush(DesignToken.Ink2) };
        var errors = new StackPanel { Spacing = RenameSpacing, Visibility = Visibility.Collapsed };
        // What the sheet's own buttons answer; the dialog itself has none, so it closes with None.
        var picked = ContentDialogResult.None; ContentDialog? sheet = null;
        Button Choice(string words, string id, ContentDialogResult answer, int column)
        {
            var button = new Button { Content = words, FontSize = DesignMetrics.Type.Body };
            button.Click += (_, _) => { picked = answer; sheet?.Hide(); };
            AutomationProperties.SetAutomationId(button, id); Grid.SetColumn(button, column);
            return button;
        }
        var buttons = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
        buttons.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); buttons.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); buttons.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        if (offerAutomatic) buttons.Children.Add(Choice(Locale.Get("pane.rename.automatic"), RenameAutomaticId, ContentDialogResult.Secondary, 0));
        buttons.Children.Add(Choice(RenameStrings.ButtonCancel, "rename-cancel", ContentDialogResult.None, 2));
        var save = Choice(RenameStrings.ButtonSave, "rename-save", ContentDialogResult.Primary, 3);
        save.Style = (Style)Application.Current.Resources["AccentButtonStyle"]; save.CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment);
        buttons.Children.Add(save);
        var content = new StackPanel { Spacing = RenameSpacing, Margin = new Thickness(RenameSheetPadding - SheetPadding), Children = { title, field, hintText, errors, buttons } };
        // The sheet follows the window's theme, not the system's: its field and buttons are stock controls in a popup.
        var dialog = sheet = StyledDialog(new ContentDialog { Content = content, XamlRoot = root.XamlRoot, RequestedTheme = root.RequestedTheme }, RenameSheetWidth);
        // No command row under the sheet's own buttons: no line over one and no height kept for one.
        SetResourcesOnce(dialog, [("ContentDialogSeparatorThickness", new Thickness(0)), ("ContentDialogMinHeight", 0d)]);
        AutomationProperties.SetAutomationId(dialog, "rename-sheet"); AutomationProperties.SetName(dialog, heading);
        void Validate()
        {
            errors.Children.Clear();
            foreach (var message in RenameSupport.Messages(field.Text))
                errors.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = RenameCaption, Foreground = brushes.Brush(DesignToken.ErrText) });
            // An empty list takes no room, so the 14pt spacing stands only between lines that show.
            errors.Visibility = errors.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            dialog.IsPrimaryButtonEnabled = save.IsEnabled = RenameSupport.IsValid(field.Text);
        }
        // TextChanged is raised by the realised text editor, so it stays silent while the dialog is
        // not in the visual tree. Watching the Text property covers typing and programmatic edits alike.
        field.RegisterPropertyChangedCallback(TextBox.TextProperty, (_, _) => Validate());
        Validate();
        // Return in the field saves, as the Mac's onSubmit and default action do (M/RenameViews.swift:35, 54).
        field.KeyDown += (_, args) => { if (args.Key != VirtualKey.Enter || !save.IsEnabled) return; args.Handled = true; picked = ContentDialogResult.Primary; dialog.Hide(); };
        dialog.Opened += (_, _) => { field.Focus(FocusState.Programmatic); field.SelectAll(); };
        dialogOpen = true;
        try
        {
            // A smoke driver answers in the buttons' place.
            var result = smokeAskName is { } driver
                ? await driver(dialog, field, errors)
                : await dialog.ShowAsync();
            if (result == ContentDialogResult.None) result = picked;
            if (offerAutomatic && result == ContentDialogResult.Secondary) return (true, null);
            return (false, result == ContentDialogResult.Primary ? RenameSupport.DisplayName(field.Text) : null);
        }
        finally { dialogOpen = false; }
    }
    /// <summary>The Automatic button's id in the rename sheet, the Mac's (M/RenameViews.swift:49).</summary>
    internal const string RenameAutomaticId = "rename-automatic";
    /// <summary>The rename sheet's buttons, in the order they stand: Automatic (an agent pane's only), Cancel, Save.</summary>
    internal static IReadOnlyList<Button> RenameButtons(ContentDialog sheet) =>
        sheet.Content is StackPanel { Children: [.., Grid row] } ? row.Children.OfType<Button>().ToList() : [];
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
        // Renaming fixes the title; Automatic hands it back to the latest request (macOS RenameViews.swift).
        var (automatic, name) = await RenameDialog(RenameStrings.HeadingSession, RenameStrings.HintSession, session.Title, offerAutomatic: session.Kind == "claude");
        if (automatic) await service.SetSessionAutoTitleAsync(id);
        else if (name is not null) await service.RenameSessionAsync(id, name);
        else return;
        Render();
    });
}
