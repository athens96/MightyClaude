using MightyClaude.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, TextBlock> dashboardGitLabels = [];
    private readonly Dictionary<string, (string Path, WorkspaceGitInfo? Info, DateTimeOffset At)> dashboardGit = [];
    private CancellationTokenSource? dashboardGitCancellation;

    /// <summary>
    /// The row's 12pt <c>ink2</c> line (M/DashboardView.swift:312-336): the provider's mark and name, the
    /// beta capsule, then "· model · 02:14 · 41%" or "· 4 days ago"; a shell or browser names its kind.
    /// </summary>
    private FrameworkElement DashboardProviderLine(WorkDashboard.Card card)
    {
        var line = new PillWrapPanel();
        var ink = brushes.Brush(DesignToken.Ink2);
        if (card.Session.Kind == "claude")
        {
            var provider = ProviderMarkView.Labelled(ProviderMark.Label(card.Session.Provider), card.Session.Provider, 12, FontWeights.Normal);
            if (provider is TextBlock single) single.Foreground = ink;
            else foreach (var words in ((Panel)provider).Children.OfType<TextBlock>()) words.Foreground = ink;
            AutomationProperties.SetAutomationId(provider, "dashboard-provider-" + card.Session.Id);
            line.Children.Add(provider);
            if (ProviderCatalog.ShowsBetaBadge(card.Session)) line.Children.Add(BetaBadgeView.Create(brushes));
        }
        var meta = new TextBlock { Text = DashboardMeta(card), FontSize = 12, Foreground = ink, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(meta, FontNumeralAlignment.Tabular);
        dashboardClocks[card.Session.Id] = meta;
        line.Children.Add(meta);
        return line;
    }

    /// <summary>The row's words after the provider: "· model · detail" for an agent, the kind for a shell or a browser.</summary>
    private string DashboardMeta(WorkDashboard.Card card)
    {
        if (card.Session.Kind != "claude") return Locale.Get(card.Session.Kind == "browser" ? "browser.tab.title" : "dashboard.kind.shell");
        var parts = new List<string>();
        if (card.Model is { } model) parts.Add(ModelLabel.Text(model));
        if (WorkDashboard.SidebarDetail(card, DateTimeOffset.UtcNow) is { Length: > 0 } detail) parts.Add(detail);
        return parts.Count == 0 ? "" : "· " + string.Join(" · ", parts);
    }

    /// <summary>
    /// A workspace group's head (M/DashboardView.swift:224-249): the name in the heading font at 17, the
    /// path 11.5 mono <c>ink2</c>, the Git badge, then the 26-high capsules Files (<c>card</c>) and
    /// Add pane (<c>ink</c> under <c>card</c> words). The capsules wrap under the name when narrow.
    /// </summary>
    private (FrameworkElement Header, Button Files, Button Add) DashboardWorkspaceHeader(Workspace workspace)
    {
        var header = new Grid { ColumnSpacing = 10, RowSpacing = 8, Padding = new(2, 0, 2, 0) };
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.RowDefinitions.Add(new() { Height = GridLength.Auto }); header.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var details = new Grid { ColumnSpacing = 10 };
        details.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); details.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); details.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var heading = SafeButton(workspace.Name, () => SelectWorkspace(workspace.Id));
        heading.Content = new TextBlock { Text = workspace.Name, FontSize = DesignMetrics.Type.Header, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(DesignMetrics.Font.Heading), FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
        heading.Padding = new(0); heading.MinHeight = 0; heading.BorderThickness = new(0); heading.VerticalAlignment = VerticalAlignment.Center;
        PaintPlainButton(heading, brushes.Transparent, brushes.Transparent, ink: brushes.Brush(DesignToken.Ink));
        details.Children.Add(heading);
        var path = new TextBlock { Text = workspace.Path, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(DesignMetrics.Font.Mono), FontSize = 11.5, Foreground = brushes.Brush(DesignToken.Ink2), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(path, workspace.Path); Grid.SetColumn(path, 1); details.Children.Add(path);
        var git = new TextBlock { FontSize = DesignMetrics.Type.Pill, Foreground = brushes.Brush(DesignToken.Ink2), TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(git, "dashboard-git-" + workspace.Id);
        dashboardGitLabels[workspace.Id] = git; Grid.SetColumn(git, 2); details.Children.Add(git); ApplyDashboardGit(workspace.Id, workspace.Path);
        header.Children.Add(details);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var files = SafeButton(Locale.Get("files.pane.title"), async () =>
        {
            if (dialogOpen || !service.Snapshot.Workspaces.Any(w => w.Id == workspace.Id)) return;
            HideDashboard(); await OpenFilePane(workspace.Id);
        });
        DashboardCapsule(files, "\uE8B7", Locale.Get("files.pane.title"), brushes.Brush(DesignToken.Card), brushes.Brush(DesignToken.CardRaised), brushes.Brush(DesignToken.Ink));
        AutomationProperties.SetAutomationId(files, "dashboard-open-files-" + workspace.Id); ToolTipService.SetToolTip(files, Locale.Get("menu.showFiles"));
        var add = new Button { Flyout = DashboardAddMenu(workspace.Id) };
        DashboardCapsule(add, "\uE710", Locale.Get("workspace.addPane"), brushes.Brush(DesignToken.Ink), brushes.Brush(DesignToken.Ink2), brushes.Brush(DesignToken.Card));
        AutomationProperties.SetAutomationId(add, "dashboard-add-session-" + workspace.Id);
        AutomationProperties.SetName(add, Locale.Get("workspace.addPaneAccessibility", new Dictionary<string, string> { ["workspace"] = workspace.Name }));
        ToolTipService.SetToolTip(add, Locale.Get("workspace.addPaneHelp"));
        actions.Children.Add(files); actions.Children.Add(add); Grid.SetColumn(actions, 1); header.Children.Add(actions);
        header.SizeChanged += (_, args) =>
        {
            var narrow = args.NewSize.Width < 570;
            Grid.SetColumnSpan(details, narrow ? 2 : 1);
            Grid.SetColumn(actions, narrow ? 0 : 1); Grid.SetRow(actions, narrow ? 1 : 0); Grid.SetColumnSpan(actions, narrow ? 2 : 1);
        };
        return (header, files, add);
    }

    /// <summary>The group head's capsule (M/DashboardView.swift:234-249): a symbol and 11.5 bold words, h11, 26 high.</summary>
    private void DashboardCapsule(Button button, string glyph, string words, SolidColorBrush fill, SolidColorBrush hover, SolidColorBrush ink)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        label.Children.Add(new FontIcon { Glyph = glyph, FontSize = 11, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(new TextBlock { Text = words, FontSize = 11.5, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        button.Content = label; button.Height = DashboardCapsuleHeight; button.MinHeight = 0; button.Padding = new(11, 0, 11, 0);
        button.CornerRadius = new(DashboardCapsuleHeight / 2); button.BorderThickness = new(0); button.VerticalAlignment = VerticalAlignment.Center;
        PaintPlainButton(button, fill, hover, ink: ink);
    }

    /// <summary>The group head's capsule height (M/DashboardView.swift:238).</summary>
    internal const double DashboardCapsuleHeight = 26;

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
        MarkAddPaneMenu(menu, AddPaneMenu.Entries());
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
