using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace MightyClaude.WinUI;

// The two new ways to add a workspace (docs/superpowers/specs/2026-10-26-workspace-create-design.md; the Mac's
// WorkspaceCreateViews.swift): a new folder, optionally `git init`, and 'Git으로 생성'. The web page draws the sheets
// (native/windows/web/src/dialogs/WorkspaceCreate.tsx) and asks the methods registered here; the WinUI fallback has its own
// ContentDialogs. Every rule, message and git run is Core's (WorkspaceCreate, GitTool, GitCloneJob); a created folder is then
// added as Ctrl+O adds one (OpenWorkspaceFolder).
public sealed partial class MainWindow
{
    /// <summary>The clones running, by the web dialog that started them (<see cref="FallbackCreateJob"/> for the WinUI fallback's).</summary>
    private readonly Dictionary<int, GitCloneJob> gitJobs = [];
    /// <summary>The web sheet's address check running; a newer one stops it.</summary>
    private CancellationTokenSource? gitProbeCancel;
    /// <summary>The WinUI fallback's clone in <see cref="gitJobs"/>: web dialog ids start at 1.</summary>
    private const int FallbackCreateJob = 0;
    /// <summary>The create sheets' width (M/WorkspaceCreateViews.swift: 460).</summary>
    internal const double CreateSheetWidth = 460;
    /// <summary>The menu glyphs of the two new ways (Segoe Fluent Icons Add and Download; the existing folder keeps its NewFolder glyph).</summary>
    internal const string NewFolderGlyph = "", FromGitGlyph = "";

