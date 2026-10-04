using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

// Design stage 6 (.omc/plans/windows-design-conversion.md): the dashboard, the files pane, settings,
// the sheets and the popovers. The light pass opens each of them on fixture data and reads its colours
// and geometry off the element tree; the toggle to dark follows with the dashboard, the files pane and
// the settings window still open, and the dark pass reads the same instances again, so every colour it
// sees was recoloured in place. Every failure names the token, the expected hex and the actual one.
public sealed partial class MainWindow
{
    private const string PanelsDesignKey = "panelsDesign";

    /// <summary>The instances the light pass checked, which the dark pass must find again.</summary>
    private sealed record PanelsDesignViews(UIElement Dashboard, DashboardParts Parts, FrameworkElement SettingsFrame, ContentDialog Sheet, Grid FilesHead, Brush Keyword, Style Popover);

    private async Task<Dictionary<string, object?>> RunPanelsDesignSmoke(Workspace workspace)
    {
        var original = service.Snapshot;
        var previousTree = EffectiveLayout(original, workspace.Id); var previousMode = LayoutMode(original, workspace.Id);
        var filesPane = FilePaneKind.PaneId(workspace.Id);
        Task? settingsOpening = null;
        var checks = new Dictionary<string, object?>();
        try
        {
            // Two usage windows per Claude pane, one past 90% so a bar draws waitText.
            var now = DateTimeOffset.UtcNow;
            await service.UpdateAsync(s => s with
            {
                Theme = "light", ActiveWorkspaceId = workspace.Id, ClaudeDirectUsageLookupEnabled = false,
                Sessions = s.Sessions.Select(pane => pane.Kind != "shell" && pane.Provider == "claude"
                    ? pane with { SessionUsage = (pane.SessionUsage ?? new SessionUsage { Provider = "claude", Source = "smoke.fixture" }) with { RateLimits = [new("five_hour", 24, now.AddHours(3).ToString("O")), new("seven_day", 95)], RateLimitsUpdatedAt = now.ToString("O") } }
                    : pane).ToList(),
            });
            Render();
            var agent = service.Snapshot.ActiveSessionId is { } active && views.TryGetValue(active, out var activeView) && activeView.SessionForSmoke.Kind == "claude" ? activeView
                : throw new InvalidOperationException($"{PanelsDesignKey}: needs the active smoke pane to be an agent pane; active {service.Snapshot.ActiveSessionId ?? "none"}");
            await ApplyLayoutPreset("tabs");
            await WaitUI(() => agent.Container.IsLoaded, () => $"{PanelsDesignKey}: the agent pane never loaded");

            var popover = await RequirePopoversInTheme(agent, null);
            var sheet = ResumeChoiceDialog("claude");
            await RequireSheetInTheme(sheet);
            await OpenFilePane(workspace.Id);
            await WaitUI(() => views.TryGetValue(filesPane, out var opened) && opened.FilesHost is { IsLoaded: true }, () => $"{PanelsDesignKey}: the files pane never loaded");
            var files = views[filesPane];
            await WaitUI(() => files.FilesTree.Children.ContainsKey(""), () => $"{PanelsDesignKey}: the files tree never listed the workspace");
            await files.FilesSmokeOpenFolder("Sources");
            await WaitUI(() => files.FilesTree.Children.ContainsKey("Sources"), () => $"{PanelsDesignKey}: the Sources folder never opened");
            // Select the row first: its debounced preview is then cancelled by the immediate one, so
            // nothing redraws the source after the light pass read its runs.
            await files.FilesSmokeSelect("Sources/App.swift");
            await files.FilesSmokePreview("Sources/App.swift");
            var (filesHead, keyword) = await files.RequireFilesDesignInTheme(null);
            var (dashboardContent, parts) = await RequireDashboardDesignInTheme(null);
            settingsOpening = ShowCategorizedSettingsAsync();
            var frame = await RequireSettingsDesignInTheme(null);
            var light = new PanelsDesignViews(dashboardContent, parts, frame, sheet, filesHead, keyword, popover);

            await service.UpdateAsync(s => s with { Theme = "dark" }); Render();
            Require(ReferenceEquals(views[filesPane], files) && ReferenceEquals(views[agent.SessionId], agent), $"{PanelsDesignKey} (dark): the toggle rebuilt a pane instead of reusing it");
            await RequireSettingsDesignInTheme(light.SettingsFrame);
            settingsWindow?.Close(); await settingsOpening; settingsOpening = null;
            await RequireDashboardDesignInTheme(light);
            HideDashboard(); root.UpdateLayout();
            await files.RequireFilesDesignInTheme(light);
            await RequireSheetInTheme(light.Sheet);
            await SelectLayoutSession(agent.SessionId);
            await WaitUI(() => agent.Container.IsLoaded, () => $"{PanelsDesignKey} (dark): the agent pane never loaded again");
            await RequirePopoversInTheme(agent, light.Popover);
            checks["dashboard"] = true; checks["filesPane"] = true; checks["settings"] = true; checks["sheet"] = true; checks["popovers"] = true; checks["bothThemes"] = true;
            return checks;
        }
        finally
        {
            if (settingsOpening is not null) { settingsWindow?.Close(); await settingsOpening; }
            dashboardHeldForSmoke = false; HideDashboard();
            if (service.Snapshot.Sessions.Any(s => s.Id == filesPane)) await CloseSession(filesPane);
            await service.UpdateAsync(s => SaveLayoutMode(SaveLayout(s with { Theme = original.Theme, Sessions = s.Sessions.Select(p => original.Sessions.FirstOrDefault(o => o.Id == p.Id) ?? p).ToList(), ClaudeDirectUsageLookupEnabled = original.ClaudeDirectUsageLookupEnabled }, workspace.Id, previousTree), workspace.Id, previousMode));
            usage?.SetDirectClaudeLookup(original.ClaudeDirectUsageLookupEnabled);
            Render();
            if (original.ActiveSessionId is { } previous && service.Snapshot.Sessions.Any(s => s.Id == previous)) await SelectLayoutSession(previous);
        }
    }

