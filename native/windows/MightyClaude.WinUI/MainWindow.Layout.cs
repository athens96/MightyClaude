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

    /// <summary>The tab group holding the workspace's active pane: where a new pane joins, as on the Mac (M/AppStore.swift:520).</summary>
    private string? ActiveGroupId(AppSnapshot state, string workspace) => state.ActiveSessionId is { } active
        ? PaneLayout.Groups(EffectiveLayout(state, workspace)).FirstOrDefault(g => g.SessionIds.Contains(active))?.Id : null;

    /// <summary>
    /// The state with a new pane in it: in <paramref name="groupId"/>, else in the active pane's group, shown there
    /// and made the active pane. It joins the tree before it becomes the active pane: as the active pane it would
    /// first be shown in the first group, and taking it out again would reset the tab that group was showing.
    /// </summary>
    private AppSnapshot AddToLayout(AppSnapshot state, string workspace, RunSession pane, string? groupId)
    {
        var home = groupId ?? ActiveGroupId(state, workspace);
        var joined = state with { Sessions = state.Sessions.Append(pane).ToList() };
        var tree = EffectiveLayout(joined, workspace);
        if (tree is not null) tree = home is not null ? PaneLayout.Move(tree, pane.Id, home) : PaneLayout.Select(tree, pane.Id);
        return SaveLayoutSelection(SaveLayout(joined with { ActiveSessionId = pane.Id }, workspace, tree), workspace, pane.Id);
    }

    /// <summary>
    /// A press inside a pane makes it the active one, as on the Mac (M/SessionPaneView.swift:170-190): the
    /// accent outline, its group's strip rule and the sidebar selection move to it. Only the sidebar rows are
    /// drawn again, so the press itself, the focus and a text selection it starts are left alone. The state
    /// changes with the press and is drawn at once; the save follows.
    /// </summary>
    private async void ActivatePane(string sessionId)
    {
        if (rendering || closing || showsDashboard || service.ActiveSessionId == sessionId) return;
        try
        {
            var saving = service.UpdateAsync(state => state.Sessions.FirstOrDefault(s => s.Id == sessionId) is { } session && session.WorkspaceId == state.ActiveWorkspaceId
                ? SaveLayoutSelection(state, session.WorkspaceId, sessionId) with { ActiveSessionId = sessionId } : state);
            var current = service.Snapshot;
            if (current.ActiveSessionId == sessionId && current.ActiveWorkspaceId is { } workspace)
            {
                foreach (var (id, view) in views) view.ShowActive(id == sessionId);
                foreach (var group in PaneLayout.Groups(EffectiveLayout(current, workspace)))
                    if (tabStrips.TryGetValue(group.Id, out var strip))
                        strip.Rule.Background = group.SessionIds.Contains(sessionId) ? brushes.Brush(DesignToken.Accent, DesignMetrics.Opacity.TabActiveRule) : brushes.Brush(DesignToken.Line);
                RenderSidebar(); mobileRouter?.Changed();
            }
            await saving;
        }
        catch (Exception ex) { error.Text = ex.Message; }
    }

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
        tabIndicators.Clear(); tabBetas.Clear(); tabStrips.Clear(); tabCells.Clear(); tabTitles.Clear(); groupSlimHeaders.Clear(); splitDividers.Clear(); dropHints.Clear();
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
        // The dock sits Inset.Dock inside its scroll view on every side (M/PaneDockView.swift).
        const double inset = DesignMetrics.Inset.Dock;
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
        var dragged = false; var travelled = 0.0;
        divider.DragStarted += (_, _) => travelled = 0;
        divider.DragDelta += (_, args) =>
        {
            var size = (horizontal ? grid.ActualWidth : grid.ActualHeight) - DesignMetrics.Layout.SplitDivider; if (size <= 0) return;
            var change = horizontal ? args.HorizontalChange : args.VerticalChange; if (change == 0) return;
            // A press that wobbles a pixel or two is still a click: it moves nothing, so the second press of a double-click finds the same divider.
            if (!dragged) { travelled += change; if (Math.Abs(travelled) < DividerSlack) return; change = travelled; }
            dragged = true;
            ratio = PaneLayout.ClampRatio(ratio + change / size);
            if (horizontal) { grid.ColumnDefinitions[0].Width = new(ratio, GridUnitType.Star); grid.ColumnDefinitions[2].Width = new(1 - ratio, GridUnitType.Star); }
            else { grid.RowDefinitions[0].Height = new(ratio, GridUnitType.Star); grid.RowDefinitions[2].Height = new(1 - ratio, GridUnitType.Star); }
        };
        divider.DragCompleted += async (_, args) =>
        {
            // A press that moved nothing saves nothing and leaves the divider in place, so a second press can make it a double-click.
            if (!dragged) return;
            dragged = false;
            await Act(async () =>
            {
                if (!args.Canceled) await service.UpdateAsync(s => EffectiveLayout(s, workspace) is { } current ? SaveLayoutMode(SaveLayout(s, workspace, PaneLayout.Resize(current, node.Id, ratio)), workspace, "custom") : s);
                Render();
            });
        };
        divider.DoubleTapped += async (_, args) => { args.Handled = true; await ResetSplit(workspace, node.Id); };
        splitDividers[node.Id] = (divider, handle);
        grid.Children.Add(dividerHost); grid.Children.Add(handle); return grid;
    }

    /// <summary>How far a press on a divider may move before it becomes a drag.</summary>
    private const double DividerSlack = 3;

    /// <summary>A double-click on a split's divider shares the room evenly again (M/PaneDockView.swift:146).</summary>
    private Task ResetSplit(string workspace, string splitId) => Act(async () =>
    {
        await service.UpdateAsync(s => EffectiveLayout(s, workspace) is { } current ? SaveLayoutMode(SaveLayout(s, workspace, PaneLayout.Resize(current, splitId, .5)), workspace, "custom") : s);
        Render();
    });

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
    /// <summary>The drop preview while a tab is dragged over a group: accent at 0.16 under the zone's words on page at 0.95 (M/PaneDockDrag.swift:138, 143).</summary>
    private const double DropHintOpacity = 0.16, DropHintLabelOpacity = 0.95;
    /// <summary>The drop preview's margin inside its tab group.</summary>
    internal const double DropHintInset = DesignMetrics.Spacing.Xs;
    /// <summary>
    /// Each split's divider and the handle drawn over it, by split id, and each tab group's drop preview with
    /// the call that shows it for a zone, by group id (read by the paletteDesign smoke).
    /// </summary>
    private readonly Dictionary<string, (Thumb Divider, Border Handle)> splitDividers = [];
    private readonly Dictionary<string, (Grid Hint, Microsoft.UI.Xaml.Shapes.Rectangle Edge, Border Label, TextBlock Words, Action<string> Show)> dropHints = [];

    /// <summary>
    /// The header menus' focus item (M/SessionPaneView.swift:283, M/AppStore+PaneLayouts.swift:88-91): makes
    /// the pane the active one and shows only its group, or goes back to the layout that was left.
    /// </summary>
    private Task TogglePaneFocus(string id) => Act(async () =>
    {
        await SelectLayoutSession(id);
        await ApplyLayoutPreset(LayoutMode(service.Snapshot, service.Snapshot.ActiveWorkspaceId) == "focus" ? "custom" : "focus");
    });

    /// <summary>A tab's own menu (M/PaneDockDrag.swift:339-349): rename, focus view, close the tab.</summary>
    private MenuFlyout TabMenu(string id)
    {
        var rename = MenuItem(Locale.Get("menu.rename"), () => RenameSession(id));
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => rename.IsEnabled = !dialogOpen;
        menu.Items.Add(rename);
        menu.Items.Add(MenuItem(Locale.Get("menu.focusPane"), () => Act(async () => { await SelectLayoutSession(id); await ApplyLayoutPreset("focus"); })));
        menu.Items.Add(MenuItem(Locale.Get("menu.closeTab"), () => CloseSession(id)));
        return menu;
    }

    /// <summary>The tab strip's space above and below its tabs: what Layout.TabStrip leaves around a Layout.Tab tab.</summary>
    internal const double TabStripPadding = (DesignMetrics.Layout.TabStrip - DesignMetrics.Layout.Tab) / 2;

    /// <summary>
    /// A tab group is one card (M/PaneDockView.swift:162-193): the 38pt strip on top, then the slim ink
    /// bar when the selected pane is not a conversation, then the pane. The strip (M/PaneDockView.swift:195-216)
    /// is the subtle wash with the card's top corners and a 1pt rule over its bottom edge (an overlay, as
    /// on the Mac, so it takes no room from the tabs), accent x 0.55 on the group holding the active pane.
    /// It holds nothing but its tabs, scrolls sideways without a scroll bar and brings the selected tab
    /// into view.
    /// </summary>
    private FrameworkElement BuildTabGroup(PaneLayoutNode node, string workspace, AppSnapshot state)
    {
        var group = new Grid { AllowDrop = true, Background = brushes.Brush(DesignToken.Card), CornerRadius = new CornerRadius(DesignMetrics.Radius.Pane) };
        group.RowDefinitions.Add(new() { Height = GridLength.Auto }); group.RowDefinitions.Add(new() { Height = GridLength.Auto }); group.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xxs, Padding = new Thickness(DesignMetrics.Spacing.Xs, TabStripPadding, DesignMetrics.Spacing.Xs, TabStripPadding) };
        var bar = new ScrollViewer { Content = tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled };
        var holdsActive = state.ActiveSessionId is { } activeId && node.SessionIds.Contains(activeId);
        var strip = new Grid { Height = DesignMetrics.Layout.TabStrip, Background = brushes.Subtle, CornerRadius = new CornerRadius(DesignMetrics.Radius.Pane, DesignMetrics.Radius.Pane, 0, 0) };
        var rule = new Border
        {
            Height = DesignMetrics.Stroke.Line, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false,
            Background = holdsActive ? brushes.Brush(DesignToken.Accent, DesignMetrics.Opacity.TabActiveRule) : brushes.Brush(DesignToken.Line),
        };
        strip.Children.Add(bar); strip.Children.Add(rule);
        AutomationProperties.SetAutomationId(strip, "pane-tab-strip-" + node.Id); tabStrips[node.Id] = (strip, rule);
        group.Children.Add(strip);
        // The drop preview (M/PaneDockDrag.swift:131-146, M/PaneDockView.swift:28-38): the zone set in 5, radius 9,
        // accent x 0.16 under a 2pt accent dash [6, 4], its words 12 semibold accent on a page x 0.95 capsule.
        var accent = brushes.Brush(DesignToken.Accent);
        var hintWords = new TextBlock { Text = Locale.Get("layout.drop.merge"), FontFamily = BodyFont, FontSize = DesignMetrics.Type.Block, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = accent };
        var hintEdge = new Microsoft.UI.Xaml.Shapes.Rectangle { RadiusX = 9, RadiusY = 9, Fill = brushes.Brush(DesignToken.Accent, DropHintOpacity), Stroke = accent, StrokeThickness = DesignMetrics.Stroke.Active, StrokeDashArray = [.. DesignMetrics.Dash.InStrokeUnits([6, 4], DesignMetrics.Stroke.Active)] };
        var hint = new Grid { Margin = new Thickness(DropHintInset), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        var hintLabel = new Border { Child = hintWords, Padding = new Thickness(DesignMetrics.Spacing.Md, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Md, DesignMetrics.Spacing.Sm), CornerRadius = new CornerRadius(15), Background = brushes.Brush(DesignToken.Page, DropHintLabelOpacity), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        hint.Children.Add(hintEdge); hint.Children.Add(hintLabel);
        // The preview covers the half the pane would take, or the whole group to join its tabs, less its margin.
        void ShowHint(string edge)
        {
            hint.Width = edge is "left" or "right" ? Math.Max(0, group.ActualWidth / 2 - 2 * DropHintInset) : double.NaN; hint.Height = edge is "top" or "bottom" ? Math.Max(0, group.ActualHeight / 2 - 2 * DropHintInset) : double.NaN;
            hint.HorizontalAlignment = edge == "left" ? HorizontalAlignment.Left : edge == "right" ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            hint.VerticalAlignment = edge == "top" ? VerticalAlignment.Top : edge == "bottom" ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
            hintWords.Text = Locale.Get(edge switch { "left" => "layout.drop.left", "right" => "layout.drop.right", "top" => "layout.drop.top", "bottom" => "layout.drop.bottom", _ => "layout.drop.merge" });
            hint.Visibility = Visibility.Visible;
        }
        dropHints[node.Id] = (hint, hintEdge, hintLabel, hintWords, ShowHint);
        var selected = node.SelectedSessionId ?? node.SessionIds[0];
        FrameworkElement? selectedCell = null;
        foreach (var id in node.SessionIds)
        {
            var session = state.Sessions.First(s => s.Id == id); var isSelected = id == selected;
            // PaneDockTab (M/PaneDockView.swift:236-266): the handle covers the tab up to its close button,
            // l10 r6, at least 56 wide and Layout.Tab tall; the selected tab is a card with a line border, r6, drawn as a shape
            // under the buttons so the border takes no room from them.
            var tab = Button(session.Title, () => SelectLayoutSession(id)); tab.CanDrag = true; tab.AllowDrop = true; tab.MinWidth = 56; tab.Height = DesignMetrics.Layout.Tab; tab.MinHeight = 0; tab.Padding = new(DesignMetrics.Inset.TabLeading, 0, DesignMetrics.Inset.TabTrailing, 0); tab.CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment); tab.BorderThickness = new(0);
            tab.HorizontalContentAlignment = HorizontalAlignment.Left; tab.VerticalAlignment = VerticalAlignment.Center;
            PaintPlainButton(tab, brushes.Transparent, brushes.Transparent);
            tab.Content = TabIndicator(session, isSelected); tab.ContextFlyout = TabMenu(id);
            tab.DoubleTapped += async (_, args) => { args.Handled = true; await RenameSession(id); };
            ToolTipService.SetToolTip(tab, Locale.Get("layout.tab.dragTooltip", new Dictionary<string, string> { ["title"] = session.Title }));
            // Where a dragged tab would land: a 2pt accent bar on this tab's leading or trailing edge (M/PaneDockDrag.swift:218-222).
            var landing = new Border { Width = DesignMetrics.Stroke.Active, CornerRadius = new CornerRadius(1), Margin = new Thickness(0, DesignMetrics.Spacing.Xxs, 0, DesignMetrics.Spacing.Xxs), Background = accent, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            tab.DragStarting += (_, args) => { draggedSessionId = id; draggedWorkspaceId = workspace; args.Data.SetData(PaneDragFormat, id); args.Data.RequestedOperation = DataPackageOperation.Move; };
            tab.DropCompleted += (_, _) => { draggedSessionId = null; draggedWorkspaceId = null; };
            tab.DragOver += (_, args) =>
            {
                if (!IsPaneDrag(args, workspace)) return;
                args.AcceptedOperation = DataPackageOperation.Move; args.Handled = true; hint.Visibility = Visibility.Collapsed;
                landing.HorizontalAlignment = args.GetPosition(tab).X > tab.ActualWidth / 2 ? HorizontalAlignment.Right : HorizontalAlignment.Left; landing.Visibility = Visibility.Visible;
            };
            tab.DragLeave += (_, _) => landing.Visibility = Visibility.Collapsed;
            tab.Drop += async (_, args) =>
            {
                if (!IsPaneDrag(args, workspace)) return;
                args.Handled = true; landing.Visibility = Visibility.Collapsed; var moved = draggedSessionId!; var index = node.SessionIds.IndexOf(id) + (args.GetPosition(tab).X > tab.ActualWidth / 2 ? 1 : 0);
                await DockSession(moved, workspace, node.Id, "center", index);
            };
            // The Mac's xmark at 8 medium draws 6.3 wide; Segoe's Cancel needs 9.5 to draw as wide.
            var close = Button("×", () => CloseSession(id)); close.Content = new FontIcon { Glyph = "\uE711", FontSize = 9.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium }; close.MinWidth = 0; close.MinHeight = 0; close.Width = 20; close.Height = DesignMetrics.Layout.Tab; close.Padding = new(0); close.CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment); close.BorderThickness = new(0);
            PaintPlainButton(close, brushes.Transparent, brushes.Transparent, ink: brushes.Brush(DesignToken.Ink2));
            AutomationProperties.SetName(close, Locale.Get("layout.tab.closeAccessibility", new Dictionary<string, string> { ["title"] = session.Title }));
            var handle = new Grid(); handle.Children.Add(tab); handle.Children.Add(landing);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Padding = new Thickness(0, 0, DesignMetrics.Spacing.Xxs, 0) }; row.Children.Add(handle); row.Children.Add(close);
            var shape = new Border
            {
                CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), BorderThickness = new Thickness(DesignMetrics.Stroke.Line), IsHitTestVisible = false,
                Background = isSelected ? brushes.Brush(DesignToken.Card) : brushes.Transparent, BorderBrush = isSelected ? brushes.Brush(DesignToken.Line) : brushes.Transparent,
            };
            var tabCell = new Grid { Height = DesignMetrics.Layout.Tab, VerticalAlignment = VerticalAlignment.Center }; tabCell.Children.Add(shape); tabCell.Children.Add(row);
            AutomationProperties.SetAutomationId(tabCell, "pane-tab-" + id);
            tabCells[id] = (tabCell, shape, tab, close, tabTitles[id]);
            tabs.Children.Add(tabCell);
            if (isSelected) selectedCell = tabCell;
        }
        // A strip with more tabs than room shows the selected one in its middle (M/PaneDockView.swift:207), once it has been laid out.
        bar.Loaded += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (selectedCell is not { IsLoaded: true } || bar.ScrollableWidth <= 0) return;
            var left = selectedCell.TransformToVisual(tabs).TransformPoint(new Windows.Foundation.Point()).X;
            bar.ChangeView(Math.Clamp(left + selectedCell.ActualWidth / 2 - bar.ViewportWidth / 2, 0, bar.ScrollableWidth), null, null, true);
        });
        if (!views.TryGetValue(selected, out var pane)) { pane = new(this, selected); pane.InitRefresher(); views[selected] = pane; }
        if (paneHosts.ContainsKey(selected)) throw new InvalidOperationException(Locale.Get("layout.error.paneInTwoGroups"));
        // A terminal of an agent, a browser or the files pane wears the slim ink bar over it (M/PaneDockView.swift:178-180).
        var shown = state.Sessions.First(s => s.Id == selected);
        if (GroupSlimHeaderKinds.Contains(shown.Kind))
        {
            var slim = BuildGroupSlimHeader(shown); Grid.SetRow(slim, 1); group.Children.Add(slim);
            // A press on the bar makes its pane the active one, as a press in the pane does (M/PaneChrome.swift:139).
            slim.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => ActivatePane(selected)), true);
        }
        var paneHost = new Border(); paneHosts.Add(selected, paneHost);
        paneHost.Child = pane.Container; paneHost.ContextFlyout = SessionMenu(selected); Grid.SetRow(paneHost, 2); group.Children.Add(paneHost); pane.Refresh();
        Grid.SetRowSpan(hint, 3); group.Children.Add(hint);
        group.DragOver += (_, args) =>
        {
            if (!IsPaneDrag(args, workspace)) return;
            args.Handled = true; args.AcceptedOperation = DataPackageOperation.Move;
            ShowHint(DropEdge(args.GetPosition(group), group));
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
