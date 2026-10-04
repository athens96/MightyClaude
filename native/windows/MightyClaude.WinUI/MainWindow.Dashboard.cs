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
    /// <summary>Each dashboard row's meta words after the provider, recounted on every tick.</summary>
    private readonly Dictionary<string, TextBlock> dashboardClocks = [];
    private readonly Dictionary<string, TextBlock> sidebarDetails = [];
    private readonly Dictionary<string, StatusCountsView> workspaceStatusCounts = [];
    private Button? dashboardButton;
    /// <summary>The work-status entry's wash (card at radius 10 while the dashboard shows), its title and its counts.</summary>
    private Border? dashboardEntry;
    private TextBlock? dashboardEntryTitle;
    private Border? dashboardEntryIcon;
    /// <summary>The shadow caster under the work-status entry, shown while it is selected.</summary>
    private Microsoft.UI.Xaml.Shapes.Rectangle? dashboardEntryShadow;
    /// <summary>The cell the entry and its shadow caster share.</summary>
    private Grid? dashboardEntryHost;
    private StatusCountsView? dashboardCounts;
    private void InitDashboard()
    {
        var button = dashboardButton = Button(Locale.Get("phone.dashboard.title"), () => { showsDashboard = true; RenderSidebar(); RenderDashboard(); return Task.CompletedTask; });
        button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Padding = new Thickness(0); button.Margin = new Thickness(9, 12, 9, 0);
        PlainSidebarButton(button, brushes.Transparent, brushes.Transparent, radius: DesignMetrics.Radius.Entry);
        // The work-status entry (M/WorkspaceView.swift:136-158): a 24×24 run tile at radius 7 with the white
        // grid symbol 11, the 13pt semibold title and the counts, padding h10 v8.
        var row = new Grid { ColumnSpacing = 9 };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        dashboardEntryIcon = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(DesignMetrics.Radius.Search), Background = brushes.Brush(DesignToken.Run), VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon { Glyph = "\uF0E2", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = brushes.Brush(DesignToken.OnStatus), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        AutomationProperties.SetAccessibilityView(dashboardEntryIcon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        row.Children.Add(dashboardEntryIcon);
        dashboardEntryTitle = new TextBlock { FontSize = DesignMetrics.Type.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = brushes.Brush(DesignToken.Ink), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(dashboardEntryTitle, 1); row.Children.Add(dashboardEntryTitle);
        dashboardCounts = new StatusCountsView(brushes, "sidebar-dashboard-running"); Grid.SetColumn(dashboardCounts.View, 2); row.Children.Add(dashboardCounts.View);
        dashboardEntry = new Border { Child = row, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(DesignMetrics.Radius.Entry) };
        // The selected entry's 0.06 shadow (doc §4) sits under the wash in the same cell.
        dashboardEntryShadow = CardShadow.Caster(DesignMetrics.Radius.Entry, CardShadow.SelectedEntry, brushes.Brush(DesignToken.Card));
        dashboardEntryShadow.Visibility = Visibility.Collapsed;
        dashboardEntryHost = new Grid(); dashboardEntryHost.Children.Add(dashboardEntryShadow); dashboardEntryHost.Children.Add(dashboardEntry);
        button.Content = dashboardEntryHost;
        AutomationProperties.SetAutomationId(button, "sidebar-dashboard"); ToolTipService.SetToolTip(button, Locale.Get("dashboard.sidebarHelp"));
        sidebarTop.Children.Insert(1, button); RefreshDashboardChrome();
        dashboard = new ScrollViewer { Visibility = Visibility.Collapsed, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(dashboard, 1); Grid.SetColumn(dashboard, 1); root.Children.Add(dashboard);
        // Over the sidebar's trailing divider, the full height of the window like the surface it resizes.
        // Opacity 0 keeps the hit area and hides the stock grey Thumb bar (and with it the focus rect),
        // so the divider itself turns accent while the grip is hovered, dragged or keyboard-focused.
        var grip = new Thumb { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Opacity = 0, IsTabStop = true };
        AutomationProperties.SetName(grip, Locale.Get("sidebar.resize")); AutomationProperties.SetAutomationId(grip, "sidebar-resize");
        var gripHost = new ResizeCursorHost(grip, horizontal: true) { Width = 8, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRowSpan(gripHost, 3); root.Children.Add(gripHost);
        var gripHovered = false;
        void ShowGrip() => sidebarSurface.BorderBrush = brushes.Brush(gripHovered || grip.IsDragging || grip.FocusState == FocusState.Keyboard ? DesignToken.Accent : DesignToken.Line);
        grip.PointerEntered += (_, _) => { gripHovered = true; ShowGrip(); }; grip.PointerExited += (_, _) => { gripHovered = false; ShowGrip(); };
        grip.GotFocus += (_, _) => ShowGrip(); grip.LostFocus += (_, _) => ShowGrip(); grip.DragCompleted += (_, _) => ShowGrip();
        grip.DragDelta += (_, args) => root.ColumnDefinitions[0].Width = new(Math.Clamp(root.ColumnDefinitions[0].Width.Value + args.HorizontalChange, DesignMetrics.Layout.SidebarMin, DesignMetrics.Layout.SidebarMax));
        grip.DragCompleted += async (_, args) => { var width = args.Canceled ? service.Snapshot.SidebarWidth : root.ColumnDefinitions[0].Width.Value; await Act(() => service.UpdateAsync(s => s with { SidebarWidth = width })); root.ColumnDefinitions[0].Width = new(width); };
        grip.DoubleTapped += async (_, args) => { args.Handled = true; await Act(() => service.UpdateAsync(s => s with { SidebarWidth = DesignMetrics.Layout.SidebarDefault })); root.ColumnDefinitions[0].Width = new(DesignMetrics.Layout.SidebarDefault); };
        grip.KeyDown += async (_, args) =>
        {
            if (args.Key is not (Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right)) return;
            args.Handled = true; var width = Math.Clamp(service.Snapshot.SidebarWidth + (args.Key == Windows.System.VirtualKey.Right ? 10 : -10), DesignMetrics.Layout.SidebarMin, DesignMetrics.Layout.SidebarMax);
            await Act(() => service.UpdateAsync(s => s with { SidebarWidth = width })); root.ColumnDefinitions[0].Width = new(width);
        };
    }
    private WorkDashboard.Attention DashboardAttention(string id) => views.TryGetValue(id, out var pane) ? pane.DashboardAttention : new();
    private void HideDashboard() { showsDashboard = false; RefreshDashboardEntry(); StopDashboardGit(); dashboardFingerprint = null; if (dashboard is not null) dashboard.Visibility = Visibility.Collapsed; panes.Visibility = Visibility.Visible; RefreshWorkspaceHeader(); }
    /// <summary>The dashboard rows' marks with the pane each draws, redrawn for the theme on every render (the content is kept).</summary>
    private readonly List<(StatusMark Mark, string SessionId)> dashboardMarks = [];
    /// <summary>The tiles, the usage card and the first group's parts the design smoke reads.</summary>
    private DashboardParts? dashboardParts;
    internal sealed record DashboardParts(Border Running, Border Waiting, Border Done, IReadOnlyList<Grid> TileHosts, Border? Usage, IReadOnlyList<Grid> UsageBars, Border? Rows, Button? FirstRow, Button? Files, Button? Add, TextBlock Title);
    /// <summary>
    /// Set by the design smoke while it reads the dashboard in both themes: the drawn dashboard is kept
    /// (its clocks, marks and colours still refresh) even if a pane's status or logs change meanwhile,
    /// so the dark pass reads the very views the light pass read.
    /// </summary>
    private bool dashboardHeldForSmoke;

    private void RenderDashboard()
    {
        if (dashboard is null || closing || !showsDashboard) return;
        panes.Visibility = Visibility.Collapsed; dashboard.Visibility = Visibility.Visible; RefreshWorkspaceHeader();
        var state = service.Snapshot;
        RefreshDashboardGit();
        var usageCards = usage?.LeadingBars() ?? [];
        var dark = state.Theme != "light";
        foreach (var (mark, id) in dashboardMarks)
            if (state.Sessions.FirstOrDefault(s => s.Id == id) is { } marked) mark.Update(WorkDashboard.MakeCard(marked, DashboardAttention(id)).DisplayStatus, marked.Kind, DashboardAttention(id).Total, dark);
        foreach (var session in state.Sessions)
            if (dashboardClocks.TryGetValue(session.Id, out var clockLabel))
                clockLabel.Text = DashboardMeta(WorkDashboard.MakeCard(session, DashboardAttention(session.Id)));
        // The theme is not part of the key: every colour is a shared brush the toggle recolours in place.
        var key = System.Text.Json.JsonSerializer.Serialize(new { Locale.LanguagePreference, state.ActiveSessionId, state.Workspaces, Usage = usageCards, Sessions = state.Sessions.Select(s => new { s.Id, s.Status, s.Title, s.Model, s.Kind, s.Provider, s.CurrentActivity, Log = s.Logs.LastOrDefault(), s.SessionUsage, Attention = DashboardAttention(s.Id) }) }, Wire.Json);
        if (key == dashboardFingerprint || (dashboardHeldForSmoke && dashboardParts is not null)) return; dashboardFingerprint = key;
        dashboardClocks.Clear(); dashboardGitLabels.Clear(); dashboardMarks.Clear();
        // M/DashboardView.swift:94-118: padding h28 t20 b24; the title, the tiles 16 below, each group 22 below.
        var content = new StackPanel { Padding = new(28, 20, 28, 24) };
        var heading = new StackPanel { Spacing = 3 };
        var title = new TextBlock { Text = Locale.Get("phone.dashboard.title"), FontSize = DesignMetrics.Type.DashTitle, FontFamily = new FontFamily(DesignMetrics.Font.Heading), FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = brushes.Brush(DesignToken.Ink) };
        AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        heading.Children.Add(title);
        heading.Children.Add(new TextBlock { Text = Locale.Get("dashboard.subtitle", new Dictionary<string, string> { ["workspaces"] = state.Workspaces.Count.ToString(), ["panes"] = state.Sessions.Count.ToString() }), FontSize = 12, Foreground = brushes.Brush(DesignToken.Ink2) });
        content.Children.Add(heading);
        var stats = WorkDashboard.Count(state.Sessions, DashboardAttention);
        var tiles = new Grid { ColumnSpacing = 12, Margin = new(0, 16, 0, 0) };
        var specs = new[]
        {
            ("phone.dashboard.stat.running", stats.Running, DesignToken.Run, DesignToken.OnStatus, DesignToken.OnStatus, "dashboard-stat-running"),
            ("phone.dashboard.stat.waiting", stats.Waiting, DesignToken.Wait, DesignToken.OnWait, DesignToken.OnWait, "dashboard-stat-waiting"),
            ("phone.dashboard.stat.done", stats.Done, DesignToken.Card, DesignToken.DoneText, DesignToken.Ink2, "dashboard-stat-done"),
        };
        var drawnTiles = new List<Border>(); var tileHosts = new List<Grid>();
        for (var i = 0; i < specs.Length; i++)
        {
            var (labelKey, count, fill, number, ink, id) = specs[i];
            tiles.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star), MinWidth = DashboardTileMinWidth, MaxWidth = usageCards.Count > 0 ? DashboardTileMaxWidth : double.PositiveInfinity });
            var (host, tile) = DashboardTile(count, Locale.Get(labelKey), fill, number, ink);
            AutomationProperties.SetAutomationId(tile, id);
            Grid.SetColumn(host, i); tiles.Children.Add(host); drawnTiles.Add(tile); tileHosts.Add(host);
        }
        Border? usageCard = null; var usageBars = new List<Grid>();
        if (usageCards.Count > 0)
        {
            tiles.ColumnDefinitions.Add(new() { Width = new(3, GridUnitType.Star), MinWidth = DashboardUsageMinWidth });
            var (host, card) = DashboardUsageCard(usageCards, usageBars);
            usageCard = card; Grid.SetColumn(host, specs.Length); tiles.Children.Add(host);
        }
        content.Children.Add(tiles);
        if (state.Workspaces.Count == 0) content.Children.Add(new TextBlock { Text = Locale.Get("dashboard.empty"), FontSize = DesignMetrics.Type.Body, Foreground = brushes.Brush(DesignToken.Ink2), Margin = new(0, 28, 0, 0) });
        Border? firstRows = null; Button? firstRow = null, firstFiles = null, firstAdd = null;
        foreach (var workspace in state.Workspaces)
        {
            var group = new StackPanel { Spacing = 10, Margin = new(0, 22, 0, 0) };
            AutomationProperties.SetAutomationId(group, "dashboard-workspace-" + workspace.Id);
            var (header, files, add) = DashboardWorkspaceHeader(workspace);
            group.Children.Add(header);
            firstFiles ??= files; firstAdd ??= add;
            var cards = WorkDashboard.Ordered(state.Sessions.Where(s => s.WorkspaceId == workspace.Id && WorkDashboard.IsCounted(s.Kind)).Select(s => WorkDashboard.MakeCard(s, DashboardAttention(s.Id))));
            if (cards.Count == 0) { group.Children.Add(new TextBlock { Text = Locale.Get("phone.workspaces.noSessions"), FontSize = 12, Foreground = brushes.Brush(DesignToken.Ink2), Margin = new(2, 0, 2, 0) }); content.Children.Add(group); continue; }
            // One card per workspace, its panes as glyph rows with a line between them inset 43.
            var list = new StackPanel();
            for (var index = 0; index < cards.Count; index++)
            {
                if (index > 0) list.Children.Add(new Border { Height = DesignMetrics.Stroke.Line, Margin = new(DashboardDividerInset, 0, 0, 0), Background = brushes.Brush(DesignToken.Line) });
                var corner = DesignMetrics.Radius.Tile - DesignMetrics.Stroke.Line;
                var row = DashboardRow(cards[index], dark, new CornerRadius(index == 0 ? corner : 0, index == 0 ? corner : 0, index == cards.Count - 1 ? corner : 0, index == cards.Count - 1 ? corner : 0));
                firstRow ??= row; list.Children.Add(row);
            }
            var (rowsHost, rows) = ShadowedCard(list, brushes.Brush(DesignToken.Card), CardShadow.DashboardCard);
            rows.BorderBrush = brushes.Brush(DesignToken.Line); rows.BorderThickness = new(DesignMetrics.Stroke.Line);
            firstRows ??= rows;
            group.Children.Add(rowsHost);
            content.Children.Add(group);
        }
        dashboardParts = new(drawnTiles[0], drawnTiles[1], drawnTiles[2], tileHosts, usageCard, usageBars, firstRows, firstRow, firstFiles, firstAdd, title);
        dashboard.Content = content;
    }

    /// <summary>The tiles' bounds and the usage card's least width (M/DashboardView.swift:136-147, 176).</summary>
    internal const double DashboardTileMinWidth = 110, DashboardTileMaxWidth = 220, DashboardTileHeight = 92, DashboardUsageMinWidth = 260, DashboardDividerInset = 43;

    /// <summary>A rounded card over its Composition shadow (decision Q5): the caster and the card share one cell.</summary>
    private static (Grid Host, Border Card) ShadowedCard(UIElement child, SolidColorBrush fill, double shadow)
    {
        var card = new Border { Child = child, Background = fill, CornerRadius = new(DesignMetrics.Radius.Tile) };
        var host = new Grid();
        host.Children.Add(CardShadow.Caster(DesignMetrics.Radius.Tile, shadow, fill)); host.Children.Add(card);
        return (host, card);
    }

    /// <summary>
    /// A count tile (M/DashboardView.swift:136-147): 92 high, radius 18, padding h16 t12 b13, the number in
    /// the heading font at 34 over the 12.5 bold label, on its tone's fill with the 0.05 shadow.
    /// </summary>
    private (Grid Host, Border Tile) DashboardTile(int count, string label, DesignToken fill, DesignToken number, DesignToken ink)
    {
        var body = new Grid();
        body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var figure = new TextBlock { Text = count.ToString(System.Globalization.CultureInfo.CurrentCulture), FontSize = DesignMetrics.Type.Tile, FontFamily = new FontFamily(DesignMetrics.Font.Heading), FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = brushes.Brush(number), LineHeight = DesignMetrics.Type.Tile, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(figure, Microsoft.UI.Xaml.FontNumeralAlignment.Tabular);
        body.Children.Add(figure);
        var words = new TextBlock { Text = label, FontSize = DesignMetrics.Type.SideRow, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = brushes.Brush(ink), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetRow(words, 2); body.Children.Add(words);
        var (host, tile) = ShadowedCard(body, brushes.Brush(fill), CardShadow.DashboardCard);
        tile.Height = DashboardTileHeight; tile.Padding = new(16, 12, 16, 13);
        host.MinWidth = DashboardTileMinWidth;
        AutomationProperties.SetName(tile, Locale.Get("phone.dashboard.statLabel", new Dictionary<string, string> { ["label"] = label, ["count"] = count.ToString(System.Globalization.CultureInfo.CurrentCulture) }));
        return (host, tile);
    }

    /// <summary>
    /// The account usage card beside the tiles (M/DashboardView.swift:163-219): radius 18, padding h16 v11,
    /// the account-usage title 12 bold over a row per provider with its leading two windows as 6pt bars. A click
    /// opens the status bar's usage popover, as the dashboard's account chips did. The rows are the chips'
    /// leading windows (<see cref="AccountUsageStatus.LeadingBars()"/>): session, then weekly, never the spend limit.
    /// </summary>
    private (Grid Host, Border Card) DashboardUsageCard(IReadOnlyList<AccountUsageBars> cards, List<Grid> bars)
    {
        var body = new StackPanel { Spacing = 7 };
        var top = new Grid { ColumnSpacing = 7 };
        top.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); top.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); top.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        top.Children.Add(new FontIcon { Glyph = "\uE9D9", FontSize = 11, Foreground = brushes.Brush(DesignToken.Ink2), VerticalAlignment = VerticalAlignment.Center });
        var heading = new TextBlock { Text = Locale.Get("dashboard.usage.title"), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = brushes.Brush(DesignToken.Ink) };
        Grid.SetColumn(heading, 1); top.Children.Add(heading);
        var shared = new TextBlock { Text = Locale.Get("dashboard.usage.shared"), FontSize = DesignMetrics.Type.Pill, Foreground = brushes.Brush(DesignToken.Ink2), HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(6, 0, 0, 0) };
        Grid.SetColumn(shared, 2); top.Children.Add(shared);
        body.Children.Add(top);
        foreach (var card in cards)
        {
            var windows = card.Bars;
            var row = new Grid { ColumnSpacing = 14 };
            row.ColumnDefinitions.Add(new() { Width = new(78) });
            foreach (var _ in windows) row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            name.Children.Add(ProviderMarkView.Create(card.Provider, 11));
            name.Children.Add(new TextBlock { Text = ProviderCatalog.Name(card.Provider), FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = brushes.Brush(DesignToken.Ink), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(name);
            for (var i = 0; i < windows.Count; i++)
            {
                var window = windows[i];
                var line = new Grid { ColumnSpacing = 8 };
                line.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); line.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); line.ColumnDefinitions.Add(new() { Width = GridLength.Auto, MinWidth = 30 });
                line.Children.Add(new TextBlock { Text = window.Label, FontSize = 11.5, Foreground = brushes.Brush(DesignToken.Ink2), VerticalAlignment = VerticalAlignment.Center });
                var bar = UsageBar(window.Fraction, window.Warning); Grid.SetColumn(bar, 1); line.Children.Add(bar); bars.Add(bar);
                // Worded as the status bar chips word it (AccountUsageSupport.Percent), so both agree.
                var percent = new TextBlock { Text = window.Percent, FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = brushes.Brush(DesignToken.Ink), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(percent, Microsoft.UI.Xaml.FontNumeralAlignment.Tabular);
                Grid.SetColumn(percent, 2); line.Children.Add(percent);
                Grid.SetColumn(line, i + 1); row.Children.Add(line);
            }
            body.Children.Add(row);
        }
        var (host, usageCard) = ShadowedCard(body, brushes.Brush(DesignToken.Card), CardShadow.DashboardCard);
        usageCard.Padding = new(16, 11, 16, 11); usageCard.MinHeight = DashboardTileHeight;
        usageCard.Tapped += (_, _) => { if (usageButton?.Flyout is { } flyout) flyout.ShowAt(usageButton); };
        AutomationProperties.SetAutomationId(usageCard, "dashboard-usage");
        AutomationProperties.SetName(usageCard, Locale.Get("dashboard.usage.title"));
        return (host, usageCard);
    }

    /// <summary>
    /// One pane as a row (M/DashboardView.swift:262-338): the 18pt glyph, the 14pt title (medium once it
    /// has settled), the 12pt <c>ink2</c> line "[mark] Claude · model · 4 days ago", the running pane's last step
    /// in 11.3 mono, and a waiting pane's word in <c>waitText</c>; padding h14 v11, the subtle wash under the pointer.
    /// </summary>
    private Button DashboardRow(WorkDashboard.Card card, bool dark, CornerRadius corners)
    {
        var tone = StatusGlyph.Tone(card.DisplayStatus);
        var settled = tone is DesignTone.Done or DesignTone.Stop or DesignTone.Idle;
        var row = new Grid { ColumnSpacing = 11 };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var mark = new StatusMark(DashboardGlyphSize); mark.Update(card.DisplayStatus, card.Session.Kind, card.Attention.Total, dark);
        mark.View.VerticalAlignment = VerticalAlignment.Top; mark.View.Margin = new(0, 1, 0, 0);
        dashboardMarks.Add((mark, card.Session.Id)); row.Children.Add(mark.View);
        var description = new StackPanel { Spacing = 1 };
        description.Children.Add(new TextBlock { Text = card.Session.Title, FontSize = DesignMetrics.Type.DashRow, FontWeight = settled ? Microsoft.UI.Text.FontWeights.Medium : Microsoft.UI.Text.FontWeights.SemiBold, Foreground = brushes.Brush(DesignToken.Ink), TextTrimming = TextTrimming.CharacterEllipsis, MinHeight = 20 });
        description.Children.Add(DashboardProviderLine(card));
        if (card.Session.Status == "running" && card.LastActivity is { } last)
            description.Children.Add(new TextBlock { Text = last, FontSize = 11.3, FontFamily = new FontFamily(DesignMetrics.Font.Mono), Foreground = brushes.Brush(card.ActivityIsError ? DesignToken.ErrText : DesignToken.Ink2), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 4, 0, 0) });
        Grid.SetColumn(description, 1); row.Children.Add(description);
        if (card.Attention.Total > 0)
        {
            var waiting = new TextBlock { Text = DashboardAttentionWord(card.Attention), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = brushes.Brush(DesignToken.WaitText), MinHeight = 20 };
            Grid.SetColumn(waiting, 2); row.Children.Add(waiting);
        }
        var open = Button(card.Session.Title, async () => { HideDashboard(); await SelectLayoutSession(card.Session.Id); });
        open.Content = row; open.HorizontalAlignment = HorizontalAlignment.Stretch; open.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        open.Padding = new(14, 11, 14, 11); open.BorderThickness = new(0); open.CornerRadius = corners;
        PaintPlainButton(open, brushes.Transparent, brushes.Subtle, ink: brushes.Brush(DesignToken.Ink));
        AutomationProperties.SetAutomationId(open, "dashboard-card-" + card.Session.Id);
        AutomationProperties.SetName(open, Locale.Get("phone.card.label", new Dictionary<string, string> { ["title"] = card.Session.Title, ["status"] = card.Attention.Total > 0 ? Locale.Get("phone.card.attention", new Dictionary<string, string> { ["count"] = card.Attention.Total.ToString() }) : StateLabel(card.DisplayStatus) }));
        return open;
    }

    /// <summary>The dashboard rows' glyph size (M/DashboardView.swift:270).</summary>
    internal const double DashboardGlyphSize = 18;

    private static string DashboardAttentionWord(WorkDashboard.Attention attention) => attention.Questions > 0
        ? Locale.Get("phone.card.questions", new Dictionary<string, string> { ["count"] = attention.Questions.ToString() })
        : Locale.Get("phone.card.permissions", new Dictionary<string, string> { ["count"] = attention.Permissions.ToString() });

    private void RefreshDashboardChrome()
    {
        if (dashboardButton is null || dashboardEntryTitle is null) return;
        dashboardEntryTitle.Text = Locale.Get("phone.dashboard.title");
        ToolTipService.SetToolTip(dashboardButton, Locale.Get("dashboard.sidebarHelp"));
        // The counts' spoken labels are in the language too.
        dashboardCounts?.Invalidate(); RefreshDashboardEntry();
    }
    /// <summary>
    /// The work-status entry's selected wash (the <c>card</c> surface at radius 10 while the
    /// dashboard shows, nothing otherwise) with the Mac's 0.06 shadow under it, and its counts over
    /// every pane. The shadow's caster shows only while the entry is selected.
    /// </summary>
    private void RefreshDashboardEntry()
    {
        if (dashboardEntry is null) return;
        dashboardEntry.Background = showsDashboard ? brushes.Brush(DesignToken.Card) : brushes.Transparent;
        if (dashboardEntryShadow is not null) dashboardEntryShadow.Visibility = showsDashboard ? Visibility.Visible : Visibility.Collapsed;
        var state = service.Snapshot;
        dashboardCounts?.Update(WorkDashboard.WorkspaceBadges(state.Sessions, DashboardAttention), state.Theme != "light");
        NameDashboardEntry();
    }
    /// <summary>The entry reads as its title and then its counts, "Work status, 2 running" (M/WorkspaceView.swift:155).</summary>
    private string DashboardEntryName() => string.Join(", ", (dashboardCounts?.Labels ?? []).Prepend(Locale.Get("phone.dashboard.title")));
    private void NameDashboardEntry()
    {
        if (dashboardButton is null) return;
        var name = DashboardEntryName();
        if (AutomationProperties.GetName(dashboardButton) != name) AutomationProperties.SetName(dashboardButton, name);
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
