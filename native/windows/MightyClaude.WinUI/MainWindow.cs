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
    private readonly Grid root = new() { Padding = new Thickness(12), ColumnSpacing = 12, RowSpacing = 8, Background = WindowBackground(false) };
    private static SolidColorBrush WindowBackground(bool light) => new(light
        ? Windows.UI.Color.FromArgb(255, 245, 246, 249)
        : Windows.UI.Color.FromArgb(255, 24, 26, 31));
    private readonly StackPanel sidebar = new() { Spacing = 10 };
    private readonly Grid panes = new() { ColumnSpacing = 12, RowSpacing = 12 };
    private readonly ListView workspaces = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBox search = new() { Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBlock sessionsHeader = new() { FontSize = 11, Opacity = .6, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button addFolderButton, settingsButton;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Opacity = .75 };
    private readonly TextBlock error = new() { Foreground = new SolidColorBrush(Colors.OrangeRed), TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox layout = new() { Width = 105 };
    private readonly Dictionary<string, PaneView> views = [];
    private RuntimeInfo? runtime;
    private bool rendering, canClose, closing;
    private readonly StartupOptions options;
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CliUpdateService cliUpdateService;
    internal readonly CliUpdateCoordinator coordinator;
    // Reads who each CLI is signed in as when the CLI 계정 section opens and
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
        Title = "MightyClaude"; AppWindow.Resize(new SizeInt32(1440, 920));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "MightyClaude.ico"));
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var legacy = new[] { "MightyClaude", "mighty-claude" }.Select(name => Path.Combine(appData, name)).FirstOrDefault(path => File.Exists(Path.Combine(path, "workspace-state.json")));
        service = new(options.ProfileDirectory ?? Path.Combine(appData, "MightyClaudeNative"), options.ProfileDirectory is null ? legacy : null, Path.Combine(AppContext.BaseDirectory, "claude-mods"));
        cliUpdateService = new(new CliRunner());
        coordinator = new((provider, token) =>
        {
            var fn = smokeCliUpdater;
            return fn is not null ? fn(provider, token) : cliUpdateService.UpdateAsync(provider, token);
        });
        coordinator.StateChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (!coordinator.IsUpdating && coordinator.Results.Count > 0)
                lastCliUpdateResults = coordinator.Results;
        });
        service.RunEventReceived += value => DispatcherQueue.TryEnqueue(() => { if (closing) return; if (views.TryGetValue(value.SessionId, out var pane)) { pane.Refresh(); if (value.Type == "status" && value.Status is "stopped" or "completed" or "error") pane.ClearToolPermissions(); } RefreshRunningIndicators(); HandleRunEventForNotification(value); });
        // Claude's extra tool-permission requests never travel as a RunEvent:
        // they are ephemeral, so they reach the pane that can show the bar and
        // nowhere else — not the snapshot.
        service.ToolPermissionChanged += value => DispatcherQueue.TryEnqueue(() => { if (closing) return; if (views.TryGetValue(value.RunId, out var pane)) pane.ReceiveToolPermission(value); });
        service.PersistenceFailed += ex => DispatcherQueue.TryEnqueue(() => error.Text = Locale.Get("window.error.saveFailed", new Dictionary<string, string> { ["reason"] = ex.Message }));
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(252) }); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var brandImage = new Image { Source = new BitmapImage(new Uri("ms-appx:///Assets/mightyclaude.png")), Width = 28, Height = 28 };
        AutomationProperties.SetAccessibilityView(brandImage, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        brand.Children.Add(brandImage); brand.Children.Add(new TextBlock { Text = "MightyClaude", FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }); sidebar.Children.Add(brand);
        foreach (var name in new[] { "grid", "columns", "focus", "tabs", "custom" }) layout.Items.Add(new ComboBoxItem { Tag = name });
        layout.SelectionChanged += async (_, _) => { if (!rendering && layout.SelectedItem is ComboBoxItem item) await ApplyLayoutPreset((string)item.Tag); };
        addFolderButton = Button("", PickFolder); sidebar.Children.Add(search); sidebar.Children.Add(addFolderButton); sidebar.Children.Add(workspaces);
        sidebar.Children.Add(sessionsHeader); sidebar.Children.Add(sessionLinks);
        var sideHost = new Grid { RowSpacing = 10 }; sideHost.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); sideHost.RowDefinitions.Add(new() { Height = GridLength.Auto });
        sideHost.Children.Add(new ScrollViewer { Content = sidebar, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled });
        var navigation = new StackPanel { Spacing = 6 }; navigation.Children.Add(layout);
        settingsButton = Button("", OpenSettings); navigation.Children.Add(settingsButton); ApplyChromeText(); Grid.SetRow(navigation, 1); sideHost.Children.Add(navigation);
        search.TextChanged += (_, _) => RenderSidebar();
        workspaces.SelectionChanged += async (_, _) => { if (!rendering && workspaces.SelectedItem is ListViewItem { Tag: string id }) await SelectWorkspace(id); };
        Grid.SetRow(sideHost, 1); root.Children.Add(sideHost); Grid.SetRow(panes, 1); Grid.SetColumn(panes, 1); root.Children.Add(panes);
        var footer = new StackPanel { Spacing = 3 }; footer.Children.Add(error);
        // The bottom status bar: the account usage chips, then the status text.
        var statusRow = new Grid { ColumnSpacing = 10 };
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusRow.Children.Add(BuildAccountUsage()); Grid.SetColumn(status, 1); statusRow.Children.Add(status);
        footer.Children.Add(statusRow); Grid.SetRow(footer, 2); Grid.SetColumnSpan(footer, 2); root.Children.Add(footer); Content = root;
        AppWindow.Closing += async (_, args) => { if (canClose) return; args.Cancel = true; if (closing) return; closing = true; clock.Stop(); root.IsHitTestVisible = false; try { await coordinator.ShutdownAsync(); await ShutdownAppUpdateAsync(); await ShutdownAccountUsageAsync(); await service.DisposeAsync(); canClose = true; Close(); } catch (Exception ex) { error.Text = Locale.Get("window.error.shutdownFailed", new Dictionary<string, string> { ["reason"] = ex.Message }); root.IsHitTestVisible = true; closing = false; } };
        clock.Tick += (_, _) => RefreshRunningIndicators(); clock.Start();
        _ = Initialize();
    }
    private async Task Initialize()
    {
        if (!options.SmokeTest) { await Act(async () => { await service.InitializeAsync(); Locale.LanguagePreference = service.Snapshot.LanguagePreference; ApplyChromeText(); Render(); await InitNotifierAsync(); await RefreshRuntime(); coordinator.BeginAutomaticIfNeeded(service.Snapshot); BeginAutomaticAppUpdateCheck(); }); return; }
        try { await service.InitializeAsync(); Locale.LanguagePreference = service.Snapshot.LanguagePreference; ApplyChromeText(); Render(); await RunUISmoke(); }
        catch (Exception ex) { options.WriteStartupFailure(ex); await FinishSmoke(false); }
    }
    // The sidebar chrome is built before Initialize reads the saved language,
    // so its text is set again once the preference is applied.
    private void ApplyChromeText()
    {
        search.PlaceholderText = Locale.Get("sidebar.searchPlaceholder");
        foreach (var item in layout.Items.OfType<ComboBoxItem>())
            item.Content = Locale.Get((string)item.Tag switch { "grid" => "layout.mode.grid", "columns" => "layout.mode.columns", "focus" => "layout.mode.focus", "tabs" => "layout.mode.tabs", _ => "layout.mode.custom" });
        addFolderButton.Content = Locale.Get("sidebar.addProjectFolder"); AutomationProperties.SetName(addFolderButton, (string)addFolderButton.Content);
        sessionsHeader.Text = Locale.Get("sidebar.sessionsHeader");
        settingsButton.Content = Locale.Get("settings.settingsWindowTitle"); AutomationProperties.SetName(settingsButton, (string)settingsButton.Content);
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
        await Act(async () => { var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this)); if (await picker.PickSingleFolderAsync() is { } folder) { await service.AddWorkspaceAsync(folder.Path); Render(); } });
    }
    private async Task RemoveWorkspace() { await Act(async () => { if (service.Snapshot.ActiveWorkspaceId is { } id) { await service.RemoveWorkspaceAsync(id); Render(); } }); }
    private async Task AddPane(string kind, string provider = "claude", string? groupId = null)
    {
        await Act(async () => { var workspace = service.Snapshot.ActiveWorkspaceId ?? throw new InvalidOperationException(Locale.Get("window.error.addWorkspaceFirst")); var pane = new RunSession { WorkspaceId = workspace, Kind = kind, Provider = provider, Title = kind == "shell" ? Locale.Get("session.title.shell") : ProviderCatalog.Name(provider) }; await service.UpdateAsync(s => { var added = s with { Sessions = s.Sessions.Append(pane).ToList(), ActiveSessionId = pane.Id }; var tree = EffectiveLayout(added, workspace); if (tree is not null && groupId is not null) tree = PaneLayout.Move(tree, pane.Id, groupId); return SaveLayoutSelection(SaveLayout(added, workspace, tree), workspace, pane.Id); }); Render(); });
    }
    private async Task RefreshRuntime() { await Act(async () => { status.Text = Locale.Get("window.status.checkingRuntimeAndModels"); runtime = await service.Providers.GetRuntimeAsync(true); RefreshEnvironment(); }); }
    private void RefreshEnvironment()
    {
        foreach (var pane in views.Values) pane.Refresh();
        status.Text = runtime is null ? Locale.Get("window.status.checkingRuntime") : string.Join("   ·   ", runtime.Providers.Select(p => $"{p.Name}: {(p.Available ? p.Version : p.Detail)}"));
    }
    private ProviderRuntime? Runtime(string provider) => runtime?.Providers.FirstOrDefault(p => p.Id == provider);
    private void RenderSidebar()
    {
        var previous = rendering; rendering = true;
        var state = service.Snapshot; workspaces.Items.Clear();
        foreach (var workspace in state.Workspaces.Where(w => (w.Name + w.Path).Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
        {
            var label = new StackPanel { Spacing = 3 }; label.Children.Add(new TextBlock { Text = workspace.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }); label.Children.Add(new TextBlock { Text = Locale.Get("workspace.location.thisComputer"), FontSize = 11, Opacity = .65 });
            var item = new ListViewItem { Content = label, Tag = workspace.Id, ContextFlyout = WorkspaceMenu(workspace.Id) }; ToolTipService.SetToolTip(item, workspace.Path); workspaces.Items.Add(item); if (workspace.Id == state.ActiveWorkspaceId) workspaces.SelectedItem = item;
        }
        sessionLinks.Children.Clear(); sessionIndicators.Clear();
        foreach (var session in state.Sessions.Where(s => s.WorkspaceId == state.ActiveWorkspaceId))
        {
            var button = Button(session.Title, () => SelectLayoutSession(session.Id)); button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Content = SessionIndicator(session); button.ContextFlyout = SessionMenu(session.Id); sessionLinks.Children.Add(button);
        }
        rendering = previous;
    }
    private void Render()
    {
        if (closing) return; rendering = true; var state = service.Snapshot;
        root.RequestedTheme = state.Theme == "light" ? ElementTheme.Light : ElementTheme.Dark;
        root.Background = WindowBackground(state.Theme == "light"); root.ColumnDefinitions[0].Width = new GridLength(state.SidebarWidth);
        layout.SelectedItem = layout.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == LayoutMode(state, state.ActiveWorkspaceId)); RenderSidebar();
        RenderAccountUsage();
        DetachPaneViews(); panes.Children.Clear(); panes.RowDefinitions.Clear(); panes.ColumnDefinitions.Clear();
        // A closed session runs nothing more: end its refresher before dropping the pane.
        foreach (var stale in views.Keys.Where(id => !state.Sessions.Any(s => s.Id == id)).ToArray()) { views[stale].Refresher?.Close(); views.Remove(stale); }
        RenderPaneLayout(state);
        status.Text = runtime is null ? Locale.Get("window.status.checkingRuntime") : string.Join("   ·   ", runtime.Providers.Select(p => $"{p.Name}: {(p.Available ? p.Version : p.Detail)}"));
        rendering = false;
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
    private sealed partial class PaneView
    {
        private static string InputShortcuts => Locale.Get("composer.inputShortcuts");
        private readonly MainWindow owner;
        private readonly string id;
        private readonly TextBlock label = new() { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock detail = new() { FontSize = 10, Opacity = .6, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock inputHint = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock permissionHint = new() { FontSize = 11, Opacity = .75, TextWrapping = TextWrapping.Wrap };
        private readonly AgentTranscript output = new();
        private readonly TextBlock elapsed = new() { FontSize = 11, Opacity = .65, VerticalAlignment = VerticalAlignment.Center };
        // Auto height uses the native text layout, including soft wraps and IME
        // composition. Start with one line and scroll internally at the cap.
        private readonly TextBox input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 30, MaxHeight = 140, MaxLength = 100000, PlaceholderText = Locale.Get("composer.placeholder.idle"), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), Padding = new Thickness(4, 5, 4, 5) };
        private readonly Button provider = Pill(100), model = Pill(180), effort = Pill(125), permission = Pill(135), more = Pill(40);
        private readonly Microsoft.UI.Xaml.Controls.Primitives.ToggleButton fast = new() { Content = "ϟ Fast", MinWidth = 0, Padding = new Thickness(10, 5, 10, 5), CornerRadius = new CornerRadius(16), FontSize = 11, MinHeight = 32, Height = 32 };
        private readonly Button send, attach, context;
        private readonly Grid selectors = new() { ColumnSpacing = 3, Height = 32, VerticalAlignment = VerticalAlignment.Center };
        private readonly PillWrapPanel attachmentChips = new();
        private readonly List<RunAttachment> pendingAttachments = [];
        private bool updating, draftLoaded, attachmentsLoading, composingInput, suppressCompositionEnter, starting, stopping, canSend;
        private int composerMode;
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
        private static Button Pill(double maxWidth) => new() { MinWidth = 0, MaxWidth = maxWidth, MinHeight = 32, Height = 32, Padding = new Thickness(7, 0, 7, 0), CornerRadius = new CornerRadius(16), FontSize = 11, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
        private static void Label(Button button, string text, string name)
        {
            button.Content = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 }; AutomationProperties.SetName(button, name + ": " + text);
        }
        internal PaneView(MainWindow owner, string id)
        {
            this.owner = owner; this.id = id;
            InitSlashPalette(); InitPermissionBar();
            var grid = new Grid { Padding = new Thickness(12), RowSpacing = 8 };
            foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) grid.RowDefinitions.Add(new RowDefinition { Height = height });
            var header = new Grid { ColumnSpacing = 8 }; header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var state = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; state.Children.Add(label); state.Children.Add(elapsed); header.Children.Add(state);
            var copy = Button(Locale.Get("pane.copyButton"), () => { Copy(output.Text); return Task.CompletedTask; }); copy.Height = 28; copy.MinHeight = 0; copy.Padding = new(8, 0, 8, 0); Grid.SetColumn(copy, 2); header.Children.Add(copy); grid.Children.Add(header);
            Grid.SetRow(output.View, 1); grid.Children.Add(output.View);
            ScrollViewer.SetVerticalScrollBarVisibility(input, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(input, ScrollBarVisibility.Disabled);
            attach = Button("+", PickAttachments); attach.MinWidth = 0; attach.Width = attach.Height = 32; attach.Padding = new Thickness(5); attach.CornerRadius = new CornerRadius(16); attach.Content = new SymbolIcon(Symbol.Attach); AutomationProperties.SetName(attach, Locale.Get("composer.attach.name")); ToolTipService.SetToolTip(attach, Locale.Get("composer.attach.tooltip"));
            var controls = new FrameworkElement[] { attach, provider, model, effort, permission, fast, more };
            for (var index = 0; index < controls.Length; index++) { selectors.ColumnDefinitions.Add(new() { Width = index == 2 ? new(1, GridUnitType.Star) : GridLength.Auto }); Grid.SetColumn(controls[index], index); controls[index].VerticalAlignment = VerticalAlignment.Center; selectors.Children.Add(controls[index]); }
            model.HorizontalAlignment = HorizontalAlignment.Stretch; model.HorizontalContentAlignment = HorizontalAlignment.Left; model.MaxWidth = double.PositiveInfinity; model.MinWidth = 0;
            selectors.SizeChanged += (_, _) => ArrangeComposer();
            var bottom = new Grid { ColumnSpacing = 5, Height = 32 }; bottom.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); bottom.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); bottom.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); bottom.Children.Add(selectors);
            context = Button("—", ShowContext); context.Width = 44; context.Height = 32; context.MinWidth = 0; context.Padding = new(2, 0, 2, 0); context.CornerRadius = new(16); context.FontSize = 10; context.Background = new SolidColorBrush(Colors.Transparent); AutomationProperties.SetName(context, Locale.Get("composer.context.name")); Grid.SetColumn(context, 1); bottom.Children.Add(context);
            send = Button("↑", PrimaryAction); send.Width = send.Height = 32; send.MinWidth = 0; send.Padding = new Thickness(0); send.CornerRadius = new CornerRadius(16); send.FontSize = 20; send.Background = new SolidColorBrush(Colors.CornflowerBlue); send.Foreground = new SolidColorBrush(Colors.Black); AutomationProperties.SetName(send, Locale.Get("composer.send.name")); Grid.SetColumn(send, 2); bottom.Children.Add(send);
            var composer = new StackPanel { Spacing = 7 }; attachmentChips.Visibility = Visibility.Collapsed; composer.Children.Add(toolPermissionHost); composer.Children.Add(attachmentChips); composer.Children.Add(slashPaletteHost); composer.Children.Add(input); composer.Children.Add(bottom); composer.Children.Add(permissionHint); composer.Children.Add(inputHint); composer.Children.Add(statusLineHost);
            var card = new Border { Child = composer, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(75, 135, 135, 135)), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(12, 135, 135, 135)), Padding = new Thickness(10, 2, 10, 10) };
            var composerScroll = new ScrollViewer { Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollMode = ScrollMode.Auto };
            Grid.SetRow(composerScroll, 2); grid.Children.Add(composerScroll);
            Container = new Border { Child = grid, BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Colors.Gray), CornerRadius = new CornerRadius(10) };
            Container.SizeChanged += (_, args) => composerScroll.MaxHeight = Math.Max(150, args.NewSize.Height - 144);
            fast.Click += async (_, _) => { if (!updating) await ChangeSettings(s => s with { FastMode = !s.FastMode && Capabilities.FastMode }); };
            ToolTipService.SetToolTip(fast, Locale.Get("composer.fast.tooltip")); AutomationProperties.SetName(fast, "Codex Fast");
            ToolTipService.SetToolTip(input, InputShortcuts);
            input.TextChanged += async (_, _) => { if (!updating) { var draft = input.Text; RefreshComposerState(); RefreshPalette(draft); await owner.Act(() => Change(p => p with { Draft = draft })); } };
            input.TextCompositionStarted += (_, _) => composingInput = true;
            input.TextCompositionEnded += (_, _) =>
            {
                composingInput = false;
                // Some IMEs commit before routing the same Enter to the control.
                // That key completes composition; only a later press may send.
                suppressCompositionEnter = IsInputKeyDown(Windows.System.VirtualKey.Enter);
            };
            input.PreviewKeyUp += (_, _) => { if (!IsInputKeyDown(Windows.System.VirtualKey.Enter)) suppressCompositionEnter = false; };
            input.PreviewKeyDown += async (_, args) =>
            {
                if (args.Handled) return;
                if (paletteState.IsOpen && HandlePaletteKey(args.Key)) { args.Handled = true; return; }
                if (args.Key == Windows.System.VirtualKey.Enter)
                {
                    if (!SubmitKeyAllowed(composingInput, suppressCompositionEnter, IsInputKeyDown(Windows.System.VirtualKey.Shift), IsInputKeyDown(Windows.System.VirtualKey.Control) || IsInputKeyDown(Windows.System.VirtualKey.Menu) || IsInputKeyDown(Windows.System.VirtualKey.LeftWindows) || IsInputKeyDown(Windows.System.VirtualKey.RightWindows))) return;
                    args.Handled = true;
                    // Send rechecks the same busy/content/capability guards used
                    // by the button. Holding Enter must not submit again.
                    if (!args.KeyStatus.WasKeyDown) await Send();
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
            card.Drop += async (_, args) => { if (!AttachmentInput.ContainsFiles(args.DataView)) return; var deferral = args.GetDeferral(); args.Handled = true; try { if (Session.Kind == "shell") owner.error.Text = Locale.Get("wire.startRun.attachmentAiOnly"); else await LoadAttachments(() => AttachmentInput.ReadDataAsync(args.DataView)); } finally { deferral.Complete(); } };
            AutomationProperties.SetName(input, Locale.Get("composer.input.name"));  AutomationProperties.SetLiveSetting(inputHint, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            input.GotFocus += (_, _) => card.BorderBrush = new SolidColorBrush(Colors.CornflowerBlue);
            input.LostFocus += (_, _) => { composingInput = false; suppressCompositionEnter = false; card.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(75, 135, 135, 135)); };
        }
        private static bool IsInputKeyDown(Windows.System.VirtualKey key) =>
            (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        private static bool SubmitKeyAllowed(bool composing, bool committedOnThisEnter, bool shift, bool otherModifier) => !composing && !committedOnThisEnter && !shift && !otherModifier;
        private Task Send() => owner.Act(async () =>
        {
            RefreshComposerState(); if (!canSend || starting || composingInput) return;
            starting = true; RefreshComposerState();
            try
            {
            var pane = Session; var submitted = input.Text; var files = pendingAttachments.ToArray();
            await owner.StartFromComposer(new StartRunRequest(
                pane.Id, pane.WorkspaceId, pane.Kind, submitted,
                RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot),
                pane.Model, pane.Provider, pane.Settings, pane.ResumeId, files));
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
        private Task PrimaryAction() => Session.Status == "running" || starting ? owner.Act(async () => { if (stopping) return; stopping = true; RefreshComposerState(); try { await owner.service.StopAsync(id); } finally { stopping = false; if (owner.service.Snapshot.Sessions.Any(p => p.Id == id)) Refresh(); } }) : Send();
        private void RefreshComposerState()
        {
            var pane = Session; var busy = pane.Status == "running" || starting; var runtime = owner.Runtime(pane.Provider); var workspace = Workspace;
            var catalog = runtime?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider);
            var registeredModels = RegisteredModelsFor(pane.Provider, workspace, owner.service.Snapshot);
            var unsupportedEffort = pane.Kind == "claude" && pane.Settings.Effort != "default" && !ProviderCatalog.Efforts(pane.Provider, pane.Model, catalog, registeredModels).Contains(pane.Settings.Effort);
            string? unsupportedSettings = null;
            if (pane.Kind == "claude" && pane.Settings.PermissionMode == "auto" && runtime?.Capabilities.PermissionModes?.Contains("auto") != true) unsupportedSettings = Locale.Get("composer.hint.autoModeUnverified");
            if (pendingAttachments.Count > 0 && !Capabilities.Attachments) unsupportedSettings = Locale.Get("composer.hint.attachmentsUnsupported");
            var reason = busy ? ""
                : attachmentsLoading ? Locale.Get("composer.hint.attachmentsLoading")
                : pane.Kind == "claude" && runtime?.Available != true ? Locale.Get("composer.hint.draftStillAllowed", new Dictionary<string, string> { ["reason"] = runtime?.Detail ?? Locale.Get("composer.hint.refreshRuntime") })
                : unsupportedEffort ? Locale.Get("composer.hint.effortUnverified", new Dictionary<string, string> { ["effort"] = pane.Settings.Effort })
                : unsupportedSettings ?? "";
            inputHint.Text = reason; inputHint.Visibility = reason.Length == 0 ? Visibility.Collapsed : Visibility.Visible; AutomationProperties.SetHelpText(input, reason.Length == 0 ? InputShortcuts : reason + " " + InputShortcuts);
            permissionHint.Text = pane.Settings.PermissionMode == "fullAccess" ? Locale.Get("composer.hint.fullAccess") : "";
            permissionHint.Visibility = pane.Kind == "claude" && permissionHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            input.PlaceholderText = busy ? Locale.Get("composer.placeholder.busy") : pane.Kind == "shell" ? Locale.Get("composer.placeholder.shell") : Locale.Get("composer.placeholder.idle");
            canSend = !busy && !attachmentsLoading && (pane.Kind == "shell" || runtime?.Available == true) && !unsupportedEffort && unsupportedSettings is null && (!string.IsNullOrWhiteSpace(input.Text) || pendingAttachments.Count > 0);
            send.IsEnabled = busy ? !stopping : canSend; send.Content = busy ? "■" : "↑"; send.FontSize = busy ? 13 : 20;
            AutomationProperties.SetName(send, busy ? Locale.Get("composer.stop.name") : Locale.Get("composer.send.name")); ToolTipService.SetToolTip(send, busy ? Locale.Get("composer.stop.tooltip") : Locale.Get("composer.send.tooltip"));
            context.Visibility = pane.Kind == "shell" ? Visibility.Collapsed : Visibility.Visible; context.Content = pane.SessionUsage?.ContextPercent is { } percent ? $"{percent:0}%" : "—";
            ToolTipService.SetToolTip(context, pane.SessionUsage?.ContextPercent is null ? Locale.Get("composer.context.unavailable") : Locale.Get("composer.context.tooltip"));
            foreach (var control in selectors.Children.OfType<Control>()) control.IsEnabled = !busy;
            attach.IsEnabled = !attachmentsLoading; attach.Visibility = pane.Kind == "shell" ? Visibility.Collapsed : Visibility.Visible;
        }
        private Task PickAttachments() => LoadAttachments(async () =>
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
            return await AttachmentInput.ReadFilesAsync(await picker.PickMultipleFilesAsync());
        });
        private async Task LoadAttachments(Func<Task<List<RunAttachment>>> read)
        {
            if (attachmentsLoading || Session.Kind == "shell") return;
            attachmentsLoading = true; RefreshComposerState();
            try
            {
                var files = await read();
                if (owner.closing || !owner.service.Snapshot.Sessions.Any(p => p.Id == id)) return;
                _ = AttachmentSupport.Validate(pendingAttachments.Concat(files).ToArray()); pendingAttachments.AddRange(files); RefreshAttachments();
            }
            catch (Exception ex) { owner.error.Text = ex.Message; }
            finally
            {
                attachmentsLoading = false;
                if (!owner.closing && owner.service.Snapshot.Sessions.Any(p => p.Id == id)) { RefreshComposerState(); input.Focus(FocusState.Programmatic); }
            }
        }
        private void RefreshAttachments()
        {
            attachmentChips.Children.Clear(); attachmentChips.Visibility = pendingAttachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (var file in pendingAttachments)
            {
                var row = new Grid { ColumnSpacing = 2 }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var preview = Button(file.Name, () => PreviewAttachment(file)); preview.Content = new TextBlock { Text = (file.MediaType.StartsWith("image/", StringComparison.Ordinal) ? "▧ " : "▤ ") + file.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 }; preview.MinWidth = 0; preview.MaxWidth = 210; preview.Padding = new Thickness(7, 4, 7, 4); preview.Background = new SolidColorBrush(Colors.Transparent); preview.BorderThickness = new Thickness(0); AutomationProperties.SetName(preview, Locale.Get("composer.attachment.preview", new Dictionary<string, string> { ["name"] = file.Name })); ToolTipService.SetToolTip(preview, $"{file.Name} · {AttachmentSupport.DecodedLength(file):N0} bytes"); row.Children.Add(preview);
                if (file.MediaType.StartsWith("image/", StringComparison.Ordinal))
                {
                    var thumbnail = new Image { Width = 28, Height = 28, Stretch = Stretch.Uniform }; var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 }; content.Children.Add(thumbnail); content.Children.Add(new TextBlock { Text = file.Name, MaxWidth = 145, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11, VerticalAlignment = VerticalAlignment.Center }); preview.Content = content; _ = LoadThumbnail(file, thumbnail);
                }
                var remove = Button("×", () => { pendingAttachments.RemoveAll(a => a.Id == file.Id); RefreshAttachments(); RefreshComposerState(); input.Focus(FocusState.Programmatic); return Task.CompletedTask; }); remove.MinWidth = 0; remove.Width = 25; remove.Padding = new Thickness(3); remove.Background = new SolidColorBrush(Colors.Transparent); remove.BorderThickness = new Thickness(0); AutomationProperties.SetName(remove, Locale.Get("composer.attachment.remove", new Dictionary<string, string> { ["name"] = file.Name })); Grid.SetColumn(remove, 1); row.Children.Add(remove);
                attachmentChips.Children.Add(new Border { Child = row, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 135, 135, 135)), MaxWidth = 240 });
            }
        }
        private static async Task LoadThumbnail(RunAttachment file, Image image) { try { image.Source = await AttachmentInput.PreviewAsync(file, 48); } catch (Exception) { image.Visibility = Visibility.Collapsed; } }
        private Task PreviewAttachment(RunAttachment file) => owner.Act(async () =>
        {
            var content = new StackPanel { Spacing = 10, MaxWidth = 640 }; content.Children.Add(new TextBlock { Text = $"{file.MediaType} · {AttachmentSupport.DecodedLength(file):N0} bytes", FontSize = 11 });
            if (file.MediaType.StartsWith("image/", StringComparison.Ordinal)) content.Children.Add(new Image { Source = await AttachmentInput.PreviewAsync(file), MaxHeight = 420, Stretch = Stretch.Uniform });
            else if (file.MediaType == "text/plain") { var text = System.Text.Encoding.UTF8.GetString(AttachmentSupport.Decode(file)); content.Children.Add(new TextBox { Text = text[..Math.Min(text.Length, 20000)], IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 350 }); }
            else content.Children.Add(new TextBlock { Text = Locale.Get("composer.attachment.sentAsFile"), TextWrapping = TextWrapping.Wrap });
            await new ContentDialog { Title = file.Name, Content = content, CloseButtonText = Locale.Get("settings.closeButton"), XamlRoot = owner.root.XamlRoot }.ShowAsync();
        });
        private Task Change(Func<RunSession, RunSession> update) => owner.service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == id ? update(p) : p).ToList() });
        private Task ChangeSettings(Func<RunSettings, RunSettings> update) => owner.Act(async () => { if (Session.Status == "running") return; await Change(p => p with { Settings = update(p.Settings) }); Refresh(); input.Focus(FocusState.Programmatic); });
        private Task ChangeModel(string value) => owner.Act(async () => { if (Session.Status == "running") return; if (!Wire.Model(value)) throw new ArgumentException(Locale.Get("composer.model.invalidName")); var pane = Session; var catalog = owner.Runtime(pane.Provider)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider); var registeredModels = RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot); await Change(p => p with { Model = value, Settings = p.Settings with { Effort = ProviderCatalog.Efforts(p.Provider, value, catalog, registeredModels).Contains(p.Settings.Effort) ? p.Settings.Effort : "default" } }); Refresh(); input.Focus(FocusState.Programmatic); });
        private static MenuFlyoutItem Item(string text, Func<Task> action, bool selected = false, string? help = null)
        {
            var item = new MenuFlyoutItem { Text = (selected ? "✓  " : "") + text }; item.Click += async (_, _) => await action(); if (help is not null) ToolTipService.SetToolTip(item, help); return item;
        }
        private static string PermissionLabel(string provider, string mode) => mode switch { "manual" => provider == "codex" ? Locale.Get("permission.label.defaultCodex") : Locale.Get("permission.label.default"), "plan" => Locale.Get("permission.label.plan"), "acceptEdits" => provider == "codex" ? Locale.Get("composer.permission.acceptEditsCodex") : Locale.Get("composer.permission.acceptEdits"), "auto" => "Auto mode", "fullAccess" => Locale.Get("composer.permission.fullAccess"), _ => mode };
        private static string PermissionHelp(string provider, string mode) => mode switch { "manual" => provider == "codex" ? Locale.Get("composer.permissionHelp.manualCodex") : Locale.Get("composer.permissionHelp.manual"), "plan" => Locale.Get("composer.permissionHelp.plan"), "acceptEdits" => provider == "codex" ? Locale.Get("composer.permissionHelp.acceptEditsCodex") : Locale.Get("composer.permissionHelp.acceptEdits"), "auto" => Locale.Get("composer.permissionHelp.auto"), "fullAccess" => Locale.Get("composer.permissionHelp.fullAccess"), _ => "" };
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
        private void RefreshMenus(RunSession pane, ModelCatalog catalog)
        {
            var caps = Capabilities;
            Label(provider, pane.Provider == "claude" ? "Claude ⌄" : pane.Provider == "codex" ? "Codex ⌄" : "Gemini ⌄", Locale.Get("composer.label.runner"));
            var providers = new MenuFlyout(); foreach (var value in Wire.Providers) providers.Items.Add(Item(ProviderCatalog.Name(value), () => owner.Act(async () => { if (Session.Status == "running" || Session.Provider == value) return; await Change(p => p with { Provider = value, Title = p.Title == ProviderCatalog.Name(p.Provider) ? ProviderCatalog.Name(value) : p.Title, Model = "default", Settings = new(), ResumeId = null }); Refresh(); input.Focus(FocusState.Programmatic); }), pane.Provider == value)); provider.Flyout = providers;
            var selectedModel = catalog.Models.FirstOrDefault(m => m.Value == pane.Model); Label(model, (pane.Model == "default" ? Locale.Get("composer.model.default") : selectedModel?.DisplayName ?? pane.Model) + " ⌄", Locale.Get("composer.label.model")); ToolTipService.SetToolTip(model, selectedModel?.Description ?? pane.Model);
            var models = new MenuFlyout(); foreach (var row in catalog.Models) models.Items.Add(Item(row.DisplayName, () => ChangeModel(row.Value), pane.Model == row.Value, row.Description));
            if (!catalog.Models.Any(m => m.Value == pane.Model)) models.Items.Add(Item(pane.Model, () => ChangeModel(pane.Model), true));
            if (pane.Provider != "gemini") { models.Items.Add(new MenuFlyoutSeparator()); models.Items.Add(Item(Locale.Get("composer.model.enterIdMenu"), CustomModel)); } model.Flyout = models;
            var registeredModels2 = RegisteredModelsFor(pane.Provider, Workspace, owner.service.Snapshot);
            var levels = ProviderCatalog.Efforts(pane.Provider, pane.Model, catalog, registeredModels2); var knownEffort = pane.Settings.Effort == "default" || levels.Contains(pane.Settings.Effort);
            Label(effort, (pane.Settings.Effort == "default" ? "Auto" : pane.Settings.Effort) + (knownEffort ? " ⌄" : Locale.Get("composer.effort.unverifiedSuffix")), Locale.Get("composer.label.effort")); var efforts = new MenuFlyout();
            foreach (var value in new[] { "default" }.Concat(levels)) efforts.Items.Add(Item(value == "default" ? Locale.Get("composer.effort.auto") : value, () => ChangeSettings(s => s with { Effort = value }), pane.Settings.Effort == value)); effort.Flyout = efforts;
            effort.Visibility = caps.Effort || pane.Settings.Effort != "default" ? Visibility.Visible : Visibility.Collapsed;
            Label(permission, PermissionLabel(pane.Provider, pane.Settings.PermissionMode) + " ⌄", Locale.Get("composer.label.permission")); ToolTipService.SetToolTip(permission, PermissionHelp(pane.Provider, pane.Settings.PermissionMode)); var permissions = new MenuFlyout();
            foreach (var mode in (caps.PermissionModes ?? []).Where(ProviderCatalog.PermissionModes(pane.Provider).Contains)) permissions.Items.Add(Item(PermissionLabel(pane.Provider, mode), () => ChangeSettings(s => s with { PermissionMode = mode, NetworkAccess = pane.Provider == "codex" && mode == "acceptEdits" && s.NetworkAccess }), pane.Settings.PermissionMode == mode, PermissionHelp(pane.Provider, mode)));
            permission.Flyout = permissions;
            fast.IsChecked = pane.Settings.FastMode; fast.Visibility = pane.Provider == "codex" && (caps.FastMode || pane.Settings.FastMode) ? Visibility.Visible : Visibility.Collapsed;
            Label(more, "···", Locale.Get("composer.more")); more.Flyout = MoreMenu(pane, caps);
            provider.Visibility = model.Visibility = permission.Visibility = more.Visibility = pane.Kind == "shell" ? Visibility.Collapsed : Visibility.Visible;
            if (pane.Kind == "shell") effort.Visibility = fast.Visibility = Visibility.Collapsed;
        }
        private MenuFlyout MoreMenu(RunSession pane, ProviderCapabilities caps)
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
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Opening += (_, _) => { rename.IsEnabled = !owner.dialogOpen; if (plugins is not null) plugins.IsEnabled = !owner.dialogOpen; };
            AddOverflowSettings(menu, pane, caps);
            if (pane.Kind == "claude" && pane.Provider == "codex")
            {
                if (caps.WebSearch || pane.Settings.WebSearch != "default")
                {
                    var web = new MenuFlyoutSubItem { Text = Locale.Get("composer.webSearch.menu") };
                    foreach (var value in caps.WebSearch ? new[] { "default", "disabled", "cached", "live" } : ["default"])
                    { var title = Locale.Get(value == "default" ? "settings.run.webSearchDefault" : value == "disabled" ? "composer.webSearch.off" : value == "cached" ? "settings.run.webSearchCached" : "settings.run.webSearchLive"); web.Items.Add(Item(title, () => ChangeSettings(s => s with { WebSearch = value }), pane.Settings.WebSearch == value)); }
                    menu.Items.Add(web);
                }
                if (caps.NetworkAccess || pane.Settings.NetworkAccess)
                {
                    var network = Item(Locale.Get("composer.network.toggle"), () => ChangeSettings(s => s with { NetworkAccess = !s.NetworkAccess && s.PermissionMode == "acceptEdits" && Capabilities.NetworkAccess }), pane.Settings.NetworkAccess, Locale.Get("composer.network.help"));
                    network.IsEnabled = pane.Settings.NetworkAccess || caps.NetworkAccess && pane.Settings.PermissionMode == "acceptEdits"; menu.Items.Add(network);
                }
            }
            if (pane.Kind == "claude" && (caps.MaxTurns || caps.MaxBudgetUsd)) menu.Items.Add(Item(Locale.Get("composer.limits.menu"), Limits));
            if (pane.Kind == "claude")
            {
                if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(Item(Locale.Get("composer.newConversation"), () => owner.Act(async () => { if (Session.Status == "running") return; await Change(p => p with { ResumeId = null }); Refresh(); input.Focus(FocusState.Programmatic); })));
            }
            if (menu.Items.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = Locale.Get("composer.more.empty"), IsEnabled = false });
            return menu;
        }
        internal void Refresh()
        {
            var pane = Session; updating = true; var runtime = owner.Runtime(pane.Provider); var catalog = runtime?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider);
            label.Text = StateLabel(pane.Status); output.Update(pane, owner.service.Snapshot.Theme == "light"); RefreshElapsed();
            // Do not rewrite or recreate the editor during output/metadata refreshes.
            if (!draftLoaded) { input.Text = pane.Draft; draftLoaded = true; RefreshPalette(input.Text); }
            RefreshMenus(pane, catalog);
            detail.Text = Locale.Get("workspace.location.thisComputer") + (pane.Kind == "shell" ? " · " + Locale.Get("composer.detail.shell") : " · " + (catalog.Source == "cli" ? Locale.Get("composer.detail.modelsFromCli") : Locale.Get("composer.detail.defaultModels")) + (pane.ResumeId is null ? "" : " · " + Locale.Get("composer.detail.resuming")));
            RefreshComposerState(); ArrangeComposer();
            RequestStatusLineRefresh();
            updating = false;
        }
        private Task CustomModel() => owner.Act(async () =>
        {
            if (Session.Status == "running") return; var field = new TextBox { Header = Locale.Get("composer.model.idHeader"), Text = Session.Model }; var validation = new TextBlock { TextWrapping = TextWrapping.Wrap }; var content = new StackPanel { Spacing = 8 }; content.Children.Add(field); content.Children.Add(validation);
            var dialog = new ContentDialog { Title = Locale.Get("composer.model.enterIdTitle"), Content = content, XamlRoot = owner.root.XamlRoot, PrimaryButtonText = Locale.Get("composer.model.select"), CloseButtonText = Locale.Get("settings.run.cancelButton") };
            dialog.PrimaryButtonClick += (_, args) => { if (!Wire.Model(field.Text.Trim())) { validation.Text = Locale.Get("composer.model.invalidName"); args.Cancel = true; } };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ChangeModel(field.Text.Trim());
        });
        private Task Limits() => owner.Act(async () =>
        {
            var pane = Session; if (pane.Status == "running") return; var caps = Capabilities; var content = new StackPanel { Spacing = 10 };
            var turns = new TextBox { Header = Locale.Get("composer.limits.maxTurns"), Text = pane.Settings.MaxTurns?.ToString(CultureInfo.InvariantCulture) ?? "" }; var budget = new TextBox { Header = Locale.Get("composer.limits.maxBudget"), Text = pane.Settings.MaxBudgetUsd?.ToString(CultureInfo.InvariantCulture) ?? "" };
            if (caps.MaxTurns) content.Children.Add(turns); if (caps.MaxBudgetUsd) content.Children.Add(budget);
            var validation = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Colors.OrangeRed) }; content.Children.Add(validation);
            var dialog = new ContentDialog { Title = Locale.Get("settings.run.limitsTitle"), XamlRoot = owner.root.XamlRoot, Content = content, PrimaryButtonText = Locale.Get("settings.run.applyButton"), CloseButtonText = Locale.Get("settings.run.cancelButton") };
            dialog.PrimaryButtonClick += async (sender, args) =>
            {
                var deferral = args.GetDeferral();
                try
                {
                    int? maxTurns = caps.MaxTurns && turns.Text.Trim() != "" ? int.Parse(turns.Text, CultureInfo.InvariantCulture) : null; double? maxBudget = caps.MaxBudgetUsd && budget.Text.Trim() != "" ? double.Parse(budget.Text, CultureInfo.InvariantCulture) : null;
                    var setting = Session.Settings with { MaxTurns = maxTurns, MaxBudgetUsd = maxBudget }; _ = new StartRunRequest(pane.Id, pane.WorkspaceId, pane.Kind, "validation", [], pane.Model, pane.Provider, setting).Validate(); await Change(p => p with { Settings = setting });
                }
                catch (Exception ex) { args.Cancel = true; validation.Text = ex.Message; }
                finally { deferral.Complete(); }
            };
            await dialog.ShowAsync(); Refresh(); input.Focus(FocusState.Programmatic);
        });
    }
}
