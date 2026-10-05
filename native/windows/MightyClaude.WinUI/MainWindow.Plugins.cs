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

    // The running app owns this from its first moment — a field of the window,
    // built with the real shared runner, never by the smoke harness. Every
    // plugin read is made through its runner and both mutations are started
    // through it, so install and marketplace refresh really run in the app.
    private readonly PluginOperations pluginOperations = new();

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
        // One sheet at a time: while Settings or another sheet is open this one waits to be asked again.
        if (dialogOpen) return;
        if (service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == service.Snapshot.ActiveWorkspaceId) is not { } workspace) return;
        await ShowPluginBrowser(provider, workspace);
    });

    private async Task<ClaudePluginBrowser> ShowPluginBrowser(string provider, Workspace workspace)
    {
        var browser = new ClaudePluginBrowser(provider, workspace);
        // The app's own operations object carries the shared runner: 8 MiB of
        // listing output, the macOS cap. Anything past it is refused by the
        // parser rather than truncated into a short list. The provider picks
        // the reader; both answer with the same ClaudePluginSnapshot.
        var operations = pluginOperations;
        var runner = operations.Runner;
        IPluginReader reader = smokeReaderFactory is { } factory
            ? factory(provider)
            : provider == ClaudePluginBrowser.CodexProvider
                ? new CodexPluginReader(runner)
                : (IPluginReader)new ClaudePluginReader(runner);

        // The window the Mac's way (M/ClaudePluginView.swift:184-303): 760×620 with padding 20 — the heading, the
        // tabs beside the reload button, the search box beside the marketplace filter, on the marketplace tab the
        // scope beside the marketplace refresh, what the read and the last operation said, the note, a rule and
        // the rows; then the progress, the cancel and the close buttons on the last line.
        var rows = new StackPanel { Spacing = 10 };
        var status = SettingsText("", 11, DesignToken.WaitText, selectable: true); status.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(status, PluginAutomationId(provider, "load-status"));
        // The command's own output, 80 high behind its disclosure (M/ClaudePluginView.swift:277-282).
        var diagnostics = SettingsText("", 10, mono: true, selectable: true);
        var diagnosticsBox = new ScrollViewer { Content = diagnostics, Height = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Visibility = Visibility.Collapsed };
        var diagnosticsMark = new FontIcon { Glyph = PluginFoldedGlyph, FontSize = 8, VerticalAlignment = VerticalAlignment.Center };
        var diagnosticsToggle = Button(PluginStrings.DiagnosticsDisclosure, () =>
        {
            var open = diagnosticsBox.Visibility != Visibility.Visible;
            diagnosticsBox.Visibility = open ? Visibility.Visible : Visibility.Collapsed; diagnosticsMark.Glyph = open ? PluginUnfoldedGlyph : PluginFoldedGlyph;
            return Task.CompletedTask;
        });
        var diagnosticsLabel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        diagnosticsLabel.Children.Add(diagnosticsMark); diagnosticsLabel.Children.Add(new TextBlock { Text = PluginStrings.DiagnosticsDisclosure, FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
        diagnosticsToggle.Content = diagnosticsLabel; diagnosticsToggle.Padding = new Thickness(0); diagnosticsToggle.MinWidth = 0; diagnosticsToggle.MinHeight = 0;
        diagnosticsToggle.BorderThickness = new Thickness(0); diagnosticsToggle.HorizontalAlignment = HorizontalAlignment.Left;
        PaintPlainButton(diagnosticsToggle, brushes.Transparent, brushes.Transparent, ink: brushes.Brush(DesignToken.Ink));
        diagnosticsToggle.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(diagnosticsToggle, PluginAutomationId(provider, "diagnostics"));

        var version = SettingsText("", 10, DesignToken.Ink2, mono: true); version.VerticalAlignment = VerticalAlignment.Top;
        var progress = SettingsText(PluginStrings.ProgressLoading, 11, DesignToken.Ink2); progress.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(progress, PluginAutomationId(provider, "progress"));

        // The controls are built first and their handlers attached below, after
        // every local the handlers read has been assigned.
        var installedTab = PluginTab(); var marketplaceTab = PluginTab();
        AutomationProperties.SetAutomationId(installedTab, PluginAutomationId(provider, "tab-installed"));
        AutomationProperties.SetAutomationId(marketplaceTab, PluginAutomationId(provider, "tab-marketplace"));
        AutomationProperties.SetName(installedTab, PluginStrings.TabInstalled);
        AutomationProperties.SetName(marketplaceTab, PluginStrings.TabMarketplace);

        // The search box (M/ClaudePluginView.swift:227-231): a plain field after the magnifier, on the subtle wash.
        var search = new TextBox { PlaceholderText = PluginStrings.SearchPlaceholder, FontSize = DesignMetrics.Type.Title, BorderThickness = new Thickness(0), Padding = new Thickness(0), MinHeight = 0, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        var plain = new List<(string Key, object Value)>();
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled" }) plain.Add((key, brushes.Transparent));
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" }) plain.Add((key, brushes.Brush(DesignToken.Ink)));
        foreach (var key in new[] { "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundFocused" }) plain.Add((key, brushes.Tertiary));
        plain.Add(("TextControlBorderThemeThicknessFocused", new Thickness(0)));
        SetResourcesOnce(search, plain);
        // AppKit's placeholder is the tertiary ink; the template takes it from the property in every state (BuildSidebarSearch).
        search.PlaceholderForeground = brushes.Tertiary;
        AutomationProperties.SetAutomationId(search, PluginAutomationId(provider, "search"));
        AutomationProperties.SetName(search, PluginStrings.SearchPlaceholder);
        var filter = SettingsPopup(new ComboBox(), bordered: true);
        AutomationProperties.SetAutomationId(filter, PluginAutomationId(provider, "marketplace-filter"));
        AutomationProperties.SetName(filter, PluginStrings.TabMarketplace);

        var reload = SettingsPush(new Button { Content = SettingsGlyphLabel(PluginReloadGlyph, PluginStrings.ButtonReload, DesignMetrics.Type.Title) });
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
        var scopePicker = SettingsPopup(new ComboBox(), bordered: true);
        AutomationProperties.SetAutomationId(scopePicker, PluginAutomationId(provider, "scope-picker"));
        AutomationProperties.SetName(scopePicker, PluginStrings.ScopePickerLabel);
        foreach (var opt in browser.ScopeOptions)
            scopePicker.Items.Add(new ComboBoxItem { Content = opt.Label, Tag = opt.Value });
        var pickerNote = SettingsText("", 10, DesignToken.Ink2);
        AutomationProperties.SetAutomationId(pickerNote, PluginAutomationId(provider, "picker-note"));

        // The marketplace refresh button and the Codex Git-only note.
        var refreshBtn = SettingsPush(new Button { Content = PluginStrings.ButtonMarketplaceRefresh });
        AutomationProperties.SetAutomationId(refreshBtn, PluginAutomationId(provider, "refresh-marketplaces"));
        var marketplaceUnavailable = SettingsText("", 10, DesignToken.Ink2); marketplaceUnavailable.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(marketplaceUnavailable, PluginAutomationId(provider, "marketplace-unavailable"));

        // Progress label, cancel button and result text for running operations.
        var operationProgress = SettingsText("", 11, DesignToken.Ink2); operationProgress.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(operationProgress, PluginAutomationId(provider, "operation-progress"));
        var cancelBtn = SettingsPush(new Button());
        AutomationProperties.SetAutomationId(cancelBtn, PluginAutomationId(provider, "cancel-operation"));
        cancelBtn.Click += (_, _) => browser.RequestCancel();
        cancelBtn.Visibility = Visibility.Collapsed;
        // The small ring that turns while the list is read or an operation runs (M/ClaudePluginView.swift:198-202).
        var working = new ProgressRing { Width = 16, Height = 16, MinWidth = 0, MinHeight = 0, IsActive = false, Visibility = Visibility.Collapsed, Foreground = brushes.Brush(DesignToken.Ink2), VerticalAlignment = VerticalAlignment.Center };
        // What the last operation said (M/ClaudePluginView.swift:270-276): its mark and sentence, doneText when it succeeded and waitText otherwise.
        var operationMark = new FontIcon { FontSize = 11, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        var operationResult = SettingsText("", 11, selectable: true);
        AutomationProperties.SetAutomationId(operationResult, PluginAutomationId(provider, "operation-result"));
        var resultRow = new Grid { ColumnSpacing = 5, Visibility = Visibility.Collapsed };
        resultRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); resultRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        resultRow.Children.Add(operationMark); Grid.SetColumn(operationResult, 1); resultRow.Children.Add(operationResult);
        void ShowResult(string? text, bool succeeded)
        {
            operationResult.Text = text ?? ""; resultRow.Visibility = text is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
            var ink = brushes.Brush(succeeded ? DesignToken.DoneText : DesignToken.WaitText);
            operationResult.Foreground = ink; operationMark.Foreground = ink; operationMark.Glyph = succeeded ? PluginDoneGlyph : PluginNoteGlyph;
        }

        // The scope beside the marketplace refresh (M/ClaudePluginView.swift:239-252): its label and pop-up at the
        // leading edge, the button at the trailing one, 12 apart.
        var pickerRow = new Grid { ColumnSpacing = 12 };
        pickerRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); pickerRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var scopeLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        scopePicker.HorizontalAlignment = HorizontalAlignment.Left;
        scopeLine.Children.Add(SettingsText(PluginStrings.ScopePickerLabel)); scopeLine.Children.Add(scopePicker);
        pickerRow.Children.Add(scopeLine); Grid.SetColumn(refreshBtn, 1); pickerRow.Children.Add(refreshBtn);

        // The close button of the last line. It waits while an operation runs, as on the Mac.
        ContentDialog? sheet = null;
        var close = SettingsPush(Button(PluginStrings.ButtonClose, () => { sheet?.Hide(); return Task.CompletedTask; }));
        AutomationProperties.SetAutomationId(close, PluginAutomationId(provider, "close"));

        // Wraps an install or refresh call: starts the operation (Phase set
        // synchronously), redraws to show progress, awaits, redraws the result.
        async Task<ClaudePluginOperationResult> OperateAsync(Func<Task<ClaudePluginOperationResult>> op)
        {
            if (PluginMutationBlockReason(provider, workspace) is { } block)
            { ShowResult(block, false); return new(ClaudePluginStatus.Skipped, block); }
            var task = op();
            NotifyAutomaticUpdates(); RenderPlugins();
            try { var result = await task; if (!closing) RenderPlugins(); return result; }
            finally { if (!closing) { NotifyAutomaticUpdates(); TrackLoginTask(ResendLoginRequestsAfterUpdate()); } }
        }

        void RenderPlugins()
        {
            // A tab's words are semibold on the accent tint when it is chosen, regular on the subtle wash otherwise.
            foreach (var (tab, value) in new[] { (installedTab, ClaudePluginBrowser.InstalledTab), (marketplaceTab, ClaudePluginBrowser.MarketplaceTab) })
            {
                var chosen = browser.Tab == value; var chip = (Border)tab.Content; var words = (TextBlock)chip.Child;
                words.Text = browser.TabLabel(value); words.FontWeight = chosen ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
                chip.Background = chosen ? brushes.Brush(DesignToken.Accent, PluginTabTint) : brushes.Subtle;
            }
            var onMarketplace = browser.Tab == ClaudePluginBrowser.MarketplaceTab;
            version.Text = browser.Snapshot?.CliVersion ?? "";
            reload.IsEnabled = !browser.Loading;
            status.Text = browser.StatusText ?? "";
            status.Visibility = browser.StatusText is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
            var output = browser.Snapshot?.DiagnosticOutput ?? "";
            // The sentence is quiet while the list is good and nothing went wrong on the way to it.
            status.Foreground = brushes.Brush(browser.IsReady && output.Length == 0 ? DesignToken.Ink2 : DesignToken.WaitText);
            diagnostics.Text = output;
            diagnosticsToggle.Visibility = output.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (output.Length == 0) { diagnosticsBox.Visibility = Visibility.Collapsed; diagnosticsMark.Glyph = PluginFoldedGlyph; }
            RenderFilter();

            // Scope picker: keep the selection stable across redraws. The scope and the marketplace refresh belong to the marketplace tab.
            var scopeIdx = browser.ScopeOptions.ToList().FindIndex(o => o.Value == browser.Scope);
            scopePicker.SelectedIndex = Math.Max(0, scopeIdx);
            pickerNote.Text = browser.ScopeNote;
            pickerRow.Visibility = pickerNote.Visibility = onMarketplace ? Visibility.Visible : Visibility.Collapsed;

            // Refresh button: enabled when there is at least one refreshable marketplace.
            refreshBtn.IsEnabled = browser.CanRefreshMarketplaces;
            marketplaceUnavailable.Text = browser.RefreshUnavailableNote ?? "";
            marketplaceUnavailable.Visibility = onMarketplace && browser.RefreshUnavailableNote is { Length: > 0 }
                ? Visibility.Visible : Visibility.Collapsed;

            // Progress / cancel / result.
            operationProgress.Text = browser.ProgressLabel ?? "";
            cancelBtn.Content = browser.CancelLabel;
            cancelBtn.IsEnabled = browser.CanCancel;
            operationProgress.Visibility = cancelBtn.Visibility = browser.IsMutating ? Visibility.Visible : Visibility.Collapsed;
            progress.Visibility = browser.Loading && !browser.IsMutating ? Visibility.Visible : Visibility.Collapsed;
            working.IsActive = browser.Loading || browser.IsMutating; working.Visibility = working.IsActive ? Visibility.Visible : Visibility.Collapsed;
            // macOS keeps Close disabled while an operation runs.
            close.IsEnabled = browser.CanClose;
            ShowResult(browser.ResultText, browser.LastResult?.Status == ClaudePluginStatus.Succeeded);

            // Rows: catalog tab gets an install button per row.
            rows.Children.Clear();
            if (onMarketplace)
            {
                foreach (var row in browser.Rows())
                {
                    var btnLabel = browser.InstallButtonLabel(row.Id);
                    var installBtn = SettingsPush(new Button { Content = btnLabel, IsEnabled = browser.CanInstall(row.Id) });
                    AutomationProperties.SetAutomationId(installBtn, PluginAutomationId(provider, "install-" + row.Id));
                    var capturedId = row.Id;
                    installBtn.Click += async (_, _) => await OperateAsync(() => operations.InstallAsync(browser, reader, capturedId));
                    rows.Children.Add(PluginRowPanel(provider, row, installBtn));
                }
            }
            else
            {
                foreach (var row in browser.Rows()) rows.Children.Add(PluginRow(provider, row));
            }
            if (rows.Children.Count == 0)
            {
                // M/ClaudePluginView.swift:349-362: the sentence, and under it the way to add a marketplace, centred with 28 above and below.
                var empty = new StackPanel { Spacing = 10, Margin = new Thickness(0, 28, 0, 28) };
                var message = SettingsText(browser.EmptyMessage, 12, DesignToken.Ink2); message.HorizontalAlignment = HorizontalAlignment.Center; message.TextAlignment = TextAlignment.Center;
                empty.Children.Add(message);
                if (browser.ShowsMarketplaceHelpLink)
                    empty.Children.Add(new HyperlinkButton { Content = PluginStrings.MarketplaceHelpLink, NavigateUri = new Uri("https://code.claude.com/docs/en/discover-plugins#add-marketplaces"), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11, Padding = new Thickness(0), MinHeight = 0 });
                if (browser.MarketplaceHelpText is { Length: > 0 } help)
                {
                    var sentence = SettingsText(help, 11, DesignToken.Ink2); sentence.HorizontalAlignment = HorizontalAlignment.Center; sentence.TextAlignment = TextAlignment.Center;
                    AutomationProperties.SetAutomationId(sentence, PluginAutomationId(provider, "marketplace-help"));
                    empty.Children.Add(sentence);
                }
                rows.Children.Add(empty);
            }
        }

        async Task LoadPluginsAsync()
        {
            if (browser.Loading) return;
            browser.BeginLoad();
            RenderPlugins();
            ClaudePluginSnapshot snapshot;
            try
            {
                var smokeRead = provider == ClaudePluginBrowser.CodexProvider ? smokeCodexPluginRead : smokePluginRead;
                snapshot = AnyCliUpdateRunning ? new ClaudePluginSnapshot { Status = ClaudePluginStatus.Busy, Detail = Locale.Get("loginRecovery.updating") }
                    : smokeRead is { } fixture
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
        refreshBtn.Click += async (_, _) => await OperateAsync(() => operations.RefreshMarketplacesAsync(browser, reader));
        search.RegisterPropertyChangedCallback(TextBox.TextProperty, (_, _) => { browser.Search = search.Text; RenderPlugins(); });
        filter.SelectionChanged += (_, _) =>
        {
            if (rebuildingFilter) return;
            var wanted = filter.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "";
            if (browser.MarketplaceFilter == wanted) return;
            browser.MarketplaceFilter = wanted;
            RenderPlugins();
        };

        // The heading (M/ClaudePluginView.swift:186-195): the puzzle piece in accent, the title (18 semibold) over the
        // workspace's name and its path (10pt mono in the tertiary ink, :191), and the CLI's version at the trailing edge.
        var title = new TextBlock { Text = browser.Title, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = brushes.Brush(DesignToken.Ink), LineHeight = 22, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        AutomationProperties.SetAutomationId(title, PluginAutomationId(provider, "title"));
        var workspaceName = SettingsText(workspace.Name, 12, DesignToken.Ink2); var workspacePath = SettingsTertiary(workspace.Path, mono: true);
        foreach (var line in new[] { workspaceName, workspacePath }) { line.TextWrapping = TextWrapping.NoWrap; line.TextTrimming = TextTrimming.CharacterEllipsis; }
        ToolTipService.SetToolTip(workspacePath, workspace.Path);
        var named = new StackPanel { Spacing = 4 };
        named.Children.Add(title); named.Children.Add(workspaceName); named.Children.Add(workspacePath);
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var piece = new FontIcon { Glyph = PluginGlyph, FontSize = 25, Foreground = brushes.Brush(DesignToken.Accent), VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetAccessibilityView(piece, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        header.Children.Add(piece); Grid.SetColumn(named, 1); header.Children.Add(named); Grid.SetColumn(version, 2); header.Children.Add(version);

        // The tabs, 6 apart, and the reload button at the trailing edge.
        var tabs = new Grid();
        tabs.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); tabs.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var tabLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        tabLine.Children.Add(installedTab); tabLine.Children.Add(marketplaceTab);
        tabs.Children.Add(tabLine); Grid.SetColumn(reload, 1); tabs.Children.Add(reload);

        // The search box takes what the 230-wide marketplace filter leaves (M/ClaudePluginView.swift:226-237).
        var searchLine = new Grid { ColumnSpacing = 7 };
        searchLine.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); searchLine.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        searchLine.Children.Add(SettingsSymbol(PluginSearchGlyph, 12, DesignToken.Ink2)); Grid.SetColumn(search, 1); searchLine.Children.Add(search);
        var searchBox = new Border { Child = searchLine, Padding = new Thickness(9), CornerRadius = new CornerRadius(DesignMetrics.Radius.Row), Background = brushes.Subtle };
        var filterLine = new Grid { ColumnSpacing = 8, Width = PluginFilterWidth, VerticalAlignment = VerticalAlignment.Center };
        filterLine.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); filterLine.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        filter.HorizontalAlignment = HorizontalAlignment.Stretch;
        filterLine.Children.Add(SettingsText(PluginStrings.TabMarketplace)); Grid.SetColumn(filter, 1); filterLine.Children.Add(filter);
        var filters = new Grid { ColumnSpacing = 10 };
        filters.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); filters.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        filters.Children.Add(searchBox); Grid.SetColumn(filterLine, 1); filters.Children.Add(filterLine);

        // Everything above the rows, 12 apart; what does not apply is collapsed and leaves no gap.
        var controls = new StackPanel { Spacing = 12 };
        foreach (var part in new FrameworkElement[] { tabs, filters, pickerRow, pickerNote, marketplaceUnavailable, status, resultRow, diagnosticsToggle, diagnosticsBox, SettingsText(browser.FooterNote, 10, DesignToken.Ink2) }) controls.Children.Add(part);
        var list = new Grid { RowSpacing = 12 };
        list.RowDefinitions.Add(new() { Height = GridLength.Auto }); list.RowDefinitions.Add(new() { Height = GridLength.Auto }); list.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        list.Children.Add(controls);
        var rule = new Border { Height = DesignMetrics.Stroke.Line, Background = brushes.Brush(DesignToken.Line) };
        Grid.SetRow(rule, 1); list.Children.Add(rule);
        // The rows keep 16 clear at the trailing edge for the scroller (M/ClaudePluginView.swift:299).
        rows.Padding = new Thickness(0, 0, 16, 0);
        var scroller = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
        Grid.SetRow(scroller, 2); list.Children.Add(scroller);

        // The last line (M/ClaudePluginView.swift:197-210): the ring and what is being done, then the cancel and close buttons.
        var footer = new Grid { ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var doing = new Grid(); doing.Children.Add(progress); doing.Children.Add(operationProgress);
        footer.Children.Add(working); Grid.SetColumn(doing, 1); footer.Children.Add(doing);
        Grid.SetColumn(cancelBtn, 2); footer.Children.Add(cancelBtn); Grid.SetColumn(close, 3); footer.Children.Add(close);

        var body = new Grid { RowSpacing = 14 };
        body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.Children.Add(header); Grid.SetRow(list, 1); body.Children.Add(list); Grid.SetRow(footer, 2); body.Children.Add(footer);
        RenderPlugins();

        // The sheet has no title or buttons of its own: the heading and the last line are its content, as on the Mac.
        var dialog = StyledDialog(new ContentDialog
        {
            Content = body,
            XamlRoot = root.XamlRoot,
        }, PluginSheetWidth, PluginSheetHeight);
        sheet = dialog;
        AutomationProperties.SetAutomationId(dialog, PluginAutomationId(provider, "browser"));
        AutomationProperties.SetName(dialog, browser.Title);
        dialog.Opened += (_, _) => _ = LoadPluginsAsync();
        // macOS keeps Close disabled while an operation runs; Esc closes a
        // ContentDialog whatever its buttons say, so the close itself is refused.
        dialog.Closing += (_, args) => { if (!browser.CanClose) args.Cancel = true; };

        dialogOpen = true;
        try
        {
            var smokeDialog = provider == ClaudePluginBrowser.CodexProvider ? smokeCodexPluginDialog : smokePluginDialog;
            if (smokeDialog is { } driver)
                await driver(new PluginSmokeSurface(dialog, browser, LoadPluginsAsync, SelectPluginTab,
                    id => OperateAsync(() => operations.InstallAsync(browser, reader, id)),
                    () => OperateAsync(() => operations.RefreshMarketplacesAsync(browser, reader)),
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

    /// <summary>The plugin sheet's size and its marketplace filter's width (M/ClaudePluginView.swift:212, 236).</summary>
    internal const double PluginSheetWidth = 760, PluginSheetHeight = 620, PluginFilterWidth = 230;
    /// <summary>The chosen tab's tint: accent at 0.17 (M/ClaudePluginView.swift:309).</summary>
    private const double PluginTabTint = 0.17;
    /// <summary>The Mac's symbols in Segoe Fluent Icons: puzzlepiece.extension, arrow.clockwise, magnifyingglass, the disclosure's chevrons, checkmark.circle.fill and info.circle.</summary>
    private const string PluginGlyph = "", PluginReloadGlyph = "", PluginSearchGlyph = "", PluginFoldedGlyph = "", PluginUnfoldedGlyph = "", PluginDoneGlyph = "", PluginNoteGlyph = "";

    /// <summary>
    /// A tab of the plugin sheet (M/ClaudePluginView.swift:305-311): a plain button around a radius-7 chip
    /// with padding h12 v7 and 12pt words. The words, their weight and the chip's fill are drawn as the
    /// sheet is rendered, on the content.
    /// </summary>
    private Button PluginTab()
    {
        var chip = new Border { Child = new TextBlock { FontSize = 12, Foreground = brushes.Brush(DesignToken.Ink) }, Padding = new Thickness(12, 7, 12, 7), CornerRadius = new CornerRadius(7) };
        var tab = new Button { Content = chip, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(7) };
        PaintPlainButton(tab, brushes.Transparent, brushes.Transparent);
        return tab;
    }

    /// <summary>The words a tab shows, read off its chip.</summary>
    private static string PluginTabLabel(Button tab) => ((TextBlock)((Border)tab.Content).Child).Text;

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
            case Border border:
                if (border.Child is { } boxed)
                {
                    yield return boxed;
                    foreach (var nested in PluginDescendants(boxed)) yield return nested;
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
    // again, and the macOS sentence a missing CLI produces. No claude process
    // starts and no workspace changes.
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
                // The title stands in the sheet's own heading, as on the Mac.
                title = PluginControl<TextBlock>(dialog, "claude", "title").Text;
                // The Opened handler's own action: the first read.
                await surface.Load();
                Require(surface.Browser.IsReady, "the plugin list did not load from the fixture");
                installedTab = PluginTabLabel(PluginControl<Button>(dialog, "claude", "tab-installed"));
                marketplaceTab = PluginTabLabel(PluginControl<Button>(dialog, "claude", "tab-marketplace"));
                installedRows = PluginRowCount(dialog, "claude");
                installedSubtitle = surface.Browser.InstalledRows()[0].Subtitle;

                // The marketplace tab button's own action.
                await surface.SelectTab(ClaudePluginBrowser.MarketplaceTab);
                availableRows = PluginRowCount(dialog, "claude");
                availableSubtitle = surface.Browser.AvailableRows()[0].Subtitle;

                // The real filter: selecting a marketplace raises SelectionChanged.
                var filter = PluginControl<ComboBox>(dialog, "claude", "marketplace-filter");
                Require(filter.Items.Count == 3 && (string)((ComboBoxItem)filter.Items[0]).Content! == PluginStrings.FilterAll,
                    "the marketplace filter must show All and the registered marketplaces");
                filter.SelectedIndex = filter.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == "other");
                filteredRows = PluginRowCount(dialog, "claude");

                // The real search box: its Text change raises the registered callback.
                var search = PluginControl<TextBox>(dialog, "claude", "search");
                Require(search.PlaceholderText == PluginStrings.SearchPlaceholder, "the search box placeholder differs");
                search.Text = "no-such-plugin-zz";
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
                    "the plugin window must have no dialog button that changes the list");
                // The close button is the last line's own (M/ClaudePluginView.swift:208), and the sheet has no button of its own.
                Require(dialog.CloseButtonText is null or "" && (string)PluginControl<Button>(dialog, "claude", "close").Content! == PluginStrings.ButtonClose, "the close button text differs");
                // The reload button's words stand after its symbol.
                Require(((StackPanel)PluginControl<Button>(dialog, "claude", "reload").Content).Children.OfType<TextBlock>().Single().Text == PluginStrings.ButtonReload,
                    "the reload button text differs");

                // The reload button's own action, answered by a missing CLI.
                await surface.Load();
                var status = PluginControl<TextBlock>(dialog, "claude", "load-status");
                reloadedStatus = status.Text;
                Require(status.Visibility == Visibility.Visible, "the reason the CLI was not found is not shown");
            };
            await ShowPluginBrowser("claude", workspace);

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
                Reads = reads,
                MutatingControls = mutating,
                Restored = true,
            };
            Require(outcome.Title == PluginStrings.TitleTemplate.Replace("{provider}", "Claude") && outcome.InstalledTab == PluginStrings.TabCountTemplate.Replace("{title}", PluginStrings.TabInstalled).Replace("{count}", "2") && outcome.MarketplaceTab == PluginStrings.TabCountTemplate.Replace("{title}", PluginStrings.TabMarketplace).Replace("{count}", "3"),
                "the plugin window title or tab counts differ from macOS");
            Require(outcome.InstalledRows == 2 && outcome.AvailableRows == 3 && outcome.FilteredRows == 1 && outcome.SearchedRows == 0,
                "tabs, filter and search did not narrow the list as on macOS");
            // scope-picker + refresh-marketplaces + 3 install-* buttons = 5
            Require(outcome.MutatingControls == 5, "unexpected number of changing controls on the Claude marketplace tab: " + outcome.MutatingControls);
            Require(outcome.ReloadedFromStatus == PluginStrings.DetailMissingCli, "the missing-CLI sentence differs from macOS");
            Require(outcome.Reads == 2, "wrong number of list reads: " + outcome.Reads);

            // The same sheet once more, shown for real with the fixture's rows: its size and heading are the
            // Mac's (M/ClaudePluginView.swift:186-212), and both tabs are captured to be read next to that file.
            smokePluginRead = _ => Task.FromResult(PluginSmokeSnapshot);
            smokePluginDialog = async surface =>
            {
                var dialog = surface.Dialog; var theme = SmokeTheme;
                // The Mac's size, or what a smaller window leaves it (a display scaled past 125% makes this window that small).
                var fits = root.XamlRoot.Size;
                Require(OwnResource(dialog, "ContentDialogMinWidth") is double width && width == SheetFit(PluginSheetWidth, fits.Width) && OwnResource(dialog, "ContentDialogMaxHeight") is double height && height == SheetFit(PluginSheetHeight, fits.Height),
                    $"the plugin sheet must be {PluginSheetWidth}×{PluginSheetHeight}, or as much of that as the {fits.Width}×{fits.Height} window leaves; got {OwnResource(dialog, "ContentDialogMinWidth")}×{OwnResource(dialog, "ContentDialogMaxHeight")}");
                var heading = PluginControl<TextBlock>(dialog, "claude", "title");
                Require(heading.FontSize == 18 && heading.FontWeight.Weight == Microsoft.UI.Text.FontWeights.SemiBold.Weight, $"the plugin sheet's title must be 18pt semibold; got {heading.FontSize} at {heading.FontWeight.Weight}");
                var showing = dialog.ShowAsync();
                try
                {
                    await WaitUI(() => dialog.IsLoaded && surface.Browser.IsReady && !surface.Browser.Loading && PluginRowCount(dialog, "claude") == 2, () => "the plugin sheet never showed the fixture's installed rows");
                    await SettleDesktopCapture(dialog);
                    await CaptureElement(dialog, Path.Combine(options.ProfileDirectory!, "smoke-plugins-installed-" + theme + ".png"));
                    await surface.SelectTab(ClaudePluginBrowser.MarketplaceTab);
                    await SettleDesktopCapture(dialog);
                    await CaptureElement(dialog, Path.Combine(options.ProfileDirectory!, "smoke-plugins-marketplace-" + theme + ".png"));
                }
                finally { dialog.Hide(); await showing; }
            };
            await ShowPluginBrowser("claude", workspace);
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
                title = PluginControl<TextBlock>(dialog, "codex", "title").Text;
                await surface.Load();
                Require(surface.Browser.IsReady, "the Codex plugin list did not load from the fixture");
                installedTab = PluginTabLabel(PluginControl<Button>(dialog, "codex", "tab-installed"));
                marketplaceTab = PluginTabLabel(PluginControl<Button>(dialog, "codex", "tab-marketplace"));
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
                search.Text = "no-such-plugin-zz";
                searchedRows = PluginRowCount(dialog, "codex");
                search.Text = "";
                filter.SelectedIndex = 0;

                // Looking must offer nothing that would change anything.
                mutating = PluginDescendants(dialog.Content).OfType<FrameworkElement>()
                    .Count(e => AutomationProperties.GetAutomationId(e) is { Length: > 0 } id
                        && ClaudePluginSupport.NamesAChange(id, "codex"));
                Require(dialog.PrimaryButtonText is null or "" && dialog.SecondaryButtonText is null or "",
                    "the Codex plugin window must have no dialog button that changes the list");

                // The reload button's own action, answered by an empty registry.
                await surface.Load();
                noMarketplaceHelp = PluginControl<TextBlock>(dialog, "codex", "marketplace-help").Text;

                // And again, answered by a CLI without the JSON plugin commands.
                await surface.Load();
                var status = PluginControl<TextBlock>(dialog, "codex", "load-status");
                unsupportedStatus = status.Text;
                Require(status.Visibility == Visibility.Visible, "the reason the Codex CLI is unsupported is not shown");
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
            Require(outcome.Title == PluginStrings.TitleTemplate.Replace("{provider}", "Codex") && outcome.InstalledTab == PluginStrings.TabCountTemplate.Replace("{title}", PluginStrings.TabInstalled).Replace("{count}", "1") && outcome.MarketplaceTab == PluginStrings.TabCountTemplate.Replace("{title}", PluginStrings.TabMarketplace).Replace("{count}", "2"),
                "the Codex plugin window title or tab counts differ from macOS");
            Require(outcome.InstalledRows == 1 && outcome.InstalledSubtitle == PluginStrings.SubtitleTemplate.Replace("{left}", "sample").Replace("{right}", PluginStrings.ScopeUser),
                "the Codex installed list must be one user-scope row: " + outcome.InstalledRows + " / " + outcome.InstalledSubtitle);
            Require(outcome.AvailableRows == 2 && outcome.FilteredRows == 1 && outcome.SearchedRows == 0,
                "tabs, filter and search did not narrow the Codex list as on macOS");
            // scope-picker + refresh-marketplaces + 2 install-* buttons = 4
            Require(outcome.MutatingControls == 4, "unexpected number of changing controls on the Codex marketplace tab: " + outcome.MutatingControls);
            Require(outcome.FooterNote == CodexPluginStrings.FooterNote, "the Codex window footer differs");
            Require(outcome.ReadyStatus == CodexPluginStrings.DetailReady, "Codex must keep showing the ready sentence after reading the list");
            Require(outcome.NoMarketplaceHelp == CodexPluginStrings.MarketplaceHelp, "the no-marketplace sentence differs from macOS");
            Require(outcome.UnsupportedStatus == CodexPluginStrings.DetailUnsupported, "the unsupported-CLI sentence differs from macOS");
            Require(outcome.Reads == 3, "wrong number of Codex list reads: " + outcome.Reads);
            return outcome;
        }
        finally
        {
            smokeCodexPluginRead = beforeRead;
            smokeCodexPluginDialog = beforeDialog;
            Render();
        }
    }

    /// <summary>
    /// A plugin as a row on the subtle wash at radius 9 with padding 12 (M/ClaudePluginView.swift:312-348).
    /// An installed one: its name (13 semibold) and version (10 mono) against its state word at the trailing
    /// edge, the description, where it comes from, its project path, its errors in <c>waitText</c> and its
    /// notes, 7 apart. A catalog one: the same words 6 apart, with <paramref name="install"/> at the top of
    /// the trailing edge, 14 from them.
    /// </summary>
    private Grid PluginRowPanel(string provider, ClaudePluginRow row, Button? install = null)
    {
        var panel = new Grid { ColumnSpacing = 14, Padding = new Thickness(12), CornerRadius = new CornerRadius(9), Background = brushes.Subtle };
        panel.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); panel.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        AutomationProperties.SetAutomationId(panel, PluginAutomationId(provider, "row-" + row.Id));
        var words = new StackPanel { Spacing = install is null ? 7 : 6 };
        var title = new Grid { ColumnSpacing = 8 };
        title.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); title.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); title.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var name = SettingsText(row.Name, 13); name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; name.MaxLines = 2; name.TextTrimming = TextTrimming.CharacterEllipsis;
        title.Children.Add(name);
        if (row.Version is { Length: > 0 })
        {
            var version = SettingsText(row.Version, 10, DesignToken.Ink2, mono: true); Grid.SetColumn(version, 1); title.Children.Add(version);
        }
        if (row.State is { Length: > 0 })
        {
            // The state word: doneText while the plugin is on (M/ClaudePluginView.swift:318-319).
            var state = SettingsText(row.State, 10, row.State == PluginStrings.StateEnabled ? DesignToken.DoneText : DesignToken.Ink2, medium: true);
            state.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(state, 2); title.Children.Add(state);
        }
        words.Children.Add(title);
        if (row.Description.Length > 0)
        {
            var description = SettingsText(row.Description, 11, DesignToken.Ink2); description.MaxLines = 3; description.TextTrimming = TextTrimming.CharacterEllipsis;
            words.Children.Add(description);
        }
        // Where it comes from: ink2 on an installed row, the tertiary ink on a catalog one (M/ClaudePluginView.swift:322, 340).
        words.Children.Add(install is null ? SettingsText(row.Subtitle, 10, DesignToken.Ink2) : SettingsTertiary(row.Subtitle));
        if (row.ProjectPath is { Length: > 0 })
        {
            // The project's path: 10pt mono in the tertiary ink (M/ClaudePluginView.swift:323).
            var path = SettingsTertiary(row.ProjectPath, mono: true); path.TextWrapping = TextWrapping.NoWrap; path.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTipService.SetToolTip(path, row.ProjectPath); words.Children.Add(path);
        }
        foreach (var error in row.Errors) words.Children.Add(SettingsText(error, 10, DesignToken.WaitText));
        foreach (var note in row.Notes) words.Children.Add(SettingsText(note, 10, DesignToken.Ink2));
        panel.Children.Add(words);
        if (install is not null) { install.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(install, 1); panel.Children.Add(install); }
        return panel;
    }

    private FrameworkElement PluginRow(string provider, ClaudePluginRow row) => PluginRowPanel(provider, row);

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
                Require(surface.Browser.IsReady, "the plugin list did not load from the fixture");
                claudeScopes = surface.Browser.ScopeOptions.Count;
                Require(claudeScopes == 3, "Claude must offer 3 install scopes: " + claudeScopes);
                Require(surface.Browser.ScopeOptions[0].Value == "local" && surface.Browser.ScopeOptions[1].Value == "project" && surface.Browser.ScopeOptions[2].Value == "user",
                    "the Claude scopes must be local, project and user");

                await surface.SelectTab(ClaudePluginBrowser.MarketplaceTab);
                // docs@sample is in the catalog and not installed: can be installed.
                Require(surface.Browser.CanInstall("docs@sample"), "the docs@sample install button is not enabled");

                var install = await surface.Install("docs@sample");
                installResult = install.Detail;
                Require(installResult == PluginStrings.InstallSucceeded,
                    "the install result sentence differs from macOS: " + installResult);

                var refresh = await surface.Refresh();
                refreshResult = refresh.Detail;
                Require(refresh.Status == ClaudePluginStatus.Succeeded,
                    "the marketplace refresh failed: " + refreshResult);
            };
            await ShowPluginBrowser("claude", workspace);

            // --- Claude: the real cancel button stops a running install ---
            smokeReaderFactory = _ => new FakeMarketplaceReader(PluginSmokeSnapshot, holdUntilCancelled: true);
            smokePluginDialog = async surface =>
            {
                await surface.Load();
                await surface.SelectTab(ClaudePluginBrowser.MarketplaceTab);
                var pending = surface.Install("docs@sample");
                Require(surface.Browser.IsMutating, "the install started but the window is not in progress");
                Require(surface.Browser.ProgressLabel == PluginStrings.ProgressInstalling,
                    "the install progress sentence differs from macOS: " + surface.Browser.ProgressLabel);
                Require(!surface.Browser.CanClose, "closing was not blocked while an operation ran");
                Require(surface.Browser.CanCancel, "an operation in progress could not be cancelled");
                surface.RequestCancel();
                cancelResult = (await pending).Detail;
                Require(cancelResult == PluginStrings.OperationCancelledByUser,
                    "the cancel sentence differs from macOS: " + cancelResult);
                Require(surface.Browser.CanClose && !surface.Browser.IsMutating,
                    "the window stayed busy after cancelling");
            };
            await ShowPluginBrowser("claude", workspace);

            // --- Codex: verify single user-level scope option ---
            smokeReaderFactory = _ => new FakeMarketplaceReader(CodexPluginSmokeSnapshot);
            smokeCodexPluginDialog = async surface =>
            {
                await surface.Load();
                Require(surface.Browser.IsReady, "the Codex plugin list did not load from the fixture");
                codexScopes = surface.Browser.ScopeOptions.Count;
                Require(codexScopes == 1 && surface.Browser.ScopeOptions[0].Value == "user",
                    "Codex must offer the user scope only");
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
