using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>
    /// The sidebar fold (MainWindow.SidebarToggle.cs): the workspace header's button folds the sidebar to
    /// nothing and the content takes its width; the dashboard's brings it back at the width it had; the
    /// state is saved; every button's name follows the fold; Ctrl+B is bound. The window is put back as it was.
    /// </summary>
    private async Task<Dictionary<string, object?>> RunSidebarToggleSmoke()
    {
        var original = service.Snapshot; var wasDashboard = showsDashboard;
        const double width = 300;
        var collapse = Locale.Get("sidebar.collapse"); var expand = Locale.Get("sidebar.expand");
        // Renders rebuild the dashboard and the welcome, so each button is looked up again after every wait.
        Button? Find(string id) => VisualChildren(root).OfType<Button>().FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == id && b.IsLoaded && b.ActualWidth > 0 && b.ActualHeight > 0);
        async Task<Button> Shown(string id)
        {
            await WaitUI(() => Find(id) is not null, () => $"the sidebar button '{id}' never showed (header {workspaceHeader.Visibility}, dashboard {dashboard?.Visibility})");
            return Find(id)!;
        }
        static void Press(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        async Task<AppSnapshot> Saved(bool collapsed)
        {
            var stored = await new StateStore(StateDirectory).LoadAsync();
            for (var tries = 0; stored.SidebarCollapsed != collapsed && tries < 100; tries++) { await Task.Delay(40); stored = await new StateStore(StateDirectory).LoadAsync(); }
            return stored;
        }
        try
        {
            Require(HasSidebarToggleShortcut, "Ctrl+B must be bound to the sidebar fold on the window root; no such accelerator was found");
            if (showsDashboard) HideDashboard();
            await service.UpdateAsync(s => s with { SidebarCollapsed = false, SidebarWidth = width, ActiveWorkspaceId = s.ActiveWorkspaceId ?? s.Workspaces.FirstOrDefault()?.Id }); Render();
            var header = await Shown("sidebar-toggle"); root.UpdateLayout();
            var openSidebar = root.ColumnDefinitions[0].ActualWidth; var openContent = detailTop.ActualWidth;
            Require(Math.Abs(openSidebar - width) < 1 && sidebarSurface.Visibility == Visibility.Visible,
                $"the open sidebar must be {width} wide and shown before the fold; got {openSidebar:F1}, {sidebarSurface.Visibility}");
            Require(AutomationProperties.GetName(header) == collapse && Equals(ToolTipService.GetToolTip(header), collapse + " (" + SidebarToggleShortcut + ")"),
                $"the open sidebar's button must be named '{collapse}' with the tooltip '{collapse} ({SidebarToggleShortcut})'; got '{AutomationProperties.GetName(header)}' / '{ToolTipService.GetToolTip(header)}'");

            Press(header);
            await WaitUI(() => service.Snapshot.SidebarCollapsed && root.ColumnDefinitions[0].ActualWidth < 0.5 && sidebarSurface.Visibility == Visibility.Collapsed,
                () => $"the header button must fold the sidebar to width 0 and hide it; got collapsed={service.Snapshot.SidebarCollapsed}, width {root.ColumnDefinitions[0].ActualWidth:F1}, {sidebarSurface.Visibility}");
            root.UpdateLayout();
            Require(Math.Abs(detailTop.ActualWidth - (openContent + openSidebar)) < 1.5 && Math.Abs(detailTop.ActualWidth - root.ActualWidth) < 1.5,
                $"the content must take the sidebar's {openSidebar:F1}: expected {openContent + openSidebar:F1} (the window's {root.ActualWidth:F1}); got {detailTop.ActualWidth:F1}");
            Require(sidebarGripHost?.Visibility == Visibility.Collapsed, $"the sidebar's resize grip must hide with it; got {sidebarGripHost?.Visibility.ToString() ?? "no grip"}");
            Require(service.Snapshot.SidebarWidth == width, $"folding must keep the saved width {width}; got {service.Snapshot.SidebarWidth}");
            header = await Shown("sidebar-toggle");
            Require(AutomationProperties.GetName(header) == expand, $"the folded sidebar's button must be named '{expand}'; got '{AutomationProperties.GetName(header)}'");
            var stored = await Saved(true);
            Require(stored.SidebarCollapsed && stored.SidebarWidth == width, $"the fold must be saved with the width: expected collapsed=True width={width}; got collapsed={stored.SidebarCollapsed} width={stored.SidebarWidth}");
            var welcome = BuildWelcome().Children.OfType<Button>().FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "welcome-sidebar-toggle");
            Require(welcome is not null && AutomationProperties.GetName(welcome) == expand, $"the welcome's sidebar button must be built named '{expand}' while folded; got '{(welcome is null ? "no button" : AutomationProperties.GetName(welcome))}'");

            // From the dashboard: its own button, drawn with it, unfolds the sidebar at the width it had.
            showsDashboard = true; RenderSidebar(); RenderDashboard();
            var fromDashboard = await Shown("dashboard-sidebar-toggle");
            Require(AutomationProperties.GetName(fromDashboard) == expand, $"the dashboard's sidebar button must be named '{expand}' while folded; got '{AutomationProperties.GetName(fromDashboard)}'");
            Require(dashboard is not null && Math.Abs(dashboard.ActualWidth - root.ActualWidth) < 1.5, $"the dashboard must fill the window while folded: expected {root.ActualWidth:F1}; got {dashboard?.ActualWidth:F1}");
            Press(fromDashboard);
            await WaitUI(() => !service.Snapshot.SidebarCollapsed && Math.Abs(root.ColumnDefinitions[0].ActualWidth - width) < 1 && sidebarSurface.Visibility == Visibility.Visible,
                () => $"the dashboard's button must bring the sidebar back {width} wide; got collapsed={service.Snapshot.SidebarCollapsed}, width {root.ColumnDefinitions[0].ActualWidth:F1}, {sidebarSurface.Visibility}");
            Require(sidebarGripHost?.Visibility == Visibility.Visible && root.ColumnDefinitions[0].MinWidth == DesignMetrics.Layout.SidebarMin,
                $"the unfolded sidebar must have its grip and its minimum {DesignMetrics.Layout.SidebarMin} back; got {sidebarGripHost?.Visibility.ToString() ?? "no grip"}, minimum {root.ColumnDefinitions[0].MinWidth}");
            fromDashboard = await Shown("dashboard-sidebar-toggle");
            Require(AutomationProperties.GetName(fromDashboard) == collapse, $"the dashboard's sidebar button must be named '{collapse}' once unfolded; got '{AutomationProperties.GetName(fromDashboard)}'");
            stored = await Saved(false);
            Require(!stored.SidebarCollapsed && stored.SidebarWidth == width, $"the unfold must be saved: expected collapsed=False width={width}; got collapsed={stored.SidebarCollapsed} width={stored.SidebarWidth}");

            // Folding with the keyboard in the sidebar hands focus to the sidebar button that stays on screen.
            await WaitUI(() => search.Focus(FocusState.Keyboard) && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root.XamlRoot!) is DependencyObject inSidebar && IsInsideSidebar(inSidebar),
                () => $"the sidebar search never took keyboard focus; focus is on {Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root.XamlRoot!)?.GetType().Name ?? "nothing"}");
            await ToggleSidebar();
            root.UpdateLayout();
            object? focusedAfter = null;
            await WaitUI(() => (focusedAfter = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root.XamlRoot!)) is Button { ActualWidth: > 0 } shown && sidebarToggles.Contains(shown),
                () => $"folding with focus in the search must move focus to the shown sidebar button ({string.Join(", ", sidebarToggles.Select(t => AutomationProperties.GetAutomationId(t) + (IsShownToRoot(t) ? " shown" : " hidden")))}); focus is on {(focusedAfter is FrameworkElement e ? e.GetType().Name + " '" + AutomationProperties.GetAutomationId(e) + "'" : focusedAfter?.GetType().Name ?? "nothing")}");
            Require(service.Snapshot.SidebarCollapsed && focusedAfter is Button { ActualWidth: > 0 } focusedToggle && sidebarToggles.Contains(focusedToggle),
                $"folding with focus in the search must move focus to the shown sidebar button; got collapsed={service.Snapshot.SidebarCollapsed}, focus on {(focusedAfter is FrameworkElement f ? f.GetType().Name + " '" + AutomationProperties.GetAutomationId(f) + "'" : focusedAfter?.GetType().Name ?? "nothing")}");
            await ToggleSidebar();
            await WaitUI(() => !service.Snapshot.SidebarCollapsed && sidebarSurface.Visibility == Visibility.Visible, () => $"the sidebar must unfold again after the focus check; got collapsed={service.Snapshot.SidebarCollapsed}, {sidebarSurface.Visibility}");

            // The grip, through the handlers its Thumb calls (no mouse): a drag inside the bounds only moves the
            // column; one past the fold threshold folds the sidebar, and neither it nor the release saves a width,
            // so the width from before the drag comes back with the unfold.
            var column = root.ColumnDefinitions[0];
            await SidebarGripDragged(-20);
            Require(!service.Snapshot.SidebarCollapsed && Math.Abs(column.Width.Value - (width - 20)) < 0.5 && service.Snapshot.SidebarWidth == width,
                $"a grip drag inside the bounds must move the column to {width - 20} without saving; got collapsed={service.Snapshot.SidebarCollapsed}, column {column.Width.Value:F1}, saved {service.Snapshot.SidebarWidth}");
            await SidebarGripDragged(DesignMetrics.Layout.SidebarFoldThreshold - column.Width.Value - 1);
            await SidebarGripReleased(canceled: false);
            root.UpdateLayout();
            Require(service.Snapshot.SidebarCollapsed && sidebarSurface.Visibility == Visibility.Collapsed && sidebarGripHost?.Visibility == Visibility.Collapsed && column.ActualWidth < 0.5,
                $"a grip drag under the fold threshold {DesignMetrics.Layout.SidebarFoldThreshold} must fold the sidebar and its grip; got collapsed={service.Snapshot.SidebarCollapsed}, {sidebarSurface.Visibility}, grip {sidebarGripHost?.Visibility.ToString() ?? "none"}, width {column.ActualWidth:F1}");
            Require(service.Snapshot.SidebarWidth == width, $"folding by drag must keep the width from before the drag, {width}; got {service.Snapshot.SidebarWidth}");
            stored = await Saved(true);
            Require(stored.SidebarCollapsed && stored.SidebarWidth == width, $"the fold by drag must be saved with the earlier width: expected collapsed=True width={width}; got collapsed={stored.SidebarCollapsed} width={stored.SidebarWidth}");
            await ToggleSidebar();
            await WaitUI(() => !service.Snapshot.SidebarCollapsed && Math.Abs(column.ActualWidth - width) < 1,
                () => $"unfolding after a fold by drag must bring the sidebar back {width} wide; got collapsed={service.Snapshot.SidebarCollapsed}, width {column.ActualWidth:F1}");
            return new() { ["shortcutBound"] = true, ["toggleCollapses"] = true, ["contentTakesSpace"] = true, ["toggleRestoresWidth"] = true, ["statePersists"] = true, ["automationNameFollows"] = true, ["focusLeavesFoldedSidebar"] = true, ["foldByDragKeepsWidth"] = true };
        }
        finally
        {
            await service.UpdateAsync(_ => original);
            if (wasDashboard) { showsDashboard = true; RenderSidebar(); RenderDashboard(); } else HideDashboard();
            Render();
        }
    }
}
