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
    private void RequireSubtle(Border border, string what, string key)
    {
        var theme = SmokeTheme; var subtle = DesignFixture.Value.GetProperty("windowsOnly").GetProperty("subtle").GetProperty(theme);
        var hex = subtle.GetProperty("color").GetString()!; var opacity = subtle.GetProperty("opacity").GetDouble();
        var want = $"#{(int)Math.Round(opacity * 255):X2}{hex[1..]}";
        Require(ReferenceEquals(border.Background, brushes.Subtle) && Describe(border.Background) == want,
            $"{key} ({theme}): {what} must be the shared subtle wash {hex} at opacity {opacity} ({want}); got {Describe(border.Background)}{(ReferenceEquals(border.Background, brushes.Subtle) ? "" : " from another brush")}");
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
