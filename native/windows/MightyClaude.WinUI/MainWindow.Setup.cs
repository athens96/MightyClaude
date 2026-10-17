using System.Runtime.InteropServices;
using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MightyClaude.WinUI;

/// <summary>
/// 필요한 도구 (M/AppStore+Setup.swift, M/SetupToolsView.swift): what the app needs on this PC, read from the bundled catalog
/// (SetupCatalog), probed fresh, and installed in one PowerShell window of its own. The app writes the script from the catalog
/// alone, never elevates itself and never sees a password: whatever a step asks, the user answers in that window. The screen is a
/// window of its own, so its confirmation can open over it (WinUI forbids nested dialogs) and it holds back neither the phone nor the
/// automatic sign-in recovery; Settings › 도구 › 구성 요소 hosts the same list.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The install the screen started: one script in one window.</summary>
    private sealed class SetupRun(IReadOnlyList<string> items, string started, string done)
    {
        public IReadOnlyList<string> Items { get; } = items;
        public string Started { get; } = started;
        public string Done { get; } = done;
        public DateTimeOffset Opened { get; } = DateTimeOffset.UtcNow;
        public bool Finished { get; set; }
        /// <summary>The script has not said it started 20 s after the window opened.</summary>
        public bool NotStarted { get; set; }
    }

    /// <summary>While an install runs, the run's own items are probed every 5 s; otherwise everything every 30 s.</summary>
    private static readonly TimeSpan SetupRunProbeInterval = TimeSpan.FromSeconds(5), SetupIdleProbeInterval = TimeSpan.FromSeconds(30), SetupWatchInterval = TimeSpan.FromSeconds(2), SetupStartDeadline = TimeSpan.FromSeconds(20);

    private SetupCatalog? setupCatalog;
    private readonly Dictionary<string, SetupProbeResult> setupResults = new(StringComparer.Ordinal);
    private readonly HashSet<string> setupSelection = new(StringComparer.Ordinal);
    /// <summary>The GUI smoke's fixed probe results: nothing on the machine is probed while they are set.</summary>
    private Dictionary<string, SetupProbeResult>? setupFixedResults;
    private bool setupProbing, setupForceQueued, setupDidPreselect, setupResultWaiting, setupNeedsAttentionShown;
    private string? setupMessage;
    private SetupRun? setupRun;
    private CancellationTokenSource? setupWatch;
    private Window? setupWindow;
    private string? setupFocus;
    /// <summary>How many terminal windows the screen opened: the smoke proves a cancelled confirmation opened none.</summary>
    private int setupTerminalsOpened;
    /// <summary>Every list on screen (the setup window's, Settings'), drawn again when anything changes.</summary>
    private readonly List<Action> setupRedraws = [];
    private DispatcherTimer? setupPoll;
    private DateTimeOffset setupProbedAt;

    private bool SetupRunning => setupRun is { Finished: false };
    private IEnumerable<SetupItem> SetupItems => setupCatalog?.Items.Where(item => item.Entry(SetupPlatform.Windows) is not null) ?? [];

    /// <summary>The catalog, read once. A broken one is said, not hidden.</summary>
    private SetupCatalog? LoadSetupCatalog()
    {
        if (setupCatalog is not null) return setupCatalog;
        try { setupCatalog = SetupCatalog.Bundled(); }
        catch (InvalidDataException ex) { setupMessage = Locale.Get("setup.error.catalog", new Dictionary<string, string> { ["error"] = ex.Message }); }
        return setupCatalog;
    }

    /// <summary>
    /// Probes the items again (only <paramref name="only"/>, when given). <paramref name="force"/> reads PATH again from the registry
    /// (HKLM and HKCU), as an install that just finished changed it for new processes only; a forced refresh asked for while a probe
    /// runs is queued. When an agent CLI changed, the runtime is read again, so a pane blocked for a missing CLI is not blocked any longer.
    /// </summary>
    private async Task RefreshSetup(bool force = false, IReadOnlySet<string>? only = null)
    {
        if (closing || LoadSetupCatalog() is not { } catalog) { RedrawSetup(); return; }
        if (setupProbing) { if (force) setupForceQueued = true; return; }
        setupProbing = true; RedrawSetup();
        Dictionary<string, SetupProbeResult> results;
        try
        {
            if (setupFixedResults is { } fixedResults) results = new(fixedResults);
            else
            {
                var environment = force ? await CliEnvironment.RefreshAsync(true) : CliEnvironment.Current();
                results = await Task.Run(() => new SetupProber(environment).ProbeAllAsync(catalog, only));
            }
        }
        finally { setupProbing = false; setupProbedAt = DateTimeOffset.UtcNow; }
        if (closing) return;
        foreach (var (id, result) in results) setupResults[id] = result;
        setupSelection.RemoveWhere(id => setupResults.GetValueOrDefault(id)?.IsInstalled == true);
        if (setupFixedResults is null && runtime is { } held && SetupRuntimeSync.IsStale(held.Providers, catalog, setupResults))
        {
            await RefreshRuntime();
            await accountsCoordinator.RefreshAsync(CliAccountProviders);
        }
        RedrawSetup();
        // The welcome's button follows the rule as it changes.
        if (SetupNeedsAttention != setupNeedsAttentionShown && service.Snapshot.Workspaces.Count == 0) Render();
        if (setupForceQueued) { setupForceQueued = false; await RefreshSetup(true); }
    }

    /// <summary>
    /// At launch: the screen opens when no agent CLI can run or Git is not there, unless the user asked never to see it again.
    /// Never in a smoke run, which has its own screens to show.
    /// </summary>
    private async Task PresentSetupIfNeeded()
    {
        if (options.SmokeTest || closing) return;
        await RefreshSetup();
        if (setupCatalog is { } catalog && SetupFirstRun.ShouldPresent(catalog, setupResults, SetupFirstRun.NeverShow())) OpenSetup();
    }

    /// <summary>The welcome screen's button shows while this holds.</summary>
    private bool SetupNeedsAttention => setupCatalog is { } catalog && SetupFirstRun.NeedsAttention(catalog, setupResults);

    /// <summary>Opens the screen (or brings it forward), with <paramref name="focus"/> picked and shown first when it is missing.</summary>
    private void OpenSetup(string? focus = null)
    {
        if (LoadSetupCatalog() is null && setupMessage is null) return;
        setupResultWaiting = false; RefreshStatusBar();
        setupFocus = focus;
        if (focus is not null && setupCatalog?.Item(focus) is { } item && SetupRows.IsSelectable(SetupStateOf(item), item)) setupSelection.Add(focus);
        if (setupWindow is { } open) { RedrawSetup(); open.Activate(); return; }
        var frame = new Grid { RequestedTheme = root.RequestedTheme, Language = WindowLanguage(), Background = WindowBackground(), KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden, Padding = new Thickness(DesignMetrics.Inset.Sheet), RowSpacing = DesignMetrics.Spacing.Md };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) frame.RowDefinitions.Add(new() { Height = height });
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
        heading.Children.Add(SettingsSymbol("", 15, DesignToken.Accent));
        heading.Children.Add(SettingsText(Locale.Get("setup.title"), 15, medium: true));
        frame.Children.Add(heading);
        var subtitle = SettingsText(Locale.Get("setup.windows.subtitle"), 12, DesignToken.Ink2);
        Grid.SetRow(subtitle, 1); frame.Children.Add(subtitle);
        var list = BuildSetupContent();
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, DesignMetrics.Spacing.Md, 0) };
        Grid.SetRow(scroll, 2); frame.Children.Add(scroll);
        var footer = new Grid { ColumnSpacing = DesignMetrics.Spacing.Md, BorderBrush = brushes.Brush(DesignToken.Line), BorderThickness = new Thickness(0, DesignMetrics.Stroke.Line, 0, 0), Padding = new Thickness(0, DesignMetrics.Spacing.Md, 0, 0) };
        footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var neverShow = new CheckBox { Content = SettingsText(Locale.Get("setup.button.neverShow"), 11), MinWidth = 0 };
        AutomationProperties.SetName(neverShow, Locale.Get("setup.button.neverShow")); AutomationProperties.SetAutomationId(neverShow, "setup-never-show");
        footer.Children.Add(neverShow);
        var window = new Window { Title = Locale.Get("setup.title"), Content = frame }; AppText.Track(window);
        var later = SettingsPush(Button(Locale.Get("setup.button.later"), () => { window.Close(); return Task.CompletedTask; }));
        AutomationProperties.SetAutomationId(later, "setup-later"); Grid.SetColumn(later, 1); footer.Children.Add(later);
        Grid.SetRow(footer, 3); frame.Children.Add(footer);
        // Esc is 다음에 하기, as the Mac's cancel action; an open confirmation or menu takes the key first.
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            if (frame.XamlRoot is null || Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(frame.XamlRoot).Any(popup => popup.Child is not ToolTip)) return;
            args.Handled = true; window.Close();
        };
        frame.KeyboardAccelerators.Add(escape);
        window.Closed += (_, _) =>
        {
            // The choice holds however the screen is closed.
            if (neverShow.IsChecked == true) SetupFirstRun.SetNeverShow();
            if (ReferenceEquals(setupWindow, window)) { setupWindow = null; setupFocus = null; }
        };
        setupWindow = window;
        var scale = root.XamlRoot?.RasterizationScale ?? 1;
        window.AppWindow.ResizeClient(new((int)Math.Round(SetupWindowWidth * scale), (int)Math.Round(SetupWindowHeight * scale)));
        brushes.ApplyTitleBar(window.AppWindow);
        window.Activate();
    }

    /// <summary>The screen's size in points: the Mac's 600-wide sheet, tall enough for most of the list.</summary>
    internal const double SetupWindowWidth = 620, SetupWindowHeight = 700;

    /// <summary>
    /// The groups, their rows and the buttons under them: the setup window's body and Settings › 구성 요소'. It is drawn again from the
    /// screen's state whenever that changes, while it is on screen; the probes start as it shows and go on while any list is shown.
    /// </summary>
    private StackPanel BuildSetupContent()
    {
        var host = new StackPanel { Spacing = DesignMetrics.Spacing.Lg };
        AutomationProperties.SetAutomationId(host, "setup-list");
        void Draw() => FillSetupContent(host);
        host.Loaded += async (_, _) =>
        {
            if (!setupRedraws.Contains(Draw)) setupRedraws.Add(Draw);
            StartSetupPoll();
            Draw();
            await Act(async () =>
            {
                await RefreshSetup(force: setupFixedResults is null);
                PreselectSetupOnce();
                if (setupFixedResults is null) await accountsCoordinator.RefreshAsync(CliAccountProviders);
                RedrawSetup();
            });
        };
        host.Unloaded += (_, _) =>
        {
            if (host.IsLoaded) return;
            setupRedraws.Remove(Draw);
            if (setupRedraws.Count == 0) { setupPoll?.Stop(); setupPoll = null; }
        };
        Draw();
        return host;
    }

    private void RedrawSetup() { foreach (var draw in setupRedraws.ToList()) draw(); }

    /// <summary>While a list shows: the run's items every 5 s while it runs (its watcher does it), otherwise everything every 30 s while something is still missing.</summary>
    private void StartSetupPoll()
    {
        if (setupPoll is not null) return;
        setupPoll = new DispatcherTimer { Interval = SetupRunProbeInterval };
        setupPoll.Tick += async (_, _) =>
        {
            if (closing || SetupRunning || setupProbing || DateTimeOffset.UtcNow - setupProbedAt < SetupIdleProbeInterval) return;
            if (!SetupItems.Any(item => setupResults.GetValueOrDefault(item.Id)?.IsInstalled != true)) return;
            await Act(() => RefreshSetup());
        };
        setupPoll.Start();
    }

    /// <summary>The first time the list shows with results: Git when it is missing, and Claude Code when no agent CLI is there. Never again once the picks changed.</summary>
    private void PreselectSetupOnce()
    {
        if (setupDidPreselect || setupCatalog is not { } catalog || setupResults.Count == 0) return;
        setupDidPreselect = true;
        setupSelection.UnionWith(SetupFirstRun.Preselection(catalog, setupResults));
    }

    private SetupRowState SetupStateOf(SetupItem item) =>
        SetupRows.Derive(setupResults.GetValueOrDefault(item.Id), setupRun?.Items.Contains(item.Id) == true, setupRun?.Finished ?? true);

    /// <summary>The selection plus the missing prerequisites it needs, in install order.</summary>
    private IReadOnlyList<string> SetupPlanIds => setupCatalog is { } catalog
        ? SetupPlan.Order(setupSelection, catalog, SetupPlatform.Windows, id => setupResults.GetValueOrDefault(id)?.IsInstalled == true) : [];

    private void ToggleSetupSelection(string id)
    {
        if (SetupRunning) return;
        setupDidPreselect = true;
        if (!setupSelection.Add(id)) setupSelection.Remove(id);
        RedrawSetup();
    }

    private void FillSetupContent(StackPanel host)
    {
        host.Children.Clear();
        var plan = SetupPlanIds; var added = plan.Where(id => !setupSelection.Contains(id)).ToHashSet(StringComparer.Ordinal);
        foreach (var group in Enum.GetValues<SetupGroup>())
        {
            var items = SetupItems.Where(item => item.Group == group).ToList();
            if (items.Count == 0) continue;
            var section = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
            section.Children.Add(SettingsText(Locale.Get(SetupCatalog.GroupTitleKey(group)), 11, DesignToken.Ink2, medium: true));
            foreach (var item in items) section.Children.Add(SetupItemRow(item, added.Contains(item.Id)));
            host.Children.Add(section);
        }
        host.Children.Add(SetupFooter(plan));
    }

    private FrameworkElement SetupItemRow(SetupItem item, bool added)
    {
        var state = SetupStateOf(item); var result = setupResults.GetValueOrDefault(item.Id);
        var selectable = SetupRows.IsSelectable(state, item);
        var focused = item.Id == setupFocus;
        var row = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm, Padding = new Thickness(focused ? DesignMetrics.Spacing.Xs : 0, DesignMetrics.Spacing.Xxs, focused ? DesignMetrics.Spacing.Xs : 0, DesignMetrics.Spacing.Xxs), CornerRadius = new CornerRadius(DesignMetrics.Radius.FileRow) };
        if (focused) row.Background = brushes.Brush(DesignToken.Accent, 0.12);
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        AutomationProperties.SetAutomationId(row, "setup-row-" + item.Id);
        AutomationProperties.SetItemStatus(row, state.ToString());
        var check = new CheckBox
        {
            IsChecked = setupSelection.Contains(item.Id) || added, IsEnabled = selectable && !added && !SetupRunning, MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top,
            Opacity = selectable || setupSelection.Contains(item.Id) || added ? 1 : 0,
        };
        AutomationProperties.SetName(check, SetupCatalog.Title(item)); AutomationProperties.SetAutomationId(check, "setup-check-" + item.Id);
        var id = item.Id;
        check.Click += (_, _) => ToggleSetupSelection(id);
        row.Children.Add(check);
        var words = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
        var head = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
        head.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); head.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
        if (item.Provider is { } provider) name.Children.Add(ProviderMarkView.Create(provider, SettingsProviderMark));
        else name.Children.Add(SettingsSymbol(item.Group switch { SetupGroup.Prerequisite => "", SetupGroup.Style => "", SetupGroup.Network => "", SetupGroup.Dev => "", _ => "" }, 12, DesignToken.Ink));
        name.Children.Add(SettingsText(SetupCatalog.Title(item), 13, medium: true));
        if (item.Provider is { } beta && ProviderCatalog.IsBeta(beta)) name.Children.Add(BetaBadgeView.Create(brushes));
        if (item.Required) name.Children.Add(SettingsCapsule(Locale.Get("setup.badge.required"), brushes.Brush(DesignToken.Accent), brushes.Brush(DesignToken.Accent, 0.15), 9, 3));
        if (result?.ShownVersion is { } version) name.Children.Add(SettingsText(version, 10, DesignToken.Ink2, mono: true));
        head.Children.Add(name);
        var status = state switch
        {
            SetupRowState.Installed => DesignToken.DoneText,
            SetupRowState.Missing or SetupRowState.Outdated or SetupRowState.Attention => DesignToken.WaitText,
            SetupRowState.Installing => DesignToken.Accent,
            SetupRowState.Failed => DesignToken.ErrText,
            _ => DesignToken.Ink2,
        };
        var word = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xs, VerticalAlignment = VerticalAlignment.Top };
        if (state == SetupRowState.Installing) word.Children.Add(new ProgressRing { IsActive = true, Width = 10, Height = 10, MinWidth = 0, MinHeight = 0 });
        word.Children.Add(SettingsText(Locale.Get(SetupRows.StatusKey(state)), 10, status, medium: true));
        Grid.SetColumn(word, 1); head.Children.Add(word);
        words.Children.Add(head);
        words.Children.Add(SettingsText(SetupCatalog.Detail(item), 11, DesignToken.Ink2));
        if (result is { State: SetupProbeState.Outdated, Minimum: { } minimum })
            words.Children.Add(SettingsText(Locale.Get("setup.outdatedDetail", new Dictionary<string, string> { ["version"] = result.Version ?? "?", ["minimum"] = minimum }), 11, DesignToken.WaitText));
        if (result is { State: SetupProbeState.Unverified, Minimum: { } floor })
            words.Children.Add(SettingsText(Locale.Get("setup.unverifiedDetail", new Dictionary<string, string> { ["minimum"] = floor }), 11, DesignToken.WaitText));
        if (added) words.Children.Add(SettingsText(Locale.Get("setup.addedForSelection"), 10, DesignToken.Accent));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm, Margin = new Thickness(0, DesignMetrics.Spacing.Xxs, 0, 0) };
        // Sign-in once an agent CLI is there and signed out; the CLI update for an old agent CLI; the page for what only a page installs.
        if (item.Provider is { } signIn && state == SetupRowState.Installed && accountsCoordinator.Statuses.GetValueOrDefault(signIn)?.LoggedIn == false)
            // The CLI accounts' own sign-in (an external terminal on the CLI's login command); why it cannot start now is said here.
            actions.Children.Add(SetupAction("setup.button.signIn", "setup-signin-" + item.Id, async () =>
            {
                try { setupMessage = null; await StartCliSignIn(signIn, CliLoginOption.Account); }
                catch (InvalidOperationException ex) { setupMessage = ex.Message; }
                RedrawSetup();
            }, prominent: true));
        if (item.Provider is not null && state == SetupRowState.Outdated)
        {
            var update = SetupAction(AnyCliUpdateRunning ? "settings.components.inProgressLabel" : "setup.button.update", "setup-update-" + item.Id, () =>
            {
                if (!AnyCliUpdateRunning && !pluginOperations.IsRunning) coordinator.Start();
                return Task.CompletedTask;
            }, prominent: true);
            update.IsEnabled = !AnyCliUpdateRunning && !pluginOperations.IsRunning;
            actions.Children.Add(update);
        }
        if (state != SetupRowState.Installed && item.Entry(SetupPlatform.Windows)?.Install is SetupRecipe.VendorPage page)
            actions.Children.Add(SetupAction("setup.button.openPage", "setup-page-" + item.Id, async () => await Launcher.LaunchUriAsync(page.Url)));
        if (actions.Children.Count > 0) words.Children.Add(actions);
        Grid.SetColumn(words, 1); row.Children.Add(words);
        return row;
    }

    private Button SetupAction(string key, string automationId, Func<Task> action, bool prominent = false)
    {
        var button = SettingsPush(Button(Locale.Get(key), () => Act(action)), SettingsControlSize.Small, prominent: prominent);
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    /// <summary>What is going on, then 다시 확인 and the install button.</summary>
    private StackPanel SetupFooter(IReadOnlyList<string> plan)
    {
        var footer = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
        StackPanel Warning(string text, string automationId)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
            line.Children.Add(SettingsSymbol("", 12, DesignToken.WaitText));
            var words = SettingsText(text, 11, selectable: true); words.MaxWidth = 520; line.Children.Add(words);
            AutomationProperties.SetAutomationId(line, automationId);
            return line;
        }
        if (setupMessage is { } message) footer.Children.Add(Warning(message, "setup-message"));
        if (setupRun is { NotStarted: true })
        {
            var hint = Warning(Locale.Get("setup.run.checkTerminal"), "setup-run-not-started");
            var end = SettingsPush(Button(Locale.Get("setup.run.end"), () => Act(AbandonSetupRun)), SettingsControlSize.Small);
            AutomationProperties.SetAutomationId(end, "setup-run-end"); hint.Children.Add(end);
            footer.Children.Add(hint);
        }
        var buttons = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
        buttons.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); buttons.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); buttons.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        if (SetupRunning)
        {
            var running = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
            running.Children.Add(new ProgressRing { IsActive = true, Width = 12, Height = 12, MinWidth = 0, MinHeight = 0 });
            running.Children.Add(SettingsText(Locale.Get("setup.running"), 11, DesignToken.Ink2));
            buttons.Children.Add(running);
        }
        var recheck = SettingsPush(Button(Locale.Get(setupProbing ? "setup.button.checking" : "setup.button.recheck"), () => Act(() => RefreshSetup(force: true))));
        recheck.IsEnabled = !setupProbing; AutomationProperties.SetAutomationId(recheck, "setup-recheck");
        Grid.SetColumn(recheck, 1); buttons.Children.Add(recheck);
        var install = SettingsPush(new Button { Content = Locale.Get("setup.button.install") }, prominent: true);
        AutomationProperties.SetName(install, Locale.Get("setup.button.install")); AutomationProperties.SetAutomationId(install, "setup-install");
        install.IsEnabled = plan.Count > 0 && !SetupRunning;
        install.Click += async (_, _) => await Act(() => RequestSetupInstall(install.XamlRoot));
        Grid.SetColumn(install, 2); buttons.Children.Add(install);
        footer.Children.Add(buttons);
        return footer;
    }

    // ── Installing ─────────────────────────────────────────────────────────────

    /// <summary>The one confirmation before anything runs: the lines that run first, then every step's command exactly as the script has it, in order.</summary>
    private ContentDialog? BuildSetupConfirmation(XamlRoot? xamlRoot, out IReadOnlyList<SetupScriptStep> steps)
    {
        steps = setupCatalog is { } catalog ? SetupScript.Steps(SetupPlanIds, catalog, SetupCatalog.Title) : [];
        if (steps.Count == 0) return null;
        var body = new StackPanel { Spacing = DesignMetrics.Spacing.Md };
        var preamble = new StackPanel { Spacing = DesignMetrics.Spacing.Xs };
        preamble.Children.Add(SettingsText(Locale.Get("setup.confirm.preamble"), 12, medium: true));
        preamble.Children.Add(SettingsText(string.Join("\n", SetupScript.Preamble(CliEnvironment.Current())), 11, mono: true, selectable: true));
        AutomationProperties.SetAutomationId(preamble, "setup-confirm-preamble");
        body.Children.Add(preamble);
        for (var index = 0; index < steps.Count; index++)
        {
            var part = new StackPanel { Spacing = DesignMetrics.Spacing.Xs };
            part.Children.Add(SettingsText($"{index + 1}. {steps[index].Title}", 12, medium: true));
            part.Children.Add(SettingsText(steps[index].Body, 11, mono: true, selectable: true));
            AutomationProperties.SetAutomationId(part, "setup-confirm-step-" + steps[index].ItemId);
            body.Children.Add(part);
        }
        var content = new StackPanel { Spacing = DesignMetrics.Spacing.Md };
        content.Children.Add(SettingsText(Locale.Get("setup.windows.confirm.body"), 12, DesignToken.Ink2));
        content.Children.Add(new ScrollViewer { Content = body, MaxHeight = 340 });
        return StyledDialog(new ContentDialog
        {
            Title = Locale.Get("setup.confirm.title"), Content = content, XamlRoot = xamlRoot ?? root.XamlRoot,
            PrimaryButtonText = Locale.Get("setup.confirm.run"), CloseButtonText = Locale.Get("setup.confirm.cancel"), DefaultButton = ContentDialogButton.Close,
        });
    }

    private async Task RequestSetupInstall(XamlRoot? xamlRoot)
    {
        if (SetupRunning || BuildSetupConfirmation(xamlRoot, out var steps) is not { } dialog) return;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await StartSetupInstall(steps);
    }

    /// <summary>
    /// Writes the script into the app's own setup folder and runs it in a window of its own. The probe, not the script's exit code,
    /// decides what got installed; the screen stays open and follows along.
    /// </summary>
    private async Task StartSetupInstall(IReadOnlyList<SetupScriptStep> steps)
    {
        if (SetupRunning || closing || options.SmokeTest) return;
        var environment = CliEnvironment.Current();
        var folder = SetupScriptFiles.Folder(options.ProfileDirectory, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        string script; (string Started, string Done) markers;
        try
        {
            SetupScriptFiles.PrepareDirectory(folder);
            // Earlier runs' scripts and markers are of no further use.
            SetupScriptFiles.RemoveLeftovers(folder);
            markers = SetupScriptFiles.Markers(folder);
            script = SetupScriptFiles.WriteScript(SetupScript.Render(steps, SetupScriptLabels.Localized(), environment, markers.Started, markers.Done), folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            setupMessage = Locale.Get("setup.error.script", new Dictionary<string, string> { ["error"] = ex.Message }); RedrawSetup(); return;
        }
        // The app wrote this command itself from its own catalog, after the user confirmed it. Windows Terminal when it is there, else the console host.
        try
        {
            var argv = SetupScript.LaunchArguments(script, environment);
            var variables = WindowsCommand.Variables(environment);
            var plan = CliAccountTerminal.LaunchPlan(argv, WindowsCommand.Find("wt", variables.GetValueOrDefault("PATH"), variables.GetValueOrDefault("PATHEXT")) is not null, folder, argv[0]);
            var info = new System.Diagnostics.ProcessStartInfo(plan.Executable) { UseShellExecute = false, WorkingDirectory = folder };
            foreach (var value in plan.Arguments) info.ArgumentList.Add(value);
            foreach (var (key, value) in environment) info.Environment[key] = value;
            using var process = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException(Locale.Get("windows.process.startFailed"));
            setupTerminalsOpened++;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            setupMessage = Locale.Get("setup.windows.error.terminal", new Dictionary<string, string> { ["error"] = ex.Message }); RedrawSetup(); return;
        }
        setupMessage = null;
        setupRun = new SetupRun(steps.Select(step => step.ItemId).ToList(), markers.Started, markers.Done);
        RedrawSetup();
        await WatchSetupRun(setupRun);
    }

    /// <summary>
    /// The script makes its start marker first thing and its end marker when its steps end for any reason; a window closed before
    /// that ends the run too (its PowerShell is gone). Then everything is probed again, with PATH read anew.
    /// </summary>
    private async Task WatchSetupRun(SetupRun run)
    {
        setupWatch?.Cancel();
        var watch = setupWatch = new CancellationTokenSource();
        var probed = DateTimeOffset.UtcNow;
        try
        {
            while (!watch.IsCancellationRequested && !closing && ReferenceEquals(setupRun, run) && !run.Finished)
            {
                await Task.Delay(SetupWatchInterval, watch.Token);
                if (await Task.Run(() => SetupScriptFiles.RunEnded(run.Started, run.Done))) break;
                var stalled = !SetupScriptFiles.MarkerExists(run.Started) && DateTimeOffset.UtcNow - run.Opened > SetupStartDeadline;
                if (run.NotStarted != stalled) { run.NotStarted = stalled; RedrawSetup(); }
                // Only the run's own items, every few seconds.
                if (DateTimeOffset.UtcNow - probed >= SetupRunProbeInterval) { probed = DateTimeOffset.UtcNow; await RefreshSetup(only: run.Items.ToHashSet(StringComparer.Ordinal)); }
            }
        }
        catch (OperationCanceledException) { return; }
        if (!watch.IsCancellationRequested) await FinishSetupRun(run);
    }

    /// <summary>
    /// Ends the run: by the watcher, or by the user when the script never started. The screen comes back by itself only while the app
    /// is in front; otherwise the status bar says the install is over.
    /// </summary>
    private async Task FinishSetupRun(SetupRun run)
    {
        if (closing || !ReferenceEquals(setupRun, run) || run.Finished) return;
        run.Finished = true; run.NotStarted = false;
        await RefreshSetup(force: true);
        if (setupWindow is not null) return;
        if (AppIsInFront()) OpenSetup(); else { setupResultWaiting = true; RefreshStatusBar(); }
    }

    /// <summary>The hint's button: the script never started, so the run is closed by hand.</summary>
    private Task AbandonSetupRun()
    {
        if (setupRun is not { Finished: false } run) return Task.CompletedTask;
        setupWatch?.Cancel();
        return FinishSetupRun(run);
    }

    /// <summary>The foreground window is one of the app's own.</summary>
    private bool AppIsInFront()
    {
        var front = GetForegroundWindow();
        return new[] { this, settingsWindow, setupWindow }.OfType<Window>().Any(window => WinRT.Interop.WindowNative.GetWindowHandle(window) == front);
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();

    /// <summary>
    /// The setup item for an agent pane's provider when its CLI is missing on this PC, so the blocked line can offer to install it
    /// (M/AppStore+Setup.swift setupItemForMissingCLI). An old Claude is no missing CLI: it gets the update.
    /// </summary>
    private string? SetupItemForMissingCli(RunSession session)
    {
        if (session.Kind != "claude" || runtime is null || Runtime(session.Provider) is { Version: not null }) return null;
        return LoadSetupCatalog()?.Items.FirstOrDefault(item => item.Provider == session.Provider)?.Id;
    }
}