    /// <summary>A brush is the very shared instance it should be, named for the message.</summary>
    private void RequireSame(Brush? actual, Brush expected, string what)
    {
        Require(ReferenceEquals(actual, expected), $"{PanelsDesignKey} ({SmokeTheme}): {what} must be the shared {Describe(expected)} brush; got {Describe(actual)}{(actual is null ? "" : " from another brush")}");
    }

    private static FrameworkElement? Ancestor(DependencyObject start, Func<DependencyObject, bool> match)
    {
        for (var node = VisualTreeHelper.GetParent(start); node is not null; node = VisualTreeHelper.GetParent(node))
            if (match(node)) return node as FrameworkElement;
        return null;
    }

    /// <summary>
    /// The dashboard (M/DashboardView.swift:94-338): the 29pt heading-font title, the three 92-high
    /// radius-18 tiles on run / wait / card with their inks and 0.05 shadows, the usage card's 6pt bars,
    /// a workspace's rows on one card with the subtle wash under the pointer, its 26-high capsules, and
    /// the sidebar's selected work-status entry with its 0.06 shadow.
    /// </summary>
    private async Task<(UIElement Content, DashboardParts Parts)> RequireDashboardDesignInTheme(PanelsDesignViews? light)
    {
        const string key = PanelsDesignKey; var theme = SmokeTheme;
        showsDashboard = true; RefreshDashboardEntry(); RenderDashboard(); root.UpdateLayout();
        await WaitUI(() => dashboardParts is { } p && p.Running.IsLoaded && p.Usage is { IsLoaded: true } && p.Rows is { IsLoaded: true } && p.FirstRow is { IsLoaded: true },
            () => $"{key} ({theme}): the dashboard's tiles, usage card and rows never loaded (usage card {(dashboardParts?.Usage is null ? "missing" : "present")})");
        // From the light pass's first draw on, the drawn dashboard is kept, so a pane changing status
        // cannot swap a row or capsule out from under the waits below, nor the views under the dark pass.
        dashboardHeldForSmoke = true;
        var parts = dashboardParts!; var content = (UIElement)dashboard!.Content;
        if (light is not null)
            Require(ReferenceEquals(content, light.Dashboard) && ReferenceEquals(parts, light.Parts),
                $"{key} ({theme}): the toggle rebuilt the dashboard instead of recolouring it in place");
        RequireFont(parts.Title, DesignMetrics.Type.DashTitle, Microsoft.UI.Text.FontWeights.Bold, $"({theme}) the dashboard title", key);
        Require(parts.Title.FontFamily?.Source == DesignMetrics.Font.Heading, $"{key} ({theme}): the dashboard title must be Font.Heading '{DesignMetrics.Font.Heading}'; got '{parts.Title.FontFamily?.Source}'");
        RequireBrush(parts.Title, e => ((TextBlock)e).Foreground, DesignToken.Ink, "the dashboard title", key: key);
        foreach (var (tile, host, fill, number, ink, name) in new[]
        {
            (parts.Running, parts.TileHosts[0], DesignToken.Run, DesignToken.OnStatus, DesignToken.OnStatus, "the running tile"),
            (parts.Waiting, parts.TileHosts[1], DesignToken.Wait, DesignToken.OnWait, DesignToken.OnWait, "the waiting tile"),
            (parts.Done, parts.TileHosts[2], DesignToken.Card, DesignToken.DoneText, DesignToken.Ink2, "the done tile"),
        })
        {
            RequireRadius(tile, DesignMetrics.Radius.Tile, $"{name} (Radius.Tile)", key);
            Require(tile.Height == DashboardTileHeight && tile.Padding == new Thickness(16, 12, 16, 13), $"{key} ({theme}): {name} must be {DashboardTileHeight} high with padding h16 t12 b13; got {tile.Height}, {tile.Padding}");
            RequireBrush(tile, e => ((Border)e).Background, fill, $"{name}'s fill", key: key);
            var body = (Grid)tile.Child; var figure = (TextBlock)body.Children[0]; var label = (TextBlock)body.Children[1];
            Require(figure.FontSize == DesignMetrics.Type.Tile && figure.FontFamily?.Source == DesignMetrics.Font.Heading && label.FontSize == DesignMetrics.Type.SideRow,
                $"{key} ({theme}): {name} must show its number in Font.Heading at {DesignMetrics.Type.Tile}pt over a {DesignMetrics.Type.SideRow}pt label; got {figure.FontSize}pt '{figure.FontFamily?.Source}', {label.FontSize}pt");
            RequireBrush(figure, e => ((TextBlock)e).Foreground, number, $"{name}'s number", key: key);
            RequireBrush(label, e => ((TextBlock)e).Foreground, ink, $"{name}'s label", key: key);
            RequireCardShadow(host, CardShadow.DashboardCard, name, key);
        }
        var usageCard = parts.Usage!;
        RequireRadius(usageCard, DesignMetrics.Radius.Tile, "the usage card (Radius.Tile)", key);
        RequireBrush(usageCard, e => ((Border)e).Background, DesignToken.Card, "the usage card", key: key);
        // The chips' leading windows (session, then weekly, never the spend limit), read from Core as the card was.
        var rows = (usage?.LeadingBars() ?? []).SelectMany(c => c.Bars).ToList();
        Require(parts.UsageBars.Count == rows.Count && rows.Count > 0 && rows.Any(r => r.Warning), $"{key} ({theme}): the usage card must draw one bar per leading window, one of them past 90%; got {parts.UsageBars.Count} bars for {rows.Count} windows");
        for (var i = 0; i < rows.Count; i++) RequireUsageBar(parts.UsageBars[i], rows[i].Fraction, rows[i].Warning, $"usage bar {i} ({rows[i].Label} {rows[i].Percent})");
        var group = parts.Rows!;
        RequireRadius(group, DesignMetrics.Radius.Tile, "a workspace's rows card (Radius.Tile)", key);
        RequireThickness(group, DesignMetrics.Stroke.Line, "a workspace's rows card (Stroke.Line)", key);
        RequireBrush(group, e => ((Border)e).Background, DesignToken.Card, "a workspace's rows card", key: key);
        RequireBrush(group, e => ((Border)e).BorderBrush, DesignToken.Line, "a workspace's rows card border", key: key);
        var row = parts.FirstRow!;
        Require(row.Padding == new Thickness(14, 11, 14, 11), $"{key} ({theme}): a dashboard row's padding must be h14 v11; got {row.Padding}");
        RequireSubtle(await StateBackground(row, "PointerOver", brushes.Subtle, key), "a dashboard row's PointerOver key frame", key);
        RequireClear(OwnResource(row, "ButtonBackground") as Brush, "a dashboard row at rest", key);
        foreach (var (capsule, fill, hover, ink, name) in new[] { (parts.Files!, DesignToken.Card, DesignToken.CardRaised, DesignToken.Ink, "the files capsule"), (parts.Add!, DesignToken.Ink, DesignToken.Ink2, DesignToken.Card, "the add-pane capsule") })
        {
            Require(capsule.Height == DashboardCapsuleHeight && capsule.CornerRadius == new CornerRadius(DashboardCapsuleHeight / 2), $"{key} ({theme}): {name} must be a {DashboardCapsuleHeight}-high capsule; got {capsule.Height}, {capsule.CornerRadius}");
            RequireBrush(capsule, e => OwnResource(e, "ButtonBackground") as Brush, fill, $"{name}'s fill", key: key);
            RequireBrush(capsule, e => OwnResource(e, "ButtonForeground") as Brush, ink, $"{name}'s words", key: key);
            var frame = await StateBackground(capsule, "PointerOver", brushes.Brush(hover), key);
            RequireBrush(capsule, _ => frame, hover, $"{name}'s PointerOver key frame", key: key);
        }
        // The sidebar's work-status entry while the dashboard shows: card with the 0.06 shadow.
        RequireBrush(dashboardEntry!, e => ((Border)e).Background, DesignToken.Card, "the selected work-status entry", key: key);
        Require(dashboardEntryShadow is { Visibility: Visibility.Visible }, $"{key} ({theme}): the selected work-status entry must show its shadow caster");
        RequireCardShadow(dashboardEntryHost!, CardShadow.SelectedEntry, "the selected work-status entry", key);
        return (content, parts);
    }

