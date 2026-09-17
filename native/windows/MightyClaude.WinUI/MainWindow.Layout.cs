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
    private readonly StackPanel sessionLinks = new() { Spacing = 3 };
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
        tabIndicators.Clear();
        foreach (var stale in layoutDefaults.Keys.Where(id => !state.Workspaces.Any(w => w.Id == id)).ToArray()) layoutDefaults.Remove(stale);
        if (state.ActiveWorkspaceId is not { } workspace || EffectiveLayout(state, workspace) is not { } node)
        {
            var empty = new StackPanel { Spacing = 18, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(32) };
            empty.Children.Add(new TextBlock { Text = state.Workspaces.Count == 0 ? "프로젝트 폴더를 추가해 시작하세요." : "새 실행 창을 추가하세요.", FontSize = 20, Opacity = .6, TextWrapping = TextWrapping.Wrap });
            if (state.ActiveWorkspaceId is not null) empty.Children.Add(new Button { Content = "+ 실행 창", Flyout = NewSessionMenu(), HorizontalAlignment = HorizontalAlignment.Center });
            panes.Children.Add(empty);
            return;
        }
        if (LayoutMode(state, workspace) == "focus") node = PaneLayout.Groups(node).FirstOrDefault(g => g.SessionIds.Contains(state.ActiveSessionId ?? "")) ?? PaneLayout.Groups(node).First();
        var content = BuildLayoutNode(node, workspace, state);
        var minimum = LayoutMinimum(node);
        content.Width = Math.Max(minimum.Width, panes.ActualWidth > 0 ? panes.ActualWidth : 900); content.Height = Math.Max(minimum.Height, panes.ActualHeight > 0 ? panes.ActualHeight : 650);
        var viewport = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto, VerticalScrollMode = ScrollMode.Auto };
        viewport.SizeChanged += (_, args) => { content.Width = Math.Max(minimum.Width, args.NewSize.Width); content.Height = Math.Max(minimum.Height, args.NewSize.Height); };
        panes.Children.Add(viewport);
    }

    private static Windows.Foundation.Size LayoutMinimum(PaneLayoutNode node)
    {
        if (node.Kind == "tabs") return new(315, 280);
        var a = LayoutMinimum(node.Children[0]); var b = LayoutMinimum(node.Children[1]);
        return node.Axis == "horizontal" ? new(a.Width + b.Width + 8, Math.Max(a.Height, b.Height)) : new(Math.Max(a.Width, b.Width), a.Height + b.Height + 8);
    }

    private FrameworkElement BuildLayoutNode(PaneLayoutNode node, string workspace, AppSnapshot state)
    {
        if (node.Kind == "tabs") return BuildTabGroup(node, workspace, state);
        var horizontal = node.Axis == "horizontal"; var grid = new Grid(); var ratio = node.Ratio;
        if (horizontal)
        {
            grid.ColumnDefinitions.Add(new() { Width = new(ratio, GridUnitType.Star), MinWidth = LayoutMinimum(node.Children[0]).Width }); grid.ColumnDefinitions.Add(new() { Width = new(8) }); grid.ColumnDefinitions.Add(new() { Width = new(1 - ratio, GridUnitType.Star), MinWidth = LayoutMinimum(node.Children[1]).Width });
        }
        else
        {
            grid.RowDefinitions.Add(new() { Height = new(ratio, GridUnitType.Star), MinHeight = LayoutMinimum(node.Children[0]).Height }); grid.RowDefinitions.Add(new() { Height = new(8) }); grid.RowDefinitions.Add(new() { Height = new(1 - ratio, GridUnitType.Star), MinHeight = LayoutMinimum(node.Children[1]).Height });
        }
        var first = BuildLayoutNode(node.Children[0], workspace, state); var second = BuildLayoutNode(node.Children[1], workspace, state);
        if (horizontal) Grid.SetColumn(second, 2); else Grid.SetRow(second, 2);
        grid.Children.Add(first); grid.Children.Add(second);
        var divider = new Thumb { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(65, 135, 135, 135)), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        AutomationProperties.SetName(divider, horizontal ? "좌우 분할 크기 조절" : "상하 분할 크기 조절"); ToolTipService.SetToolTip(divider, "드래그하여 분할 크기 조절");
        if (horizontal) Grid.SetColumn(divider, 1); else Grid.SetRow(divider, 1);
        divider.DragDelta += (_, args) =>
        {
            var size = (horizontal ? grid.ActualWidth : grid.ActualHeight) - 8; if (size <= 0) return;
            ratio = PaneLayout.ClampRatio(ratio + (horizontal ? args.HorizontalChange : args.VerticalChange) / size);
            if (horizontal) { grid.ColumnDefinitions[0].Width = new(ratio, GridUnitType.Star); grid.ColumnDefinitions[2].Width = new(1 - ratio, GridUnitType.Star); }
            else { grid.RowDefinitions[0].Height = new(ratio, GridUnitType.Star); grid.RowDefinitions[2].Height = new(1 - ratio, GridUnitType.Star); }
        };
        divider.DragCompleted += async (_, args) => await Act(async () =>
        {
            if (!args.Canceled) await service.UpdateAsync(s => EffectiveLayout(s, workspace) is { } current ? SaveLayoutMode(SaveLayout(s, workspace, PaneLayout.Resize(current, node.Id, ratio)), workspace, "custom") : s);
            Render();
        });
        grid.Children.Add(divider); return grid;
    }

    private bool IsPaneDrag(DragEventArgs args, string workspace) => draggedSessionId is not null && draggedWorkspaceId == workspace && args.DataView.Contains(PaneDragFormat);
    private static string DropEdge(Windows.Foundation.Point position, FrameworkElement view)
    {
        var x = position.X / Math.Max(1, view.ActualWidth); var y = position.Y / Math.Max(1, view.ActualHeight);
        if (x < .2) return "left"; if (x > .8) return "right"; if (y < .2) return "top"; if (y > .8) return "bottom"; return "center";
    }

    private FrameworkElement BuildTabGroup(PaneLayoutNode node, string workspace, AppSnapshot state)
    {
        var group = new Grid { AllowDrop = true, Background = new SolidColorBrush(Colors.Transparent) };
        group.RowDefinitions.Add(new() { Height = GridLength.Auto }); group.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        var bar = new ScrollViewer { Content = tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled };
        var tabHeader = new Grid { ColumnSpacing = 4 }; tabHeader.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); tabHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        tabHeader.Children.Add(bar);
        var add = new Button { Content = "+", Width = 30, Height = 30, MinWidth = 0, Padding = new(0), Flyout = NewSessionMenu(node.Id) }; AutomationProperties.SetName(add, "이 그룹에 실행 창 추가"); Grid.SetColumn(add, 1); tabHeader.Children.Add(add); group.Children.Add(tabHeader);
        var selected = node.SelectedSessionId ?? node.SessionIds[0];
        foreach (var id in node.SessionIds)
        {
            var session = state.Sessions.First(s => s.Id == id);
            var tab = Button(session.Title, () => SelectLayoutSession(id)); tab.CanDrag = true; tab.AllowDrop = true; tab.MaxWidth = 250; tab.Padding = new(10, 6, 10, 6); tab.Margin = new(0, 0, 0, 4);
            tab.Content = SessionIndicator(session, tab: true); tab.ContextFlyout = SessionMenu(id);
            tab.DoubleTapped += async (_, args) => { args.Handled = true; await RenameSession(id); };
            if (id == selected) tab.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 100, 149, 237));
            ToolTipService.SetToolTip(tab, "드래그하여 탭 이동 · 실행 창 가장자리에 놓아 분할");
            tab.DragStarting += (_, args) => { draggedSessionId = id; draggedWorkspaceId = workspace; args.Data.SetData(PaneDragFormat, id); args.Data.RequestedOperation = DataPackageOperation.Move; };
            tab.DropCompleted += (_, _) => { draggedSessionId = null; draggedWorkspaceId = null; };
            tab.DragOver += (_, args) => { if (!IsPaneDrag(args, workspace)) return; args.AcceptedOperation = DataPackageOperation.Move; args.Handled = true; tab.BorderBrush = new SolidColorBrush(Colors.CornflowerBlue); tab.BorderThickness = new(2); };
            tab.DragLeave += (_, _) => tab.BorderThickness = new(0);
            tab.Drop += async (_, args) =>
            {
                if (!IsPaneDrag(args, workspace)) return;
                args.Handled = true; var moved = draggedSessionId!; var index = node.SessionIds.IndexOf(id) + (args.GetPosition(tab).X > tab.ActualWidth / 2 ? 1 : 0);
                await DockSession(moved, workspace, node.Id, "center", index);
            };
            var tabCell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 }; tabCell.Children.Add(tab);
            var close = Button("×", () => CloseSession(id)); close.MinWidth = 0; close.Width = 22; close.Height = 30; close.Padding = new(0); close.Background = new SolidColorBrush(Colors.Transparent); close.BorderThickness = new(0); AutomationProperties.SetName(close, session.Title + " 닫기"); tabCell.Children.Add(close); tabs.Children.Add(tabCell);
        }
        if (!views.TryGetValue(selected, out var pane)) { pane = new(this, selected); views[selected] = pane; }
        if (paneHosts.ContainsKey(selected)) throw new InvalidOperationException("같은 실행 창이 두 레이아웃 그룹에 연결되어 있습니다.");
        var paneHost = new Border(); paneHosts.Add(selected, paneHost);
        paneHost.Child = pane.Container; Grid.SetRow(paneHost, 1); group.Children.Add(paneHost); pane.Refresh();
        var hint = new Border { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(70, 100, 149, 237)), BorderBrush = new SolidColorBrush(Colors.CornflowerBlue), BorderThickness = new(2), IsHitTestVisible = false, Visibility = Visibility.Collapsed, Child = new TextBlock { Text = "탭으로 합치기", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        Grid.SetRowSpan(hint, 2); group.Children.Add(hint);
        group.DragOver += (_, args) =>
        {
            if (!IsPaneDrag(args, workspace)) return;
            args.Handled = true; args.AcceptedOperation = DataPackageOperation.Move;
            var edge = DropEdge(args.GetPosition(group), group); hint.Visibility = Visibility.Visible;
            hint.Width = edge is "left" or "right" ? group.ActualWidth / 2 : double.NaN; hint.Height = edge is "top" or "bottom" ? group.ActualHeight / 2 : double.NaN;
            hint.HorizontalAlignment = edge == "left" ? HorizontalAlignment.Left : edge == "right" ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            hint.VerticalAlignment = edge == "top" ? VerticalAlignment.Top : edge == "bottom" ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
            ((TextBlock)hint.Child).Text = edge switch { "left" => "왼쪽으로 분할", "right" => "오른쪽으로 분할", "top" => "위로 분할", "bottom" => "아래로 분할", _ => "탭으로 합치기" };
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