    /// <summary>Where the sheets start: the last location used, else the active workspace's parent folder, else home.</summary>
    private string CreateLocationDefault()
    {
        var state = service.Snapshot;
        var active = LocalPath(state.Workspaces.FirstOrDefault(w => w.Id == state.ActiveWorkspaceId));
        return WorkspaceCreate.DefaultLocation(state.LastCreateLocation, active, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>Opens the new-folder (<see cref="AddPaneMenu.NewFolder"/>) or Git (<see cref="AddPaneMenu.FromGit"/>) sheet; one sheet at a time.</summary>
    private async Task ShowWorkspaceCreateAsync(string kind)
    {
        if (dialogOpen || closing || kind is not (AddPaneMenu.NewFolder or AddPaneMenu.FromGit)) return;
        if (webShell is not null) { await ShowWebWorkspaceCreateAsync(kind); return; }
        if (kind == AddPaneMenu.NewFolder) await ShowNewFolderDialogAsync(); else await ShowGitCreateDialogAsync();
    }

    /// <summary>A folder made or cloned: its location is remembered, it is added and selected as Ctrl+O adds one, and a failed git init is said.</summary>
    private async Task FinishWorkspaceCreateAsync(string location, string path, string? notice)
    {
        await service.RememberCreateLocationAsync(location);
        await OpenWorkspaceFolder(path);
        if (notice is not null) error.Text = notice;
    }

    /// <summary>Stops every clone (the window is closing): each removes what it wrote.</summary>
    private void CancelWorkspaceCreateJobs()
    {
        foreach (var job in gitJobs.Values) job.Cancel();
        gitProbeCancel?.Cancel();
    }

    /// <summary>What a name in a location gives: the first reason it cannot be made (the Mac's order: name, location, an existing file or folder), worded.</summary>
    private static (string? Problem, string Target, bool Exists, bool LocationMissing, string? Message) CheckCreate(string location, string name)
    {
        var problem = WorkspaceCreate.CheckName(name);
        var target = WorkspaceCreate.Target(location, name);
        var locationMissing = !Directory.Exists(location);
        var exists = problem is null && Path.Exists(target);
        var message = problem is not null ? WorkspaceCreate.NameMessage(problem)
            : locationMissing ? Locale.Get("workspace.location.error.missing")
            : exists ? WorkspaceCreate.NameMessage("exists") : null;
        return (problem, target, exists, locationMissing, message);
    }

    private static bool CanCreate((string? Problem, string Target, bool Exists, bool LocationMissing, string? Message) check) =>
        check.Problem is null && !check.Exists && !check.LocationMissing;

    /// <summary>A failed git init's notice, shown in the window's banner once the folder is added (the Mac's store.error).</summary>
    private static string GitInitNotice(string? message) =>
        Locale.Get("workspace.newFolder.gitInitFailed", new Dictionary<string, string> { ["message"] = message ?? "" });

    /// <summary>The folder picker as Ctrl+O sets it up (<see cref="PickFolder"/>); null when nothing was picked.</summary>
    private async Task<string?> PickCreateLocation()
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    // ── The web sheets ──

    /// <summary>The sheets' words (web CreateLabels), each from its locale key; <c>path</c> keeps its <c>{path}</c>.</summary>
    private static object WorkspaceCreateLabels(bool git) => new
    {
        name = Locale.Get("workspace.create.name"), location = Locale.Get("workspace.create.location"), choose = Locale.Get("workspace.create.chooseLocation"),
        path = Locale.Get("workspace.create.path"), gitInit = Locale.Get("workspace.newFolder.gitInit"),
        create = git ? Locale.Get("workspace.git.create") : Locale.Get("workspace.newFolder.create"), cancel = Locale.Get("workspace.create.cancel"),
        url = Locale.Get("workspace.git.url"), urlPlaceholder = Locale.Get("workspace.git.urlPlaceholder"), test = Locale.Get("workspace.git.test"),
        checking = Locale.Get("workspace.git.checking"), reachable = Locale.Get("workspace.git.reachable"), openSetup = Locale.Get("workspace.git.openSetup"),
        starting = WorkspaceCreate.ProgressMessage(new CloneProgress("starting", 0, 0)),
    };

    /// <summary>
    /// The sheet in the page; it answers <c>{choice:"done", path, location, notice?}</c>, <c>{choice:"openSetup"}</c> (Git is
    /// missing: 필요한 도구 opens once the sheet is gone, at Git) or <c>{choice:"cancel"}</c>.
    /// </summary>
    private async Task ShowWebWorkspaceCreateAsync(string kind)
    {
        var git = kind == AddPaneMenu.FromGit;
        JsonElement result;
        dialogOpen = true;
        var id = 0;
        try
        {
            (id, var answer) = OpenWebDialog(new
            {
                type = git ? "gitCreate" : "newFolder",
                title = git ? Locale.Get("workspace.git.title") : Locale.Get("workspace.newFolder.title"),
                subtitle = git ? Locale.Get("workspace.git.subtitle") : Locale.Get("workspace.newFolder.subtitle"),
                labels = WorkspaceCreateLabels(git), location = CreateLocationDefault(),
            });
            result = await answer;
        }
        finally
        {
            dialogOpen = false;
            // A sheet gone without its answer (the page loaded again) leaves no clone or check behind.
            if (gitJobs.TryGetValue(id, out var job)) job.Cancel();
            gitProbeCancel?.Cancel();
        }
        if (result.ValueKind != JsonValueKind.Object) return;
        var choice = result.Text("choice");
        if (choice == "openSetup") { OpenSetup("git"); return; }
        if (choice != "done" || result.Text("path") is not { } path || result.Text("location") is not { } location || !Directory.Exists(path)) return;
        var notice = result.Text("notice");
        await Act(() => FinishWorkspaceCreateAsync(location, path, notice));
    }

    private void RegisterWorkspaceCreateMethods(WebBridge bridge)
    {
        bridge.Register("workspaceCreate", async args => { if (args.Text("kind") is { } kind) await ShowWorkspaceCreateAsync(kind); return null; });
        bridge.Register("createCheck", args =>
        {
            var check = CheckCreate(args.Text("location") ?? "", args.Text("name") ?? "");
            return Task.FromResult<object?>(new { problem = check.Problem, target = check.Target, exists = check.Exists, locationMissing = check.LocationMissing, message = check.Message });
        });
        bridge.Register("pickLocation", async _ => await PickCreateLocation());
        bridge.Register("makeFolder", async args =>
        {
            var location = args.Text("location") ?? ""; var name = args.Text("name") ?? "";
            var check = CheckCreate(location, name);
            if (!CanCreate(check)) return new { outcome = "failed", message = check.Message, notice = (string?)null, path = check.Target };
            var made = await GitTool.MakeFolderAsync(check.Target, ArgFlag(args, "gitInit"));
            return new { outcome = made.Outcome, message = made.Message, notice = made.Outcome == "madeButGitInitFailed" ? GitInitNotice(made.Message) : null, path = check.Target };
        });
        bridge.Register("repoName", args => Task.FromResult<object?>(WorkspaceCreate.RepoName(args.Text("url") ?? "")));
        bridge.Register("gitProbe", async args =>
        {
            // One check at a time: a newer address stops the older one, whose call answers `cancelled`.
            gitProbeCancel?.Cancel();
            var cancel = gitProbeCancel = new CancellationTokenSource();
            try
            {
                var found = await GitTool.ProbeAsync(args.Text("url") ?? "", cancel.Token);
                return new { reachable = found.Reachable, kind = found.Kind, message = found.Kind is { } failure ? WorkspaceCreate.ErrorMessage(failure, found.Message) : null };
            }
            catch (OperationCanceledException) { return new { reachable = false, kind = (string?)"cancelled", message = (string?)null }; }
            finally
            {
                if (ReferenceEquals(gitProbeCancel, cancel)) gitProbeCancel = null;
                cancel.Dispose();
            }
        });
        bridge.Register("gitCreate", async args =>
        {
            // Only for a Git sheet that is open, one clone at a time in it: a second press answers `busy` and the first clone runs on.
            if (ArgNumber(args, "dialogId") is not { } number || !webDialogs.ContainsKey((int)number)) return null;
            var dialogId = (int)number;
            if (gitJobs.ContainsKey(dialogId)) return new { outcome = "busy", kind = (string?)null, message = (string?)null, path = (string?)null };
            var check = CheckCreate(args.Text("location") ?? "", args.Text("name") ?? "");
            if (!CanCreate(check)) return new { outcome = "failed", kind = (string?)null, message = check.Message, path = (string?)null };
            void Progress(CloneProgress step) =>
                webShell?.Bridge.Push(new { kind = "gitProgress", id = dialogId, phase = step.Phase, percent = step.Percent, fraction = step.Fraction, text = WorkspaceCreate.ProgressMessage(step) });
            Progress(new CloneProgress("starting", 0, 0));
            // GitCloneJob reports from its reader thread; the page is written to on the UI thread only.
            var queue = DispatcherQueue;
            var job = new GitCloneJob(args.Text("url") ?? "", check.Target, step => queue.TryEnqueue(() => Progress(step)));
            gitJobs[dialogId] = job;
            CloneResult result;
            try { result = await job.RunAsync(); }
            finally { gitJobs.Remove(dialogId); }
            return new
            {
                outcome = result.Outcome, kind = result.Kind,
                message = result.Kind is { } failure ? WorkspaceCreate.ErrorMessage(failure, result.Message) : result.Message,
                path = result.Outcome == "done" ? check.Target : null,
            };
        });
        bridge.Register("gitCreateCancel", args =>
        {
            if (ArgNumber(args, "dialogId") is { } number && gitJobs.TryGetValue((int)number, out var job)) job.Cancel();
            return Task.FromResult<object?>(null);
        });
    }

    // ── The menus ──

    private MenuFlyoutItem NewFolderMenuItem()
    {
        var item = MenuItem(Locale.Get(AddPaneMenu.NewFolderKey), () => ShowWorkspaceCreateAsync(AddPaneMenu.NewFolder));
        item.Icon = new FontIcon { Glyph = NewFolderGlyph };
        AutomationProperties.SetAutomationId(item, "add-pane-new-folder");
        return item;
    }

    private MenuFlyoutItem FromGitMenuItem()
    {
        var item = MenuItem(Locale.Get(AddPaneMenu.FromGitKey), () => ShowWorkspaceCreateAsync(AddPaneMenu.FromGit));
        item.Icon = new FontIcon { Glyph = FromGitGlyph };
        AutomationProperties.SetAutomationId(item, "add-pane-from-git");
        return item;
    }

    /// <summary>
    /// The three ways to add a workspace as a menu (the sidebar's folder-plus button): built as it opens, so its words are in
    /// the language read after the sidebar was built, and off while a sheet is open.
    /// </summary>
    private MenuFlyout WorkspaceAddMenu()
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            foreach (var item in new[] { OpenProjectMenuItem(), NewFolderMenuItem(), FromGitMenuItem() }) { item.IsEnabled = !dialogOpen; menu.Items.Add(item); }
        };
        return menu;
    }

