using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

// The design conversion's smoke assertions (.omc/plans/windows-design-conversion.md). Every
// colour is read off the element tree and compared with native/contracts/fixtures/design-tokens.json
// itself, not with the C# palette, and every failure names the token, the expected hex and the
// actual one, so the public check-run annotation alone says what went wrong.
public sealed partial class MainWindow
{
    private static readonly Lazy<JsonElement> DesignFixture = new(() =>
    {
        const string name = "MightyClaude.WinUI.DesignTokens.json";
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(name + " is not embedded in the WinUI app");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    });

    private string SmokeTheme => service.Snapshot.Theme == "light" ? "light" : "dark";

    private static string FixtureHex(string theme, DesignToken token) =>
        DesignFixture.Value.GetProperty(theme).GetProperty(char.ToLowerInvariant(token.ToString()[0]) + token.ToString()[1..]).GetString()!;

    private static string Describe(Brush? brush) => brush switch
    {
        SolidColorBrush solid => $"#{solid.Color.A:X2}{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}",
        null => "no brush",
        _ => brush.GetType().Name,
    };

    /// <summary>
    /// A brush read off an element is the token's colour in the current theme at an opacity:
    /// alpha round(opacity × 255), each channel within 1 of the fixture's hex.
    /// </summary>
    private void RequireBrush(FrameworkElement element, Func<FrameworkElement, Brush?> get, DesignToken token, string what, double opacity = 1, string key = "designTokens")
    {
        var theme = SmokeTheme;
        var expected = FixtureHex(theme, token);
        var actual = get(element);
        var rgb = Convert.ToUInt32(expected[1..], 16);
        var alpha = (int)Math.Round(opacity * 255);
        Require(actual is SolidColorBrush { Color: var c }
            && Math.Abs(c.A - alpha) <= 1 && Math.Abs(c.R - (int)(rgb >> 16 & 0xFF)) <= 1 && Math.Abs(c.G - (int)(rgb >> 8 & 0xFF)) <= 1 && Math.Abs(c.B - (int)(rgb & 0xFF)) <= 1,
            $"{key} ({theme}): {what} must be token {token} {expected} at opacity {opacity}; got {Describe(actual)}");
    }

    /// <summary>
    /// A split view's line (<see cref="DesignBrushes.SplitLine"/>): the shared brush for its day token, which is that
    /// token's colour by day and black by night, as AppKit draws a split view's divider.
    /// </summary>
    private void RequireSplitLine(FrameworkElement element, Func<FrameworkElement, Brush?> get, DesignToken day, double opacity, string what, string key)
    {
        var theme = SmokeTheme; var actual = get(element);
        Require(ReferenceEquals(actual, brushes.SplitLine(day, opacity)), $"{key} ({theme}): {what} must be the shared split-line brush for token {day} at {opacity}; got {Describe(actual)}");
        if (theme == "light") RequireBrush(element, get, day, what, opacity, key);
        else Require(actual is SolidColorBrush { Color: { A: 255, R: 0, G: 0, B: 0 } }, $"{key} ({theme}): {what} must be black by night; got {Describe(actual)}");
    }

    private static void RequireRadius(Border border, double radius, string what, string key = "designTokens") =>
        Require(border.CornerRadius == new CornerRadius(radius), $"{key}: {what} must have radius {radius}; got {border.CornerRadius}");

    private static void RequireThickness(Border border, double width, string what, string key = "designTokens") =>
        Require(border.BorderThickness == new Thickness(width), $"{key}: {what} must have a {width} border; got {border.BorderThickness}");

    /// <summary>
    /// Stage 1 in the theme just rendered: the window background is the shared page brush with
    /// the fixture's page hex; every stock-control resource DesignBrushes writes is in both theme
    /// dictionaries with its token's fixture hex; and a real accent button in the tree resolves
    /// its background to the accent, which shows WinUI honours ThemeDictionaries added at runtime.
    /// </summary>
    private void RequireDesignTokensInTheme(Button accentProbe)
    {
        var theme = SmokeTheme;
        Require(root.RequestedTheme == (theme == "light" ? ElementTheme.Light : ElementTheme.Dark), $"designTokens ({theme}): the window did not take the {theme} theme");
        Require(ReferenceEquals(root.Background, brushes.Brush(DesignToken.Page)), $"designTokens ({theme}): the window background is not the shared page brush; got {Describe(root.Background)}");
        RequireBrush(root, e => ((Grid)e).Background, DesignToken.Page, "the window background");
        foreach (var key in new[] { "Light", "Dark" })
        {
            var themeResources = Application.Current.Resources.ThemeDictionaries.TryGetValue(key, out var dictionary) ? dictionary as ResourceDictionary : null;
            Require(themeResources is not null, $"designTokens ({theme}): Application.Resources.ThemeDictionaries has no {key} dictionary");
            foreach (var (name, token, opacity, isColor) in DesignBrushes.ControlResources)
            {
                var expected = FixtureHex(key.ToLowerInvariant(), token);
                var want = $"#{(int)Math.Round(opacity * 255):X2}{expected[1..]}";
                var value = themeResources!.TryGetValue(name, out var found) ? found : null;
                var got = value switch { Windows.UI.Color c => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}", Brush b => Describe(b), null => "missing", _ => value.GetType().Name };
                Require(got == want && (value is Windows.UI.Color) == isColor, $"designTokens ({theme}): ThemeDictionaries[{key}][{name}] must be token {token} {expected} at opacity {opacity}; got {got}");
            }
            var font = themeResources!.TryGetValue(DesignBrushes.ControlFontResource, out var family) ? (family as FontFamily)?.Source : null;
            Require(font == DesignMetrics.Font.Body, $"designTokens ({theme}): ThemeDictionaries[{key}][{DesignBrushes.ControlFontResource}] must be '{DesignMetrics.Font.Body}'; got '{font ?? "missing"}'");
        }
        accentProbe.UpdateLayout(); root.UpdateLayout();
        RequireBrush(accentProbe, e => ((Control)e).Background, DesignToken.Accent,
            "the resolved Background of a stock AccentButtonStyle button (anything else means WinUI ignored the ThemeDictionaries DesignBrushes.ApplyControlResources added at runtime, and the overrides must move to App.xaml)");
    }

