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
    /// <summary>The status bar under the dock: the subtle wash, a 1pt top <c>line</c>, padding h20 v8 (M/WorkspaceView.swift:375-395).</summary>
    private readonly Border statusBar = new() { BorderThickness = new Thickness(0, DesignMetrics.Stroke.Line, 0, 0), Padding = new Thickness(20, 8, 20, 8) };
    /// <summary>
    /// The active workspace's name, path, Git state, counts and files button, padding h24 t14 b10,
    /// with the Divider under it as a 1pt bottom <c>line</c> (M/WorkspaceView.swift:21-22, 289-319).
    /// </summary>
    private readonly Grid workspaceHeader = new() { ColumnSpacing = 14, Padding = new Thickness(24, 14, 24, 10), BorderThickness = new Thickness(0, 0, 0, DesignMetrics.Stroke.Line), Visibility = Visibility.Collapsed };
    private readonly TextBlock workspaceHeaderName = new() { FontSize = DesignMetrics.Type.Header, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock workspaceHeaderPath = new() { FontFamily = new FontFamily(DesignMetrics.Font.Mono), FontSize = DesignMetrics.Type.Mono, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border workspaceHeaderGit = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock workspaceHeaderCounts = new() { FontSize = DesignMetrics.Type.Mono, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button workspaceHeaderFiles = new() { Width = 26, Height = 24, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), VerticalAlignment = VerticalAlignment.Center, Content = new FontIcon { Glyph = "\uE8B7", FontSize = 13 } };
    /// <summary>The workspace the header shows, or null while it is hidden (no workspace, or the dashboard).</summary>
    private string? workspaceHeaderId;
    private string workspaceHeaderFullPath = "";
    /// <summary>The path's context menu item: copies the whole path, not the trimmed text.</summary>
    private readonly MenuFlyoutItem workspaceHeaderCopyPath = new();

    private void InitAppShell()
    {
        sidebarSurface.Background = brushes.Brush(DesignToken.Sidebar); sidebarSurface.BorderBrush = brushes.Brush(DesignToken.Line);
        AutomationProperties.SetAutomationId(sidebarSurface, "sidebar-surface");
        statusBar.Background = brushes.Subtle; statusBar.BorderBrush = brushes.Brush(DesignToken.Line);
        AutomationProperties.SetAutomationId(statusBar, "status-bar");
        status.Foreground = brushes.Brush(DesignToken.Ink2); error.Foreground = brushes.Brush(DesignToken.ErrText);
        // An empty error line takes no height, so the status bar stays one 10pt row. Every writer
        // of error.Text (twenty-odd sites) goes through this one callback.
        error.Visibility = Visibility.Collapsed;
        error.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => error.Visibility = string.IsNullOrEmpty(error.Text) ? Visibility.Collapsed : Visibility.Visible);

        workspaceHeader.BorderBrush = brushes.Brush(DesignToken.Line);
        workspaceHeaderName.Foreground = brushes.Brush(DesignToken.Ink);
        workspaceHeaderPath.Foreground = workspaceHeaderCounts.Foreground = workspaceHeaderFiles.Foreground = brushes.Brush(DesignToken.Ink2);
        workspaceHeader.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        workspaceHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); workspaceHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 3 }; text.Children.Add(workspaceHeaderName);
        var pathRow = new Grid { ColumnSpacing = 10 };
        pathRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); pathRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        pathRow.Children.Add(workspaceHeaderPath); Grid.SetColumn(workspaceHeaderGit, 1); pathRow.Children.Add(workspaceHeaderGit);
        pathRow.SizeChanged += (_, _) => FitWorkspaceHeaderPath(); workspaceHeaderGit.SizeChanged += (_, _) => FitWorkspaceHeaderPath();
        workspaceHeaderCopyPath.Click += (_, _) => Copy(workspaceHeaderFullPath);
        var pathMenu = new MenuFlyout(); pathMenu.Items.Add(workspaceHeaderCopyPath); workspaceHeaderPath.ContextFlyout = pathMenu;
        text.Children.Add(pathRow); workspaceHeader.Children.Add(text);
        Grid.SetColumn(workspaceHeaderCounts, 1); workspaceHeader.Children.Add(workspaceHeaderCounts);
        workspaceHeaderFiles.Click += async (_, _) => { if (!dialogOpen && workspaceHeaderId is { } id) await OpenFilePane(id); };
        Grid.SetColumn(workspaceHeaderFiles, 2); workspaceHeader.Children.Add(workspaceHeaderFiles);
    }

    /// <summary>Shows the active workspace in the header, or hides it with no workspace and behind the dashboard.</summary>
    private void RefreshWorkspaceHeader()
    {
        var state = service.Snapshot;
        var workspace = showsDashboard ? null : state.Workspaces.FirstOrDefault(w => w.Id == state.ActiveWorkspaceId);
        workspaceHeaderId = workspace?.Id;
        workspaceHeader.Visibility = workspace is null ? Visibility.Collapsed : Visibility.Visible;
        if (workspace is null) return;
        workspaceHeaderName.Text = workspace.Name;
        AutomationProperties.SetAutomationId(workspaceHeader, "workspace-header-" + workspace.Id);
        AutomationProperties.SetAutomationId(workspaceHeaderCounts, "workspace-header-status-" + workspace.Id);
        workspaceHeaderFullPath = workspace.Path; ToolTipService.SetToolTip(workspaceHeaderPath, workspace.Path);
        workspaceHeaderCopyPath.Text = Locale.Get("pane.copyButton");
        workspaceHeaderGit.Child = WorkspaceGitBadge(workspace);
        UpdateWorkspaceStatusCounts(workspace.Id, workspaceHeaderCounts);
        var files = Locale.Get("menu.showFiles");
        AutomationProperties.SetName(workspaceHeaderFiles, files); ToolTipService.SetToolTip(workspaceHeaderFiles, files + " (" + FilePaneKind.Shortcut + ")");
        AutomationProperties.SetAutomationId(workspaceHeaderFiles, "workspace-open-files-" + workspace.Id);
        FitWorkspaceHeaderPath();
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
        if (workspaceHeaderPath.Parent is not Grid row || row.ActualWidth <= 0) return;
        var available = Math.Max(0, row.ActualWidth - workspaceHeaderGit.ActualWidth - row.ColumnSpacing);
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