    /// <summary>A card's caster (its host's first child) casts the Mac's black shadow at <paramref name="opacity"/>, blur 1, one point down.</summary>
    private void RequireCardShadow(Grid host, double opacity, string what, string key)
    {
        var caster = host.Children.OfType<Microsoft.UI.Xaml.Shapes.Rectangle>().FirstOrDefault();
        var shadow = caster is null ? null : CardShadow.Of(caster);
        Require(shadow is not null && Math.Abs(shadow.Opacity - opacity) < 0.001 && shadow.BlurRadius == 1 && shadow.Offset.Y == 1 && shadow.Color.A == 255 && shadow.Color.R == 0 && shadow.Color.G == 0 && shadow.Color.B == 0,
            $"{key} ({SmokeTheme}): {what} must cast a black shadow at {opacity}, blur 1, y 1; got {(shadow is null ? "none" : $"{Hex(shadow.Color)} at {shadow.Opacity}, blur {shadow.BlurRadius}, y {shadow.Offset.Y}")}");
    }

    /// <summary>A usage bar: a 6pt runSoft capsule filled to its fraction in run, or waitText past 90%.</summary>
    private void RequireUsageBar(Grid track, double fraction, bool warning, string what)
    {
        const string key = PanelsDesignKey; var theme = SmokeTheme;
        var fill = (Border)track.Children[0];
        Require(track.Height == UsageBarHeight && fill.Height == UsageBarHeight && track.CornerRadius == new CornerRadius(UsageBarHeight / 2),
            $"{key} ({theme}): {what} must be a {UsageBarHeight}pt capsule; got track {track.Height} at {track.CornerRadius}, fill {fill.Height}");
        RequireBrush(track, e => ((Grid)e).Background, DesignToken.RunSoft, $"{what}'s track", key: key);
        RequireBrush(fill, e => ((Border)e).Background, warning ? DesignToken.WaitText : DesignToken.Run, $"{what}'s fill ({(warning ? "past 90%" : "under 90%")})", key: key);
        var want = track.ActualWidth * Math.Clamp(fraction, 0, 1);
        Require(track.ActualWidth > 0 && Math.Abs(fill.Width - want) <= 1, $"{key} ({theme}): {what} must be filled to {fraction:P0} of {track.ActualWidth:F1}; got {fill.Width:F1}");
    }

