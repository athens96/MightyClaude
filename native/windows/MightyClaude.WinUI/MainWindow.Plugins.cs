using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

// The plugin window: the installed and available plugins of the active
// workspace, with their marketplace, scope, version and source. One window
// serves both providers — Claude through ClaudePluginReader and Codex through
// CodexPluginReader — and it is opened from the run pane menu and from the
// /plugin (Claude) and /plugins (Codex) slash commands.
//
// The only two mutations available are the two macOS has: install one plugin
// from the catalog (Claude: scope picker with the macOS three scopes; Codex:
// user level only) and refresh the registered marketplaces. Progress, cancel
// and result copy are taken from PluginStrings without any Korean typed here.
// Every decision — which rows, what each says, what buttons are enabled — is
// made by ClaudePluginBrowser in Core and proven on the Mac.
public sealed partial class MainWindow
{
    // Smoke hooks. Off in the real app: the window reads through the CLI and is
    // shown to the user.
    private Func<Workspace, Task<ClaudePluginSnapshot>>? smokePluginRead;
    private Func<PluginSmokeSurface, Task>? smokePluginDialog;
    private Func<Workspace, Task<ClaudePluginSnapshot>>? smokeCodexPluginRead;
    private Func<PluginSmokeSurface, Task>? smokeCodexPluginDialog;
    // Overrides the reader for the marketplace smoke so no real CLI starts.
    private Func<string, IPluginReader>? smokeReaderFactory;

    /// What the smoke driver is handed instead of a shown dialog: the real
    /// dialog it would see, the Core state it renders, and the actions the
    /// Opened event, the reload/tab/install/refresh buttons and cancel invoke.
    internal sealed record PluginSmokeSurface(
        ContentDialog Dialog, ClaudePluginBrowser Browser,
        Func<Task> Load, Func<string, Task> SelectTab,
        Func<string, Task<ClaudePluginOperationResult>> Install,
        Func<Task<ClaudePluginOperationResult>> Refresh,
        Action RequestCancel);

