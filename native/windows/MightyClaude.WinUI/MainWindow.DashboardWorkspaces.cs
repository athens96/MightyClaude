using System.Globalization;
using MightyClaude.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>Each group head's Git capsule (M/WorkspaceGitView.swift:36-56): the capsule, its branch words, the dirty dot and the ahead / behind counts.</summary>
    private sealed record DashboardGitBadge(Border Capsule, TextBlock Label, FrameworkElement Dirty, TextBlock Ahead, TextBlock Behind);
    private readonly Dictionary<string, DashboardGitBadge> dashboardGitLabels = [];
    private readonly Dictionary<string, (string Path, WorkspaceGitInfo? Info, DateTimeOffset At)> dashboardGit = [];
    private CancellationTokenSource? dashboardGitCancellation;

    /// <summary>
    /// The row's 12pt <c>ink2</c> line (M/DashboardView.swift:313-337), its parts 4 apart: the provider's mark and
    /// name, the beta capsule, then "· model · 02:14 · 41%" or "· 4 days ago". A pane that is not an agent's names
    /// its kind, and a shell says it runs in the app's own terminal in semibold <c>waitText</c>.
    /// </summary>
    private FrameworkElement DashboardProviderLine(WorkDashboard.Card card)
    {
        // 17 high, as the Mac lays the line out (a row is 60: v11, the 20-high title, 1, this line).
        var line = new Grid { ColumnSpacing = 4, MinHeight = DashboardMetaHeight };
        var ink = brushes.Brush(DesignToken.Ink2);
        void Add(FrameworkElement part, bool rest = false)
        {
            line.ColumnDefinitions.Add(new() { Width = rest ? new(1, GridUnitType.Star) : GridLength.Auto });
            part.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(part, line.ColumnDefinitions.Count - 1); line.Children.Add(part);
        }
        if (card.Session.Kind == "claude")
        {
            var provider = ProviderMarkView.Labelled(ProviderMark.Label(card.Session.Provider), card.Session.Provider, 12, FontWeights.Normal);
            if (provider is TextBlock single) single.Foreground = ink;
            else foreach (var words in ((Panel)provider).Children.OfType<TextBlock>()) words.Foreground = ink;
            AutomationProperties.SetAutomationId(provider, "dashboard-provider-" + card.Session.Id);
            Add(provider);
            if (ProviderCatalog.ShowsBetaBadge(card.Session)) Add(BetaBadgeView.Create(brushes));
        }
        var local = card.Session.Kind == "shell";
        var meta = new TextBlock { Text = DashboardMeta(card), FontSize = 12, Foreground = ink, TextTrimming = TextTrimming.CharacterEllipsis };
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(meta, FontNumeralAlignment.Tabular);
        dashboardClocks[card.Session.Id] = meta;
        Add(meta, rest: !local);
        if (local) Add(new TextBlock { Text = Locale.Get("phone.card.localTerminal"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = brushes.Brush(DesignToken.WaitText), TextTrimming = TextTrimming.CharacterEllipsis }, rest: true);
        return line;
    }

    /// <summary>The meta line's height on the Mac, measured on docs/design-system/crops/dashboard-top-light.webp (rows of 60).</summary>
    internal const double DashboardMetaHeight = 17;

    /// <summary>
    /// The row's words after the provider (M/DashboardView.swift:318-334): "· model · detail" for an agent, "shell ·"
    /// before the local-terminal words, and the kind for the other panes (an agent's own terminal or browser names its agent).
    /// </summary>
    private string DashboardMeta(WorkDashboard.Card card)
    {
        var kind = card.Session.Kind;
        if (kind == "shell") return Locale.Get("dashboard.kind.shell") + " ·";
        if (kind == "browser") return Locale.Get("browser.tab.title");
        if (FilePaneKind.IsFilePane(kind)) return Locale.Get("files.pane.title");
        if (AgentIOPaneKind.IsAgentIO(kind)) return Locale.Get(kind == AgentIOPaneKind.Terminal ? "dashboard.kind.agentTerminal" : "dashboard.kind.agentBrowser") + " · " + ProviderMark.Label(card.Session.Provider);
        if (kind != "claude") return kind;
        var parts = new List<string>();
        if (card.Model is { } model) parts.Add(ModelLabel.Text(model));
        if (WorkDashboard.SidebarDetail(card, DateTimeOffset.UtcNow) is { Length: > 0 } detail) parts.Add(detail);
        return parts.Count == 0 ? "" : "· " + string.Join(" · ", parts);
    }

    /// <summary>
    /// A workspace group's head (M/DashboardView.swift:207-238), its parts 10 apart: the name in the heading font
    /// at 17, the path 11.5 mono <c>ink2</c> cut in the middle, the Git capsule, then (at least 8 further) the
    /// 26-high capsules Files (<c>card</c>) and Add pane (<c>ink</c> under <c>card</c> words). The capsules wrap
    /// under the name when narrow, 8 below it; on one line the head is as high as its capsules, so the rows' card
    /// stands the group's 10 under it (M/DashboardView.swift:207).
    /// </summary>
    private (FrameworkElement Header, Button Files, Button Add) DashboardWorkspaceHeader(Workspace workspace)
    {
        const double spacing = 10, gap = spacing + 8 + spacing, wrapped = 8;
        var header = new Grid { Padding = new(2, 0, 2, 0) };
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.RowDefinitions.Add(new() { Height = GridLength.Auto }); header.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var details = new Grid { ColumnSpacing = spacing, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        details.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); details.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); details.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var heading = SafeButton(workspace.Name, () => SelectWorkspace(workspace.Id));
        heading.Content = new TextBlock { Text = workspace.Name, FontSize = DesignMetrics.Type.Header, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(DesignMetrics.Font.Heading), FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
        heading.Padding = new(0); heading.MinHeight = 0; heading.BorderThickness = new(0); heading.VerticalAlignment = VerticalAlignment.Center;
        PaintPlainButton(heading, brushes.Transparent, brushes.Transparent, ink: brushes.Brush(DesignToken.Ink));
        details.Children.Add(heading);
        var path = new TextBlock { Text = workspace.Path, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(DesignMetrics.Font.Mono), FontSize = 11.5, Foreground = brushes.Brush(DesignToken.Ink2), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(path, workspace.Path); Grid.SetColumn(path, 1); details.Children.Add(path);
        var git = DashboardGitCapsule(workspace.Id);
        Grid.SetColumn(git.Capsule, 2); details.Children.Add(git.Capsule); ApplyDashboardGit(workspace.Id, workspace.Path);
        header.Children.Add(details);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        var files = SafeButton(Locale.Get("files.pane.title"), async () =>
        {
            if (dialogOpen || !service.Snapshot.Workspaces.Any(w => w.Id == workspace.Id)) return;
            HideDashboard(); await OpenFilePane(workspace.Id);
        });
        DashboardCapsule(files, PanelSymbol.FolderBold(brushes.Brush(DesignToken.Ink)), Locale.Get("files.pane.title"), brushes.Brush(DesignToken.Card), brushes.Brush(DesignToken.CardRaised), brushes.Brush(DesignToken.Ink));
        AutomationProperties.SetAutomationId(files, "dashboard-open-files-" + workspace.Id); ToolTipService.SetToolTip(files, Locale.Get("menu.showFiles"));
        var add = new Button { Flyout = DashboardAddMenu(workspace.Id) };
        DashboardCapsule(add, PanelSymbol.PlusBold(brushes.Brush(DesignToken.Card)), Locale.Get("workspace.addPane"), brushes.Brush(DesignToken.Ink), brushes.Brush(DesignToken.Ink2), brushes.Brush(DesignToken.Card));
        AutomationProperties.SetAutomationId(add, "dashboard-add-session-" + workspace.Id);
        AutomationProperties.SetName(add, Locale.Get("workspace.addPaneAccessibility", new Dictionary<string, string> { ["workspace"] = workspace.Name }));
        ToolTipService.SetToolTip(add, Locale.Get("workspace.addPaneHelp"));
        actions.Children.Add(files); actions.Children.Add(add); Grid.SetColumn(actions, 1); header.Children.Add(actions);
        // The name keeps its words, the path gives way from its middle, and the capsules stay whole (M/DashboardView.swift:211-212).
        void Fit()
        {
            if (header.ActualWidth <= 0) return;
            var narrow = header.ActualWidth < 570;
            // A grid keeps its row spacing under an empty second row too: the 8 is there only while the capsules stand in it.
            if (header.RowSpacing != (narrow ? wrapped : 0)) header.RowSpacing = narrow ? wrapped : 0;
            Grid.SetColumnSpan(details, narrow ? 2 : 1);
            Grid.SetColumn(actions, narrow ? 0 : 1); Grid.SetRow(actions, narrow ? 1 : 0); Grid.SetColumnSpan(actions, narrow ? 2 : 1);
            var room = header.ActualWidth - header.Padding.Left - header.Padding.Right - (narrow ? 0 : actions.ActualWidth + gap);
            heading.MaxWidth = Math.Max(0, room);
            var badge = git.Capsule.Visibility == Visibility.Visible ? git.Capsule.ActualWidth + spacing : 0;
            MiddleTrim.Fit(path, workspace.Path, Math.Max(0, room - heading.ActualWidth - spacing - badge));
        }
        header.SizeChanged += (_, _) => Fit(); heading.SizeChanged += (_, _) => Fit(); actions.SizeChanged += (_, _) => Fit(); git.Capsule.SizeChanged += (_, _) => Fit();
        return (header, files, add);
    }

    /// <summary>
    /// The group head's capsule (M/DashboardView.swift:215-231): a bold symbol and 11.5 bold words in a label,
    /// h11, 26 high. The symbol carries the capsule's ink itself (a stroke takes no foreground from the button).
    /// </summary>
    private void DashboardCapsule(Button button, FrameworkElement symbol, string words, SolidColorBrush fill, SolidColorBrush hover, SolidColorBrush ink)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DashboardCapsuleLabelSpacing };
        label.Children.Add(symbol);
        label.Children.Add(new TextBlock { Text = words, FontSize = 11.5, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        button.Content = label; button.Height = DashboardCapsuleHeight; button.MinHeight = 0; button.MinWidth = 0; button.Padding = new(11, 0, 11, 0);
        button.CornerRadius = new(DashboardCapsuleHeight / 2); button.BorderThickness = new(0); button.VerticalAlignment = VerticalAlignment.Center;
        PaintPlainButton(button, fill, hover, ink: ink);
    }

    /// <summary>The group head's capsule height (M/DashboardView.swift:218) and the gap a label leaves between its symbol and words (measured on docs/design-system/crops/dashboard-group-light.webp).</summary>
    internal const double DashboardCapsuleHeight = 26, DashboardCapsuleLabelSpacing = 8;

    /// <summary>
    /// The Git capsule (M/WorkspaceGitView.swift:39-50): 10pt medium <c>ink2</c> on the subtle wash, padding h7 v3,
    /// its parts 5 apart: the branch symbol, the branch, a 5pt <c>accent</c> dot while there are changes, ↑ahead ↓behind.
    /// At most 260 wide; hidden until the workspace has been read.
    /// </summary>
    private DashboardGitBadge DashboardGitCapsule(string workspaceId)
    {
        var ink = brushes.Brush(DesignToken.Ink2);
        TextBlock Words() => new() { FontSize = DesignMetrics.Type.Small, FontWeight = FontWeights.Medium, Foreground = ink, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var label = Words(); var ahead = Words(); var behind = Words();
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(ahead, FontNumeralAlignment.Tabular); Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(behind, FontNumeralAlignment.Tabular);
        var dirty = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 5, Height = 5, Fill = brushes.Brush(DesignToken.Accent), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(dirty, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        // Only the branch gives way in a capsule that reached its 260 (the symbol, the dot and the counts keep their room).
        label.MaxWidth = 170;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        foreach (var part in new FrameworkElement[] { PanelSymbol.Branch(ink), label, dirty, ahead, behind }) row.Children.Add(part);
        var capsule = new Border { Child = row, Padding = new(7, 3, 7, 3), CornerRadius = new(DashboardGitRadius), Background = brushes.Subtle, MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(capsule, "dashboard-git-" + workspaceId);
        return dashboardGitLabels[workspaceId] = new(capsule, label, dirty, ahead, behind);
    }

    /// <summary>The Git capsule's corner: half its height (a 10pt line and v3).</summary>
    private const double DashboardGitRadius = 10;

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
        if (!dashboardGitLabels.TryGetValue(id, out var badge)) return;
        var info = dashboardGit.TryGetValue(id, out var cached) && cached.Path == path ? cached.Info : null;
        badge.Capsule.Visibility = info is null ? Visibility.Collapsed : Visibility.Visible;
        badge.Label.Text = info?.Label ?? "";
        badge.Dirty.Visibility = info is { IsDirty: true } ? Visibility.Visible : Visibility.Collapsed;
        static string Count(string arrow, int? count) => count is int value && value > 0 ? arrow + value.ToString(CultureInfo.InvariantCulture) : "";
        badge.Ahead.Text = Count("↑", info?.Ahead); badge.Ahead.Visibility = badge.Ahead.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        badge.Behind.Text = Count("↓", info?.Behind); badge.Behind.Visibility = badge.Behind.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (info is null) return;
        var state = "Git · " + info.Label + " · " + Locale.Get(info.IsDirty ? "git.dirty" : "git.clean");
        ToolTipService.SetToolTip(badge.Capsule, state + "\n" + Locale.Get("git.localUpstream")); AutomationProperties.SetName(badge.Capsule, state);
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

/// <summary>
/// The SF Symbols of the dashboard, the files pane and the sheets that Segoe Fluent Icons has no close glyph
/// for, drawn as strokes in the symbol's own point box (sizes measured on docs/design-system/crops, 2 px = 1 pt).
/// The ink is one of the window's shared brushes, so a theme toggle recolours a symbol already on screen.
/// </summary>
internal static class PanelSymbol
{
    /// <summary>A stroke's next point: a line to it, or with a radius an arc to it (clockwise, y down).</summary>
    internal readonly record struct To(double X, double Y, double Radius = 0);

    /// <summary>Strokes with round caps and joins in a <paramref name="width"/> × <paramref name="height"/> box; each stroke starts at its first point.</summary>
    internal static ShapePath Strokes(double width, double height, double thickness, Brush ink, params To[][] strokes)
    {
        var geometry = new PathGeometry();
        foreach (var stroke in strokes)
        {
            var figure = new PathFigure { StartPoint = new Point(stroke[0].X, stroke[0].Y), IsClosed = false, IsFilled = false };
            foreach (var to in stroke.Skip(1))
                figure.Segments.Add(to.Radius > 0
                    ? new ArcSegment { Point = new Point(to.X, to.Y), Size = new Size(to.Radius, to.Radius), SweepDirection = SweepDirection.Clockwise }
                    : new LineSegment { Point = new Point(to.X, to.Y) });
            geometry.Figures.Add(figure);
        }
        var path = new ShapePath
        {
            Data = geometry, Stroke = ink, StrokeThickness = thickness, Width = width, Height = height, IsHitTestVisible = false,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAccessibilityView(path, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return path;
    }

    /// <summary>
    /// <c>folder</c> at 11.5 bold: a 13 × 11 outline at 1.5 with its tab, over the thinner line under the tab; 1.5 of
    /// side bearing. The Mac's 12.9 × 10.9 with its edges set on whole points, so the line stays clear of the
    /// outline at one pixel a point.
    /// </summary>
    internal static FrameworkElement FolderBold(Brush ink)
    {
        var folder = new Grid { Width = 13, Height = 11, Margin = new Thickness(1.5, 0, 1.5, 0), VerticalAlignment = VerticalAlignment.Center };
        folder.Children.Add(Strokes(13, 11, 1.5, ink, [[new(0.75, 3), new(3, 0.75, 2.25), new(4.1, 0.75), new(5.6, 2.25), new(10.25, 2.25), new(12.25, 4.25, 2), new(12.25, 8.25), new(10.25, 10.25, 2), new(2.75, 10.25), new(0.75, 8.25, 2), new(0.75, 3)]]));
        folder.Children.Add(Strokes(13, 11, 1, ink, [[new(3, 4.5), new(10, 4.5)]]));
        return folder;
    }

    /// <summary><c>plus</c> at 11.5 bold: 9.6 square, 1.6 strokes; about 1 of side bearing.</summary>
    internal static ShapePath PlusBold(Brush ink)
    {
        var plus = Strokes(9.6, 9.6, 1.6, ink, [new(0.8, 4.8), new(8.8, 4.8)], [new(4.8, 0.8), new(4.8, 8.8)]);
        plus.Margin = new Thickness(1.3, 0, 0.7, 0);
        return plus;
    }

    /// <summary><c>line.3.horizontal.decrease</c> at 10: three centred lines 9.8, 7.9 and 5.9 wide, 2.55 apart.</summary>
    internal static ShapePath FilterLines(Brush ink) => Strokes(9.8, 6, 0.9, ink, [new(0.45, 0.45), new(9.35, 0.45)], [new(1.4, 3), new(8.4, 3)], [new(2.4, 5.55), new(7.4, 5.55)]);

    /// <summary><c>waveform.path.ecg</c> at 11: one pulse on a 10.7 × 10.6 line.</summary>
    internal static ShapePath Pulse(Brush ink) => Strokes(10.7, 10.6, 0.9, ink, [[new(0.45, 5.3), new(2.7, 5.3), new(3.7, 0.45), new(5.9, 10.15), new(7.5, 3.3), new(8.2, 5.3), new(10.25, 5.3)]]);

    /// <summary><c>arrow.triangle.branch</c> at 10 medium: a stem and a branch off it, each ending in an arrow head.</summary>
    internal static ShapePath Branch(Brush ink) => Strokes(8.6, 10, 1.1, ink,
        [new(2.3, 9.4), new(2.3, 1.2)], [new(0.7, 3), new(2.3, 0.9), new(3.9, 3)],
        [new(2.3, 7.8), new(4.6, 6.4), new(6.3, 4.4), new(6.3, 1.2)], [new(4.7, 3), new(6.3, 0.9), new(7.9, 3)]);

    /// <summary><c>chevron.left.forwardslash.chevron.right</c>: the mark of a source file, 14 × 9 scaled to <paramref name="size"/> points of type.</summary>
    internal static ShapePath Code(Brush ink, double size = 11)
    {
        var k = size / 11;
        return Strokes(14 * k, 9 * k, 1 * k, ink, [new(4.2 * k, 1 * k), new(0.8 * k, 4.5 * k), new(4.2 * k, 8 * k)], [new(8.3 * k, 0.6 * k), new(5.7 * k, 8.4 * k)], [new(9.8 * k, 1 * k), new(13.2 * k, 4.5 * k), new(9.8 * k, 8 * k)]);
    }

    /// <summary><c>doc.richtext</c>: a page with a folded corner, a picture and a line of words, 9.4 × 11.6 scaled to <paramref name="size"/> points of type.</summary>
    internal static ShapePath RichText(Brush ink, double size = 11)
    {
        var k = size / 11;
        To At(double x, double y, double radius = 0) => new(x * k, y * k, radius * k);
        return Strokes(9.4 * k, 11.6 * k, 0.9 * k, ink,
            [At(0.5, 2), At(2, 0.5, 1.5), At(5.9, 0.5), At(8.9, 3.5), At(8.9, 9.6), At(7.4, 11.1, 1.5), At(2, 11.1), At(0.5, 9.6, 1.5), At(0.5, 2)],
            [At(5.9, 0.5), At(5.9, 3.5), At(8.9, 3.5)],
            [At(2.5, 5), At(4.7, 5), At(4.7, 7.2), At(2.5, 7.2), At(2.5, 5)], [At(2.5, 9), At(6.9, 9)]);
    }

    /// <summary><c>arrow.up.left.and.down.right.and.arrow.up.right.and.down.left</c> at 12: two crossed arrows to the four corners.</summary>
    internal static ShapePath Fit(Brush ink) => Strokes(12, 12, 1, ink,
        [new(1, 1), new(11, 11)], [new(11, 1), new(1, 11)],
        [new(1, 4.2), new(1, 1), new(4.2, 1)], [new(11, 7.8), new(11, 11), new(7.8, 11)], [new(7.8, 1), new(11, 1), new(11, 4.2)], [new(4.2, 11), new(1, 11), new(1, 7.8)]);

    /// <summary><c>arrow.turn.up.right</c> at 8: up, then right to an arrow head (a symbolic link).</summary>
    internal static ShapePath TurnUpRight(Brush ink) => Strokes(7, 7.5, 0.8, ink, [new(0.6, 7), new(0.6, 3.6), new(2.1, 2.1, 1.5), new(6.3, 2.1)], [new(4.7, 0.5), new(6.3, 2.1), new(4.7, 3.7)]);

    /// <summary><c>arrow.up.circle</c> at 11: an arrow up in a ring.</summary>
    internal static ShapePath ArrowUpCircle(Brush ink) => Strokes(11.2, 11.2, 0.9, ink,
        [new(5.6, 0.6), new(5.6, 10.6, 5), new(5.6, 0.6, 5)], [new(5.6, 8.2), new(5.6, 3.2)], [new(3.5, 5.2), new(5.6, 3.1), new(7.7, 5.2)]);
}

/// <summary>
/// The Mac's <c>.truncationMode(.middle)</c> and <c>(.head)</c>, which WinUI lacks: a text block shows the longest
/// head and tail of whole text elements around an ellipsis that fit a width (never cutting inside a surrogate pair
/// or a composed Hangul syllable), found by measuring the block itself. End trimming stays as the backstop.
/// </summary>
internal static class MiddleTrim
{
    /// <summary>
    /// Shows <paramref name="full"/> in <paramref name="block"/>, cut in the middle (or, with <paramref name="head"/>,
    /// at its start) when it is wider than <paramref name="width"/>, and caps the block at that width.
    /// </summary>
    internal static void Fit(TextBlock block, string full, double width, bool head = false)
    {
        block.MaxWidth = double.PositiveInfinity;
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        bool Fits(string text) { block.Text = text; block.Measure(unbounded); return block.DesiredSize.Width <= width; }
        var starts = StringInfo.ParseCombiningCharacters(full);
        if (!Fits(full) && starts.Length > 2)
        {
            string Kept(int keep) => head ? "…" + full[starts[starts.Length - keep]..] : full[..starts[(keep + 1) / 2]] + "…" + (keep / 2 == 0 ? "" : full[starts[starts.Length - keep / 2]..]);
            // The longest kept count (head + tail elements) that fits; 1 always shows the first element.
            int low = 1, high = starts.Length - 1;
            while (low < high) { var mid = (low + high + 1) / 2; if (Fits(Kept(mid))) low = mid; else high = mid - 1; }
            block.Text = Kept(low);
        }
        block.MaxWidth = Math.Max(0, width);
    }
}
