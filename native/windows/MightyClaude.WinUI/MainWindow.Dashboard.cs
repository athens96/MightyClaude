using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private ScrollViewer? dashboard;
    private bool showsDashboard;
    private string? dashboardFingerprint;
    private readonly Dictionary<string, TextBlock> dashboardClocks = [];
    private readonly Dictionary<string, TextBlock> sidebarDetails = [];
    private readonly Dictionary<string, TextBlock> workspaceStatusCounts = [];
    private Button? dashboardButton;
    private void InitDashboard()
    {
        var button = dashboardButton = Button(Locale.Get("phone.dashboard.title"), () => { showsDashboard = true; RenderDashboard(); return Task.CompletedTask; });
        button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetAutomationId(button, "sidebar-dashboard"); ToolTipService.SetToolTip(button, Locale.Get("dashboard.sidebarHelp"));
        sidebar.Children.Insert(1, button);
        dashboard = new ScrollViewer { Visibility = Visibility.Collapsed, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(dashboard, 1); Grid.SetColumn(dashboard, 1); root.Children.Add(dashboard);
        var grip = new Thumb { Width = 8, HorizontalAlignment = HorizontalAlignment.Right, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 128, 128, 128)), IsTabStop = true };
        AutomationProperties.SetName(grip, Locale.Get("sidebar.resize")); AutomationProperties.SetAutomationId(grip, "sidebar-resize");
        Grid.SetRow(grip, 1); root.Children.Add(grip);
        grip.DragDelta += (_, args) => root.ColumnDefinitions[0].Width = new(Math.Clamp(root.ColumnDefinitions[0].Width.Value + args.HorizontalChange, 200, 400));
        grip.DragCompleted += async (_, args) => { var width = args.Canceled ? service.Snapshot.SidebarWidth : root.ColumnDefinitions[0].Width.Value; await Act(() => service.UpdateAsync(s => s with { SidebarWidth = width })); root.ColumnDefinitions[0].Width = new(width); };
        grip.DoubleTapped += async (_, args) => { args.Handled = true; await Act(() => service.UpdateAsync(s => s with { SidebarWidth = 252 })); root.ColumnDefinitions[0].Width = new(252); };
        grip.KeyDown += async (_, args) =>
        {
            if (args.Key is not (Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right)) return;
            args.Handled = true; var width = Math.Clamp(service.Snapshot.SidebarWidth + (args.Key == Windows.System.VirtualKey.Right ? 10 : -10), 200, 400);
            await Act(() => service.UpdateAsync(s => s with { SidebarWidth = width })); root.ColumnDefinitions[0].Width = new(width);
        };
    }
    private WorkDashboard.Attention DashboardAttention(string id) => views.TryGetValue(id, out var pane) ? pane.DashboardAttention : new();
    private void HideDashboard() { showsDashboard = false; dashboardFingerprint = null; if (dashboard is not null) dashboard.Visibility = Visibility.Collapsed; panes.Visibility = Visibility.Visible; }
    private void RenderDashboard()
    {
        if (dashboard is null || closing || !showsDashboard) return;
        panes.Visibility = Visibility.Collapsed; dashboard.Visibility = Visibility.Visible;
        var state = service.Snapshot;
        var accountChips = usage?.Chips() ?? [];
        foreach (var session in state.Sessions)
            if (dashboardClocks.TryGetValue(session.Id, out var clockLabel))
                clockLabel.Text = session.RunTiming is { IsValid: true } timing ? timing.Label() : "";
        var key = System.Text.Json.JsonSerializer.Serialize(new { Locale.LanguagePreference, state.ActiveSessionId, state.Theme, state.Workspaces, Account = accountChips, Sessions = state.Sessions.Select(s => new { s.Id, s.Status, s.Title, s.Model, s.Kind, s.Provider, s.CurrentActivity, Log = s.Logs.LastOrDefault(), s.SessionUsage, Attention = DashboardAttention(s.Id) }) }, Wire.Json);
        if (key == dashboardFingerprint) return; dashboardFingerprint = key;
        dashboardClocks.Clear();
        var content = new StackPanel { Spacing = 20, Padding = new(24, 10, 24, 24) };
        content.Children.Add(new TextBlock { Text = Locale.Get("phone.dashboard.title"), FontSize = 29, FontWeight = Microsoft.UI.Text.FontWeights.Bold });
        content.Children.Add(new TextBlock { Text = Locale.Get("dashboard.subtitle", new Dictionary<string, string> { ["workspaces"] = state.Workspaces.Count.ToString(), ["panes"] = state.Sessions.Count.ToString() }), FontSize = 12, Opacity = .7 });
        if (accountChips.Count > 0)
        {
            var account = new PillWrapPanel();
            foreach (var chip in accountChips)
            {
                var item = Button(ProviderCatalog.Name(chip.Provider) + " " + chip.Text, () => { usageButton?.Flyout?.ShowAt(usageButton); return Task.CompletedTask; });
                item.FontSize = 11; item.Padding = new(10, 4, 10, 4); item.CornerRadius = new(12); account.Children.Add(item);
            }
            content.Children.Add(account);
        }
        var stats = WorkDashboard.Count(state.Sessions, DashboardAttention);
        var tiles = new Grid { ColumnSpacing = 12 };
        var values = new[] { ("phone.dashboard.stat.running", stats.Running), ("phone.dashboard.stat.waiting", stats.Waiting), ("phone.dashboard.stat.done", stats.Done) };
        for (var i = 0; i < values.Length; i++)
        {
            tiles.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var tile = new StackPanel { Spacing = 6, Padding = new(16) }; tile.Children.Add(new TextBlock { Text = Locale.Get(values[i].Item1), FontSize = 12, Opacity = .75 }); tile.Children.Add(new TextBlock { Text = values[i].Item2.ToString(), FontSize = 32, FontWeight = Microsoft.UI.Text.FontWeights.Bold });
            var border = new Border { Child = tile, CornerRadius = new(12), BorderThickness = new(1), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(60, 128, 128, 128)) }; Grid.SetColumn(border, i); tiles.Children.Add(border);
        }
        content.Children.Add(tiles);
        if (state.Workspaces.Count == 0) content.Children.Add(new TextBlock { Text = Locale.Get("dashboard.empty"), Opacity = .7 });
        foreach (var workspace in state.Workspaces)
        {
            var group = new StackPanel { Spacing = 8 };
            var heading = Button(workspace.Name, async () => { HideDashboard(); await SelectWorkspace(workspace.Id); }); heading.FontSize = 17; heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; group.Children.Add(heading);
            foreach (var card in WorkDashboard.Ordered(state.Sessions.Where(s => s.WorkspaceId == workspace.Id && WorkDashboard.IsCounted(s.Kind)).Select(s => WorkDashboard.MakeCard(s, DashboardAttention(s.Id)))))
            {
                var row = new Grid { ColumnSpacing = 12 }; row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var mark = new StatusMark(); mark.Update(card.DisplayStatus, card.Session.Kind, card.Attention.Total, state.Theme != "light"); row.Children.Add(mark.View);
                var description = new StackPanel { Spacing = 4 }; description.Children.Add(new TextBlock { Text = card.Session.Title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                if (card.Session.Kind == "claude") description.Children.Add(new TextBlock { Text = ProviderCatalog.Name(card.Session.Provider) + (card.Model is { } model ? " · " + ModelLabel.Text(model) : ""), FontSize = 11, Opacity = .7 });
                if (card.LastActivity is { } last) description.Children.Add(new TextBlock { Text = last, FontSize = 11, Opacity = .7, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(description, 1); row.Children.Add(description);
                var detail = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center }; detail.Children.Add(new TextBlock { Text = StateLabel(card.DisplayStatus), FontSize = 11 });
                if (card.ContextPercent is { } percent) detail.Children.Add(new TextBlock { Text = $"{percent:0}%", FontSize = 11 });
                if (card.Timing is { } timing)
                {
                    var clockLabel = new TextBlock { Text = timing.Label(), FontSize = 11, Opacity = .7 };
                    dashboardClocks[card.Session.Id] = clockLabel; detail.Children.Add(clockLabel);
                }
                Grid.SetColumn(detail, 2); row.Children.Add(detail);
                var open = Button(card.Session.Title, async () => { HideDashboard(); await SelectLayoutSession(card.Session.Id); }); open.Content = row; open.HorizontalAlignment = HorizontalAlignment.Stretch; open.HorizontalContentAlignment = HorizontalAlignment.Stretch; open.Padding = new(12); group.Children.Add(open);
            }
            content.Children.Add(group);
        }
        dashboard.Content = content;
    }
    private void RefreshDashboardChrome()
    {
        if (dashboardButton is null) return;
        dashboardButton.Content = Locale.Get("phone.dashboard.title");
        AutomationProperties.SetName(dashboardButton, Locale.Get("phone.dashboard.title"));
        ToolTipService.SetToolTip(dashboardButton, Locale.Get("dashboard.sidebarHelp"));
    }
    private TextBlock WorkspaceStatusCounts(Workspace workspace)
    {
        var value = new TextBlock { FontSize = 10, Opacity = .75, TextWrapping = TextWrapping.Wrap };
        workspaceStatusCounts[workspace.Id] = value; UpdateWorkspaceStatusCounts(workspace.Id, value); return value;
    }
    private void UpdateWorkspaceStatusCounts(string id, TextBlock label)
    {
        var badges = WorkDashboard.WorkspaceBadges(service.Snapshot.Sessions.Where(s => s.WorkspaceId == id), DashboardAttention);
        label.Text = string.Join(" · ", new[]
        {
            ("waiting", badges.Questions + badges.Permissions), ("error", badges.Errors),
            ("running", badges.Running), ("completed", badges.Done), ("stopped", badges.Stopped), ("idle", badges.Idle),
        }.Where(value => value.Item2 > 0).Select(value => StateLabel(value.Item1) + " " + value.Item2));
        label.Visibility = label.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private sealed partial class PaneView
    {
        internal WorkDashboard.Attention DashboardAttention => WorkDashboard.Pending(toolPermissions);
    }
}
