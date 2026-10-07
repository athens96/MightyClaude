using System.Diagnostics;
using System.Globalization;
using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;

namespace MightyClaude.WinUI;

/// <summary>
/// The read-only files pane of docs/file-pane.md: a workspace tree on the left and a
/// source, Markdown or image preview on the right. Opened with Ctrl+Shift+E (macOS ⇧⌘E),
/// the + menu or the workspace menu; one pane per workspace, placed left of the current
/// pane. Every rule — containment, encodings, caps, highlighting, the filter — comes from
/// MightyClaude.Core WorkspaceFiles.cs; this file only draws it. Nothing here writes.
/// </summary>
public sealed partial class MainWindow
{
    // Each workspace's tree (opened folders, selection, the file last previewed) lives while
    // the app runs, so a closed and reopened pane shows what it showed before.
    private readonly Dictionary<string, FilePaneTree> filePaneTrees = [];

    private FilePaneTree FilePaneTreeFor(string workspaceId)
    {
        if (!filePaneTrees.TryGetValue(workspaceId, out var tree)) filePaneTrees[workspaceId] = tree = new FilePaneTree();
        return tree;
    }

    /// <summary>Ctrl+Shift+E opens the active workspace's files pane from anywhere in the window.</summary>
    private void InitFilePane()
    {
        // The window-wide shortcut never shows as a tooltip over the whole window.
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        var accelerator = new KeyboardAccelerator { Key = VirtualKey.E, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift };
        accelerator.Invoked += async (_, args) => { args.Handled = true; await OpenFilePane(); };
        root.KeyboardAccelerators.Add(accelerator);
    }

    internal bool HasOpenFilesAccelerator => root.KeyboardAccelerators.Any(a => a.Key == VirtualKey.E && a.Modifiers == (VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift));

    private MenuFlyoutItem OpenFilesMenuItem(string? workspaceId = null)
    {
        var item = MenuItem(Locale.Get("menu.showFiles"), () => OpenFilePane(workspaceId));
        item.KeyboardAcceleratorTextOverride = FilePaneKind.Shortcut;
        item.Icon = new FontIcon { Glyph = "\uE8B7" };
        return item;
    }

    /// <summary>
    /// Shows the workspace's one files pane: focuses it when it is open, otherwise puts it
    /// left of the current pane (or as a tab when there is no room for a split). It is never
    /// saved, so it does not come back after a restart.
    /// </summary>
    internal Task OpenFilePane(string? workspaceId = null) => Act(async () =>
    {
        var state = service.Snapshot;
        var workspace = state.Workspaces.FirstOrDefault(w => w.Id == (workspaceId ?? state.ActiveWorkspaceId))
            ?? throw new InvalidOperationException(Locale.Get("window.error.addWorkspaceFirst"));
        var id = FilePaneKind.PaneId(workspace.Id);
        if (state.Sessions.Any(s => s.Id == id)) { await SelectLayoutSession(id); return; }
        if (state.Sessions.Count >= 128) throw new InvalidOperationException(Locale.Get("files.error.noRoom"));
        var current = state.ActiveWorkspaceId == workspace.Id ? state.ActiveSessionId : state.PaneLayoutActiveSessionIds?.GetValueOrDefault(workspace.Id);
        await service.UpdateAsync(s =>
        {
            var pane = new RunSession { Id = id, WorkspaceId = workspace.Id, Kind = FilePaneKind.Kind, Title = Locale.Get("files.pane.title") };
            // The pane joins the tree before it becomes the active one, as a new pane does (AddToLayout): placed as the
            // active pane it would first be shown in the first group, and moving it out would reset the tab shown there.
            var joined = s with { Sessions = s.Sessions.Append(pane).ToList() };
            var added = joined with { ActiveWorkspaceId = workspace.Id, ActiveSessionId = id };
            if (EffectiveLayout(joined, workspace.Id) is not { } tree) return SaveLayoutSelection(added, workspace.Id, id);
            var placed = PaneLayout.Select(FilePaneKind.Place(tree, id, current), id);
            var next = SaveLayoutSelection(SaveLayout(added, workspace.Id, placed), workspace.Id, id);
            return LayoutMode(added, workspace.Id) == "focus" ? next : SaveLayoutMode(next, workspace.Id, placed.Kind == "split" ? "custom" : "tabs");
        });
        Render();
    });

    private sealed partial class PaneView
    {
        /// <summary>A source line: 12pt mono set 15 apart, as the Mac's text view lays SF Mono out (measured on docs/design-system/crops/files-pane-light.webp).</summary>
        private const double FilesLineHeight = 15;
        private static readonly FontFamily MonoFont = new(DesignMetrics.Font.Mono);
        private bool filesAttached, renderingTree, showMarkdownSource;
        private Grid? filesHost;
        private TextBox? filesFilter;
        private ListView? filesList;
        private StackPanel? previewBanners, previewTools, previewNames;
        private TextBlock? previewTitle, previewPath, previewEncoding;
        /// <summary>The preview head's file symbol, its line underneath and the path it names (null while no file is chosen).</summary>
        private Border? previewIcon, previewHeaderLine;
        private string? previewHeaderPath;
        private Border? previewContent;
        private Grid? previewHeader, filesFilterRow;
        /// <summary>The tree's share of the pane's width (the divider's place), and the names of the rows drawn, which give way from their middle.</summary>
        private double filesTreeShare = FilesTreeShare;
        private readonly List<(TextBlock Block, string Full, double Taken)> filesNames = [];
        private int previewRequest, filterEdits;
        private CancellationTokenSource? previewCancel;
        private FilesPrepared? shown;
        // Fixed when the view is built: a closed pane's late work must never look its session up again.
        private FilePaneTree? filesTree;
        private string filesRoot = "";
        private const int MaximumColouredRuns = 20_000;
        private readonly Dictionary<string, FilePaneTree.Row> filesRows = [];
        private readonly List<FilePaneTree.Row> filesRowOrder = [];

        /// <summary>
        /// What a preview needs, prepared off the UI thread: the file, its colour spans, the
        /// line-number column and the rendered Markdown. HighlightUnits is how far colouring
        /// reaches when it stops early (the highlighter's cap, or a run cap on Windows).
        /// </summary>
        private sealed record FilesPrepared(FilePreviewData Data, List<SourceToken> Tokens, string LineNumbers, bool Wraps, bool HighlightCapped, int HighlightUnits, string? MarkdownRtf, bool RenderCapped = false, string? MarkdownSource = null, bool MarkdownLight = false);

        /// <summary>The rendered Markdown preview on screen, its source and the theme its RTF carries (null when none shows).</summary>
        private (RichEditBox View, string Source, bool Light)? markdownShown;

        private FilePaneTree Tree => filesTree ??= owner.FilePaneTreeFor(Session.WorkspaceId);
        /// <summary>This view is still the one shown for its pane.</summary>
        private bool FilesAlive => !owner.closing && owner.views.TryGetValue(id, out var current) && ReferenceEquals(current, this);
        internal FilePaneTree FilesTree => Tree;
        internal FrameworkElement? FilesHost => filesHost;
        internal FilePreviewData? FilesShown => shown?.Data;
        internal int FilesTreeItemCount => filesList?.Items.Count ?? 0;
        /// <summary>The parts the design smoke reads: the filter row, the tree, the preview head and its content.</summary>
        internal (Grid? Filter, ListView? Tree, Grid? Head, Border? Content) FilesDesignParts => (filesFilterRow, filesList, previewHeader, previewContent);
        /// <summary>The bitmap size the image preview last decoded, for the GUI smoke.</summary>
        internal (long Width, long Height)? FilesPreviewPixels { get; private set; }