    // ── The WinUI fallback's sheets ──

    private TextBlock CreateText(string text, double size, DesignToken ink) => new() { Text = text, TextSize = size, Foreground = brushes.Brush(ink), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>A caption under the fields (the path, a problem): 10pt, hidden while empty.</summary>
    private TextBlock CreateCaption(DesignToken ink) => new() { TextSize = RenameCaption, Foreground = brushes.Brush(ink), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Visibility = Visibility.Collapsed };

    private static void ShowCaption(TextBlock caption, string? text)
    {
        caption.Text = text ?? "";
        caption.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private TextBox CreateField(string label, string placeholder, string id)
    {
        var field = new TextBox { PlaceholderText = placeholder, TextSize = DesignMetrics.Type.Body, PlaceholderForeground = brushes.Tertiary, IsSpellCheckEnabled = false };
        AutomationProperties.SetName(field, label); AutomationProperties.SetAutomationId(field, id);
        return field;
    }

    /// <summary>The fields' grid: the labels trailing in the first column, the fields filling the second.</summary>
    private static Grid CreateGrid()
    {
        var grid = new Grid { ColumnSpacing = DesignMetrics.Spacing.Md, RowSpacing = DesignMetrics.Spacing.Md };
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    private void AddCreateRow(Grid grid, string? label, FrameworkElement field)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        if (label is not null)
        {
            var words = CreateText(label, DesignMetrics.Type.Row, DesignToken.Ink); words.HorizontalAlignment = HorizontalAlignment.Right; words.TextWrapping = TextWrapping.NoWrap;
            Grid.SetRow(words, row); grid.Children.Add(words);
        }
        Grid.SetRow(field, row); Grid.SetColumn(field, 1); grid.Children.Add(field);
    }

    /// <summary>The location row: the path (its whole in the tooltip) and the folder picker, which calls <paramref name="picked"/>.</summary>
    private (Grid Row, TextBlock Path, Button Choose) CreateLocationRow(Func<string> current, Action<string> picked)
    {
        var row = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var path = CreateText(current(), DesignMetrics.Type.Row, DesignToken.Ink); path.TextWrapping = TextWrapping.NoWrap; path.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTipService.SetToolTip(path, current());
        AutomationProperties.SetAutomationId(path, "workspace-create-location");
        var choose = new Button { Content = new FontIcon { Glyph = "", TextSize = DesignMetrics.Type.Row } };
        var words = Locale.Get("workspace.create.chooseLocation");
        AutomationProperties.SetName(choose, words); ToolTipService.SetToolTip(choose, words); AutomationProperties.SetAutomationId(choose, "workspace-create-choose-location");
        choose.Click += async (_, _) =>
        {
            try { if (await PickCreateLocation() is { } folder) { path.Text = folder; ToolTipService.SetToolTip(path, folder); picked(folder); } }
            catch (Exception ex) when (ex is not OutOfMemoryException) { error.Text = ex.Message; }
        };
        Grid.SetColumn(choose, 1);
        row.Children.Add(path); row.Children.Add(choose);
        return (row, path, choose);
    }

    /// <summary>The sheet's heading in its content: the title and the line under it (M/WorkspaceCreateViews.swift CreateSheetHeading).</summary>
    private StackPanel CreateHeading(string title, string subtitle) =>
        new() { Spacing = DesignMetrics.Spacing.Xxs, Children = { SheetTitle(title), CreateText(subtitle, DesignMetrics.Type.Pill, DesignToken.Ink2) } };

    private ContentDialog CreateDialog(StackPanel content, string title, string primary, string id)
    {
        var dialog = StyledDialog(new ContentDialog
        {
            Content = content, XamlRoot = root.XamlRoot, RequestedTheme = root.RequestedTheme,
            PrimaryButtonText = primary, CloseButtonText = Locale.Get("workspace.create.cancel"), DefaultButton = ContentDialogButton.Primary,
        }, CreateSheetWidth);
        AutomationProperties.SetAutomationId(dialog, id); AutomationProperties.SetName(dialog, title);
        return dialog;
    }

    /// <summary>
    /// "새 폴더 만들기" as a ContentDialog: the name, the location, ☐ git init, the path it gives and the first problem. 만들기
    /// keeps the sheet open while the folder is made and on a failure, which it shows; a made folder is added.
    /// </summary>
    private async Task ShowNewFolderDialogAsync()
    {
        var title = Locale.Get("workspace.newFolder.title");
        var location = CreateLocationDefault();
        var name = CreateField(Locale.Get("workspace.create.name"), Locale.Get("workspace.create.name"), "workspace-new-folder-name");
        var gitInit = new CheckBox { Content = Locale.Get("workspace.newFolder.gitInit") };
        AutomationProperties.SetAutomationId(gitInit, "workspace-new-folder-git-init");
        var preview = CreateCaption(DesignToken.Ink2); var problem = CreateCaption(DesignToken.ErrText);
        AutomationProperties.SetAutomationId(problem, "workspace-create-problem");
        var busy = new ProgressRing { IsActive = false, Width = DesignMetrics.Spacing.Xl, Height = DesignMetrics.Spacing.Xl, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        string? failure = null; var making = false; (string Location, string Path, string? Notice)? made = null;
        ContentDialog? sheet = null;
        void Validate()
        {
            if (sheet is null) return;
            var check = CheckCreate(location, name.Text);
            ShowCaption(preview, name.Text.Trim().Length == 0 ? null : Locale.Get("workspace.create.path", new Dictionary<string, string> { ["path"] = check.Target }));
            // While the folder is being made it exists already: no "already exists" line then; an untouched empty name shows none.
            ShowCaption(problem, failure ?? (making || name.Text.Length == 0 ? null : check.Message));
            sheet.IsPrimaryButtonEnabled = !making && CanCreate(check);
        }
        var (locationRow, _, choose) = CreateLocationRow(() => location, folder => { location = folder; failure = null; Validate(); });
        var grid = CreateGrid();
        AddCreateRow(grid, Locale.Get("workspace.create.name"), name);
        AddCreateRow(grid, Locale.Get("workspace.create.location"), locationRow);
        AddCreateRow(grid, null, gitInit);
        var content = new StackPanel { Spacing = DesignMetrics.Spacing.Lg, Children = { CreateHeading(title, Locale.Get("workspace.newFolder.subtitle")), grid, preview, problem, busy } };
        var dialog = sheet = CreateDialog(content, title, Locale.Get("workspace.newFolder.create"), "workspace-new-folder-sheet");
        name.RegisterPropertyChangedCallback(TextBox.TextProperty, (_, _) => { failure = null; Validate(); });
        Validate();
        async Task Make()
        {
            var check = CheckCreate(location, name.Text);
            if (making || !CanCreate(check)) return;
            making = true; failure = null; busy.IsActive = true; busy.Visibility = Visibility.Visible;
            name.IsEnabled = gitInit.IsEnabled = choose.IsEnabled = false; Validate();
            var where = location;
            try
            {
                var result = await GitTool.MakeFolderAsync(check.Target, gitInit.IsChecked == true);
                if (result.Outcome != "failed") { made = (where, check.Target, result.Outcome == "madeButGitInitFailed" ? GitInitNotice(result.Message) : null); making = false; dialog.Hide(); return; }
                failure = result.Message;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { failure = ex.Message; }
            making = false; busy.IsActive = false; busy.Visibility = Visibility.Collapsed;
            name.IsEnabled = gitInit.IsEnabled = choose.IsEnabled = true; Validate();
        }
        // 만들기 never closes the sheet itself: it closes once the folder is there.
        dialog.PrimaryButtonClick += (sender, args) => { args.Cancel = true; _ = Make(); };
        dialog.Closing += (_, args) => { if (making) args.Cancel = true; };
        dialog.Opened += (_, _) => name.Focus(FocusState.Programmatic);
        dialogOpen = true;
        try { await dialog.ShowAsync(); }
        finally { dialogOpen = false; }
        if (made is { } done) await Act(() => FinishWorkspaceCreateAsync(done.Location, done.Path, done.Notice));
    }

    /// <summary>
    /// "Git으로 생성" as a ContentDialog: the address, checked once typing stops for 0.6 s (or at once from 연결 확인), the
    /// location, the folder name that follows the address until it is typed in, the path and the first problem. 생성 clones
    /// with progress, keeping the sheet open; Cancel (or Esc) stops a running clone, which removes what it wrote, and closes
    /// the sheet at rest. A failure stays in the sheet; with Git missing it offers 필요한 도구.
    /// </summary>
    private async Task ShowGitCreateDialogAsync()
    {
        var title = Locale.Get("workspace.git.title");
        var location = CreateLocationDefault();
        var url = CreateField(Locale.Get("workspace.git.url"), Locale.Get("workspace.git.urlPlaceholder"), "workspace-git-url");
        var name = CreateField(Locale.Get("workspace.create.name"), Locale.Get("workspace.create.name"), "workspace-git-name");
        var preview = CreateCaption(DesignToken.Ink2); var problem = CreateCaption(DesignToken.ErrText);
        AutomationProperties.SetAutomationId(problem, "workspace-create-problem");
        // The check's line: its state, the way to 필요한 도구 when Git is missing, and 연결 확인 at the trailing edge.
        var probeText = CreateText("", DesignMetrics.Type.Pill, DesignToken.Ink2); probeText.IsTextSelectionEnabled = true;
        AutomationProperties.SetAutomationId(probeText, "workspace-git-probe");
        var openSetupButton = new Button { Content = Locale.Get("workspace.git.openSetup"), TextSize = DesignMetrics.Type.Pill, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(openSetupButton, "workspace-git-open-setup");
        var test = new HyperlinkButton { Content = Locale.Get("workspace.git.test"), TextSize = DesignMetrics.Type.Pill, Padding = new Thickness(0), IsEnabled = false };
        AutomationProperties.SetAutomationId(test, "workspace-git-test");
        var probeRow = new Grid { ColumnSpacing = DesignMetrics.Spacing.Md };
        foreach (var width in new[] { new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) probeRow.ColumnDefinitions.Add(new() { Width = width });
        Grid.SetColumn(openSetupButton, 1); Grid.SetColumn(test, 2);
        probeRow.Children.Add(probeText); probeRow.Children.Add(openSetupButton); probeRow.Children.Add(test);
        // The clone's own failure, under the problem line, as the Mac's clone error row.
        var cloneError = CreateCaption(DesignToken.ErrText); cloneError.TextSize = DesignMetrics.Type.Pill;
        AutomationProperties.SetAutomationId(cloneError, "workspace-git-clone-error");
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0 };
        var progressText = CreateText("", DesignMetrics.Type.Pill, DesignToken.Ink2);
        var progress = new StackPanel { Spacing = DesignMetrics.Spacing.Xs, Visibility = Visibility.Collapsed, Children = { bar, progressText } };
        AutomationProperties.SetAutomationId(progress, "workspace-git-progress");

        var probe = "idle"; string? probeKind = null; var nameEdited = false; var settingName = false; var openSetupAfter = false;
        (string Kind, string Message)? cloneFailure = null;
        GitCloneJob? job = null; (string Location, string Path)? cloned = null;
        CancellationTokenSource? probeCancel = null;
        ContentDialog? sheet = null;
        bool Running() => job is not null;
        void ShowProbe(string state, string? kind = null, string? message = null)
        {
            probe = state; probeKind = kind;
            probeText.Text = state switch
            {
                "checking" => Locale.Get("workspace.git.checking"),
                "reachable" => "✓ " + Locale.Get("workspace.git.reachable"),
                "failed" => "✕ " + message,
                _ => "",
            };
            probeText.Foreground = brushes.Brush(state switch { "reachable" => DesignToken.DoneText, "failed" => DesignToken.ErrText, _ => DesignToken.Ink2 });
        }
        void Validate()
        {
            if (sheet is null) return;
            var check = CheckCreate(location, name.Text);
            ShowCaption(preview, name.Text.Trim().Length == 0 ? null : Locale.Get("workspace.create.path", new Dictionary<string, string> { ["path"] = check.Target }));
            // git makes the target at once: no "already exists" line while the clone runs.
            ShowCaption(problem, Running() || name.Text.Length == 0 ? null : check.Message);
            ShowCaption(cloneError, cloneFailure is { } failed ? "✕ " + failed.Message : null);
            // The way to 필요한 도구 shows only while a check or a clone says Git is missing; it goes with the line that said so.
            openSetupButton.Visibility = probe == "failed" && probeKind == "gitMissing" || cloneFailure?.Kind == "gitMissing" ? Visibility.Visible : Visibility.Collapsed;
            test.IsEnabled = !Running() && url.Text.Trim().Length > 0;
            sheet.IsPrimaryButtonEnabled = !Running() && probe == "reachable" && CanCreate(check);
        }
        async Task Probe()
        {
            probeCancel?.Cancel();
            var address = url.Text.Trim();
            if (address.Length == 0 || Running()) { ShowProbe("idle"); Validate(); return; }
            var cancel = probeCancel = new CancellationTokenSource();
            ShowProbe("checking"); Validate();
            try
            {
                var found = await GitTool.ProbeAsync(address, cancel.Token);
                if (!ReferenceEquals(probeCancel, cancel)) return;
                if (found.Reachable) ShowProbe("reachable");
                else ShowProbe("failed", found.Kind, WorkspaceCreate.ErrorMessage(found.Kind ?? "other", found.Message));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { if (ReferenceEquals(probeCancel, cancel)) ShowProbe("failed", "other", WorkspaceCreate.ErrorMessage("other", ex.Message)); }
            finally
            {
                if (ReferenceEquals(probeCancel, cancel)) probeCancel = null;
                cancel.Dispose();
            }
            Validate();
        }
        var typing = DispatcherQueue.CreateTimer(); typing.Interval = TimeSpan.FromMilliseconds(600); typing.IsRepeating = false;
        typing.Tick += (_, _) => { typing.Stop(); _ = Probe(); };

        var (locationRow, _, choose) = CreateLocationRow(() => location, folder => { location = folder; cloneFailure = null; Validate(); });
        var grid = CreateGrid();
        AddCreateRow(grid, Locale.Get("workspace.git.url"), url);
        AddCreateRow(grid, null, probeRow);
        AddCreateRow(grid, Locale.Get("workspace.create.location"), locationRow);
        AddCreateRow(grid, Locale.Get("workspace.create.name"), name);
        var content = new StackPanel { Spacing = DesignMetrics.Spacing.Lg, Children = { CreateHeading(title, Locale.Get("workspace.git.subtitle")), grid, preview, problem, cloneError, progress } };
        var dialog = sheet = CreateDialog(content, title, Locale.Get("workspace.git.create"), "workspace-git-sheet");

        // A new address drops the check on the old one at once (Enter must not clone what the check has not seen); the name follows it until typed in.
        url.RegisterPropertyChangedCallback(TextBox.TextProperty, (_, _) =>
        {
            probeCancel?.Cancel(); probeCancel = null;
            ShowProbe("idle"); cloneFailure = null;
            if (!nameEdited) { settingName = true; name.Text = WorkspaceCreate.RepoName(url.Text) ?? ""; settingName = false; }
            typing.Stop();
            if (url.Text.Trim().Length > 0) typing.Start();
            Validate();
        });
        name.RegisterPropertyChangedCallback(TextBox.TextProperty, (_, _) => { if (!settingName) nameEdited = true; cloneFailure = null; Validate(); });
        test.Click += (_, _) => { typing.Stop(); _ = Probe(); };
        openSetupButton.Click += (_, _) => { if (Running()) return; openSetupAfter = true; dialog.Hide(); };
        Validate();

        void SetRunning(bool running)
        {
            url.IsEnabled = name.IsEnabled = choose.IsEnabled = !running;
            progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        }
        void ShowProgress(CloneProgress step) { bar.Value = step.Fraction; progressText.Text = WorkspaceCreate.ProgressMessage(step); }
        async Task Clone()
        {
            var check = CheckCreate(location, name.Text);
            if (Running() || probe != "reachable" || !CanCreate(check)) return;
            cloneFailure = null;
            var where = location;
            GitCloneJob? started = null;
            // GitCloneJob reports from its reader thread; the sheet is drawn on the UI thread only, and only for this clone.
            var queue = DispatcherQueue;
            started = new GitCloneJob(url.Text, check.Target, step => queue.TryEnqueue(() => { if (ReferenceEquals(job, started)) ShowProgress(step); }));
            job = gitJobs[FallbackCreateJob] = started;
            ShowProgress(new CloneProgress("starting", 0, 0)); SetRunning(true); Validate();
            CloneResult result;
            try { result = await started.RunAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { result = new("failed", "other", ex.Message); }
            finally { job = null; gitJobs.Remove(FallbackCreateJob); }
            if (result.Outcome == "done") { cloned = (where, check.Target); dialog.Hide(); return; }
            SetRunning(false);
            if (result.Outcome == "failed") cloneFailure = (result.Kind ?? "other", WorkspaceCreate.ErrorMessage(result.Kind ?? "other", result.Message));
            Validate();
        }
        // 생성 never closes the sheet itself: it closes once the clone is done.
        dialog.PrimaryButtonClick += (sender, args) => { args.Cancel = true; _ = Clone(); };
        // Cancel and Esc stop a running clone and keep the sheet; it closes once the clone is done (or at rest).
        dialog.Closing += (_, args) => { if (Running() && cloned is null) { args.Cancel = true; job?.Cancel(); } };
        content.Unloaded += (_, _) =>
        {
            if (content.IsLoaded) return;
            typing.Stop(); probeCancel?.Cancel();
        };
        dialog.Opened += (_, _) => url.Focus(FocusState.Programmatic);
        dialogOpen = true;
        try { await dialog.ShowAsync(); }
        finally { dialogOpen = false; typing.Stop(); probeCancel?.Cancel(); job?.Cancel(); }
        if (openSetupAfter) { OpenSetup("git"); return; }
        if (cloned is { } done) await Act(() => FinishWorkspaceCreateAsync(done.Location, done.Path, null));
    }
}
