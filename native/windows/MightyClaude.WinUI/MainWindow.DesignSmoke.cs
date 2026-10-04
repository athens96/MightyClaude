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
        RequireBrush(sidebarSurface, e => ((Border)e).BorderBrush, DesignToken.Line, "the sidebar's trailing divider", key: key);
        Require(sidebarSurface.BorderThickness == new Thickness(0, 0, DesignMetrics.Stroke.Line, 0), $"{key} ({theme}): the sidebar divider must be Stroke.Line {DesignMetrics.Stroke.Line} on the trailing edge only; got {sidebarSurface.BorderThickness}");
        var column = root.ColumnDefinitions[0];
        Require(column.MinWidth == DesignMetrics.Layout.SidebarMin && column.MaxWidth == DesignMetrics.Layout.SidebarMax,
            $"{key} ({theme}): the sidebar column must be bounded by Layout.SidebarMin {DesignMetrics.Layout.SidebarMin} and SidebarMax {DesignMetrics.Layout.SidebarMax}; got {column.MinWidth}..{column.MaxWidth}");

        var workspace = service.Snapshot.Workspaces.First(w => w.Id == service.Snapshot.ActiveWorkspaceId);
        Require(workspaceHeader.Visibility == Visibility.Visible && workspaceHeaderName.Text == workspace.Name && Grid.GetRow(workspaceHeader) == 0 && Grid.GetColumn(workspaceHeader) == 1,
            $"{key} ({theme}): the workspace header must show the active workspace '{workspace.Name}' over the dock; got '{workspaceHeaderName.Text}', {workspaceHeader.Visibility}, row {Grid.GetRow(workspaceHeader)} column {Grid.GetColumn(workspaceHeader)}");
        Require(workspaceHeader.Padding == new Thickness(24, 14, 24, 10), $"{key} ({theme}): the workspace header padding must be h24 t14 b10; got {workspaceHeader.Padding}");
        Require(workspaceHeader.BorderThickness == new Thickness(0, 0, 0, DesignMetrics.Stroke.Line), $"{key} ({theme}): the divider under the workspace header must be a bottom Stroke.Line {DesignMetrics.Stroke.Line} only; got {workspaceHeader.BorderThickness}");
        RequireBrush(workspaceHeader, e => ((Grid)e).BorderBrush, DesignToken.Line, "the divider under the workspace header", key: key);
        Require(AutomationProperties.GetAutomationId(workspaceHeader) == "workspace-header-" + workspace.Id && AutomationProperties.GetAutomationId(workspaceHeaderCounts) == "workspace-header-status-" + workspace.Id,
            $"{key} ({theme}): the header and its counts must carry the Mac's ids workspace-header-{workspace.Id} / workspace-header-status-{workspace.Id}; got '{AutomationProperties.GetAutomationId(workspaceHeader)}' / '{AutomationProperties.GetAutomationId(workspaceHeaderCounts)}'");
        Require(workspaceHeaderName.FontSize == DesignMetrics.Type.Header && workspaceHeaderName.FontWeight.Weight == Microsoft.UI.Text.FontWeights.SemiBold.Weight,
            $"{key} ({theme}): the workspace header name must be Type.Header {DesignMetrics.Type.Header}pt semibold; got {workspaceHeaderName.FontSize}pt weight {workspaceHeaderName.FontWeight.Weight}");
        RequireBrush(workspaceHeaderName, e => ((TextBlock)e).Foreground, DesignToken.Ink, "the workspace header name", key: key);
        Require(workspaceHeaderPath.FontFamily?.Source == DesignMetrics.Font.Mono && workspaceHeaderPath.FontSize == DesignMetrics.Type.Mono && workspaceHeaderPath.IsTextSelectionEnabled,
            $"{key} ({theme}): the workspace path must be selectable Font.Mono '{DesignMetrics.Font.Mono}' at Type.Mono {DesignMetrics.Type.Mono}pt; got '{workspaceHeaderPath.FontFamily?.Source}' at {workspaceHeaderPath.FontSize}pt");
        RequireBrush(workspaceHeaderPath, e => ((TextBlock)e).Foreground, DesignToken.Ink2, "the workspace header path", key: key);
        Require(workspaceHeaderFiles.Width == 26 && workspaceHeaderFiles.Height == 24 && AutomationProperties.GetAutomationId(workspaceHeaderFiles) == "workspace-open-files-" + workspace.Id,
            $"{key} ({theme}): the header files button must be 26×24 for the active workspace; got {workspaceHeaderFiles.Width}×{workspaceHeaderFiles.Height} '{AutomationProperties.GetAutomationId(workspaceHeaderFiles)}'");

        var dock = (panes.Children.OfType<ScrollViewer>().FirstOrDefault()?.Content as FrameworkElement)?.Margin;
        Require(dock == new Thickness(DesignMetrics.Layout.DockInset), $"{key} ({theme}): the pane dock must sit Layout.DockInset {DesignMetrics.Layout.DockInset} inside its scroll view; got {dock?.ToString() ?? "no dock"}");

        foreach (var (pane, name, token, opacity) in new[] { (active, "the active pane", DesignToken.Accent, DesignMetrics.Opacity.PaneActiveBorder), (inactive, "an inactive pane", DesignToken.Line, 1.0) })
        {
            RequireRadius(pane.Container, DesignMetrics.Radius.Pane, $"{name} card (Radius.Pane)", key);
            RequireThickness(pane.Container, DesignMetrics.Stroke.Line, $"{name} card (Stroke.Line)", key);
            RequireBrush(pane.Container, e => ((Border)e).Background, DesignToken.Card, $"{name} card background", key: key);
            RequireBrush(pane.Container, e => ((Border)e).BorderBrush, token, $"{name} card border", opacity, key);
        }

        RequireSubtle(statusBar, "the status bar background", key);
        RequireBrush(statusBar, e => ((Border)e).BorderBrush, DesignToken.Line, "the status bar's top divider", key: key);
        Require(statusBar.BorderThickness == new Thickness(0, DesignMetrics.Stroke.Line, 0, 0) && statusBar.Padding == new Thickness(20, 8, 20, 8) && Grid.GetRow(statusBar) == 2 && Grid.GetColumn(statusBar) == 1,
            $"{key} ({theme}): the status bar must be under the dock with a top Stroke.Line {DesignMetrics.Stroke.Line} and padding h20 v8; got border {statusBar.BorderThickness}, padding {statusBar.Padding}, row {Grid.GetRow(statusBar)} column {Grid.GetColumn(statusBar)}");
        Require(status.FontSize == DesignMetrics.Type.Small && status.TextWrapping == TextWrapping.NoWrap && status.TextTrimming == TextTrimming.CharacterEllipsis,
            $"{key} ({theme}): the status text must be one trimmed Type.Small {DesignMetrics.Type.Small}pt line; got {status.FontSize}pt, {status.TextWrapping}, {status.TextTrimming}");
        Require((error.Visibility == Visibility.Collapsed) == string.IsNullOrEmpty(error.Text), $"{key} ({theme}): the error line must be collapsed exactly while it is empty; got {error.Visibility} with '{error.Text}'");
        // One 10pt row on the Mac is about 36pt tall (padding 8 + chip 19 + 8, plus the line).
        if (error.Visibility == Visibility.Collapsed)
            Require(statusBar.ActualHeight is > 0 and <= 40, $"{key} ({theme}): the status bar must be one row, at most 40 tall (the Mac's is about 36); got {statusBar.ActualHeight:F1}");
        RequireBrush(status, e => ((TextBlock)e).Foreground, DesignToken.Ink2, "the status text", key: key);
        RequireBrush(error, e => ((TextBlock)e).Foreground, DesignToken.ErrText, "the error text", key: key);
        RequireTitleBar(key);
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
    /// own resource for the state must be <paramref name="expectedResource"/>.
    /// </summary>
    private static async Task<Brush?> StateBackground(Button button, string state, Brush expectedResource, string key)
    {
        var id = AutomationProperties.GetAutomationId(button);
        var resourceKey = "ButtonBackground" + state;
        Require(button.Resources.TryGetValue(resourceKey, out var resource) && ReferenceEquals(resource, expectedResource),
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
        RequireBrush(search, e => ((TextBox)e).Resources.TryGetValue("TextControlPlaceholderForeground", out var value) ? value as Brush : null, DesignToken.SidebarInk2, "the search placeholder", key: key);
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
        RequireBrush((FontIcon)icon.Child, e => ((FontIcon)e).Foreground, DesignToken.OnStatus, "the work-status tile's symbol", key: key);
        RequireFont(dashboardEntryTitle!, DesignMetrics.Type.Title, Microsoft.UI.Text.FontWeights.SemiBold, $"({theme}) the work-status title", key);
        RequireBrush(dashboardEntryTitle!, e => ((TextBlock)e).Foreground, DesignToken.Ink, "the work-status title", key: key);
        Require(entry.Padding == new Thickness(10, 8, 10, 8), $"{key} ({theme}): the work-status entry padding must be h10 v8; got {entry.Padding}");
        RequireRadius(entry, DesignMetrics.Radius.Entry, "the work-status entry (Radius.Entry)", key);
        RequireClear(entry.Background, "the work-status entry while the dashboard is hidden", key);
        showsDashboard = true; RefreshDashboardEntry();
        try { RequireBrush(entry, e => ((Border)e).Background, DesignToken.Card, "the work-status entry while the dashboard shows", key: key); }
        finally { showsDashboard = false; RefreshDashboardEntry(); }

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
        Require(ReferenceEquals(activeRow.BorderBrush, brushes.RowSelectedBorder) && Describe(activeRow.BorderBrush) == borderWant && activeRow.BorderThickness == new Thickness(DesignMetrics.Stroke.Hairline),
            $"{key} ({theme}): the selected pane row's border must be black at {border.GetProperty("opacity").GetDouble()} ({borderWant}), {DesignMetrics.Stroke.Hairline}pt; got {Describe(activeRow.BorderBrush)}, {activeRow.BorderThickness}");
        foreach (var (row, what) in new[] { (activeRow, "the selected pane row"), (inactiveRow, "a pane row") })
            Require(row.CornerRadius == new CornerRadius(DesignMetrics.Radius.Row) && row.Padding == new Thickness(8, 6, 9, 7),
                $"{key} ({theme}): {what} must have radius {DesignMetrics.Radius.Row} and padding l8 r9 t6 b7; got {row.CornerRadius}, {row.Padding}");
        RequireClear(inactiveRow.Background, "a pane row at rest", key);
        RequireClear(inactiveRow.BorderBrush, "a pane row's border at rest", key);
        RequireSubtle(await StateBackground(inactiveRow, "PointerOver", brushes.Subtle, key), "a pane row under the pointer", key);
        var selectedHover = await StateBackground(activeRow, "PointerOver", brushes.Brush(DesignToken.Card), key);
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
            var entryName = AutomationProperties.GetName(dashboardButton!);
            var entryWant = string.Join(", ", dashboardCounts!.Entries.Where(e => e.Entry.Visibility == Visibility.Visible).Select(e => AutomationProperties.GetName(e.Entry)).Prepend(Locale.Get("phone.dashboard.title")));
            Require(entryName == entryWant, $"{key} ({theme}): the work-status entry must read its title and counts, '{entryWant}'; got '{entryName}'");
        }
        finally
        {
            views[inactive.Id].ReceiveToolPermission(request with { State = "answered" });
            await service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => originals.TryGetValue(p.Id, out var status) ? p with { Status = status } : p).ToList() });
            RefreshRunningIndicators();
        }
        Require(sessionIndicators[inactive.Id].Status.Visibility == Visibility.Collapsed, $"{key} ({theme}): the waiting words stayed after the request was answered: '{sessionIndicators[inactive.Id].Status.Text}'");
        RequireCounts(workspaceStatusCounts[workspaceId], WorkDashboard.WorkspaceBadges(service.Snapshot.Sessions.Where(s => s.WorkspaceId == workspaceId), DashboardAttention), "the workspace counts after the restore", key, false);

        // The layout picker (decision Q3) and the footer (M/WorkspaceView.swift:107-118).
        RequireSubtle(layout.Background, "the layout picker", key);
        Require(layout.CornerRadius == new CornerRadius(DesignMetrics.Radius.Search) && layout.BorderThickness == new Thickness(0) && layout.FontSize == 12,
            $"{key} ({theme}): the layout picker must be 12pt at radius {DesignMetrics.Radius.Search} with no border; got {layout.FontSize}pt, {layout.CornerRadius}, {layout.BorderThickness}");
        RequireBrush(layout, e => ((Control)e).Resources.TryGetValue("ComboBoxBackgroundBorderBrushFocused", out var ring) ? ring as Brush : null, DesignToken.Accent, "the layout picker's keyboard-focus ring", key: key);
        var footer = sidebarFooter!;
        RequireBrush(footer, e => ((Grid)e).BorderBrush, DesignToken.Line, "the footer divider", key: key);
        Require(footer.BorderThickness == new Thickness(0, DesignMetrics.Stroke.Line, 0, 0) && footer.Padding == new Thickness(16),
            $"{key} ({theme}): the footer must have a top Stroke.Line {DesignMetrics.Stroke.Line} and padding 16; got {footer.BorderThickness}, {footer.Padding}");
        var glyph = (sidebarThemeButton?.Content as FontIcon)?.Glyph; var glyphWant = theme == "light" ? SidebarMoonGlyph : SidebarSunGlyph;
        Require(glyph == glyphWant, $"{key} ({theme}): the theme button must show the {(theme == "light" ? "moon" : "sun")} U+{(int)glyphWant[0]:X4}; got {(glyph is { Length: > 0 } g ? $"U+{(int)g[0]:X4}" : "none")}");
    }

    /// <summary>
    /// Counts in the Mac's order — wait, run, err — each a 12pt glyph of its tone and an 11pt bold
    /// <c>ink</c> count equal to the badges, a zero hidden; <paramref name="allThree"/> also requires
    /// all three to show.
    /// </summary>
    private void RequireCounts(StatusCountsView counts, WorkDashboard.Badges badges, string what, string key, bool allThree)
    {
        var theme = SmokeTheme;
        var shown = counts.Entries.Where(e => e.Entry.Visibility == Visibility.Visible).Select(e => e.Tone).ToList();
        if (allThree) Require(shown.SequenceEqual([DesignTone.Wait, DesignTone.Run, DesignTone.Err]), $"{key} ({theme}): {what} must show wait, run, err in that order; got {string.Join(", ", shown)}");
        foreach (var (tone, mark, count, entry) in counts.Entries)
        {
            var value = tone switch { DesignTone.Wait => badges.Questions + badges.Permissions, DesignTone.Run => badges.Running, _ => badges.Errors };
            Require((entry.Visibility == Visibility.Visible) == (value > 0), $"{key} ({theme}): {what}' {tone} entry must be {(value > 0 ? "shown" : "hidden")} for {value}; got {entry.Visibility}");
            if (value == 0) continue;
            var glyph = StatusGlyph.Kind(tone);
            Require(count.Text == value.ToString(System.Globalization.CultureInfo.InvariantCulture) && mark.Glyph == glyph && mark.View.Width == 12,
                $"{key} ({theme}): {what}' {tone} entry must be a 12pt {glyph} and {value}; got {mark.View.Width}pt {mark.Glyph} and '{count.Text}'");
            RequireFont(count, DesignMetrics.Type.Pill, Microsoft.UI.Text.FontWeights.Bold, $"({theme}) {what}' {tone} count", key);
            RequireBrush(count, e => ((TextBlock)e).Foreground, DesignToken.Ink, $"{what}' {tone} count", key: key);
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
}
