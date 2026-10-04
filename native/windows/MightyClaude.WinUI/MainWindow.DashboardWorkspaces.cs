using MightyClaude.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, TextBlock> dashboardGitLabels = [];
    private readonly Dictionary<string, (string Path, WorkspaceGitInfo? Info, DateTimeOffset At)> dashboardGit = [];
    private CancellationTokenSource? dashboardGitCancellation;

    private FrameworkElement DashboardProviderLine(WorkDashboard.Card card)
    {
        var line = new PillWrapPanel();
        var provider = ProviderMarkView.Labelled(ProviderMark.Label(card.Session.Provider), card.Session.Provider, 11, FontWeights.Normal);
        AutomationProperties.SetAutomationId(provider, "dashboard-provider-" + card.Session.Id);
        line.Children.Add(provider);
        if (ProviderCatalog.ShowsBetaBadge(card.Session)) line.Children.Add(BetaBadgeView.Create(DarkTheme));
        if (card.Model is { } model) line.Children.Add(new TextBlock { Text = "· " + ModelLabel.Text(model), FontSize = 11, Opacity = .7, TextTrimming = TextTrimming.CharacterEllipsis });
        return line;
    }

    private FrameworkElement DashboardWorkspaceHeader(Workspace workspace)
    {
        var header = new Grid { ColumnSpacing = 10, RowSpacing = 8 };
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.RowDefinitions.Add(new() { Height = GridLength.Auto }); header.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var details = new StackPanel { Spacing = 4 };
        var heading = SafeButton(workspace.Name, () => SelectWorkspace(workspace.Id));
        heading.HorizontalAlignment = HorizontalAlignment.Left; heading.FontSize = 17; heading.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(DesignMetrics.Font.Heading); heading.FontWeight = FontWeights.Bold;
        details.Children.Add(heading);
        var path = new TextBlock { Text = workspace.Path, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(DesignMetrics.Font.Mono), FontSize = 11, Opacity = .7, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTipService.SetToolTip(path, workspace.Path); details.Children.Add(path);
        var git = new TextBlock { FontSize = 11, Opacity = .7, TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(git, "dashboard-git-" + workspace.Id);
        dashboardGitLabels[workspace.Id] = git; details.Children.Add(git); ApplyDashboardGit(workspace.Id, workspace.Path);
        header.Children.Add(details);
        var actions = new PillWrapPanel();
        var files = SafeButton(Locale.Get("files.pane.title"), async () =>
        {
            if (dialogOpen || !service.Snapshot.Workspaces.Any(w => w.Id == workspace.Id)) return;
            HideDashboard(); await OpenFilePane(workspace.Id);
        });
        AutomationProperties.SetAutomationId(files, "dashboard-open-files-" + workspace.Id); ToolTipService.SetToolTip(files, Locale.Get("menu.showFiles"));
        var add = new Button { Content = "+ " + Locale.Get("workspace.addPane"), Flyout = DashboardAddMenu(workspace.Id) };
        AutomationProperties.SetAutomationId(add, "dashboard-add-session-" + workspace.Id);
        AutomationProperties.SetName(add, Locale.Get("workspace.addPaneAccessibility", new Dictionary<string, string> { ["workspace"] = workspace.Name }));
        ToolTipService.SetToolTip(add, Locale.Get("workspace.addPaneHelp"));
        actions.Children.Add(files); actions.Children.Add(add); Grid.SetColumn(actions, 1); header.Children.Add(actions);
        header.SizeChanged += (_, args) =>
        {
            var narrow = args.NewSize.Width < 570;
            Grid.SetColumnSpan(details, narrow ? 2 : 1);
            Grid.SetColumn(actions, narrow ? 0 : 1); Grid.SetRow(actions, narrow ? 1 : 0); Grid.SetColumnSpan(actions, narrow ? 2 : 1);
            actions.MaxWidth = Math.Max(1, args.NewSize.Width);
        };
        return header;
    }

    private MenuFlyout DashboardAddMenu(string workspaceId)
    {
        var menu = new MenuFlyout();
        foreach (var entry in AddPaneMenu.Entries())
        {
            if (entry == AddPaneMenu.Separator) { menu.Items.Add(new MenuFlyoutSeparator()); continue; }
            if (entry == AddPaneMenu.OpenProject) { menu.Items.Add(OpenProjectMenuItem()); continue; }
            var title = entry switch { AddPaneMenu.Shell => Locale.Get("session.newTab.shell"), AddPaneMenu.Browser => Locale.Get("browser.newTab"), AddPaneMenu.Files => Locale.Get("menu.showFiles"), _ => ProviderCatalog.BetaLabel(AddPaneMenu.AgentProvider(entry)!, ProviderCatalog.Name(AddPaneMenu.AgentProvider(entry)!)) };
            var item = MenuItem(title, () => DashboardAddPane(workspaceId, entry));
            AutomationProperties.SetAutomationId(item, "dashboard-add-" + entry + "-" + workspaceId); menu.Items.Add(item);
        }
        menu.Opening += (_, _) => { foreach (var item in menu.Items) item.IsEnabled = !dialogOpen && service.Snapshot.Workspaces.Any(w => w.Id == workspaceId); };
        return menu;
    }

    private Task DashboardAddPane(string workspaceId, string entry) => Act(async () =>
    {
        if (dialogOpen || !service.Snapshot.Workspaces.Any(w => w.Id == workspaceId)) return;
        await SelectWorkspace(workspaceId);
        // Runtime refresh can yield. A later navigation must never add to the
        // newly selected workspace using an older dashboard menu's intent.
        if (service.Snapshot.ActiveWorkspaceId != workspaceId || !service.Snapshot.Workspaces.Any(w => w.Id == workspaceId)) return;
        if (entry == AddPaneMenu.Shell) await AddPane("shell");
        else if (entry == AddPaneMenu.Browser) await AddBrowserPane();
        else if (entry == AddPaneMenu.Files) await OpenFilePane(workspaceId);
        else if (AddPaneMenu.AgentProvider(entry) is { } provider) await AddAgentPane(provider, null);
    });

    private void ApplyDashboardGit(string id, string path)
    {
        if (!dashboardGitLabels.TryGetValue(id, out var label)) return;
        var info = dashboardGit.TryGetValue(id, out var cached) && cached.Path == path ? cached.Info : null;
        label.Text = info?.Badge ?? ""; label.Visibility = info is null ? Visibility.Collapsed : Visibility.Visible;
        if (info is not null) ToolTipService.SetToolTip(label, "Git · " + info.Label + " · " + Locale.Get(info.IsDirty ? "git.dirty" : "git.clean") + "\n" + Locale.Get("git.localUpstream"));
    }

    private async void RefreshDashboardGit()
    {
        if (options.SmokeTest || closing || !showsDashboard || dashboardGitCancellation is not null) return;
        var workspaces = service.Snapshot.Workspaces;
        foreach (var id in dashboardGit.Keys.Where(id => !workspaces.Any(w => w.Id == id)).ToArray()) dashboardGit.Remove(id);
        var pending = workspaces.Where(w => !dashboardGit.TryGetValue(w.Id, out var c) || c.Path != w.Path || DateTimeOffset.UtcNow - c.At > TimeSpan.FromSeconds(10)).ToArray();
        if (pending.Length == 0) return;
        var cancellation = new CancellationTokenSource(); dashboardGitCancellation = cancellation;
        try
        {
            foreach (var workspace in pending)
            {
                if (cancellation.IsCancellationRequested || closing || !showsDashboard) break;
                var info = await WorkspaceGitInfo.ReadAsync(workspace.Path, cancellation.Token);
                if (cancellation.IsCancellationRequested || closing || !showsDashboard) break;
                if (!service.Snapshot.Workspaces.Any(w => w.Id == workspace.Id && w.Path == workspace.Path)) continue;
                dashboardGit[workspace.Id] = (workspace.Path, info, DateTimeOffset.UtcNow); ApplyDashboardGit(workspace.Id, workspace.Path);
            }
        }
        catch (Exception) { /* Local Git availability must never block the dashboard. */ }
        finally { if (ReferenceEquals(dashboardGitCancellation, cancellation)) dashboardGitCancellation = null; cancellation.Dispose(); }
    }
    private void StopDashboardGit() { dashboardGitCancellation?.Cancel(); dashboardGitCancellation = null; }
}
