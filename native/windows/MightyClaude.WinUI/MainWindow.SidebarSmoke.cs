using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private async Task<Dictionary<string, object?>> RunSidebarSmoke()
    {
        var original = service.Snapshot; var originalSearch = search.Text;
        var first = original.Workspaces.First(); var second = original.Workspaces.Last();
        var firstSession = original.Sessions.First(p => p.WorkspaceId == first.Id);
        var retainedPane = views[firstSession.Id]; var draft = firstSession.Draft;
        var otherSession = new RunSession { WorkspaceId = second.Id, Title = "Other workspace fixture", Draft = "Preserved fixture draft" };
        async Task Invoke(string id)
        {
            Button? button = null;
            await WaitUI(() => (button = VisualChildren(root).OfType<Button>().FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == id)) is { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 });
            Require(button!.IsTabStop && button.Focus(FocusState.Keyboard), "Sidebar action must support native keyboard focus: " + id);
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        }
        try
        {
            search.Text = "";
            await service.UpdateAsync(s => s with { ExpandedWorkspaceIds = [first.Id], ActiveWorkspaceId = first.Id, ActiveSessionId = firstSession.Id, Sessions = [.. s.Sessions, otherSession] }); Render();
            await Invoke("workspace-expand-" + second.Id);
            await WaitUI(() => WorkspaceDisclosure.Expanded(service.Snapshot).SetEquals([first.Id, second.Id]) && sidebarSessionButtons.ContainsKey(otherSession.Id));
            Require(service.Snapshot.ActiveSessionId == firstSession.Id && service.Snapshot.ActiveWorkspaceId == first.Id && ReferenceEquals(views[firstSession.Id], retainedPane), "Expanding another workspace must preserve the active pane and editor instance.");
            await Invoke("workspace-expand-" + first.Id);
            await WaitUI(() => !sidebarSessionButtons.ContainsKey(firstSession.Id));
            Require(service.Snapshot.ActiveSessionId == firstSession.Id && service.Snapshot.Sessions.First(s => s.Id == firstSession.Id).Draft == draft, "Collapsing an active list must preserve its selection and draft.");
            await Invoke("sidebar-session-" + otherSession.Id);
            await WaitUI(() => service.Snapshot.ActiveSessionId == otherSession.Id && service.Snapshot.ActiveWorkspaceId == second.Id);
            await Invoke("workspace-select-" + first.Id);
            await WaitUI(() => service.Snapshot.ActiveWorkspaceId == first.Id && sidebarSessionButtons.ContainsKey(firstSession.Id));
            Require(WorkspaceDisclosure.Expanded(service.Snapshot).SetEquals([first.Id, second.Id]) && ReferenceEquals(views[firstSession.Id], retainedPane), "Selecting a workspace reopens it without collapsing another or replacing the composer.");
            var add = VisualChildren(sidebar).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "workspace-add-session-" + second.Id);
            await WaitUI(() => add.IsLoaded && add.ActualWidth > 0);
            Require(add.Flyout is MenuFlyout { Items.Count: > 0 } menu && menu.Items.Count == AddPaneMenu.Entries().Count, "Every workspace has the complete shared Add Pane menu.");
            add.Flyout!.ShowAt(add);
            var files = ((MenuFlyout)add.Flyout).Items.OfType<MenuFlyoutItem>().Single(item => AutomationProperties.GetAutomationId(item) == "dashboard-add-" + AddPaneMenu.Files + "-" + second.Id);
            // The smoke shares its desktop: a press in another program puts an open menu away, so it is shown again until its item is there.
            await WaitUI(() => { if (!add.Flyout.IsOpen) add.Flyout.ShowAt(add); return files.IsLoaded && files.ActualWidth > 0 && files.ActualHeight > 0; });
            ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(files).GetPattern(PatternInterface.Invoke)).Invoke();
            // The add selects its workspace first, and that reads the workspace's CLIs again before the pane is made
            // (SelectWorkspace): on a busy machine that alone outlasts a UI wait.
            await WaitUI(() => service.Snapshot.ActiveWorkspaceId == second.Id && service.Snapshot.Sessions.Any(p => p.WorkspaceId == second.Id && FilePaneKind.IsFilePane(p.Kind)), seconds: RuntimeReadWait);
            add.Flyout.Hide();
            await ToggleWorkspaceDisclosure(first.Id, false); await ToggleWorkspaceDisclosure(second.Id, false);
            Require(service.Snapshot.ExpandedWorkspaceIds is { Count: 0 } && sidebarSessionButtons.Count == 0, "Both lists may stay collapsed independently of the active workspace.");
            var stored = await new StateStore(StateDirectory).LoadAsync();
            Require(stored.ExpandedWorkspaceIds is { Count: 0 }, "All-collapsed state must be persisted as [], not converted to null fallback.");
            await Invoke("sidebar-toggle-theme");
            await WaitUI(() => service.Snapshot.Theme != original.Theme);
            Require(ReferenceEquals(views[firstSession.Id], retainedPane) && service.Snapshot.Sessions.First(s => s.Id == firstSession.Id).Draft == draft, "Quick theme toggle must preserve the input control and draft.");
            return new() { ["independentDisclosure"] = true, ["selectionReopensOnlyTarget"] = true, ["inactiveWorkspacePaneSelection"] = true, ["workspaceBoundAddMenu"] = true, ["collapsedStatePersists"] = true, ["draftAndPaneIdentity"] = true, ["keyboardAccessible"] = true, ["quickTheme"] = true };
        }
        finally { await service.UpdateAsync(_ => original); search.Text = originalSearch; Render(); }
    }

    /// <summary>The whole window as it is now, written as <c>smoke-shell-{name}.png</c> in the smoke profile.</summary>
    private async Task<string> CaptureShellSmoke(string name)
    {
        root.UpdateLayout(); await Task.Delay(150); root.UpdateLayout();
        return await CaptureSmoke(Path.Combine(options.ProfileDirectory!, "smoke-shell-" + name + ".png"));
    }

    /// <summary>
    /// A popover's content, written as <c>smoke-shell-{name}.png</c>. The window's renderer does not read
    /// popups, so the content is laid for the capture on a card in the window, drawn as the popovers'
    /// presenter draws it (<see cref="CardFlyoutStyle"/>: card, a 1pt line, radius 10, padding 16), and
    /// then let go, for the caller to hand back to its flyout.
    /// </summary>
    private async Task CaptureShellPopover(string name, FrameworkElement content)
    {
        var card = new Border
        {
            Child = content, Background = brushes.Brush(DesignToken.Card), BorderBrush = brushes.Brush(DesignToken.Line), BorderThickness = new Thickness(DesignMetrics.Stroke.Line),
            CornerRadius = new CornerRadius(DesignMetrics.Radius.Entry), Padding = new Thickness(PopoverPadding), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 16, 44),
        };
        Grid.SetRowSpan(card, 3); Grid.SetColumnSpan(card, 2); root.Children.Add(card);
        try
        {
            root.UpdateLayout(); await Task.Delay(250); root.UpdateLayout();
            await CaptureElement(card, Path.Combine(options.ProfileDirectory!, "smoke-shell-" + name + ".png"));
        }
        finally { card.Child = null; root.Children.Remove(card); }
    }

    /// <summary>
    /// A popover's switch is the Mac's mini switch (M/StatusBarUsage.swift:229, M/AgentCompanionViews.swift:65), which is the
    /// form's own (<see cref="SettingsSwitch"/>): a 26×15 capsule around a 13pt knob, <c>accent</c> when on and a quiet fill
    /// when off, under its id and its name.
    /// </summary>
    private void RequirePopoverSwitch(Microsoft.UI.Xaml.Controls.Primitives.ToggleButton? toggle, string id, string name, bool on, string what)
    {
        const string key = SidebarDesignKey; var theme = SmokeTheme;
        Require(toggle is { Width: SettingsSwitchWidth, Height: SettingsSwitchHeight, Content: Grid } && toggle.IsChecked == on && AutomationProperties.GetAutomationId(toggle) == id && AutomationProperties.GetName(toggle) == name,
            $"{key} ({theme}): {what} must be the {SettingsSwitchWidth}×{SettingsSwitchHeight} switch '{id}' named '{name}', {(on ? "on" : "off")}; got {(toggle is null ? "none" : $"{toggle.GetType().Name} {toggle.Width}×{toggle.Height} '{AutomationProperties.GetAutomationId(toggle)}' named '{AutomationProperties.GetName(toggle)}', checked {toggle.IsChecked}")}");
        var capsule = (Grid)toggle!.Content; var knob = capsule.Children.OfType<Microsoft.UI.Xaml.Shapes.Ellipse>().Single();
        Require(knob.Width == SettingsSwitchKnob && capsule.CornerRadius == new CornerRadius(SettingsSwitchHeight / 2), $"{key} ({theme}): {what} must be a capsule around a {SettingsSwitchKnob} knob; got knob {knob.Width}, {capsule.CornerRadius}");
        if (on) RequireBrush(toggle, _ => capsule.Background, DesignToken.Accent, what + " while on", key: key);
        else RequireBrush(toggle, _ => capsule.Background, DesignToken.Ink2, what + " while off", SettingsSwitchOffOpacity, key);
    }

    /// <summary>A menu's items as words, a separator as "-", for comparing a menu with the Mac's.</summary>
    private static List<string> MenuWords(Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase? menu) =>
        menu is MenuFlyout { Items: var items } ? items.Select(item => item is MenuFlyoutItem words ? words.Text : "-").ToList() : [];

    /// <summary>
    /// The shell's looks the design checks cannot read as values, captured for the eye in the theme just
    /// rendered: the window with a Git capsule in its header, both status bar popovers, the welcome and
    /// the no-panes invitation over the detail column, the launch splash and the rename sheet; and the
    /// add-pane menu and the two context menus, read as words against the Mac's. Everything it shows is
    /// put away again.
    /// </summary>
    private async Task CaptureShellStates(string theme)
    {
        var state = service.Snapshot;
        var workspace = state.Workspaces.First(w => w.Id == state.ActiveWorkspaceId);
        // A Git reading for the header's capsule: a smoke run reads no repository.
        var (savedKey, savedInfo) = (gitWorkspaceKey, gitInfo);
        gitWorkspaceKey = workspace.Id + "|" + workspace.Path; gitInfo = new WorkspaceGitInfo("feature/parity", "0123456789abcdef", true, 2, 1);
        RefreshWorkspaceHeader();
        try
        {
            Require(gitBadges.TryGetValue(workspace.Id, out var git) && git is { Visibility: Visibility.Visible, MaxWidth: GitBadgeMaxWidth, Height: GitBadgeHeight } && git.Padding == new Thickness(7, 0, 7, 0) && git.Child is Grid { ColumnSpacing: 5, Children.Count: 5 },
                $"{SidebarDesignKey} ({theme}): the header's Git capsule must hold the branch symbol, the name, the change dot and the ahead and behind counts, 5 apart, {GitBadgeHeight} high (the Mac's 10pt line in v3) with h7, at most {GitBadgeMaxWidth} wide");
            // With the capsule the header is the Mac's: t14, the 17pt name's line, 3, the capsule's 18, b10, and the line under it.
            root.UpdateLayout();
            Require(Math.Abs(workspaceHeader.ActualHeight - 66) <= 1.5, $"{SidebarDesignKey} ({theme}): the workspace header with its Git capsule must be about 66 high, as the Mac's; got {workspaceHeader.ActualHeight:F1}");
            RequireSubtle(git!, "the header's Git capsule", SidebarDesignKey);
            await CaptureShellSmoke("window-" + theme);
        }
        finally { gitWorkspaceKey = savedKey; gitInfo = savedInfo; RefreshWorkspaceHeader(); }

        // The status bar with its update badge (M/WorkspaceView.swift:383-388), which no smoke run has a newer version to show: shown for the picture, then put back.
        if (updateBadge is { } badge)
        {
            badge.Visibility = Visibility.Visible; updateBadgeText.Text = Locale.Get("window.status.updateBadge", new Dictionary<string, string> { ["version"] = "9.9.9" });
            try
            {
                root.UpdateLayout(); await Task.Delay(150);
                Require(Math.Abs(statusBar.ActualHeight - (16 + CompanionCapsuleHeight)) < 0.6 && badge.ActualHeight <= CompanionCapsuleHeight, $"{SidebarDesignKey} ({theme}): the update badge must not make the status bar taller; got {statusBar.ActualHeight:F1} with a {badge.ActualHeight:F1}-high badge");
                await CaptureElement(statusBar, Path.Combine(options.ProfileDirectory!, "smoke-shell-update-badge-" + theme + ".png"));
            }
            finally { updateBadgeText.Text = ""; RefreshStatusBar(); }
        }

        // The error banner over the header (M/WorkspaceView.swift:18, 405-411), given a message for the picture:
        // one 12pt line in padding 12 is 40 high, edge to edge over the detail column, and the header moves down under it.
        var said = error.Text; error.Text = Locale.Get("window.error.addWorkspaceFirst");
        try
        {
            root.UpdateLayout();
            var headerTop = workspaceHeader.TransformToVisual(detailTop).TransformPoint(new Windows.Foundation.Point()).Y;
            Require(errorBanner.Visibility == Visibility.Visible && Math.Abs(errorBanner.ActualHeight - 40) <= 1 && Math.Abs(errorBanner.ActualWidth - detailTop.ActualWidth) < 0.5 && Math.Abs(headerTop - errorBanner.ActualHeight) < 0.5,
                $"{SidebarDesignKey} ({theme}): the error banner must be about 40 high across the detail column with the header under it; got {errorBanner.ActualHeight:F1} high, {errorBanner.ActualWidth:F1} of {detailTop.ActualWidth:F1} wide, the header at {headerTop:F1}");
            await CaptureShellSmoke("error-" + theme);
        }
        finally { error.Text = said; }

        // The usage popover (M/StatusBarUsage.swift:207-237) and the agent status popover (M/AgentCompanionViews.swift:27-68).
        if (usageButton is { Visibility: Visibility.Visible, Flyout: Flyout { Content: ScrollViewer usageHost } usagePopover })
        {
            RenderAccountUsageDetails();
            Require(usageDetails.Spacing == 12 && usageDetails.Width == UsagePopoverWidth - 2 * PopoverPadding && ReferenceEquals(usagePopover.FlyoutPresenterStyle, CardFlyoutStyle)
                && usageDetails.Children[0] is Grid { Children: [FontIcon, TextBlock { FontSize: DesignMetrics.Type.Title }, Microsoft.UI.Xaml.Controls.Button] },
                $"{SidebarDesignKey} ({theme}): the usage popover must be {UsagePopoverWidth} wide with its padding {PopoverPadding} (M/StatusBarUsage.swift:235), its parts 12 apart, under the pie symbol, its 13pt title and the refresh button; got content width {usageDetails.Width}, spacing {usageDetails.Spacing}");
            foreach (var card in usageDetails.Children.OfType<StackPanel>())
            {
                RequireSubtle(card.Background, "a usage card", SidebarDesignKey);
                Require(card.Spacing == 8 && card.Padding == new Thickness(10) && card.CornerRadius == new CornerRadius(DesignMetrics.Radius.CardButton) && card.Children[0] is Grid { ColumnSpacing: 6 } head
                    && head.Children[0] is Microsoft.UI.Xaml.Shapes.Path mark && Math.Abs(mark.Width - UsageCardMark) < 0.01 && head.Children[1] is TextBlock { FontSize: 12 },
                    $"{SidebarDesignKey} ({theme}): the usage card {AutomationProperties.GetAutomationId(card)} must be padding 10 at radius {DesignMetrics.Radius.CardButton}, its parts 8 apart, under the {UsageCardMark:F1}pt mark and the 12pt name, 6 apart; got spacing {card.Spacing}, padding {card.Padding}");
            }
            if (usage!.Providers.Contains("claude")) RequirePopoverSwitch(usageDirectToggle, "statusbar-usage-direct-toggle", AccountUsageStrings.ToggleLabel, usage.DirectClaudeLookupEnabled, "the usage popover's direct-lookup switch");
            usageHost.Content = null;
            try { await CaptureShellPopover("usage-popover-" + theme, usageDetails); }
            finally { usageHost.Content = usageDetails; }
        }
        if (companionStatusFlyout is { } agentsPopover && companionStatusBody is { } agentsBody)
        {
            // The rows are drawn while the popover shows; the capture stands in for it.
            companionStatusVisible = true;
            try
            {
                RefreshCompanionControls();
                Require(agentsBody.Width == CompanionPopoverWidth - 2 * CompanionPopoverPadding && agentsBody.Spacing == CompanionPopoverSpacing && agentsBody.Margin == new Thickness(CompanionPopoverPadding - PopoverPadding) && ReferenceEquals(agentsPopover.FlyoutPresenterStyle, CardFlyoutStyle)
                    && companionStatusHeading is { FontSize: 14 } && companionStatusItems.Count > 0 && companionStatusItems.Values.All(item => item.Button.Padding == new Thickness(10) && item.Button.CornerRadius == new CornerRadius(DesignMetrics.Radius.Entry)),
                    $"{SidebarDesignKey} ({theme}): the agent status popover must be {CompanionPopoverWidth} wide with its padding {CompanionPopoverPadding} (M/AgentCompanionViews.swift:66), its parts {CompanionPopoverSpacing} apart, under a 14pt title, each agent a card in padding 10 at radius {DesignMetrics.Radius.Entry}; got content width {agentsBody.Width}, spacing {agentsBody.Spacing}, {companionStatusItems.Count} agents");
                foreach (var item in companionStatusItems.Values) RequireSubtle(OwnResource(item.Button, "ButtonBackground") as Microsoft.UI.Xaml.Media.Brush, "an agent's card in the status popover", SidebarDesignKey);
                // Each card's open arrow is the tertiary ink (M/AgentCompanionViews.swift:57).
                foreach (var item in companionStatusItems.Values)
                    Require(item.Button.Content is Grid { Children: [.., Grid { Width: 9 } arrow] } && arrow.Children.OfType<Microsoft.UI.Xaml.Shapes.Polyline>().All(line => ReferenceEquals(line.Stroke, brushes.Tertiary)),
                        $"{SidebarDesignKey} ({theme}): an agent card's open arrow must be the 9pt arrow in the shared tertiary brush");
                RequirePopoverSwitch(companionStatusPet, "companion-status-pet", Locale.Get("companion.status.petSwitch"), companionPreferences.Enabled, "the agent status popover's pet switch");
                agentsPopover.Content = null;
                try { await CaptureShellPopover("agents-popover-" + theme, agentsBody); }
                finally { agentsPopover.Content = agentsBody; }
            }
            finally { companionStatusVisible = false; }
        }

        // The add-pane menu from the sidebar's last row (M/WorkspaceView.swift:418-446), then the workspace row's
        // (M/WorkspaceView.swift:193-197) and a pane row's (M/WorkspaceView.swift:259-262) context menus, as words.
        var group = workspaces.Children.OfType<StackPanel>().Single(g => AutomationProperties.GetAutomationId(g) == "sidebar-workspace-" + workspace.Id);
        var add = group.Children.OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "workspace-add-session-" + workspace.Id);
        string Agent(string provider) => ProviderCatalog.BetaLabel(provider, Locale.Get("workspace.newAgentPane", new Dictionary<string, string> { ["provider"] = ProviderMark.Label(provider) }));
        var addWant = AddPaneMenu.Entries().Select(entry => entry switch
        {
            AddPaneMenu.Separator => "-", AddPaneMenu.Shell => Locale.Get("workspace.newTerminal"), AddPaneMenu.Browser => Locale.Get("browser.newTab"),
            AddPaneMenu.Files => Locale.Get("menu.showFiles"), AddPaneMenu.OpenProject => Locale.Get(AddPaneMenu.OpenProjectKey), _ => Agent(AddPaneMenu.AgentProvider(entry)!),
        }).ToList();
        foreach (var (menu, what) in new[] { (add.Flyout, "the sidebar's add-pane menu"), (NewSessionMenu(), "a tab group's add-pane menu") })
        {
            Require(MenuWords(menu).SequenceEqual(addWant), $"{SidebarDesignKey} ({theme}): {what} must read as the Mac's, [{string.Join(" | ", addWant)}]; got [{string.Join(" | ", MenuWords(menu))}]");
            Require(((MenuFlyout)menu).Items.OfType<MenuFlyoutItem>().All(item => item.Icon is not null), $"{SidebarDesignKey} ({theme}): every item of {what} must carry its mark");
        }
        var workspaceWant = new List<string> { RenameStrings.MenuEntry, Locale.Get("workspace.menu.remove"), Locale.Get("menu.showInExplorer"), "-", Locale.Get("menu.showFiles") };
        var workspaceMenu = MenuWords(((Grid)group.Children[0]).ContextFlyout);
        Require(workspaceMenu.SequenceEqual(workspaceWant), $"{SidebarDesignKey} ({theme}): a workspace row's menu must read [{string.Join(" | ", workspaceWant)}] (the Mac's three, then the files pane); got [{string.Join(" | ", workspaceMenu)}]");
        // The header's name and blank space offer the Mac's one item (M/WorkspaceTitlebar.swift:30-36); the path keeps its own menu.
        var headerMenu = MenuWords(workspaceHeader.ContextFlyout);
        Require(headerMenu.SequenceEqual([Locale.Get("menu.renameWorkspace")]) && workspaceHeader.Background is not null && workspaceHeaderPath.ContextFlyout is MenuFlyout { Items.Count: 1 },
            $"{SidebarDesignKey} ({theme}): the workspace header's menu must be the one item '{Locale.Get("menu.renameWorkspace")}' over its name and blank space; got [{string.Join(" | ", headerMenu)}]");
        var paneWant = new List<string> { RenameStrings.MenuEntry, Locale.Get("menu.closePane") };
        foreach (var (id, row) in sidebarSessionButtons)
            Require(MenuWords(row.ContextFlyout).SequenceEqual(paneWant), $"{SidebarDesignKey} ({theme}): the pane row {id}'s menu must read as the Mac's, [{string.Join(" | ", paneWant)}]; got [{string.Join(" | ", MenuWords(row.ContextFlyout))}]");

        // The welcome and the no-panes invitation stand where the dock does, over the status bar.
        var stage = new Grid { Background = WindowBackground() };
        Grid.SetRowSpan(stage, 2); Grid.SetColumn(stage, 1); root.Children.Add(stage);
        try
        {
            stage.Children.Add(BuildWelcome()); await CaptureShellSmoke("welcome-" + theme);
            stage.Children.Clear(); stage.Children.Add(BuildEmptyPanes(true)); await CaptureShellSmoke("empty-panes-" + theme);
        }
        finally { root.Children.Remove(stage); }

        ShowLaunchSplash();
        try { await CaptureShellSmoke("splash-" + theme); }
        finally { HideLaunchSplash(); }

        // The rename sheet (M/RenameViews.swift:29-63), shown for real and dismissed without a change: 360 wide in
        // padding 22, the 13pt bold heading, the name field with its label as the placeholder, the caption and the
        // refusals in 10pt and the sheet's own buttons, 14 apart — Automatic leading, Cancel and the accent Save trailing.
        if (state.ActiveSessionId is { } renamed)
        {
            var offersAutomatic = state.Sessions.First(s => s.Id == renamed).Kind == "claude";
            var driver = smokeAskName;
            smokeAskName = async (dialog, field, errors) =>
            {
                Require(OwnResource(dialog, "ContentDialogMinWidth") is double min && min == RenameSheetWidth && OwnResource(dialog, "ContentDialogMaxWidth") is double max && max == RenameSheetWidth,
                    $"{SidebarDesignKey} ({theme}): the rename sheet must be {RenameSheetWidth} wide; got min {OwnResource(dialog, "ContentDialogMinWidth")}, max {OwnResource(dialog, "ContentDialogMaxWidth")}");
                Require(dialog.Content is StackPanel { Spacing: RenameSpacing, Children: [TextBlock { FontSize: DesignMetrics.Type.Title } heading, TextBox name, TextBlock { FontSize: RenameCaption } caption, StackPanel { Spacing: RenameSpacing } refusals, Grid { ColumnSpacing: 8 }] } sheet
                    && sheet.Margin == new Thickness(RenameSheetPadding - SheetPadding) && heading.FontWeight.Weight == Microsoft.UI.Text.FontWeights.Bold.Weight && ReferenceEquals(heading.Foreground, brushes.Brush(DesignToken.Ink))
                    && ReferenceEquals(name, field) && ReferenceEquals(refusals, errors) && field.Header is null && field.PlaceholderText == RenameStrings.FieldLabel && ReferenceEquals(caption.Foreground, brushes.Brush(DesignToken.Ink2)),
                    $"{SidebarDesignKey} ({theme}): the rename sheet must be the {DesignMetrics.Type.Title}pt bold heading, the name field (its label the placeholder), the {RenameCaption}pt ink2 caption, the refusals and the buttons, {RenameSpacing} apart in padding {RenameSheetPadding}");
                // No dialog title or command row: the sheet's own heading and buttons stand in the Mac's places (M/RenameViews.swift:31, 46-57).
                var buttons = RenameButtons(dialog);
                var wantIds = offersAutomatic ? new[] { RenameAutomaticId, "rename-cancel", "rename-save" } : ["rename-cancel", "rename-save"];
                var wantWords = offersAutomatic ? new[] { Locale.Get("pane.rename.automatic"), RenameStrings.ButtonCancel, RenameStrings.ButtonSave } : [RenameStrings.ButtonCancel, RenameStrings.ButtonSave];
                Require(dialog.Title is null && string.IsNullOrEmpty(dialog.PrimaryButtonText) && string.IsNullOrEmpty(dialog.SecondaryButtonText) && string.IsNullOrEmpty(dialog.CloseButtonText)
                    && buttons.Select(b => AutomationProperties.GetAutomationId(b)).SequenceEqual(wantIds) && buttons.Select(b => b.Content as string).SequenceEqual(wantWords)
                    && buttons.Select(Grid.GetColumn).SequenceEqual(offersAutomatic ? new[] { 0, 2, 3 } : [2, 3]) && buttons.All(b => b.FontSize == DesignMetrics.Type.Body)
                    && buttons[^1].Style == (Style)Application.Current.Resources["AccentButtonStyle"] && buttons.SkipLast(1).All(b => b.Style is null),
                    $"{SidebarDesignKey} ({theme}): the rename sheet's buttons must be [{string.Join(" | ", wantWords)}], {DesignMetrics.Type.Body}pt, the first leading and the last two trailing with Save the accent one; got [{string.Join(" | ", buttons.Select(b => b.Content))}] in columns {string.Join(", ", buttons.Select(Grid.GetColumn))}");
                Require(dialog.RequestedTheme == root.RequestedTheme, $"{SidebarDesignKey} ({theme}): the rename sheet must follow the window's theme; got {dialog.RequestedTheme}, window {root.RequestedTheme}");
                var showing = dialog.ShowAsync().AsTask();
                try
                {
                    FrameworkElement? surface = null;
                    await WaitUI(() => dialog.IsLoaded && (surface = VisualChildren(dialog).OfType<FrameworkElement>().FirstOrDefault(part => part.Name == "BackgroundElement")) is { ActualWidth: > 0 }, () => "shell captures: the rename sheet never opened");
                    Require(Math.Abs(surface!.ActualWidth - RenameSheetWidth) <= 1, $"{SidebarDesignKey} ({theme}): the shown rename sheet must be {RenameSheetWidth} wide; got {surface.ActualWidth:F1}");
                    // Each button is as wide as its words, so none is cut, and the row fits the sheet.
                    foreach (var button in buttons)
                    {
                        button.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                        Require(button.ActualWidth >= button.DesiredSize.Width - 0.5, $"{SidebarDesignKey} ({theme}): the rename sheet's '{button.Content}' button must show its words whole; got {button.ActualWidth:F1} of {button.DesiredSize.Width:F1}");
                    }
                    // An invalid name shows its refusal in 10pt errText and keeps Save disabled.
                    var kept = field.Text; field.Text = new string('a', RenameSupport.MaximumTextElements + 1);
                    await WaitUI(() => errors.Children.Count > 0 && !dialog.IsPrimaryButtonEnabled && !buttons[^1].IsEnabled, () => "shell captures: the rename sheet never refused a name that is too long");
                    Require(errors.Visibility == Visibility.Visible && errors.Children.OfType<TextBlock>().All(line => line.FontSize == RenameCaption && ReferenceEquals(line.Foreground, brushes.Brush(DesignToken.ErrText))),
                        $"{SidebarDesignKey} ({theme}): the rename sheet's refusals must be {RenameCaption}pt errText");
                    field.Text = kept;
                    await WaitUI(() => errors.Children.Count == 0 && errors.Visibility == Visibility.Collapsed && buttons[^1].IsEnabled, () => "shell captures: the rename sheet kept a refusal for a valid name");
                    // The sheet lives in a popup, which the window's renderer may not read: a capture that comes back empty is left out.
                    await Task.Delay(350);
                    try { await CaptureElement(surface, Path.Combine(options.ProfileDirectory!, "smoke-shell-rename-" + theme + ".png")); }
                    catch (InvalidOperationException) { }
                }
                finally { dialog.Hide(); try { await showing; } catch (Exception) { /* The sheet is closing; its own failure is not the capture's. */ } }
                return ContentDialogResult.None;
            };
            try { await RenameSession(renamed); }
            finally { smokeAskName = driver; }
        }
    }
}