    /// <summary>
    /// The settings sheet (M/SettingsViews.swift:152-269, 325-338): 800×700 points, the page frame, the
    /// heading (padding 22 on card, the 21pt accent gear, the 17pt title), the 200-wide list on the
    /// sidebar surface whose chosen row is filled with accent under onAccent words, the grouped section
    /// cards (card, 1pt line, radius 10), the close row on card.
    /// </summary>
    private async Task<FrameworkElement> RequireSettingsDesignInTheme(FrameworkElement? light)
    {
        const string key = PanelsDesignKey; var theme = SmokeTheme;
        await WaitUI(() => settingsWindow?.Content is FrameworkElement { XamlRoot: not null, ActualWidth: > 0, IsLoaded: true }, () => $"{key} ({theme}): the settings window never opened");
        var frame = (Grid)settingsWindow!.Content;
        if (light is not null) Require(ReferenceEquals(frame, light), $"{key} ({theme}): the toggle replaced the open settings window's content instead of recolouring it");
        Require(frame.RequestedTheme == root.RequestedTheme, $"{key} ({theme}): the open settings window must follow the theme; got {frame.RequestedTheme}, window {root.RequestedTheme}");
        // 800×700 points at the display scale, clamped to the display's work area (less the margin).
        var scale = root.XamlRoot?.RasterizationScale ?? 1; var size = settingsWindow.AppWindow.Size;
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(settingsWindow.AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest)?.WorkArea;
        var margin = Math.Round(2 * SettingsWindowMargin * scale);
        double Want(double points, int? free) => Math.Min(Math.Round(points * scale), free is { } f && f > margin ? f - margin : double.MaxValue);
        var (wantWidth, wantHeight) = (Want(SettingsWindowWidth, area?.Width), Want(SettingsWindowHeight, area?.Height));
        Require(Math.Abs(size.Width - wantWidth) <= 1 && Math.Abs(size.Height - wantHeight) <= 1,
            $"{key} ({theme}): the settings window must be min({SettingsWindowWidth}×{SettingsWindowHeight} points × {scale}, work area {area?.Width}×{area?.Height} less {margin}) = {wantWidth}×{wantHeight} px; got {size.Width}×{size.Height} px");
        RequireSame(frame.Background, brushes.Brush(DesignToken.Page), "the settings frame");
        RequireBrush(frame, e => ((Grid)e).Background, DesignToken.Page, "the settings frame", key: key);
        FrameworkElement Part(string id) => VisualChildren(frame).OfType<FrameworkElement>().FirstOrDefault(e => AutomationProperties.GetAutomationId(e) == id)
            ?? throw new InvalidOperationException($"{key} ({theme}): the settings window has no {id}");
        var heading = (Grid)Part("settings-heading");
        Require(heading.Padding == new Thickness(22), $"{key} ({theme}): the settings heading padding must be 22; got {heading.Padding}");
        RequireBrush(heading, e => ((Grid)e).Background, DesignToken.Card, "the settings heading", key: key);
        RequireBrush(heading, e => ((Grid)e).BorderBrush, DesignToken.Line, "the line under the settings heading", key: key);
        var gear = heading.Children.OfType<FontIcon>().Single(); var words = heading.Children.OfType<StackPanel>().Single();
        Require(gear.FontSize == 21, $"{key} ({theme}): the settings gear must be 21pt; got {gear.FontSize}");
        RequireBrush(gear, e => ((FontIcon)e).Foreground, DesignToken.Accent, "the settings gear", key: key);
        RequireFont((TextBlock)words.Children[0], DesignMetrics.Type.Header, Microsoft.UI.Text.FontWeights.SemiBold, $"({theme}) the settings title", key);
        RequireBrush((TextBlock)words.Children[1], e => ((TextBlock)e).Foreground, DesignToken.Ink2, "the settings subtitle", key: key);
        var host = (Border)Part("settings-navigation-host");
        Require(Math.Abs(host.ActualWidth - SettingsNavigationWidth) < 1, $"{key} ({theme}): the settings list must be {SettingsNavigationWidth} wide; got {host.ActualWidth:F1}");
        RequireBrush(host, e => ((Border)e).Background, DesignToken.Sidebar, "the settings list surface", key: key);
        var navigation = (ListView)Part("settings-navigation");
        RequireSame(OwnResource(navigation, "ListViewItemBackgroundSelected") as Brush, brushes.Brush(DesignToken.Accent), "the settings list's ListViewItemBackgroundSelected");
        RequireSame(OwnResource(navigation, "ListViewItemForegroundSelected") as Brush, brushes.Brush(DesignToken.OnAccent), "the settings list's ListViewItemForegroundSelected");
        RequireSubtle(OwnResource(navigation, "ListViewItemBackgroundPointerOver") as Brush, "the settings list's ListViewItemBackgroundPointerOver", key);
        var selected = navigation.SelectedItem as ListViewItem;
        await WaitUI(() => selected is { IsLoaded: true } && VisualTreeHelper.GetChildrenCount(selected) > 0, () => $"{key} ({theme}): the chosen settings row never loaded");
        var presenter = VisualTreeHelper.GetChild(selected!, 0);
        Require(presenter is ListViewItemPresenter, $"{key} ({theme}): the settings row's template root must be a ListViewItemPresenter; got {presenter.GetType().Name}");
        RequireBrush(selected!, _ => ((ListViewItemPresenter)presenter).SelectedBackground, DesignToken.Accent, "the chosen settings row's resolved SelectedBackground", key: key);
        RequireBrush(selected!, _ => ((ListViewItemPresenter)presenter).SelectedForeground, DesignToken.OnAccent, "the chosen settings row's resolved SelectedForeground", key: key);
        var card = VisualChildren(frame).OfType<StackPanel>().Select(s => s.Children.Count > 1 && s.Children[0] is TextBlock t && AutomationProperties.GetAutomationId(t).StartsWith("settings-section-", StringComparison.Ordinal) ? s.Children[1] as Border : null).FirstOrDefault(b => b is not null)
            ?? throw new InvalidOperationException($"{key} ({theme}): the settings window shows no section card");
        RequireRadius(card, SettingsCardRadius, "a settings section card", key);
        RequireThickness(card, DesignMetrics.Stroke.Line, "a settings section card", key);
        RequireBrush(card, e => ((Border)e).Background, DesignToken.Card, "a settings section card", key: key);
        RequireBrush(card, e => ((Border)e).BorderBrush, DesignToken.Line, "a settings section card border", key: key);
        var footer = (Grid)Part("settings-footer");
        RequireBrush(footer, e => ((Grid)e).Background, DesignToken.Card, "the settings close row", key: key);
        return frame;
    }

