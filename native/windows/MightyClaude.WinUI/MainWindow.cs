using System.Globalization;
using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly DesktopService service;
    /// <summary>Thumbnails of the pictures agents showed, shared by every transcript.</summary>
    private readonly AgentPictures pictures;
    /// <summary>The design-token brushes this window, its settings window and companion share; recoloured in place by <see cref="Render"/>.</summary>
    internal readonly DesignBrushes brushes = new();
    // No padding or spacing: the sidebar surface runs edge to edge and the dock keeps its own inset (MainWindow.Shell.cs).
    private readonly Grid root = new();
    /// <summary>The window's background: the token <c>page</c>, one brush for every theme.</summary>
    private SolidColorBrush WindowBackground() => brushes.Brush(DesignToken.Page);
    // No spacing: every sidebar part carries the Mac's own margins (MainWindow.Sidebar.cs).
    // The search, the work-status entry and the section header stay put; only the list under
    // them scrolls (M/WorkspaceView.swift:69-96).
    private readonly StackPanel sidebarTop = new();
    /// <summary>The scrolling part of the sidebar: the workspace list and its empty text.</summary>
    private readonly StackPanel sidebar = new();
    private readonly Grid panes = new() { ColumnSpacing = 12, RowSpacing = 12 };
    private readonly StackPanel workspaces = new() { Spacing = 4, Margin = new Thickness(9, 0, 9, 0) };
    private readonly TextBox search = new() { FontSize = 12, BorderThickness = new Thickness(0), Padding = new Thickness(0), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock sessionsHeader = new() { FontSize = DesignMetrics.Type.Small, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    // The whole Windows app is a beta: the badge beside the sidebar brand.
    private readonly TextBlock brandBeta = new() { FontSize = DesignMetrics.Type.Badge, FontWeight = Microsoft.UI.Text.FontWeights.Medium };
    private readonly Button addFolderButton, settingsButton;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = DesignMetrics.Type.Small, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap };
    // The Windows-only layout picker (decision Q3: kept, styled as a sidebar control).
    private readonly ComboBox layout = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Dictionary<string, PaneView> views = [];
    private RuntimeInfo? runtime;
    private bool rendering, canClose, closing;
    private readonly StartupOptions options;
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CliUpdateService cliUpdateService;
    internal readonly CliUpdateCoordinator coordinator;
    // Reads who each CLI is signed in as when the CLI accounts section opens and
    // after a sign-in terminal closes; the section's buttons call it.
    internal readonly CliAccountsCoordinator accountsCoordinator = new(new CliRunner());
    internal Func<string, CancellationToken, Task<CliUpdateResult>>? smokeCliUpdater;
    internal Func<StatusLineDiscovery>? smokeStatusLineDiscovery;
    internal Func<StatusLineConfig, StatusLineContext, CancellationToken, Task<StatusLineResult>>? smokeStatusLineRunner;
    private bool dialogOpen;
    private Func<ContentDialog, TextBox, StackPanel, Task<ContentDialogResult>>? smokeAskName;
    public MainWindow(StartupOptions options)
    {
        this.options = options;
        DesignBrushes.ApplyControlResources(Application.Current.Resources); root.Background = WindowBackground();
        AppWindow.Resize(new SizeInt32(1440, 920));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "MightyClaude.ico"));
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var legacy = new[] { "MightyClaude", "mighty-claude" }.Select(name => Path.Combine(appData, name)).FirstOrDefault(path => File.Exists(Path.Combine(path, "workspace-state.json")));
        var profile = options.ProfileDirectory ?? Path.Combine(appData, "MightyClaudeNative");
        // The splash paints before the state loads: give it the saved theme, not a navy flash.
        var savedTheme = StateStore.SavedTheme(profile);
        brushes.Apply(DesignTokens.Palette(savedTheme)); root.RequestedTheme = savedTheme == "light" ? ElementTheme.Light : ElementTheme.Dark; brushes.ApplyTitleBar(AppWindow);
        service = new(profile, options.ProfileDirectory is null ? legacy : null, Path.Combine(AppContext.BaseDirectory, "claude-mods"));
        pictures = new(service.Images, DispatcherQueue);
        cliUpdateService = new(new CliRunner());
        coordinator = new(UpdateManualProvider);
        coordinator.StateChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (!coordinator.IsUpdating)
            {
                if (coordinator.Results.Count > 0) lastCliUpdateResults = coordinator.Results;
                if (!closing) TrackLoginTask(ManualUpdateFinished());
            }
        });
        InitializeLoginRecovery();
        service.RunEventReceived += value => DispatcherQueue.TryEnqueue(() => { if (closing) return; RecordProviderActivity(value); ReceiveLoginSignal(value); if (views.TryGetValue(value.SessionId, out var pane)) { pane.Refresh(); pane.ReceiveQueueRunEvent(value); if (value.Type == "status" && value.Status is "stopped" or "completed" or "error") pane.ClearToolPermissions(); } RefreshRunningIndicators(); HandleRunEventForNotification(value); });
        // Claude's extra tool-permission requests never travel as a RunEvent:
        // they are ephemeral, so they reach the pane that can show the bar and
        // nowhere else — not the snapshot.
        service.ToolPermissionChanged += value => DispatcherQueue.TryEnqueue(() => { if (closing) return; if (views.TryGetValue(value.RunId, out var pane)) pane.ReceiveToolPermission(value); try { ReceiveCompanionPermission(value); } catch (Exception ex) { companionError = ex.Message; } });
        service.PersistenceFailed += ex => DispatcherQueue.TryEnqueue(() => error.Text = Locale.Get("window.error.saveFailed", new Dictionary<string, string> { ["reason"] = ex.Message }));
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DesignMetrics.Layout.SidebarDefault), MinWidth = DesignMetrics.Layout.SidebarMin, MaxWidth = DesignMetrics.Layout.SidebarMax }); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var name in new[] { "grid", "columns", "focus", "tabs", "custom" }) layout.Items.Add(new ComboBoxItem { Tag = name });
        layout.SelectionChanged += async (_, _) => { if (!rendering && layout.SelectedItem is ComboBoxItem item) await ApplyLayoutPreset((string)item.Tag); };
        addFolderButton = Button("", PickFolder); sidebarTop.Children.Add(BuildSidebarSearch()); sidebarTop.Children.Add(BuildSidebarSectionHeader()); sidebar.Children.Add(workspaces); sidebar.Children.Add(sidebarEmpty); StyleSidebarOpenFolder();
        InitAppShell();
        var sideHost = new Grid(); sideHost.RowDefinitions.Add(new() { Height = GridLength.Auto }); sideHost.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); sideHost.RowDefinitions.Add(new() { Height = GridLength.Auto });
        sideHost.Children.Add(sidebarTop);
        var list = new ScrollViewer { Content = sidebar, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled }; Grid.SetRow(list, 1); sideHost.Children.Add(list);
        // Under the list, the open-folder button (only while none is listed), the layout picker, then the footer.
        var navigation = new StackPanel(); navigation.Children.Add(addFolderButton); navigation.Children.Add(BuildSidebarTools());
        settingsButton = Button("", OpenSettings); navigation.Children.Add(BuildSidebarFooter()); Grid.SetRow(navigation, 2); sideHost.Children.Add(navigation);
        search.TextChanged += (_, _) => RenderSidebar();
        sidebarSurface.Child = sideHost; Grid.SetRowSpan(sidebarSurface, 3); root.Children.Add(sidebarSurface);
        // Over the dock, the error banner and the workspace header (M/WorkspaceView.swift:16-22).
        Grid.SetColumn(detailTop, 1); root.Children.Add(detailTop); Grid.SetRow(panes, 1); Grid.SetColumn(panes, 1); root.Children.Add(panes);
        // The bottom status bar, in the Mac's order (MainWindow.Shell.cs).
        statusBar.Child = BuildStatusBar(); ApplyChromeText(); Grid.SetRow(statusBar, 2); Grid.SetColumn(statusBar, 1); root.Children.Add(statusBar); Content = root;
        AppWindow.Closing += async (_, args) => { if (canClose) return; args.Cancel = true; if (closing) return; closing = true; ShutdownCompanion(); settingsWindow?.Close(); foreach (var pane in views.Values) { pane.CloseReferencePreview(); pane.CloseBrowserView(); } clock.Stop(); StopWorkspaceGit(); root.IsHitTestVisible = false; try { await ShutdownAutomaticUpdates(); await coordinator.ShutdownAsync(); await ShutdownAppUpdateAsync(); await ShutdownAccountUsageAsync(); await ShutdownLoginRecovery(); await ShutdownStatusLines(); await CloseTerminalsAsync(); await ShutdownAgentIO(); await ShutdownMobileRemote(); await service.DisposeAsync(); canClose = true; Close(); } catch (Exception ex) { error.Text = Locale.Get("window.error.shutdownFailed", new Dictionary<string, string> { ["reason"] = ex.Message }); root.IsHitTestVisible = true; closing = false; } };
        clock.Tick += (_, _) => RefreshRunningIndicators(); clock.Start();
        InitFilePane(); InitAddPaneShortcuts();
        ShowLaunchSplash(); InitParityShortcuts(); InitWorkspaceGit(); InitDashboard();
        _ = Initialize();
    }
    private async Task Initialize()
    {
        try
        {
        if (!options.SmokeTest) { await Act(async () => { await service.InitializeAsync(); Locale.LanguagePreference = service.Snapshot.LanguagePreference; ApplyChromeText(); Render(); HideLaunchSplash(); InitializeAgentIO(); await InitializeCompanionAsync(); await InitializeMobileRemote(); await InitNotifierAsync(); await RefreshRuntime(); BeginAutomaticUpdates(); BeginAutomaticAppUpdateCheck(); }); return; }
        try { await service.InitializeAsync(); Locale.LanguagePreference = service.Snapshot.LanguagePreference; ApplyChromeText(); Render(); HideLaunchSplash(); await RunUISmoke(); }
        catch (Exception ex) { options.WriteStartupFailure(ex); await FinishSmoke(false); }
        }
        finally { HideLaunchSplash(); }
    }
    // The sidebar chrome is built before Initialize reads the saved language,
    // so its text is set again once the preference is applied.
    private void ApplyChromeText()
    {
        RefreshDashboardChrome();
        Title = Locale.Get("window.title.betaTemplate", new Dictionary<string, string> { ["app"] = "Mighty Claude" });
        brandBeta.Text = Locale.Get("badge.beta"); AutomationProperties.SetName(brandBeta, Locale.Get("badge.betaAccessibility"));
        search.PlaceholderText = Locale.Get("sidebar.searchPlaceholder");
        foreach (var item in layout.Items.OfType<ComboBoxItem>())
            item.Content = Locale.Get((string)item.Tag switch { "grid" => "layout.mode.grid", "columns" => "layout.mode.columns", "focus" => "layout.mode.focus", "tabs" => "layout.mode.tabs", _ => "layout.mode.custom" });
        addFolderLabel.Text = Locale.Get("sidebar.openFolder"); AutomationProperties.SetName(addFolderButton, addFolderLabel.Text);
        sessionsHeader.Text = Locale.Get("sidebar.workspacesHeader");
        RefreshSidebarThemeButton(); RefreshSidebarToggles();
        AutomationProperties.SetName(settingsButton, Locale.Get("settings.settingsWindowTitle"));
        ToolTipService.SetToolTip(settingsButton, Locale.Get("settings.settingsWindowTitle"));
        if (errorBannerDismiss is { } dismiss) { AutomationProperties.SetName(dismiss, Locale.Get("window.error.dismiss")); ToolTipService.SetToolTip(dismiss, Locale.Get("window.error.dismiss")); }
        // The counts' words and the status bar's are in the language too.
        workspaceHeaderCounts?.Invalidate(); RefreshStatusBar(); RefreshCompanionControls();
    }
    private async Task Act(Func<Task> action)
    {
        try { error.Text = ""; await action(); }
        catch (Exception ex) { error.Text = ex.Message; if (options.SmokeTest) throw; }
    }
    internal static Button Button(string title, Func<Task> action)
    {
        var button = new Button { Content = title }; AutomationProperties.SetName(button, title);
        button.Click += async (_, _) => await action(); return button;
    }
    private Button SafeButton(string title, Func<Task> action) => Button(title, () => Act(action));
    private async Task PickFolder()
    {
        await Act(async () => { var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this)); if (await picker.PickSingleFolderAsync() is { } folder) { await service.AddWorkspaceAsync(folder.Path); HideDashboard(); Render(); } });
    }
    private Task RemoveWorkspace() => service.Snapshot.ActiveWorkspaceId is { } id ? ConfirmRemoveWorkspace(id) : Task.CompletedTask;
    private async Task AddPane(string kind, string provider = "claude", string? groupId = null, Func<RunSession, RunSession>? shape = null)
    {
        await Act(async () => { var workspace = service.Snapshot.ActiveWorkspaceId ?? throw new InvalidOperationException(Locale.Get("window.error.addWorkspaceFirst")); HideDashboard(); var pane = new RunSession { WorkspaceId = workspace, Kind = kind, Provider = provider, Title = kind == "shell" ? Locale.Get("session.title.shell") : ProviderCatalog.Name(provider) }; pane = SessionTemplate.Inherit(pane, service.Snapshot.Sessions); if (shape is not null) pane = shape(pane); await service.UpdateAsync(s => AddToLayout(s, workspace, pane, groupId)); Render(); });
    }
    // While a check runs the status bar says so; at rest it names the machine, as the Mac's does (RefreshStatusBar).
    private async Task RefreshRuntime() { await Act(async () => { runtimeChecks++; RefreshStatusBar(); try { if (await ReloadProviderModels()) RefreshEnvironment(); } finally { runtimeChecks--; RefreshStatusBar(); } }); }
    private void RefreshEnvironment()
    {
        foreach (var pane in views.Values) pane.Refresh();
        RefreshStatusBar();
    }
    private ProviderRuntime? Runtime(string provider) => runtime?.Providers.FirstOrDefault(p => p.Id == provider);
    private void RenderSidebar()
    {
        RenderWorkspaceSidebar();
    }
    private void Render()
    {
        if (closing) return; rendering = true; var state = service.Snapshot;
        brushes.Apply(DesignTokens.Palette(state.Theme));
        root.RequestedTheme = state.Theme == "light" ? ElementTheme.Light : ElementTheme.Dark; darkTheme = state.Theme != "light";
        // Every open window of this app shares the theme; ApplyTitleBar skips a bar already in this palette.
        brushes.ApplyTitleBar(AppWindow); if (settingsWindow is { } settings) brushes.ApplyTitleBar(settings.AppWindow); if (companionQuestionWindow is { } companion) brushes.ApplyTitleBar(companion.AppWindow);
        root.Background = WindowBackground(); ApplySidebarCollapsed();
        layout.SelectedItem = layout.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == LayoutMode(state, state.ActiveWorkspaceId)); RenderSidebar(); RefreshWorkspaceHeader();
        RenderAccountUsage();
        DetachPaneViews(); panes.Children.Clear(); panes.RowDefinitions.Clear(); panes.ColumnDefinitions.Clear();
        // A closed session runs nothing more: end its refresher before dropping the pane.
        foreach (var stale in views.Keys.Where(id => !state.Sessions.Any(s => s.Id == id)).ToArray()) { CloseStatusLine(views[stale]); views[stale].ForgetGraphHistory(); views[stale].CloseReferencePreview(); views[stale].CloseBrowserView(); CloseTerminal(views[stale]); views.Remove(stale); }
        RenderPaneLayout(state);
        foreach (var (id, view) in views) view.ShowActive(id == state.ActiveSessionId);
        RefreshStatusBar();
        rendering = false;
        RefreshWorkspaceGit();
        RenderDashboard();
        ReconcileAgentIO();
        RefreshLoginCards();
        mobileRouter?.Changed();
        if (settingsWindow?.Content is FrameworkElement settingsRoot) { settingsRoot.RequestedTheme = root.RequestedTheme; if (settingsRoot is Grid settingsFrame) settingsFrame.Background = WindowBackground(); }
    }
    private static void Copy(string value) { var data = new DataPackage(); data.SetText(value); Clipboard.SetContent(data); }

    private sealed class PillWrapPanel : Panel
    {
        private const double Gap = 5;
        protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
        {
            var width = double.IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width) : 800;
            double x = 0, y = 0, height = 0, used = 0;
            foreach (var child in Children.Where(c => c.Visibility == Visibility.Visible))
            {
                child.Measure(new(width, double.PositiveInfinity)); var size = child.DesiredSize; var itemWidth = Math.Min(width, size.Width);
                if (x > 0 && x + itemWidth > width) { y += height + Gap; x = 0; height = 0; }
                used = Math.Max(used, x + itemWidth); x += itemWidth + Gap; height = Math.Max(height, size.Height);
            }
            return new(Math.Min(width, used), y + height);
        }
        protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
        {
            double x = 0, y = 0, height = 0;
            foreach (var child in Children.Where(c => c.Visibility == Visibility.Visible))
            {
                var size = child.DesiredSize; var width = Math.Min(finalSize.Width, size.Width);
                if (x > 0 && x + width > finalSize.Width) { y += height + Gap; x = 0; height = 0; }
                child.Arrange(new(x, y, width, size.Height)); x += width + Gap; height = Math.Max(height, size.Height);
            }
            return finalSize;
        }
    }
    /// <summary>
    /// The Mac's <c>LazyVGrid</c> with one adaptive column rule (M/UserQuestionnaireCard.swift:167, M/AgentQuestionPanel.swift:34,
    /// M/GuidedPanel.swift:92): as many equal columns as fit at <see cref="Minimum"/>, none wider than <see cref="Maximum"/>,
    /// <see cref="Gap"/> apart both ways, each row as tall as its tallest tile and the tiles aligned to its top.
    /// </summary>
    private sealed class AdaptiveGridPanel : Panel
    {
        internal double Minimum { get; init; } = 200;
        internal double Maximum { get; init; } = double.PositiveInfinity;
        internal double Gap { get; init; } = 6;
        private (int Columns, double Width) Fit(double available)
        {
            var columns = Math.Max(1, (int)Math.Floor((available + Gap) / (Minimum + Gap)));
            return (columns, Math.Min(Maximum, Math.Max(0, (available - (columns - 1) * Gap) / columns)));
        }
        protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
        {
            var available = double.IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width) : 600;
            var (columns, width) = Fit(available);
            double y = 0, row = 0; var index = 0;
            foreach (var child in Children.Where(c => c.Visibility == Visibility.Visible))
            {
                if (index > 0 && index % columns == 0) { y += row + Gap; row = 0; }
                child.Measure(new(width, double.PositiveInfinity)); row = Math.Max(row, child.DesiredSize.Height); index++;
            }
            return new(available, y + row);
        }
        protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
        {
            var (columns, width) = Fit(finalSize.Width);
            var visible = Children.Where(c => c.Visibility == Visibility.Visible).ToList();
            double y = 0;
            for (var start = 0; start < visible.Count; start += columns)
            {
                var cells = visible.Skip(start).Take(columns).ToList(); var row = cells.Max(c => c.DesiredSize.Height);
                for (var column = 0; column < cells.Count; column++) cells[column].Arrange(new(column * (width + Gap), y, width, cells[column].DesiredSize.Height));
                y += row + Gap;
            }
            return finalSize;
        }
    }
    private sealed partial class PaneView
    {
        private static string InputShortcuts => Locale.Get("composer.inputShortcuts");
        private readonly MainWindow owner;
        private readonly string id;
        /// <summary>The pane header's state word: 11.5 semibold in its tone's ink (M/SessionPaneView.swift:220).</summary>
        private readonly TextBlock label = new() { FontSize = DesignMetrics.Type.State, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock detail = new() { FontSize = 10, Opacity = .6, TextWrapping = TextWrapping.Wrap };
        /// <summary>Why a draft cannot run yet: the reason line of the row over the toolbar (M/SessionPaneView.swift:636).</summary>
        private readonly TextBlock inputHint = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
        private readonly AgentTranscript output = new();
        /// <summary>The pane header's figures: 11 mono in <c>ink2</c>, the first part of the line to give way (M/PaneChrome.swift:61-70).</summary>
        private readonly TextBlock elapsed = new() { FontSize = DesignMetrics.Type.Mono, FontFamily = new FontFamily(DesignMetrics.Font.Mono), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        // Auto height uses the native text layout, including soft wraps and IME
        // composition. One 13pt line is 20 high and the editor scrolls inside itself after six
        // (M/NativeComposerEditor.swift:32-33, M/TextEditorHeightReader.swift:143-149); the 5pt side padding
        // is the Mac text view's line-fragment padding. A 13pt line of the body font is laid out 18 high here
        // (16 on the Mac), so the padding above and below it is 1.
        private readonly TextBox input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = ComposerLine, MaxHeight = ComposerLine + 5 * ComposerLineStep, MaxLength = 100000, PlaceholderText = Locale.Get("composer.placeholder.idle"), BorderThickness = new Thickness(0), Padding = new Thickness(5, 1, 5, 1), FontSize = DesignMetrics.Type.Body };
        /// <summary>The editor with one line, and what each further line adds (13pt in the body font).</summary>
        private const double ComposerLine = 20, ComposerLineStep = 17.3;
        // The pills, in the Mac's order (M/SessionPaneView.swift:713-723): attach, model, effort, permission, Fast, … and,
        // in a narrow pane, the options menu that stands in for the last four.
        private readonly Button attach = NewPill<Button>(), model = NewPill<Button>(chevron: true), effort = NewPill<Button>(chevron: true), permission = NewPill<Button>(chevron: true), more = NewPill<Button>(), options = NewPill<Button>();
        private readonly Microsoft.UI.Xaml.Controls.Primitives.ToggleButton fast = NewPill<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>();
        private Button send = null!, context = null!;
        /// <summary>The send / stop button's coloured shape, drawn under the button (PaintSend, MainWindow.Composer.cs).</summary>
        private readonly Border sendDisc = new() { Width = 32, Height = 32, IsHitTestVisible = false };
        /// <summary>The send button's symbols (the arrow, the queue mark and the stop square), one shown and inked by <see cref="PaintSend"/> in every state.</summary>
        private readonly Grid sendGlyph = new() { Width = 32, Height = 32 };
        /// <summary>The pills last painted active (<see cref="PaintPill"/>), so a change of enablement keeps their look.</summary>
        private readonly HashSet<ContentControl> activePills = [];
        private readonly Grid sendHost = new() { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center };
        /// <summary>The composer card's surface (M/SessionPaneView.swift:654); its edge is <see cref="composerRing"/>.</summary>
        private Border composerCard = null!;
        /// <summary>The shape under the composer card that casts its shadow (CardShadow).</summary>
        private Microsoft.UI.Xaml.Shapes.Rectangle composerShadow = null!;
        private bool composerFocused, composerDropTargeted;
        private readonly StackPanel selectors = new() { Orientation = Orientation.Horizontal, Spacing = ToolbarSpacing, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        /// <summary>The attachments of the next request: one row that scrolls sideways (M/SessionPaneView.swift:542-553).</summary>
        private readonly StackPanel attachmentChips = new() { Orientation = Orientation.Horizontal, Spacing = 7 };
        private readonly ScrollViewer attachmentsScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollMode = ScrollMode.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled, Margin = new Thickness(10, 10, 10, 0), Visibility = Visibility.Collapsed };
        private readonly List<RunAttachment> pendingAttachments = [];
        private bool updating, draftLoaded, attachmentsLoading, composingInput, starting, stopping, canSend;
        public Border Container { get; }
        private RunSession Session => owner.service.Snapshot.Sessions.First(s => s.Id == id);
        private Workspace Workspace => owner.service.Snapshot.Workspaces.First(w => w.Id == Session.WorkspaceId);
        private ProviderCapabilities Capabilities
        {
            get
            {
                var pane = Session;
                return owner.Runtime(pane.Provider)?.Capabilities ?? ProviderCatalog.Capabilities(pane.Provider);
            }
        }
        internal PaneView(MainWindow owner, string id)
        {
            this.owner = owner; this.id = id;
            output.OpenReference = OpenReferencePreview; output.OpenImage = OpenTranscriptImage;
            InitSlashPalette(); InitPermissionBar(); InitializeStyles();
            var grid = new Grid { Padding = new Thickness(12), RowSpacing = 8 };
            foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) grid.RowDefinitions.Add(new RowDefinition { Height = height });
            // The 34pt header line runs edge to edge over the pane card (MainWindow.PaneHeader.cs).
            var header = BuildPaneHeader(); grid.Children.Add(header);
            // Copy lives in the header's … menu, as on the Mac (decision Q4).
            AddPaneMenu();
            Grid.SetRow(output.View, 1); grid.Children.Add(output.View);
            PaintTranscriptSurface(grid); InitializeEmptyOutput(grid);
            // The composer and what hangs off it (MainWindow.Composer.cs).
            var composerScroll = BuildComposer(grid); var card = composerCard;
            Container = new Border { Child = grid, Background = owner.brushes.Brush(DesignToken.Card), BorderThickness = new Thickness(DesignMetrics.Stroke.Line), BorderBrush = owner.brushes.Brush(DesignToken.Line), CornerRadius = new CornerRadius(DesignMetrics.Radius.Pane) };
            InitializeTerminal(grid, composerScroll);
            // A press anywhere in the pane makes it the active one, also where a control handles the press itself (M/SessionPaneView.swift:170-190).
            Container.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => owner.ActivatePane(id)), true);
            Container.SizeChanged += (_, args) => composerScroll.MaxHeight = Math.Max(150, args.NewSize.Height - 144);
            fast.Click += async (_, _) => { if (!updating) await ChangeSettings(s => s with { FastMode = !s.FastMode && Capabilities.FastMode }); };
            ToolTipService.SetToolTip(fast, Locale.Get("composer.fast.tooltip")); AutomationProperties.SetName(fast, "Codex Fast");
            ToolTipService.SetToolTip(input, InputShortcuts);
            input.TextChanged += async (_, _) => { if (!updating) { var draft = input.Text; RefreshComposerState(); RefreshPalette(draft); await owner.Act(() => Change(p => p with { Draft = draft })); } };
            input.TextCompositionStarted += (_, _) => composingInput = true;
            input.TextCompositionEnded += (_, _) =>
            {
                composingInput = false;
                // Ending composition is not a submit command. If the IME then
                // explicitly routes Enter, the normal key handler may send the
                // committed text on that same key, as the macOS editor does.
            };
            input.PreviewKeyDown += async (_, args) =>
            {
                if (args.Handled) return;
                if (!composingInput && paletteState.IsOpen && HandlePaletteKey(args.Key)) { args.Handled = true; return; }
                if (args.Key == Windows.System.VirtualKey.Enter)
                {
                    if (!SubmitKeyAllowed(composingInput, IsInputKeyDown(Windows.System.VirtualKey.Shift), IsInputKeyDown(Windows.System.VirtualKey.Menu) || IsInputKeyDown(Windows.System.VirtualKey.LeftWindows) || IsInputKeyDown(Windows.System.VirtualKey.RightWindows))) return;
                    args.Handled = true;
                    // Send rechecks the same busy/content/capability guards used
                    // by the button. Holding Enter must not submit again.
                    if (!args.KeyStatus.WasKeyDown) await Send(steering: IsInputKeyDown(Windows.System.VirtualKey.Control));
                    return;
                }
                if (args.Key != Windows.System.VirtualKey.V || !IsInputKeyDown(Windows.System.VirtualKey.Control)) return;
                try { var data = Clipboard.GetContent(); if (!AttachmentInput.ContainsFiles(data)) return; args.Handled = true; if (Session.Kind == "shell") { owner.error.Text = Locale.Get("wire.startRun.attachmentAiOnly"); return; } await LoadAttachments(() => AttachmentInput.ReadDataAsync(data)); } catch (Exception ex) { owner.error.Text = ex.Message; }
            };
            input.Paste += async (_, args) =>
            {
                try { var data = Clipboard.GetContent(); if (!AttachmentInput.ContainsFiles(data)) return; args.Handled = true; if (Session.Kind == "shell") { owner.error.Text = Locale.Get("wire.startRun.attachmentAiOnly"); return; } await LoadAttachments(() => AttachmentInput.ReadDataAsync(data)); } catch (Exception ex) { owner.error.Text = ex.Message; }
            };
            card.AllowDrop = true;
            card.DragOver += (_, args) => { if (!AttachmentInput.ContainsFiles(args.DataView)) return; args.AcceptedOperation = attachmentsLoading || Session.Kind == "shell" ? DataPackageOperation.None : DataPackageOperation.Copy; args.Handled = true; if (Session.Kind == "shell") owner.error.Text = Locale.Get("wire.startRun.attachmentAiOnly"); };
            // The drop ring follows files over any part of the card, the editor included (which handles
            // the event itself), and goes only when the pointer has left the card's bounds.
            card.AddHandler(UIElement.DragOverEvent, new DragEventHandler((_, args) =>
            {
                if (composerDropTargeted || Session.Kind == "shell" || !AttachmentInput.ContainsFiles(args.DataView)) return;
                composerDropTargeted = true; PaintComposerRing();
            }), true);
            card.DragLeave += (_, args) =>
            {
                var at = args.GetPosition(card);
                if (at.X > 0 && at.Y > 0 && at.X < card.ActualWidth && at.Y < card.ActualHeight) return;
                composerDropTargeted = false; PaintComposerRing();
            };
            card.Drop += async (_, args) => { composerDropTargeted = false; PaintComposerRing(); if (!AttachmentInput.ContainsFiles(args.DataView)) return; var deferral = args.GetDeferral(); args.Handled = true; try { if (Session.Kind == "shell") owner.error.Text = Locale.Get("wire.startRun.attachmentAiOnly"); else await LoadAttachments(() => AttachmentInput.ReadDataAsync(args.DataView)); } finally { deferral.Complete(); } };
            AutomationProperties.SetName(input, Locale.Get("composer.input.name"));  AutomationProperties.SetLiveSetting(inputHint, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            input.GotFocus += (_, _) => { composerFocused = true; PaintComposerRing(); };
            input.LostFocus += (_, _) => { composingInput = false; composerFocused = false; PaintComposerRing(); };
        }
        /// <summary>
        /// The pane card's border: <c>line</c>, or accent × 0.58 on the active pane (M/SessionPaneView.swift:164,
        /// M/AgentTerminalPaneView.swift:46). A browser or the files pane is part of its group's card under
        /// the group's slim bar: no edge of its own and square top corners (M/BrowserPaneView.swift:30-34,
        /// M/FilePaneView.swift:22-28). All are shared brushes, so a theme toggle recolours a reused pane in
        /// place. Render sets it on every pane.
        /// </summary>
        internal void ShowActive(bool active)
        {
            var b = owner.brushes;
            if (paneKind is "browser" or AgentIOPaneKind.Browser or FilePaneKind.Kind)
            {
                Container.BorderBrush = b.Transparent; Container.BorderThickness = new Thickness(0);
                Container.CornerRadius = new CornerRadius(0, 0, DesignMetrics.Radius.Pane, DesignMetrics.Radius.Pane);
                // Its content fills the card, over the pane grid's 12pt padding (the browser's host is built that way);
                // a margin the files pane gives its own host is left alone.
                if (filesHost is { } files && files.Margin == default) files.Margin = new Thickness(-12);
            }
            else Container.BorderBrush = active ? b.Brush(DesignToken.Accent, DesignMetrics.Opacity.PaneActiveBorder) : b.Brush(DesignToken.Line);
            PaintHeaderFade();
        }
        private static bool IsInputKeyDown(Windows.System.VirtualKey key) =>
            (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        private static bool SubmitKeyAllowed(bool composing, bool shift, bool otherModifier) => !composing && !shift && !otherModifier;
        private Task Send(bool steering = false) => owner.Act(async () =>
        {
            if (owner.ManualMutationBlockReason(Session) is not null) { RefreshComposerState(); return; }
            if (await DeferBusyComposer(steering)) return;
            RefreshComposerState(); if (!canSend || starting || composingInput) return;
            starting = true; var submissionVersion = ++composerSubmissionVersion; RefreshComposerState();
            try
            {
            var pane = Session; var submitted = input.Text; var files = pendingAttachments.ToArray();
            var styledSubmission = await PrepareStyleSubmission(submitted, files.Length > 0);
            var request = await PrepareStyleRunRequest(new StartRunRequest(
                pane.Id, pane.WorkspaceId, pane.Kind, styledSubmission,
                RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot),
                pane.Model, pane.Provider, pane.Settings, pane.ResumeId, files));
            if (!QueuePaneAlive || submissionVersion != composerSubmissionVersion) return;
            await owner.StartFromComposer(request);
            if (!owner.service.Snapshot.Sessions.Any(p => p.Id == id)) return;
            var consumed = files.Select(file => file.Id).ToHashSet(); pendingAttachments.RemoveAll(file => consumed.Contains(file.Id)); RefreshAttachments();
            // Only consume the submitted draft. A newer draft typed while start
            // was awaiting must remain untouched, and a rejected start keeps input.
            if (input.Text == submitted)
            {
                updating = true; input.Text = ""; updating = false;
                await Change(p => p with { Draft = "" });
            }
            Refresh();
            } finally { starting = false; if (owner.service.Snapshot.Sessions.Any(p => p.Id == id)) Refresh(); }
        });
        private Task PrimaryAction() => ComposerPrimaryAction();
        private void RefreshComposerState()
        {
            var pane = Session; var busy = pane.Status == "running" || starting || queueStarting || owner.BackgroundUpdateHolds(pane); var runtime = owner.Runtime(pane.Provider); var workspace = Workspace;
            var catalog = runtime?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider);
            var registeredModels = RegisteredModelsFor(pane.Provider, workspace, owner.service.Snapshot);
            var levels = ProviderCatalog.Efforts(pane.Provider, pane.Model, catalog, registeredModels);
            var unsupportedEffort = pane.Kind == "claude" && pane.Settings.Effort != "default" && !levels.Contains(pane.Settings.Effort);
            string? unsupportedSettings = null;
            if (pane.Kind == "claude" && pane.Settings.PermissionMode == "auto" && runtime?.Capabilities.PermissionModes?.Contains("auto") != true) unsupportedSettings = Locale.Get("composer.hint.autoModeUnverified");
            if (pendingAttachments.Count > 0 && !Capabilities.Attachments) unsupportedSettings = Locale.Get("composer.hint.attachmentsUnsupported");
            var mutationBlock = owner.ManualMutationBlockReason(pane);
            // Why a draft cannot run yet: its own row over the toolbar (M/SessionPaneView.swift:631-643). Attachments being
            // read and a background update holding sends each have their own row with a spinner (:585-590, 622-630).
            var reason = mutationBlock ?? (busy || attachmentsLoading ? ""
                : pane.Kind == "claude" && runtime?.Available != true ? Locale.Get("composer.hint.draftStillAllowed", new Dictionary<string, string> { ["reason"] = runtime?.Detail ?? Locale.Get("composer.hint.refreshRuntime") })
                : unsupportedEffort ? Locale.Get("composer.hint.effortUnverified", new Dictionary<string, string> { ["effort"] = pane.Settings.Effort })
                : unsupportedSettings ?? "");
            inputHint.Text = reason; blockedRow.Visibility = reason.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            loadingRow.Visibility = attachmentsLoading ? Visibility.Visible : Visibility.Collapsed;
            var holds = owner.BackgroundUpdateHolds(pane); holdRow.Visibility = holds ? Visibility.Visible : Visibility.Collapsed;
            if (holds) holdText.Text = Locale.Get("settings.cliUpdate.backgroundUpdateQueued", new Dictionary<string, string> { ["provider"] = ProviderMark.Label(pane.Provider) });
            var said = reason.Length > 0 ? reason : attachmentsLoading && !busy ? Locale.Get("composer.hint.attachmentsLoading") : "";
            AutomationProperties.SetHelpText(input, said.Length == 0 ? InputShortcuts : said + " " + InputShortcuts);
            // While a run is busy the editor says what Enter will do with the next request (M/SessionPaneView.swift:98-99).
            input.PlaceholderText = busy ? Locale.Get(pane.Kind == "claude" && pane.Provider == "claude" ? "composer.placeholder.busy" : "composer.placeholder.busyQueue") : pane.Kind == "shell" ? Locale.Get("composer.placeholder.shell") : Locale.Get("composer.placeholder.idle");
            canSend = mutationBlock is null && !busy && !attachmentsLoading && (pane.Kind == "shell" || runtime?.Available == true) && !unsupportedEffort && unsupportedSettings is null && (!string.IsNullOrWhiteSpace(input.Text) || pendingAttachments.Count > 0);
            send.IsEnabled = busy ? !stopping : canSend; ShowSendSymbol(busy ? "stop" : "send");
            AutomationProperties.SetName(send, busy ? Locale.Get("composer.stop.name") : Locale.Get("composer.send.name")); ToolTipService.SetToolTip(send, busy ? Locale.Get("composer.stop.tooltip") : Locale.Get("composer.send.tooltip"));
            context.Visibility = pane.Kind == "shell" ? Visibility.Collapsed : Visibility.Visible; RefreshContextIndicator();
            ToolTipService.SetToolTip(context, pane.SessionUsage?.ContextPercent is null ? Locale.Get("composer.context.unavailable") : Locale.Get("composer.context.tooltip"));
            resumeHost.Visibility = pane.ResumeId is null ? Visibility.Collapsed : Visibility.Visible;
            foreach (var control in selectors.Children.OfType<Control>()) control.IsEnabled = !busy; HoldRunSettings(busy);
            // With no level to choose and none chosen the effort menu is off, and Fast where it is unsupported and off (M/SessionPaneView.swift:378, 410).
            if (levels.Length == 0 && pane.Settings.Effort == "default") effort.IsEnabled = false;
            if (!Capabilities.FastMode && !pane.Settings.FastMode) fast.IsEnabled = false;
            attach.IsEnabled = !attachmentsLoading; attach.Visibility = pane.Kind == "shell" ? Visibility.Collapsed : Visibility.Visible;
            RefreshStyles();
            RefreshQueuedComposer(busy);
            if (mutationBlock is not null && (!busy || HasComposerContent)) send.IsEnabled = false;
            RefreshStyleComposer();
            RefreshNextActions(pane, busy);
            PaintSend();
        }
        private Task PickAttachments() => LoadAttachments(async () =>
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
            return await AttachmentInput.ReadFilesAsync(await picker.PickMultipleFilesAsync());
        });
        private async Task LoadAttachments(Func<Task<List<RunAttachment>>> read)
        {
            if (attachmentsLoading || Session.Kind == "shell") return;
            // A new read takes the last one's notice down; what goes wrong with this one says so in the composer (M/AppStore+Attachments.swift:105, 117).
            attachmentsLoading = true; ShowAttachmentError(null); RefreshComposerState();
            try
            {
                var files = await read();
                if (owner.closing || !owner.service.Snapshot.Sessions.Any(p => p.Id == id)) return;
                _ = AttachmentSupport.Validate(pendingAttachments.Concat(files).ToArray()); pendingAttachments.AddRange(files); RefreshAttachments();
            }
            catch (Exception ex) { if (!owner.closing && owner.service.Snapshot.Sessions.Any(p => p.Id == id)) ShowAttachmentError(ex.Message); }
            finally
            {
                attachmentsLoading = false;
                if (!owner.closing && owner.service.Snapshot.Sessions.Any(p => p.Id == id)) { RefreshComposerState(); input.Focus(FocusState.Programmatic); }
            }
        }
        /// <summary>A size as the Mac's file byte count reads it (M/ComposerAttachments.swift:40-42): decimal units, whole kilobytes, one decimal from a megabyte.</summary>
        private static string ByteLabel(long bytes) => bytes < 1000 ? bytes.ToString(CultureInfo.CurrentCulture) + " bytes"
            : bytes < 1_000_000 ? (bytes / 1000.0).ToString("0", CultureInfo.CurrentCulture) + " KB" : (bytes / 1_000_000.0).ToString("0.#", CultureInfo.CurrentCulture) + " MB";
        /// <summary>
        /// The attachment chips (M/ComposerAttachments.swift:12-36): a 34pt thumbnail or an accent document mark at radius 5,
        /// the name in 11 medium over its size in 10pt <c>ink2</c> (106 wide), and a 20 × 26 remove button, 7 apart and padded 6
        /// on the subtle wash at radius 9 with a hairline <c>line</c>. Pressing the chip previews the attachment.
        /// </summary>
        private void RefreshAttachments()
        {
            var b = owner.brushes;
            attachmentChips.Children.Clear(); attachmentsScroll.Visibility = pendingAttachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            FitComposerSpacing();
            foreach (var file in pendingAttachments)
            {
                var image = file.MediaType.StartsWith("image/", StringComparison.Ordinal); var size = ByteLabel(AttachmentSupport.DecodedLength(file));
                var picture = new Grid { Width = 34, Height = 34, CornerRadius = new CornerRadius(DesignMetrics.Radius.FileRow), Background = b.Brush(DesignToken.Ink, AttachmentPictureWash) };
                if (image) { var thumbnail = new Microsoft.UI.Xaml.Shapes.Rectangle { RadiusX = DesignMetrics.Radius.FileRow, RadiusY = DesignMetrics.Radius.FileRow }; picture.Children.Add(thumbnail); _ = LoadThumbnail(file, thumbnail); }
                else picture.Children.Add(new FontIcon { Glyph = file.MediaType == "application/pdf" ? "" : "", FontSize = 17, Foreground = b.Brush(DesignToken.Accent) });
                var words = new StackPanel { Spacing = 2, Width = 106, VerticalAlignment = VerticalAlignment.Center };
                words.Children.Add(new TextBlock { Text = file.Name, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = b.Brush(DesignToken.Ink), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
                words.Children.Add(new TextBlock { Text = size, FontSize = 10, Foreground = b.Brush(DesignToken.Ink2) });
                var face = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 }; face.Children.Add(picture); face.Children.Add(words);
                var preview = Button(file.Name, () => PreviewAttachment(file)); preview.Content = face; preview.MinWidth = 0; preview.MinHeight = 0; preview.Padding = new Thickness(0); preview.BorderThickness = new Thickness(0); preview.CornerRadius = new CornerRadius(DesignMetrics.Radius.FileRow);
                owner.PaintPlainButton(preview, b.Transparent, b.Transparent);
                AutomationProperties.SetName(preview, Locale.Get("composer.attachment.preview", new Dictionary<string, string> { ["name"] = file.Name })); ToolTipService.SetToolTip(preview, $"{file.Name} · {size}");
                var remove = Button("×", () => { pendingAttachments.RemoveAll(a => a.Id == file.Id); ShowAttachmentError(null); RefreshAttachments(); RefreshComposerState(); input.Focus(FocusState.Programmatic); return Task.CompletedTask; });
                remove.Content = new FontIcon { Glyph = "", FontSize = 9, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }; remove.MinWidth = 0; remove.MinHeight = 0; remove.Width = 20; remove.Height = 26; remove.Padding = new Thickness(0); remove.BorderThickness = new Thickness(0); remove.CornerRadius = new CornerRadius(DesignMetrics.Radius.FileRow); remove.VerticalAlignment = VerticalAlignment.Center;
                owner.PaintPlainButton(remove, b.Transparent, b.Subtle, ink: b.Brush(DesignToken.Ink2));
                AutomationProperties.SetName(remove, Locale.Get("composer.attachment.remove", new Dictionary<string, string> { ["name"] = file.Name })); AutomationProperties.SetAutomationId(remove, "remove-attachment-" + file.Id);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 }; row.Children.Add(preview); row.Children.Add(remove);
                var chip = new Border { Child = row, Padding = new Thickness(6), CornerRadius = new CornerRadius(AttachmentChipRadius), Background = b.Brush(DesignToken.Ink, AttachmentChipWash), BorderBrush = b.Brush(DesignToken.Line), BorderThickness = new Thickness(DesignMetrics.Stroke.Hairline) };
                AutomationProperties.SetAutomationId(chip, "attachment-" + file.Id);
                attachmentChips.Children.Add(chip);
            }
        }
        /// <summary>An attachment chip's corner, and the ink washes under the chip and under its picture (M/ComposerAttachments.swift:31, 19).</summary>
        private const double AttachmentChipRadius = 9, AttachmentChipWash = 0.045, AttachmentPictureWash = 0.04;
        /// <summary>Fills a chip's rounded thumbnail with the picture, scaled to fill it (M/ComposerAttachments.swift:15-20, 117-127).</summary>
        private static async Task LoadThumbnail(RunAttachment file, Microsoft.UI.Xaml.Shapes.Rectangle thumbnail) { try { thumbnail.Fill = new ImageBrush { ImageSource = await AttachmentInput.PreviewAsync(file, 96), Stretch = Stretch.UniformToFill }; } catch (Exception) { thumbnail.Visibility = Visibility.Collapsed; } }
        private Task PreviewAttachment(RunAttachment file) => owner.Act(async () =>
        {
            var content = new StackPanel { Spacing = 10, MaxWidth = 640 }; content.Children.Add(new TextBlock { Text = $"{file.MediaType} · {AttachmentSupport.DecodedLength(file):N0} bytes", FontSize = 11 });
            if (file.MediaType.StartsWith("image/", StringComparison.Ordinal)) content.Children.Add(new Image { Source = await AttachmentInput.PreviewAsync(file), MaxHeight = 420, Stretch = Stretch.Uniform });
            else if (file.MediaType == "text/plain") { var text = System.Text.Encoding.UTF8.GetString(AttachmentSupport.Decode(file)); content.Children.Add(new TextBox { AcceptsReturn = true, Text = text[..Math.Min(text.Length, 20000)], IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 350 }); }
            else content.Children.Add(new TextBlock { Text = Locale.Get("composer.attachment.sentAsFile"), TextWrapping = TextWrapping.Wrap });
            await owner.StyledDialog(new ContentDialog { Title = file.Name, Content = content, CloseButtonText = Locale.Get("settings.closeButton"), XamlRoot = owner.root.XamlRoot }).ShowAsync();
        });
        private Task Change(Func<RunSession, RunSession> update) => owner.service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == id ? update(p) : p).ToList() });
        private Task ChangeSettings(Func<RunSettings, RunSettings> update) => owner.Act(async () => { if (Session.Status == "running") return; await Change(p => p with { Settings = update(p.Settings) }); Refresh(); input.Focus(FocusState.Programmatic); });
        private Task ChangeModel(string value) => owner.Act(async () => { if (Session.Status == "running") return; if (!Wire.Model(value)) throw new ArgumentException(Locale.Get("composer.model.invalidName")); var pane = Session; var catalog = owner.Runtime(pane.Provider)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider); var registeredModels = RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot); await Change(p => p with { Model = value, Settings = p.Settings with { Effort = ProviderCatalog.Efforts(p.Provider, value, catalog, registeredModels).Contains(p.Settings.Effort) ? p.Settings.Effort : "default" } }); Refresh(); input.Focus(FocusState.Programmatic); });
        private static MenuFlyoutItem Item(string text, Func<Task> action, string? help = null)
        {
            var item = new MenuFlyoutItem { Text = text }; item.Click += async (_, _) => await action(); if (help is not null) ToolTipService.SetToolTip(item, help); return item;
        }
        /// <summary>
        /// A row of a choice menu, with the standard check mark on the current choice as the Mac's menus show it
        /// (M/SessionPaneView.swift:331-347). A click flips a toggle item's mark by itself; the mark is the
        /// setting's, so it is put back, and the menu is built again when the setting changes.
        /// </summary>
        private static ToggleMenuFlyoutItem Choice(string text, Func<Task> action, bool selected, string? help = null)
        {
            var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = selected };
            item.Click += async (_, _) => { item.IsChecked = selected; await action(); };
            if (help is not null) ToolTipService.SetToolTip(item, help);
            return item;
        }
        /// <summary>A section header in a menu (the Mac's <c>Section("공급자")</c>): a small quiet line over its rows that cannot be chosen.</summary>
        private MenuFlyoutItem MenuHeader(string text)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = false, FontSize = DesignMetrics.Type.Pill, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, MinHeight = 0, Padding = new Thickness(11, 7, 11, 3) };
            owner.SetResourcesOnce(item, [("MenuFlyoutItemForegroundDisabled", owner.brushes.Brush(DesignToken.Ink3))]);
            return item;
        }
        /// <summary>
        /// A permission mode's name (M/SettingsViews.swift:341-346): the Claude CLI's own mode names for Claude, in
        /// every language as on the Mac, and the shared labels for the other agents.
        /// </summary>
        private static string PermissionLabel(string provider, string mode)
        {
            if (provider == "claude") return mode switch { "plan" => "Plan mode", "acceptEdits" => "Accept file edits", "auto" => "Auto mode", "fullAccess" => "Bypass", _ => "Always ask" };
            return mode switch
            {
                "plan" => Locale.Get("permission.label.plan"), "onRequest" => Locale.Get("permission.label.onRequest"), "fullAccess" => Locale.Get("permission.label.fullAccess"),
                "acceptEdits" => Locale.Get(provider == "codex" ? "permission.label.acceptEditsCodex" : "permission.label.acceptEdits"),
                _ => Locale.Get(provider == "codex" ? "permission.label.defaultCodex" : "permission.label.default"),
            };
        }
        /// <summary>A thinking effort's name on its pill and in its menu: the CLI's own level names (M/SessionPaneView.swift:899-901).</summary>
        private static string EffortLabel(string effort) => effort switch { "low" => "Low", "medium" => "Medium", "high" => "High", "xhigh" => "XHigh", "max" => "Max", _ => "Auto" };
        private static string PermissionHelp(string provider, string mode) => mode switch { "onRequest" => Locale.Get("permission.codex.onRequest"), "manual" => provider == "codex" ? Locale.Get("composer.permissionHelp.manualCodex") : Locale.Get("composer.permissionHelp.manual"), "plan" => Locale.Get("composer.permissionHelp.plan"), "acceptEdits" => provider == "codex" ? Locale.Get("composer.permissionHelp.acceptEditsCodex") : Locale.Get("composer.permissionHelp.acceptEdits"), "auto" => Locale.Get("composer.permissionHelp.auto"), "fullAccess" => Locale.Get("composer.permissionHelp.fullAccess"), _ => "" };
        /// Model names registered in saved state, for the effort list. Workspace
        /// entries come first and an app entry whose name is already present is
        /// skipped. Nothing here resolves a model — the pane's own model is used
        /// as-is, and "default" means the CLI decides.
        private static IReadOnlyList<RegisteredModelEntry> RegisteredModelsFor(string provider, Workspace workspace, AppSnapshot snapshot)
        {
            static IReadOnlyList<RegisteredModelEntry> Entries(string kind, ModelDefaultsConfig? config)
            {
                if (config is null) return Array.Empty<RegisteredModelEntry>();
                return kind == "codex" ? config.Codex.RegisteredModels : config.Claude.RegisteredModels;
            }
            var fromWorkspace = Entries(provider, workspace.ModelDefaults);
            var fromApp = Entries(provider, snapshot.ModelDefaults);
            if (fromApp.Count == 0) return fromWorkspace;
            if (fromWorkspace.Count == 0) return fromApp;
            var names = fromWorkspace.Select(entry => entry.Name).ToHashSet();
            return fromWorkspace.Concat(fromApp.Where(entry => !names.Contains(entry.Name))).ToList().AsReadOnly();
        }
        /// <summary>The provider mark on the model pill: 12 on the Mac, which draws it at 12 × 1.15 (M/ComposerControls.swift:16, M/ProviderIcon.swift:18).</summary>
        private const double ModelMarkSize = 13.8;
        /// <summary>The permission pill's symbol now: the open lock while everything is allowed, else the half shield (M/SessionPaneView.swift:398).</summary>
        private bool? permissionUnlocked;

        /// <summary>
        /// Brings the pills and their menus up to the pane (M/SessionPaneView.swift:326-450): the model pill with
        /// its provider's mark, effort, permission, Fast, the … pill (on while a run setting is set) and the
        /// options menu a narrow pane shows instead. Which of them show is <see cref="ArrangeComposer"/>'s.
        /// </summary>
        private void RefreshMenus(RunSession pane, ModelCatalog catalog)
        {
            var caps = Capabilities;
            if (pillProvider != pane.Provider)
            {
                pillProvider = pane.Provider; var parts = Parts(model);
                var mark = ProviderMarkView.Create(pane.Provider, ModelMarkSize); mark.HorizontalAlignment = HorizontalAlignment.Center;
                parts.IconHost.Children.Clear(); parts.IconHost.Children.Add(mark); parts.IconHost.Visibility = Visibility.Visible;
            }
            var selection = ModelLabel.Selection(pane, catalog); var selectedModel = catalog.Models.FirstOrDefault(m => m.Value == pane.Model);
            Label(model, selection, Locale.Get("composer.label.model"));
            ToolTipService.SetToolTip(model, ProviderCatalog.BetaLabel(pane.Provider, ProviderMark.Label(pane.Provider)) + " · " + selection + (selectedModel?.Description is { Length: > 0 } description ? "\n" + description : ""));
            model.Flyout = ModelMenu(pane, catalog);

            var levels = ProviderCatalog.Efforts(pane.Provider, pane.Model, catalog, RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot));
            Label(effort, EffortLabel(pane.Settings.Effort), Locale.Get("composer.effort.label"));
            if (pane.Settings.Effort != "default" && !levels.Contains(pane.Settings.Effort)) AutomationProperties.SetName(effort, AutomationProperties.GetName(effort) + Locale.Get("composer.effort.unverifiedSuffix"));
            // What the pill does, or why it is off (M/SessionPaneView.swift:377).
            ToolTipService.SetToolTip(effort, levels.Length == 0 ? Locale.Get("composer.effort.unknownLevels") : Locale.Get("composer.effort.label") + " · " + Locale.Get("settings.run.appliesNextRequest"));
            effort.Flyout = ChoiceMenu(EffortChoices(pane, levels));

            var unlocked = pane.Settings.PermissionMode == "fullAccess";
            Label(permission, PermissionLabel(pane.Provider, pane.Settings.PermissionMode), Locale.Get("composer.label.permission"));
            ToolTipService.SetToolTip(permission, PermissionHelp(pane.Provider, pane.Settings.PermissionMode));
            AutomationProperties.SetHelpText(permission, unlocked ? Locale.Get("composer.hint.fullAccess") : "");
            permission.Flyout = ChoiceMenu(PermissionChoices(pane, caps));
            if (permissionUnlocked != unlocked) { permissionUnlocked = unlocked; SetPillIcon(permission, unlocked ? ComposerGlyph.Unlock() : ComposerGlyph.Shield()); }
            PaintPill(permission, unlocked);

            fast.IsChecked = pane.Settings.FastMode;
            // The … pill is on while a run setting is set (M/SessionPaneView.swift:417-419); its own menu keeps the pane's
            // actions the Mac has in the pane header (rename, plugins, a new conversation).
            AutomationProperties.SetName(more, Locale.Get("composer.more")); ToolTipService.SetToolTip(more, Locale.Get("settings.run.title"));
            PaintPill(more, pane.Settings.WebSearch != "default" || pane.Settings.NetworkAccess || pane.Settings.MaxTurns is not null || pane.Settings.MaxBudgetUsd is not null);
            more.ContextFlyout = PaneActionsMenu(pane); options.ContextFlyout = PaneActionsMenu(pane);
            var optionsName = Locale.Get("composer.effort.label") + " · " + Locale.Get("composer.label.permission") + " · " + Locale.Get("settings.run.title");
            AutomationProperties.SetName(options, optionsName); ToolTipService.SetToolTip(options, optionsName);
            options.Flyout = OptionsMenu(pane, caps, levels);
            PaintPill(options, unlocked || pane.Settings.FastMode);
        }

        private static MenuFlyout ChoiceMenu(IEnumerable<MenuFlyoutItemBase> rows)
        {
            var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft };
            foreach (var row in rows) menu.Items.Add(row);
            return menu;
        }

        /// <summary>
        /// The model pill's menu (M/SessionPaneView.swift:326-350): the providers under their header, the model list's
        /// refresh, then the models under theirs, the current provider and model checked. Entering a model ID by hand
        /// stays at the end, where Windows had it.
        /// </summary>
        private MenuFlyout ModelMenu(RunSession pane, ModelCatalog catalog)
        {
            var rows = new List<MenuFlyoutItemBase> { MenuHeader(Locale.Get("composer.label.runner")) };
            foreach (var value in Wire.Providers) rows.Add(Choice(ProviderCatalog.BetaLabel(value, ProviderMark.Label(value)), () => ChangeProvider(value), pane.Provider == value));
            rows.Add(new MenuFlyoutSeparator());
            var refresh = Item(Locale.Get(refreshingPaneModels ? "composer.model.refreshing" : "composer.model.refresh"), RefreshPaneModels); refresh.IsEnabled = !refreshingPaneModels;
            rows.Add(refresh); rows.Add(new MenuFlyoutSeparator());
            rows.Add(MenuHeader(Locale.Get("composer.label.model")));
            foreach (var row in ModelLabel.PickerOptions(pane, catalog)) rows.Add(Choice(row.DisplayName, () => ChangeModel(row.Value), pane.Model == row.Value, row.Description.Length > 0 ? row.Description : null));
            if (pane.Provider != "gemini") { rows.Add(new MenuFlyoutSeparator()); rows.Add(Item(Locale.Get("composer.model.enterIdMenu"), CustomModel)); }
            return ChoiceMenu(rows);
        }

        /// <summary>Auto, then the levels the model takes, the current one checked (M/SessionPaneView.swift:359-366).</summary>
        private IEnumerable<MenuFlyoutItemBase> EffortChoices(RunSession pane, string[] levels) =>
            new[] { "default" }.Concat(levels).Select(value => (MenuFlyoutItemBase)Choice(EffortLabel(value), () => ChangeSettings(s => s with { Effort = value }), pane.Settings.Effort == value, value == "default" ? Locale.Get("composer.effort.auto") : null)).ToList();

        /// <summary>The permission modes this runner takes, in the Mac's order (M/SessionPaneView.swift:385-392).</summary>
        private IEnumerable<MenuFlyoutItemBase> PermissionChoices(RunSession pane, ProviderCapabilities caps)
        {
            var offered = (caps.PermissionModes ?? []).Where(ProviderCatalog.PermissionModes(pane.Provider).Contains).ToHashSet();
            return new[] { "plan", "manual", "acceptEdits", "auto", "onRequest", "fullAccess" }.Where(offered.Contains).Select(mode => (MenuFlyoutItemBase)Choice(PermissionLabel(pane.Provider, mode),
                () => ChangeSettings(s => s with { PermissionMode = mode, NetworkAccess = pane.Provider == "codex" && mode is ("acceptEdits" or "onRequest") && s.NetworkAccess }), pane.Settings.PermissionMode == mode, PermissionHelp(pane.Provider, mode))).ToList();
        }

        /// <summary>
        /// The options menu of a narrow pane (M/SessionPaneView.swift:428-450): effort and permission as submenus
        /// that say the current choice, Fast for Codex, then the run settings.
        /// </summary>
        private MenuFlyout OptionsMenu(RunSession pane, ProviderCapabilities caps, string[] levels)
        {
            var rows = new List<MenuFlyoutItemBase>();
            if (caps.Effort || pane.Settings.Effort != "default")
            {
                var efforts = new MenuFlyoutSubItem { Text = Locale.Get("composer.effort.label") + " · " + EffortLabel(pane.Settings.Effort), IsEnabled = levels.Length > 0 || pane.Settings.Effort != "default" };
                foreach (var row in EffortChoices(pane, levels)) efforts.Items.Add(row);
                rows.Add(efforts);
            }
            var permissions = new MenuFlyoutSubItem { Text = Locale.Get("composer.label.permission") + " · " + PermissionLabel(pane.Provider, pane.Settings.PermissionMode) };
            foreach (var row in PermissionChoices(pane, caps)) permissions.Items.Add(row);
            rows.Add(permissions);
            if (pane.Provider == "codex" && (caps.FastMode || pane.Settings.FastMode))
            {
                var quick = Choice(Locale.Get(pane.Settings.FastMode ? "composer.fast.on" : "composer.fast.off"), () => ChangeSettings(s => s with { FastMode = !s.FastMode && Capabilities.FastMode }), pane.Settings.FastMode, Locale.Get("composer.fast.tooltip"));
                quick.IsEnabled = caps.FastMode || pane.Settings.FastMode; rows.Add(quick);
            }
            rows.Add(new MenuFlyoutSeparator());
            rows.Add(Item(Locale.Get("composer.runSettings.more"), () => ShowRunSettings(options)));
            return ChoiceMenu(rows);
        }

        /// <summary>
        /// The pane's own actions that the composer's … menu used to list: rename, the plugin window and a new
        /// conversation. The Mac keeps them in the pane header; here they stay reachable from the … pill's
        /// context menu, since its click now opens the run settings as on the Mac.
        /// </summary>
        private MenuFlyout PaneActionsMenu(RunSession pane)
        {
            var menu = new MenuFlyout();
            var rename = Item(RenameStrings.MenuEntry, () => owner.RenameSession(id));
            menu.Items.Add(rename);
            // The plugin window, the same one /plugin and /plugins open.
            // Claude and Codex both have one; Gemini has none on macOS either.
            MenuFlyoutItem? plugins = null;
            if (pane.Kind == "claude" && pane.Provider is "claude" or "codex")
            {
                plugins = Item(PluginStrings.TitleTemplate.Replace("{provider}", CliUpdateService.ProviderLabel(pane.Provider)),
                    () => owner.OpenPluginBrowser(pane.Provider));
                menu.Items.Add(plugins);
            }
            menu.Opening += (_, _) => { rename.IsEnabled = !owner.dialogOpen; if (plugins is not null) plugins.IsEnabled = !owner.dialogOpen; };
            if (pane.Kind == "claude")
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(Item(Locale.Get("composer.newConversation"), () => owner.Act(ResetConversation)));
            }
            return menu;
        }
        internal void Refresh()
        {
            // A files pane runs nothing: it draws its tree and preview instead (MainWindow.Files.cs).
            if (FilePaneKind.IsFilePane(Session.Kind)) { EnsureFilesView(); RethemeFilesMarkdown(); return; }
            RefreshTerminalTheme(); RethemeMightyTranscripts(owner.service.Snapshot.Theme == "light"); RethemePlanViews(); FitReferencePreview();
            var pane = Session; updating = true; var runtime = owner.Runtime(pane.Provider); var catalog = runtime?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider);
            var state = owner.service.Snapshot; RefreshHeaderStatus(pane, state.Theme != "light"); output.Update(pane, state.Theme == "light", owner.pictures, state.Workspaces.FirstOrDefault(w => w.Id == pane.WorkspaceId)?.Path); RefreshEmptyOutput(pane); RefreshElapsed(pane); RefreshPlanHistory(pane); RedecidePlanPlace();
            // Do not rewrite or recreate the editor during output/metadata refreshes.
            if (!draftLoaded) { input.Text = pane.Draft; draftLoaded = true; RefreshPalette(input.Text); }
            RefreshMenus(pane, catalog);
            detail.Text = Locale.Get("workspace.location.thisComputer") + (pane.Kind == "shell" ? " · " + Locale.Get("composer.detail.shell") : " · " + (catalog.Source == "cli" ? Locale.Get("composer.detail.modelsFromCli") : Locale.Get("composer.detail.defaultModels")) + (pane.ResumeId is null ? "" : " · " + Locale.Get("composer.detail.resuming")));
            RefreshComposerState(); ArrangeComposer();
            RequestStatusLineRefresh();
            RenderLoginRecovery();
            updating = false;
        }
        private Task CustomModel() => owner.Act(async () =>
        {
            if (Session.Status == "running") return; var field = new TextBox { Header = Locale.Get("composer.model.idHeader"), Text = Session.Model }; var validation = new TextBlock { TextWrapping = TextWrapping.Wrap }; var content = new StackPanel { Spacing = 8 }; content.Children.Add(field); content.Children.Add(validation);
            var dialog = owner.StyledDialog(new ContentDialog { Title = Locale.Get("composer.model.enterIdTitle"), Content = content, XamlRoot = owner.root.XamlRoot, PrimaryButtonText = Locale.Get("composer.model.select"), CloseButtonText = Locale.Get("settings.run.cancelButton") });
            dialog.PrimaryButtonClick += (_, args) => { if (!Wire.Model(field.Text.Trim())) { validation.Text = Locale.Get("composer.model.invalidName"); args.Cancel = true; } };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ChangeModel(field.Text.Trim());
        });
    }
}
