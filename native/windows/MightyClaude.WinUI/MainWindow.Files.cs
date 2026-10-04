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
        private TextBlock? previewTitle, previewEncoding;
        private Border? previewContent;
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
        private sealed record FilesPrepared(FilePreviewData Data, List<SourceToken> Tokens, string LineNumbers, bool Wraps, bool HighlightCapped, int HighlightUnits, string? MarkdownRtf, bool RenderCapped = false);

        private FilePaneTree Tree => filesTree ??= owner.FilePaneTreeFor(Session.WorkspaceId);
        /// <summary>This view is still the one shown for its pane.</summary>
        private bool FilesAlive => !owner.closing && owner.views.TryGetValue(id, out var current) && ReferenceEquals(current, this);
        internal FilePaneTree FilesTree => Tree;
        internal FrameworkElement? FilesHost => filesHost;
        internal FilePreviewData? FilesShown => shown?.Data;
        internal int FilesTreeItemCount => filesList?.Items.Count ?? 0;
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
            var divider = new Border { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(60, 135, 135, 135)) };
            Grid.SetColumn(divider, 1); filesHost.Children.Add(divider);
            var preview = BuildFilesPreview();
            Grid.SetColumn(preview, 2); filesHost.Children.Add(preview);

            Grid.SetRow(filesHost, 0); Grid.SetRowSpan(filesHost, Math.Max(1, grid.RowDefinitions.Count));
            grid.Children.Add(filesHost);
            _ = StartFiles();
        }

        private Grid BuildFilesTree()
        {
            var panel = new Grid { RowSpacing = 6, Padding = new Thickness(0, 0, 8, 0) };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var bar = new Grid { ColumnSpacing = 4 };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            filesFilter = new TextBox { PlaceholderText = Locale.Get("files.tree.filter"), Text = Tree.Filter };
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
            bar.Children.Add(filesFilter);
            var refresh = new Button { Content = new FontIcon { Glyph = "", FontSize = 13 }, MinWidth = 0, Padding = new Thickness(8, 5, 8, 5) };
            AutomationProperties.SetName(refresh, Locale.Get("files.tree.refresh")); ToolTipService.SetToolTip(refresh, Locale.Get("files.tree.refresh"));
            refresh.Click += async (_, _) => await RefreshFiles();
            Grid.SetColumn(refresh, 1); bar.Children.Add(refresh);
            panel.Children.Add(bar);

            filesList = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = false };
            AutomationProperties.SetAutomationId(filesList, "files-tree");
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

        private Grid BuildFilesPreview()
        {
            var panel = new Grid { RowSpacing = 6, Padding = new Thickness(10, 0, 0, 0) };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var header = new Grid { ColumnSpacing = 8, MinHeight = 32 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            previewTitle = new TextBlock { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            header.Children.Add(previewTitle);
            previewEncoding = new TextBlock { FontSize = 11, Opacity = .6, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(previewEncoding, Locale.Get("files.preview.encoding"));
            Grid.SetColumn(previewEncoding, 1); header.Children.Add(previewEncoding);
            previewTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(previewTools, 2); header.Children.Add(previewTools);
            panel.Children.Add(header);
            previewBanners = new StackPanel { Spacing = 4 };
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

        private void Note(string text) => filesNotes?.Children.Add(new TextBlock { Text = text, FontSize = 11, Opacity = .65, TextWrapping = TextWrapping.Wrap });

        private ListViewItem TreeItem(FilePaneTree.Row row, bool light)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(row.Depth * 14, 0, 0, 0) };
            line.Children.Add(new TextBlock { Text = row.Entry.IsDirectory && !Tree.IsFiltering ? row.IsExpanded ? "▾" : "▸" : "", Width = 10, Opacity = .6, VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(new FontIcon { Glyph = row.Entry.IsDirectory ? "" : "", FontSize = 13, Opacity = .75 });
            var name = new TextBlock { Text = Tree.IsFiltering ? row.Entry.RelativePath : row.Entry.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Opacity = row.Entry.IsNoise ? .5 : 1 };
            line.Children.Add(name);
            if (row.Entry.IsSymlink) line.Children.Add(new TextBlock { Text = "↗", FontSize = 10, Opacity = .55, VerticalAlignment = VerticalAlignment.Center });
            var item = new ListViewItem { Content = line, Tag = row.Entry.RelativePath, MinHeight = 26, Padding = new Thickness(6, 0, 6, 0) };
            AutomationProperties.SetName(item, row.Entry.Name);
            ToolTipService.SetToolTip(item, row.Entry.RelativePath);
            if (row.Entry.IsDirectory)
                item.Tapped += async (_, _) => { if (!Tree.IsFiltering) await ToggleFolder(row.Entry); };
            return item;
        }

        private static ListViewItem CaptionItem(string text, int depth) => new()
        {
            Content = new TextBlock { Text = text, FontSize = 11, Opacity = .6, Margin = new Thickness(depth * 14 + 15, 0, 0, 0), TextWrapping = TextWrapping.Wrap },
            IsHitTestVisible = false, IsTabStop = false, MinHeight = 22,
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
            SetPreviewHeader(path.Split('/')[^1], null);
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

        private void ShowPreviewFailed()
        {
            previewBanners?.Children.Clear(); previewTools?.Children.Clear();
            if (previewContent is not null) previewContent.Child = Centered(Locale.Get("files.preview.failed"));
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
            return new(data, tokens, numbers, longest > SourceLines.WrapThreshold, capped, units, markdown, renderCapped);
        }

        private void SetPreviewHeader(string title, TextEncodingKind? encoding)
        {
            if (previewTitle is not null) previewTitle.Text = title;
            if (previewEncoding is null) return;
            previewEncoding.Text = encoding is { } kind ? TextEncodings.DisplayName(kind) : "";
            AutomationProperties.SetName(previewEncoding, encoding is { } named ? Locale.Get("files.preview.encoding") + " " + TextEncodings.DisplayName(named) : "");
        }

        private void ShowPlaceholder()
        {
            SetPreviewHeader("", null);
            if (previewContent is not null) previewContent.Child = Centered(Locale.Get("files.preview.placeholder"));
        }

        private static TextBlock Centered(string text) => new() { Text = text, Opacity = .6, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };

        private void Banner(string text) => previewBanners?.Children.Add(new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 230, 170, 60)), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 8, 4),
            Child = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap },
        });

        private async Task DrawPreview(FilesPrepared prepared, int request)
        {
            var data = prepared.Data;
            previewBanners?.Children.Clear(); previewTools?.Children.Clear();
            SetPreviewHeader(data.Name, data.Encoding);
            if (previewContent is null) return;
            if (data.Failure is { } failure) { previewContent.Child = Centered(failure); return; }
            switch (data.Kind.Tag)
            {
                case FilePreviewKindTag.Source:
                    SourceBanners(prepared);
                    previewContent.Child = SourceView(prepared);
                    break;
                case FilePreviewKindTag.Markdown:
                    if (!data.MarkdownRenderable) Banner(Locale.Get("files.markdown.tooLarge"));
                    else MarkdownToggle(prepared);
                    if (prepared.MarkdownRtf is { } rtf && !showMarkdownSource) previewContent.Child = MarkdownView(rtf);
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

        /// <summary>The Preview | Source switch of a Markdown file.</summary>
        private void MarkdownToggle(FilesPrepared prepared)
        {
            ToggleButton Choice(string key, bool source)
            {
                var button = new ToggleButton { Content = Locale.Get(key), IsChecked = showMarkdownSource == source, Padding = new Thickness(10, 3, 10, 3), MinWidth = 0, FontSize = 12 };
                button.Click += async (_, _) => { showMarkdownSource = source; await DrawPreview(prepared, previewRequest); };
                return button;
            }
            previewTools?.Children.Add(Choice("files.markdown.rendered", false));
            previewTools?.Children.Add(Choice("files.markdown.source", true));
        }

        /// <summary>Monospaced text with line numbers and the highlighter's colours, selectable for copying.</summary>
        private FrameworkElement SourceView(FilesPrepared prepared)
        {
            var text = prepared.Data.Text ?? "";
            var light = owner.service.Snapshot.Theme == "light";
            var body = new RichTextBlock
            {
                FontFamily = MonoFont, FontSize = 12, LineHeight = FilesLineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                IsTextSelectionEnabled = true, TextWrapping = prepared.Wraps ? TextWrapping.Wrap : TextWrapping.NoWrap,
            };
            var paragraph = new Paragraph();
            var position = 0;
            foreach (var token in prepared.Tokens)
            {
                if (token.Location > position) paragraph.Inlines.Add(new Run { Text = text[position..token.Location] });
                paragraph.Inlines.Add(new Run { Text = text.Substring(token.Location, token.Length), Foreground = TokenBrush(token.Kind, light) });
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
                    FontFamily = MonoFont, FontSize = 12, LineHeight = FilesLineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Opacity = .45, TextAlignment = TextAlignment.Right, Text = prepared.LineNumbers,
                };
                AutomationProperties.SetAccessibilityView(numbers, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                grid.Children.Add(numbers);
            }
            Grid.SetColumn(body, 1); grid.Children.Add(body);
            return new ScrollViewer
            {
                Content = grid, Padding = new Thickness(4, 2, 4, 8),
                HorizontalScrollBarVisibility = prepared.Wraps ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
                HorizontalScrollMode = prepared.Wraps ? ScrollMode.Disabled : ScrollMode.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
        }

        private static SolidColorBrush TokenBrush(SourceTokenKind kind, bool light) => new(kind switch
        {
            SourceTokenKind.Keyword => light ? Windows.UI.Color.FromArgb(255, 155, 35, 147) : Windows.UI.Color.FromArgb(255, 252, 95, 163),
            SourceTokenKind.String => light ? Windows.UI.Color.FromArgb(255, 196, 26, 22) : Windows.UI.Color.FromArgb(255, 252, 106, 93),
            SourceTokenKind.Comment => light ? Windows.UI.Color.FromArgb(255, 93, 108, 121) : Windows.UI.Color.FromArgb(255, 127, 140, 152),
            _ => light ? Windows.UI.Color.FromArgb(255, 28, 0, 207) : Windows.UI.Color.FromArgb(255, 208, 191, 105),
        });

        /// <summary>Rendered with the transcript's Markdown renderer: no images, no local links.</summary>
        private static FrameworkElement MarkdownView(string rtf)
        {
            var view = new RichEditBox { IsReadOnly = true, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), Padding = new Thickness(8) };
            ScrollViewer.SetVerticalScrollBarVisibility(view, ScrollBarVisibility.Auto);
            AutomationProperties.SetName(view, Locale.Get("files.markdown.rendered"));
            try { view.IsReadOnly = false; view.Document.SetText(TextSetOptions.FormatRtf, rtf); }
            finally { view.IsReadOnly = true; }
            return view;
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
            var label = new TextBlock { Text = Locale.Get("files.image.pixels", new Dictionary<string, string> { ["width"] = width.ToString(CultureInfo.InvariantCulture), ["height"] = height.ToString(CultureInfo.InvariantCulture) }), FontSize = 11, Opacity = .65, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
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
                var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 12 }, MinWidth = 0, Padding = new Thickness(7, 4, 7, 4), IsEnabled = enabled };
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
            card.Children.Add(new FontIcon { Glyph = "", FontSize = 30, Opacity = .6 });
            card.Children.Add(new TextBlock { Text = Locale.Get("files.preview.unsupported"), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            card.Children.Add(new TextBlock { Text = data.Name, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, MaxLines = 2 });
            card.Children.Add(new TextBlock { Text = Locale.Get("files.preview.size") + "  " + FormatBytes(data.Size), FontSize = 11, Opacity = .7, HorizontalAlignment = HorizontalAlignment.Center });
            if (data.Modified is { } modified) card.Children.Add(new TextBlock { Text = Locale.Get("files.preview.modified") + "  " + modified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), FontSize = 11, Opacity = .7, HorizontalAlignment = HorizontalAlignment.Center });
            if (reason is not null) card.Children.Add(new TextBlock { Text = reason, FontSize = 11, Opacity = .7, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
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