    /// <summary>
    /// The start-new-or-resume sheet (M/ResumeSessionSheet.swift), shown for real: 400 wide, padding 20,
    /// the agent's 18pt mark, its template's surface the shared card brush and its border the line.
    /// </summary>
    private async Task RequireSheetInTheme(ContentDialog sheet)
    {
        const string key = PanelsDesignKey; var theme = SmokeTheme;
        Require(OwnResource(sheet, "ContentDialogMinWidth") is double min && min == ChoiceSheetWidth && OwnResource(sheet, "ContentDialogMaxWidth") is double max && max == ChoiceSheetWidth,
            $"{key} ({theme}): the choice sheet must be {ChoiceSheetWidth} wide; got min {OwnResource(sheet, "ContentDialogMinWidth")}, max {OwnResource(sheet, "ContentDialogMaxWidth")}");
        Require(OwnResource(sheet, "ContentDialogPadding") is Thickness padding && padding == new Thickness(SheetPadding), $"{key} ({theme}): the choice sheet padding must be {SheetPadding}; got {OwnResource(sheet, "ContentDialogPadding")}");
        RequireSame(OwnResource(sheet, "ContentDialogTopOverlay") as Brush, brushes.Brush(DesignToken.Card), "the sheet's ContentDialogTopOverlay");
        RequireSame(OwnResource(sheet, "ContentDialogBorderBrush") as Brush, brushes.Brush(DesignToken.Line), "the sheet's ContentDialogBorderBrush");
        var mark = ((Grid)sheet.Content).Children.OfType<Microsoft.UI.Xaml.Shapes.Path>().Single();
        Require(mark.Width == ResumeMarkSize, $"{key} ({theme}): the sheet's agent mark must be {ResumeMarkSize}; got {mark.Width}");
        RequireBrush((TextBlock)((Grid)sheet.Content).Children[1], e => ((TextBlock)e).Foreground, DesignToken.Ink2, "the sheet's sentence", key: key);
        Task<ContentDialogResult>? showing = null;
        try
        {
            dialogOpen = true;
            showing = sheet.ShowAsync().AsTask();
            FrameworkElement? surface = null;
            await WaitUI(() => sheet.IsLoaded && (surface = VisualChildren(sheet).OfType<FrameworkElement>().FirstOrDefault(e => e.Name == "BackgroundElement")) is { ActualWidth: > 0 },
                () => $"{key} ({theme}): the shown sheet has no laid-out BackgroundElement");
            var fill = surface switch { Border b => b.Background, Panel p => p.Background, Control c => c.Background, _ => null };
            RequireSame(fill, brushes.Brush(DesignToken.Card), "the shown sheet's surface");
            RequireBrush(surface!, _ => fill, DesignToken.Card, "the shown sheet's surface", key: key);
            Require(Math.Abs(surface!.ActualWidth - ChoiceSheetWidth) <= 1, $"{key} ({theme}): the shown choice sheet must be {ChoiceSheetWidth} wide; got {surface.ActualWidth:F1}");
            if (surface is Border border) RequireBrush(border, e => ((Border)e).BorderBrush, DesignToken.Line, "the shown sheet's border", key: key);
        }
        finally
        {
            if (showing is not null) { sheet.Hide(); try { await showing; } catch (Exception) { /* The sheet is closing; its own failure is not the check's. */ } }
            dialogOpen = false;
        }
    }

