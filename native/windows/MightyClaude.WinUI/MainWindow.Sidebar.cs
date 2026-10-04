using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, Button> sidebarSessionButtons = [];
    private readonly Dictionary<string, Button> workspaceDisclosureButtons = [];
    private Button? sidebarThemeButton;

    private FrameworkElement BuildSidebarFooter()
    {
        // Mac groups the brand and the two global actions below the project tree.
        // Keep that grouping while retaining native focus, tooltip and UIA behavior.
        var footer = new Grid { ColumnSpacing = 2, Padding = new Thickness(0, 10, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(35, 128, 128, 128)) };
        AutomationProperties.SetAutomationId(footer, "sidebar-footer");
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var brand = new Grid { ColumnSpacing = 6, VerticalAlignment = VerticalAlignment.Center };
        brand.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); brand.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); brand.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var image = new Image { Source = new BitmapImage(new Uri("ms-appx:///Assets/mightyclaude.png")), Width = 20, Height = 20 };
        AutomationProperties.SetAccessibilityView(image, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw); brand.Children.Add(image);
        var title = new TextBlock { Text = "Mighty Claude", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1); brand.Children.Add(title);
        var beta = new Border { Child = brandBeta, CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(110, 135, 135, 135)), Padding = new Thickness(4, 0, 4, 1), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(beta, 2); brand.Children.Add(beta); footer.Children.Add(brand);
        var theme = BuildSidebarThemeButton(); Grid.SetColumn(theme, 1); footer.Children.Add(theme);
        settingsButton.Content = new SymbolIcon(Symbol.Setting) { Width = 16, Height = 16 };
        AutomationProperties.SetAutomationId(settingsButton, "sidebar-settings");
        foreach (var button in new[] { theme, settingsButton }) { button.Width = 28; button.Height = 28; button.MinWidth = 0; button.Padding = new Thickness(4); }
        Grid.SetColumn(settingsButton, 2); footer.Children.Add(settingsButton);
        return footer;
    }

    private Button BuildSidebarThemeButton()
    {
        sidebarThemeButton = SafeButton("", async () =>
        {
            await service.UpdateAsync(state => state with { Theme = state.Theme == "light" ? "dark" : "light" });
            Render();
        });
        AutomationProperties.SetAutomationId(sidebarThemeButton, "sidebar-toggle-theme");
        RefreshSidebarThemeButton();
        return sidebarThemeButton;
    }

    private void RefreshSidebarThemeButton()
    {
        if (sidebarThemeButton is null) return;
        var title = Locale.Get("sidebar.toggleTheme");
        sidebarThemeButton.Content = new TextBlock { Text = service.Snapshot.Theme == "light" ? "\u263E" : "\u2600\uFE0E", FontSize = 16 };
        AutomationProperties.SetName(sidebarThemeButton, title); ToolTipService.SetToolTip(sidebarThemeButton, title);
    }

    private async Task ToggleWorkspaceDisclosure(string id, bool keyboardFocus)
    {
        await Act(async () =>
        {
            await service.UpdateAsync(state => WorkspaceDisclosure.Toggle(state, id));
            if (closing) return;
            RenderSidebar();
            if (keyboardFocus && workspaceDisclosureButtons.TryGetValue(id, out var disclosure))
            {
                if (disclosure.IsLoaded) disclosure.Focus(FocusState.Keyboard);
                else
                {
                    void RestoreFocus(object sender, RoutedEventArgs args)
                    {
                        disclosure.Loaded -= RestoreFocus;
                        if (!closing && ReferenceEquals(workspaceDisclosureButtons.GetValueOrDefault(id), disclosure)) disclosure.Focus(FocusState.Keyboard);
                    }
                    disclosure.Loaded += RestoreFocus;
                }
            }
        });
    }

    private void RenderWorkspaceSidebar()
    {
        var previous = rendering; rendering = true;
        try
        {
            var state = service.Snapshot;
            var listed = AddPaneMenu.Filtered(state.Workspaces, search.Text);
            var expanded = WorkspaceDisclosure.Expanded(state);
            workspaces.Children.Clear(); workspaceStatusCounts.Clear(); sidebarSessionButtons.Clear(); workspaceDisclosureButtons.Clear();
            sessionIndicators.Clear(); sidebarDetails.Clear(); sidebarMarks.Clear(); sidebarBetas.Clear();
            sessionsHeader.Text = Locale.Get("phone.workspaces.title") + "  " + state.Workspaces.Count;
            addFolderButton.Visibility = listed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshSidebarThemeButton();
            foreach (var workspace in listed)
            {
                var selected = !showsDashboard && state.ActiveWorkspaceId == workspace.Id;
                var isExpanded = expanded.Contains(workspace.Id);
                var group = new StackPanel { Spacing = 3, Margin = new Thickness(0, 0, 0, selected ? 10 : 1) };
                AutomationProperties.SetAutomationId(group, "sidebar-workspace-" + workspace.Id);
                var header = new Grid { CornerRadius = new CornerRadius(7), Background = SidebarHighlight(selected), ContextFlyout = WorkspaceMenu(workspace.Id) };
                header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var label = new Grid { ColumnSpacing = 7 };
                label.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); label.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); label.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                label.Children.Add(new FontIcon { Glyph = "\uE8B7", FontSize = 14 });
                var title = new TextBlock { Text = workspace.Name, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(title, 1); label.Children.Add(title);
                var badges = WorkspaceStatusCounts(workspace); Grid.SetColumn(badges, 2); label.Children.Add(badges);
                var select = SidebarButton(workspace.Name, () => SelectWorkspace(workspace.Id)); select.Content = label;
                AutomationProperties.SetAutomationId(select, "workspace-select-" + workspace.Id); header.Children.Add(select);
                Button disclosure = null!;
                disclosure = SidebarButton(isExpanded ? "⌄" : "›", () => ToggleWorkspaceDisclosure(workspace.Id, disclosure.FocusState == FocusState.Keyboard));
                disclosure.Width = 28; disclosure.MinWidth = 0; disclosure.Padding = new Thickness(3); Grid.SetColumn(disclosure, 1);
                var disclosureName = Locale.Get(isExpanded ? "workspace.collapseAccessibility" : "workspace.expandAccessibility", new Dictionary<string, string> { ["workspace"] = workspace.Name });
                AutomationProperties.SetName(disclosure, disclosureName); AutomationProperties.SetAutomationId(disclosure, "workspace-expand-" + workspace.Id); ToolTipService.SetToolTip(disclosure, disclosureName);
                header.Children.Add(disclosure); workspaceDisclosureButtons[workspace.Id] = disclosure;
                ToolTipService.SetToolTip(select, workspace.Path); group.Children.Add(header);
                if (isExpanded)
                {
                    var children = new StackPanel { Spacing = 2, Margin = new Thickness(22, 0, 2, 0) };
                    foreach (var session in state.Sessions.Where(session => session.WorkspaceId == workspace.Id))
                    {
                        var button = SidebarButton(session.Title, () => SelectLayoutSession(session.Id));
                        button.Content = SessionIndicator(session); button.ContextFlyout = SessionMenu(session.Id);
                        button.Background = SidebarHighlight(!showsDashboard && state.ActiveSessionId == session.Id);
                        AutomationProperties.SetAutomationId(button, "sidebar-session-" + session.Id);
                        AutomationProperties.SetName(button, AutomationProperties.GetName((DependencyObject)button.Content));
                        sidebarSessionButtons[session.Id] = button; children.Children.Add(button);
                    }
                    var add = SidebarButton("+ " + Locale.Get("workspace.addPane"), () => Task.CompletedTask);
                    add.Flyout = DashboardAddMenu(workspace.Id); add.FontSize = 11;
                    AutomationProperties.SetAutomationId(add, "workspace-add-session-" + workspace.Id);
                    AutomationProperties.SetName(add, Locale.Get("workspace.addPaneAccessibility", new Dictionary<string, string> { ["workspace"] = workspace.Name }));
                    ToolTipService.SetToolTip(add, Locale.Get("workspace.addPaneHelp")); children.Children.Add(add); group.Children.Add(children);
                }
                workspaces.Children.Add(group);
            }
        }
        finally { rendering = previous; }
    }

    private static SolidColorBrush SidebarHighlight(bool selected) => new(selected ? Windows.UI.Color.FromArgb(28, 110, 140, 245) : Colors.Transparent);
    private Button SidebarButton(string title, Func<Task> action)
    {
        var button = SafeButton(title, action);
        button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Padding = new Thickness(8, 7, 8, 7); button.BorderThickness = new Thickness(0); button.Background = SidebarHighlight(false); button.CornerRadius = new CornerRadius(7);
        return button;
    }
}