    /// <summary>A stock accent button in the window, invisible and inert, that the theme checks read; removed by the caller.</summary>
    private Button AddAccentProbe()
    {
        // In the star row, top-left over the sidebar, so the auto rows and the layout keep their size.
        var probe = new Button { Content = "Aa", Style = (Style)Application.Current.Resources["AccentButtonStyle"], Opacity = 0, IsHitTestVisible = false, IsTabStop = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(probe, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Grid.SetRow(probe, 1); root.Children.Add(probe); probe.ApplyTemplate(); root.UpdateLayout();
        return probe;
    }

    /// <summary>
    /// After the toggle: the brushes handed out before it are the same instances, recoloured to
    /// the new theme's hex in place, so a reused pane follows the theme without being rebuilt.
    /// </summary>
    private void RequireDesignBrushesRecolouredInPlace(SolidColorBrush page, SolidColorBrush card, string before)
    {
        var theme = SmokeTheme;
        Require(theme != before, $"designTokens: the theme did not change from {before}");
        Require(ReferenceEquals(page, brushes.Brush(DesignToken.Page)) && ReferenceEquals(card, brushes.Brush(DesignToken.Card)), $"designTokens ({theme}): the page or card brush was replaced instead of recoloured");
        Require(ReferenceEquals(root.Background, page), $"designTokens ({theme}): the window background left the shared page brush; got {Describe(root.Background)}");
        var expected = FixtureHex(theme, DesignToken.Card);
        Require(Describe(card) == "#FF" + expected[1..] && expected != FixtureHex(before, DesignToken.Card), $"designTokens ({theme}): the card brush must hold token Card {expected} after the toggle from {before}; got {Describe(card)}");
    }

    private const string AppShellKey = "appShellDesign";

    /// <summary>
    /// Stage 2, the app shell, in the theme just rendered (M/WorkspaceView.swift, M/SessionPaneView.swift:164):
    /// the edge-to-edge, full-height sidebar surface with its trailing line; the workspace header's
    /// type; the dock inset; the pane card (card, 1pt, radius 11, line or accent × 0.58 when
    /// active); the status bar (subtle wash, top line, 10pt ink2); the error ink; and the system
    /// title bar's colours.
    /// </summary>
    private void RequireAppShellInTheme(PaneView active, PaneView inactive)
    {
        const string key = AppShellKey; var theme = SmokeTheme;
        Require(root.Padding == new Thickness(0) && root.ColumnSpacing == 0 && root.RowSpacing == 0,
            $"{key} ({theme}): the window root must have no padding or spacing, so the sidebar runs edge to edge; got padding {root.Padding}, spacing {root.ColumnSpacing}/{root.RowSpacing}");
        root.UpdateLayout();
        Require(Grid.GetRow(sidebarSurface) == 0 && Grid.GetRowSpan(sidebarSurface) == root.RowDefinitions.Count && Math.Abs(sidebarSurface.ActualHeight - root.ActualHeight) < 1,
            $"{key} ({theme}): the sidebar surface must fill the window height; got row {Grid.GetRow(sidebarSurface)} span {Grid.GetRowSpan(sidebarSurface)} of {root.RowDefinitions.Count}, height {sidebarSurface.ActualHeight:F1} of {root.ActualHeight:F1}");
        RequireBrush(sidebarSurface, e => ((Border)e).Background, DesignToken.Sidebar, "the sidebar surface background", key: key);
        // The split view's divider is darker than both surfaces on the Mac: #CACECF by day, where the separator stands for it, and black by night.
        RequireSplitLine(sidebarSurface, e => ((Border)e).BorderBrush, DesignToken.Ink, SeparatorOpacity, "the sidebar's trailing divider", key);
        Require(sidebarSurface.BorderThickness == new Thickness(0, 0, DesignMetrics.Stroke.Line, 0), $"{key} ({theme}): the sidebar divider must be Stroke.Line {DesignMetrics.Stroke.Line} on the trailing edge only; got {sidebarSurface.BorderThickness}");
        var column = root.ColumnDefinitions[0];
        Require(column.MinWidth == DesignMetrics.Layout.SidebarMin && column.MaxWidth == DesignMetrics.Layout.SidebarMax,
            $"{key} ({theme}): the sidebar column must be bounded by Layout.SidebarMin {DesignMetrics.Layout.SidebarMin} and SidebarMax {DesignMetrics.Layout.SidebarMax}; got {column.MinWidth}..{column.MaxWidth}");

        var workspace = service.Snapshot.Workspaces.First(w => w.Id == service.Snapshot.ActiveWorkspaceId);
        // Over the dock the error banner, then the header (M/WorkspaceView.swift:16-22).
        Require(workspaceHeader.Visibility == Visibility.Visible && workspaceHeaderName.Text == workspace.Name && Grid.GetRow(detailTop) == 0 && Grid.GetColumn(detailTop) == 1
            && detailTop.Children.Count == 2 && ReferenceEquals(detailTop.Children[0], errorBanner) && ReferenceEquals(detailTop.Children[1], workspaceHeader),
            $"{key} ({theme}): the workspace header must show the active workspace '{workspace.Name}' over the dock, under the error banner; got '{workspaceHeaderName.Text}', {workspaceHeader.Visibility}, row {Grid.GetRow(detailTop)} column {Grid.GetColumn(detailTop)}, {detailTop.Children.Count} parts");
        Require(workspaceHeader.Padding == new Thickness(24, 14, 24, 10), $"{key} ({theme}): the workspace header padding must be h24 t14 b10; got {workspaceHeader.Padding}");
        Require(workspaceHeader.BorderThickness == new Thickness(0, 0, 0, DesignMetrics.Stroke.Line), $"{key} ({theme}): the divider under the workspace header must be a bottom Stroke.Line {DesignMetrics.Stroke.Line} only; got {workspaceHeader.BorderThickness}");
        // A SwiftUI Divider (M/WorkspaceView.swift:22): the separator, the label colour at a tenth.
        RequireBrush(workspaceHeader, e => ((Grid)e).BorderBrush, DesignToken.Ink, "the divider under the workspace header", SeparatorOpacity, key);
        var headerCounts = workspaceHeaderCounts ?? throw new InvalidOperationException($"{key} ({theme}): the workspace header has no counts");
        var headerRun = headerCounts.Entries.First(e => e.Tone == DesignTone.Run).Entry;
        Require(AutomationProperties.GetAutomationId(workspaceHeader) == "workspace-header-" + workspace.Id && AutomationProperties.GetAutomationId(headerCounts.View) == "workspace-header-status-" + workspace.Id
            && AutomationProperties.GetAutomationId(headerRun) == "workspace-header-running-" + workspace.Id,
            $"{key} ({theme}): the header, its counts and its running count must carry the Mac's ids workspace-header-{workspace.Id} / workspace-header-status-{workspace.Id} / workspace-header-running-{workspace.Id}; got '{AutomationProperties.GetAutomationId(workspaceHeader)}' / '{AutomationProperties.GetAutomationId(headerCounts.View)}' / '{AutomationProperties.GetAutomationId(headerRun)}'");
        // StatusCounts(long: true) (M/WorkspaceView.swift:305-307, 459-502): every state, zeros left out, each named.
        Require(headerCounts.Entries.Select(e => e.Tone).SequenceEqual(StatusCountsView.Tones(true)) && Grid.GetColumn(headerCounts.View) == 1 && workspaceHeader.ColumnSpacing == 14,
            $"{key} ({theme}): the header's counts must be the long form (wait, run, err, done, stop, idle) between the name and the files button, 14 apart; got {string.Join(", ", headerCounts.Entries.Select(e => e.Tone))} in column {Grid.GetColumn(headerCounts.View)}, spacing {workspaceHeader.ColumnSpacing}");
        RequireCounts(headerCounts, WorkDashboard.WorkspaceBadges(service.Snapshot.Sessions.Where(s => s.WorkspaceId == workspace.Id), DashboardAttention), "the workspace header's counts", key, false);
        Require(workspaceHeaderName.FontSize == DesignMetrics.Type.Header && workspaceHeaderName.FontWeight.Weight == Microsoft.UI.Text.FontWeights.SemiBold.Weight,
            $"{key} ({theme}): the workspace header name must be Type.Header {DesignMetrics.Type.Header}pt semibold; got {workspaceHeaderName.FontSize}pt weight {workspaceHeaderName.FontWeight.Weight}");
        RequireBrush(workspaceHeaderName, e => ((TextBlock)e).Foreground, DesignToken.Ink, "the workspace header name", key: key);
        Require(workspaceHeaderPath.FontFamily?.Source == DesignMetrics.Font.Mono && workspaceHeaderPath.FontSize == DesignMetrics.Type.Mono && workspaceHeaderPath.IsTextSelectionEnabled,
            $"{key} ({theme}): the workspace path must be selectable Font.Mono '{DesignMetrics.Font.Mono}' at Type.Mono {DesignMetrics.Type.Mono}pt; got '{workspaceHeaderPath.FontFamily?.Source}' at {workspaceHeaderPath.FontSize}pt");
        RequireBrush(workspaceHeaderPath, e => ((TextBlock)e).Foreground, DesignToken.Ink2, "the workspace header path", key: key);
        Require(workspaceHeaderFiles.Width == 26 && workspaceHeaderFiles.Height == 24 && AutomationProperties.GetAutomationId(workspaceHeaderFiles) == "workspace-open-files-" + workspace.Id,
            $"{key} ({theme}): the header files button must be 26×24 for the active workspace; got {workspaceHeaderFiles.Width}×{workspaceHeaderFiles.Height} '{AutomationProperties.GetAutomationId(workspaceHeaderFiles)}'");

        var dock = (panes.Children.OfType<ScrollViewer>().FirstOrDefault()?.Content as FrameworkElement)?.Margin;
        Require(dock == new Thickness(DesignMetrics.Inset.Dock), $"{key} ({theme}): the pane dock must sit Inset.Dock {DesignMetrics.Inset.Dock} inside its scroll view; got {dock?.ToString() ?? "no dock"}");

        foreach (var (pane, name, token, opacity) in new[] { (active, "the active pane", DesignToken.Accent, DesignMetrics.Opacity.PaneActiveBorder), (inactive, "an inactive pane", DesignToken.Line, 1.0) })
        {
            RequireRadius(pane.Container, DesignMetrics.Radius.Pane, $"{name} card (Radius.Pane)", key);
            RequireThickness(pane.Container, DesignMetrics.Stroke.Line, $"{name} card (Stroke.Line)", key);
            RequireBrush(pane.Container, e => ((Border)e).Background, DesignToken.Card, $"{name} card background", key: key);
            RequireBrush(pane.Container, e => ((Border)e).BorderBrush, token, $"{name} card border", opacity, key);
        }

        RequireSubtle(statusBar, "the status bar background", key);
        RequireBrush(statusBar, e => ((Border)e).BorderBrush, DesignToken.Ink, "the status bar's top divider (M/WorkspaceView.swift:394, a Divider)", SeparatorOpacity, key);
        // Padding h20 v8 (M/WorkspaceView.swift:393); the Mac's top line is an overlay, so the 8 above counts the line here.
        Require(statusBar.BorderThickness == new Thickness(0, DesignMetrics.Stroke.Line, 0, 0) && statusBar.Padding == new Thickness(20, 8 - DesignMetrics.Stroke.Line, 20, 8) && Grid.GetRow(statusBar) == 2 && Grid.GetColumn(statusBar) == 1,
            $"{key} ({theme}): the status bar must be under the dock with a top Stroke.Line {DesignMetrics.Stroke.Line} and padding h20 v8 (the line inside the 8 above); got border {statusBar.BorderThickness}, padding {statusBar.Padding}, row {Grid.GetRow(statusBar)} column {Grid.GetColumn(statusBar)}");
        Require(status.FontSize == DesignMetrics.Type.Small && status.TextWrapping == TextWrapping.NoWrap && status.TextTrimming == TextTrimming.CharacterEllipsis,
            $"{key} ({theme}): the status text must be one trimmed Type.Small {DesignMetrics.Type.Small}pt line; got {status.FontSize}pt, {status.TextWrapping}, {status.TextTrimming}");
        RequireStatusBarOrder(key);
        // The Mac's error banner (M/WorkspaceView.swift:405-411): on errSoft, padding 12, its message in the default ink, shown only with a message.
        Require((error.Visibility == Visibility.Collapsed) == string.IsNullOrEmpty(error.Text) && errorBanner.Visibility == error.Visibility,
            $"{key} ({theme}): the error banner must be collapsed exactly while there is no message; got banner {errorBanner.Visibility}, line {error.Visibility} with '{error.Text}'");
        RequireBrush(errorBanner, e => ((Border)e).Background, DesignToken.ErrSoft, "the error banner", key: key);
        Require(errorBanner.Padding == new Thickness(12) && error.FontSize == 12 && error.IsTextSelectionEnabled && errorBanner.Child is Grid { ColumnSpacing: 9, Children: [FontIcon, TextBlock, Microsoft.UI.Xaml.Controls.Button] },
            $"{key} ({theme}): the error banner must be a warning symbol, the selectable 12pt message and a close button, 9 apart, padding 12; got padding {errorBanner.Padding}, {error.FontSize}pt");
        RequireBrush((FontIcon)((Grid)errorBanner.Child).Children[0], e => ((FontIcon)e).Foreground, DesignToken.ErrText, "the error banner's symbol", key: key);
        // One 10pt row: padding 8 + the capsule's 21 + 8, the line inside the top 8. The Mac's bar is 37 tall on every screen (docs/design-system/screens: 74px at 2x).
        Require(Math.Abs(statusBar.ActualHeight - (16 + CompanionCapsuleHeight)) < 0.6, $"{key} ({theme}): the status bar must be one row {16 + CompanionCapsuleHeight} tall, as the Mac's; got {statusBar.ActualHeight:F1}");
        RequireBrush(status, e => ((TextBlock)e).Foreground, DesignToken.Ink2, "the status text", key: key);
        RequireBrush(error, e => ((TextBlock)e).Foreground, DesignToken.Ink, "the error text", key: key);
        RequireTitleBar(key);
    }

    /// <summary>
    /// The status bar in the Mac's order (M/WorkspaceView.swift:375-395, M/StatusBarUsage.swift:159-193,
    /// M/AgentCompanionViews.swift:5-25): the machine symbol and words; then, at the trailing end and 7
    /// apart, the active workspace's pane count, a dot, the running count, the update badge, a 12-high
    /// line, the usage chips (each the provider's mark and its windows on a subtle capsule) with their own
    /// line, and the pet controls — the paw and the status capsule.
    /// </summary>
    private void RequireStatusBarOrder(string key)
    {
        var theme = SmokeTheme; var state = service.Snapshot;
        // At rest the bar names the machine; the CLI versions are not its words any more.
        Require(runtime is not null && runtimeChecks == 0 && status.Text == Locale.Get("window.status.thisMachine"),
            $"{key} ({theme}): at rest the status words must name the machine, '{Locale.Get("window.status.thisMachine")}'; got '{status.Text}' (runtime {(runtime is null ? "unread" : "read")}, {runtimeChecks} checks running)");
        Require(statusBar.Child is Grid { ColumnSpacing: StatusBarSpacing, Children: [Grid leading, StackPanel trailing] } && ReferenceEquals(trailing, statusTrailing) && Grid.GetColumn(trailing) == 1
            && leading.ColumnSpacing == StatusBarSpacing && leading.Children is [Grid { Width: 12, Height: 10 }, TextBlock words] && ReferenceEquals(words, status),
            $"{key} ({theme}): the status bar must be the 12×10 machine symbol and the status words, then the trailing parts, {StatusBarSpacing} apart");
        var machine = (Grid)((Grid)((Grid)statusBar.Child).Children[0]).Children[0];
        RequireBrush(machine.Children.OfType<Border>().Single(), e => ((Border)e).BorderBrush, DesignToken.Ink2, "the machine symbol", key: key);
        var parts = statusTrailing.Children.ToList();
        Require(statusTrailing.Spacing == StatusBarSpacing && parts.Count == 8 && ReferenceEquals(parts[0], statusPanes) && parts[1] is TextBlock && ReferenceEquals(parts[2], statusRunning)
            && ReferenceEquals(parts[3], updateBadge) && parts[4] is Border && ReferenceEquals(parts[5], usageButton) && ReferenceEquals(parts[6], usageDivider)
            && parts[7] is StackPanel { Spacing: 10, Children: [Microsoft.UI.Xaml.Controls.Button paw, Microsoft.UI.Xaml.Controls.Button capsule] } && ReferenceEquals(paw, companionToggleControl) && ReferenceEquals(capsule, companionStatusControl),
            $"{key} ({theme}): the status bar's trailing parts must be the pane count, a dot, the running count, the update badge, a line, the usage chips, their line and the pet controls (paw, then capsule, 10 apart), {StatusBarSpacing} apart; got {parts.Count} parts: {string.Join(", ", parts.Select(p => p.GetType().Name + "'" + AutomationProperties.GetAutomationId(p) + "'"))}");
        string Count(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var panesWant = Locale.Get("window.status.paneCount", new Dictionary<string, string> { ["count"] = Count(state.Sessions.Count(s => s.WorkspaceId == state.ActiveWorkspaceId)) });
        var runningWant = Locale.Get("window.status.runningCount", new Dictionary<string, string> { ["count"] = Count(state.Sessions.Count(s => s.Status == "running")) });
        Require(statusPanes.Text == panesWant && statusRunning.Text == runningWant, $"{key} ({theme}): the status bar must count '{panesWant}' and '{runningWant}'; got '{statusPanes.Text}' and '{statusRunning.Text}'");
        var dot = (TextBlock)parts[1];
        Require(dot.Margin == new Thickness(3, 0, 3, 0), $"{key} ({theme}): the dot between the counts must have 3 on either side; got {dot.Margin}");
        foreach (var (text, what) in new[] { (statusPanes, "the pane count"), (dot, "the dot between the counts"), (statusRunning, "the running count") })
        {
            Require(text.FontSize == DesignMetrics.Type.Small, $"{key} ({theme}): {what} must be {DesignMetrics.Type.Small}pt; got {text.FontSize}pt");
            RequireBrush(text, e => ((TextBlock)e).Foreground, DesignToken.Ink2, what, key: key);
        }
        // No newer version in a smoke run: the badge takes no room; it is the accent 10pt medium words behind the arrow disc.
        Require(updateBadge is { Visibility: Visibility.Collapsed } && AutomationProperties.GetAutomationId(updateBadge) == "app-update-badge" && updateBadgeText.FontSize == DesignMetrics.Type.Small && updateBadgeText.FontWeight.Weight == Microsoft.UI.Text.FontWeights.Medium.Weight,
            $"{key} ({theme}): the update badge must be hidden with no newer version, 10pt medium; got {updateBadge?.Visibility}, {updateBadgeText.FontSize}pt weight {updateBadgeText.FontWeight.Weight}");
        RequireBrush(updateBadgeText, e => ((TextBlock)e).Foreground, DesignToken.Accent, "the update badge", key: key);
        foreach (var (line, what) in new[] { ((Border)parts[4], "the line before the usage chips"), (usageDivider!, "the line after the usage chips") })
        {
            Require(line.Width == DesignMetrics.Stroke.Line && line.Height == StatusDividerHeight && line.Margin == new Thickness(StatusDividerInset, 0, StatusDividerInset, 0),
                $"{key} ({theme}): {what} must be {StatusDividerHeight} high with {StatusDividerInset} on either side; got {line.Width}×{line.Height}, {line.Margin}");
            RequireBrush(line, e => ((Border)e).Background, DesignToken.Ink, what, SeparatorOpacity, key);
        }
        // The chips show with a provider that has an agent pane, and their line with them.
        Require(usageButton is { Visibility: Visibility.Visible } && usageDivider!.Visibility == Visibility.Visible && usageChips.Spacing == 6 && usageChips.Children.Count > 0,
            $"{key} ({theme}): the usage chips and the line after them must show while an agent pane exists, 6 apart; got {usageButton?.Visibility}, line {usageDivider!.Visibility}, {usageChips.Children.Count} chips");
        foreach (var chip in usageChips.Children.OfType<Border>())
        {
            var id = AutomationProperties.GetAutomationId(chip);
            RequireSubtle(chip, $"the usage chip {id}", key);
            // Sizes with a fraction are read back as the single-precision values the layout holds.
            Require(chip.Height == UsageChipHeight && chip.Padding == new Thickness(7, 0, 7, 0) && chip.CornerRadius == new CornerRadius(UsageChipHeight / 2)
                && chip.Child is StackPanel { Spacing: 5, Children.Count: >= 2 } face && face.Children[0] is Microsoft.UI.Xaml.Shapes.Path mark && Math.Abs(mark.Width - UsageChipMark) < 0.01
                && face.Children.Skip(1).All(word => word is TextBlock { FontSize: DesignMetrics.Type.Small }),
                $"{key} ({theme}): the usage chip {id} must be the provider's {UsageChipMark:F2}pt mark and 10pt words, 5 apart, on a capsule {UsageChipHeight} high (the Mac's 10pt line in v3) with h7; got {chip.Height} high, padding {chip.Padding}, a {(((chip.Child as StackPanel)?.Children.FirstOrDefault() as FrameworkElement)?.Width ?? 0):F2}pt {(chip.Child as StackPanel)?.Children.FirstOrDefault()?.GetType().Name}");
            foreach (var word in ((StackPanel)chip.Child).Children.OfType<TextBlock>())
                Require(ReferenceEquals(word.Foreground, brushes.Brush(DesignToken.Ink2)) || ReferenceEquals(word.Foreground, brushes.Brush(DesignToken.WaitText)), $"{key} ({theme}): the usage chip {id}'s words must be ink2, or waitText near a limit; got {Describe(word.Foreground)}");
        }
        // The pet controls (M/AgentCompanionViews.swift:8-23).
        Require(ReferenceEquals(companionGlyph.Foreground, brushes.Brush(companionPreferences.Enabled ? DesignToken.Accent : DesignToken.Ink2)) && companionToggleControl!.Content is Viewbox pawBox
            && Math.Abs(pawBox.Width - CompanionGlyphSize) < 0.01 && Math.Abs(pawBox.Height - CompanionGlyphSize) < 0.01,
            $"{key} ({theme}): the paw must be {CompanionGlyphSize}pt, accent while the pet shows and ink2 otherwise; got {Describe(companionGlyph.Foreground)} with the pet {(companionPreferences.Enabled ? "showing" : "hidden")}");
        var pill = companionStatusCapsule!;
        RequireSubtle(pill, "the agent status capsule", key);
        Require(pill.Height == CompanionCapsuleHeight && pill.Padding == new Thickness(8, 0, 8, 0) && pill.CornerRadius == new CornerRadius(CompanionCapsuleHeight / 2) && pill.Child is StackPanel { Spacing: 5 },
            $"{key} ({theme}): the agent status capsule must be {CompanionCapsuleHeight} high (the Mac's 13-high symbol in v4), padding h8, its symbol 5 from its count; got {pill.Height}, {pill.Padding}, {pill.CornerRadius}");
        var busy = state.Sessions.Count(s => s.Kind == "claude" && CompanionStatus(s) is "running" or "waiting" or "starting" or "queued");
        var (waveform, dots) = (companionStatusBusy!, companionStatusIdle!);
        Require((waveform.Visibility == Visibility.Visible) == (busy > 0) && (dots.Visibility == Visibility.Visible) == (busy == 0) && (companionStatusNumber.Visibility == Visibility.Visible) == (busy > 0)
            && (busy == 0 || companionStatusNumber.Text == Count(busy)),
            $"{key} ({theme}): the capsule must show the waveform and the count while agents work ({busy}) and the dots alone otherwise; got waveform {waveform.Visibility}, dots {dots.Visibility}, count '{companionStatusNumber.Text}' {companionStatusNumber.Visibility}");
        RequireBrush(companionStatusNumber, e => ((TextBlock)e).Foreground, DesignToken.Ink2, "the agent status count", key: key);
    }

    /// <summary>A background is the shared subtle wash with the fixture's windowsOnly.subtle colour and opacity for this theme.</summary>
    private void RequireSubtle(Border border, string what, string key) => RequireSubtle(border.Background, what, key);

    private void RequireSubtle(Brush? actual, string what, string key)
    {
        var theme = SmokeTheme; var subtle = DesignFixture.Value.GetProperty("windowsOnly").GetProperty("subtle").GetProperty(theme);
        var hex = subtle.GetProperty("color").GetString()!; var opacity = subtle.GetProperty("opacity").GetDouble();
        var want = $"#{(int)Math.Round(opacity * 255):X2}{hex[1..]}";
        Require(ReferenceEquals(actual, brushes.Subtle) && Describe(actual) == want,
            $"{key} ({theme}): {what} must be the shared subtle wash {hex} at opacity {opacity} ({want}); got {Describe(actual)}{(ReferenceEquals(actual, brushes.Subtle) ? "" : " from another brush")}");
    }

    /// <summary>A brush draws nothing: alpha 0.</summary>
    private void RequireClear(Brush? actual, string what, string key) =>
        Require(actual is SolidColorBrush { Color.A: 0 }, $"{key} ({SmokeTheme}): {what} must be transparent (alpha 00); got {Describe(actual)}");

    private static void RequireFont(TextBlock text, double size, Windows.UI.Text.FontWeight weight, string what, string key) =>
        Require(text.FontSize == size && text.FontWeight.Weight == weight.Weight, $"{key}: {what} must be {size}pt weight {weight.Weight}; got {text.FontSize}pt weight {text.FontWeight.Weight}");

    /// <summary>
    /// The background a button's template gives a visual state ("PointerOver", "Pressed"): the
    /// value of the state's Background key frame in the template root's CommonStates group, read
    /// after the button entered the state (so the frame's ThemeResource is resolved against the
    /// button's own resources) and before it returns to "Normal". Reading the frame, not the
    /// animated property, keeps the check independent of when the storyboard ticks. The button's
    /// own resource for the state (written once into <c>button.Resources</c>) must be
    /// <paramref name="expectedResource"/>.
    /// </summary>
    private static async Task<Brush?> StateBackground(Button button, string state, Brush expectedResource, string key)
    {
        var id = AutomationProperties.GetAutomationId(button);
        var resourceKey = "ButtonBackground" + state;
        var resource = OwnResource(button, resourceKey);
        Require(ReferenceEquals(resource, expectedResource),
            $"{key}: the button {id} must carry the shared {Describe(expectedResource)} brush as {resourceKey}; got {Describe(resource as Brush)}");
        await WaitUI(() => button.IsLoaded);
        button.ApplyTemplate();
        var presenter = VisualTreeHelper.GetChildrenCount(button) > 0 ? VisualTreeHelper.GetChild(button, 0) as FrameworkElement : null;
        Require(presenter is not null, $"{key}: the button {id} has no template root");
        Require(VisualStateManager.GoToState(button, state, false), $"{key}: the button {id} could not enter its {state} state");
        try
        {
            var visual = VisualStateManager.GetVisualStateGroups(presenter!).FirstOrDefault(g => g.Name == "CommonStates")?.States.FirstOrDefault(v => v.Name == state);
            var frame = visual?.Storyboard?.Children.OfType<Microsoft.UI.Xaml.Media.Animation.ObjectAnimationUsingKeyFrames>()
                .FirstOrDefault(a => Microsoft.UI.Xaml.Media.Animation.Storyboard.GetTargetProperty(a) == "Background")?.KeyFrames.FirstOrDefault();
            Require(frame is not null, $"{key}: the button {id}'s template has no Background key frame in CommonStates.{state}");
            return frame!.Value as Brush;
        }
        finally { VisualStateManager.GoToState(button, "Normal", false); }
    }

    private const string SidebarDesignKey = "sidebarDesign";

    /// <summary>
    /// Stage 3, the sidebar, in the theme just rendered (M/WorkspaceView.swift:71-341, 455-529,
    /// M/BetaBadge.swift): the search field with and without results, the work-status entry, the
    /// section header, the selected and other workspace rows with their counts, the selected and
    /// hovered pane rows, the meta line, the amber waiting words, the beta capsules, the add row,
    /// the layout picker and the footer. Every colour is read off the element tree.
    /// </summary>
    private async Task RequireSidebarDesignInTheme()
    {
        // Rows are drawn as selected only while the dashboard is hidden; the check opens the
        // active workspace's list and gives the lists back as it found them.
        HideDashboard();
        var expanded = service.Snapshot.ExpandedWorkspaceIds;
        try { await RequireSidebarDesignChecks(); }
        finally { await service.UpdateAsync(s => s with { ExpandedWorkspaceIds = expanded }); RenderSidebar(); }
    }

    private async Task RequireSidebarDesignChecks()
    {
        const string key = SidebarDesignKey; var theme = SmokeTheme;
        var workspaceId = service.Snapshot.ActiveWorkspaceId ?? throw new InvalidOperationException($"{key} ({theme}): needs an active workspace");
        if (!WorkspaceDisclosure.Expanded(service.Snapshot).Contains(workspaceId)) await service.UpdateAsync(s => WorkspaceDisclosure.Open(s, workspaceId));
        search.Text = ""; RenderSidebar(); root.UpdateLayout();
        var state = service.Snapshot;

        // Only the list scrolls (M/WorkspaceView.swift:69-96): the search, the work-status entry and
        // the section header sit above the scroll view, the open-folder button under it.
        var scroller = VisualChildren(sidebarSurface).OfType<ScrollViewer>().FirstOrDefault(v => ReferenceEquals(v.Content, sidebar));
        Require(scroller is not null && sidebar.Children.Contains(workspaces), $"{key} ({theme}): the workspace list must be the content of the sidebar's scroll view");
        var scrolled = VisualChildren(scroller!).ToHashSet();
        foreach (var (part, what) in new (FrameworkElement, string)[] { (sidebarSearchBox, "the search field"), (dashboardButton!, "the work-status entry"), (sessionsHeaderRow, "the section header"), (addFolderButton, "the open-folder button") })
            Require(!scrolled.Contains(part), $"{key} ({theme}): {what} must stay put outside the scrolling list");

        // The search field (M/WorkspaceView.swift:71-77).
        RequireSubtle(sidebarSearchBox, "the search field's wash", key);
        RequireRadius(sidebarSearchBox, DesignMetrics.Radius.Search, "the search field (Radius.Search)", key);
        Require(sidebarSearchBox.Padding == new Thickness(9) && sidebarSearchBox.Margin == new Thickness(14, 10, 14, 0),
            $"{key} ({theme}): the search field must have padding 9 and margin h14 t10; got padding {sidebarSearchBox.Padding}, margin {sidebarSearchBox.Margin}");
        RequireBrush(sidebarSearchIcon, e => ((FontIcon)e).Foreground, DesignToken.SidebarInk2, "the search magnifier", key: key);
        Require(search.FontSize == 12 && search.BorderThickness == new Thickness(0), $"{key} ({theme}): the search text must be 12pt with no border; got {search.FontSize}pt, border {search.BorderThickness}");
        foreach (var resource in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrushFocused" })
            RequireClear(search.Resources.TryGetValue(resource, out var value) ? value as Brush : null, $"the search box's {resource}", key);
        // AppKit's placeholder is the tertiary ink, the label colour at about a quarter, dimmer than the magnifier (M/WorkspaceView.swift:73).
        Require(ReferenceEquals(search.PlaceholderForeground, brushes.Tertiary), $"{key} ({theme}): the search placeholder must be the shared tertiary brush; got {Describe(search.PlaceholderForeground)}");
        RequireBrush(search, e => ((TextBox)e).PlaceholderForeground, DesignToken.Ink, "the search placeholder", DesignBrushes.TertiaryOpacity, key);
        // The ink that is drawn, not only the one that was asked for: the template's placeholder text takes it.
        var drawnPlaceholder = VisualChildren(search).OfType<TextBlock>().FirstOrDefault(t => t.Name == "PlaceholderTextContentPresenter");
        Require(drawnPlaceholder is not null && ReferenceEquals(drawnPlaceholder.Foreground, search.PlaceholderForeground),
            $"{key} ({theme}): the search field's drawn placeholder must take the placeholder ink; got {Describe(drawnPlaceholder?.Foreground)}{(drawnPlaceholder is null ? " (the template has no PlaceholderTextContentPresenter)" : "")}");
        // 9 + the 12pt text field's 15 + 9, as the Mac's field measures.
        Require(sidebarSearchBox.Child is Grid { Height: SearchRowHeight, ColumnSpacing: 7 } && Math.Abs(sidebarSearchBox.ActualHeight - (SearchRowHeight + 18)) < 0.5,
            $"{key} ({theme}): the search field must be {SearchRowHeight + 18} high (padding 9 around a {SearchRowHeight}-high row), the magnifier 7 from the text; got {sidebarSearchBox.ActualHeight:F1}");
        var workspaceName = state.Workspaces.First(w => w.Id == workspaceId).Name;
        try
        {
            search.Text = workspaceName; RenderSidebar();
            Require(workspaces.Children.Count > 0 && sidebarEmpty.Visibility == Visibility.Collapsed && addFolderButton.Visibility == Visibility.Collapsed,
                $"{key} ({theme}): a search for '{workspaceName}' must list it with no empty text; got {workspaces.Children.Count} rows, empty text {sidebarEmpty.Visibility}");
            search.Text = "no-such-workspace-" + Wire.Id(); RenderSidebar();
            var expected = Locale.Get("sidebar.noSearchResults");
            Require(workspaces.Children.Count == 0 && sidebarEmpty.Visibility == Visibility.Visible && sidebarEmpty.Text == expected,
                $"{key} ({theme}): a search with no match must say '{expected}'; got {workspaces.Children.Count} rows, '{sidebarEmpty.Text}' {sidebarEmpty.Visibility}");
            Require(sidebarEmpty.FontSize == 12 && sidebarEmpty.Padding == new Thickness(16), $"{key} ({theme}): the empty text must be 12pt with padding 16; got {sidebarEmpty.FontSize}pt, {sidebarEmpty.Padding}");
            RequireBrush(sidebarEmpty, e => ((TextBlock)e).Foreground, DesignToken.SidebarInk2, "the empty-search text", key: key);
            Require(addFolderButton.Visibility == Visibility.Visible && addFolderButton.FontSize == 12 && addFolderButton.CornerRadius == new CornerRadius(DesignMetrics.Radius.Search),
                $"{key} ({theme}): the open-folder button must show, 12pt at radius {DesignMetrics.Radius.Search}; got {addFolderButton.Visibility}, {addFolderButton.FontSize}pt, {addFolderButton.CornerRadius}");
            RequireSubtle(addFolderButton.Background, "the open-folder button", key);
            Require(addFolderButton.Content is StackPanel { Children: [FontIcon { Glyph: "\uE8F4" }, TextBlock words] } && words.Text == Locale.Get("sidebar.openFolder") && AutomationProperties.GetName(addFolderButton) == words.Text,
                $"{key} ({theme}): the open-folder button must be the folder-plus symbol and '{Locale.Get("sidebar.openFolder")}'; got '{AutomationProperties.GetName(addFolderButton)}'");
            await CaptureShellSmoke("search-empty-" + theme);
        }
        finally { search.Text = ""; RenderSidebar(); }
        // The text box raises TextChanged later, and each one rebuilds the rows: let them run
        // before the rows below are read, so none is replaced while a hover state plays.
        await Task.Delay(150); RenderSidebar(); root.UpdateLayout();

        // The work-status entry (M/WorkspaceView.swift:136-158).
        var icon = dashboardEntryIcon!; var entry = dashboardEntry!;
        Require(icon.Width == 24 && icon.Height == 24, $"{key} ({theme}): the work-status tile must be 24×24; got {icon.Width}×{icon.Height}");
        RequireRadius(icon, DesignMetrics.Radius.Search, "the work-status tile (Radius.Search)", key);
        RequireBrush(icon, e => ((Border)e).Background, DesignToken.Run, "the work-status tile", key: key);
        // square.grid.2x2.fill: four filled rounded squares, not outlines (M/WorkspaceView.swift:141).
        var squares = (icon.Child as Canvas)?.Children.OfType<Microsoft.UI.Xaml.Shapes.Rectangle>().ToList() ?? [];
        Require(icon.Child is Canvas { Children.Count: 4 } grid && Math.Abs(grid.Width - DashboardEntrySymbol) < 0.01 && Math.Abs(grid.Height - DashboardEntrySymbol) < 0.01 && squares.Count == 4
            && squares.All(square => Math.Abs(square.Width - DashboardEntrySquare) < 0.01 && Math.Abs(square.Height - DashboardEntrySquare) < 0.01 && square is { RadiusX: > 0, Stroke: null }),
            $"{key} ({theme}): the work-status tile's symbol must be four filled {DashboardEntrySquare}pt rounded squares in a {DashboardEntrySymbol}pt box; got {icon.Child?.GetType().Name} with {squares.Count} squares");
        foreach (var square in squares)
            RequireBrush(square, e => ((Microsoft.UI.Xaml.Shapes.Rectangle)e).Fill, DesignToken.OnStatus, "the work-status tile's symbol", key: key);
        RequireFont(dashboardEntryTitle!, DesignMetrics.Type.Title, Microsoft.UI.Text.FontWeights.SemiBold, $"({theme}) the work-status title", key);
        RequireBrush(dashboardEntryTitle!, e => ((TextBlock)e).Foreground, DesignToken.Ink, "the work-status title", key: key);
        Require(entry.Padding == new Thickness(10, 8, 10, 8), $"{key} ({theme}): the work-status entry padding must be h10 v8; got {entry.Padding}");
        RequireRadius(entry, DesignMetrics.Radius.Entry, "the work-status entry (Radius.Entry)", key);
        RequireClear(entry.Background, "the work-status entry while the dashboard is hidden", key);
        // While the dashboard shows, the entry is the sidebar's one selection: no workspace row and no pane
        // row is drawn selected (M/WorkspaceView.swift:137, 161, 216). Shown the way the app shows it.
        var selectedPane = state.ActiveSessionId;
        showsDashboard = true; RenderDashboard(); root.UpdateLayout();
        try
        {
            RequireBrush(entry, e => ((Border)e).Background, DesignToken.Card, "the work-status entry while the dashboard shows", key: key);
            Require(dashboardEntryShadow is { Visibility: Visibility.Visible }, $"{key} ({theme}): the selected work-status entry must show its shadow");
            var row = (Grid)workspaces.Children.OfType<StackPanel>().Single(g => AutomationProperties.GetAutomationId(g) == "sidebar-workspace-" + workspaceId).Children[0];
            RequireClear(row.Background, "the active workspace's row while the dashboard shows", key);
            RequireBrush((FontIcon)((Grid)row.Children.OfType<Button>().First().Content).Children[0], e => ((FontIcon)e).Foreground, DesignToken.SidebarInk2, "the active workspace's folder while the dashboard shows", key: key);
            if (selectedPane is not null && sidebarSessionButtons.TryGetValue(selectedPane, out var paneRow))
            {
                RequireClear(paneRow.Background, "the active pane's row while the dashboard shows", key);
                RequireClear(sidebarRowEdges[selectedPane].BorderBrush, "the active pane's row hairline while the dashboard shows", key);
            }
            Require(workspaceHeader.Visibility == Visibility.Collapsed, $"{key} ({theme}): the workspace header must give way to the dashboard; got {workspaceHeader.Visibility}");
            await CaptureShellSmoke("dashboard-" + theme);
        }
        finally { HideDashboard(); root.UpdateLayout(); }
        RequireClear(entry.Background, "the work-status entry once the dashboard is hidden again", key);

        // The section header "워크스페이스 6".
        Require(sessionsHeader.Text == Locale.Get("sidebar.workspacesHeader"), $"{key} ({theme}): the section header must say '{Locale.Get("sidebar.workspacesHeader")}'; got '{sessionsHeader.Text}'");
        RequireFont(sessionsHeader, DesignMetrics.Type.Small, Microsoft.UI.Text.FontWeights.SemiBold, $"({theme}) the section header", key);
        RequireBrush(sessionsHeader, e => ((TextBlock)e).Foreground, DesignToken.SidebarInk2, "the section header", key: key);
        Require(sessionsCount.FontSize == DesignMetrics.Type.Small && sessionsCount.FontFamily?.Source == DesignMetrics.Font.Mono && sessionsCount.Text == state.Workspaces.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"{key} ({theme}): the section count must be {state.Workspaces.Count} in Font.Mono at {DesignMetrics.Type.Small}pt; got '{sessionsCount.Text}' in '{sessionsCount.FontFamily?.Source}' at {sessionsCount.FontSize}pt");
        RequireBrush(sessionsCount, e => ((TextBlock)e).Foreground, DesignToken.SidebarInk2, "the section count", key: key);
        Require(sessionsHeaderRow.Margin == new Thickness(20, 20, 20, 11), $"{key} ({theme}): the section header padding must be h20 t20 b11; got {sessionsHeaderRow.Margin}");

        // Workspace rows (M/WorkspaceView.swift:160-203).
        Grid WorkspaceHeader(string id) => (Grid)workspaces.Children.OfType<StackPanel>().Single(g => AutomationProperties.GetAutomationId(g) == "sidebar-workspace-" + id).Children[0];
        var header = WorkspaceHeader(workspaceId);
        RequireBrush(header, e => ((Grid)e).Background, DesignToken.SidebarAccent, "the selected workspace row", DesignMetrics.Opacity.SidebarWorkspaceSelected, key);
        Require(header.CornerRadius == new CornerRadius(DesignMetrics.Radius.Search), $"{key} ({theme}): the workspace row must have radius {DesignMetrics.Radius.Search}; got {header.CornerRadius}");
        var select = header.Children.OfType<Button>().First(); var label = (Grid)select.Content;
        var folder = (FontIcon)label.Children[0]; var name = (TextBlock)label.Children[1];
        Require(folder.FontSize == 14 && select.Padding == new Thickness(11, 10, 4, 10) && label.ColumnSpacing == 9,
            $"{key} ({theme}): the workspace row must be folder 14, spacing 9, padding l11 r4 v10; got folder {folder.FontSize}, spacing {label.ColumnSpacing}, padding {select.Padding}");
        RequireBrush(folder, e => ((FontIcon)e).Foreground, DesignToken.SidebarAccent, "the selected workspace's folder", key: key);
        RequireFont(name, DesignMetrics.Type.Row, Microsoft.UI.Text.FontWeights.Medium, $"({theme}) the workspace name", key);
        RequireBrush(name, e => ((TextBlock)e).Foreground, DesignToken.Ink, "the workspace name", key: key);
        var disclosure = workspaceDisclosureButtons[workspaceId];
        Require(disclosure.Width == 18 && disclosure.Height == 18 && disclosure.Content is FontIcon { FontSize: 8 },
            $"{key} ({theme}): the disclosure must be an 8pt chevron in an 18×18 box; got {disclosure.Width}×{disclosure.Height} {disclosure.Content?.GetType().Name}");
        RequireBrush((FontIcon)disclosure.Content!, e => ((FontIcon)e).Foreground, DesignToken.SidebarInk2, "the disclosure chevron", key: key);
        if (state.Workspaces.FirstOrDefault(w => w.Id != workspaceId) is { } other)
        {
            var otherHeader = WorkspaceHeader(other.Id);
            RequireClear(otherHeader.Background, "an unselected workspace row", key);
            RequireBrush((FontIcon)((Grid)otherHeader.Children.OfType<Button>().First().Content).Children[0], e => ((FontIcon)e).Foreground, DesignToken.SidebarInk2, "an unselected workspace's folder", key: key);
        }

        // Pane rows (M/WorkspaceView.swift:214-277, 516-529).
        var activeId = state.ActiveSessionId ?? throw new InvalidOperationException($"{key} ({theme}): needs an active pane");
        var rows = state.Sessions.Where(s => s.WorkspaceId == workspaceId).ToList();
        var inactive = rows.FirstOrDefault(s => s.Id != activeId && views.ContainsKey(s.Id) && WorkDashboard.IsCounted(s.Kind) && ProviderMark.SidebarProvider(s) is not null)
            ?? throw new InvalidOperationException($"{key} ({theme}): needs an agent pane besides the active one in the workspace");
        var activeRow = sidebarSessionButtons[activeId]; var inactiveRow = sidebarSessionButtons[inactive.Id];
        var border = DesignFixture.Value.GetProperty("opacities").EnumerateArray().First(e => e.GetProperty("name").GetString() == "sidebarRowSelectedBorder");
        var borderWant = $"#{(int)Math.Round(border.GetProperty("opacity").GetDouble() * 255):X2}000000";
        RequireBrush(activeRow, e => ((Button)e).Background, DesignToken.Card, "the selected pane row", key: key);
        // The hairline is an overlay over the row's padding (M/WorkspaceView.swift:526), so it takes no room from the row.
        var (activeEdge, inactiveEdge) = (sidebarRowEdges[activeId], sidebarRowEdges[inactive.Id]);
        Require(ReferenceEquals(activeEdge.BorderBrush, brushes.RowSelectedBorder) && Describe(activeEdge.BorderBrush) == borderWant && activeEdge.BorderThickness == new Thickness(DesignMetrics.Stroke.Hairline)
            && activeEdge.CornerRadius == new CornerRadius(DesignMetrics.Radius.Row) && activeEdge.Margin == new Thickness(-8, -6, -9, -7) && !activeEdge.IsHitTestVisible && activeRow.BorderThickness == new Thickness(0),
            $"{key} ({theme}): the selected pane row's hairline must be an overlay over its padding, black at {border.GetProperty("opacity").GetDouble()} ({borderWant}), {DesignMetrics.Stroke.Hairline}pt at radius {DesignMetrics.Radius.Row}; got {Describe(activeEdge.BorderBrush)}, {activeEdge.BorderThickness}, margin {activeEdge.Margin}, the row's own border {activeRow.BorderThickness}");
        foreach (var (row, what) in new[] { (activeRow, "the selected pane row"), (inactiveRow, "a pane row") })
            Require(row.CornerRadius == new CornerRadius(DesignMetrics.Radius.Row) && row.Padding == new Thickness(8, 6, 9, 7),
                $"{key} ({theme}): {what} must have radius {DesignMetrics.Radius.Row} and padding l8 r9 t6 b7; got {row.CornerRadius}, {row.Padding}");
        // The group's 3pt spacing around rows set in l22 r2 v1 (M/WorkspaceView.swift:165, 253): 4 under the
        // workspace row, 5 between pane rows, 4 over the add row; the workspace row first, the add row last.
        var group = workspaces.Children.OfType<StackPanel>().Single(g => AutomationProperties.GetAutomationId(g) == "sidebar-workspace-" + workspaceId);
        Require(group.Spacing == 3 && group.Margin == new Thickness(0, 0, 0, 12) && group.Children is [Grid, StackPanel { Spacing: 5 } paneRows, Microsoft.UI.Xaml.Controls.Button] && paneRows.Margin == new Thickness(22, 1, 2, 1) && paneRows.Children.Contains(activeRow),
            $"{key} ({theme}): the selected workspace's group must be its row, its pane rows (5 apart, set in l22 r2 v1) and the add row, 3 apart with 12 under it; got spacing {group.Spacing}, margin {group.Margin}, {group.Children.Count} parts");
        root.UpdateLayout();
        // A row is its two lines in t6 b7: 6 + 17 + 15 + 7 (M/WorkspaceView.swift:230, 247, 286).
        Require(Math.Abs(inactiveRow.ActualHeight - 45) <= 0.6 && Math.Abs(activeRow.ActualHeight - 45) <= 0.6, $"{key} ({theme}): a pane row must be 45 high (6 + the 17 title line + the 15 meta line + 7); got {inactiveRow.ActualHeight:F1} and, selected, {activeRow.ActualHeight:F1}");
        // The rows whose height is their words' measure as the Mac's: the workspace row 34 (v10 around the 12pt line),
        // the add row 29 (v8 around the 11pt line), the section header's 10pt line 12 (M/WorkspaceView.swift:81-86, 176, 332).
        var (workspaceRow, addRow) = ((Grid)group.Children[0], (Microsoft.UI.Xaml.Controls.Button)group.Children[^1]);
        Require(Math.Abs(workspaceRow.ActualHeight - 34) <= 0.6 && Math.Abs(addRow.ActualHeight - 29) <= 0.6 && Math.Abs(sessionsHeaderRow.ActualHeight - 12) <= 0.6,
            $"{key} ({theme}): the workspace row must be 34 high, the add row 29 and the section header's line 12, as on the Mac; got {workspaceRow.ActualHeight:F1}, {addRow.ActualHeight:F1}, {sessionsHeaderRow.ActualHeight:F1}");
        var rowLeft = activeRow.TransformToVisual(sidebarSurface).TransformPoint(new Windows.Foundation.Point()).X;
        Require(Math.Abs(rowLeft - 31) <= 0.5, $"{key} ({theme}): a pane row must start 31 from the sidebar's edge (the list's 9 and the row's 22); got {rowLeft:F1}");
        RequireClear(inactiveRow.Background, "a pane row at rest", key);
        RequireClear(inactiveEdge.BorderBrush, "a pane row's hairline at rest", key);
        // A status change re-renders the sidebar with new row buttons, so each hover check reads the
        // row shown at that moment rather than the one looked up above.
        async Task<Button> ShownRow(string id)
        {
            await WaitUI(() => sidebarSessionButtons.TryGetValue(id, out var row) && row.IsLoaded, () => $"{key} ({theme}): the sidebar row for {id} never loaded");
            return sidebarSessionButtons[id];
        }
        RequireSubtle(await StateBackground(await ShownRow(inactive.Id), "PointerOver", brushes.Subtle, key), "a pane row under the pointer", key);
        var selectedHover = await StateBackground(await ShownRow(activeId), "PointerOver", brushes.Brush(DesignToken.Card), key);
        RequireBrush(activeRow, _ => selectedHover, DesignToken.Card, "the selected pane row under the pointer", key: key);
        foreach (var session in rows.Where(s => sidebarTitles.ContainsKey(s.Id)))
        {
            var title = sidebarTitles[session.Id];
            var shown = StatusGlyph.DisplayStatus(session.Status, PendingRequests(session.Id));
            RequireFont(title, DesignMetrics.Type.SideRow, SidebarTitleWeight(shown), $"({theme}) the pane title of {session.Id} ({shown})", key);
            RequireBrush(title, e => ((TextBlock)e).Foreground, DesignToken.Ink, $"the pane title of {session.Id}", key: key);
        }
        Require(SidebarTitleWeight("completed").Weight == Microsoft.UI.Text.FontWeights.Medium.Weight && SidebarTitleWeight("running").Weight == Microsoft.UI.Text.FontWeights.SemiBold.Weight,
            $"{key}: a finished pane's title must be medium and a running one's semibold");
        Require(sessionIndicators[activeId].Mark.View is FrameworkElement { Width: StatusGlyph.RowSize } markView && markView.Margin.Top == 1.5,
            $"{key} ({theme}): the pane row glyph must be {StatusGlyph.RowSize} wide, 1.5 from the top; got {sessionIndicators[activeId].Mark.View.Width}, {sessionIndicators[activeId].Mark.View.Margin}");
        Require(sidebarDetails.ContainsKey(activeId), $"{key} ({theme}): the active smoke pane {activeId} has no meta line");
        RequireBrush(sidebarDetails[activeId], e => ((TextBlock)e).Foreground, DesignToken.Ink2, "the selected row's meta line", key: key);
        RequireBrush(sidebarDetails[inactive.Id], e => ((TextBlock)e).Foreground, DesignToken.SidebarInk2, "a row's meta line", key: key);
        Require(sidebarDetails[inactive.Id].FontSize == DesignMetrics.Type.Pill, $"{key} ({theme}): the meta line must be {DesignMetrics.Type.Pill}pt; got {sidebarDetails[inactive.Id].FontSize}pt");

        // A pane that is not an agent's names its kind on the meta line (macOS DashboardText.kindLine).
        var kindLine = SidebarKindLine(new RunSession { Id = "smoke-sidebar-kind", WorkspaceId = workspaceId, Kind = "shell" }, false);
        sidebarKindLines.Remove("smoke-sidebar-kind");
        var kindWant = Locale.Get("dashboard.kind.shell") + " · " + Locale.Get("phone.card.localTerminal");
        Require(kindLine.Text == kindWant && kindLine.FontSize == DesignMetrics.Type.Pill, $"{key} ({theme}): a shell row's meta line must be '{kindWant}' at {DesignMetrics.Type.Pill}pt; got '{kindLine.Text}' at {kindLine.FontSize}pt");
        RequireBrush(kindLine, e => ((TextBlock)e).Foreground, DesignToken.SidebarInk2, "a shell row's kind line", key: key);
        foreach (var (id, line) in sidebarKindLines)
            RequireBrush(line, e => ((TextBlock)e).Foreground, id == activeId ? DesignToken.Ink2 : DesignToken.SidebarInk2, $"the kind line of {id}", key: key);

        // Beta capsules (M/BetaBadge.swift): a beta row's and the footer's.
        var betaId = sidebarBetas.FirstOrDefault() ?? throw new InvalidOperationException($"{key} ({theme}): no sidebar row carries the beta capsule");
        var rowBeta = (Border)((Grid)((Grid)sidebarSessionButtons[betaId].Content).Children[1]).Children[1];
        foreach (var (badge, what) in new[] { (rowBeta, "a beta row's capsule"), (sidebarFooterBeta!, "the footer's beta capsule") })
        {
            RequireBrush(badge, e => ((Border)e).Background, DesignToken.StopSoft, what, key: key);
            RequireBrush((TextBlock)badge.Child, e => ((TextBlock)e).Foreground, DesignToken.StopText, what + " text", key: key);
            RequireFont((TextBlock)badge.Child, DesignMetrics.Type.Badge, Microsoft.UI.Text.FontWeights.Medium, $"({theme}) {what} text", key);
            Require(badge.Padding == new Thickness(5, 1, 5, 1) && badge.BorderThickness == new Thickness(0), $"{key} ({theme}): {what} must be padding h5 v1 with no outline; got {badge.Padding}, {badge.BorderThickness}");
        }

        // The add row (M/WorkspaceView.swift:322-341).
        var add = workspaces.Children.OfType<StackPanel>().SelectMany(g => g.Children.OfType<Button>()).Single(b => AutomationProperties.GetAutomationId(b) == "workspace-add-session-" + workspaceId);
        var addLabel = (StackPanel)add.Content;
        Require(add.Padding == new Thickness(26, 8, 12, 8) && addLabel.Children[0] is FontIcon { FontSize: 10 } && addLabel.Children[1] is TextBlock { FontSize: DesignMetrics.Type.Pill },
            $"{key} ({theme}): the add row must be a 10pt plus and 11pt words, padding l26 r12 v8; got padding {add.Padding}");
        RequireBrush((FontIcon)addLabel.Children[0], e => ((FontIcon)e).Foreground, DesignToken.SidebarAccent, "the add row's plus", key: key);
        RequireBrush((TextBlock)addLabel.Children[1], e => ((TextBlock)e).Foreground, DesignToken.SidebarAccent, "the add row's words", key: key);

        // The waiting words and the counts: a pending request, a running pane and a failed one.
        var others = rows.Where(s => s.Id != inactive.Id && WorkDashboard.IsCounted(s.Kind)).OrderBy(s => s.Id == activeId).Take(2).ToList();
        Require(others.Count == 2, $"{key} ({theme}): the counts check needs three counted panes in the workspace; got {others.Count + 1}");
        var originals = rows.ToDictionary(s => s.Id, s => s.Status);
        var request = new ToolPermissionRequest("smoke-sidebar-wait", inactive.Id, "smoke-sidebar-tuid", "Bash", "{\"command\":\"ls\"}", "List files");
        views[inactive.Id].ReceiveToolPermission(request);
        try
        {
            await service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == others[0].Id ? p with { Status = "running" } : p.Id == others[1].Id ? p with { Status = "error" } : p).ToList() });
            RefreshRunningIndicators();
            var wait = sessionIndicators[inactive.Id].Status;
            var waitWant = Locale.Get("phone.card.permissions", new Dictionary<string, string> { ["count"] = "1" });
            Require(wait.Visibility == Visibility.Visible && wait.Text == waitWant, $"{key} ({theme}): a pane with a pending request must say '{waitWant}'; got '{wait.Text}' {wait.Visibility}");
            RequireFont(wait, 10.5, Microsoft.UI.Text.FontWeights.Bold, $"({theme}) the waiting words", key);
            RequireBrush(wait, e => ((TextBlock)e).Foreground, DesignToken.WaitText, "the waiting words", key: key);
            Require(sessionIndicators[others[1].Id].Status.Visibility == Visibility.Collapsed, $"{key} ({theme}): a pane with nothing pending must show no words on the right; got '{sessionIndicators[others[1].Id].Status.Text}'");
            RequireFont(sidebarTitles[inactive.Id], DesignMetrics.Type.SideRow, Microsoft.UI.Text.FontWeights.SemiBold, $"({theme}) a waiting pane's title", key);
            RequireCounts(workspaceStatusCounts[workspaceId], WorkDashboard.WorkspaceBadges(service.Snapshot.Sessions.Where(s => s.WorkspaceId == workspaceId), DashboardAttention), "the workspace counts", key, true);
            RequireCounts(dashboardCounts!, WorkDashboard.WorkspaceBadges(service.Snapshot.Sessions, DashboardAttention), "the work-status counts", key, true);
            // The header's long form counts the same panes and names each state ("? 1 waiting  ✳ 1 running  ! 1 error").
            RequireCounts(workspaceHeaderCounts!, WorkDashboard.WorkspaceBadges(service.Snapshot.Sessions.Where(s => s.WorkspaceId == workspaceId), DashboardAttention), "the workspace header's counts", key, true);
            Require(statusRunning.Text == Locale.Get("window.status.runningCount", new Dictionary<string, string> { ["count"] = "1" }), $"{key} ({theme}): the status bar must count the one running pane; got '{statusRunning.Text}'");
            // With an agent at work the status capsule is the waveform and the count of busy agents (M/AgentCompanionViews.swift:15-16).
            RefreshCompanionControls();
            var busyAgents = service.Snapshot.Sessions.Count(s => s.Kind == "claude" && CompanionStatus(s) is "running" or "waiting" or "starting" or "queued");
            Require(busyAgents > 0 && companionStatusBusy is { Visibility: Visibility.Visible } && companionStatusIdle is { Visibility: Visibility.Collapsed } && companionStatusNumber.Visibility == Visibility.Visible
                && companionStatusNumber.Text == busyAgents.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"{key} ({theme}): with {busyAgents} agents at work the status capsule must show the waveform and that count; got waveform {companionStatusBusy?.Visibility}, dots {companionStatusIdle?.Visibility}, '{companionStatusNumber.Text}' {companionStatusNumber.Visibility}");
            await CaptureShellSmoke("counts-" + theme);
            var entryName = AutomationProperties.GetName(dashboardButton!);
            var entryWant = string.Join(", ", dashboardCounts!.Entries.Where(e => e.Entry.Visibility == Visibility.Visible).Select(e => AutomationProperties.GetName(e.Entry)).Prepend(Locale.Get("phone.dashboard.title")));
            Require(entryName == entryWant, $"{key} ({theme}): the work-status entry must read its title and counts, '{entryWant}'; got '{entryName}'");
        }
        finally
        {
            views[inactive.Id].ReceiveToolPermission(request with { State = "answered" });
            await service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => originals.TryGetValue(p.Id, out var status) ? p with { Status = status } : p).ToList() });
            RefreshRunningIndicators(); RefreshCompanionControls();
        }
        Require(sessionIndicators[inactive.Id].Status.Visibility == Visibility.Collapsed, $"{key} ({theme}): the waiting words stayed after the request was answered: '{sessionIndicators[inactive.Id].Status.Text}'");
        RequireCounts(workspaceStatusCounts[workspaceId], WorkDashboard.WorkspaceBadges(service.Snapshot.Sessions.Where(s => s.WorkspaceId == workspaceId), DashboardAttention), "the workspace counts after the restore", key, false);

        // The layout picker (decision Q3) and the footer (M/WorkspaceView.swift:107-118).
        RequireSubtle(layout.Background, "the layout picker", key);
        Require(layout.CornerRadius == new CornerRadius(DesignMetrics.Radius.Search) && layout.BorderThickness == new Thickness(0) && layout.FontSize == 12,
            $"{key} ({theme}): the layout picker must be 12pt at radius {DesignMetrics.Radius.Search} with no border; got {layout.FontSize}pt, {layout.CornerRadius}, {layout.BorderThickness}");
        RequireBrush(layout, e => ((Control)e).Resources.TryGetValue("ComboBoxBackgroundBorderBrushFocused", out var ring) ? ring as Brush : null, DesignToken.Accent, "the layout picker's keyboard-focus ring", key: key);
        var footer = sidebarFooter!;
        RequireBrush(footer, e => ((Grid)e).BorderBrush, DesignToken.Ink, "the footer divider (M/WorkspaceView.swift:108, a Divider)", SeparatorOpacity, key);
        Require(footer.BorderThickness == new Thickness(0, DesignMetrics.Stroke.Line, 0, 0) && footer.Padding == new Thickness(16),
            $"{key} ({theme}): the footer must have a top Stroke.Line {DesignMetrics.Stroke.Line} and padding 16; got {footer.BorderThickness}, {footer.Padding}");
        var glyph = (sidebarThemeButton?.Content as FontIcon)?.Glyph; var glyphWant = theme == "light" ? SidebarMoonGlyph : SidebarSunGlyph;
        Require(glyph == glyphWant, $"{key} ({theme}): the theme button must show the {(theme == "light" ? "moon" : "sun")} U+{(int)glyphWant[0]:X4}; got {(glyph is { Length: > 0 } g ? $"U+{(int)g[0]:X4}" : "none")}");
        // The name and current version determine the row height; the footer must fit both without clipping.
        root.UpdateLayout();
        var brandHeight = ((FrameworkElement)footer.Children[0]).ActualHeight;
        Require(brandHeight >= 20 && Math.Abs(footer.ActualHeight - (DesignMetrics.Stroke.Line + 32 + brandHeight)) < 0.5, $"{key} ({theme}): the footer must fit the brand and version inside padding 16; got footer {footer.ActualHeight:F1}, brand {brandHeight:F1}");
        Require(settingsButton.Content is FontIcon { FontSize: SidebarFooterGlyph } && settingsButton.Width == SidebarFooterButton && sidebarThemeButton!.Width == SidebarFooterButton,
            $"{key} ({theme}): the footer's symbols must be {SidebarFooterGlyph}pt in {SidebarFooterButton}pt hit areas; got {settingsButton.Width} and {sidebarThemeButton!.Width}");
        // The Mac's gear stands with its centre 24 from the sidebar's dividing line and the theme symbol's 24.5 before it
        // (docs/design-system/screens/01-main-mighty-diagram-light.webp); layout rounding may move either by under a pixel.
        double Centre(FrameworkElement button) => button.TransformToVisual(footer).TransformPoint(new Windows.Foundation.Point(button.ActualWidth / 2, 0)).X;
        var (gearCentre, themeCentre) = (Centre(settingsButton), Centre(sidebarThemeButton));
        Require(Math.Abs(footer.ActualWidth - gearCentre - (16 + SidebarFooterGearCentre)) <= 0.75 && Math.Abs(gearCentre - themeCentre - SidebarFooterPitch) <= 0.75,
            $"{key} ({theme}): the gear's centre must be {16 + SidebarFooterGearCentre} from the footer's trailing edge and the theme symbol's {SidebarFooterPitch} before it, as the Mac's; got {footer.ActualWidth - gearCentre:F1} and {gearCentre - themeCentre:F1}");
        await CaptureShellStates(theme);
    }

    /// <summary>
    /// Counts in the Mac's order — wait, run, err, and in the long form done, stop, idle
    /// (M/WorkspaceView.swift:459-502) — each a 12pt glyph of its tone and an 11pt bold <c>ink</c> count
    /// equal to the badges, 3 apart and 9 between, a zero hidden; the long form names each state in 11pt
    /// medium <c>ink2</c>, 1 further from the count. <paramref name="allThree"/> also requires wait, run
    /// and err all to show.
    /// </summary>
    private void RequireCounts(StatusCountsView counts, WorkDashboard.Badges badges, string what, string key, bool allThree)
    {
        var theme = SmokeTheme;
        var shown = counts.Entries.Where(e => e.Entry.Visibility == Visibility.Visible).Select(e => e.Tone).ToList();
        if (allThree) Require(shown.Take(3).SequenceEqual([DesignTone.Wait, DesignTone.Run, DesignTone.Err]), $"{key} ({theme}): {what} must show wait, run, err in that order; got {string.Join(", ", shown)}");
        Require(counts.View.Spacing == 9 && counts.View.Children.Select(child => child as StackPanel).SequenceEqual(counts.Entries.Select(e => e.Entry)),
            $"{key} ({theme}): {what} must lay its entries out in the Mac's order, 9 apart; got spacing {counts.View.Spacing}");
        foreach (var (tone, mark, count, entry, word) in counts.Entries)
        {
            var (a, b) = StatusCountsView.Counted(badges, tone); var value = a + b;
            Require((entry.Visibility == Visibility.Visible) == (value > 0), $"{key} ({theme}): {what}' {tone} entry must be {(value > 0 ? "shown" : "hidden")} for {value}; got {entry.Visibility}");
            if (value == 0) continue;
            var glyph = StatusGlyph.Kind(tone);
            Require(count.Text == value.ToString(System.Globalization.CultureInfo.InvariantCulture) && mark.Glyph == glyph && mark.View.Width == 12 && entry.Spacing == 3,
                $"{key} ({theme}): {what}' {tone} entry must be a 12pt {glyph} and {value}, 3 apart; got {mark.View.Width}pt {mark.Glyph} and '{count.Text}', spacing {entry.Spacing}");
            RequireFont(count, DesignMetrics.Type.Pill, Microsoft.UI.Text.FontWeights.Bold, $"({theme}) {what}' {tone} count", key);
            RequireBrush(count, e => ((TextBlock)e).Foreground, DesignToken.Ink, $"{what}' {tone} count", key: key);
            if (word is null) continue;
            var name = Locale.Get(StatusGlyph.WordKey(tone));
            Require(word.Text == name && word.Margin == MacLine(left: 1) && ReferenceEquals(entry.Children[^1], word), $"{key} ({theme}): {what}' {tone} entry must end with its state's name '{name}', 1 further from the count (on the Mac's line: {MacLine(left: 1)}); got '{word.Text}', {word.Margin}");
            RequireFont(word, DesignMetrics.Type.Pill, Microsoft.UI.Text.FontWeights.Medium, $"({theme}) {what}' {tone} word", key);
            RequireBrush(word, e => ((TextBlock)e).Foreground, DesignToken.Ink2, $"{what}' {tone} word", key: key);
        }
    }

    /// <summary>The system title bar carries the theme's sidebar behind ink (decision Q2), read back from AppWindow.TitleBar.</summary>
    private void RequireTitleBar(string key)
    {
        var theme = SmokeTheme; var bar = AppWindow.TitleBar;
        Require(Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported(),
            $"{key} ({theme}): AppWindowTitleBar.IsCustomizationSupported() is false, so the title bar cannot take token Sidebar {FixtureHex(theme, DesignToken.Sidebar)}");
        foreach (var (name, value, token) in new (string, Windows.UI.Color?, DesignToken)[]
        {
            ("BackgroundColor", bar.BackgroundColor, DesignToken.Sidebar), ("InactiveBackgroundColor", bar.InactiveBackgroundColor, DesignToken.Sidebar),
            ("ButtonBackgroundColor", bar.ButtonBackgroundColor, DesignToken.Sidebar), ("ForegroundColor", bar.ForegroundColor, DesignToken.Ink),
            ("ButtonForegroundColor", bar.ButtonForegroundColor, DesignToken.Ink),
        })
        {
            var expected = FixtureHex(theme, token);
            var got = value is { } c ? $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}" : "unset";
            Require(got == "#FF" + expected[1..], $"{key} ({theme}): AppWindow.TitleBar.{name} must be token {token} {expected}; got {got}");
        }
    }
    private const string PaneChromeKey = "paneChromeDesign";

    /// <summary>A derived value of the fixture for the current theme (<c>derived.{theme}.{name}</c>).</summary>
    private string DerivedHex(string name) => DesignFixture.Value.GetProperty("derived").GetProperty(SmokeTheme).GetProperty(name).GetString()!;

    /// <summary>A tone's text ink in the current theme (<c>derived.{theme}.tones.{tone}.text</c>).</summary>
    private string ToneTextHex(DesignTone tone) =>
        DesignFixture.Value.GetProperty("derived").GetProperty(SmokeTheme).GetProperty("tones").GetProperty(tone.ToString().ToLowerInvariant()).GetProperty("text").GetString()!;

    /// <summary>A brush is an opaque fixture hex that is not a palette token (a derived value), named for the message.</summary>
    private void RequireHex(Brush? actual, string expected, string name, string what, string key)
    {
        var want = "#FF" + expected[1..].ToUpperInvariant();
        Require(Describe(actual) == want, $"{key} ({SmokeTheme}): {what} must be {name} {expected}; got {Describe(actual)}");
    }

    private sealed partial class PaneView
    {
        /// <summary>The brushes the pane's long-lived chrome holds; a theme toggle must recolour these instances, never replace them.</summary>
        internal Brush?[] PaneChromeBrushes() =>
            [paneHeader?.Background, paneHeader?.BorderBrush, headerTitle.Foreground, label.Foreground, elapsed.Foreground, modeSwitch?.Background, modeDefaultChip?.Fill, composerCard.Background, composerShadow.Fill];

        /// <summary>
        /// Stage 4 in the theme just rendered, on this (reused) pane: the tab strip and the pane's tab
        /// (M/PaneDockView.swift:194-262), the Layout.PaneHeader header with its title, state word in each tone's
        /// ink and figures (M/SessionPaneView.swift:208-251), the … menu holding Copy (decision Q4), the
        /// Default | Mighty switch (segmentTrack, segmentOn, shadow 0.12), the composer card with its
        /// focus ring and shadow (M/SessionPaneView.swift:654-656), the pills, the send / stop shape, the
        /// context ring, the status-line toggle and the permission card. Every colour is read off the tree.
        /// </summary>
        internal async Task RequirePaneChromeInTheme()
        {
            const string key = PaneChromeKey; var o = owner; var theme = o.SmokeTheme; var b = o.brushes;
            o.root.UpdateLayout();
            var pane = Session;

            // The tab strip of the group holding this pane, and its tabs: sizes are read as laid out, and
            // every tab button must fit inside its cell and the cell inside the strip (nothing clipped).
            var group = PaneLayout.Groups(o.EffectiveLayout(o.service.Snapshot, pane.WorkspaceId)!).First(g => g.SessionIds.Contains(id));
            Require(o.tabStrips.TryGetValue(group.Id, out var stripParts), $"{key} ({theme}): the tab group {group.Id} holding pane {id} has no tab strip");
            var (strip, rule) = stripParts;
            Require(strip.Height == DesignMetrics.Layout.TabStrip && Math.Abs(strip.ActualHeight - DesignMetrics.Layout.TabStrip) < .5,
                $"{key} ({theme}): the tab strip must be Layout.TabStrip {DesignMetrics.Layout.TabStrip} tall; got {strip.Height} (actual {strip.ActualHeight:F1})");
            o.RequireSubtle(strip.Background, "the tab strip", key);
            Require(strip.CornerRadius == new CornerRadius(DesignMetrics.Radius.Pane, DesignMetrics.Radius.Pane, 0, 0),
                $"{key} ({theme}): the tab strip must have the card's top corners (Radius.Pane {DesignMetrics.Radius.Pane}); got {strip.CornerRadius}");
            var ruleTop = rule.TransformToVisual(strip).TransformPoint(new Windows.Foundation.Point()).Y;
            Require(Math.Abs(rule.ActualHeight - DesignMetrics.Stroke.Line) < .01 && Math.Abs(ruleTop + rule.ActualHeight - strip.ActualHeight) < .5,
                $"{key} ({theme}): the strip's rule must be a Stroke.Line {DesignMetrics.Stroke.Line} overlay on its bottom edge; got {rule.ActualHeight:F2} tall at y {ruleTop:F1} of {strip.ActualHeight:F1}");
            o.RequireBrush(rule, e => ((Border)e).Background, DesignToken.Accent, "the rule under the active group's tab strip (tabActiveRule)", DesignMetrics.Opacity.TabActiveRule, key);
            // The strip holds its tabs and nothing else (M/PaneDockView.swift:195-216 has no add button), and scrolls without a scroll bar.
            var strays = VisualChildren(strip).OfType<Button>().Where(button => !o.tabCells.Values.Any(parts => ReferenceEquals(parts.Tab, button) || ReferenceEquals(parts.Close, button))).Select(AutomationProperties.GetName).ToList();
            Require(strays.Count == 0, $"{key} ({theme}): the tab strip must hold only its tabs' buttons; also found {string.Join(", ", strays)}");
            var stripScroll = strip.Children.OfType<ScrollViewer>().Single();
            Require(stripScroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Hidden && stripScroll.Content is StackPanel { Spacing: 3 } tabRowPanel && tabRowPanel.Padding == new Thickness(5, TabStripPadding, 5, TabStripPadding),
                $"{key} ({theme}): the tab strip must scroll sideways with no scroll bar, its tabs 3 apart in padding h5 v{TabStripPadding}; got {stripScroll.HorizontalScrollBarVisibility}, {(stripScroll.Content as StackPanel)?.Spacing}, {(stripScroll.Content as StackPanel)?.Padding}");
            bool Inside(FrameworkElement inner, FrameworkElement outer, out string where)
            {
                var top = inner.TransformToVisual(outer).TransformPoint(new Windows.Foundation.Point());
                where = $"{inner.ActualWidth:F1}x{inner.ActualHeight:F1} at ({top.X:F1}, {top.Y:F1}) in {outer.ActualWidth:F1}x{outer.ActualHeight:F1}";
                return inner.ActualHeight > 0 && top.Y >= -.5 && top.Y + inner.ActualHeight <= outer.ActualHeight + .5;
            }
            foreach (var sessionId in group.SessionIds)
            {
                var parts = o.tabCells[sessionId];
                Require(Math.Abs(parts.Cell.ActualHeight - DesignMetrics.Layout.Tab) < .5 && Math.Abs(parts.Tab.ActualHeight - DesignMetrics.Layout.Tab) < .5 && Math.Abs(parts.Close.ActualHeight - DesignMetrics.Layout.Tab) < .5,
                    $"{key} ({theme}): tab {sessionId} must lay out Layout.Tab {DesignMetrics.Layout.Tab} tall with its tab and close buttons as tall; got cell {parts.Cell.ActualHeight:F1}, tab {parts.Tab.ActualHeight:F1}, close {parts.Close.ActualHeight:F1}");
                foreach (var (inner, outer, what) in new (FrameworkElement, FrameworkElement, string)[] { (parts.Tab, parts.Cell, "tab button in its cell"), (parts.Close, parts.Cell, "close button in its cell"), (parts.Cell, strip, "tab cell in the strip") })
                    Require(Inside(inner, outer, out var where), $"{key} ({theme}): the {what} of {sessionId} is clipped: {where}");
            }
            var (cell, shape, tab, close, title) = o.tabCells[id];
            o.RequireBrush(shape, e => ((Border)e).Background, DesignToken.Card, "the selected tab", key: key);
            o.RequireBrush(shape, e => ((Border)e).BorderBrush, DesignToken.Line, "the selected tab's border", key: key);
            Require(shape.CornerRadius == new CornerRadius(DesignMetrics.Radius.Segment) && shape.BorderThickness == new Thickness(DesignMetrics.Stroke.Line) && Math.Abs(shape.ActualHeight - cell.ActualHeight) < .5
                && tab.MinWidth == 56 && tab.Padding == new Thickness(10, 0, 6, 0),
                $"{key} ({theme}): the selected tab must be a radius {DesignMetrics.Radius.Segment} shape with a Stroke.Line border filling its cell, at least 56 wide, padding l10 r6; got {shape.CornerRadius}, {shape.BorderThickness}, {shape.ActualHeight:F1}/{cell.ActualHeight:F1}, {tab.MinWidth}, {tab.Padding}");
            RequireFont(title, DesignMetrics.Type.Pill, Microsoft.UI.Text.FontWeights.SemiBold, $"({theme}) the selected tab's title", key);
            Require(title.MaxWidth == 125, $"{key} ({theme}): a tab title must be at most 125 wide; got {title.MaxWidth}");
            o.RequireBrush(title, e => ((TextBlock)e).Foreground, DesignToken.Ink, "the selected tab's title", key: key);
            var closeRow = cell.Children.OfType<StackPanel>().Single();
            Require(close.Width == 20 && close.Height == DesignMetrics.Layout.Tab && close.Content is FontIcon && closeRow.Spacing == 0 && closeRow.Padding == new Thickness(0, 0, 2, 0) && ReferenceEquals(closeRow.Children[closeRow.Children.Count - 1], close),
                $"{key} ({theme}): a tab must end with its close, an x in a 20x{DesignMetrics.Layout.Tab} box with 2 after it (M/PaneDockView.swift); got {close.Width}x{close.Height}, row padding {closeRow.Padding}");
            o.RequireBrush(close, e => ((Control)e).Foreground, DesignToken.Ink2, "a tab's close", key: key);
            // The Mac's tab order puts the 12pt mark last; it shows for a counted pane that is not idle.
            var tabRow = (StackPanel)tab.Content; var tabMark = o.tabIndicators[id];
            // An agent's tab leads with its mark in the Mac's frame, 1.15 times the 10pt size (M/ProviderIcon.swift:18), 6 before the title.
            Require(tabRow.Spacing == 6 && tabRow.Children[0] is Microsoft.UI.Xaml.Shapes.Path { Width: 11.5, Height: 11.5 },
                $"{key} ({theme}): an agent tab must lead with its 11.5pt mark, 6 before the title; got spacing {tabRow.Spacing} and {tabRow.Children[0].GetType().Name} {(tabRow.Children[0] as FrameworkElement)?.Width}");
            var markShown = WorkDashboard.IsCounted(pane.Kind) && StatusGlyph.Tone(StatusGlyph.DisplayStatus(pane.Status, PendingRequests)) != DesignTone.Idle;
            Require(ReferenceEquals(tabRow.Children[tabRow.Children.Count - 1], tabMark.View) && tabMark.View.Width == StatusGlyph.TabSize
                && tabMark.View.Visibility == (markShown ? Visibility.Visible : Visibility.Collapsed),
                $"{key} ({theme}): the tab's {StatusGlyph.TabSize}pt mark must come last and show only for a counted pane that is not idle ({pane.Kind}, {pane.Status}); got width {tabMark.View.Width}, {tabMark.View.Visibility}");
            if (group.SessionIds.FirstOrDefault(s => s != id) is { } otherId && o.tabCells.TryGetValue(otherId, out var other))
            {
                o.RequireClear(other.Shape.Background, "an unselected tab", key);
                o.RequireClear(other.Shape.BorderBrush, "an unselected tab's border", key);
                RequireFont(other.Title, DesignMetrics.Type.Pill, Microsoft.UI.Text.FontWeights.Normal, $"({theme}) an unselected tab's title", key);
                o.RequireBrush(other.Title, e => ((TextBlock)e).Foreground, DesignToken.Ink2, "an unselected tab's title", key: key);
            }

            // The Layout.PaneHeader header line.
            var header = paneHeader ?? throw new InvalidOperationException($"{key} ({theme}): the pane has no header");
            Require(header.Height == DesignMetrics.Layout.PaneHeader && Math.Abs(header.ActualHeight - DesignMetrics.Layout.PaneHeader) < .5 && header.Padding == new Thickness(14, 0, 10, 0),
                $"{key} ({theme}): the pane header must be Layout.PaneHeader {DesignMetrics.Layout.PaneHeader} tall with padding l14 r10; got {header.Height} (actual {header.ActualHeight:F1}), {header.Padding}");
            Require(Math.Abs(header.ActualWidth - (Container.ActualWidth - 2 * DesignMetrics.Stroke.Line)) < 1,
                $"{key} ({theme}): the pane header must run edge to edge inside the card ({Container.ActualWidth - 2 * DesignMetrics.Stroke.Line:F1}); got {header.ActualWidth:F1}");
            o.RequireBrush(header, e => ((Grid)e).Background, DesignToken.Card, "the pane header", key: key);
            o.RequireBrush(header, e => ((Grid)e).BorderBrush, DesignToken.Line, "the line under the pane header", key: key);
            Require(header.BorderThickness == new Thickness(0, 0, 0, DesignMetrics.Stroke.Line), $"{key} ({theme}): the line under the header must be a bottom Stroke.Line only; got {header.BorderThickness}");
            Require(headerMark.View.Width == StatusGlyph.HeaderSize && StatusGlyph.HeaderSize == 14, $"{key} ({theme}): the header glyph must be 14; got {headerMark.View.Width}");
            RequireFont(headerTitle, DesignMetrics.Type.Title, Microsoft.UI.Text.FontWeights.Bold, $"({theme}) the header title", key);
            Require(headerTitle.Text == pane.Title, $"{key} ({theme}): the header title must be the pane's title '{pane.Title}'; got '{headerTitle.Text}'");
            o.RequireBrush(headerTitle, e => ((TextBlock)e).Foreground, DesignToken.Ink, "the header title", key: key);
            RequireFont(label, DesignMetrics.Type.State, Microsoft.UI.Text.FontWeights.SemiBold, $"({theme}) the header state word", key);
            foreach (var figures in new[] { elapsed, figuresRest })
            {
                Require(figures.FontFamily?.Source == DesignMetrics.Font.Mono && figures.FontSize == DesignMetrics.Type.Mono && figures.TextTrimming == TextTrimming.None,
                    $"{key} ({theme}): the header figures must be untrimmed Font.Mono at Type.Mono {DesignMetrics.Type.Mono}; got '{figures.FontFamily?.Source}' at {figures.FontSize}, {figures.TextTrimming}");
                o.RequireBrush(figures, e => ((TextBlock)e).Foreground, DesignToken.Ink2, "the header figures", key: key);
            }
            var dark = theme == "dark";
            // The state words below are the pane's own; a pending request would turn every one amber.
            Require(PendingRequests == 0, $"{key} ({theme}): the header colour check needs a pane with no pending requests; pane {id} has {PendingRequests}");
            try
            {
                foreach (var (status, tone) in new[] { ("running", DesignTone.Run), ("completed", DesignTone.Done), ("error", DesignTone.Err), ("stopped", DesignTone.Stop), ("idle", DesignTone.Idle) })
                {
                    RefreshHeaderStatus(pane with { Status = status }, dark);
                    o.RequireHex(label.Foreground, o.ToneTextHex(tone), $"derived.tones.{tone.ToString().ToLowerInvariant()}.text", $"the header state word while {status}", key);
                }
                // The figures (C/PaneHero.swift:21-30, M/PaneChrome.swift:61): the clock, the context, the cost and the tool
                // calls since the latest request, the last with its name; a figure the pane has no number for is left out.
                var now = DateTimeOffset.UtcNow;
                LogEntry Tool(string name, string kind) => new("smoke-" + name, "system", name, Wire.Now(), pane.Provider, new AgentActivity("smoke-" + name, pane.Provider, kind, "completed", name));
                var counted = pane with
                {
                    RunTiming = new(now.AddSeconds(-134), now, now),
                    SessionUsage = new() { Provider = pane.Provider, Source = "smoke.fixture", ContextUsedTokens = 41, ContextWindowTokens = 100, CostUSD = 0.38 },
                    Logs = [Tool("earlier", "command"), new("smoke-request", "user", "request", Wire.Now(), pane.Provider), Tool("turn", "turn"), .. Enumerable.Range(0, 12).Select(i => Tool("tool-" + i, "command"))],
                };
                RefreshElapsed(counted); RefreshHeaderStatus(counted, dark);
                var figuresWant = "02:14 · 41% · $0.38 · " + Locale.Get("pane.hero.figure", new Dictionary<string, string> { ["label"] = Locale.Get("phone.session.hero.tools"), ["value"] = "12" });
                Require(elapsed.Text + figuresRest.Text == figuresWant, $"{key} ({theme}): the header figures must read '{figuresWant}'; got '{elapsed.Text + figuresRest.Text}'");
                // Their tooltip names every figure, then the agent, and says under them what the clock counts (M/PaneChrome.swift:58-59, 71); the accessible name is its first line.
                string Named(string label, string value) => Locale.Get("pane.hero.figure", new Dictionary<string, string> { ["label"] = Locale.Get(label), ["value"] = value });
                var tipLead = string.Join(" · ", Named("phone.session.hero.elapsed", "02:14"), Named("phone.session.hero.context", "41%"), Named("phone.session.hero.cost", "$0.38"), Named("phone.session.hero.tools", "12"), ProviderCatalog.Name(pane.Provider));
                var tip = (ToolTipService.GetToolTip(figuresWords!) as ToolTip)?.Content as string ?? ""; var tipName = AutomationProperties.GetName(figuresHost!);
                Require(tip.StartsWith(tipLead, StringComparison.Ordinal) && tip == tipName + "\n" + Locale.Get("pane.hero.elapsed.finished"),
                    $"{key} ({theme}): the figures' tooltip must start '{tipLead}' and end with what the clock counts under its accessible name; got '{tip}' for the name '{tipName}'");
                var bare = pane with { RunTiming = null, SessionUsage = null, Logs = [] };
                RefreshElapsed(bare); RefreshHeaderStatus(bare, dark);
                Require((elapsed.Text + figuresRest.Text).Length == 0, $"{key} ({theme}): a pane with nothing to count must show no figures; got '{elapsed.Text + figuresRest.Text}'");
            }
            finally { RefreshElapsed(Session); RefreshHeaderStatus(Session, dark); }
            o.root.UpdateLayout();
            Require(HeaderFitsSmoke(false), $"{key} ({theme}): the header controls must sit inside its one {DesignMetrics.Layout.PaneHeader}pt line at width {header.ActualWidth:F1}");
            var wrongAtRest = HeaderGivesWaySmoke();
            Require(wrongAtRest is null, $"{key} ({theme}): the header at its own width ({header.ActualWidth:F1}): {wrongAtRest}");
            // The header gives way as the Mac's does (M/SessionPaneView.swift:215-239), here with a title too long for any
            // pane: at the dock's least pane width, at a third of the default window (three panes side by side) and at
            // the pane's own width. The figures go first, then the title trims; the word and the controls stay whole.
            var paneWidth = Container.Width; var paneAlignment = Container.HorizontalAlignment;
            try
            {
                // The pane itself takes the title for the while, so the one-second tick redraws the header with it too.
                await Change(p => p with { Title = string.Join(" ", Enumerable.Repeat("A pane title too long for any header to show whole.", 8)) }); RefreshHeaderStatus(Session, dark);
                foreach (var width in new[] { DesignMetrics.Layout.PaneMinWidth, 372, double.NaN })
                {
                    Container.Width = width; Container.HorizontalAlignment = double.IsNaN(width) ? paneAlignment : HorizontalAlignment.Left;
                    o.root.UpdateLayout(); await Task.Delay(60); o.root.UpdateLayout();
                    var at = double.IsNaN(width) ? "wide" : width.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Require(HeaderFitsSmoke(false), $"{key} ({theme}): at {at} ({header.ActualWidth:F1}) a header control left the one {DesignMetrics.Layout.PaneHeader}pt line or the switch lost its words");
                    var wrong = HeaderGivesWaySmoke();
                    Require(wrong is null, $"{key} ({theme}): the header at {at} ({header.ActualWidth:F1}): {wrong}");
                    var (line, figuresRoom) = (headerLine!, figuresHost!);
                    // A title is at most 120 characters, which a wide pane may still show whole; a narrow one never can.
                    if (!double.IsNaN(width))
                        Require(headerTitle.ActualWidth < line.Ideal(headerTitle) - 1.01 && headerTitle.IsTextTrimmed && figuresRoom.ActualWidth < .5 && Math.Abs(label.ActualWidth - line.Ideal(label)) < 1.01,
                            $"{key} ({theme}): at {at} a title too long must trim with an ellipsis after the figures gave way, before the state word; title {headerTitle.ActualWidth:F1} of {line.Ideal(headerTitle):F1} (trimmed {headerTitle.IsTextTrimmed}), figures {figuresRoom.ActualWidth:F1}, word {label.ActualWidth:F1} of {line.Ideal(label):F1}");
                    await CaptureElement(Container, Path.Combine(o.options.ProfileDirectory!, $"smoke-chrome-header-{at}-{theme}.png"));
                }
            }
            finally { Container.Width = paneWidth; Container.HorizontalAlignment = paneAlignment; await Change(p => p with { Title = pane.Title }); RefreshHeaderStatus(Session, dark); o.root.UpdateLayout(); }

            // The … menu is the Mac's (M/SessionPaneView.swift:281-295): rename, focus view, copy the run log (decision
            // Q4), start a new conversation, show the background work line, then close behind a separator. The header has no Copy button.
            var menuButton = paneMenuButton ?? throw new InvalidOperationException($"{key} ({theme}): the header has no pane menu button");
            Require(menuButton.Width == 22 && menuButton.Height == 24 && ReferenceEquals(paneHeaderControls.Children[paneHeaderControls.Children.Count - 1], menuButton),
                $"{key} ({theme}): the pane menu button must be the last, 22x24 header control; got {menuButton.Width}x{menuButton.Height}");
            o.RequireBrush(menuButton, e => ((Control)e).Foreground, DesignToken.Ink2, "the pane menu button", key: key);
            o.RequireSubtle(await StateBackground(menuButton, "PointerOver", b.Subtle, key), "the pane menu button under the pointer", key);
            var items = ((MenuFlyout)menuButton.Flyout).Items;
            var menuWant = new[] { Locale.Get("menu.rename"), Locale.Get("menu.focusPane"), Locale.Get("pane.menu.copyLog"), Locale.Get("pane.menu.newConversation"), Locale.Get("pane.menu.backgroundWork"), null, Locale.Get("menu.closePane") };
            var menuGot = items.Select(item => item is MenuFlyoutItem entry ? entry.Text : null).ToArray();
            Require(menuGot.SequenceEqual(menuWant) && AutomationProperties.GetAutomationId(items[2]) == "pane-menu-copy-" + id,
                $"{key} ({theme}): the pane menu must read {string.Join(" | ", menuWant.Select(text => text ?? "-"))}; got {string.Join(" | ", menuGot.Select(text => text ?? "-"))}");
            Require(!VisualChildren(header).OfType<Button>().Any(button => AutomationProperties.GetName(button) == Locale.Get("pane.copyButton")),
                $"{key} ({theme}): Copy moved into the pane menu (decision Q4), so the header must hold no '{Locale.Get("pane.copyButton")}' button");

            // The plugin and terminal buttons (M/SessionPaneView.swift:258-276): 22x24 in the header's ink2, after the
            // switch and before the menu; plugins show in the Mighty view of a Claude or Codex pane, the terminal once
            // the agent has one of its own.
            Require(pluginButton is { Width: 22, Height: 24 } && terminalButton is { Width: 22, Height: 24 }, $"{key} ({theme}): the header must hold a 22x24 plugin and terminal button");
            foreach (var (button, what) in new[] { (pluginButton!, "the plugin button"), (terminalButton!, "the terminal button") })
                o.RequireBrush(button, e => ((Control)e).Foreground, DesignToken.Ink2, what, key: key);
            var controlOrder = new UIElement?[] { modeSwitch, pluginButton, terminalButton, menuButton }.Select(control => paneHeaderControls.Children.IndexOf(control!)).ToArray();
            Require(controlOrder.All(index => index >= 0) && controlOrder.SequenceEqual(controlOrder.OrderBy(index => index)),
                $"{key} ({theme}): the header controls must stand switch, plugins, terminal, menu; got positions {string.Join(", ", controlOrder)}");
            Require((terminalButton!.Visibility == Visibility.Visible) == o.agentTerminals.ContainsKey(id), $"{key} ({theme}): the terminal button must show exactly while the agent has a terminal; got {terminalButton.Visibility}");
            try
            {
                foreach (var (mode, shown) in new[] { ("mighty", pane.Provider is "claude" or "codex"), ("default", false) })
                {
                    RefreshHeaderStatus(pane with { AgentViewMode = mode }, dark);
                    Require((pluginButton!.Visibility == Visibility.Visible) == shown, $"{key} ({theme}): the plugin button of a {pane.Provider} pane in the {mode} view must be {(shown ? "shown" : "hidden")}; got {pluginButton.Visibility}");
                }
            }
            finally { RefreshHeaderStatus(Session, dark); }

            // The Default | Mighty switch.
            Require(modeSwitch is not null && modeDefaultChip is not null && modeMightyChip is not null && modeDefaultButton is not null && modeMightyButton is not null,
                $"{key} ({theme}): the smoke pane shows no Default | Mighty switch");
            Require(ReferenceEquals(modeSwitch!.Background, b.SegmentTrack) && modeSwitch.CornerRadius == new CornerRadius(DesignMetrics.Radius.Row) && modeSwitch.Padding == new Thickness(2),
                $"{key} ({theme}): the switch track must be the shared segmentTrack brush, radius {DesignMetrics.Radius.Row}, padding 2; got {Describe(modeSwitch.Background)}, {modeSwitch.CornerRadius}, {modeSwitch.Padding}");
            o.RequireHex(modeSwitch.Background, o.DerivedHex("segmentTrack"), "derived.segmentTrack", "the switch track", key);
            var mighty = pane.AgentViewMode == "mighty";
            var (onChip, offChip, onButton, offButton) = mighty ? (modeMightyChip!, modeDefaultChip!, modeMightyButton!, modeDefaultButton!) : (modeDefaultChip!, modeMightyChip!, modeDefaultButton!, modeMightyButton!);
            Require(onChip.Visibility == Visibility.Visible && offChip.Visibility == Visibility.Collapsed, $"{key} ({theme}): only the chosen side ({(mighty ? "mighty" : "default")}) may show its chip; got {onChip.Visibility} / {offChip.Visibility}");
            Require(ReferenceEquals(onChip.Fill, b.SegmentOn), $"{key} ({theme}): the chip must be the shared segmentOn brush; got {Describe(onChip.Fill)}");
            o.RequireHex(onChip.Fill, o.DerivedHex("segmentOn"), "derived.segmentOn", "the chosen side's chip", key);
            var chipShadow = CardShadow.Of(onChip);
            Require(onChip.RadiusX == DesignMetrics.Radius.Segment && chipShadow is not null && Math.Abs(chipShadow.Opacity - CardShadow.SegmentChip) < .001 && Math.Abs(chipShadow.Offset.Y - 1) < .01,
                $"{key} ({theme}): the chip must be radius {DesignMetrics.Radius.Segment} with a {CardShadow.SegmentChip} shadow one point down; got radius {onChip.RadiusX}, shadow {(chipShadow is null ? "none" : $"{chipShadow.Opacity} at y {chipShadow.Offset.Y}")}");
            // The shadow is drawn over its caster, so the chip's own face lies on top of both: the chip itself is segmentOn, not segmentOn under 0.12 of black.
            var chipCell = VisualTreeHelper.GetParent(onChip) as Grid;
            Require(chipCell is { Children.Count: 3 } && ReferenceEquals(chipCell.Children[0], onChip) && chipCell.Children[1] is Microsoft.UI.Xaml.Shapes.Rectangle { Visibility: Visibility.Visible } chipFace
                && ReferenceEquals(chipFace.Fill, b.SegmentOn) && chipFace.RadiusX == DesignMetrics.Radius.Segment && ReferenceEquals(chipCell.Children[2], onButton),
                $"{key} ({theme}): the chosen side must be its caster, then a segmentOn face of radius {DesignMetrics.Radius.Segment} over the caster's shadow, then the button; got {chipCell?.Children.Count.ToString() ?? "no"} parts");
            foreach (var (button, ink, what) in new[] { (onButton, DesignToken.Ink, "the chosen side"), (offButton, DesignToken.Ink2, "the other side") })
            {
                Require(button.Height == 20 && button.CornerRadius == new CornerRadius(DesignMetrics.Radius.Segment) && button.Padding == new Thickness(8, 0, 8, 0),
                    $"{key} ({theme}): {what} of the switch must be 20 tall, radius {DesignMetrics.Radius.Segment}, padding h8; got {button.Height}, {button.CornerRadius}, {button.Padding}");
                var words = ((Panel)button.Content).Children.OfType<TextBlock>().Single();
                RequireFont(words, DesignMetrics.Type.Pill, Microsoft.UI.Text.FontWeights.SemiBold, $"({theme}) {what}'s words", key);
                o.RequireBrush(words, e => ((TextBlock)e).Foreground, ink, $"{what}'s words", key: key);
                // The symbol stands in the Mac Label's 15x12 slot, 8 before the word and in its ink; the word shows at every width.
                var option = (StackPanel)button.Content;
                Require(option.Spacing == 8 && option.Children[0] is Microsoft.UI.Xaml.Shapes.Path { Width: 15, Height: 12 } symbol && ReferenceEquals(symbol.Fill, words.Foreground) && words.Visibility == Visibility.Visible,
                    $"{key} ({theme}): {what} of the switch must be a drawn symbol in a 15x12 slot, 8 before its word and in the word's ink; got spacing {option.Spacing}, {option.Children[0].GetType().Name}");
            }

            // The composer card: card, r16, shadow 0.05; its edge is an overlay — line at 1pt, accent x 0.8 at 1.5pt while the
            // editor has focus — so the thicker ring moves nothing (M/SessionPaneView.swift:654-655).
            var card = composerCard; var edge = composerRing;
            RequireRadius(card, DesignMetrics.Radius.Composer, "the composer card (Radius.Composer)", key);
            RequireRadius(edge, DesignMetrics.Radius.Composer, "the composer card's edge (Radius.Composer)", key);
            o.RequireBrush(card, e => ((Border)e).Background, DesignToken.Card, "the composer card", key: key);
            Require(card.BorderThickness == new Thickness(0) && card.Padding == new Thickness(0) && !edge.IsHitTestVisible && edge.Background is null,
                $"{key} ({theme}): the composer's edge must be an overlay that takes no input and no room (the card itself has no border or padding); got card border {card.BorderThickness}, padding {card.Padding}");
            var cardShadow = CardShadow.Of(composerShadow);
            Require(cardShadow is not null && Math.Abs(cardShadow.Opacity - CardShadow.Composer) < .001 && Math.Abs(cardShadow.Offset.Y - 1) < .01 && composerShadow.RadiusX == DesignMetrics.Radius.Composer,
                $"{key} ({theme}): the composer card must cast a {CardShadow.Composer} shadow one point down; got {(cardShadow is null ? "none" : $"{cardShadow.Opacity} at y {cardShadow.Offset.Y}")}");
            // The card stands 12 from the pane's edges on every side and its parts 9 apart (M/SessionPaneView.swift:519, 660).
            var cardAt = card.TransformToVisual(Container).TransformPoint(new Windows.Foundation.Point());
            var inset = DesignMetrics.Stroke.Line + ComposerMargin;
            Require(Math.Abs(cardAt.X - inset) < .6 && Math.Abs(Container.ActualWidth - cardAt.X - card.ActualWidth - inset) < .6 && Math.Abs(Container.ActualHeight - cardAt.Y - card.ActualHeight - inset) < .6,
                $"{key} ({theme}): the composer card must stand {ComposerMargin} inside the pane's edge at the sides and the bottom; got left {cardAt.X:F1}, right {Container.ActualWidth - cardAt.X - card.ActualWidth:F1}, bottom {Container.ActualHeight - cardAt.Y - card.ActualHeight:F1} (with the {DesignMetrics.Stroke.Line} pane border)");
            Require(composerPanel.Spacing == 9, $"{key} ({theme}): the composer's parts must be 9 apart; got {composerPanel.Spacing}");
            // With nothing between them, the conversation's surface ends the same 12 over the card (M/SessionPaneView.swift:147-161, 660).
            var surface = output.View;
            if (surface.Visibility == Visibility.Visible && surface.ActualHeight > 0 && new FrameworkElement[] { nextActionsHost, toolPermissionHost, agentWebPromptScroll }.All(part => part.Visibility == Visibility.Collapsed || part.ActualHeight == 0))
            {
                var over = cardAt.Y - (surface.TransformToVisual(Container).TransformPoint(new Windows.Foundation.Point()).Y + surface.ActualHeight);
                Require(Math.Abs(over - ComposerMargin) < .6, $"{key} ({theme}): the conversation's surface must end {ComposerMargin} over the composer card; got {over:F1}");
            }
            var previousFocus = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(o.root.XamlRoot) as Control;
            try
            {
                // Programmatic focus can be refused (a window that is not in front); the ring is then
                // driven through the same flag the focus events set, so the colours are still checked.
                async Task Blur()
                {
                    if (menuButton.Focus(FocusState.Programmatic)) await WaitUI(() => !composerFocused);
                    else { composerFocused = false; PaintComposerRing(); }
                }
                if (composerFocused) await Blur();
                o.RequireBrush(edge, e => ((Border)e).BorderBrush, DesignToken.Line, "the composer edge without focus", key: key);
                RequireThickness(edge, DesignMetrics.Stroke.Line, "the composer edge without focus (Stroke.Line)", key);
                var toolbarAt = toolbar.TransformToVisual(card).TransformPoint(new Windows.Foundation.Point());
                if (input.Focus(FocusState.Programmatic)) await WaitUI(() => composerFocused);
                else { composerFocused = true; PaintComposerRing(); }
                o.RequireBrush(edge, e => ((Border)e).BorderBrush, DesignToken.Accent, "the composer focus ring (composerFocus)", DesignMetrics.Opacity.ComposerFocus, key);
                RequireThickness(edge, DesignMetrics.Stroke.Focus, "the composer focus ring (Stroke.Focus)", key);
                o.root.UpdateLayout();
                var focusedAt = toolbar.TransformToVisual(card).TransformPoint(new Windows.Foundation.Point());
                Require(Math.Abs(focusedAt.X - toolbarAt.X) < .01 && Math.Abs(focusedAt.Y - toolbarAt.Y) < .01, $"{key} ({theme}): the focus ring must not move the composer's contents; the toolbar went from ({toolbarAt.X:F2}, {toolbarAt.Y:F2}) to ({focusedAt.X:F2}, {focusedAt.Y:F2})");
                await Blur();
                o.RequireBrush(edge, e => ((Border)e).BorderBrush, DesignToken.Line, "the composer edge after the editor lost focus", key: key);
                RequireThickness(edge, DesignMetrics.Stroke.Line, "the composer edge after the editor lost focus (Stroke.Line)", key);
            }
            finally { previousFocus?.Focus(FocusState.Programmatic); composerFocused = input.FocusState != FocusState.Unfocused; PaintComposerRing(); }
            RequireShared(input.Resources["TextControlBackgroundFocused"] as Brush, b.Transparent, "the editor's focused fill", key);
            // The editor: 13pt, one line 20 high, six at most, set 8 in from the card with the text view's own 5 (M/NativeComposerEditor.swift:32-33, M/SessionPaneView.swift:583).
            Require(input.FontSize == DesignMetrics.Type.Body && input.MinHeight == ComposerLine && input.Padding.Left == 5 && inputRow.Margin.Left == 8 && inputRow.Margin.Right == 8,
                $"{key} ({theme}): the editor must be {DesignMetrics.Type.Body}pt, {ComposerLine} high for one line, padded 5 inside a row 8 from the card's edge; got {input.FontSize}pt, min {input.MinHeight}, padding {input.Padding}, row margin {inputRow.Margin}");
            // Typed words start at the row's 8 and the text view's 5, with no room kept for the hidden chip (the Mac's HStack spaces only what it
            // shows); they stand 2 up in their line box; the placeholder starts 2 after them, as the Mac's label does (M/TextEditorHeightReader.swift:86, 93).
            var drawnPlaceholder = VisualChildren(input).OfType<TextBlock>().FirstOrDefault(part => part.Name == "PlaceholderTextContentPresenter");
            Require(inputRow.ColumnSpacing == 0 && styleEnterChip.Margin == new Thickness(0, 0, ComposerChipGap, 0) && input.Margin == new Thickness(0, -ComposerTextLift, 0, ComposerTextLift)
                && drawnPlaceholder is not null && drawnPlaceholder.Margin == new Thickness(ComposerPlaceholderInset, 0, 0, 0),
                $"{key} ({theme}): the editor's row must keep its {ComposerChipGap} gap on the chip alone, the editor {ComposerTextLift} up in its room and the placeholder {ComposerPlaceholderInset} after typed words; got row spacing {inputRow.ColumnSpacing}, chip margin {styleEnterChip.Margin}, editor margin {input.Margin}, placeholder margin {drawnPlaceholder?.Margin.ToString() ?? "no PlaceholderTextContentPresenter"}");
            // The placeholder is the Mac's placeholderTextColor, the tertiary ink (M/TextEditorHeightReader.swift:94), on the property the template draws from.
            RequireShared(input.Resources["TextControlPlaceholderForeground"] as Brush, b.Tertiary, "the editor's placeholder", key);
            RequireShared(input.PlaceholderForeground, b.Tertiary, "the editor's placeholder at rest", key);
            o.RequireBrush(input, e => ((TextBox)e).PlaceholderForeground, DesignToken.Ink, "the editor's placeholder at rest", DesignBrushes.TertiaryOpacity, key);
            RequireShared(drawnPlaceholder!.Foreground, b.Tertiary, "the editor's drawn placeholder", key);

            // The toolbar (M/SessionPaneView.swift:707-771, M/ComposerControls.swift:39-41): one Layout.Toolbar row 10 from the card's sides,
            // the pills leading and 6 apart in the Mac's order, the right cluster trailing.
            Require(toolbar.Height == DesignMetrics.Layout.Toolbar && toolbar.Margin.Left == 10 && toolbar.Margin.Right == 10 && selectors.Spacing == ToolbarSpacing && toolbarActions.Spacing == ToolbarSpacing
                && selectors.HorizontalAlignment == HorizontalAlignment.Left,
                $"{key} ({theme}): the toolbar must be {DesignMetrics.Layout.Toolbar} high, 10 from the card's sides, its controls {ToolbarSpacing} apart and the pills leading; got {toolbar.Height}, {toolbar.Margin}, {selectors.Spacing}/{toolbarActions.Spacing}, {selectors.HorizontalAlignment}");
            var order = selectors.Children.OfType<Control>().ToList();
            Require(order.SequenceEqual(new Control[] { attach, model, effort, permission, fast, more, options }),
                $"{key} ({theme}): the pills must stand in the Mac's order: attach, model, effort, permission, Fast, …, options");
            // The pills: card with a 1pt line at rest; accentSoft with accent x 0.35 while on. The look is
            // drawn on each pill's face; the button itself draws nothing at rest and only the subtle wash
            // (under the opaque face) under the pointer, from resources written once.
            foreach (var (pill, what) in new[] { (model, "the model pill"), (attach, "the attach pill") })
            {
                var face = Parts(pill).Face;
                o.RequireBrush(face, e => ((Border)e).Background, DesignToken.Card, what, key: key);
                o.RequireBrush(face, e => ((Border)e).BorderBrush, DesignToken.Line, what + "'s border", key: key);
                Require(face.CornerRadius == new CornerRadius(16) && face.BorderThickness == new Thickness(DesignMetrics.Stroke.Line) && pill.Height == DesignMetrics.Layout.Toolbar
                    && pill.Padding == new Thickness(0) && pill.BorderThickness == new Thickness(0) && Math.Abs(face.ActualHeight - pill.Height) < .5,
                    $"{key} ({theme}): {what} must be a {DesignMetrics.Layout.Toolbar}-tall capsule face with a Stroke.Line border filling a plain button; got {pill.Height}, face {face.ActualHeight} r{face.CornerRadius} {face.BorderThickness}, button padding {pill.Padding} border {pill.BorderThickness}");
                o.RequireClear(pill.Background, what + "'s button at rest", key);
                o.RequireSubtle(await StateBackground(pill, "PointerOver", b.Subtle, key), what + "'s button under the pointer", key);
            }
            // A pill is as wide as its contents (never stretched): the 14pt symbol box, the words and the 7pt chevron 5 apart, 8 in from the edge.
            var modelParts = Parts(model); var modelRow = (StackPanel)modelParts.Face.Child;
            Require(modelRow.Spacing == 5 && modelParts.IconHost.Width == 14 && modelParts.IconHost.Height == 14 && modelParts.Face.Padding.Left + DesignMetrics.Stroke.Line == 8 && double.IsNaN(model.Width) && model.HorizontalAlignment != HorizontalAlignment.Stretch,
                $"{key} ({theme}): the model pill must be sized to its contents with a 14pt symbol box, spacing 5 and padding 8; got spacing {modelRow.Spacing}, box {modelParts.IconHost.Width}x{modelParts.IconHost.Height}, padding {modelParts.Face.Padding}, width {model.Width}");
            Require(Math.Abs(model.ActualWidth - (modelRow.ActualWidth + 16)) < 1 && model.ActualWidth < selectors.ActualWidth,
                $"{key} ({theme}): the model pill must be exactly its contents plus the padding wide; got {model.ActualWidth:F1} for contents {modelRow.ActualWidth:F1}");
            Require(modelParts.IconHost.Children.Count == 1 && modelParts.IconHost.Children[0] is Microsoft.UI.Xaml.Shapes.Path && modelParts.Icon is null && pillProvider == pane.Provider,
                $"{key} ({theme}): the model pill must carry its provider's mark ({pane.Provider}); got {pillProvider ?? "none"}");
            Require(modelParts.Chevron.View.Visibility == Visibility.Visible && Parts(effort).HasChevron && Parts(permission).HasChevron && !Parts(fast).HasChevron && !Parts(attach).HasChevron,
                $"{key} ({theme}): the model, effort and permission pills must end in a chevron; attach and Fast must not");
            o.RequireBrush(modelParts.Chevron.View, _ => modelParts.Chevron.Colour, DesignToken.Ink2, "the model pill's chevron", key: key);
            Require(attach.Width == DesignMetrics.Layout.Toolbar && more.Width == DesignMetrics.Layout.Toolbar && Parts(attach).Words.Visibility == Visibility.Collapsed,
                $"{key} ({theme}): the attach and … pills must be {DesignMetrics.Layout.Toolbar}pt circles around their symbol; got {attach.Width} and {more.Width}");
            o.RequireBrush(PillText(model), e => ((TextBlock)e).Foreground, DesignToken.Ink, "the model pill's words", key: key);
            RequireFont(PillText(model), DesignMetrics.Type.Pill, Microsoft.UI.Text.FontWeights.Medium, $"({theme}) the model pill's words", key);
            Require(PillText(model).Text == ModelLabel.Selection(pane, o.Runtime(pane.Provider)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider)),
                $"{key} ({theme}): the model pill must read the model's label alone; got '{PillText(model).Text}'");
            var fullAccess = pane.Settings.PermissionMode == "fullAccess";
            try
            {
                PaintPill(permission, true);
                var face = Parts(permission).Face;
                o.RequireBrush(face, e => ((Border)e).Background, DesignToken.AccentSoft, "an active pill", key: key);
                o.RequireBrush(face, e => ((Border)e).BorderBrush, DesignToken.Accent, "an active pill's border (pillActiveBorder)", DesignMetrics.Opacity.PillActiveBorder, key);
                o.RequireBrush(PillText(permission), e => ((TextBlock)e).Foreground, DesignToken.Accent, "an active pill's words", key: key);
                o.RequireBrush(face, _ => Parts(permission).Icon?.Colour, DesignToken.Accent, "an active pill's symbol", key: key);
                Require(ReferenceEquals(OwnResource(permission, "ButtonBackground"), b.Transparent) && ReferenceEquals(OwnResource(permission, "ButtonBackgroundPointerOver"), b.Subtle),
                    $"{key} ({theme}): an active pill's button must keep the resources written once (transparent, subtle under the pointer); got {Describe(OwnResource(permission, "ButtonBackground") as Brush)} / {Describe(OwnResource(permission, "ButtonBackgroundPointerOver") as Brush)}");
            }
            finally { PaintPill(permission, fullAccess); }
            // A disabled pill is its whole face at half strength, as SwiftUI draws a disabled plain button (the Mac's running composer).
            var permissionEnabled = permission.IsEnabled;
            try
            {
                // The face follows the IsEnabledChanged event.
                permission.IsEnabled = false;
                await WaitUI(() => Math.Abs(Parts(permission).Face.Opacity - DisabledDim) < .001, () => $"{key} ({theme}): a disabled pill's face must be drawn at {DisabledDim}; got {Parts(permission).Face.Opacity}");
                permission.IsEnabled = true;
                await WaitUI(() => Parts(permission).Face.Opacity == 1, () => $"{key} ({theme}): an enabled pill's face must be opaque; got {Parts(permission).Face.Opacity}");
            }
            finally { permission.IsEnabled = permissionEnabled; }
            var fastChecked = fast.IsChecked; var fastFace = Parts(fast).Face;
            try
            {
                foreach (var on in new[] { false, true })
                {
                    fast.IsChecked = on;
                    var state = on ? "on" : "off";
                    // The face follows the Checked / Unchecked events.
                    await WaitUI(() => ReferenceEquals(fastFace.Background, b.Brush(on ? DesignToken.AccentSoft : DesignToken.Card)), $"{key} ({theme}): the Fast pill's face did not follow its check ({state})");
                    o.RequireBrush(fastFace, e => ((Border)e).Background, on ? DesignToken.AccentSoft : DesignToken.Card, $"the Fast pill {state}", key: key);
                    if (on) o.RequireBrush(fastFace, e => ((Border)e).BorderBrush, DesignToken.Accent, "the Fast pill's border on (pillActiveBorder)", DesignMetrics.Opacity.PillActiveBorder, key);
                    else o.RequireBrush(fastFace, e => ((Border)e).BorderBrush, DesignToken.Line, "the Fast pill's border off", key: key);
                    o.RequireBrush(PillText(fast), e => ((TextBlock)e).Foreground, on ? DesignToken.Accent : DesignToken.Ink, $"the Fast pill's words {state}", key: key);
                    Require(fastFilled == on, $"{key} ({theme}): the Fast pill must show the filled bolt exactly while on ({state})");
                }
                Require(ReferenceEquals(OwnResource(fast, "ToggleButtonBackgroundChecked"), b.Transparent) && ReferenceEquals(OwnResource(fast, "ToggleButtonBackground"), b.Transparent)
                    && ReferenceEquals(OwnResource(fast, "ToggleButtonBackgroundPointerOver"), b.Subtle) && ReferenceEquals(OwnResource(fast, "ToggleButtonBackgroundCheckedPointerOver"), b.Subtle),
                    $"{key} ({theme}): the Fast toggle's own resources must stay transparent with the subtle wash under the pointer, checked or not");
            }
            finally { fast.IsChecked = fastChecked; }

            // Send and stop: the run circle with a draft, the track circle without, the err square while stopping.
            // The shared pane still holds an attachment from the composer check, which alone makes it
            // sendable: set the attachments aside and put them back afterwards.
            Require(ReferenceEquals(send.Content, sendGlyph) && ReferenceEquals(OwnResource(send, "ButtonBackground"), b.Transparent) && ReferenceEquals(OwnResource(send, "ButtonBackgroundDisabled"), b.Transparent),
                $"{key} ({theme}): the send button must draw nothing of its own and show its symbol as content; got {send.Content?.GetType().Name}, {Describe(OwnResource(send, "ButtonBackground") as Brush)}");
            var previousDraft = input.Text; var attachments = pendingAttachments.ToArray();
            try
            {
                pendingAttachments.Clear(); RefreshAttachments();
                // Whether a draft may be sent also hangs on the pane's runtime, which the runner may not
                // have; the look is checked for the state the button is in, then for each state set directly.
                input.Text = "paneChromeDesign"; await WaitUI(() => Session.Draft == input.Text);
                Require(sendSymbol == "send" && sendArrow.View.Visibility == Visibility.Visible && sendStop.View.Visibility == Visibility.Collapsed, $"{key} ({theme}): an idle pane with a draft must show the send arrow, not the stop square; got {sendSymbol}");
                o.RequireBrush(sendDisc, e => ((Border)e).Background, send.IsEnabled ? DesignToken.Run : DesignToken.Track, $"the send circle with a draft (send {(send.IsEnabled ? "enabled" : "disabled")})", key: key);
                send.IsEnabled = true; PaintSend();
                o.RequireBrush(sendDisc, e => ((Border)e).Background, DesignToken.Run, "the send circle with a draft", key: key);
                o.RequireBrush(sendDisc, _ => sendArrow.Colour, DesignToken.OnStatus, "the send arrow", key: key);
                Require(sendDisc.Width == DesignMetrics.Layout.Toolbar && sendDisc.Height == DesignMetrics.Layout.Toolbar && sendDisc.CornerRadius == new CornerRadius(16) && sendHost.Opacity == 1, $"{key} ({theme}): the send shape must be an opaque {DesignMetrics.Layout.Toolbar} circle; got {sendDisc.Width}x{sendDisc.Height} r{sendDisc.CornerRadius} at {sendHost.Opacity}");
                input.Text = ""; await WaitUI(() => !send.IsEnabled && Session.Draft == "");
                o.RequireBrush(sendDisc, e => ((Border)e).Background, DesignToken.Track, "the send circle with nothing to send", key: key);
                o.RequireBrush(sendDisc, _ => sendArrow.Colour, DesignToken.Ink2, "the disabled send arrow", key: key);
                Require(Math.Abs(sendHost.Opacity - DisabledDim) < .001, $"{key} ({theme}): a send button with nothing to send must be drawn at {DisabledDim}, as the Mac's disabled button; got {sendHost.Opacity}");
                ShowSendSymbol("stop"); PaintSend();
                o.RequireBrush(sendDisc, e => ((Border)e).Background, DesignToken.Err, "the stop square", key: key);
                Require(sendDisc.CornerRadius == new CornerRadius(DesignMetrics.Radius.Row) && sendStop.View.Visibility == Visibility.Visible && sendArrow.View.Visibility == Visibility.Collapsed, $"{key} ({theme}): the stop square must have radius {DesignMetrics.Radius.Row} and show the stop symbol; got {sendDisc.CornerRadius}");
                o.RequireBrush(sendDisc, _ => sendStop.Colour, DesignToken.OnStatus, "the stop symbol while the run is stopping (send disabled)", key: key);
            }
            finally
            {
                updating = true; input.Text = previousDraft; updating = false; await Change(p => p with { Draft = previousDraft });
                pendingAttachments.Clear(); pendingAttachments.AddRange(attachments); RefreshAttachments(); RefreshComposerState();
            }

            // The context ring: its own toolbar-high button; a track in ink x 0.12 two points wide, an accent arc, waitText from 95%.
            Require(contextIndicator is not null && context.Width == DesignMetrics.Layout.Toolbar && context.Height == DesignMetrics.Layout.Toolbar && Math.Abs(contextIndicator.View.Width - (ContextRingSize + 2)) < .01, $"{key} ({theme}): the composer must have a context ring {ContextRingSize + 2} across (a {ContextRingSize}pt circle and its 2pt line) in a {DesignMetrics.Layout.Toolbar}pt button; got {contextIndicator?.View.Width} in {context.Width}x{context.Height}");
            o.RequireBrush(contextIndicator!.View, _ => contextIndicator.TrackStroke, DesignToken.Ink, "the context ring's track", ContextUsageRing.TrackOpacity, key);
            var ring = new ContextUsageRing(b, ContextRingSize); ring.Update(40);
            o.RequireBrush(ring.View, _ => ring.ArcStroke, DesignToken.Accent, "the context arc under 95%", key: key);
            ring.Update(96);
            o.RequireBrush(ring.View, _ => ring.ArcStroke, DesignToken.WaitText, "the context arc from 95%", key: key);

            // The status-line toggle: in the composer's right cluster after the context ring, in its 16pt-wide, toolbar-high frame (M/SessionPaneView.swift:123-133, 736);
            // ink2 off and accent on, the subtle wash under the pointer.
            var toggleTheme = statusLineToggle.Resources.ThemeDictionaries[theme == "light" ? "Light" : "Dark"] as ResourceDictionary;
            Require(statusLineToggle.Width == 16 && statusLineToggle.Height == DesignMetrics.Layout.Toolbar && toggleTheme is not null
                && ReferenceEquals(toggleTheme["ToggleButtonForeground"], b.Brush(DesignToken.Ink2)) && ReferenceEquals(toggleTheme["ToggleButtonForegroundChecked"], b.Brush(DesignToken.Accent))
                && ReferenceEquals(toggleTheme["ToggleButtonBackgroundPointerOver"], b.Subtle),
                $"{key} ({theme}): the status-line toggle must be 16x{DesignMetrics.Layout.Toolbar} with the shared ink2 / accent / subtle brushes; got {statusLineToggle.Width}x{statusLineToggle.Height}");
            var cluster = toolbarActions.Children.ToList();
            Require(cluster.IndexOf(resumeHost) == 0 && cluster.IndexOf(context) == 1 && cluster.IndexOf(statusLineToggle) == 2 && cluster.IndexOf(sendHost) == cluster.Count - 1 && !paneHeaderControls.Children.Contains(statusLineToggle),
                $"{key} ({theme}): the right cluster must be the resume mark, the context ring, the status-line toggle, then stop / send, and the toggle must have left the pane header");
            o.RequireBrush(resumeHost, _ => resumeMark.Colour, DesignToken.Ink2, "the resume mark", key: key);
            Require(resumeHost.Visibility == (pane.ResumeId is null ? Visibility.Collapsed : Visibility.Visible), $"{key} ({theme}): the resume mark must show exactly while the pane continues an earlier conversation; got {resumeHost.Visibility}");

            // The permission card: the wait card, its buttons r9.
            RequireRadius(permissionCard, DesignMetrics.Radius.Composer, "the permission card (Radius.Composer)", key);
            RequireThickness(permissionCard, DesignMetrics.Stroke.Active, "the permission card's wait edge (Stroke.Active)", key);
            o.RequireBrush(permissionCard, e => ((Border)e).BorderBrush, DesignToken.Wait, "the permission card's edge", key: key);
            o.RequireBrush(permissionCard, e => ((Border)e).Background, DesignToken.Card, "the permission card", key: key);
            o.RequireBrush(permAllowButton, e => ((Control)e).Background, DesignToken.Ink, "the allow button", key: key);
            o.RequireBrush(permAllowButton, e => ((Control)e).Foreground, DesignToken.Card, "the allow button's words", key: key);
            o.RequireBrush(permDenyButton, e => ((Control)e).Background, DesignToken.CardRaised, "the deny button", key: key);
            o.RequireBrush(permDenyButton, e => ((Control)e).BorderBrush, DesignToken.Line, "the deny button's border", key: key);
            Require(permAllowButton.CornerRadius == new CornerRadius(DesignMetrics.Radius.CardButton) && permDenyButton.CornerRadius == new CornerRadius(DesignMetrics.Radius.CardButton),
                $"{key} ({theme}): the card buttons must have radius {DesignMetrics.Radius.CardButton}; got {permAllowButton.CornerRadius}, {permDenyButton.CornerRadius}");

            // Pictures of the composer's states in this theme, beside the smoke's other screenshots (smoke-composer-*.png).
            await CaptureComposerSmoke();
        }

        private void RequireShared(Brush? actual, Brush expected, string what, string key) =>
            Require(ReferenceEquals(actual, expected), $"{key} ({owner.SmokeTheme}): {what} must be the shared {Describe(expected)} brush; got {Describe(actual)}");
    }
}