    /// <summary>
    /// The popovers (M/StatusBarUsage.swift:167-312, M/SessionInfoViews.swift): the usage and session
    /// popovers' presenter is the card at radius 10 with a 1pt line and padding 16, the usage rows draw
    /// 6pt bars, the session popover's ring block sits on accentSoft under an ink2 status line.
    /// </summary>
    private async Task<Style> RequirePopoversInTheme(PaneView agent, Style? light)
    {
        const string key = PanelsDesignKey; var theme = SmokeTheme;
        var style = CardFlyoutStyle;
        if (light is not null) Require(ReferenceEquals(style, light), $"{key} ({theme}): the popover style was replaced instead of recoloured");
        Require(usageButton is { Visibility: Visibility.Visible, Flyout: Flyout } && ReferenceEquals(((Flyout)usageButton.Flyout).FlyoutPresenterStyle, style),
            $"{key} ({theme}): the status bar's usage popover must show and use the card presenter style");
        var flyout = (Flyout)usageButton!.Flyout;
        flyout.ShowAt(usageButton);
        try
        {
            await WaitUI(() => usageDetailsOpen && usageDetails.IsLoaded && Ancestor(usageDetails, n => n is FlyoutPresenter) is not null, () => $"{key} ({theme}): the usage popover never opened");
            RequirePresenter((FlyoutPresenter)Ancestor(usageDetails, n => n is FlyoutPresenter)!, "the usage popover");
            var claude = usage!.Cards().First(c => c.Provider == "claude");
            for (var i = 0; i < claude.Windows.Count; i++)
            {
                var index = i;
                Grid? bar = null;
                await WaitUI(() => (bar = VisualChildren(usageDetails).OfType<Grid>().FirstOrDefault(g => AutomationProperties.GetAutomationId(g) == "statusbar-usage-bar-claude-" + index)) is { ActualWidth: > 0 },
                    () => $"{key} ({theme}): the usage popover never laid out Claude's bar {index}");
                RequireUsageBar(bar!, claude.Windows[i].Fraction, claude.Windows[i].Warning, $"the usage popover's Claude bar {i}");
            }
        }
        finally { flyout.Hide(); }
        await agent.RequireSessionPopoverInTheme(style);
        return style;
    }

