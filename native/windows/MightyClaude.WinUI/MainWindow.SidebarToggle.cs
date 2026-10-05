using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

// The sidebar fold (M/WorkspaceView.swift SidebarToggleButton): Ctrl+B or the sidebar button at the
// content's top-leading edge hides the sidebar and its resize grip entirely, and the content takes
// the window. The state is saved in the snapshot (SidebarCollapsed); SidebarWidth is kept while folded,
// so unfolding brings the sidebar back at the width it had.
public sealed partial class MainWindow
{
    /// <summary>Segoe Fluent Icons DockLeft, the counterpart of the Mac's <c>sidebar.left</c>.</summary>
    internal const string SidebarToggleGlyph = "\uE90C";
    internal const string SidebarToggleShortcut = "Ctrl+B";
    /// <summary>The sidebar buttons in the tree now; each one's name and tooltip follow the fold.</summary>
    private readonly HashSet<Button> sidebarToggles = [];
    /// <summary>The sidebar's resize grip, hidden with the sidebar.</summary>
    private ResizeCursorHost? sidebarGripHost;

    /// <summary>
    /// A sidebar button, built the way the workspace header's files button is: the glyph at 13 in
    /// <c>ink2</c> on a clear 26×24 button. The workspace header keeps one; the dashboard and the
    /// welcome draw theirs again with themselves.
    /// </summary>
    private Button NewSidebarToggle(string automationId)
    {
        var button = new Button
        {
            Width = 26, Height = 24, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Background = brushes.Transparent, Foreground = brushes.Brush(DesignToken.Ink2), VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = SidebarToggleGlyph, FontSize = 13 },
        };
        AutomationProperties.SetAutomationId(button, automationId);
        button.Click += async (_, _) => { if (!dialogOpen) await ToggleSidebar(); };
        // The workspace header's rename menu is for its name and blank space, not this button.
        button.ContextRequested += (_, args) => args.Handled = true;
        button.Loaded += (_, _) => { sidebarToggles.Add(button); RefreshSidebarToggle(button); };
        button.Unloaded += (_, _) => { if (!button.IsLoaded) sidebarToggles.Remove(button); };
        RefreshSidebarToggle(button);
        return button;
    }

    /// <summary>"Collapse sidebar" while it shows, "Expand sidebar" while it is folded, in the language.</summary>
    private void RefreshSidebarToggle(Button button)
    {
        var label = Locale.Get(service.Snapshot.SidebarCollapsed ? "sidebar.expand" : "sidebar.collapse");
        if (AutomationProperties.GetName(button) != label) AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label + " (" + SidebarToggleShortcut + ")");
    }

    private void RefreshSidebarToggles()
    {
        foreach (var button in sidebarToggles) RefreshSidebarToggle(button);
    }

    /// <summary>Lays the sidebar column out for the saved fold: hidden at width 0, or shown at the saved width.</summary>
    private void ApplySidebarCollapsed()
    {
        var state = service.Snapshot; var column = root.ColumnDefinitions[0];
        column.MinWidth = state.SidebarCollapsed ? 0 : DesignMetrics.Layout.SidebarMin;
        column.Width = new GridLength(state.SidebarCollapsed ? 0 : state.SidebarWidth);
        sidebarSurface.Visibility = state.SidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        if (sidebarGripHost is { } grip) grip.Visibility = sidebarSurface.Visibility;
        RefreshSidebarToggles();
    }

    /// <summary>Folds the sidebar away or brings it back, and saves that.</summary>
    private Task ToggleSidebar() => Act(() => SetSidebarCollapsed(!service.Snapshot.SidebarCollapsed));

    /// <summary>
    /// Folds or unfolds the sidebar now: the snapshot and the layout change at once, and the returned
    /// task is the save. Keyboard focus inside a sidebar that folds moves to the visible sidebar button.
    /// </summary>
    private Task SetSidebarCollapsed(bool collapsed)
    {
        if (service.Snapshot.SidebarCollapsed == collapsed) return Task.CompletedTask;
        var focusInSidebar = collapsed && root.XamlRoot is { } xamlRoot && FocusManager.GetFocusedElement(xamlRoot) is DependencyObject focused && IsInsideSidebar(focused);
        // Focus moves before the fold: once the focused element collapses, WinUI hands focus to the
        // next control on its own (the status bar), after any move made here.
        if (focusInSidebar) FocusShownSidebarToggle();
        var save = service.UpdateAsync(s => s with { SidebarCollapsed = collapsed });
        ApplySidebarCollapsed();
        // WinUI settles focus after the layout pass; if it still moved off, put it back once that is done.
        if (focusInSidebar) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (root.XamlRoot is { } settled && FocusManager.GetFocusedElement(settled) is Button focused && sidebarToggles.Contains(focused)) return;
            FocusShownSidebarToggle();
        });
        return save;
    }

    /// <summary>
    /// Puts keyboard focus on a sidebar button that is really on screen: loaded, outside the sidebar,
    /// and visible all the way up to the window (a button in a hidden header can keep its last size).
    /// </summary>
    private bool FocusShownSidebarToggle()
    {
        foreach (var toggle in sidebarToggles.Where(b => b.IsLoaded && !IsInsideSidebar(b) && IsShownToRoot(b)))
            if (toggle.Focus(FocusState.Keyboard)) return true;
        return false;
    }

    /// <summary>Whether the element and every ancestor up to the window's content are visible.</summary>
    private bool IsShownToRoot(UIElement element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return false;
            if (ReferenceEquals(node, root)) return true;
        }
        return false;
    }

    /// <summary>Whether the element stands in the sidebar's visual tree.</summary>
    private bool IsInsideSidebar(DependencyObject element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, sidebarSurface)) return true;
        return false;
    }

    internal bool HasSidebarToggleShortcut => root.KeyboardAccelerators.Any(a => a.Key == Windows.System.VirtualKey.B && a.Modifiers == Windows.System.VirtualKeyModifiers.Control);
}
