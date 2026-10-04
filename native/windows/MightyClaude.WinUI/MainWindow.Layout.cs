using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private const string PaneDragFormat = "dev.mightyclaude.pane-tab";
    // Keep generated legacy layouts stable until the first user layout action.
    private readonly Dictionary<string, PaneLayoutNode> layoutDefaults = [];
    // Own each attachment explicitly: FrameworkElement.Parent can be unavailable
    // while a generated tab/split tree is being detached from the visual tree.
    private readonly Dictionary<string, Border> paneHosts = [];
    private string? draggedSessionId, draggedWorkspaceId;

    private static string LayoutMode(AppSnapshot state, string? workspace) => workspace is not null && state.PaneLayoutModes?.TryGetValue(workspace, out var mode) == true ? mode : state.Layout;
    private static AppSnapshot SaveLayoutMode(AppSnapshot state, string workspace, string mode)
    { var modes = new Dictionary<string, string>(state.PaneLayoutModes ?? []); modes[workspace] = mode; return state with { PaneLayoutModes = modes }; }
    private static AppSnapshot SaveLayoutSelection(AppSnapshot state, string workspace, string id)
    { var selections = new Dictionary<string, string>(state.PaneLayoutActiveSessionIds ?? []); selections[workspace] = id; return state with { PaneLayoutActiveSessionIds = selections }; }

    private PaneLayoutNode? EffectiveLayout(AppSnapshot state, string workspaceId)
    {
        var ids = state.Sessions.Where(s => s.WorkspaceId == workspaceId).Select(s => s.Id).ToArray();
        PaneLayoutNode? node = null;
        if (state.PaneLayouts?.TryGetValue(workspaceId, out node) != true) layoutDefaults.TryGetValue(workspaceId, out node);
        node ??= PaneLayout.Preset(ids, LayoutMode(state, workspaceId), state.ActiveWorkspaceId == workspaceId ? state.ActiveSessionId : state.PaneLayoutActiveSessionIds?.GetValueOrDefault(workspaceId));
        node = PaneLayout.Normalize(node, ids, state.ActiveWorkspaceId == workspaceId ? state.ActiveSessionId : null);
        if (node is not null) layoutDefaults[workspaceId] = node;
        return node;
    }

    private Task ApplyLayoutPreset(string preset) => Act(async () =>
    {
        await service.UpdateAsync(state =>
        {
            if (state.ActiveWorkspaceId is not { } workspace) return state with { Layout = preset };
            // Focus changes only the viewport. Returning to a split preset or
            // custom must not silently discard the workspace's saved tree.
            var node = preset is "custom" or "focus" ? EffectiveLayout(state, workspace) : PaneLayout.Preset(state.Sessions.Where(s => s.WorkspaceId == workspace).Select(s => s.Id), preset, state.ActiveSessionId);
            return SaveLayoutMode(SaveLayout(state, workspace, node), workspace, preset);
        });
        Render();
    });

    private static AppSnapshot SaveLayout(AppSnapshot state, string workspace, PaneLayoutNode? node)
    {
        var layouts = new Dictionary<string, PaneLayoutNode>(state.PaneLayouts ?? []);
        if (node is null) layouts.Remove(workspace); else layouts[workspace] = node;
        return state with { PaneLayouts = layouts };
    }

    private Task SelectLayoutSession(string sessionId) => Act(async () =>
    {
        HideDashboard();
        await service.UpdateAsync(state =>
        {
            var session = state.Sessions.FirstOrDefault(s => s.Id == sessionId);
            if (session is null) return state;
            var node = EffectiveLayout(state, session.WorkspaceId);
            return SaveLayoutSelection(SaveLayout(state, session.WorkspaceId, node is null ? null : PaneLayout.Select(node, sessionId)), session.WorkspaceId, sessionId) with { ActiveWorkspaceId = session.WorkspaceId, ActiveSessionId = sessionId };
        });
        Render();
    });

    private Task DockSession(string sessionId, string workspaceId, string groupId, string edge, int? index = null) => Act(async () =>
    {
        await service.UpdateAsync(state =>
        {
            if (state.ActiveWorkspaceId != workspaceId || !state.Sessions.Any(s => s.Id == sessionId && s.WorkspaceId == workspaceId)) return state;
            var node = EffectiveLayout(state, workspaceId); if (node is null || !PaneLayout.Groups(node).Any(g => g.Id == groupId)) return state;
            return SaveLayoutSelection(SaveLayoutMode(SaveLayout(state, workspaceId, PaneLayout.Move(node, sessionId, groupId, edge, index)), workspaceId, "custom"), workspaceId, sessionId) with { ActiveSessionId = sessionId };
        });
        Render();
    });

    private void DetachPaneViews()
    {
        // Clear the actual owner before discarding the old layout. Removing the
        // outer viewport alone leaves its descendants parented to the old tree.
        foreach (var host in paneHosts.Values) host.Child = null;
        paneHosts.Clear();
        foreach (var view in views.Values)
        {
            // PaneView owns draft text, selection and attachment objects. Move
            // its container instead of recreating it when tabs or splits change.
            Grid.SetRow(view.Container, 0); Grid.SetColumn(view.Container, 0);
        }
    }

    private void RenderPaneLayout(AppSnapshot state)
    {
        tabIndicators.Clear(); tabBetas.Clear(); tabStrips.Clear(); tabCells.Clear(); tabTitles.Clear();
        foreach (var stale in layoutDefaults.Keys.Where(id => !state.Workspaces.Any(w => w.Id == id)).ToArray()) layoutDefaults.Remove(stale);
        if (state.ActiveWorkspaceId is not { } workspace || EffectiveLayout(state, workspace) is not { } node)
        {
            // No project: the welcome; a project with no panes: the add-pane invitation (MainWindow.EmptyStates.cs).
            panes.Children.Add(state.Workspaces.Count == 0 ? BuildWelcome() : BuildEmptyPanes(state.ActiveWorkspaceId is not null));
            return;
        }
        if (LayoutMode(state, workspace) == "focus") node = PaneLayout.Groups(node).FirstOrDefault(g => g.SessionIds.Contains(state.ActiveSessionId ?? "")) ?? PaneLayout.Groups(node).First();
        var content = BuildLayoutNode(node, workspace, state);
        var minimum = LayoutMinimum(node);
        // The dock sits DockInset inside its scroll view on every side (M/PaneDockView.swift:62-63).
        const double inset = DesignMetrics.Layout.DockInset;
        content.Margin = new Thickness(inset);
        content.Width = Math.Max(minimum.Width, (panes.ActualWidth > 0 ? panes.ActualWidth : 900) - 2 * inset); content.Height = Math.Max(minimum.Height, (panes.ActualHeight > 0 ? panes.ActualHeight : 650) - 2 * inset);
        var viewport = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto, VerticalScrollMode = ScrollMode.Auto };
        viewport.SizeChanged += (_, args) => { content.Width = Math.Max(minimum.Width, args.NewSize.Width - 2 * inset); content.Height = Math.Max(minimum.Height, args.NewSize.Height - 2 * inset); };
        panes.Children.Add(viewport);
    }

    private static Windows.Foundation.Size LayoutMinimum(PaneLayoutNode node)
    {
        if (node.Kind == "tabs") return new(DesignMetrics.Layout.PaneMinWidth, DesignMetrics.Layout.PaneMinHeight);
        var a = LayoutMinimum(node.Children[0]); var b = LayoutMinimum(node.Children[1]);
        return node.Axis == "horizontal" ? new(a.Width + b.Width + DesignMetrics.Layout.SplitDivider, Math.Max(a.Height, b.Height)) : new(Math.Max(a.Width, b.Width), a.Height + b.Height + DesignMetrics.Layout.SplitDivider);
    }

    private FrameworkElement BuildLayoutNode(PaneLayoutNode node, string workspace, AppSnapshot state)
    {
        if (node.Kind == "tabs") return BuildTabGroup(node, workspace, state);
        var horizontal = node.Axis == "horizontal"; var grid = new Grid(); var ratio = node.Ratio;
        if (horizontal)
        {
            grid.ColumnDefinitions.Add(new() { Width = new(ratio, GridUnitType.Star), MinWidth = LayoutMinimum(node.Children[0]).Width }); grid.ColumnDefinitions.Add(new() { Width = new(DesignMetrics.Layout.SplitDivider) }); grid.ColumnDefinitions.Add(new() { Width = new(1 - ratio, GridUnitType.Star), MinWidth = LayoutMinimum(node.Children[1]).Width });
        }
        else
        {
            grid.RowDefinitions.Add(new() { Height = new(ratio, GridUnitType.Star), MinHeight = LayoutMinimum(node.Children[0]).Height }); grid.RowDefinitions.Add(new() { Height = new(DesignMetrics.Layout.SplitDivider) }); grid.RowDefinitions.Add(new() { Height = new(1 - ratio, GridUnitType.Star), MinHeight = LayoutMinimum(node.Children[1]).Height });
        }
        var first = BuildLayoutNode(node.Children[0], workspace, state); var second = BuildLayoutNode(node.Children[1], workspace, state);
        if (horizontal) Grid.SetColumn(second, 2); else Grid.SetRow(second, 2);
        grid.Children.Add(first); grid.Children.Add(second);
        // The Thumb is the invisible hit area (Opacity 0 still takes the pointer and hides the stock
        // grey hover bar); the handle drawn over it is line, accent while hovered or dragged.
        var divider = new Thumb { Background = new SolidColorBrush(Colors.Transparent), Opacity = 0, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        var handle = new Border { Width = horizontal ? 3 : 30, Height = horizontal ? 30 : 3, CornerRadius = new CornerRadius(2), Background = brushes.Brush(DesignToken.Line), IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(handle, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        divider.PointerEntered += (_, _) => handle.Background = brushes.Brush(DesignToken.Accent);
        divider.PointerExited += (_, _) => { if (!divider.IsDragging) handle.Background = brushes.Brush(DesignToken.Line); };
        AutomationProperties.SetName(divider, Locale.Get(horizontal ? "layout.divider.horizontal" : "layout.divider.vertical")); ToolTipService.SetToolTip(divider, Locale.Get("layout.divider.tooltip"));
        var dividerHost = new ResizeCursorHost(divider, horizontal);
        if (horizontal) { Grid.SetColumn(dividerHost, 1); Grid.SetColumn(handle, 1); } else { Grid.SetRow(dividerHost, 1); Grid.SetRow(handle, 1); }
        divider.DragDelta += (_, args) =>
        {
            var size = (horizontal ? grid.ActualWidth : grid.ActualHeight) - DesignMetrics.Layout.SplitDivider; if (size <= 0) return;
            ratio = PaneLayout.ClampRatio(ratio + (horizontal ? args.HorizontalChange : args.VerticalChange) / size);
            if (horizontal) { grid.ColumnDefinitions[0].Width = new(ratio, GridUnitType.Star); grid.ColumnDefinitions[2].Width = new(1 - ratio, GridUnitType.Star); }
            else { grid.RowDefinitions[0].Height = new(ratio, GridUnitType.Star); grid.RowDefinitions[2].Height = new(1 - ratio, GridUnitType.Star); }
        };
        divider.DragCompleted += async (_, args) => await Act(async () =>
        {
            if (!args.Canceled) await service.UpdateAsync(s => EffectiveLayout(s, workspace) is { } current ? SaveLayoutMode(SaveLayout(s, workspace, PaneLayout.Resize(current, node.Id, ratio)), workspace, "custom") : s);
            Render();
        });
        grid.Children.Add(dividerHost); grid.Children.Add(handle); return grid;
    }

    private bool IsPaneDrag(DragEventArgs args, string workspace) => draggedSessionId is not null && draggedWorkspaceId == workspace && args.DataView.Contains(PaneDragFormat);
    private static string DropEdge(Windows.Foundation.Point position, FrameworkElement view)
    {
        var x = position.X / Math.Max(1, view.ActualWidth); var y = position.Y / Math.Max(1, view.ActualHeight);
        if (x < .2) return "left"; if (x > .8) return "right"; if (y < .2) return "top"; if (y > .8) return "bottom"; return "center";
    }

    /// <summary>
    /// Each tab group's strip and its bottom rule, by group id, and each tab's cell, its drawn shape,
    /// its buttons and title, by session id (read by the paneChromeDesign smoke).
    /// </summary>
    private readonly Dictionary<string, (Grid Strip, Border Rule)> tabStrips = [];
    private readonly Dictionary<string, (Grid Cell, Border Shape, Button Tab, Button Close, TextBlock Title)> tabCells = [];
    /// <summary>The fill of the drop-zone hint while a tab is dragged over a group: accent at 0.18, a Windows-only affordance.</summary>
    private const double DropHintOpacity = 0.18;

    /// <summary>
    /// A tab group is one card (M/PaneDockView.swift:162-193): the 38pt strip on top, the selected
    /// pane under it. The strip (M/PaneDockView.swift:194-216) is the subtle wash with the card's top
    /// corners and a 1pt rule over its bottom edge (an overlay, as on the Mac, so it takes no room from
    /// the tabs), accent x 0.55 on the group holding the active pane.
    /// </summary>
    private FrameworkElement BuildTabGroup(PaneLayoutNode node, string workspace, AppSnapshot state)
    {
        var group = new Grid { AllowDrop = true, Background = brushes.Brush(DesignToken.Card), CornerRadius = new CornerRadius(DesignMetrics.Radius.Pane) };
        group.RowDefinitions.Add(new() { Height = GridLength.Auto }); group.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Padding = new Thickness(5, 4, 5, 4) };
        var bar = new ScrollViewer { Content = tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled };
        var tabHeader = new Grid { ColumnSpacing = 4 }; tabHeader.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); tabHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        tabHeader.Children.Add(bar);
        // The Windows-only add button, drawn as a header icon button.
        var add = new Button { Content = new FontIcon { Glyph = "\uE710", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, Width = 22, Height = 24, MinWidth = 0, MinHeight = 0, Padding = new(0), Margin = new(0, 0, 8, 0), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), VerticalAlignment = VerticalAlignment.Center, Flyout = NewSessionMenu(node.Id) };
        PaintPlainButton(add, brushes.Transparent, brushes.Subtle, ink: brushes.Brush(DesignToken.Ink2));
        AutomationProperties.SetName(add, Locale.Get("layout.group.addPane")); ToolTipService.SetToolTip(add, Locale.Get("layout.group.addPane")); Grid.SetColumn(add, 1); tabHeader.Children.Add(add);
        var holdsActive = state.ActiveSessionId is { } activeId && node.SessionIds.Contains(activeId);
        var strip = new Grid { Height = DesignMetrics.Layout.TabStrip, Background = brushes.Subtle, CornerRadius = new CornerRadius(DesignMetrics.Radius.Pane, DesignMetrics.Radius.Pane, 0, 0) };
        var rule = new Border
        {
            Height = DesignMetrics.Stroke.Line, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false,
            Background = holdsActive ? brushes.Brush(DesignToken.Accent, DesignMetrics.Opacity.TabActiveRule) : brushes.Brush(DesignToken.Line),
        };
        strip.Children.Add(tabHeader); strip.Children.Add(rule);
        AutomationProperties.SetAutomationId(strip, "pane-tab-strip-" + node.Id); tabStrips[node.Id] = (strip, rule);
        group.Children.Add(strip);
        var selected = node.SelectedSessionId ?? node.SessionIds[0];
        foreach (var id in node.SessionIds)
        {
            var session = state.Sessions.First(s => s.Id == id); var isSelected = id == selected;
            // PaneDockTab (M/PaneDockView.swift:219-262): the handle covers the tab up to its close button,
            // l10 r6, at least 56x30; the selected tab is a card with a line border, r6, drawn as a shape
            // under the buttons so the border takes no room from them.
            var tab = Button(session.Title, () => SelectLayoutSession(id)); tab.CanDrag = true; tab.AllowDrop = true; tab.MinWidth = 56; tab.Height = 30; tab.MinHeight = 0; tab.Padding = new(10, 0, 6, 0); tab.CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment);
            tab.HorizontalContentAlignment = HorizontalAlignment.Left; tab.VerticalAlignment = VerticalAlignment.Center;
            PlainSidebarButton(tab, brushes.Transparent, brushes.Transparent);
            tab.Content = TabIndicator(session, isSelected); tab.ContextFlyout = SessionMenu(id);
            tab.DoubleTapped += async (_, args) => { args.Handled = true; await RenameSession(id); };
            ToolTipService.SetToolTip(tab, Locale.Get("layout.tab.dragTooltip"));
            tab.DragStarting += (_, args) => { draggedSessionId = id; draggedWorkspaceId = workspace; args.Data.SetData(PaneDragFormat, id); args.Data.RequestedOperation = DataPackageOperation.Move; };
            tab.DropCompleted += (_, _) => { draggedSessionId = null; draggedWorkspaceId = null; };
            tab.DragOver += (_, args) => { if (!IsPaneDrag(args, workspace)) return; args.AcceptedOperation = DataPackageOperation.Move; args.Handled = true; tab.BorderBrush = brushes.Brush(DesignToken.Accent); tab.BorderThickness = new(DesignMetrics.Stroke.Active); };
            tab.DragLeave += (_, _) => tab.BorderThickness = new(0);
            tab.Drop += async (_, args) =>
            {
                if (!IsPaneDrag(args, workspace)) return;
                args.Handled = true; var moved = draggedSessionId!; var index = node.SessionIds.IndexOf(id) + (args.GetPosition(tab).X > tab.ActualWidth / 2 ? 1 : 0);
                await DockSession(moved, workspace, node.Id, "center", index);
            };
            var close = Button("×", () => CloseSession(id)); close.Content = new FontIcon { Glyph = "\uE711", FontSize = 8, FontWeight = Microsoft.UI.Text.FontWeights.Medium }; close.MinWidth = 0; close.MinHeight = 0; close.Width = 20; close.Height = 30; close.Padding = new(0); close.CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment);
            close.Foreground = brushes.Brush(DesignToken.Ink2); PlainSidebarButton(close, brushes.Transparent, brushes.Transparent);
            AutomationProperties.SetName(close, Locale.Get("layout.tab.closeAccessibility", new Dictionary<string, string> { ["title"] = session.Title }));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Padding = new Thickness(0, 0, 2, 0) }; row.Children.Add(tab); row.Children.Add(close);
            var shape = new Border
            {
                CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), BorderThickness = new Thickness(DesignMetrics.Stroke.Line), IsHitTestVisible = false,
                Background = isSelected ? brushes.Brush(DesignToken.Card) : brushes.Transparent, BorderBrush = isSelected ? brushes.Brush(DesignToken.Line) : brushes.Transparent,
            };
            var tabCell = new Grid { Height = 30, VerticalAlignment = VerticalAlignment.Center }; tabCell.Children.Add(shape); tabCell.Children.Add(row);
            AutomationProperties.SetAutomationId(tabCell, "pane-tab-" + id);
            tabCells[id] = (tabCell, shape, tab, close, tabTitles[id]);
            tabs.Children.Add(tabCell);
        }
        if (!views.TryGetValue(selected, out var pane)) { pane = new(this, selected); pane.InitRefresher(); views[selected] = pane; }
        if (paneHosts.ContainsKey(selected)) throw new InvalidOperationException(Locale.Get("layout.error.paneInTwoGroups"));
        var paneHost = new Border(); paneHosts.Add(selected, paneHost);
        paneHost.Child = pane.Container; paneHost.ContextFlyout = SessionMenu(selected); Grid.SetRow(paneHost, 1); group.Children.Add(paneHost); pane.Refresh();
        var hint = new Border { Background = brushes.Brush(DesignToken.Accent, DropHintOpacity), BorderBrush = brushes.Brush(DesignToken.Accent), BorderThickness = new(DesignMetrics.Stroke.Active), CornerRadius = new CornerRadius(DesignMetrics.Radius.Pane), IsHitTestVisible = false, Visibility = Visibility.Collapsed, Child = new TextBlock { Text = Locale.Get("layout.drop.merge"), Foreground = brushes.Brush(DesignToken.Ink), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        Grid.SetRowSpan(hint, 2); group.Children.Add(hint);
        group.DragOver += (_, args) =>
        {
            if (!IsPaneDrag(args, workspace)) return;
            args.Handled = true; args.AcceptedOperation = DataPackageOperation.Move;
            var edge = DropEdge(args.GetPosition(group), group); hint.Visibility = Visibility.Visible;
            hint.Width = edge is "left" or "right" ? group.ActualWidth / 2 : double.NaN; hint.Height = edge is "top" or "bottom" ? group.ActualHeight / 2 : double.NaN;
            hint.HorizontalAlignment = edge == "left" ? HorizontalAlignment.Left : edge == "right" ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            hint.VerticalAlignment = edge == "top" ? VerticalAlignment.Top : edge == "bottom" ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
            ((TextBlock)hint.Child).Text = Locale.Get(edge switch { "left" => "layout.drop.left", "right" => "layout.drop.right", "top" => "layout.drop.top", "bottom" => "layout.drop.bottom", _ => "layout.drop.merge" });
        };
        group.DragLeave += (_, _) => hint.Visibility = Visibility.Collapsed;
        group.Drop += async (_, args) =>
        {
            if (!IsPaneDrag(args, workspace)) return;
            args.Handled = true; hint.Visibility = Visibility.Collapsed;
            await DockSession(draggedSessionId!, workspace, node.Id, DropEdge(args.GetPosition(group), group));
        };
        return group;
    }
}
