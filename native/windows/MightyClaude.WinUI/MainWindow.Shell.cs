using System.Globalization;
using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

// The app shell (.omc/plans/windows-design-conversion.md, stage 2): the full-height sidebar
// surface, the workspace header over the pane dock and the status bar, laid out as the Mac's
// WorkspaceView does and coloured only with the shared token brushes, so a theme toggle
// recolours them in place.
public sealed partial class MainWindow
{
    /// <summary>The sidebar's solid surface, edge to edge and full height, with a 1pt <c>line</c> on its trailing edge (M/WorkspaceView.swift:124).</summary>
    private readonly Border sidebarSurface = new() { BorderThickness = new Thickness(0, 0, DesignMetrics.Stroke.Line, 0) };
    /// <summary>
    /// The status bar under the dock: the subtle wash under a 1pt top <c>line</c>, padding h20 v8
    /// (M/WorkspaceView.swift:393-394). The Mac's line is an overlay over that padding and that wash,
    /// so the 8 above the row counts the line here and the wash runs under it.
    /// </summary>
    private readonly Border statusBar = new() { BorderThickness = new Thickness(0, DesignMetrics.Stroke.Line, 0, 0), Padding = new Thickness(20, 8 - DesignMetrics.Stroke.Line, 20, 8), BackgroundSizing = BackgroundSizing.OuterBorderEdge };
    /// <summary>"3 panes" of the active workspace and "1 running" over every workspace (M/WorkspaceView.swift:380-382).</summary>
    private readonly TextBlock statusPanes = new() { FontSize = DesignMetrics.Type.Small, VerticalAlignment = VerticalAlignment.Center }, statusRunning = new() { FontSize = DesignMetrics.Type.Small, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>The accent "new version" badge, shown while an app update is available or ready (M/WorkspaceView.swift:383-388).</summary>
    private Button? updateBadge;
    private readonly TextBlock updateBadgeText = new() { FontSize = DesignMetrics.Type.Small, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>The line after the usage chips, shown with them (M/StatusBarUsage.swift:169).</summary>
    private Border? usageDivider;
    /// <summary>The status bar's trailing parts, in the Mac's order (M/WorkspaceView.swift:380-391).</summary>
    private readonly StackPanel statusTrailing = new() { Orientation = Orientation.Horizontal, Spacing = StatusBarSpacing, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>Runtime checks in flight; while one runs the status words say so instead of naming the machine.</summary>
    private int runtimeChecks;
    /// <summary>The status bar's HStack spacing, its dividers' height and their side padding (M/WorkspaceView.swift:376, 389).</summary>
    internal const double StatusBarSpacing = 7, StatusDividerHeight = 12, StatusDividerInset = 4;
    /// <summary>
    /// The error banner over the detail column (M/WorkspaceView.swift:18, 405-411): the warning
    /// triangle in <c>errText</c>, the 12pt message and a close button, 9 apart, padding 12 on <c>errSoft</c>.
    /// </summary>
    private readonly Border errorBanner = new() { Padding = new Thickness(12), Visibility = Visibility.Collapsed };
    /// <summary>The detail column's top row: the error banner over the workspace header (M/WorkspaceView.swift:16-22).</summary>
    private readonly StackPanel detailTop = new();
    /// <summary>
    /// The active workspace's name, path, Git state, counts and files button, padding h24 t14 b10,
    /// with the Divider under it as a 1pt bottom <c>line</c> (M/WorkspaceView.swift:21-22, 289-319).
    /// </summary>
    private readonly Grid workspaceHeader = new() { ColumnSpacing = 14, Padding = new Thickness(24, 14, 24, 10), BorderThickness = new Thickness(0, 0, 0, DesignMetrics.Stroke.Line), Visibility = Visibility.Collapsed };
    private readonly TextBlock workspaceHeaderName = new() { FontSize = DesignMetrics.Type.Header, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock workspaceHeaderPath = new() { FontFamily = new FontFamily(DesignMetrics.Font.Mono), FontSize = DesignMetrics.Type.Mono, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border workspaceHeaderGit = new() { VerticalAlignment = VerticalAlignment.Center };
    /// <summary>The header's counts, the long form: each state's glyph, count and name, as in "1 running  1 done" (M/WorkspaceView.swift:305-307).</summary>
    private StatusCountsView? workspaceHeaderCounts;
    private readonly Button workspaceHeaderFiles = new() { Width = 26, Height = 24, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), VerticalAlignment = VerticalAlignment.Center, Content = new FontIcon { Glyph = "\uE8B7", FontSize = 13 } };
    /// <summary>The workspace the header shows, or null while it is hidden (no workspace, or the dashboard).</summary>
    private string? workspaceHeaderId;
    private string workspaceHeaderFullPath = "";
    /// <summary>The path's context menu item: copies the whole path, not the trimmed text.</summary>
    private readonly MenuFlyoutItem workspaceHeaderCopyPath = new();
    /// <summary>The one item of the menu over the header's name and blank space: "Rename workspace…" (M/WorkspaceTitlebar.swift:30-36).</summary>
    private readonly MenuFlyoutItem workspaceHeaderRename = new();
    /// <summary>Whether the sidebar's rows were last drawn for the dashboard (none selected) or for the panes.</summary>
    private bool sidebarDrawnForDashboard;

    /// <summary>
    /// SwiftUI's <c>Divider()</c> draws AppKit's separator colour, the label colour at a tenth: on the
    /// Mac's screens #D5D6DA on the light page and #CCD0D5 on the light sidebar, where the <c>line</c>
    /// token (lighter than the sidebar and the status bar's wash) would not show. For the shell's
    /// Dividers (M/WorkspaceView.swift:22, 108, 389, 394, M/StatusBarUsage.swift:169, 298,
    /// M/AgentCompanionViews.swift:64).
    /// </summary>
    internal const double SeparatorOpacity = 0.10;
    /// <summary>The shared brush of the shell's Dividers (<see cref="SeparatorOpacity"/>); never mutate it.</summary>
    internal SolidColorBrush Separator => brushes.Brush(DesignToken.Ink, SeparatorOpacity);
    /// <summary>The sidebar's trailing edge, the split view's own line (M/WorkspaceView.swift:12): the separator by day, black by night (<see cref="DesignBrushes.SplitLine"/>); never mutate it.</summary>
    internal SolidColorBrush SidebarEdge => brushes.SplitLine(DesignToken.Ink, SeparatorOpacity);

    private void InitAppShell()
    {
        sidebarSurface.Background = brushes.Brush(DesignToken.Sidebar); sidebarSurface.BorderBrush = SidebarEdge;
        AutomationProperties.SetAutomationId(sidebarSurface, "sidebar-surface");
        statusBar.Background = brushes.Subtle; statusBar.BorderBrush = Separator;
        AutomationProperties.SetAutomationId(statusBar, "status-bar");
        status.Foreground = statusPanes.Foreground = statusRunning.Foreground = brushes.Brush(DesignToken.Ink2);
        BuildErrorBanner();

        workspaceHeader.BorderBrush = Separator;
        // The name stands on the Mac's 17pt line (about 20 high; Segoe's is 23), so the header is the Mac's 66 with a Git capsule.
        workspaceHeaderName.Foreground = brushes.Brush(DesignToken.Ink); workspaceHeaderName.Margin = MacLine();
        workspaceHeaderPath.Foreground = workspaceHeaderFiles.Foreground = brushes.Brush(DesignToken.Ink2);
        workspaceHeader.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        workspaceHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); workspaceHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 3 }; text.Children.Add(workspaceHeaderName);
        var pathRow = new Grid { ColumnSpacing = 10 };
        pathRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); pathRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        pathRow.Children.Add(workspaceHeaderPath); Grid.SetColumn(workspaceHeaderGit, 1); pathRow.Children.Add(workspaceHeaderGit);
        // The path is fitted again whenever the room it has changes: the header's width, the counts beside it, the Git capsule.
        pathRow.SizeChanged += (_, _) => FitWorkspaceHeaderPath(); workspaceHeaderGit.SizeChanged += (_, _) => FitWorkspaceHeaderPath();
        workspaceHeader.SizeChanged += (_, _) => FitWorkspaceHeaderPath(); text.SizeChanged += (_, _) => FitWorkspaceHeaderPath();
        workspaceHeaderCopyPath.Click += (_, _) => Copy(workspaceHeaderFullPath);
        var pathMenu = new MenuFlyout(); pathMenu.Items.Add(workspaceHeaderCopyPath); workspaceHeaderPath.ContextFlyout = pathMenu;
        // The Mac's header offers its rename over the name and the blank space (M/WorkspaceView.swift:296, 301); the
        // path and the buttons keep their own input. The clear fill lets the blank space take the click.
        workspaceHeaderRename.Click += async (_, _) => { if (workspaceHeaderId is { } id) await RenameWorkspace(id); };
        var headerMenu = new MenuFlyout(); headerMenu.Items.Add(workspaceHeaderRename);
        headerMenu.Opening += (_, _) => workspaceHeaderRename.IsEnabled = !dialogOpen;
        workspaceHeader.Background = brushes.Transparent; workspaceHeader.ContextFlyout = headerMenu;
        text.Children.Add(pathRow); workspaceHeader.Children.Add(text);
        var counts = workspaceHeaderCounts = new StatusCountsView(brushes, "workspace-header-running", longForm: true);
        Grid.SetColumn(counts.View, 1); workspaceHeader.Children.Add(counts.View);
        workspaceHeaderFiles.Click += async (_, _) => { if (!dialogOpen && workspaceHeaderId is { } id) await OpenFilePane(id); };
        Grid.SetColumn(workspaceHeaderFiles, 2); workspaceHeader.Children.Add(workspaceHeaderFiles);
        detailTop.Children.Add(errorBanner); detailTop.Children.Add(workspaceHeader);
    }

    /// <summary>
    /// Puts the window's error line into the Mac's banner. Every writer of <c>error.Text</c>
    /// (twenty-odd sites) goes through the one callback that shows the banner while there is a
    /// message; the close button clears it, as the Mac's does.
    /// </summary>
    private void BuildErrorBanner()
    {
        errorBanner.Background = brushes.Brush(DesignToken.ErrSoft);
        AutomationProperties.SetAutomationId(errorBanner, "error-banner");
        var row = new Grid { ColumnSpacing = 9 };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var warning = new FontIcon { Glyph = "\uE7BA", FontSize = 13, Foreground = brushes.Brush(DesignToken.ErrText), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
        AutomationProperties.SetAccessibilityView(warning, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        row.Children.Add(warning);
        error.Foreground = brushes.Brush(DesignToken.Ink); error.FontSize = DesignMetrics.Type.Row; error.IsTextSelectionEnabled = true; error.Visibility = Visibility.Collapsed;
        Grid.SetColumn(error, 1); row.Children.Add(error);
        var dismiss = Button("", () => { error.Text = ""; return Task.CompletedTask; });
        dismiss.Content = new FontIcon { Glyph = "\uE711", FontSize = 11 };
        dismiss.MinWidth = 0; dismiss.MinHeight = 0; dismiss.Padding = new Thickness(3); dismiss.Margin = new Thickness(0, -2, -3, -3); dismiss.BorderThickness = new Thickness(0);
        dismiss.CornerRadius = new CornerRadius(DesignMetrics.Radius.FileRow); dismiss.VerticalAlignment = VerticalAlignment.Top;
        PaintPlainButton(dismiss, brushes.Transparent, brushes.Subtle, ink: brushes.Brush(DesignToken.Ink));
        AutomationProperties.SetAutomationId(dismiss, "error-banner-dismiss");
        Grid.SetColumn(dismiss, 2); row.Children.Add(dismiss);
        errorBanner.Child = row; errorBannerDismiss = dismiss;
        error.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
            errorBanner.Visibility = error.Visibility = string.IsNullOrEmpty(error.Text) ? Visibility.Collapsed : Visibility.Visible);
    }

    private Button? errorBannerDismiss;

    /// <summary>
    /// The status bar's one row (M/WorkspaceView.swift:375-395): the machine symbol and words at the
    /// leading edge; after the spacer the pane and running counts, the update badge, a 12-high line,
    /// the usage chips with their own line (M/StatusBarUsage.swift:159-170), then the pet controls
    /// (M/AgentCompanionViews.swift:5-25). Everything 10pt <c>ink2</c>, 7 apart.
    /// </summary>
    private FrameworkElement BuildStatusBar()
    {
        var ink = brushes.Brush(DesignToken.Ink2);
        // desktopcomputer at 10pt draws a monitor 12 wide and 10 high: a framed screen over a deeper chin, on a stand.
        var machine = new Grid { Width = 12, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        machine.Children.Add(new Border { Height = 8.2, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(1.3), BorderBrush = ink, BorderThickness = new Thickness(0.9, 0.9, 0.9, 2.1) });
        machine.Children.Add(new Microsoft.UI.Xaml.Shapes.Polygon { Points = [new(4.6, 8.2), new(7.4, 8.2), new(8, 10), new(4, 10)], Fill = ink });
        AutomationProperties.SetAccessibilityView(machine, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        AutomationProperties.SetAutomationId(machine, "status-bar-machine"); AutomationProperties.SetAutomationId(status, "status-bar-text");
        var leading = new Grid { ColumnSpacing = StatusBarSpacing };
        leading.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); leading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        leading.Children.Add(machine); Grid.SetColumn(status, 1); leading.Children.Add(status);

        statusTrailing.Children.Add(statusPanes);
        statusTrailing.Children.Add(new TextBlock { Text = "\u00B7", FontSize = DesignMetrics.Type.Small, Foreground = ink, Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center });
        statusTrailing.Children.Add(statusRunning);
        // arrow.down.circle.fill: an accent disc with the arrow cut out of it; SF's circle symbols draw 1.2 of their point size, 12 at 10pt.
        var arrow = new Grid { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
        var cut = brushes.Brush(DesignToken.OnAccent);
        arrow.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Fill = brushes.Brush(DesignToken.Accent) });
        arrow.Children.Add(new Microsoft.UI.Xaml.Shapes.Polyline { Points = [new(6, 3.1), new(6, 8.6)], Stroke = cut, StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        arrow.Children.Add(new Microsoft.UI.Xaml.Shapes.Polyline { Points = [new(3.6, 6.4), new(6, 8.8), new(8.4, 6.4)], Stroke = cut, StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round });
        updateBadgeText.Foreground = brushes.Brush(DesignToken.Accent);
        var badge = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 }; badge.Children.Add(arrow); badge.Children.Add(updateBadgeText);
        updateBadge = Button("", OpenSettings); updateBadge.Content = badge;
        updateBadge.MinWidth = 0; updateBadge.MinHeight = 0; updateBadge.Padding = new Thickness(0); updateBadge.BorderThickness = new Thickness(0); updateBadge.VerticalAlignment = VerticalAlignment.Center; updateBadge.Visibility = Visibility.Collapsed;
        PaintPlainButton(updateBadge, brushes.Transparent, brushes.Transparent);
        AutomationProperties.SetAutomationId(updateBadge, "app-update-badge");
        statusTrailing.Children.Add(updateBadge);
        statusTrailing.Children.Add(StatusDivider());
        statusTrailing.Children.Add(BuildAccountUsage());
        usageDivider = StatusDivider(); usageDivider.Visibility = Visibility.Collapsed; statusTrailing.Children.Add(usageDivider);
        statusTrailing.Children.Add(BuildCompanionControls());

        var row = new Grid { ColumnSpacing = StatusBarSpacing };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(leading); Grid.SetColumn(statusTrailing, 1); row.Children.Add(statusTrailing);
        return row;
    }

    /// <summary>A status bar divider: a 12-high separator with 4 on either side (M/WorkspaceView.swift:389).</summary>
    private Border StatusDivider() => new()
    {
        Width = DesignMetrics.Stroke.Line, Height = StatusDividerHeight, Margin = new Thickness(StatusDividerInset, 0, StatusDividerInset, 0),
        Background = Separator, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// The status bar's words and counts for the state and the language: the machine at rest, what
    /// is being checked while a runtime check runs, the active workspace's panes, every running
    /// pane, and the update badge while a newer version is available or ready.
    /// </summary>
    private void RefreshStatusBar()
    {
        if (updateBadge is null) return;
        var state = service.Snapshot;
        status.Text = Locale.Get(runtimeChecks > 0 ? "window.status.checkingRuntimeAndModels" : runtime is null ? "window.status.checkingRuntime" : "window.status.thisMachine");
        string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
        statusPanes.Text = Locale.Get("window.status.paneCount", new Dictionary<string, string> { ["count"] = Count(state.Sessions.Count(s => s.WorkspaceId == state.ActiveWorkspaceId)) });
        statusRunning.Text = Locale.Get("window.status.runningCount", new Dictionary<string, string> { ["count"] = Count(state.Sessions.Count(s => s.Status == "running")) });
        var version = appUpdate?.State is { Phase: AppUpdatePhase.Available or AppUpdatePhase.Ready, Availability.Manifest.Version: { Length: > 0 } newer } ? newer : null;
        updateBadge.Visibility = version is null ? Visibility.Collapsed : Visibility.Visible;
        if (version is null) return;
        updateBadgeText.Text = Locale.Get("window.status.updateBadge", new Dictionary<string, string> { ["version"] = version });
        if (AutomationProperties.GetName(updateBadge) != updateBadgeText.Text) AutomationProperties.SetName(updateBadge, updateBadgeText.Text);
        ToolTipService.SetToolTip(updateBadge, Locale.Get("window.status.updateBadgeHelp"));
    }

    /// <summary>Shows the active workspace in the header, or hides it with no workspace and behind the dashboard.</summary>
    private void RefreshWorkspaceHeader()
    {
        var state = service.Snapshot;
        var workspace = showsDashboard ? null : state.Workspaces.FirstOrDefault(w => w.Id == state.ActiveWorkspaceId);
        workspaceHeaderId = workspace?.Id;
        workspaceHeader.Visibility = workspace is null ? Visibility.Collapsed : Visibility.Visible;
        // The sidebar selects the work-status entry or a workspace and pane, never both
        // (M/WorkspaceView.swift:137, 161, 216): its rows follow the dashboard showing or hiding.
        if (sidebarDrawnForDashboard != showsDashboard) RenderSidebar();
        if (workspace is null) return;
        workspaceHeaderName.Text = workspace.Name;
        AutomationProperties.SetAutomationId(workspaceHeader, "workspace-header-" + workspace.Id);
        if (workspaceHeaderCounts is { } counts)
        {
            AutomationProperties.SetAutomationId(counts.View, "workspace-header-status-" + workspace.Id);
            counts.Identify("workspace-header-running-" + workspace.Id);
        }
        workspaceHeaderFullPath = workspace.Path; ToolTipService.SetToolTip(workspaceHeaderPath, workspace.Path);
        workspaceHeaderCopyPath.Text = Locale.Get("pane.copyButton"); workspaceHeaderRename.Text = Locale.Get("menu.renameWorkspace");
        workspaceHeaderGit.Child = WorkspaceGitBadge(workspace);
        RefreshWorkspaceHeaderCounts(state, state.Theme != "light");
        var files = Locale.Get("menu.showFiles");
        AutomationProperties.SetName(workspaceHeaderFiles, files); ToolTipService.SetToolTip(workspaceHeaderFiles, files + " (" + FilePaneKind.Shortcut + ")");
        AutomationProperties.SetAutomationId(workspaceHeaderFiles, "workspace-open-files-" + workspace.Id);
        FitWorkspaceHeaderPath();
    }

    /// <summary>Recounts the header's long-form counts for the workspace it shows.</summary>
    private void RefreshWorkspaceHeaderCounts(AppSnapshot state, bool dark)
    {
        if (workspaceHeaderId is not { } id || workspaceHeaderCounts is not { } counts) return;
        counts.Update(WorkDashboard.WorkspaceBadges(state.Sessions.Where(s => s.WorkspaceId == id), DashboardAttention), dark);
    }

    /// <summary>
    /// The path keeps both ends and drops the middle when it does not fit, as the Mac's
    /// <c>.truncationMode(.middle)</c> does; WinUI only trims the end. It cuts only between text
    /// elements (so never inside a surrogate pair or a composed Hangul syllable) and measures
    /// each candidate, so wide CJK characters fit as well as ASCII ones.
    /// </summary>
    private void FitWorkspaceHeaderPath()
    {
        var full = workspaceHeaderFullPath;
        workspaceHeaderPath.MaxWidth = double.PositiveInfinity;
        workspaceHeaderPath.Text = full;
        // The room is the header's own first column: a row that overflows reports its content's width,
        // not what it was given, and would call a path that runs under the Git capsule a fit.
        var room = workspaceHeader.ColumnDefinitions.Count > 0 ? workspaceHeader.ColumnDefinitions[0].ActualWidth : 0;
        if (workspaceHeaderPath.Parent is not Grid row || room <= 0) return;
        var available = Math.Max(0, room - workspaceHeaderGit.ActualWidth - (workspaceHeaderGit.ActualWidth > 0 ? row.ColumnSpacing : 0));
        bool Fits(string text)
        {
            workspaceHeaderPath.Text = text;
            workspaceHeaderPath.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            return workspaceHeaderPath.DesiredSize.Width <= available;
        }
        var starts = StringInfo.ParseCombiningCharacters(full);
        string Kept(int keep) => full[..starts[(keep + 1) / 2]] + "…" + (keep / 2 == 0 ? "" : full[starts[starts.Length - keep / 2]..]);
        if (!Fits(full) && starts.Length > 2)
        {
            // The longest kept count (head + tail elements) that fits; 1 always shows the first element.
            int low = 1, high = starts.Length - 1;
            while (low < high) { var mid = (low + high + 1) / 2; if (Fits(Kept(mid))) low = mid; else high = mid - 1; }
            workspaceHeaderPath.Text = Kept(low);
        }
        // End trimming stays as the last backstop when even the shortest form is too wide.
        workspaceHeaderPath.MaxWidth = available;
    }
}

/// <summary>
/// The host of a resize grip, showing the resize cursor for its drag axis while the pointer is
/// over the grip inside it: west–east for a divider between side-by-side panes and for the
/// sidebar, north–south between stacked panes. WinUI's Thumb is sealed and the cursor is a
/// protected property, so the cursor lives on this parent and the Thumb inherits it.
/// </summary>
internal sealed partial class ResizeCursorHost : Grid
{
    internal ResizeCursorHost(Microsoft.UI.Xaml.Controls.Primitives.Thumb grip, bool horizontal)
    {
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(horizontal ? Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast : Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth);
        Children.Add(grip);
    }
}