    private void RequirePresenter(FlyoutPresenter presenter, string what)
    {
        const string key = PanelsDesignKey; var theme = SmokeTheme;
        RequireSame(presenter.Background, brushes.Brush(DesignToken.Card), what + "'s surface");
        RequireBrush(presenter, e => ((Control)e).Background, DesignToken.Card, what + "'s surface", key: key);
        RequireBrush(presenter, e => ((Control)e).BorderBrush, DesignToken.Line, what + "'s border", key: key);
        Require(presenter.BorderThickness == new Thickness(DesignMetrics.Stroke.Line) && presenter.CornerRadius == new CornerRadius(DesignMetrics.Radius.Entry) && presenter.Padding == new Thickness(PopoverPadding),
            $"{key} ({theme}): {what} must be a 1pt, radius {DesignMetrics.Radius.Entry} card with padding {PopoverPadding}; got {presenter.BorderThickness}, {presenter.CornerRadius}, {presenter.Padding}");
    }

    private sealed partial class PaneView
    {
        internal string SessionId => id;

        /// <summary>The session popover opened from the context ring, read, and closed again.</summary>
        internal async Task RequireSessionPopoverInTheme(Style style)
        {
            const string key = PanelsDesignKey; var theme = owner.SmokeTheme;
            await ShowContext();
            try
            {
                var (flyout, status, block) = SessionInfoDesignParts;
                Require(flyout is not null && ReferenceEquals(flyout.FlyoutPresenterStyle, style), $"{key} ({theme}): the session popover must use the card presenter style");
                await WaitUI(() => sessionInfoOpen && status is { IsLoaded: true } && Ancestor(status, n => n is FlyoutPresenter) is not null, () => $"{key} ({theme}): the session popover never opened");
                owner.RequirePresenter((FlyoutPresenter)Ancestor(status!, n => n is FlyoutPresenter)!, "the session popover");
                owner.RequireBrush(status!, e => ((TextBlock)e).Foreground, DesignToken.Ink2, "the session popover's status line", key: key);
                owner.RequireBrush(block!, e => ((Border)e).Background, DesignToken.AccentSoft, "the session popover's context block", key: key);
            }
            finally { sessionInfoFlyout?.Hide(); }
        }

        /// <summary>Selects a tree row the way a click does, so the selection's look can be read.</summary>
        internal async Task FilesSmokeSelect(string path)
        {
            SelectPath(path);
            await WaitUI(() => filesList?.SelectedItem is ListViewItem { Tag: string tag, IsLoaded: true } && tag == path, () => $"{PanelsDesignKey}: the files tree never selected {path}");
        }