    /// The /plugin slash action and the run pane menu both land here.
    internal Task OpenPluginBrowser(string provider) => Act(async () =>
    {
        if (service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == service.Snapshot.ActiveWorkspaceId) is not { } workspace) return;
        await ShowPluginBrowser(provider, workspace);
    });

    private async Task<ClaudePluginBrowser> ShowPluginBrowser(string provider, Workspace workspace)
    {
        var browser = new ClaudePluginBrowser(provider, workspace);
        // 8 MiB of listing output, the macOS cap. Anything past it is refused by
        // the parser rather than truncated into a short list. The provider picks
        // the reader; both answer with the same ClaudePluginSnapshot.
        var runner = new CliRunner(outputCapBytes: ClaudePluginSupport.MaximumListingBytes);
        IPluginReader reader = smokeReaderFactory is { } factory
            ? factory(provider)
            : provider == ClaudePluginBrowser.CodexProvider
                ? new CodexPluginReader(runner)
                : (IPluginReader)new ClaudePluginReader(runner);

        var rows = new StackPanel { Spacing = 8 };
        var status = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Colors.Orange), Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(status, PluginAutomationId(provider, "load-status"));
        var diagnostics = new TextBlock { FontSize = 10, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var diagnosticsToggle = Button(PluginStrings.DiagnosticsDisclosure, () =>
        {
            diagnostics.Visibility = diagnostics.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            return Task.CompletedTask;
        });
        diagnosticsToggle.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(diagnosticsToggle, PluginAutomationId(provider, "diagnostics"));

        var version = new TextBlock { FontSize = 10, FontFamily = new FontFamily("Consolas"), Opacity = .7, VerticalAlignment = VerticalAlignment.Center };
        var progress = new TextBlock { FontSize = 11, Opacity = .7, Text = PluginStrings.ProgressLoading, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(progress, PluginAutomationId(provider, "progress"));

        // The controls are built first and their handlers attached below, after
        // every local the handlers read has been assigned.
        var installedTab = new Button { Content = browser.TabLabel(ClaudePluginBrowser.InstalledTab) };
        var marketplaceTab = new Button { Content = browser.TabLabel(ClaudePluginBrowser.MarketplaceTab) };
        AutomationProperties.SetAutomationId(installedTab, PluginAutomationId(provider, "tab-installed"));
        AutomationProperties.SetAutomationId(marketplaceTab, PluginAutomationId(provider, "tab-marketplace"));
        AutomationProperties.SetName(installedTab, PluginStrings.TabInstalled);
        AutomationProperties.SetName(marketplaceTab, PluginStrings.TabMarketplace);

        var search = new TextBox { PlaceholderText = PluginStrings.SearchPlaceholder, MinWidth = 260 };
        AutomationProperties.SetAutomationId(search, PluginAutomationId(provider, "search"));
        var filter = new ComboBox { MinWidth = 210 };
        AutomationProperties.SetAutomationId(filter, PluginAutomationId(provider, "marketplace-filter"));
        AutomationProperties.SetName(filter, PluginStrings.TabMarketplace);

        var reload = new Button { Content = PluginStrings.ButtonReload };
        AutomationProperties.SetAutomationId(reload, PluginAutomationId(provider, "reload"));
        AutomationProperties.SetName(reload, PluginStrings.ButtonReload);

        Task SelectPluginTab(string tab) { browser.Tab = tab; RenderPlugins(); return Task.CompletedTask; }

        // Rebuilding the choices raises SelectionChanged; while this flag is set
        // those raises are the window redrawing itself, not the user choosing.
        var rebuildingFilter = false;
        void RenderFilter()
        {
            var names = browser.Marketplaces;
            var wanted = new[] { "" }.Concat(names).ToList();
            var present = filter.Items.OfType<ComboBoxItem>().Select(i => (string)i.Tag).ToList();
            rebuildingFilter = true;
            try
            {
                if (!present.SequenceEqual(wanted))
                {
                    filter.Items.Clear();
                    filter.Items.Add(new ComboBoxItem { Content = PluginStrings.FilterAll, Tag = "" });
                    foreach (var name in names) filter.Items.Add(new ComboBoxItem { Content = name, Tag = name });
                }
                filter.SelectedIndex = Math.Max(0, filter.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == browser.MarketplaceFilter));
            }
            finally { rebuildingFilter = false; }
        }

        // Scope picker (Claude: 3 options, Codex: 1 option) and install controls.
        var scopePicker = new ComboBox { MinWidth = 230 };
        AutomationProperties.SetAutomationId(scopePicker, PluginAutomationId(provider, "scope-picker"));
        foreach (var opt in browser.ScopeOptions)
            scopePicker.Items.Add(new ComboBoxItem { Content = opt.Label, Tag = opt.Value });
        var pickerNote = new TextBlock { FontSize = 10, Opacity = .65, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(pickerNote, PluginAutomationId(provider, "picker-note"));

        // 마켓플레이스 새로고침 button and the Codex Git-only note.
        var refreshBtn = new Button { Content = PluginStrings.ButtonMarketplaceRefresh };
        AutomationProperties.SetAutomationId(refreshBtn, PluginAutomationId(provider, "refresh-marketplaces"));
        var marketplaceUnavailable = new TextBlock { FontSize = 10, Opacity = .65, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(marketplaceUnavailable, PluginAutomationId(provider, "marketplace-unavailable"));

        // Progress label, cancel button and result text for running operations.
        var operationProgress = new TextBlock { FontSize = 11, Opacity = .7 };
        AutomationProperties.SetAutomationId(operationProgress, PluginAutomationId(provider, "operation-progress"));
        var cancelBtn = new Button();
        AutomationProperties.SetAutomationId(cancelBtn, PluginAutomationId(provider, "cancel-operation"));
        cancelBtn.Click += (_, _) => browser.RequestCancel();
        var progressRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Visibility = Visibility.Collapsed };
        progressRow.Children.Add(operationProgress);
        progressRow.Children.Add(cancelBtn);
        var operationResult = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(operationResult, PluginAutomationId(provider, "operation-result"));

        // Wraps an install or refresh call: starts the operation (Phase set
        // synchronously), redraws to show progress, awaits, redraws the result.
        async Task<ClaudePluginOperationResult> OperateAsync(Func<Task<ClaudePluginOperationResult>> op)
        {
            var task = op();
            RenderPlugins();
            var result = await task;
            if (!closing) RenderPlugins();
            return result;
        }

        void RenderPlugins()
        {
            installedTab.Content = browser.TabLabel(ClaudePluginBrowser.InstalledTab);
            marketplaceTab.Content = browser.TabLabel(ClaudePluginBrowser.MarketplaceTab);
            installedTab.Opacity = browser.Tab == ClaudePluginBrowser.InstalledTab ? 1 : .6;
            marketplaceTab.Opacity = browser.Tab == ClaudePluginBrowser.MarketplaceTab ? 1 : .6;
            version.Text = browser.Snapshot?.CliVersion ?? "";
            progress.Visibility = browser.Loading ? Visibility.Visible : Visibility.Collapsed;
            reload.IsEnabled = !browser.Loading;
            status.Text = browser.StatusText ?? "";
            status.Visibility = browser.StatusText is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
            var output = browser.Snapshot?.DiagnosticOutput ?? "";
            diagnostics.Text = output;
            diagnosticsToggle.Visibility = output.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (output.Length == 0) diagnostics.Visibility = Visibility.Collapsed;
            RenderFilter();

            // Scope picker: keep the selection stable across redraws.
            var scopeIdx = browser.ScopeOptions.ToList().FindIndex(o => o.Value == browser.Scope);
            scopePicker.SelectedIndex = Math.Max(0, scopeIdx);
            pickerNote.Text = browser.ScopeNote;

            // Refresh button: enabled when there is at least one refreshable marketplace.
            refreshBtn.IsEnabled = browser.CanRefreshMarketplaces;
            marketplaceUnavailable.Text = browser.RefreshUnavailableNote ?? "";
            marketplaceUnavailable.Visibility = browser.RefreshUnavailableNote is { Length: > 0 }
                ? Visibility.Visible : Visibility.Collapsed;

            // Progress / cancel / result.
            operationProgress.Text = browser.ProgressLabel ?? "";
            cancelBtn.Content = browser.CancelLabel;
            cancelBtn.IsEnabled = browser.CanCancel;
            progressRow.Visibility = browser.IsMutating ? Visibility.Visible : Visibility.Collapsed;
            operationResult.Text = browser.ResultText ?? "";
            operationResult.Visibility = browser.ResultText is { Length: > 0 }
                ? Visibility.Visible : Visibility.Collapsed;

            // Rows: catalog tab gets an install button per row.
            rows.Children.Clear();
            if (browser.Tab == ClaudePluginBrowser.MarketplaceTab)
            {
                foreach (var row in browser.Rows())
                {
                    var panel = PluginRowPanel(provider, row);
                    var btnLabel = browser.InstallButtonLabel(row.Id);
                    var installBtn = new Button { Content = btnLabel, IsEnabled = browser.CanInstall(row.Id) };
                    AutomationProperties.SetAutomationId(installBtn, PluginAutomationId(provider, "install-" + row.Id));
                    var capturedId = row.Id;
                    installBtn.Click += async (_, _) => await OperateAsync(() => browser.InstallAsync(reader, capturedId));
                    panel.Children.Add(installBtn);
                    rows.Children.Add(panel);
                }
            }
            else
            {
                foreach (var row in browser.Rows()) rows.Children.Add(PluginRow(provider, row));
            }
            if (rows.Children.Count == 0)
            {
                rows.Children.Add(new TextBlock { Text = browser.EmptyMessage, FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 28, 0, 28) });
                if (browser.ShowsMarketplaceHelpLink)
                    rows.Children.Add(new HyperlinkButton { Content = PluginStrings.MarketplaceHelpLink, NavigateUri = new Uri("https://code.claude.com/docs/en/discover-plugins#add-marketplaces"), HorizontalAlignment = HorizontalAlignment.Center });
                if (browser.MarketplaceHelpText is { Length: > 0 } help)
                {
                    var sentence = new TextBlock { Text = help, FontSize = 11, Opacity = .7, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center };
                    AutomationProperties.SetAutomationId(sentence, PluginAutomationId(provider, "marketplace-help"));
                    rows.Children.Add(sentence);
                }
            }
        }

        async Task LoadPluginsAsync()
        {
            if (browser.IsRemote || browser.Loading) return;
            browser.BeginLoad();
            RenderPlugins();
            ClaudePluginSnapshot snapshot;
            try
            {
                var smokeRead = provider == ClaudePluginBrowser.CodexProvider ? smokeCodexPluginRead : smokePluginRead;
                snapshot = smokeRead is { } fixture
                    ? await fixture(workspace)
                    // The read never runs on the UI thread; the window only comes back to redraw.
                    : await Task.Run(() => reader.SnapshotAsync(workspace));
            }
            catch (Exception error)
            {
                snapshot = new ClaudePluginSnapshot { Status = ClaudePluginStatus.Failed, Detail = PluginStrings.DetailIncomplete, DiagnosticOutput = error.Message };
            }
            if (closing) return;
            browser.Apply(snapshot);
            RenderPlugins();
        }

        installedTab.Click += async (_, _) => await SelectPluginTab(ClaudePluginBrowser.InstalledTab);
        marketplaceTab.Click += async (_, _) => await SelectPluginTab(ClaudePluginBrowser.MarketplaceTab);
        reload.Click += async (_, _) => await LoadPluginsAsync();
        scopePicker.SelectionChanged += (_, _) =>
        {
            if (scopePicker.SelectedItem is ComboBoxItem { Tag: string tag }) browser.Scope = tag;
            pickerNote.Text = browser.ScopeNote;
            RenderPlugins();
        };
        refreshBtn.Click += async (_, _) => await OperateAsync(() => browser.RefreshMarketplacesAsync(reader));
        search.RegisterPropertyChangedCallback(TextBox.TextProperty, (_, _) => { browser.Search = search.Text; RenderPlugins(); });
        filter.SelectionChanged += (_, _) =>
        {
            if (rebuildingFilter) return;
            var wanted = filter.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "";
            if (browser.MarketplaceFilter == wanted) return;
            browser.MarketplaceFilter = wanted;
            RenderPlugins();
        };

        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(new TextBlock { Text = workspace.Name, FontSize = 12, Opacity = .75, TextTrimming = TextTrimming.CharacterEllipsis });
        header.Children.Add(new TextBlock { Text = workspace.Path, FontSize = 10, FontFamily = new FontFamily("Consolas"), Opacity = .55, TextTrimming = TextTrimming.CharacterEllipsis });
        header.Children.Add(version);

        var body = new StackPanel { Spacing = 10, Width = 700 };
        body.Children.Add(header);
        if (browser.IsRemote)
        {
            var remote = new StackPanel { Spacing = 8, Margin = new Thickness(0, 28, 0, 28), HorizontalAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetAutomationId(remote, PluginAutomationId(provider, "remote-unavailable"));
            remote.Children.Add(new TextBlock { Text = PluginStrings.RemoteTitle, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            remote.Children.Add(new TextBlock { Text = PluginStrings.RemoteNote, FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(remote);
        }
        else
        {
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            tabs.Children.Add(installedTab);
            tabs.Children.Add(marketplaceTab);
            tabs.Children.Add(new TextBlock { Width = 180 });
            tabs.Children.Add(reload);
            body.Children.Add(tabs);
            var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            filters.Children.Add(search);
            filters.Children.Add(filter);
            body.Children.Add(filters);
            body.Children.Add(progress);
            body.Children.Add(status);
            body.Children.Add(diagnosticsToggle);
            body.Children.Add(diagnostics);
            // Scope picker and explanation (always shown for non-remote workspaces).
            var pickerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            pickerRow.Children.Add(scopePicker);
            pickerRow.Children.Add(refreshBtn);
            body.Children.Add(pickerRow);
            body.Children.Add(pickerNote);
            body.Children.Add(marketplaceUnavailable);
            body.Children.Add(progressRow);
            body.Children.Add(operationResult);
            body.Children.Add(new TextBlock { Text = browser.FooterNote, FontSize = 10, Opacity = .65, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new ScrollViewer { Content = rows, Height = 340, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled });
            RenderPlugins();
        }

        var dialog = new ContentDialog
        {
            Title = browser.Title,
            Content = body,
            CloseButtonText = PluginStrings.ButtonClose,
            XamlRoot = root.XamlRoot,
        };
        AutomationProperties.SetAutomationId(dialog, PluginAutomationId(provider, "browser"));
        dialog.Opened += (_, _) => { if (!browser.IsRemote) _ = LoadPluginsAsync(); };
        // macOS keeps 닫기 disabled while an operation runs; a ContentDialog's
        // close button cannot be disabled, so the close itself is refused.
        dialog.Closing += (_, args) => { if (!browser.CanClose) args.Cancel = true; };

        dialogOpen = true;
        try
        {
            var smokeDialog = provider == ClaudePluginBrowser.CodexProvider ? smokeCodexPluginDialog : smokePluginDialog;
            if (smokeDialog is { } driver)
                await driver(new PluginSmokeSurface(dialog, browser, LoadPluginsAsync, SelectPluginTab,
                    id => OperateAsync(() => browser.InstallAsync(reader, id)),
                    () => OperateAsync(() => browser.RefreshMarketplacesAsync(reader)),
                    browser.RequestCancel));
            else await dialog.ShowAsync();
        }
        finally
        {
            dialogOpen = false;
            // Closing stops admitting reads. There is nothing to roll back.
            reader.Shutdown();
        }
        return browser;
    }

    private static string PluginAutomationId(string provider, string part) => ClaudePluginSupport.AutomationId(provider, part);

    // Every element the built dialog holds, so the smoke run finds its controls
    // by automation id the way a user finds them on screen.
    private static IEnumerable<DependencyObject> PluginDescendants(object? node)
    {
        switch (node)
        {
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    yield return child;
                    foreach (var nested in PluginDescendants(child)) yield return nested;
                }
                break;
            case ScrollViewer scroll:
                if (scroll.Content is DependencyObject content)
                {
                    yield return content;
                    foreach (var nested in PluginDescendants(content)) yield return nested;
                }
                break;
            case ContentControl control:
                if (control.Content is DependencyObject inner)
                {
                    yield return inner;
                    foreach (var nested in PluginDescendants(inner)) yield return nested;
                }
                break;
        }
    }

    private static T PluginControl<T>(ContentDialog dialog, string provider, string part) where T : DependencyObject =>
        PluginDescendants(dialog.Content).OfType<T>()
            .First(element => AutomationProperties.GetAutomationId(element) == PluginAutomationId(provider, part));

    private static int PluginRowCount(ContentDialog dialog, string provider) =>
        PluginDescendants(dialog.Content).OfType<FrameworkElement>()
            .Count(e => AutomationProperties.GetAutomationId(e).StartsWith(PluginAutomationId(provider, "row-"), StringComparison.Ordinal));

    private static readonly ClaudePluginSnapshot PluginSmokeSnapshot = new()
    {
        Status = ClaudePluginStatus.Ready,
        Detail = PluginStrings.DetailReady,
        CliVersion = "2.1.271 (smoke fixture)",
        Installed =
        [
            new ClaudeInstalledPlugin { PluginId = "fmt@sample", Name = "fmt", Marketplace = "sample", Scope = "user", Version = "1.2.0", Enabled = true, Description = "Formats source files" },
            new ClaudeInstalledPlugin { PluginId = "lint@other", Name = "lint", Marketplace = "other", Scope = "project", Enabled = false, ProjectPath = "C:\\fixture\\project", Description = "Lints source files" },
        ],
        Available =
        [
            new ClaudeCatalogPlugin { Id = "fmt@sample", Name = "fmt", Marketplace = "sample", Version = "1.2.0", SourceKind = "github", Description = "Formats source files" },
            new ClaudeCatalogPlugin { Id = "docs@sample", Name = "docs", Marketplace = "sample", SourceKind = "git", Description = "Writes docs" },
            new ClaudeCatalogPlugin { Id = "ship@other", Name = "ship", Marketplace = "other", SourceKind = "npm", Description = "Ships builds" },
        ],
        Marketplaces = [new ClaudePluginMarketplace("other", "git"), new ClaudePluginMarketplace("sample", "github")],
        UpdatedAt = Wire.Now(),
    };

    // Drives the real plugin window with a fixture snapshot: the two tabs with
    // their counts, the marketplace filter, the search box, a reload that reads
    // again, the macOS sentence a missing CLI produces, and the two sentences a
    // remote workspace shows. No claude process starts and no workspace changes.
    // Puts back the two smoke hooks it set.
    internal async Task<ClaudePluginSmokeOutcome> RunClaudePluginSmoke()
    {
        var beforeRead = smokePluginRead;
        var beforeDialog = smokePluginDialog;
        var workspace = service.Snapshot.Workspaces.First(w => w.Id == service.Snapshot.ActiveWorkspaceId);
        var reads = 0;
        var outcome = new ClaudePluginSmokeOutcome();
        try
        {
            smokePluginRead = _ =>
            {
                reads++;
                return Task.FromResult(reads <= 1
                    ? PluginSmokeSnapshot
                    : new ClaudePluginSnapshot { Status = ClaudePluginStatus.Missing, Detail = PluginStrings.DetailMissingCli });
            };

            string title = "", installedTab = "", marketplaceTab = "", installedSubtitle = "", availableSubtitle = "", reloadedStatus = "";
            int installedRows = 0, availableRows = 0, filteredRows = 0, searchedRows = 0, mutating = 0;

            smokePluginDialog = async surface =>
            {
                var dialog = surface.Dialog;
                title = (string)dialog.Title;
                // The Opened handler's own action: the first read.
                await surface.Load();
                Require(surface.Browser.IsReady, "플러그인 목록을 픽스처로 불러오지 못했습니다.");
                installedTab = (string)PluginControl<Button>(dialog, "claude", "tab-installed").Content!;
                marketplaceTab = (string)PluginControl<Button>(dialog, "claude", "tab-marketplace").Content!;
                installedRows = PluginRowCount(dialog, "claude");
                installedSubtitle = surface.Browser.InstalledRows()[0].Subtitle;

                // The marketplace tab button's own action.
                await surface.SelectTab(ClaudePluginBrowser.MarketplaceTab);
                availableRows = PluginRowCount(dialog, "claude");
                availableSubtitle = surface.Browser.AvailableRows()[0].Subtitle;

                // The real filter: selecting a marketplace raises SelectionChanged.
                var filter = PluginControl<ComboBox>(dialog, "claude", "marketplace-filter");
                Require(filter.Items.Count == 3 && (string)((ComboBoxItem)filter.Items[0]).Content! == PluginStrings.FilterAll,
                    "마켓플레이스 필터가 전체와 등록된 마켓플레이스를 보여주지 않습니다.");
                filter.SelectedIndex = filter.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == "other");
                filteredRows = PluginRowCount(dialog, "claude");

                // The real search box: its Text change raises the registered callback.
                var search = PluginControl<TextBox>(dialog, "claude", "search");
                Require(search.PlaceholderText == PluginStrings.SearchPlaceholder, "검색 입력란의 안내 문구가 다릅니다.");
                search.Text = "없는이름";
                searchedRows = PluginRowCount(dialog, "claude");
                search.Text = "";
                filter.SelectedIndex = 0;

                // No control that would change anything may exist in this window.
                // Core decides what an id names, so the installed tab's own id
                // (tab-installed) is not counted as an install button.
                mutating = PluginDescendants(dialog.Content).OfType<FrameworkElement>()
                    .Count(e => AutomationProperties.GetAutomationId(e) is { Length: > 0 } id
                        && ClaudePluginSupport.NamesAChange(id, "claude"));
                Require(dialog.PrimaryButtonText is null or "" && dialog.SecondaryButtonText is null or "",
                    "플러그인 창에는 목록을 바꾸는 단추가 없어야 합니다.");
                Require(dialog.CloseButtonText == PluginStrings.ButtonClose, "닫기 단추의 문구가 다릅니다.");
                Require((string)PluginControl<Button>(dialog, "claude", "reload").Content! == PluginStrings.ButtonReload,
                    "목록 새로고침 단추의 문구가 다릅니다.");

                // The reload button's own action, answered by a missing CLI.
                await surface.Load();
                var status = PluginControl<TextBlock>(dialog, "claude", "load-status");
                reloadedStatus = status.Text;
                Require(status.Visibility == Visibility.Visible, "CLI를 찾지 못한 이유가 화면에 보이지 않습니다.");
            };
            await ShowPluginBrowser("claude", workspace);

            // A remote workspace: the two macOS sentences and no read at all.
            var remoteReads = reads;
            var remoteSentences = new List<string>();
            smokePluginDialog = surface =>
            {
                var panel = PluginControl<StackPanel>(surface.Dialog, "claude", "remote-unavailable");
                remoteSentences.AddRange(panel.Children.OfType<TextBlock>().Select(t => t.Text));
                return Task.CompletedTask;
            };
            await ShowPluginBrowser("claude", workspace with { Id = "smoke-remote", Remote = new RemoteReference("smoke", "peer", "Fixture host") });
            Require(reads == remoteReads, "원격 워크스페이스에서 플러그인을 읽으려 했습니다.");
            Require(remoteSentences.SequenceEqual([PluginStrings.RemoteTitle, PluginStrings.RemoteNote]),
                "원격 워크스페이스의 두 문장이 macOS와 다릅니다.");

            outcome = new ClaudePluginSmokeOutcome
            {
                Title = title,
                InstalledTab = installedTab,
                MarketplaceTab = marketplaceTab,
                InstalledRows = installedRows,
                AvailableRows = availableRows,
                FilteredRows = filteredRows,
                SearchedRows = searchedRows,
                InstalledSubtitle = installedSubtitle,
                AvailableSubtitle = availableSubtitle,
                ReloadedFromStatus = reloadedStatus,
                RemoteSentences = remoteSentences,
                Reads = reads,
                MutatingControls = mutating,
                Restored = true,
            };
            Require(outcome.Title == "Claude 플러그인" && outcome.InstalledTab == "설치됨 2" && outcome.MarketplaceTab == "마켓플레이스 3",
                "플러그인 창의 제목 또는 탭 개수가 macOS와 다릅니다.");
            Require(outcome.InstalledRows == 2 && outcome.AvailableRows == 3 && outcome.FilteredRows == 1 && outcome.SearchedRows == 0,
                "탭·필터·검색이 목록을 macOS처럼 좁히지 않았습니다.");
            // scope-picker + refresh-marketplaces + 3 install-* buttons = 5
            Require(outcome.MutatingControls == 5, "Claude 마켓플레이스 탭의 변경 컨트롤 수가 예상과 다릅니다: " + outcome.MutatingControls);
            Require(outcome.ReloadedFromStatus == PluginStrings.DetailMissingCli, "CLI가 없을 때의 문장이 macOS와 다릅니다.");
            Require(outcome.Reads == 2, "목록 읽기 횟수가 잘못됐습니다: " + outcome.Reads);
            return outcome;
        }
        finally
        {
            smokePluginRead = beforeRead;
            smokePluginDialog = beforeDialog;
            Render();
        }
    }


    private static readonly ClaudePluginSnapshot CodexPluginSmokeSnapshot = new()
    {
        Status = ClaudePluginStatus.Ready,
        Detail = CodexPluginStrings.DetailReady,
        CliVersion = "codex-cli 0.153.4 (smoke fixture)",
        Installed =
        [
            new ClaudeInstalledPlugin { PluginId = "format@sample", Name = "format", Marketplace = "sample", Scope = "user", Version = "1.0.0", Enabled = true, Description = "Formats source files" },
            // Codex has no project scope. The window must not draw this row.
            new ClaudeInstalledPlugin { PluginId = "stray@sample", Name = "stray", Marketplace = "sample", Scope = "project", ProjectPath = "C:\\fixture\\project", Description = "Never listed under Codex" },
        ],
        Available =
        [
            new ClaudeCatalogPlugin { Id = "format@sample", Name = "format", Marketplace = "sample", Version = "1.0.0", SourceKind = "local", Description = "Formats source files" },
            new ClaudeCatalogPlugin { Id = "remote@openai-curated-remote", Name = "remote", Marketplace = "openai-curated-remote", SourceKind = "remote", Description = "Curated remote catalog entry" },
        ],
        Marketplaces = [new ClaudePluginMarketplace("sample", "git")],
        UpdatedAt = Wire.Now(),
    };

    // Drives the same real plugin window under the Codex title with a Codex
    // fixture: the two tabs with their counts, the user-level row, the Codex
    // footer, the Codex ready sentence, the sentence an empty marketplace list
    // shows, and the sentence a CLI without the JSON plugin commands produces.
    // No codex process starts and no workspace changes. Puts back the two hooks.
    internal async Task<CodexPluginSmokeOutcome> RunCodexPluginSmoke()
    {
        var beforeRead = smokeCodexPluginRead;
        var beforeDialog = smokeCodexPluginDialog;
        var workspace = service.Snapshot.Workspaces.First(w => w.Id == service.Snapshot.ActiveWorkspaceId);
        var reads = 0;
        try
        {
            smokeCodexPluginRead = _ =>
            {
                reads++;
                return Task.FromResult(reads switch
                {
                    1 => CodexPluginSmokeSnapshot,
                    // A CLI with nothing registered: ready, but the Codex
                    // sentence tells the user to register a marketplace.
                    2 => new ClaudePluginSnapshot { Status = ClaudePluginStatus.Ready, Detail = CodexPluginStrings.DetailNoMarketplaces, CliVersion = "codex-cli 0.153.4 (smoke fixture)" },
                    // A CLI whose plugin subcommands lack the JSON flags.
                    _ => new ClaudePluginSnapshot { Status = ClaudePluginStatus.Unsupported, Detail = CodexPluginStrings.DetailUnsupported },
                });
            };

            string title = "", installedTab = "", marketplaceTab = "", installedSubtitle = "", footer = "";
            string readyStatus = "", noMarketplaceHelp = "", unsupportedStatus = "";
            int installedRows = 0, availableRows = 0, filteredRows = 0, searchedRows = 0, mutating = 0;

            smokeCodexPluginDialog = async surface =>
            {
                var dialog = surface.Dialog;
                title = (string)dialog.Title;
                await surface.Load();
                Require(surface.Browser.IsReady, "Codex 플러그인 목록을 픽스처로 불러오지 못했습니다.");
                installedTab = (string)PluginControl<Button>(dialog, "codex", "tab-installed").Content!;
                marketplaceTab = (string)PluginControl<Button>(dialog, "codex", "tab-marketplace").Content!;
                installedRows = PluginRowCount(dialog, "codex");
                installedSubtitle = surface.Browser.InstalledRows()[0].Subtitle;
                footer = PluginDescendants(dialog.Content).OfType<TextBlock>().Select(t => t.Text).First(t => t == surface.Browser.FooterNote);
                readyStatus = PluginControl<TextBlock>(dialog, "codex", "load-status").Text;

                await surface.SelectTab(ClaudePluginBrowser.MarketplaceTab);
                availableRows = PluginRowCount(dialog, "codex");

                var filter = PluginControl<ComboBox>(dialog, "codex", "marketplace-filter");
                filter.SelectedIndex = filter.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == "sample");
                filteredRows = PluginRowCount(dialog, "codex");
                var search = PluginControl<TextBox>(dialog, "codex", "search");
                search.Text = "없는이름";
                searchedRows = PluginRowCount(dialog, "codex");
                search.Text = "";
                filter.SelectedIndex = 0;

                // Looking must offer nothing that would change anything.
                mutating = PluginDescendants(dialog.Content).OfType<FrameworkElement>()
                    .Count(e => AutomationProperties.GetAutomationId(e) is { Length: > 0 } id
                        && ClaudePluginSupport.NamesAChange(id, "codex"));
                Require(dialog.PrimaryButtonText is null or "" && dialog.SecondaryButtonText is null or "",
                    "Codex 플러그인 창에는 목록을 바꾸는 단추가 없어야 합니다.");

                // The reload button's own action, answered by an empty registry.
                await surface.Load();
                noMarketplaceHelp = PluginControl<TextBlock>(dialog, "codex", "marketplace-help").Text;

                // And again, answered by a CLI without the JSON plugin commands.
                await surface.Load();
                var status = PluginControl<TextBlock>(dialog, "codex", "load-status");
                unsupportedStatus = status.Text;
                Require(status.Visibility == Visibility.Visible, "Codex CLI가 지원하지 않는 이유가 화면에 보이지 않습니다.");
            };
            await ShowPluginBrowser("codex", workspace);

            var outcome = new CodexPluginSmokeOutcome
            {
                Title = title,
                InstalledTab = installedTab,
                MarketplaceTab = marketplaceTab,
                InstalledRows = installedRows,
                AvailableRows = availableRows,
                FilteredRows = filteredRows,
                SearchedRows = searchedRows,
                InstalledSubtitle = installedSubtitle,
                FooterNote = footer,
                ReadyStatus = readyStatus,
                NoMarketplaceHelp = noMarketplaceHelp,
                UnsupportedStatus = unsupportedStatus,
                Reads = reads,
                MutatingControls = mutating,
                Restored = true,
            };
            Require(outcome.Title == "Codex 플러그인" && outcome.InstalledTab == "설치됨 1" && outcome.MarketplaceTab == "마켓플레이스 2",
                "Codex 플러그인 창의 제목 또는 탭 개수가 macOS와 다릅니다.");
            Require(outcome.InstalledRows == 1 && outcome.InstalledSubtitle == PluginStrings.SubtitleTemplate.Replace("{left}", "sample").Replace("{right}", PluginStrings.ScopeUser),
                "Codex 설치 목록은 사용자 범위 한 줄이어야 합니다: " + outcome.InstalledRows + " / " + outcome.InstalledSubtitle);
            Require(outcome.AvailableRows == 2 && outcome.FilteredRows == 1 && outcome.SearchedRows == 0,
                "탭·필터·검색이 Codex 목록을 macOS처럼 좁히지 않았습니다.");
            // scope-picker + refresh-marketplaces + 2 install-* buttons = 4
            Require(outcome.MutatingControls == 4, "Codex 마켓플레이스 탭의 변경 컨트롤 수가 예상과 다릅니다: " + outcome.MutatingControls);
            Require(outcome.FooterNote == CodexPluginStrings.FooterNote, "Codex 창의 안내 문장이 다릅니다.");
            Require(outcome.ReadyStatus == CodexPluginStrings.DetailReady, "Codex는 목록을 읽은 뒤에도 안내 문장을 보여야 합니다.");
            Require(outcome.NoMarketplaceHelp == CodexPluginStrings.MarketplaceHelp, "마켓플레이스가 없을 때의 문장이 macOS와 다릅니다.");
            Require(outcome.UnsupportedStatus == CodexPluginStrings.DetailUnsupported, "CLI가 지원하지 않을 때의 문장이 macOS와 다릅니다.");
            Require(outcome.Reads == 3, "Codex 목록 읽기 횟수가 잘못됐습니다: " + outcome.Reads);
            return outcome;
        }
        finally
        {
            smokeCodexPluginRead = beforeRead;
            smokeCodexPluginDialog = beforeDialog;
            Render();
        }
    }

    private static StackPanel PluginRowPanel(string provider, ClaudePluginRow row)
    {
        var panel = new StackPanel { Spacing = 5, Padding = new Thickness(11), CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(22, 128, 128, 128)) };
        AutomationProperties.SetAutomationId(panel, PluginAutomationId(provider, "row-" + row.Id));
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        title.Children.Add(new TextBlock { Text = row.Name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (row.Version is { Length: > 0 })
            title.Children.Add(new TextBlock { Text = row.Version, FontSize = 10, FontFamily = new FontFamily("Consolas"), Opacity = .7, VerticalAlignment = VerticalAlignment.Center });
        if (row.State is { Length: > 0 })
            title.Children.Add(new TextBlock { Text = row.State, FontSize = 10, Opacity = .7, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(title);
        if (row.Description.Length > 0)
            panel.Children.Add(new TextBlock { Text = row.Description, FontSize = 11, Opacity = .75, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = row.Subtitle, FontSize = 10, Opacity = .65, TextWrapping = TextWrapping.Wrap });
        if (row.ProjectPath is { Length: > 0 })
            panel.Children.Add(new TextBlock { Text = row.ProjectPath, FontSize = 10, FontFamily = new FontFamily("Consolas"), Opacity = .5, TextTrimming = TextTrimming.CharacterEllipsis });
        foreach (var error in row.Errors)
            panel.Children.Add(new TextBlock { Text = error, FontSize = 10, Foreground = new SolidColorBrush(Colors.Orange), TextWrapping = TextWrapping.Wrap });
        foreach (var note in row.Notes)
            panel.Children.Add(new TextBlock { Text = note, FontSize = 10, Opacity = .65, TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private static FrameworkElement PluginRow(string provider, ClaudePluginRow row) => PluginRowPanel(provider, row);

    // Drives the real plugin window with a fake reader that returns success
    // immediately for install and refresh. Verifies the scope picker options,
    // the install result and the refresh result against the macOS sentences.
    // No claude or codex process starts. Puts back every hook it sets.
    internal async Task<PluginMarketplaceSmokeOutcome> RunPluginMarketplaceSmoke()
    {
        var beforeRead = smokePluginRead;
        var beforeCodexRead = smokeCodexPluginRead;
        var beforeDialog = smokePluginDialog;
        var beforeCodexDialog = smokeCodexPluginDialog;
        var beforeReaderFactory = smokeReaderFactory;
        var workspace = service.Snapshot.Workspaces.First(w => w.Id == service.Snapshot.ActiveWorkspaceId);

        int claudeScopes = 0, codexScopes = 0;
        string installResult = "", refreshResult = "", cancelResult = "";
        try
        {
            // --- Claude: verify scope options, install and marketplace refresh ---
            smokeReaderFactory = _ => new FakeMarketplaceReader(PluginSmokeSnapshot);
            smokePluginDialog = async surface =>
            {
                await surface.Load();
                Require(surface.Browser.IsReady, "플러그인 목록을 픽스처로 불러오지 못했습니다.");
                claudeScopes = surface.Browser.ScopeOptions.Count;
                Require(claudeScopes == 3, "Claude 설치 범위가 3개가 아닙니다: " + claudeScopes);
                Require(surface.Browser.ScopeOptions[0].Value == "local" && surface.Browser.ScopeOptions[1].Value == "project" && surface.Browser.ScopeOptions[2].Value == "user",
                    "Claude 범위 목록이 local·project·user가 아닙니다.");

                await surface.SelectTab(ClaudePluginBrowser.MarketplaceTab);
                // docs@sample is in the catalog and not installed: can be installed.
                Require(surface.Browser.CanInstall("docs@sample"), "docs@sample 설치 단추가 활성화되지 않았습니다.");

                var install = await surface.Install("docs@sample");
                installResult = install.Detail;
                Require(installResult == PluginStrings.InstallSucceeded,
                    "설치 완료 문장이 macOS와 다릅니다: " + installResult);

                var refresh = await surface.Refresh();
                refreshResult = refresh.Detail;
                Require(refresh.Status == ClaudePluginStatus.Succeeded,
                    "마켓플레이스 새로고침이 실패했습니다: " + refreshResult);
            };
            await ShowPluginBrowser("claude", workspace);

            // --- Claude: the real cancel button stops a running install ---
            smokeReaderFactory = _ => new FakeMarketplaceReader(PluginSmokeSnapshot, holdUntilCancelled: true);
            smokePluginDialog = async surface =>
            {
                await surface.Load();
                await surface.SelectTab(ClaudePluginBrowser.MarketplaceTab);
                var pending = surface.Install("docs@sample");
                Require(surface.Browser.IsMutating, "설치가 시작되었는데 진행 상태가 아닙니다.");
                Require(surface.Browser.ProgressLabel == PluginStrings.ProgressInstalling,
                    "설치 진행 문장이 macOS와 다릅니다: " + surface.Browser.ProgressLabel);
                Require(!surface.Browser.CanClose, "작업이 진행 중인데 닫기가 막히지 않았습니다.");
                Require(surface.Browser.CanCancel, "작업이 진행 중인데 취소할 수 없습니다.");
                surface.RequestCancel();
                cancelResult = (await pending).Detail;
                Require(cancelResult == PluginStrings.OperationCancelledByUser,
                    "취소 문장이 macOS와 다릅니다: " + cancelResult);
                Require(surface.Browser.CanClose && !surface.Browser.IsMutating,
                    "취소한 뒤에도 창이 작업 중으로 남아 있습니다.");
            };
            await ShowPluginBrowser("claude", workspace);

            // --- Codex: verify single user-level scope option ---
            smokeReaderFactory = _ => new FakeMarketplaceReader(CodexPluginSmokeSnapshot);
            smokeCodexPluginDialog = async surface =>
            {
                await surface.Load();
                Require(surface.Browser.IsReady, "Codex 플러그인 목록을 픽스처로 불러오지 못했습니다.");
                codexScopes = surface.Browser.ScopeOptions.Count;
                Require(codexScopes == 1 && surface.Browser.ScopeOptions[0].Value == "user",
                    "Codex 설치 범위가 user 하나가 아닙니다.");
            };
            await ShowPluginBrowser("codex", workspace);

            return new PluginMarketplaceSmokeOutcome
            {
                ClaudeScopeOptions = claudeScopes,
                CodexScopeOptions = codexScopes,
                InstallResult = installResult,
                CancelResult = cancelResult,
                RefreshResult = refreshResult,
                Restored = true,
            };
        }
        finally
        {
            smokePluginRead = beforeRead;
            smokeCodexPluginRead = beforeCodexRead;
            smokePluginDialog = beforeDialog;
            smokeCodexPluginDialog = beforeCodexDialog;
            smokeReaderFactory = beforeReaderFactory;
            Render();
        }
    }

    // A fake IPluginReader for the marketplace smoke: never starts a real CLI.
    // SnapshotAsync returns the fixture immediately; install and refresh return
    // success so the smoke can verify the result text without a real CLI.
    private sealed class FakeMarketplaceReader(ClaudePluginSnapshot fixture, bool holdUntilCancelled = false) : IPluginReader
    {
        public Task<ClaudePluginSnapshot> SnapshotAsync(Workspace workspace, CancellationToken cancellation = default)
            => Task.FromResult(fixture);
        public void Shutdown() { }
        public async Task<ClaudePluginOperationResult> InstallAsync(string pluginId, string scope, Workspace workspace, CancellationToken cancellation = default)
        {
            // The cancel leg waits for the window's own cancel button instead of
            // for a real CLI, so the smoke never depends on timing.
            if (holdUntilCancelled) await Task.Delay(Timeout.Infinite, cancellation);
            return new ClaudePluginOperationResult(ClaudePluginStatus.Succeeded, PluginStrings.InstallSucceeded);
        }
        public Task<ClaudePluginOperationResult> RefreshMarketplaceAsync(string marketplace, Workspace workspace, CancellationToken cancellation = default)
            => Task.FromResult(new ClaudePluginOperationResult(ClaudePluginStatus.Succeeded,
                PluginStrings.MarketplacesRefreshedTemplate.Replace("{count}", "1")));
    }
}
