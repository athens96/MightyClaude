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
        item.Icon = new FontIcon { Glyph = "" };
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
            var added = s with { Sessions = s.Sessions.Append(pane).ToList(), ActiveWorkspaceId = workspace.Id, ActiveSessionId = id };
            if (EffectiveLayout(added, workspace.Id) is not { } tree) return SaveLayoutSelection(added, workspace.Id, id);
            var placed = PaneLayout.Select(FilePaneKind.Place(tree, id, current), id);
            var next = SaveLayoutSelection(SaveLayout(added, workspace.Id, placed), workspace.Id, id);
            return LayoutMode(added, workspace.Id) == "focus" ? next : SaveLayoutMode(next, workspace.Id, placed.Kind == "split" ? "custom" : "tabs");
        });
        Render();
    });

    private sealed partial class PaneView
    {
        private const double FilesLineHeight = 18;
        private static readonly FontFamily MonoFont = new(DesignMetrics.Font.Mono);
        private bool filesAttached, renderingTree, showMarkdownSource;
        private Grid? filesHost;
        private TextBox? filesFilter;
        private ListView? filesList;
        private StackPanel? filesNotes, previewBanners, previewTools;
        private TextBlock? previewTitle, previewPath, previewEncoding;
        private FontIcon? previewIcon;
        private Border? previewContent;
        private Grid? previewHeader, filesFilterRow;
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

            filesHost = new Grid { ColumnSpacing = 0 };
            filesHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280), MinWidth = 180 });
            filesHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            filesHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            AutomationProperties.SetAutomationId(filesHost, "files-pane-" + id);

            var tree = BuildFilesTree();
            filesHost.Children.Add(tree);
            var divider = new Border { Background = owner.brushes.Brush(DesignToken.Line) };
            Grid.SetColumn(divider, 1); filesHost.Children.Add(divider);
            var preview = BuildFilesPreview();
            Grid.SetColumn(preview, 2); filesHost.Children.Add(preview);

            Grid.SetRow(filesHost, 0); Grid.SetRowSpan(filesHost, Math.Max(1, grid.RowDefinitions.Count));
            grid.Children.Add(filesHost);
            _ = StartFiles();
        }

        /// <summary>
        /// The tree column (M/FilePaneView.swift:31-81): the filter row on the subtle wash (a 10pt filter
        /// symbol, the 11pt field, the 11pt refresh), a <c>line</c> under it, then the rows.
        /// </summary>
        private Grid BuildFilesTree()
        {
            var b = owner.brushes;
            var panel = new Grid();
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var bar = filesFilterRow = new Grid { ColumnSpacing = 6, Padding = new Thickness(10, 7, 10, 7), Background = b.Subtle, BorderBrush = b.Brush(DesignToken.Line), BorderThickness = new Thickness(0, 0, 0, DesignMetrics.Stroke.Line) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.Children.Add(new FontIcon { Glyph = "\uE71C", FontSize = 10, Foreground = b.Brush(DesignToken.Ink3), VerticalAlignment = VerticalAlignment.Center });
            filesFilter = new TextBox { PlaceholderText = Locale.Get("files.tree.filter"), Text = Tree.Filter, FontSize = DesignMetrics.Type.Pill, BorderThickness = new Thickness(0), Padding = new Thickness(0), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
            var field = new List<(string Key, object Value)>();
            foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled" }) field.Add((key, b.Transparent));
            foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" }) field.Add((key, b.Brush(DesignToken.Ink)));
            foreach (var key in new[] { "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundFocused" }) field.Add((key, b.Brush(DesignToken.Ink3)));
            field.Add(("TextControlBorderThemeThicknessFocused", new Thickness(0)));
            owner.SetResourcesOnce(filesFilter, field);
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
            var refresh = new Button { Content = new FontIcon { Glyph = "\uE72C", FontSize = DesignMetrics.Type.Pill }, MinWidth = 0, MinHeight = 0, Padding = new Thickness(4, 2, 4, 2), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(DesignMetrics.Radius.FileRow), VerticalAlignment = VerticalAlignment.Center };
            owner.PaintPlainButton(refresh, b.Transparent, b.Subtle, ink: b.Brush(DesignToken.Ink2));
            AutomationProperties.SetName(refresh, Locale.Get("files.tree.refresh")); ToolTipService.SetToolTip(refresh, Locale.Get("files.tree.refresh"));
            AutomationProperties.SetAutomationId(refresh, "files-refresh");
            refresh.Click += async (_, _) => await RefreshFiles();
            Grid.SetColumn(refresh, 2); bar.Children.Add(refresh);
            panel.Children.Add(bar);

            filesList = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = false, Padding = new Thickness(0, 4, 0, 4) };
            AutomationProperties.SetAutomationId(filesList, "files-tree");
            PaintFilesRows(filesList);
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
            Grid.SetRow(filesList, 1); panel.Children.Add(filesList);
            filesNotes = new StackPanel { Spacing = 3 };
            Grid.SetRow(filesNotes, 2); panel.Children.Add(filesNotes);
            return panel;
        }

        /// <summary>
        /// The preview column (M/FilePaneView.swift:144-178): the 40-high head on the subtle wash, padding h12,
        /// with the file's symbol in <c>accent</c>, its name 12 semibold over its path 10 mono <c>ink2</c>, the
        /// encoding and the tools; a <c>line</c> under it; the banners; then the content.
        /// </summary>
        private Grid BuildFilesPreview()
        {
            var b = owner.brushes;
            var panel = new Grid();
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var header = previewHeader = new Grid { ColumnSpacing = 8, Height = DesignMetrics.Layout.PreviewHead, Padding = new Thickness(12, 0, 12, 0), Background = b.Subtle, BorderBrush = b.Brush(DesignToken.Line), BorderThickness = new Thickness(0, 0, 0, DesignMetrics.Stroke.Line) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            previewIcon = new FontIcon { Glyph = "\ue8a5", FontSize = 12, Foreground = b.Brush(DesignToken.Accent), VerticalAlignment = VerticalAlignment.Center };
            header.Children.Add(previewIcon);
            var names = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            previewTitle = new TextBlock { FontSize = DesignMetrics.Type.Block, FontWeight = FontWeights.SemiBold, Foreground = b.Brush(DesignToken.Ink), TextTrimming = TextTrimming.CharacterEllipsis };
            previewPath = new TextBlock { FontSize = DesignMetrics.Type.Small, FontFamily = MonoFont, Foreground = b.Brush(DesignToken.Ink2), TextTrimming = TextTrimming.CharacterEllipsis };
            names.Children.Add(previewTitle); names.Children.Add(previewPath);
            Grid.SetColumn(names, 1); header.Children.Add(names);
            previewEncoding = new TextBlock { FontSize = DesignMetrics.Type.Small, Foreground = b.Brush(DesignToken.Ink2), VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(previewEncoding, Locale.Get("files.preview.encoding"));
            AutomationProperties.SetAutomationId(previewEncoding, "files-encoding");
            Grid.SetColumn(previewEncoding, 2); header.Children.Add(previewEncoding);
            previewTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(previewTools, 3); header.Children.Add(previewTools);
            AutomationProperties.SetAutomationId(header, "files-preview-head");
            panel.Children.Add(header);
            previewBanners = new StackPanel();
            Grid.SetRow(previewBanners, 1); panel.Children.Add(previewBanners);
            previewContent = new Border();
            AutomationProperties.SetAutomationId(previewContent, "files-preview");
            Grid.SetRow(previewContent, 2); panel.Children.Add(previewContent);
            ShowPlaceholder();
            return panel;
        }

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
            if (filesList is null || filesNotes is null) return;
            renderingTree = true;
            try
            {
                var hadFocus = filesList.XamlRoot is { } xamlRoot && FocusManager.GetFocusedElement(xamlRoot) is ListViewItem focused && filesList.Items.Contains(focused);
                var (allRows, hitCap) = Tree.Rows();
                // Every row is an element of its own, so a huge expanded tree is cut (a note says so).
                var (rows, drawnCapped) = FilePaneDrawing.TreeRows(allRows);
                filesList.Items.Clear(); filesRows.Clear(); filesRowOrder.Clear(); filesNotes.Children.Clear();
                var light = owner.service.Snapshot.Theme == "light";
                ListViewItem? selected = null;
                foreach (var row in rows)
                {
                    filesRows[row.Entry.RelativePath] = row; filesRowOrder.Add(row);
                    var item = TreeItem(row, light);
                    filesList.Items.Add(item);
                    if (row.Entry.RelativePath == Tree.SelectedPath) selected = item;
                    if (row.Caption is { } caption) filesList.Items.Add(CaptionItem(caption, row.Depth + 1));
                }
                if (selected is not null)
                {
                    // A rebuilt list keeps its place and, when it had it, the keyboard focus.
                    filesList.SelectedItem = selected;
                    filesList.ScrollIntoView(selected);
                    if (hadFocus) owner.DispatcherQueue.TryEnqueue(() => selected.Focus(FocusState.Keyboard));
                }
                if (Tree.FolderErrors.TryGetValue("", out var rootError)) Note(rootError);
                else if (!Tree.IsFiltering && Tree.Children.TryGetValue("", out var top) && top.Count == 0) Note(Locale.Get("files.tree.empty"));
                else if (!Tree.IsFiltering && Tree.Truncated.Contains("")) Note(Locale.Get("files.tree.truncated", new Dictionary<string, string> { ["count"] = WorkspaceFiles.MaximumEntriesPerFolder.ToString(CultureInfo.CurrentCulture) }));
                if (Tree.IsFiltering && rows.Count == 0) Note(Locale.Get("files.tree.noMatches"));
                if (!Tree.IsFiltering && drawnCapped) Note(Locale.Get("files.tree.truncated", new Dictionary<string, string> { ["count"] = FilePaneDrawing.MaximumTreeRows.ToString(CultureInfo.CurrentCulture) }));
                if (Tree.IsFiltering && hitCap) Note(Locale.Get("files.tree.moreResults", new Dictionary<string, string> { ["count"] = FilePaneTree.MaximumFilterResults.ToString("N0", CultureInfo.CurrentCulture) }));
            }
            finally { renderingTree = false; }
        }

        private void Note(string text) => filesNotes?.Children.Add(new TextBlock { Text = text, FontSize = DesignMetrics.Type.Small, Foreground = owner.brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 6, 12, 6) });

        /// <summary>
        /// The tree's rows the Mac way (M/FilePaneView.swift:116-120): the chosen row on <c>accent</c> × 0.18
        /// (in every pointer state, words staying <c>ink</c>), the subtle wash under the pointer, and no
        /// stock selection bar. Written once into the list's own resources, before it is shown.
        /// </summary>
        private void PaintFilesRows(ListView list)
        {
            var b = owner.brushes; var selected = b.Brush(DesignToken.Accent, DesignMetrics.Opacity.FileSelection); var ink = b.Brush(DesignToken.Ink);
            var values = new List<(string Key, object Value)>();
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
        /// One tree row (M/FilePaneView.swift:96-124): the 8pt chevron in a 10-wide column, the 11pt symbol
        /// in a 14-wide one (folders <c>accent</c>, files <c>ink2</c>), the 11pt name, indented 8 + depth × 14,
        /// v4, radius 5; build output and dependencies at 0.55.
        /// </summary>
        private ListViewItem TreeItem(FilePaneTree.Row row, bool light)
        {
            var b = owner.brushes;
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(FilesRowIndent + row.Depth * FilesDepthIndent, 0, 0, 0), Opacity = row.Entry.IsNoise ? FilesNoiseOpacity : 1 };
            var chevron = new FontIcon { Glyph = row.IsExpanded ? "\uE70D" : "\uE76C", FontSize = 8, FontWeight = FontWeights.SemiBold, Foreground = b.Brush(DesignToken.Ink2), Width = 10, VerticalAlignment = VerticalAlignment.Center, Visibility = row.Entry.IsDirectory && !Tree.IsFiltering ? Visibility.Visible : Visibility.Collapsed };
            line.Children.Add(row.Entry.IsDirectory && !Tree.IsFiltering ? chevron : new Border { Width = 10 });
            line.Children.Add(new FontIcon { Glyph = row.Entry.IsDirectory ? "\ue8b7" : "\ue8a5", FontSize = 11, Width = 14, Foreground = b.Brush(row.Entry.IsDirectory ? DesignToken.Accent : DesignToken.Ink2), VerticalAlignment = VerticalAlignment.Center });
            var name = new TextBlock { Text = Tree.IsFiltering ? row.Entry.RelativePath : row.Entry.Name, FontSize = DesignMetrics.Type.Pill, Foreground = b.Brush(DesignToken.Ink), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            line.Children.Add(name);
            if (row.Entry.IsSymlink) line.Children.Add(new FontIcon { Glyph = "\uE72A", FontSize = 8, Foreground = b.Brush(DesignToken.Ink3), VerticalAlignment = VerticalAlignment.Center });
            var item = new ListViewItem { Content = line, Tag = row.Entry.RelativePath, MinHeight = 22, Padding = new Thickness(0, 4, 8, 4), Margin = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(DesignMetrics.Radius.FileRow) };
            AutomationProperties.SetAutomationId(item, "files-row-" + row.Entry.RelativePath);
            AutomationProperties.SetName(item, row.Entry.Name);
            ToolTipService.SetToolTip(item, row.Entry.RelativePath);
            if (row.Entry.IsDirectory)
                item.Tapped += async (_, _) => { if (!Tree.IsFiltering) await ToggleFolder(row.Entry); };
            return item;
        }

        private ListViewItem CaptionItem(string text, int depth) => new()
        {
            Content = new TextBlock { Text = text, FontSize = DesignMetrics.Type.Small, Foreground = owner.brushes.Brush(DesignToken.Ink3), Margin = new Thickness(FilesRowIndent + depth * FilesDepthIndent + 29, 0, 0, 0), TextWrapping = TextWrapping.Wrap },
            IsHitTestVisible = false, IsTabStop = false, MinHeight = 18, Padding = new Thickness(0, 3, 0, 3),
        };

        /// <summary>The tree's indents and the faded rows' opacity (M/FilePaneView.swift:116-117).</summary>
        internal const double FilesRowIndent = 8, FilesDepthIndent = 14, FilesNoiseOpacity = 0.55;

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
            previewBanners?.Children.Clear(); previewTools?.Children.Clear();
            if (previewContent is not null) previewContent.Child = new ProgressRing { IsActive = true, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
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

        private void ShowPreviewFailed()
        {
            previewBanners?.Children.Clear(); previewTools?.Children.Clear();
            if (previewContent is not null) previewContent.Child = Notice("\uE7BA", Locale.Get("files.preview.failed"));
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

        /// <summary>The head names the file (and shows only while one is chosen, as on the Mac).</summary>
        private void SetPreviewHeader(string title, TextEncodingKind? encoding, string? relativePath = null)
        {
            if (previewTitle is not null) previewTitle.Text = title;
            if (previewPath is not null) { previewPath.Text = relativePath ?? ""; ToolTipService.SetToolTip(previewPath, relativePath); }
            if (previewIcon is not null) previewIcon.Glyph = FileGlyph(title);
            if (previewHeader is not null) previewHeader.Visibility = title.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (previewEncoding is null) return;
            previewEncoding.Text = encoding is { } kind ? TextEncodings.DisplayName(kind) : "";
            AutomationProperties.SetName(previewEncoding, encoding is { } named ? Locale.Get("files.preview.encoding") + " " + TextEncodings.DisplayName(named) : "");
        }

        private void ShowPlaceholder()
        {
            SetPreviewHeader("", null);
            if (previewContent is not null) previewContent.Child = Notice("\uE8A0", Locale.Get("files.preview.placeholder"));
        }

        /// <summary>The file's symbol (M/FilePaneView.swift:131-141): Markdown, image, plain text, source or any other file.</summary>
        private static string FileGlyph(string name) => FilePreviewClassifier.FileExtension(name) switch
        {
            "md" or "markdown" => "\uE8A5",
            "png" or "jpg" or "jpeg" or "gif" or "bmp" or "webp" or "svg" or "heic" or "tiff" or "ico" or "pdf" => "\uEB9F",
            "txt" or "" => "\uE8A5",
            _ => "\uE943",
        };

        /// <summary>
        /// The empty and the failed preview (M/FilePaneView.swift:298-306): a 24pt light symbol over 12pt
        /// <c>ink2</c> words, centred, padding 20.
        /// </summary>
        private FrameworkElement Notice(string glyph, string text)
        {
            var notice = new StackPanel { Spacing = 6, Padding = new Thickness(20), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            notice.Children.Add(new FontIcon { Glyph = glyph, FontSize = 24, FontWeight = FontWeights.Light, Foreground = owner.brushes.Brush(DesignToken.Ink2) });
            notice.Children.Add(new TextBlock { Text = text, FontSize = DesignMetrics.Type.Block, Foreground = owner.brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
            AutomationProperties.SetAutomationId(notice, "files-notice");
            return notice;
        }

        /// <summary>A centred 12pt <c>ink2</c> sentence, for the reference preview's states.</summary>
        private TextBlock Centered(string text) => new() { Text = text, FontSize = DesignMetrics.Type.Block, Foreground = owner.brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };

        /// <summary>A note over the preview (M/FilePaneView.swift:286-296): an info symbol and 10pt <c>ink2</c> words on <c>waitSoft</c>, padding h12 v6.</summary>
        private void Banner(string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new FontIcon { Glyph = "\uE946", FontSize = DesignMetrics.Type.Small, Foreground = owner.brushes.Brush(DesignToken.Ink2), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
            row.Children.Add(new TextBlock { Text = text, FontSize = DesignMetrics.Type.Small, Foreground = owner.brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap });
            previewBanners?.Children.Add(new Border { Background = owner.brushes.Brush(DesignToken.WaitSoft), Padding = new Thickness(12, 6, 12, 6), Child = row });
        }

        private async Task DrawPreview(FilesPrepared prepared, int request)
        {
            var data = prepared.Data;
            previewBanners?.Children.Clear(); previewTools?.Children.Clear();
            SetPreviewHeader(data.Name, data.Encoding, data.RelativePath);
            if (previewContent is null) return;
            if (data.Failure is { } failure) { previewContent.Child = Notice("\uE7BA", failure); return; }
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
        /// The Preview | Source switch of a Markdown file, the Mac's segmented picker: a <c>segmentTrack</c>
        /// at radius 8, padding 2, the chosen side a <c>segmentOn</c> chip at radius 6. The switch is
        /// rebuilt with each preview, so the chosen side is drawn on its chip as it is built.
        /// </summary>
        private void MarkdownToggle(FilesPrepared prepared)
        {
            var b = owner.brushes;
            var track = new StackPanel { Orientation = Orientation.Horizontal, Padding = new Thickness(2), CornerRadius = new CornerRadius(DesignMetrics.Radius.Row), Background = b.SegmentTrack, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(track, "files-markdown-mode");
            Button Choice(string key, bool source)
            {
                var chosen = showMarkdownSource == source;
                var chip = new Border { Padding = new Thickness(9, 2, 9, 2), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), Background = chosen ? b.SegmentOn : b.Transparent, Child = new TextBlock { Text = Locale.Get(key), FontSize = DesignMetrics.Type.Pill, FontWeight = chosen ? FontWeights.SemiBold : FontWeights.Normal, Foreground = b.Brush(chosen ? DesignToken.Ink : DesignToken.Ink2) } };
                var button = new Button { Content = chip, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0, BorderThickness = new Thickness(0) };
                owner.PaintPlainButton(button, b.Transparent, b.Transparent);
                AutomationProperties.SetName(button, Locale.Get(key));
                button.Click += async (_, _) => { showMarkdownSource = source; await DrawPreview(prepared, previewRequest); };
                return button;
            }
            track.Children.Add(Choice("files.markdown.rendered", false));
            track.Children.Add(Choice("files.markdown.source", true));
            previewTools?.Children.Add(track);
        }

        /// <summary>Monospaced text with line numbers and the highlighter's colours, selectable for copying.</summary>
        private FrameworkElement SourceView(FilesPrepared prepared)
        {
            var text = prepared.Data.Text ?? "";
            var body = new RichTextBlock
            {
                FontFamily = MonoFont, FontSize = 12, LineHeight = FilesLineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Foreground = owner.brushes.Brush(DesignToken.Ink),
                IsTextSelectionEnabled = true, TextWrapping = prepared.Wraps ? TextWrapping.Wrap : TextWrapping.NoWrap,
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
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            // A wrapped line has no number on its continuation, as on macOS; with wrapping on,
            // numbers could not line up, so the column is left out.
            if (!prepared.Wraps)
            {
                var numbers = new TextBlock
                {
                    FontFamily = MonoFont, FontSize = DesignMetrics.Type.Small, LineHeight = FilesLineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Foreground = owner.brushes.Brush(DesignToken.Ink3), TextAlignment = TextAlignment.Right, Text = prepared.LineNumbers,
                };
                AutomationProperties.SetAccessibilityView(numbers, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                grid.Children.Add(numbers);
            }
            Grid.SetColumn(body, 1); grid.Children.Add(body);
            return new ScrollViewer
            {
                Content = grid, Padding = new Thickness(6, 8, 6, 8),
                HorizontalScrollBarVisibility = prepared.Wraps ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
                HorizontalScrollMode = prepared.Wraps ? ScrollMode.Disabled : ScrollMode.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
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

        /// <summary>Rendered with the transcript's Markdown renderer: no images, no local links.</summary>
        private static RichEditBox MarkdownView(string rtf)
        {
            var view = new RichEditBox { IsReadOnly = true, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), Padding = new Thickness(18) };
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

        private void PixelsLabel(long width, long height)
        {
            var label = new TextBlock { Text = Locale.Get("files.image.pixels", new Dictionary<string, string> { ["width"] = width.ToString(CultureInfo.InvariantCulture), ["height"] = height.ToString(CultureInfo.InvariantCulture) }), FontSize = DesignMetrics.Type.Small, Foreground = owner.brushes.Brush(DesignToken.Ink2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
            previewTools?.Children.Insert(0, label);
        }

        private static FrameworkElement FittedImage(Image image) => new Border { Child = image, Padding = new Thickness(8) };

        /// <summary>Fit (never larger than 1:1), zoom out, 1:1 and zoom in, the macOS controls.</summary>
        private FrameworkElement ZoomableImage(BitmapImage fit, byte[] bytes, long width, long height, bool fullAllowed)
        {
            var scale = owner.root.XamlRoot?.RasterizationScale ?? 1;
            double pointsWide = width / scale, pointsHigh = height / scale;
            var image = new Image { Source = fit, Stretch = Stretch.Uniform, MaxWidth = pointsWide, MaxHeight = pointsHigh };
            var viewer = new ScrollViewer { Content = image, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollMode = ScrollMode.Disabled, Padding = new Thickness(8) };
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
            Button Tool(string glyph, string key, Func<Task> action, bool enabled = true)
            {
                var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 12 }, MinWidth = 0, MinHeight = 0, Padding = new Thickness(5, 3, 5, 3), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(DesignMetrics.Radius.FileRow), IsEnabled = enabled };
                owner.PaintPlainButton(button, owner.brushes.Transparent, owner.brushes.Subtle, ink: owner.brushes.Brush(DesignToken.Ink2), disabledInk: owner.brushes.Brush(DesignToken.Ink3));
                AutomationProperties.SetName(button, Locale.Get(key)); ToolTipService.SetToolTip(button, Locale.Get(key));
                button.Click += async (_, _) => await owner.Act(action);
                return button;
            }
            previewTools?.Children.Add(Tool("", "files.image.zoomOut", () => Apply(Current() / 1.25), fullAllowed));
            previewTools?.Children.Add(Tool("", "files.image.fit", () => Apply(null)));
            previewTools?.Children.Add(Tool("", "files.image.actualSize", () => Apply(1), fullAllowed));
            previewTools?.Children.Add(Tool("", "files.image.zoomIn", () => Apply(Current() * 1.25), fullAllowed));
            AutomationProperties.SetName(image, Locale.Get("files.image.pixels", new Dictionary<string, string> { ["width"] = width.ToString(CultureInfo.InvariantCulture), ["height"] = height.ToString(CultureInfo.InvariantCulture) }));
            return viewer;
        }

        /// <summary>"This file can't be previewed" with its name, size, modified time and a File Explorer button.</summary>
        private FrameworkElement UnsupportedCard(FilePreviewData data, string? reason)
        {
            var card = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(24) };
            AutomationProperties.SetAutomationId(card, "files-unsupported");
            var b = owner.brushes;
            card.Children.Add(new FontIcon { Glyph = "\ue9ce", FontSize = 30, FontWeight = FontWeights.Light, Foreground = b.Brush(DesignToken.Ink2) });
            card.Children.Add(new TextBlock { Text = Locale.Get("files.preview.unsupported"), FontSize = DesignMetrics.Type.Title, FontWeight = FontWeights.SemiBold, Foreground = b.Brush(DesignToken.Ink), HorizontalAlignment = HorizontalAlignment.Center });
            card.Children.Add(new TextBlock { Text = data.Name, FontSize = DesignMetrics.Type.Block, Foreground = b.Brush(DesignToken.Ink), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxLines = 2 });
            // Size and modified time as a two-column grid of an ink2 label and its ink value (M/FilePaneView.swift:262-273).
            var facts = new Grid { ColumnSpacing = 10, RowSpacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
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
            var reveal = new Button { Content = Locale.Get("menu.showInExplorer"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
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