        /// <summary>
        /// The files pane (M/FilePaneView.swift:31-470): the filter row's wash and padding, the chosen row on
        /// accent × 0.18 at radius 5, the 40-high preview head on the wash, and the source's keyword and
        /// comment colours (the fixture's windowsOnly.syntax and ink2) on the very runs the light pass read.
        /// </summary>
        internal async Task<(Grid Head, Brush Keyword)> RequireFilesDesignInTheme(PanelsDesignViews? light)
        {
            const string key = PanelsDesignKey; var theme = owner.SmokeTheme; var b = owner.brushes;
            var (filter, tree, head, content) = FilesDesignParts;
            Require(filter is not null && tree is not null && head is not null && content is not null, $"{key} ({theme}): the files pane is missing its filter row, tree, preview head or content");
            owner.RequireSubtle(filter!.Background, "the files filter row", key);
            Require(filter.Padding == new Thickness(10, 7, 10, 7), $"{key} ({theme}): the files filter row padding must be h10 v7; got {filter.Padding}");
            RequireShared(OwnResource(tree!, "ListViewItemBackgroundSelected") as Brush, b.Brush(DesignToken.Accent, DesignMetrics.Opacity.FileSelection), "the files tree's ListViewItemBackgroundSelected", key);
            var selected = tree!.SelectedItem as ListViewItem;
            await WaitUI(() => selected is { IsLoaded: true } && VisualTreeHelper.GetChildrenCount(selected) > 0, () => $"{key} ({theme}): the chosen files row never loaded");
            Require(selected!.CornerRadius == new CornerRadius(DesignMetrics.Radius.FileRow), $"{key} ({theme}): the chosen files row must have Radius.FileRow {DesignMetrics.Radius.FileRow}; got {selected.CornerRadius}");
            var presenter = VisualTreeHelper.GetChild(selected, 0);
            Require(presenter is ListViewItemPresenter, $"{key} ({theme}): the files row's template root must be a ListViewItemPresenter; got {presenter.GetType().Name}");
            owner.RequireBrush(selected, _ => ((ListViewItemPresenter)presenter).SelectedBackground, DesignToken.Accent, "the chosen files row's resolved SelectedBackground", DesignMetrics.Opacity.FileSelection, key);
            if (light is not null) Require(ReferenceEquals(head, light.FilesHead), $"{key} ({theme}): the toggle rebuilt the files preview instead of recolouring it");
            Require(head!.Height == DesignMetrics.Layout.PreviewHead && head.Visibility == Visibility.Visible, $"{key} ({theme}): the preview head must show at Layout.PreviewHead {DesignMetrics.Layout.PreviewHead}; got {head.Height}, {head.Visibility}");
            owner.RequireSubtle(head.Background, "the preview head", key);
            await WaitUI(() => content!.IsLoaded && VisualChildren(content).OfType<RichTextBlock>().Any(), () => $"{key} ({theme}): the source preview never loaded");
            var runs = VisualChildren(content!).OfType<RichTextBlock>().SelectMany(t => t.Blocks.OfType<Paragraph>()).SelectMany(p => p.Inlines.OfType<Run>()).ToList();
            // Read from the live preview right before the check; the dark pass proves recolouring in place by
            // finding the very brush the light pass read on it (a redraw would still use the shared brush).
            var keyword = runs.FirstOrDefault(r => r.Text == "import") ?? throw new InvalidOperationException($"{key} ({theme}): the source preview has no 'import' run; runs: {string.Join("|", runs.Take(8).Select(r => r.Text))}");
            if (light is not null) Require(ReferenceEquals(keyword.Foreground, light.Keyword), $"{key} ({theme}): the 'import' keyword's brush was replaced instead of recoloured; got {Describe(keyword.Foreground)}");
            var want = DesignFixture.Value.GetProperty("windowsOnly").GetProperty("syntax").GetProperty(theme).GetProperty("keyword").GetString()!;
            RequireShared(keyword.Foreground, b.Syntax("keyword")!, "the 'import' keyword", key);
            owner.RequireHex(keyword.Foreground, want, "windowsOnly.syntax.keyword", "the 'import' keyword", key);
            var comment = runs.FirstOrDefault(r => r.Text.StartsWith("//", StringComparison.Ordinal)) ?? throw new InvalidOperationException($"{key} ({theme}): the source preview has no comment run");
            owner.RequireBrush(head, _ => comment.Foreground, DesignToken.Ink2, "a source comment", key: key);
            return (head, keyword.Foreground);
        }
    }
}