        /// <summary>
        /// Replaces the transcript and the composer with the tree and the preview. A files pane
        /// runs no agent, so nothing here can start a CLI run.
        /// </summary>
        internal void EnsureFilesView()
        {
            if (filesAttached || Container.Child is not Grid grid) return;
            filesAttached = true;
            var session = Session;
            filesTree = owner.FilePaneTreeFor(session.WorkspaceId);
            filesRoot = owner.service.Snapshot.Workspaces.First(w => w.Id == session.WorkspaceId).Path;
            foreach (var child in grid.Children.OfType<FrameworkElement>().ToArray()) child.Visibility = Visibility.Collapsed;

            // The Mac's split view (M/FilePaneView.swift:22-25): the tree, a 1pt divider to drag, the preview.
            filesHost = new Grid { ColumnSpacing = 0 };
            var treeColumn = new ColumnDefinition { Width = new GridLength(FilesTreeMin) };
            filesHost.ColumnDefinitions.Add(treeColumn);
            filesHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DesignMetrics.Stroke.Line) });
            filesHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            AutomationProperties.SetAutomationId(filesHost, "files-pane-" + id);

            var tree = BuildFilesTree();
            filesHost.Children.Add(tree);
            // The split view's own line: line by day, black by night (DesignBrushes.SplitLine).
            var divider = new Border { Background = owner.brushes.SplitLine(DesignToken.Line) };
            Grid.SetColumn(divider, 1); filesHost.Children.Add(divider);
            var preview = BuildFilesPreview();
            Grid.SetColumn(preview, 2); filesHost.Children.Add(preview);
            filesHost.Children.Add(FilesSplitGrip(treeColumn));
            filesHost.SizeChanged += (_, args) => treeColumn.Width = new GridLength(FilesTreeWidth(args.NewSize.Width));

            Grid.SetRow(filesHost, 0); Grid.SetRowSpan(filesHost, Math.Max(1, grid.RowDefinitions.Count));
            grid.Children.Add(filesHost);
            FitFilesHost(grid); filesHost.Loaded += (_, _) => FitFilesHost(grid);
            _ = StartFiles();
        }

        /// <summary>
        /// The Mac's split view bounds (M/FilePaneView.swift:23-24): the tree 160 to 520 wide, the preview at least
        /// 160. A SwiftUI split view opens with its panes sharing the width evenly, whatever their ideal widths
        /// (the Mac's 771-wide pane shows a 385-wide tree: docs/design-system/crops/files-pane-light.webp).
        /// </summary>
        internal const double FilesTreeMin = 160, FilesTreeMax = 520, FilesPreviewMin = 160, FilesTreeShare = 0.5;

        /// <summary>The tree's width in a pane <paramref name="total"/> wide: its share, within the split's bounds (the preview's least width first).</summary>
        private double FilesTreeWidth(double total)
        {
            var room = Math.Max(0, total - DesignMetrics.Stroke.Line);
            return Math.Max(Math.Min(FilesTreeMin, room), Math.Min(room * filesTreeShare, Math.Min(FilesTreeMax, room - FilesPreviewMin)));
        }

        /// <summary>
        /// The divider's grip: a 9-wide strip over the 1pt line that shows the resize cursor and drags the
        /// tree's share, as the Mac's split view does. It draws nothing itself (the line under it does).
        /// </summary>
        private ResizeCursorHost FilesSplitGrip(ColumnDefinition treeColumn)
        {
            const double reach = 4;
            var grip = new Thumb { Background = owner.brushes.Transparent, Opacity = 0, IsTabStop = false };
            AutomationProperties.SetAutomationId(grip, "files-split-" + id);
            AutomationProperties.SetAccessibilityView(grip, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            grip.DragDelta += (_, args) => DragFilesSplit(treeColumn, args.HorizontalChange);
            var host = new ResizeCursorHost(grip, horizontal: true) { Margin = new Thickness(-reach, 0, -reach, 0) };
            Grid.SetColumn(host, 1);
            return host;
        }

        /// <summary>Moves the divider by <paramref name="change"/>, within the split's bounds.</summary>
        private void DragFilesSplit(ColumnDefinition treeColumn, double change)
        {
            if (filesHost is not { ActualWidth: > 0 } host) return;
            var room = Math.Max(1, host.ActualWidth - DesignMetrics.Stroke.Line);
            filesTreeShare = Math.Clamp((treeColumn.Width.Value + change) / room, 0, 1);
            var width = FilesTreeWidth(host.ActualWidth);
            // The share follows the bounds, so a drag past one does not have to be dragged back first.
            filesTreeShare = width / room; treeColumn.Width = new GridLength(width);
        }

        /// <summary>
        /// The tree and the preview fill the pane card edge to edge, over the pane grid's own padding
        /// (M/PaneDockView.swift:175-188): from the card's top while the pane shows no header over them, and 2
        /// under the pane's bar when it shows one (M/PaneChrome.swift:119), whether they share its row or have
        /// the rows under it. Their corners follow the card's: it has no edge of its own, and square top
        /// corners under its group's slim bar (PaneView.ShowActive).
        /// </summary>
        private void FitFilesHost(Grid grid)
        {
            if (filesHost is null) return;
            const double gap = DesignMetrics.Spacing.Xxs;
            var p = grid.Padding;
            var header = paneHeader is { Visibility: Visibility.Visible } shown && double.IsFinite(shown.Height) ? shown : null;
            var top = Grid.GetRow(filesHost) > 0 ? gap - grid.RowSpacing : header is null ? -p.Top : header.Margin.Top + header.Height + gap;
            filesHost.Margin = new Thickness(-p.Left, top, -p.Right, -p.Bottom);
            filesHost.CornerRadius = new CornerRadius(0, 0, DesignMetrics.Radius.Pane, DesignMetrics.Radius.Pane);
        }

        /// <summary>
        /// The tree column (M/FilePaneView.swift:31-81): the 28-high filter row on the subtle wash (the 10pt
        /// filter lines in the tertiary ink, the 11pt field, the 11pt refresh, 6 apart, padding h10 v7), a
        /// <c>line</c> under it, then the rows with their notes after them.
        /// </summary>
        private Grid BuildFilesTree()
        {
            var b = owner.brushes;
            var panel = new Grid();
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var bar = filesFilterRow = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm, Height = FilesFilterHeight, Padding = new Thickness(DesignMetrics.Spacing.Md, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Md, DesignMetrics.Spacing.Sm), Background = b.Subtle };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.Children.Add(PanelSymbol.FilterLines(b.Tertiary));
            filesFilter = new TextBox { PlaceholderText = Locale.Get("files.tree.filter"), Text = Tree.Filter, FontSize = DesignMetrics.Type.Pill, VerticalAlignment = VerticalAlignment.Center };
            owner.PaintPlainTextBox(filesFilter);
            AutomationProperties.SetName(filesFilter, Locale.Get("files.tree.filter")); AutomationProperties.SetAutomationId(filesFilter, "files-filter");
            // Typing redraws the list once it pauses for about 100 ms, not on every key.
            filesFilter.TextChanged += async (_, _) =>
            {
                if (renderingTree) return;
                var edit = ++filterEdits;
                await Task.Delay(100);
                if (edit != filterEdits || !FilesAlive || filesFilter is null) return;
                Tree.Filter = filesFilter.Text; RenderTree();
            };
            Grid.SetColumn(filesFilter, 1); bar.Children.Add(filesFilter);
            // The symbol alone takes the row's room, as the Mac's plain button; its wash reaches 4 and 2 past it.
            var refresh = new Button { Content = new FontIcon { Glyph = "\uE72C", FontSize = DesignMetrics.Type.Pill }, MinWidth = 0, MinHeight = 0, Padding = new Thickness(DesignMetrics.Spacing.Xs, DesignMetrics.Spacing.Xxs, DesignMetrics.Spacing.Xs, DesignMetrics.Spacing.Xxs), Margin = new Thickness(-DesignMetrics.Spacing.Xs, -DesignMetrics.Spacing.Xxs, -DesignMetrics.Spacing.Xs, -DesignMetrics.Spacing.Xxs), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(DesignMetrics.Radius.FileRow), VerticalAlignment = VerticalAlignment.Center };
            owner.PaintPlainButton(refresh, b.Transparent, b.Subtle, ink: b.Brush(DesignToken.Ink2));
            AutomationProperties.SetName(refresh, Locale.Get("files.tree.refresh")); ToolTipService.SetToolTip(refresh, Locale.Get("files.tree.refresh"));
            AutomationProperties.SetAutomationId(refresh, "files-refresh");
            refresh.Click += async (_, _) => await RefreshFiles();
            Grid.SetColumn(refresh, 2); bar.Children.Add(refresh);
            panel.Children.Add(bar);
            var line = new Border { Height = DesignMetrics.Stroke.Line, Background = b.Brush(DesignToken.Line) };
            Grid.SetRow(line, 1); panel.Children.Add(line);

            // The Mac's rows stand where they belong at once: no slide as a folder opens, the tree refreshes or the filter changes.
            filesList = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = false, Padding = new Thickness(0, DesignMetrics.Spacing.Xs, 0, DesignMetrics.Spacing.Xs), ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection() };
            AutomationProperties.SetAutomationId(filesList, "files-tree");
            PaintFilesRows(filesList);
            filesList.SizeChanged += (_, args) => { if (args.NewSize.Width != args.PreviousSize.Width) FitTreeNames(reset: true); };
            filesList.SelectionChanged += async (_, _) =>
            {
                if (renderingTree || filesList.SelectedItem is not ListViewItem { Tag: string path } || !filesRows.TryGetValue(path, out var row)) return;
                Tree.SelectedPath = path;
                if (!row.Entry.IsDirectory) await ShowPreview(path, debounce: true);
            };
            filesList.PreviewKeyDown += async (_, args) =>
            {
                if (filesList.SelectedItem is not ListViewItem { Tag: string path } || !filesRows.TryGetValue(path, out var row)) return;
                switch (args.Key)
                {
                    case VirtualKey.Right when row.Entry.IsDirectory:
                        args.Handled = true;
                        if (Tree.IsFiltering) await RevealFolder(path);
                        else if (!row.IsExpanded) await ToggleFolder(row.Entry);
                        else SelectFirstChild(path);
                        break;
                    case VirtualKey.Left:
                        args.Handled = true;
                        if (Tree.IsFiltering) break;
                        if (row.Entry.IsDirectory && row.IsExpanded) await ToggleFolder(row.Entry);
                        else if (path.Contains('/')) SelectPath(path[..path.LastIndexOf('/')]);
                        break;
                    case VirtualKey.Enter when row.Entry.IsDirectory:
                        args.Handled = true;
                        if (Tree.IsFiltering) await RevealFolder(path); else await ToggleFolder(row.Entry);
                        break;
                }
            };
            Grid.SetRow(filesList, 2); panel.Children.Add(filesList);
            return panel;
        }

        /// <summary>The filter row's height: v7 around the 11pt field's 14 (M/FilePaneView.swift:33-44).</summary>
        internal const double FilesFilterHeight = 28;
        /// <summary>The Markdown preview's padding on every side (M/FilePaneView.swift:246 <c>Spacing.lg</c>).</summary>
        internal const double FilesMarkdownInset = DesignMetrics.Spacing.Lg;

        /// <summary>
        /// The preview column (M/FilePaneView.swift:157-185): the 40-high head on the subtle wash, padding h12, its
        /// parts 8 apart: the file's symbol in <c>accent</c>, its name 12 semibold over its path 10 mono <c>ink2</c>
        /// (cut in the middle), then after a spacer the encoding, the tools and the show-in-Explorer folder; a
        /// <c>line</c> under it; the banners; then the content.
        /// </summary>
        private Grid BuildFilesPreview()
        {
            var b = owner.brushes;
            var panel = new Grid();
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var header = previewHeader = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm, Height = DesignMetrics.Layout.PreviewHead, Padding = new Thickness(DesignMetrics.Spacing.Lg, 0, DesignMetrics.Spacing.Lg, 0), Background = b.Subtle };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            previewIcon = new Border { VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAccessibilityView(previewIcon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            header.Children.Add(previewIcon);
            var names = previewNames = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            previewTitle = new TextBlock { FontSize = DesignMetrics.Type.Block, FontWeight = FontWeights.SemiBold, Foreground = b.Brush(DesignToken.Ink), TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = 15, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
            previewPath = new TextBlock { FontSize = DesignMetrics.Type.Small, FontFamily = MonoFont, Foreground = b.Brush(DesignToken.Ink2), TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Left, LineHeight = 12, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
            names.Children.Add(previewTitle); names.Children.Add(previewPath);
            // The names take what the symbol and the tools leave, and the path gives way from its middle.
            names.SizeChanged += (_, args) => { if (previewHeaderPath is { } shown && args.NewSize.Width != args.PreviousSize.Width) MiddleTrim.Fit(previewPath, shown, args.NewSize.Width); };
            Grid.SetColumn(names, 1); header.Children.Add(names);
            // A spacer of at least 6 between the names and the tools, each 8 from it (22 in all); the tools 8 apart.
            previewTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm, Margin = new Thickness(DesignMetrics.Spacing.Md, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            previewEncoding = new TextBlock { FontSize = DesignMetrics.Type.Small, Foreground = b.Brush(DesignToken.Ink2), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            ToolTipService.SetToolTip(previewEncoding, Locale.Get("files.preview.encoding"));
            AutomationProperties.SetAutomationId(previewEncoding, "files-encoding");
            previewTools.Children.Add(previewEncoding);
            var reveal = PreviewToolButton(new FontIcon { Glyph = "\uE8B7", FontSize = 15 }, "menu.showInExplorer", b.Brush(DesignToken.Ink));
            AutomationProperties.SetAutomationId(reveal, "files-reveal");
            reveal.Click += (_, _) => { if (previewHeaderPath is { } shown) RevealInExplorer(shown); };
            previewTools.Children.Add(reveal);
            Grid.SetColumn(previewTools, 2); header.Children.Add(previewTools);
            AutomationProperties.SetAutomationId(header, "files-preview-head");
            panel.Children.Add(header);
            previewHeaderLine = new Border { Height = DesignMetrics.Stroke.Line, Background = b.Brush(DesignToken.Line) };
            Grid.SetRow(previewHeaderLine, 1); panel.Children.Add(previewHeaderLine);
            previewBanners = new StackPanel();
            Grid.SetRow(previewBanners, 2); panel.Children.Add(previewBanners);
            previewContent = new Border();
            AutomationProperties.SetAutomationId(previewContent, "files-preview");
            Grid.SetRow(previewContent, 3); panel.Children.Add(previewContent);
            ShowPlaceholder();
            return panel;
        }

        /// <summary>
        /// A tool of the preview head, the Mac's plain button: the symbol (or word) alone takes the head's room and
        /// its wash under the pointer reaches 3 past it. Named and explained by the locale key.
        /// </summary>
        private Button PreviewToolButton(FrameworkElement face, string key, Brush ink, bool enabled = true)
        {
            var button = new Button { Content = face, MinWidth = 0, MinHeight = 0, Padding = new Thickness(DesignMetrics.Spacing.Xxs), Margin = new Thickness(-DesignMetrics.Spacing.Xxs), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(DesignMetrics.Radius.FileRow), VerticalAlignment = VerticalAlignment.Center, IsEnabled = enabled };
            owner.PaintPlainButton(button, owner.brushes.Transparent, owner.brushes.Subtle, ink: ink, disabledInk: owner.brushes.Brush(DesignToken.Ink3));
            AutomationProperties.SetName(button, Locale.Get(key)); ToolTipService.SetToolTip(button, Locale.Get(key));
            return button;
        }

        /// <summary>Takes the head's tools of the last preview away: everything between the encoding and the folder.</summary>
        private void ClearPreviewTools()
        {
            if (previewTools is null) return;
            while (previewTools.Children.Count > 2) previewTools.Children.RemoveAt(1);
        }

        /// <summary>Adds a tool to the preview head, before the folder that closes it (or right after the encoding).</summary>
        private void AddPreviewTool(UIElement tool, bool first = false) => previewTools?.Children.Insert(first ? 1 : previewTools.Children.Count - 1, tool);

        /// <summary>Loads the root the first time; shows the file last previewed again when the pane reopens.</summary>
        private async Task StartFiles()
        {
            RenderTree();
            if (!Tree.Children.ContainsKey("")) await LoadFolder("");
            foreach (var path in Tree.Expanded.Where(p => !Tree.Children.ContainsKey(p)).ToList()) await LoadFolder(path);
            if (Tree.PreviewPath is { } target) await ShowPreview(target, debounce: false);
        }

        private async Task LoadFolder(string path)
        {
            var workspaceRoot = filesRoot;
            WorkspaceDirectoryListing? listing = null; WorkspaceFileError? failure = null;
            await Task.Run(() =>
            {
                try { listing = WorkspaceFiles.List(path, workspaceRoot); }
                catch (WorkspaceFileException ex) { failure = ex.Error; }
                // Anything else (a reparse point Windows cannot read, a race with a delete) is
                // an unreadable folder too: a tap must never take the app down.
                catch (Exception) { failure = WorkspaceFileError.Unreadable; }
            });
            Tree.Apply(path, listing, failure);
            if (FilesAlive) RenderTree();
        }

        private async Task RefreshFiles()
        {
            await Task.WhenAll(Tree.RefreshTargets().Select(LoadFolder));
            if (Tree.PreviewPath is { } target) await ShowPreview(target, debounce: false);
        }

        /// <summary>Opening a folder always reads it again; the earlier listing shows while it reads.</summary>
        private async Task ToggleFolder(WorkspaceFileEntry entry)
        {
            Tree.SelectedPath = entry.RelativePath;
            if (Tree.Expanded.Remove(entry.RelativePath)) { RenderTree(); return; }
            Tree.Expanded.Add(entry.RelativePath);
            RenderTree();
            await LoadFolder(entry.RelativePath);
        }

        private async Task RevealFolder(string path)
        {
            Tree.Reveal(path);
            renderingTree = true; if (filesFilter is not null) filesFilter.Text = ""; renderingTree = false;
            RenderTree();
            foreach (var folder in Tree.Expanded.Where(p => path == p || path.StartsWith(p + "/", StringComparison.Ordinal)).OrderBy(p => p.Length).ToList()) await LoadFolder(folder);
        }

        private void SelectFirstChild(string path)
        {
            var rows = filesRowOrder;
            var index = rows.FindIndex(r => r.Entry.RelativePath == path);
            if (index >= 0 && index + 1 < rows.Count && rows[index + 1].Depth > rows[index].Depth) SelectPath(rows[index + 1].Entry.RelativePath);
        }

        private void SelectPath(string path)
        {
            if (filesList?.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Tag as string == path) is { } item) { filesList.SelectedItem = item; filesList.ScrollIntoView(item); }
        }

        internal Task FilesSmokeOpenFolder(string path) => filesRows.TryGetValue(path, out var row) && !row.IsExpanded ? ToggleFolder(row.Entry) : Task.CompletedTask;
        internal Task FilesSmokePreview(string path) { Tree.SelectedPath = path; return ShowPreview(path, debounce: false); }

        private void RenderTree()
        {
            if (filesList is null) return;
            renderingTree = true;
            try
            {
                var hadFocus = filesList.XamlRoot is { } xamlRoot && FocusManager.GetFocusedElement(xamlRoot) is ListViewItem focused && filesList.Items.Contains(focused);
                var (allRows, hitCap) = Tree.Rows();
                // Every row is an element of its own, so a huge expanded tree is cut (a note says so).
                var (rows, drawnCapped) = FilePaneDrawing.TreeRows(allRows);
                filesList.Items.Clear(); filesRows.Clear(); filesRowOrder.Clear(); filesNames.Clear();
                var filtering = Tree.IsFiltering;
                ListViewItem? selected = null;
                foreach (var row in rows)
                {
                    filesRows[row.Entry.RelativePath] = row; filesRowOrder.Add(row);
                    var item = TreeItem(row, filtering);
                    filesList.Items.Add(item);
                    if (row.Entry.RelativePath == Tree.SelectedPath) selected = item;
                    if (row.Caption is { } caption && !filtering) filesList.Items.Add(CaptionItem(caption, row.Depth + 1, Tree.FolderErrors.ContainsKey(row.Entry.RelativePath) || Tree.Truncated.Contains(row.Entry.RelativePath)));
                }
                // The notes close the list and scroll with its rows (M/FilePaneView.swift:58-61).
                if (Tree.FolderErrors.TryGetValue("", out var rootError)) Note(rootError);
                else if (!filtering && Tree.Children.TryGetValue("", out var top) && top.Count == 0) Note(Locale.Get("files.tree.empty"));
                else if (!filtering && Tree.Truncated.Contains("")) Note(Locale.Get("files.tree.truncated", new Dictionary<string, string> { ["count"] = WorkspaceFiles.MaximumEntriesPerFolder.ToString(CultureInfo.CurrentCulture) }));
                if (filtering && rows.Count == 0) Note(Locale.Get("files.tree.noMatches"));
                if (!filtering && drawnCapped) Note(Locale.Get("files.tree.truncated", new Dictionary<string, string> { ["count"] = FilePaneDrawing.MaximumTreeRows.ToString(CultureInfo.CurrentCulture) }));
                if (filtering && hitCap) Note(Locale.Get("files.tree.moreResults", new Dictionary<string, string> { ["count"] = FilePaneTree.MaximumFilterResults.ToString("N0", CultureInfo.CurrentCulture) }));
                FitTreeNames(reset: false);
                if (selected is not null)
                {
                    // A rebuilt list keeps its place and, when it had it, the keyboard focus.
                    filesList.SelectedItem = selected;
                    filesList.ScrollIntoView(selected);
                    if (hadFocus) owner.DispatcherQueue.TryEnqueue(() => selected.Focus(FocusState.Keyboard));
                }
            }
            finally { renderingTree = false; }
        }

        /// <summary>A note after the rows (M/FilePaneView.swift:82-84): 10pt <c>ink2</c>, padding h12 v6; no row to choose.</summary>
        private void Note(string text) => filesList?.Items.Add(new ListViewItem
        {
            Content = new TextBlock { Text = text, FontSize = DesignMetrics.Type.Small, Foreground = owner.brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap },
            IsHitTestVisible = false, IsTabStop = false, MinHeight = 0, Padding = new Thickness(DesignMetrics.Spacing.Md, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Md, DesignMetrics.Spacing.Sm), HorizontalContentAlignment = HorizontalAlignment.Stretch,
        });

        /// <summary>
        /// Caps each row's name at the room its row leaves it, so a name too long for the tree trims; a trimmed
        /// name is then cut in its middle instead, and a filter result's path at its head (M/FilePaneView.swift:111, 114).
        /// <paramref name="reset"/> first puts every cut text back whole, for a tree whose width changed.
        /// </summary>
        private void FitTreeNames(bool reset)
        {
            if (filesList is not { ActualWidth: > 0 } list) return;
            foreach (var (block, full, taken) in filesNames)
            {
                if (reset && block.Text != full) block.Text = full;
                block.MaxWidth = double.IsNaN(taken) ? double.PositiveInfinity : Math.Max(24, list.ActualWidth - taken);
            }
        }

        /// <summary>
        /// The tree's rows the Mac way (M/FilePaneView.swift:118-121): the chosen row on <c>accent</c> × 0.18
        /// at radius 5 (in every pointer state, words staying <c>ink</c>), the subtle wash under the pointer, and no
        /// stock selection bar. Written once into the list's own resources, before it is shown.
        /// </summary>
        private void PaintFilesRows(ListView list)
        {
            var b = owner.brushes; var selected = b.Brush(DesignToken.Accent, DesignMetrics.Opacity.FileSelection); var ink = b.Brush(DesignToken.Ink);
            // The item presenter rounds its wash by this resource, not by the item's own corner.
            var values = new List<(string Key, object Value)> { ("ListViewItemCornerRadius", new CornerRadius(DesignMetrics.Radius.FileRow)) };
            foreach (var state in new[] { "", "PointerOver", "Pressed" })
            {
                values.Add(("ListViewItemBackgroundSelected" + state, selected));
                values.Add(("ListViewItemForegroundSelected" + state, ink));
                values.Add(("ListViewItemForeground" + state, ink));
                values.Add(("ListViewItemSelectionIndicator" + state + "Brush", b.Transparent));
            }
            values.Add(("ListViewItemBackgroundPointerOver", b.Subtle)); values.Add(("ListViewItemBackgroundPressed", b.Subtle));
            owner.SetResourcesOnce(list, values);
        }

        /// <summary>
        /// One tree row (M/FilePaneView.swift:102-128), its parts 5 apart: the 8pt semibold chevron in a 10-wide
        /// column, the 11pt symbol in a 14-wide one (folders <c>accent</c>, files <c>ink2</c>), the 11pt name cut in
        /// the middle, a link's 8pt arrow and, among filter results, the path in 9 mono cut at its head, both in the tertiary ink (:112, :114);
        /// indented 8 + depth × 14, v4 around the 13-high line (21 in all), radius 5; build output and dependencies
        /// at 0.55.
        /// </summary>
        private ListViewItem TreeItem(FilePaneTree.Row row, bool filtering)
        {
            const double gap = 5, chevronWidth = 10, symbolWidth = 14, rowMargin = 4, trailing = 8;
            var b = owner.brushes; var entry = row.Entry;
            var indent = FilesRowIndent + row.Depth * FilesDepthIndent;
            var line = new Grid { Margin = new Thickness(rowMargin + indent, 0, 0, 0), Opacity = entry.IsNoise ? FilesNoiseOpacity : 1 };
            foreach (var width in new[] { new GridLength(chevronWidth), new GridLength(gap + symbolWidth), GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) }) line.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            void Put(FrameworkElement part, int column)
            {
                part.Margin = new Thickness(column == 0 ? 0 : gap, 0, 0, 0); part.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(part, column); line.Children.Add(part);
            }
            if (entry.IsDirectory && !filtering) Put(new FontIcon { Glyph = row.IsExpanded ? "\uE70D" : "\uE76C", FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = b.Brush(DesignToken.Ink2), HorizontalAlignment = HorizontalAlignment.Center }, 0);
            var symbol = entry.IsDirectory ? new FontIcon { Glyph = "\uE8B7", FontSize = 13, Foreground = b.Brush(DesignToken.Accent) } : FileSymbol(entry.Name, 11, b.Brush(DesignToken.Ink2));
            symbol.HorizontalAlignment = HorizontalAlignment.Center; Put(symbol, 1);
            var name = new TextBlock { Text = entry.Name, FontSize = DesignMetrics.Type.Pill, LineHeight = FilesRowLine, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Foreground = b.Brush(DesignToken.Ink), TextTrimming = TextTrimming.CharacterEllipsis };
            Put(name, 2);
            var taken = rowMargin + indent + chevronWidth + gap + symbolWidth + gap + trailing + rowMargin;
            if (entry.IsSymlink) { var link = PanelSymbol.TurnUpRight(b.Tertiary); Put(link, 3); taken += gap + link.Width; }
            if (filtering && entry.RelativePath != entry.Name)
            {
                var path = new TextBlock { Text = entry.RelativePath, FontSize = 9, FontFamily = MonoFont, Foreground = b.Tertiary, TextTrimming = TextTrimming.CharacterEllipsis };
                Put(path, 4); taken += gap + FilesFilterPathRoom;
                path.IsTextTrimmedChanged += (block, _) => { if (block.IsTextTrimmed && block.ActualWidth > 0) MiddleTrim.Fit(block, entry.RelativePath, block.ActualWidth, head: true); };
                filesNames.Add((path, entry.RelativePath, double.NaN));
            }
            name.IsTextTrimmedChanged += (block, _) => { if (block.IsTextTrimmed && double.IsFinite(block.MaxWidth)) MiddleTrim.Fit(block, entry.Name, block.MaxWidth); };
            filesNames.Add((name, entry.Name, taken));
            // The list draws a row's wash 4 in from its sides and 2 in from its top and foot (FilesRowWash): the 4 is
            // the Mac's own inset, and each row reaches 2 over its neighbours so the wash is the whole 21 of the row.
            var item = new ListViewItem
            {
                Content = line, Tag = entry.RelativePath, MinHeight = FilesRowLine + 8 + 2 * FilesRowWash, Padding = new Thickness(0, DesignMetrics.Spacing.Xs + FilesRowWash, trailing + rowMargin, DesignMetrics.Spacing.Xs + FilesRowWash), Margin = new Thickness(0, -FilesRowWash, 0, -FilesRowWash),
                CornerRadius = new CornerRadius(DesignMetrics.Radius.FileRow), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetAutomationId(item, "files-row-" + entry.RelativePath);
            AutomationProperties.SetName(item, entry.Name);
            ToolTipService.SetToolTip(item, entry.RelativePath);
            if (entry.IsDirectory)
                item.Tapped += async (_, _) => { if (!Tree.IsFiltering) await ToggleFolder(entry); };
            return item;
        }

        /// <summary>
        /// An opened folder's caption (M/FilePaneView.swift:133-136): 10pt in the tertiary ink, 29 further in than its rows, v3.
        /// One that <paramref name="reports"/> a folder that could not be read or a list that was cut has to be read, so it keeps the secondary ink.
        /// </summary>
        private ListViewItem CaptionItem(string text, int depth, bool reports = false) => new()
        {
            Content = new TextBlock { Text = text, FontSize = DesignMetrics.Type.Small, Foreground = reports ? owner.brushes.Brush(DesignToken.Ink2) : owner.brushes.Tertiary, Margin = new Thickness(FilesRowIndent + depth * FilesDepthIndent + 29, 0, 0, 0), TextWrapping = TextWrapping.Wrap },
            IsHitTestVisible = false, IsTabStop = false, MinHeight = 18, Padding = new Thickness(0, DesignMetrics.Spacing.Xxs, 0, DesignMetrics.Spacing.Xxs),
        };

        /// <summary>The tree's indents and the faded rows' opacity (M/FilePaneView.swift:118-119).</summary>
        internal const double FilesRowIndent = 8, FilesDepthIndent = 14, FilesNoiseOpacity = 0.55;
        /// <summary>A row's 11pt line as the Mac lays it out (13 high), and the room a filter result's name leaves its path.</summary>
        internal const double FilesRowLine = 13, FilesFilterPathRoom = 48;
        /// <summary>How far in from a list item's top and foot WinUI's item presenter draws its wash (it is 4 in from the sides).</summary>
        internal const double FilesRowWash = 2;

        /// <summary>
        /// A file's symbol (M/FilePaneView.swift:139-148), drawn about as large as the Mac's at <paramref name="size"/>
        /// points of type: Markdown a page with a picture, an image a photo, plain text a page of lines, other source
        /// the code brackets, anything else a blank page.
        /// </summary>
        private static FrameworkElement FileSymbol(string name, double size, Brush ink) => FilePreviewClassifier.KindForName(name) switch
        {
            { Tag: FilePreviewKindTag.Markdown } => PanelSymbol.RichText(ink, size),
            { Tag: FilePreviewKindTag.Image } => new FontIcon { Glyph = "\uEB9F", FontSize = size + 2, Foreground = ink },
            { Tag: FilePreviewKindTag.Source, Language: SourceLanguage.Plain } => new FontIcon { Glyph = "\uF000", FontSize = size + 2, Foreground = ink },
            { Tag: FilePreviewKindTag.Source } => PanelSymbol.Code(ink, size),
            _ => new FontIcon { Glyph = "\uE7C3", FontSize = size + 2, Foreground = ink },
        };

        /// <summary>
        /// Reads the file about 100 ms after it is chosen, off the UI thread. A newer choice
        /// cancels the older read, and an older result arriving late is dropped by its number.
        /// </summary>
        private async Task ShowPreview(string path, bool debounce)
        {
            var request = ++previewRequest;
            previewCancel?.Cancel();
            previewCancel?.Dispose();
            var cancel = previewCancel = new CancellationTokenSource();
            Tree.PreviewPath = path;
            if (debounce) { try { await Task.Delay(100, cancel.Token); } catch (OperationCanceledException) { return; } }
            if (request != previewRequest || !FilesAlive) return;
            SetPreviewHeader(path.Split('/')[^1], null, path);
            previewBanners?.Children.Clear(); ClearPreviewTools();
            // The Mac's small progress view (M/FilePaneView.swift:233).
            if (previewContent is not null) previewContent.Child = new ProgressRing { IsActive = true, Width = 16, Height = 16, MinWidth = 0, MinHeight = 0, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var workspaceRoot = filesRoot;
            var light = owner.service.Snapshot.Theme == "light";
            FilesPrepared prepared;
            try { prepared = await Task.Run(() => Prepare(workspaceRoot, path, light, cancel.Token), cancel.Token); }
            catch (OperationCanceledException) { return; }
            // This runs from async void handlers (selection, keys): any failure becomes the
            // "can't read" card instead of ending the app.
            catch (Exception) { if (request == previewRequest && FilesAlive) ShowPreviewFailed(); return; }
            if (request != previewRequest || !FilesAlive) return;
            shown = prepared;
            try { await DrawPreview(prepared, request); }
            catch (Exception) { if (request == previewRequest && FilesAlive) ShowPreviewFailed(); }
        }

        /// <summary>
        /// Redraws the Markdown preview on screen in the current theme. Its RTF bakes the theme's
        /// token colours in (TranscriptRtf), so a toggle renders it again, in place, keeping the view.
        /// </summary>
        internal void RethemeFilesMarkdown()
        {
            var light = owner.service.Snapshot.Theme == "light";
            if (markdownShown is not { } markdown || markdown.Light == light || !ReferenceEquals(previewContent?.Child, markdown.View)) return;
            var rtf = TranscriptRtf.RenderMarkdown(markdown.Source, light);
            SetMarkdownRtf(markdown.View, rtf);
            markdownShown = (markdown.View, markdown.Source, light);
        }

        /// <summary>A file that could not be read shows its reason alone, with no head (M/FilePaneView.swift:54-58, 280-281).</summary>
        private void ShowPreviewFailed(string? reason = null)
        {
            previewBanners?.Children.Clear(); ClearPreviewTools();
            SetPreviewHeader("", null);
            if (previewContent is not null) previewContent.Child = Notice("\uE7BA", 28, reason ?? Locale.Get("files.preview.failed"));
        }

        private static FilesPrepared Prepare(string root, string path, bool light, CancellationToken cancellation)
        {
            var data = FilePreviewLoader.Load(root, path, cancellation);
            if (data.Text is not { } full) return new(data, [], "", false, false, 0, null);
            // Markdown renders from the whole file (it is only rendered under 128 KB); the
            // source view draws at most FilePaneDrawing.MaximumSourceUnits.
            var markdown = data.Kind.Tag == FilePreviewKindTag.Markdown && data.MarkdownRenderable ? TranscriptRtf.RenderMarkdown(full, light) : null;
            var (text, renderCapped) = FilePaneDrawing.SourceText(full);
            if (renderCapped) data = data with { Text = text };
            var (starts, longest) = SourceLines.Scan(text);
            var language = data.Kind.Tag == FilePreviewKindTag.Source ? data.Kind.Language : SourceLanguage.Plain;
            cancellation.ThrowIfCancellationRequested();
            var tokens = SourceHighlighter.Tokens(text, language);
            var capped = language != SourceLanguage.Plain && text.Length > SourceHighlighter.MaximumUnits;
            var units = SourceHighlighter.MaximumUnits;
            // Each coloured span is a text run the UI thread must create; past this many the
            // rest stays plain and the banner says how far colouring reaches.
            if (tokens.Count > MaximumColouredRuns)
            {
                tokens = tokens[..MaximumColouredRuns];
                capped = true; units = tokens[^1].Location + tokens[^1].Length;
            }
            cancellation.ThrowIfCancellationRequested();
            var numbers = string.Join('\n', Enumerable.Range(1, starts.Count));
            return new(data, tokens, numbers, longest > SourceLines.WrapThreshold, capped, units, markdown, renderCapped, markdown is null ? null : full, light);
        }

        /// <summary>
        /// The head names the file (and shows only while one is chosen, as on the Mac): its symbol in <c>accent</c>,
        /// its name, its path cut in the middle to the room the names have, and its encoding when it is text.
        /// </summary>
        private void SetPreviewHeader(string title, TextEncodingKind? encoding, string? relativePath = null)
        {
            var shows = title.Length > 0;
            previewHeaderPath = shows ? relativePath : null;
            if (previewTitle is not null) previewTitle.Text = title;
            if (previewPath is not null)
            {
                ToolTipService.SetToolTip(previewPath, relativePath);
                if (previewNames is { ActualWidth: > 0 } names && relativePath is not null) MiddleTrim.Fit(previewPath, relativePath, names.ActualWidth);
                else { previewPath.MaxWidth = double.PositiveInfinity; previewPath.Text = relativePath ?? ""; }
            }
            if (previewIcon is not null) previewIcon.Child = shows ? FileSymbol(title, 12, owner.brushes.Brush(DesignToken.Accent)) : null;
            if (previewHeader is not null) previewHeader.Visibility = shows ? Visibility.Visible : Visibility.Collapsed;
            if (previewHeaderLine is not null) previewHeaderLine.Visibility = shows ? Visibility.Visible : Visibility.Collapsed;
            if (previewEncoding is null) return;
            previewEncoding.Text = encoding is { } kind ? TextEncodings.DisplayName(kind) : "";
            previewEncoding.Visibility = encoding is null ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(previewEncoding, encoding is { } named ? Locale.Get("files.preview.encoding") + " " + TextEncodings.DisplayName(named) : "");
        }

        private void ShowPlaceholder()
        {
            SetPreviewHeader("", null);
            if (previewContent is not null) previewContent.Child = Notice("\uE90C", 32, Locale.Get("files.preview.placeholder"));
        }

        /// <summary>
        /// The empty and the failed preview (M/FilePaneView.swift:306-313): a light symbol (the Mac's at 24pt,
        /// drawn at <paramref name="size"/> to be as large) over 12pt <c>ink2</c> words, centred, 6 apart, padding 20.
        /// </summary>
        private FrameworkElement Notice(string glyph, double size, string text)
        {
            var notice = new StackPanel { Spacing = DesignMetrics.Spacing.Sm, Padding = new Thickness(DesignMetrics.Spacing.Lg), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            notice.Children.Add(new FontIcon { Glyph = glyph, FontSize = size, FontWeight = FontWeights.Light, Foreground = owner.brushes.Brush(DesignToken.Ink2) });
            notice.Children.Add(new TextBlock { Text = text, FontSize = DesignMetrics.Type.Block, Foreground = owner.brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
            AutomationProperties.SetAutomationId(notice, "files-notice");
            return notice;
        }

        /// <summary>A centred 12pt <c>ink2</c> sentence, for the reference preview's states.</summary>
        private TextBlock Centered(string text) => new() { Text = text, FontSize = DesignMetrics.Type.Block, Foreground = owner.brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(DesignMetrics.Spacing.Xl) };

        /// <summary>A note over the preview (M/FilePaneView.swift:295-304): an info symbol and 10pt <c>ink2</c> words on <c>waitSoft</c>, padding h12 v6.</summary>
        private void Banner(string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
            row.Children.Add(new FontIcon { Glyph = "\uE946", FontSize = DesignMetrics.Type.Small, Foreground = owner.brushes.Brush(DesignToken.Ink2), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, DesignMetrics.Spacing.Xxs, 0, 0) });
            row.Children.Add(new TextBlock { Text = text, FontSize = DesignMetrics.Type.Small, Foreground = owner.brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap });
            previewBanners?.Children.Add(new Border { Background = owner.brushes.Brush(DesignToken.WaitSoft), Padding = new Thickness(DesignMetrics.Spacing.Md, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Md, DesignMetrics.Spacing.Sm), Child = row });
        }

        private async Task DrawPreview(FilesPrepared prepared, int request)
        {
            var data = prepared.Data;
            previewBanners?.Children.Clear(); ClearPreviewTools();
            if (data.Failure is { } failure) { ShowPreviewFailed(failure); return; }
            SetPreviewHeader(data.Name, data.Encoding, data.RelativePath);
            if (previewContent is null) return;
            switch (data.Kind.Tag)
            {
                case FilePreviewKindTag.Source:
                    SourceBanners(prepared);
                    previewContent.Child = SourceView(prepared);
                    break;
                case FilePreviewKindTag.Markdown:
                    if (!data.MarkdownRenderable) Banner(Locale.Get("files.markdown.tooLarge"));
                    else MarkdownToggle(prepared);
                    if (prepared.MarkdownRtf is { } rtf && !showMarkdownSource)
                    {
                        // The Mac's notes on the text stand over the rendered page too (M/FilePaneView.swift:240-242); of them only this one can apply to a file small enough to render.
                        if (prepared.Wraps) Banner(Locale.Get("files.preview.wrapped", new Dictionary<string, string> { ["count"] = SourceLines.WrapThreshold.ToString("N0", CultureInfo.CurrentCulture) }));
                        var view = MarkdownView(rtf); previewContent.Child = view;
                        markdownShown = (view, prepared.MarkdownSource!, prepared.MarkdownLight);
                        // Prepared before a theme toggle (or shown again from the source switch): draw it in the theme now showing.
                        RethemeFilesMarkdown();
                    }
                    else { SourceBanners(prepared); previewContent.Child = SourceView(prepared); }
                    break;
                case FilePreviewKindTag.Image:
                    var image = await ImageView(data, request);
                    if (request == previewRequest) previewContent.Child = image;
                    break;
                default:
                    previewContent.Child = UnsupportedCard(data, data.Reason);
                    break;
            }
        }

        private void SourceBanners(FilesPrepared prepared)
        {
            if (prepared.RenderCapped) Banner(Locale.Get("files.preview.renderCapped", new Dictionary<string, string> { ["count"] = (FilePaneDrawing.MaximumSourceUnits / 1_024).ToString(CultureInfo.CurrentCulture) }));
            else if (prepared.Data.Truncated) Banner(Locale.Get("files.preview.truncated"));
            if (prepared.HighlightCapped) Banner(Locale.Get("files.preview.highlightCapped", new Dictionary<string, string> { ["count"] = prepared.HighlightUnits.ToString("N0", CultureInfo.CurrentCulture) }));
            if (prepared.Wraps) Banner(Locale.Get("files.preview.wrapped", new Dictionary<string, string> { ["count"] = SourceLines.WrapThreshold.ToString("N0", CultureInfo.CurrentCulture) }));
        }

        /// <summary>
        /// The Preview | Source switch of a Markdown file (M/FilePaneView.swift:194-199). The Mac draws the system's
        /// segmented picker, whose chosen side carries the tint (docs/design-system/screens/08-files-markdown-dark.webp):
        /// here a <c>segmentTrack</c> at radius 8, padding 2, with 12pt words, the chosen side <c>accent</c> under
        /// <c>onAccent</c> at radius 6. The switch is rebuilt with each preview, so the chosen side is drawn on its
        /// chip as it is built.
        /// </summary>
        private void MarkdownToggle(FilesPrepared prepared)
        {
            var b = owner.brushes;
            var track = new StackPanel { Orientation = Orientation.Horizontal, Padding = new Thickness(DesignMetrics.Spacing.Xxs), CornerRadius = new CornerRadius(DesignMetrics.Radius.Row), Background = b.SegmentTrack, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(track, "files-markdown-mode");
            Button Choice(string key, bool source)
            {
                var chosen = showMarkdownSource == source;
                var chip = new Border { Padding = new Thickness(DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xxs, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xxs), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), Background = chosen ? b.Brush(DesignToken.Accent) : b.Transparent, Child = new TextBlock { Text = Locale.Get(key), FontSize = DesignMetrics.Type.Block, FontWeight = chosen ? FontWeights.Medium : FontWeights.Normal, Foreground = b.Brush(chosen ? DesignToken.OnAccent : DesignToken.Ink) } };
                var button = new Button { Content = chip, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment) };
                owner.PaintPlainButton(button, b.Transparent, b.Transparent);
                AutomationProperties.SetName(button, Locale.Get(key));
                button.Click += async (_, _) => { showMarkdownSource = source; await DrawPreview(prepared, previewRequest); };
                return button;
            }
            track.Children.Add(Choice("files.markdown.rendered", false));
            track.Children.Add(Choice("files.markdown.source", true));
            AddPreviewTool(track);
        }

        /// <summary>
        /// The source (M/FilePaneView.swift:365-453, 456-511): 12pt mono in lines of 15, set in 6 and 8 from the gutter
        /// and the top (and the text view's own 5 of line padding), in the highlighter's colours and selectable for
        /// copying. Beside it the line numbers: 10pt tabular in the tertiary ink (:493), right-aligned 8 from the end of a gutter
        /// digits × 7 + 16 wide that closes with a 1pt <c>line</c> and stays in place while the text scrolls sideways.
        /// </summary>
        private FrameworkElement SourceView(FilesPrepared prepared)
        {
            const double insetX = DesignMetrics.Spacing.Sm + 5, insetY = DesignMetrics.Spacing.Md, numberEnd = 8;
            var b = owner.brushes; var text = prepared.Data.Text ?? "";
            var body = new RichTextBlock
            {
                FontFamily = MonoFont, FontSize = 12, LineHeight = FilesLineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Foreground = b.Brush(DesignToken.Ink),
                IsTextSelectionEnabled = true, TextWrapping = prepared.Wraps ? TextWrapping.Wrap : TextWrapping.NoWrap, Margin = new Thickness(insetX, insetY, insetX, insetY),
            };
            var paragraph = new Paragraph();
            var position = 0;
            foreach (var token in prepared.Tokens)
            {
                if (token.Location > position) paragraph.Inlines.Add(new Run { Text = text[position..token.Location] });
                paragraph.Inlines.Add(new Run { Text = text.Substring(token.Location, token.Length), Foreground = TokenBrush(token.Kind) });
                position = token.Location + token.Length;
            }
            if (position < text.Length) paragraph.Inlines.Add(new Run { Text = text[position..] });
            body.Blocks.Add(paragraph);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var viewer = new ScrollViewer
            {
                Content = grid,
                HorizontalScrollBarVisibility = prepared.Wraps ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
                HorizontalScrollMode = prepared.Wraps ? ScrollMode.Disabled : ScrollMode.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            // A wrapped line has no number on its continuation, as on macOS; with wrapping on,
            // numbers could not line up, so the column is left out.
            if (!prepared.Wraps)
            {
                var digits = Math.Max(2, prepared.LineNumbers.Length - prepared.LineNumbers.LastIndexOf('\n') - 1);
                // Each number sits in the middle of its line; the 10pt digits ride 1 higher in a block line than the 12pt mono does.
                var numbers = new TextBlock
                {
                    FontSize = DesignMetrics.Type.Small, LineHeight = FilesLineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Foreground = b.Tertiary, TextAlignment = TextAlignment.Right, Text = prepared.LineNumbers, Margin = new Thickness(0, insetY - 1, numberEnd - DesignMetrics.Stroke.Line, insetY),
                };
                Typography.SetNumeralAlignment(numbers, FontNumeralAlignment.Tabular);
                AutomationProperties.SetAccessibilityView(numbers, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                // The gutter carries the card's own fill and slides with the view, so the text passes under it.
                var slide = new TranslateTransform();
                var gutter = new Border
                {
                    Width = digits * 7 + 16, Child = numbers, RenderTransform = slide, Background = b.Brush(DesignToken.Card),
                    BorderBrush = b.Brush(DesignToken.Line), BorderThickness = new Thickness(0, 0, DesignMetrics.Stroke.Line, 0),
                };
                Canvas.SetZIndex(gutter, 1);
                grid.Children.Add(gutter);
                viewer.ViewChanged += (_, _) => slide.X = viewer.HorizontalOffset;
                // The gutter and its line run to the foot of the pane under a short file, as the Mac's ruler does.
                viewer.SizeChanged += (_, args) => grid.MinHeight = args.NewSize.Height;
            }
            Grid.SetColumn(body, 1); grid.Children.Add(body);
            return viewer;
        }

        /// <summary>
        /// A source span's colour (M/FilePaneView.swift:447-450): keywords pink, strings orange, numbers purple
        /// (the fixture's <c>windowsOnly.syntax</c>), comments <c>ink2</c>; shared brushes the theme recolours.
        /// </summary>
        private SolidColorBrush TokenBrush(SourceTokenKind kind) => owner.brushes.Syntax(kind switch
        {
            SourceTokenKind.Keyword => "keyword",
            SourceTokenKind.String => "string",
            SourceTokenKind.Comment => "comment",
            _ => "number",
        })!;

        /// <summary>
        /// Rendered with the transcript's Markdown renderer: no images, no local links. Padding
        /// <see cref="FilesMarkdownInset"/>, and the page stops 860 from the pane's leading edge however wide the
        /// pane is (M/FilePaneView.swift:246).
        /// </summary>
        private static RichEditBox MarkdownView(string rtf)
        {
            const double inset = FilesMarkdownInset, page = 860;
            var view = new RichEditBox { IsReadOnly = true, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), Padding = new Thickness(inset) };
            view.SizeChanged += (_, args) =>
            {
                var trailing = Math.Max(inset, args.NewSize.Width - page + inset);
                if (view.Padding.Right != trailing) view.Padding = new Thickness(inset, inset, trailing, inset);
            };
            AutomationProperties.SetAutomationId(view, "files-markdown-preview");
            ScrollViewer.SetVerticalScrollBarVisibility(view, ScrollBarVisibility.Auto);
            AutomationProperties.SetName(view, Locale.Get("files.markdown.rendered"));
            SetMarkdownRtf(view, rtf);
            // A RichEditBox paints its theme foreground over the whole document when the theme changes or it
            // is shown again, wiping the RTF's colours; the RTF it shows is set again once it has done so.
            void Repaint() => view.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (view.Tag is string current) SetMarkdownRtf(view, current); });
            view.ActualThemeChanged += (_, _) => Repaint();
            view.Loaded += (_, _) => Repaint();
            return view;
        }

        /// <summary>Shows <paramref name="rtf"/> in a read-only Markdown view and remembers it for a repaint, keeping the reader's place.</summary>
        private static void SetMarkdownRtf(RichEditBox view, string rtf)
        {
            var offset = AgentTranscript.Descendant<ScrollViewer>(view)?.VerticalOffset ?? 0;
            try { view.IsReadOnly = false; view.Document.SetText(TextSetOptions.FormatRtf, rtf); }
            finally { view.IsReadOnly = true; }
            view.Tag = rtf;
            if (offset > 0) view.DispatcherQueue.TryEnqueue(() => AgentTranscript.Descendant<ScrollViewer>(view)?.ChangeView(null, offset, null, true));
        }

        /// <summary>
        /// Bitmaps decode through WIC: fitted from a decode at most 4,096 pixels on its long
        /// side, at 1:1 or zoom from a full decode made only when asked for and only under
        /// 100 MP; nothing over 250 MP decodes at all. svg (already checked for outside
        /// references) uses the shared bounded SVG rasterizer; PDF shows its first page.
        /// </summary>
        private async Task<FrameworkElement> ImageView(FilePreviewData data, int request)
        {
            // A newer choice owns the header and banners: a late decode writes nothing there.
            var stale = new Border();
            var bytes = data.ImageBytes ?? [];
            FilesPreviewPixels = null;
            var extension = FilePreviewClassifier.FileExtension(data.Name);
            try
            {
                if (extension == "svg")
                {
                    var raster = await NativeSvgRaster.Render(bytes, FilePreviewClassifier.MaximumFitPixels);
                    if (request != previewRequest) return stale;
                    var svg = new BitmapImage(); using var rendered = await Buffer(raster.Png); await svg.SetSourceAsync(rendered);
                    return FittedImage(new Image { Source = svg, Stretch = Stretch.Uniform });
                }
                if (extension == "pdf")
                {
                    var document = await PdfDocument.LoadFromStreamAsync(await Buffer(bytes));
                    if (document.PageCount == 0) return UnsupportedCard(data, Locale.Get("files.preview.failed"));
                    using var page = document.GetPage(0);
                    if (!FilePreviewClassifier.IsDrawable(page.Size.Width, page.Size.Height)) return UnsupportedCard(data, null);
                    var scale = Math.Min(2.0, FilePreviewClassifier.MaximumFitPixels / Math.Max(page.Size.Width, page.Size.Height));
                    var rendered = new InMemoryRandomAccessStream();
                    await page.RenderToStreamAsync(rendered, new PdfPageRenderOptions { DestinationWidth = (uint)Math.Max(1, page.Size.Width * scale), DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale) });
                    rendered.Seek(0);
                    var pageImage = new BitmapImage(); await pageImage.SetSourceAsync(rendered);
                    if (request != previewRequest) return stale;
                    PixelsLabel((long)page.Size.Width, (long)page.Size.Height);
                    return FittedImage(new Image { Source = pageImage, Stretch = Stretch.Uniform, MaxWidth = page.Size.Width, MaxHeight = page.Size.Height });
                }
                var stream = await Buffer(bytes);
                var decoder = await BitmapDecoder.CreateAsync(stream);
                long width = decoder.OrientedPixelWidth, height = decoder.OrientedPixelHeight;
                if (request != previewRequest) return stale;
                if (FilePreviewClassifier.PixelCount(width, height) is not { } pixels || pixels > FilePreviewClassifier.MaximumDecodePixels)
                    return UnsupportedCard(data, Locale.Get("files.preview.tooManyPixels", new Dictionary<string, string> { ["count"] = (FilePreviewClassifier.MaximumDecodePixels / 1_000_000).ToString(CultureInfo.CurrentCulture) }));
                var fit = new BitmapImage();
                if (Math.Max(width, height) > FilePreviewClassifier.MaximumFitPixels)
                {
                    if (width >= height) fit.DecodePixelWidth = FilePreviewClassifier.MaximumFitPixels; else fit.DecodePixelHeight = FilePreviewClassifier.MaximumFitPixels;
                }
                stream.Seek(0);
                await fit.SetSourceAsync(stream);
                if (request != previewRequest) return stale;
                var fullAllowed = pixels <= FilePreviewClassifier.MaximumFullPixels;
                if (!fullAllowed) Banner(Locale.Get("files.image.fitOnly", new Dictionary<string, string> { ["count"] = (FilePreviewClassifier.MaximumFullPixels / 1_000_000).ToString(CultureInfo.CurrentCulture) }));
                PixelsLabel(width, height);
                FilesPreviewPixels = (width, height);
                return ZoomableImage(fit, bytes, width, height, fullAllowed);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return UnsupportedCard(data, Locale.Get("files.preview.failed"));
            }
        }

        private static async Task<InMemoryRandomAccessStream> Buffer(byte[] bytes)
        {
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync(); await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            return stream;
        }

        /// <summary>The image's size in the head (M/FilePaneView.swift:204-205): 10pt tabular <c>ink2</c>, before the zoom tools.</summary>
        private void PixelsLabel(long width, long height)
        {
            var label = new TextBlock { Text = Locale.Get("files.image.pixels", new Dictionary<string, string> { ["width"] = width.ToString(CultureInfo.InvariantCulture), ["height"] = height.ToString(CultureInfo.InvariantCulture) }), FontSize = DesignMetrics.Type.Small, Foreground = owner.brushes.Brush(DesignToken.Ink2), VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
            AddPreviewTool(label, first: true);
        }

        /// <summary>An image lies on the page colour, 12 clear of the pane's edges (M/FilePaneView.swift:342-352).</summary>
        private const double FilesImageInset = 12;

        private FrameworkElement FittedImage(Image image) => new Border { Child = image, Padding = new Thickness(FilesImageInset), Background = owner.brushes.Brush(DesignToken.Page) };

        /// <summary>Fit (never larger than 1:1), zoom out, 1:1 and zoom in, the macOS controls.</summary>
        private FrameworkElement ZoomableImage(BitmapImage fit, byte[] bytes, long width, long height, bool fullAllowed)
        {
            var scale = owner.root.XamlRoot?.RasterizationScale ?? 1;
            double pointsWide = width / scale, pointsHigh = height / scale;
            var image = new Image { Source = fit, Stretch = Stretch.Uniform, MaxWidth = pointsWide, MaxHeight = pointsHigh };
            var viewer = new ScrollViewer { Content = image, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollMode = ScrollMode.Disabled, Padding = new Thickness(FilesImageInset), Background = owner.brushes.Brush(DesignToken.Page) };
            BitmapImage? full = null; double? zoom = null;
            async Task Apply(double? next)
            {
                if (next is { } wanted)
                {
                    var largest = FilePreviewClassifier.MaximumZoomPoints / Math.Max(pointsWide, pointsHigh);
                    zoom = Math.Clamp(wanted, .05, Math.Max(.05, largest));
                    if (full is null)
                    {
                        full = new BitmapImage();
                        await full.SetSourceAsync(await Buffer(bytes));
                    }
                    image.Source = full; image.Stretch = Stretch.Fill; image.MaxWidth = image.MaxHeight = double.PositiveInfinity;
                    image.Width = pointsWide * zoom.Value; image.Height = pointsHigh * zoom.Value;
                    viewer.HorizontalScrollBarVisibility = viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                    viewer.HorizontalScrollMode = viewer.VerticalScrollMode = ScrollMode.Auto;
                }
                else
                {
                    zoom = null;
                    image.Source = fit; image.Stretch = Stretch.Uniform; image.Width = image.Height = double.NaN; image.MaxWidth = pointsWide; image.MaxHeight = pointsHigh;
                    viewer.HorizontalScrollBarVisibility = viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                    viewer.HorizontalScrollMode = viewer.VerticalScrollMode = ScrollMode.Disabled;
                }
            }
            double Current() => zoom ?? (image.ActualWidth > 0 ? image.ActualWidth / pointsWide : 1);
            // The head's four tools (M/FilePaneView.swift:206-216): 12pt symbols and the 10pt medium "1:1", 8 apart,
            // in the head's ink; all four rest for an image too large to draw at full size.
            var ink = owner.brushes.Brush(DesignToken.Ink);
            void Tool(FrameworkElement face, string key, Func<Task> action)
            {
                var button = PreviewToolButton(face, key, ink, fullAllowed);
                button.Click += async (_, _) => await owner.Act(action);
                AddPreviewTool(button);
            }
            Tool(new FontIcon { Glyph = "\uE71F", FontSize = 15 }, "files.image.zoomOut", () => Apply(Current() / 1.25));
            Tool(PanelSymbol.Fit(fullAllowed ? ink : owner.brushes.Brush(DesignToken.Ink3)), "files.image.fit", () => Apply(null));
            Tool(new TextBlock { Text = "1:1", FontSize = DesignMetrics.Type.Small, FontWeight = FontWeights.Medium }, "files.image.actualSize", () => Apply(1));
            Tool(new FontIcon { Glyph = "\uE8A3", FontSize = 15 }, "files.image.zoomIn", () => Apply(Current() * 1.25));
            AutomationProperties.SetName(image, Locale.Get("files.image.pixels", new Dictionary<string, string> { ["width"] = width.ToString(CultureInfo.InvariantCulture), ["height"] = height.ToString(CultureInfo.InvariantCulture) }));
            return viewer;
        }

        /// <summary>
        /// "This file can't be previewed" (M/FilePaneView.swift:257-279), its parts 8 apart, padding 24: the symbol, the
        /// 13pt semibold line, the name, size and modified time as a small table, the reason, and a File Explorer button.
        /// </summary>
        private FrameworkElement UnsupportedCard(FilePreviewData data, string? reason)
        {
            var card = new StackPanel { Spacing = DesignMetrics.Spacing.Sm, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(DesignMetrics.Spacing.Xl) };
            AutomationProperties.SetAutomationId(card, "files-unsupported");
            var b = owner.brushes;
            // doc.questionmark at 30 light: a blank page with the question mark on it.
            var symbol = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            symbol.Children.Add(new FontIcon { Glyph = "\uE7C3", FontSize = 34, FontWeight = FontWeights.Light, Foreground = b.Brush(DesignToken.Ink2) });
            symbol.Children.Add(new TextBlock { Text = "?", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = b.Brush(DesignToken.Ink2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, DesignMetrics.Spacing.Xs, 0, 0) });
            AutomationProperties.SetAccessibilityView(symbol, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            card.Children.Add(symbol);
            card.Children.Add(new TextBlock { Text = Locale.Get("files.preview.unsupported"), FontSize = DesignMetrics.Type.Title, FontWeight = FontWeights.SemiBold, Foreground = b.Brush(DesignToken.Ink), HorizontalAlignment = HorizontalAlignment.Center });
            card.Children.Add(new TextBlock { Text = data.Name, FontSize = DesignMetrics.Type.Block, Foreground = b.Brush(DesignToken.Ink), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxLines = 2 });
            // Size and modified time as a two-column grid of an ink2 label and its ink value (M/FilePaneView.swift:262-273).
            var facts = new Grid { ColumnSpacing = DesignMetrics.Spacing.Md, RowSpacing = DesignMetrics.Spacing.Xxs, HorizontalAlignment = HorizontalAlignment.Center };
            facts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); facts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            void Fact(string label, string value)
            {
                facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var row = facts.RowDefinitions.Count - 1;
                var name = new TextBlock { Text = label, FontSize = DesignMetrics.Type.Pill, Foreground = b.Brush(DesignToken.Ink2) }; Grid.SetRow(name, row); facts.Children.Add(name);
                var text = new TextBlock { Text = value, FontSize = DesignMetrics.Type.Pill, Foreground = b.Brush(DesignToken.Ink) }; Grid.SetRow(text, row); Grid.SetColumn(text, 1); facts.Children.Add(text);
            }
            Fact(Locale.Get("files.preview.size"), FormatBytes(data.Size));
            if (data.Modified is { } modified) Fact(Locale.Get("files.preview.modified"), modified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
            card.Children.Add(facts);
            if (reason is not null) card.Children.Add(new TextBlock { Text = reason, FontSize = DesignMetrics.Type.Pill, Foreground = b.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
            // The Mac's push button, 4 further under the rest.
            var reveal = owner.PushButton(Locale.Get("menu.showInExplorer"));
            reveal.HorizontalAlignment = HorizontalAlignment.Center; reveal.Margin = new Thickness(0, DesignMetrics.Spacing.Xs, 0, 0);
            AutomationProperties.SetAutomationId(reveal, "files-show-in-explorer");
            reveal.Click += (_, _) => RevealInExplorer(data.RelativePath);
            card.Children.Add(reveal);
            return card;
        }

        /// <summary>Selects the file in File Explorer — only a path that still resolves inside the workspace.</summary>
        private void RevealInExplorer(string relativePath)
        {
            if (WorkspaceFiles.Resolve(relativePath, filesRoot) is not { } path) return;
            try { using var _ = Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "/select,\"" + path + "\"", UseShellExecute = false }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { owner.error.Text = ex.Message; }
        }

        private static string FormatBytes(long bytes) => bytes switch
        {
            < 1_000 => bytes.ToString(CultureInfo.CurrentCulture) + " B",
            < 1_000_000 => (bytes / 1_000.0).ToString("0.#", CultureInfo.CurrentCulture) + " KB",
            < 1_000_000_000 => (bytes / 1_000_000.0).ToString("0.#", CultureInfo.CurrentCulture) + " MB",
            _ => (bytes / 1_000_000_000.0).ToString("0.##", CultureInfo.CurrentCulture) + " GB",
        };
    }
}
