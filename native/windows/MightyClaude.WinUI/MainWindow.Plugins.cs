using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

// The Claude plugin window: the installed and available plugins of the active
// workspace, with their marketplace, scope, version and source. It is opened
// from the run pane menu and from the /plugin slash command.
//
// Looking changes nothing, so this file draws no install button, no scope
// picker and no marketplace refresh. Every decision — which rows, what each row
// says, what an empty or failed list says — is made by ClaudePluginBrowser in
// Core and proven on the Mac. No Korean is typed here.
public sealed partial class MainWindow
{
    // Smoke hooks. Off in the real app: the window reads through the CLI and is
    // shown to the user.
    private Func<Workspace, Task<ClaudePluginSnapshot>>? smokePluginRead;
    private Func<PluginSmokeSurface, Task>? smokePluginDialog;

    /// What the smoke driver is handed instead of a shown dialog: the real
    /// dialog it would see, the Core state it renders, and the two actions the
    /// Opened event, the reload button and the tab buttons invoke.
    internal sealed record PluginSmokeSurface(
        ContentDialog Dialog, ClaudePluginBrowser Browser, Func<Task> Load, Func<string, Task> SelectTab);

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
        // the parser rather than truncated into a short list.
        var reader = new ClaudePluginReader(new CliRunner(outputCapBytes: ClaudePluginSupport.MaximumListingBytes));

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

            rows.Children.Clear();
            foreach (var row in browser.Rows()) rows.Children.Add(PluginRow(provider, row));
            if (rows.Children.Count == 0)
            {
                rows.Children.Add(new TextBlock { Text = browser.EmptyMessage, FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 28, 0, 28) });
                if (browser.ShowsMarketplaceHelp)
                    rows.Children.Add(new HyperlinkButton { Content = PluginStrings.MarketplaceHelpLink, NavigateUri = new Uri("https://code.claude.com/docs/en/discover-plugins#add-marketplaces"), HorizontalAlignment = HorizontalAlignment.Center });
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
                snapshot = smokePluginRead is { } fixture
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
            body.Children.Add(new TextBlock { Text = PluginStrings.FooterNote, FontSize = 10, Opacity = .65, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new ScrollViewer { Content = rows, Height = 380, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled });
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

        dialogOpen = true;
        try
        {
            if (smokePluginDialog is { } driver) await driver(new PluginSmokeSurface(dialog, browser, LoadPluginsAsync, SelectPluginTab));
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
            Require(outcome.MutatingControls == 0, "플러그인 창에 설치·범위·새로고침 컨트롤이 있습니다.");
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

    private static FrameworkElement PluginRow(string provider, ClaudePluginRow row)
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
}
