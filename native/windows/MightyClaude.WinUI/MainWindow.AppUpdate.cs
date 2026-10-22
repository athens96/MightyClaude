using System.Diagnostics;
using System.Runtime.InteropServices;
using MightyClaude.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

// 앱 업데이트 — the Settings section that checks the signed latest.json,
// downloads the package for this machine, verifies it and hands the swap to a
// helper that runs after the app has quit.
//
// Every decision is Core's: whether the build may check at all, which address
// wins, what the status line says, what the one button reads and whether it may
// be pressed. This file renders AppUpdateSectionView and forwards the click. No
// Korean literal is typed here — the copy is AppUpdateStrings.
public sealed partial class MainWindow
{
    internal const string AppUpdateStatusId = "app-update-status";
    internal const string AppUpdateButtonId = "app-update-action";
    internal const string AppUpdateToggleId = "app-update-automatic";
    internal const string AppUpdateAddressId = "app-update-url";
    internal const string AppUpdateRetryId = "app-update-retry";
    internal const string AppUpdateCountdownId = "app-update-countdown";
    internal const string AppUpdateStagedId = "app-update-staged";
    internal const string AppUpdateRestartId = "app-update-restart";

    private AppUpdateService? appUpdateService;
    private AppUpdateCoordinator? appUpdate;

    /// Created once, from the real build's key and address. A build without a
    /// key still gets a coordinator so the section can say it cannot check.
    internal AppUpdateCoordinator AppUpdate => appUpdate ??= CreateAppUpdate();

    private AppUpdateCoordinator CreateAppUpdate()
    {
        var stateDirectory = options.ProfileDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MightyClaudeNative");
        appUpdateService = new AppUpdateService(stateDirectory, AppUpdateBuild.PublicKey());
        var coordinator = new AppUpdateCoordinator(
            appUpdateService,
            AppVersion.Normalized(AppVersionText) ?? "0.1.0",
            AppUpdateBuild.ManifestUrl(),
            AppUpdateBuild.Architecture,
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
            LaunchUpdateHelperAsync,
            () => service.Snapshot.AppUpdateAutoCheck);
        coordinator.StateChanged += () => DispatcherQueue.TryEnqueue(AppUpdateStateChanged);
        return coordinator;
    }

    /// Starts the helper detached with a minimal environment. A helper that
    /// starts the app again (manual install, idle restart) quits the app through
    /// `QuitAsync`, the routine a person's close runs, so the run, the terminals,
    /// the phone link and the shared workspaces are shut down as on any quit
    /// (`Window.Close()` alone does not raise `AppWindow.Closing`); the install
    /// on quit (`Relaunch` false) is started while the app is already closing
    /// and must not close it a second time.
    /// The helper runs from the staged folder of the new version so the install
    /// folder can be renamed aside freely. It never runs elevated.
    private Task LaunchUpdateHelperAsync(AppUpdateInstallPlan plan)
    {
        var helperPath = plan.StagedHelperExecutable;
        var stagedRoot = Path.GetFullPath(plan.StagedDirectory.TrimEnd(Path.DirectorySeparatorChar))
            + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(helperPath).StartsWith(stagedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Locale.Get("settings.appUpdate.helperOutsideStage"));
        var info = new ProcessStartInfo(helperPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var argument in plan.HelperArguments) info.ArgumentList.Add(argument);
        info.Environment.Clear();
        foreach (var (key, value) in AppUpdateInstallPlan.HelperEnvironment) info.Environment[key] = value;
        using (Process.Start(info)) { }
        if (plan.Relaunch) DispatcherQueue.TryEnqueue(() => _ = QuitAsync());
        return Task.CompletedTask;
    }

    // The live controls. Settings builds a fresh set each time it opens, so the
    // section never re-parents a control that still belongs to a closed dialog;
    // these fields point at the newest set for the live phase updates.
    private TextBlock? appUpdateStatus, appUpdateNotes, appUpdateAddressHint, appUpdateSignature, appUpdateKeptError, appUpdateRetry;
    private Button? appUpdateButton;
    private TextBox? appUpdateAddress;
    // The rows a build with a key shows (typed address, built-in address, automatic check, signature), the notes row and the failure row.
    private Border? appUpdateAddressRow, appUpdateBuiltInRow, appUpdateAutomaticRow, appUpdateSignatureRow, appUpdateNoticeRow, appUpdateNotesRow, appUpdateFailureRow;
    private Grid? appUpdateState;
    private ProgressRing? appUpdateBusy;
    private ProgressBar? appUpdateDownload;

    // The smoke run walks the section through the download phases, which a build
    // without a public key never reaches (Windows rule 1). CI builds carry no
    // key, so the run renders the phases as a signed build would while proving
    // the refusal on the real, key-less control first.
    private bool appUpdateSmokeSignedBuild;

    private StackPanel BuildAppUpdateSectionFromState() => BuildAppUpdateSection();

    // M/AppUpdateSettingsView.swift:8-45: the version beside its label; the update address (the build's own
    // beside its label, or the field to type one over its hint); the automatic-check switch; the 10pt
    // signature note; the state beside the one button; then the release notes. A build with no key shows
    // the version, the refusal in waitText and the disabled button.
    internal StackPanel BuildAppUpdateSection()
    {
        var rows = new StackPanel();
        var version = SettingsText(Locale.Get("settings.appUpdate.betaVersionTemplate", new Dictionary<string, string> { ["version"] = AppVersionText }), 13, DesignToken.Ink2, selectable: true);
        SettingsRow(rows, SettingsLabeled(SettingsText(AppUpdateStrings.CurrentVersionLabel), version));

        var address = SettingsField(new TextBox { PlaceholderText = AppUpdateStrings.ManifestUrlPlaceholder }, 12);
        AutomationProperties.SetAutomationId(address, AppUpdateAddressId);
        address.Text = service.Snapshot.AppUpdateManifestUrlOverride ?? "";
        address.TextChanged += async (_, _) =>
        {
            var typed = address.Text;
            await service.UpdateAsync(s => s with { AppUpdateManifestUrlOverride = typed.Length == 0 ? null : typed });
        };
        var addressHint = SettingsText("", 11, DesignToken.Ink2);
        var typedAddress = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
        typedAddress.Children.Add(address); typedAddress.Children.Add(addressHint);
        appUpdateAddressRow = SettingsRow(rows, typedAddress);
        // The address is longer than the line beside its label, so the form sets it under the label (screens/10-settings-about-*.webp).
        var builtIn = new StackPanel { Spacing = DesignMetrics.Spacing.Xs };
        builtIn.Children.Add(SettingsText(Locale.Get("settings.appUpdate.manifestUrlAddressLabel")));
        builtIn.Children.Add(SettingsText(AppUpdate.BuiltInManifestUrl ?? "", 11, DesignToken.Ink2, mono: true, selectable: true));
        appUpdateBuiltInRow = SettingsRow(rows, builtIn);

        var automatic = SettingsSwitch(AppUpdateStrings.AutoUpdateToggle, service.Snapshot.AppUpdateAutoCheck, AppUpdateToggleId);
        async void AutomaticToggled()
        {
            var on = automatic.IsChecked == true;
            await service.UpdateAsync(s => s with { AppUpdateAutoCheck = on });
            AppUpdateAutomaticChanged(on);
        }
        automatic.Checked += (_, _) => AutomaticToggled(); automatic.Unchecked += (_, _) => AutomaticToggled();
        // The switch with what it does under its label (M/AppUpdateSettingsView.swift: autoUpdateToggle + autoUpdateDescription).
        appUpdateAutomaticRow = SettingsRow(rows, SettingsLabeled(SettingsTitled(AppUpdateStrings.AutoUpdateToggle, AppUpdateStrings.AutoUpdateDescription), automatic, top: true));

        var signature = SettingsText("", 10, DesignToken.Ink2);
        appUpdateSignatureRow = SettingsRow(rows, signature);
        // A build with no key says so on a row of its own, over the disabled button (M/AppUpdateSettingsView.swift:10-17);
        // the state's words move there and back as the section is rendered.
        appUpdateNoticeRow = SettingsRow(rows, new Grid());
        appUpdateNoticeRow.Visibility = Visibility.Collapsed;

        var status = SettingsText("", 11, DesignToken.Ink2, selectable: true);
        var button = SettingsPush(new Button());
        AutomationProperties.SetAutomationId(status, AppUpdateStatusId);
        AutomationProperties.SetAutomationId(button, AppUpdateButtonId);
        button.Click += async (_, _) => await AppUpdate.PressAsync(service.Snapshot.AppUpdateManifestUrlOverride);
        // The spinner while checking, staging or installing and the 140-wide bar while downloading lead the words (M/AppUpdateSettingsView.swift:54-67).
        appUpdateBusy = new ProgressRing { Width = 14, Height = 14, MinWidth = 0, MinHeight = 0, IsActive = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, DesignMetrics.Spacing.Sm, 0) };
        appUpdateDownload = new ProgressBar { Width = 140, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, DesignMetrics.Spacing.Sm, 0) };
        var state = new Grid();
        state.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); state.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); state.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        state.Children.Add(appUpdateBusy); Grid.SetColumn(appUpdateDownload, 1); state.Children.Add(appUpdateDownload); Grid.SetColumn(status, 2); state.Children.Add(status);
        appUpdateState = state;
        SettingsRow(rows, SettingsLabeled(state, button));

        // A failed check shows only here (M/AppUpdateSettingsView.swift failureNotice): the error beside a staged
        // update, which stays ready, and with the switch on the promise that the next check tries again.
        var keptError = SettingsText("", 11, DesignToken.WaitText, selectable: true);
        keptError.MaxLines = 3; keptError.TextTrimming = TextTrimming.CharacterEllipsis;
        var retry = SettingsText("", 10, DesignToken.Ink2);
        var failure = new StackPanel { Spacing = DesignMetrics.Spacing.Xs };
        failure.Children.Add(keptError); failure.Children.Add(retry);
        AutomationProperties.SetAutomationId(failure, AppUpdateRetryId);
        appUpdateFailureRow = SettingsRow(rows, failure);
        appUpdateFailureRow.Visibility = Visibility.Collapsed;
        appUpdateKeptError = keptError; appUpdateRetry = retry;

        var notes = SettingsText("", 11, DesignToken.Ink2, selectable: true);
        notes.MaxLines = 8; notes.TextTrimming = TextTrimming.CharacterEllipsis;
        appUpdateNotesRow = SettingsRow(rows, notes);
        appUpdateNotesRow.Visibility = Visibility.Collapsed;

        appUpdateAddress = address;
        appUpdateAddressHint = addressHint;
        appUpdateSignature = signature;
        appUpdateStatus = status;
        appUpdateButton = button;
        appUpdateNotes = notes;
        RenderAppUpdateSection();
        return rows;
    }

    internal static string AppVersionText =>
        typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    /// Puts the Core view on screen. Called when the section is built and again
    /// on every phase change, so the section follows a running check live.
    internal void RenderAppUpdateSection()
    {
        if (closing || appUpdateStatus is null || appUpdateButton is null) return;
        // The smoke run renders its fixtures as a signed build with the switch at its default (on), the way
        // AppUpdateSmoke.ExpectedStatuses describes them, whatever the profile saved.
        var view = AppUpdatePresentation.Describe(
            AppUpdate.State,
            appUpdateSmokeSignedBuild || AppUpdate.HasPublicKey,
            AppUpdate.BuiltInManifestUrl,
            at => at.ToLocalTime().ToString("HH:mm"),
            appUpdateSmokeSignedBuild || AppUpdate.Automatic);
        appUpdateStatus.Text = view.StatusText;
        appUpdateButton.Content = view.ButtonLabel;
        appUpdateButton.IsEnabled = view.ButtonEnabled;
        if (appUpdateSignature is not null) appUpdateSignature.Text = view.SignatureNotice;
        if (appUpdateAddress is not null) appUpdateAddress.IsEnabled = view.AddressFieldEnabled && view.SectionEnabled;
        if (appUpdateAddressHint is not null)
            appUpdateAddressHint.Text = view.BuiltInAddressLine ?? AppUpdateStrings.ManifestUrlHint;
        if (appUpdateNotes is not null)
        {
            appUpdateNotes.Text = view.Notes ?? "";
            if (appUpdateNotesRow is not null) appUpdateNotesRow.Visibility = view.Notes is null ? Visibility.Collapsed : Visibility.Visible;
        }
        // A build with no key shows neither address nor switch nor signature note; one with its own address shows it instead of the field.
        static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
        if (appUpdateAddressRow is not null) appUpdateAddressRow.Visibility = Shown(view.SectionEnabled && view.AddressFieldEnabled);
        if (appUpdateBuiltInRow is not null) appUpdateBuiltInRow.Visibility = Shown(view.SectionEnabled && !view.AddressFieldEnabled);
        if (appUpdateAutomaticRow is not null) appUpdateAutomaticRow.Visibility = Shown(view.SectionEnabled);
        if (appUpdateSignatureRow is not null) appUpdateSignatureRow.Visibility = Shown(view.SectionEnabled);
        if (appUpdateFailureRow is not null && appUpdateKeptError is not null && appUpdateRetry is not null)
        {
            appUpdateKeptError.Text = view.KeptError ?? ""; appUpdateKeptError.Visibility = Shown(view.KeptError is not null);
            appUpdateRetry.Text = view.RetryNotice ?? ""; appUpdateRetry.Visibility = Shown(view.RetryNotice is not null);
            appUpdateFailureRow.Visibility = Shown(view.SectionEnabled && (view.KeptError ?? view.RetryNotice) is not null);
        }
        if (appUpdateNoticeRow is not null && appUpdateState is not null)
        {
            var alone = !view.SectionEnabled;
            if (alone != ReferenceEquals(appUpdateNoticeRow.Child, appUpdateStatus))
            {
                if (alone) { appUpdateState.Children.Remove(appUpdateStatus); appUpdateNoticeRow.Child = appUpdateStatus; }
                else { appUpdateNoticeRow.Child = new Grid(); appUpdateState.Children.Add(appUpdateStatus); }
            }
            appUpdateNoticeRow.Visibility = Shown(alone);
        }
        // The state's ink (M/AppUpdateSettingsView.swift:48-70): accent and medium for a new version, waitText for a
        // failure or a build that cannot check, the primary ink while it works, the quiet ink otherwise.
        var phase = AppUpdate.State.Phase;
        var working = view.SectionEnabled && phase is AppUpdatePhase.Checking or AppUpdatePhase.Staging or AppUpdatePhase.Installing;
        var downloading = view.SectionEnabled && phase == AppUpdatePhase.Downloading;
        appUpdateStatus.Foreground = brushes.Brush(!view.SectionEnabled || phase == AppUpdatePhase.Failed ? DesignToken.WaitText
            : phase == AppUpdatePhase.Available ? DesignToken.Accent : working || downloading ? DesignToken.Ink : DesignToken.Ink2);
        appUpdateStatus.FontWeight = view.SectionEnabled && phase == AppUpdatePhase.Available ? Microsoft.UI.Text.FontWeights.Medium : Microsoft.UI.Text.FontWeights.Normal;
        if (appUpdateBusy is not null) { appUpdateBusy.IsActive = working; appUpdateBusy.Visibility = Shown(working); }
        if (appUpdateDownload is not null) { appUpdateDownload.Value = AppUpdate.State.DownloadFraction; appUpdateDownload.Visibility = Shown(downloading); }
    }

    /// The check at app start (when one is due) and the loop after it. It never
    /// delays the window: it is started after the first render, and its
    /// failures only reach the section.
    internal void BeginAutomaticAppUpdateCheck()
    {
        if (options.SmokeTest) return;
        // Housekeeping, each step best effort: nothing here may keep the loop, the check or the setup screen after it from
        // starting. A previous replacement could not delete the folder it was running from; this start clears it, after
        // putting back an install an interrupted swap left aside (which, with the install whole enough to run this, finds
        // nothing to do; the next helper is what puts a missing one back).
        var install = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        try { AppUpdateReplacement.RestoreInterruptedInstall(install); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Trace.TraceWarning("interrupted install not checked: " + ex.Message); }
        try { AppUpdateReplacement.PruneBackups(install); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Trace.TraceWarning("update backups not pruned: " + ex.Message); }
        // After the app replaced its own folder, Settings › Apps shows this version, not the setup program's.
        InstallerRegistration.SyncDisplayVersion(AppContext.BaseDirectory, AppVersionText);
        // Without it a quit during a sign-out would still install; the update itself goes on.
        try { WatchSessionEnding(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Trace.TraceWarning("session ending not watched: " + ex.Message); }
        StartAppUpdateLoop();
        CheckAppUpdateAtStart();
    }

    /// An update an install on quit kept (the app was opened again before the
    /// helper could swap) is checked at once and staged again from the package
    /// on disk; otherwise the start checks when one is due.
    private async void CheckAppUpdateAtStart()
    {
        if (closing || !AppUpdateCanRunAutomatically) return;
        var snapshot = service.Snapshot;
        // Failures land in the section (LastError); nothing here can throw past the coordinator.
        if (snapshot.AppUpdateAutoCheck && await AppUpdate.CheckKeptUpdateAsync(snapshot.AppUpdateManifestUrlOverride, DateTimeOffset.UtcNow)) return;
        CheckAppUpdateIfDue();
    }

    // ── The automatic update (AppUpdateAuto; M/AppStore+AppUpdate.swift) ──
    // Every coordinator call below runs on this window's dispatcher: the timer
    // ticks there, the awaits resume there, and the state the section, the
    // status bar and the banner read is only ever changed there.

    /// Ticks every `AppUpdateAuto.Tick`, and every `AppUpdateAuto.CountdownTick`
    /// while the restart banner counts down. Never created in a smoke run or a
    /// build that cannot verify updates.
    private DispatcherQueueTimer? appUpdateTimer;
    private AppUpdateRestartState appUpdateRestart = AppUpdateRestartState.Idle;
    /// The last check time saved to the profile, so each new one is saved once.
    private DateTimeOffset? appUpdateSavedCheck;
    private AppUpdatePhase? appUpdateShownPhase;
    /// Windows is signing out, restarting or shutting down (WM_QUERYENDSESSION):
    /// quitting then leaves the staged update for the next quit.
    private bool appUpdatePoweringOff;
    private InfoBar? appUpdateCountdown;
    private TextBlock? appUpdateCountdownMessage;

    /// The build checks and installs on its own: not a smoke run, it verifies
    /// manifests and it knows where they are.
    private bool AppUpdateCanRunAutomatically =>
        !options.SmokeTest && AppUpdate.HasPublicKey && AppUpdate.EffectiveManifestUrl(service.Snapshot.AppUpdateManifestUrlOverride) is not null;

    private bool AppUpdateAutomaticNow => service.Snapshot.AppUpdateAutoCheck && AppUpdateCanRunAutomatically;

    /// Every state change of the coordinator, on the dispatcher: the section
    /// follows it live; the status bar, the saved check time and the banner
    /// follow phase changes and new checks.
    private void AppUpdateStateChanged()
    {
        if (closing) return;
        RenderAppUpdateSection();
        var state = AppUpdate.State;
        if (state.CheckedAt is { } at && at != appUpdateSavedCheck && !options.SmokeTest)
        {
            appUpdateSavedCheck = at;
            _ = service.UpdateAsync(s => s with { AppUpdateLastCheckedAt = at.ToString("O") });
        }
        if (appUpdateShownPhase == state.Phase) return;
        appUpdateShownPhase = state.Phase;
        RefreshStatusBar();
        RenderAppUpdateCountdown();
    }

    private void StartAppUpdateLoop()
    {
        if (closing || !AppUpdateAutomaticNow) return;
        if (appUpdateTimer is null)
        {
            appUpdateTimer = DispatcherQueue.CreateTimer();
            appUpdateTimer.IsRepeating = true;
            appUpdateTimer.Tick += (_, _) => AppUpdateTick();
        }
        PaceAppUpdateTimer();
    }

    /// Stops the loop and a running countdown (the banner goes away; a "later" stays).
    private void StopAppUpdateLoop()
    {
        appUpdateTimer?.Stop();
        CancelAppUpdateCountdown();
    }

    /// The loop's pace: once a second while the banner counts down, every ten minutes otherwise.
    private void PaceAppUpdateTimer()
    {
        if (appUpdateTimer is not { } timer) return;
        var interval = appUpdateRestart.Kind == AppUpdateRestartKind.CountingDown ? AppUpdateAuto.CountdownTick : AppUpdateAuto.Tick;
        if (timer.IsRunning && timer.Interval == interval) return;
        timer.Stop(); timer.Interval = interval; timer.Start();
    }

    /// One pass of the loop: a check when one is due (not while the banner
    /// counts down), then the idle restart offer.
    private void AppUpdateTick()
    {
        if (closing) return;
        if (appUpdateRestart.Kind != AppUpdateRestartKind.CountingDown) CheckAppUpdateIfDue();
        OfferAppUpdateRestart();
    }

    /// Asks `AppUpdateAuto.CheckDue` (through the coordinator, which keeps the
    /// last attempt so a failed check waits for the next cycle) and checks when
    /// it says so; a newer version is then downloaded and staged.
    private async void CheckAppUpdateIfDue()
    {
        if (closing || !AppUpdateCanRunAutomatically) return;
        var snapshot = service.Snapshot;
        var last = DateTimeOffset.TryParse(snapshot.AppUpdateLastCheckedAt, out var parsed) ? parsed : (DateTimeOffset?)null;
        // Failures land in the section (LastError); nothing here can throw past the coordinator.
        await AppUpdate.CheckAutomaticallyIfDueAsync(snapshot.AppUpdateManifestUrlOverride, snapshot.AppUpdateAutoCheck, last, DateTimeOffset.UtcNow);
    }

    /// Off stops the loop, a download and the countdown; a staged update then
    /// installs only from "restart now". On starts the loop, checks when due and
    /// downloads what a check already found.
    private void AppUpdateAutomaticChanged(bool on)
    {
        if (closing || options.SmokeTest) return;
        if (!on)
        {
            StopAppUpdateLoop();
            if (AppUpdate.State.Phase == AppUpdatePhase.Downloading) AppUpdate.CancelDownload();
            RefreshStatusBar(); RenderAppUpdateSection();
            return;
        }
        if (!AppUpdateCanRunAutomatically) return;
        StartAppUpdateLoop();
        if (AppUpdate.State.Phase == AppUpdatePhase.Available) _ = AppUpdate.DownloadAsync();
        else CheckAppUpdateIfDue();
        RefreshStatusBar(); RenderAppUpdateSection();
    }

    /// One step of the idle-restart offer (`AppUpdateAuto.RestartOffer`): starts
    /// the countdown banner when the app is idle, cancels it quietly when it no
    /// longer is, and restarts to install when it runs out.
    private void OfferAppUpdateRestart()
    {
        if (closing || appUpdate is null) return;
        var ready = appUpdate.State.Phase == AppUpdatePhase.Ready;
        // Nothing staged and no banner or snooze: nothing to decide.
        if (!ready && appUpdateRestart.Kind == AppUpdateRestartKind.Idle) return;
        var now = DateTimeOffset.UtcNow;
        var decision = AppUpdateAuto.RestartOffer(appUpdateRestart, ready, AppUpdateAutomaticNow, AppUpdateAuto.IdleBlockers(CollectIdleFacts(now), now), now);
        appUpdateRestart = decision.State;
        PaceAppUpdateTimer();
        RenderAppUpdateCountdown();
        if (decision.Action == AppUpdateRestartAction.Restart) RestartForAppUpdateAutomatically();
    }

    /// The countdown ran out. In this one dispatcher turn, with nothing awaited
    /// in between, the app is asked again whether it is idle (input aside, as
    /// during the countdown), and only then is the helper started; it quits the
    /// app and starts it again. Something that started meanwhile leaves the
    /// update ready, and the next idle tick offers a fresh countdown.
    private void RestartForAppUpdateAutomatically()
    {
        if (closing || appUpdate is null) return;
        var now = DateTimeOffset.UtcNow;
        var blockers = AppUpdateAuto.IdleBlockers(CollectIdleFacts(now), now);
        if (appUpdate.State.Phase != AppUpdatePhase.Ready || !AppUpdateAutomaticNow
            || blockers.Any(blocker => blocker != AppIdleBlocker.UserActive)) return;
        // InstallAsync reaches the helper without awaiting anything unfinished, so the start happens in this turn.
        _ = appUpdate.InstallAsync(relaunch: true);
    }

    /// "Restart now", from the banner or the status bar: a running countdown is cleared first.
    private void RestartForAppUpdateNow()
    {
        appUpdateRestart = AppUpdateRestartState.Idle;
        PaceAppUpdateTimer(); RenderAppUpdateCountdown();
        _ = AppUpdate.InstallAsync();
    }

    /// The banner's "later": no banner for `AppUpdateAuto.Snooze`; quitting still installs.
    private void PostponeAppUpdateRestart()
    {
        appUpdateRestart = AppUpdateAuto.Later(DateTimeOffset.UtcNow);
        PaceAppUpdateTimer(); RenderAppUpdateCountdown();
    }

    /// Takes the banner away without a word; a "later" is kept.
    private void CancelAppUpdateCountdown()
    {
        if (appUpdateRestart.Kind == AppUpdateRestartKind.CountingDown) appUpdateRestart = AppUpdateRestartState.Idle;
        RenderAppUpdateCountdown();
    }

    /// The countdown banner over the detail column (M/WorkspaceView.swift appUpdateCountdownBanner): the
    /// message with the seconds left, "later" and the accent "restart now". Hidden until a countdown starts.
    private InfoBar BuildAppUpdateCountdown()
    {
        var message = new TextBlock { TextSize = DesignMetrics.Type.Row, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = brushes.Brush(DesignToken.Ink) };
        var later = Button(Locale.Get("appUpdate.countdown.later"), () => { PostponeAppUpdateRestart(); return Task.CompletedTask; });
        AutomationProperties.SetAutomationId(later, AppUpdateCountdownId + "-later");
        var restart = Button(Locale.Get("appUpdate.countdown.restartNow"), () => { RestartForAppUpdateNow(); return Task.CompletedTask; });
        restart.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        AutomationProperties.SetAutomationId(restart, AppUpdateCountdownId + "-restart");
        var row = new Grid { ColumnSpacing = DesignMetrics.Spacing.Md };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(message);
        Grid.SetColumn(later, 1); row.Children.Add(later);
        Grid.SetColumn(restart, 2); row.Children.Add(restart);
        // No icon, and the row is the whole banner, as on the Mac: with no title, message or action button the InfoBar moves its
        // content into the first row (its NoBannerContent state), so no empty title or message line is left above it.
        var bar = new InfoBar
        {
            Severity = InfoBarSeverity.Informational, IsIconVisible = false, IsClosable = false, IsOpen = false, Visibility = Visibility.Collapsed,
            Content = row, Background = brushes.Brush(DesignToken.AccentSoft), CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetAutomationId(bar, AppUpdateCountdownId);
        appUpdateCountdown = bar; appUpdateCountdownMessage = message;
        return bar;
    }

    /// Shows the banner while a countdown runs, with the seconds left; hides it otherwise.
    private void RenderAppUpdateCountdown()
    {
        if (appUpdateCountdown is not { } bar || appUpdateCountdownMessage is not { } message) return;
        var version = appUpdate?.State.Availability?.Manifest.Version;
        var shown = !closing && appUpdateRestart.Kind == AppUpdateRestartKind.CountingDown && !string.IsNullOrEmpty(version);
        bar.IsOpen = shown; bar.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown) return;
        var seconds = Math.Max(0, (int)Math.Ceiling((appUpdateRestart.Until - DateTimeOffset.UtcNow).TotalSeconds));
        message.Text = Locale.Get("appUpdate.countdown.message", new Dictionary<string, string> { ["version"] = version!, ["seconds"] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    }

    /// Called as the window closes, before `ShutdownAppUpdateAsync`: a staged
    /// update is installed once the app has gone, without starting it again
    /// (`AppUpdateCoordinator.InstallOnQuitAsync`). Skipped in a smoke run, while
    /// an install is already under way (it quits on its own) and while Windows
    /// is signing out or shutting down.
    internal async Task InstallAppUpdateOnQuitAsync()
    {
        appUpdateTimer?.Stop();
        if (options.SmokeTest || appUpdate is null || appUpdate.State.Phase == AppUpdatePhase.Installing) return;
        try { await appUpdate.InstallOnQuitAsync(appUpdatePoweringOff); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Trace.TraceWarning("update on quit not started: " + ex.Message); }
    }

    internal Task ShutdownAppUpdateAsync()
    {
        appUpdateTimer?.Stop();
        var pending = appUpdate?.ShutdownAsync() ?? Task.CompletedTask;
        appUpdateService?.Dispose();
        return pending;
    }

    /// Smoke: drives the real control through every fixture phase, reads the
    /// status and the button back off the built tree, proves a key-less build
    /// refuses to check, flips the saved switch and puts everything back.
    internal async Task<AppUpdateSmokeOutcome> RunAppUpdateSectionSmoke()
    {
        var panel = BuildAppUpdateSection();
        var status = SettingsElement<TextBlock>(panel, AppUpdateStatusId);
        var button = SettingsElement<Button>(panel, AppUpdateButtonId);
        var toggle = SettingsElement<ToggleButton>(panel, AppUpdateToggleId);

        // Windows rule 1 first, on the real control. A build with no public key —
        // which is what CI builds are until the key is configured — must show the
        // one sentence with the button disabled before anything else is tried.
        var refused = AppUpdatePresentation.Describe(new AppUpdateState(), false, null, _ => "00:00");
        if (!AppUpdate.HasPublicKey)
        {
            RenderAppUpdateSection();
            Require(status.Text == AppUpdateStrings.NoPublicKeyNotice && !button.IsEnabled,
                "a build without a public key must not offer an update check");
        }

        var statuses = new List<string>();
        var buttons = new List<string>();
        var before = AppUpdate.State;
        try
        {
            appUpdateSmokeSignedBuild = true;
            foreach (var fixture in AppUpdateSmoke.FixtureStates)
            {
                AppUpdate.ShowForSmoke(fixture);
                RenderAppUpdateSection();
                statuses.Add(status.Text);
                buttons.Add((string)button.Content!);
            }
        }
        finally
        {
            appUpdateSmokeSignedBuild = false;
            AppUpdate.ShowForSmoke(before);
            RenderAppUpdateSection();
        }

        await RunAppUpdateStagedSmoke(before);

        var outcome = await AppUpdateSmoke.RunAsync(
            statuses, buttons,
            !refused.SectionEnabled && !refused.ButtonEnabled && refused.StatusText == AppUpdateStrings.NoPublicKeyNotice,
            () => service.Snapshot.AppUpdateAutoCheck,
            value => service.UpdateAsync(s => s with { AppUpdateAutoCheck = value }));

        // The idle facts read the real window once (deterministic: no helper, no countdown): a composer draft holds the restart.
        Require(views.Values.FirstOrDefault() is { } draftPane && draftPane.SmokeDraftHoldsRestart(),
            "a composer draft must hold the automatic restart (AppIdleBlocker.Draft in CollectIdleFacts)");

        // The switch is named for its label, and the label stands beside it in the same row.
        Require(AutomationProperties.GetName(toggle) == AppUpdateStrings.AutoUpdateToggle && SettingsElements(panel).OfType<TextBlock>().Any(label => label.Text == AppUpdateStrings.AutoUpdateToggle),
            "the app update section must show the automatic check switch");
        return outcome;
    }

    /// Smoke: a staged update with the switch on puts "installs at the next
    /// launch" and "restart now" in the status bar instead of the badge, a newer
    /// version not yet staged shows the badge, and a countdown shows the banner
    /// with the version and the seconds left. Fixture state only: nothing is
    /// checked, downloaded or installed, and no button is pressed.
    private async Task RunAppUpdateStagedSmoke(AppUpdateState before)
    {
        var savedAutomatic = service.Snapshot.AppUpdateAutoCheck;
        var savedRestart = appUpdateRestart;
        var ready = AppUpdateSmoke.FixtureStates.First(state => state is { Phase: AppUpdatePhase.Ready, LastError: null });
        var available = AppUpdateSmoke.FixtureStates.First(state => state.Phase == AppUpdatePhase.Available);
        var version = ready.Availability!.Manifest.Version;
        try
        {
            if (!savedAutomatic) await service.UpdateAsync(s => s with { AppUpdateAutoCheck = true });
            AppUpdate.ShowForSmoke(ready);
            RefreshStatusBar();
            var stagedWords = Locale.Get("window.status.updateStaged", new Dictionary<string, string> { ["version"] = version });
            Require(updateStaged.Visibility == Visibility.Visible && updateBadge?.Visibility == Visibility.Collapsed && updateStagedText.Text == stagedWords
                    && updateRestartNow is { } restart && AutomationProperties.GetAutomationId(restart) == AppUpdateRestartId
                    && AutomationProperties.GetName(restart) == Locale.Get("window.status.updateRestartNow")
                    && ToolTipService.GetToolTip(restart) as string == Locale.Get("window.status.updateStagedHelp"),
                $"a staged update with the switch on must show '{stagedWords}' and 'restart now' in the status bar instead of the badge; got staged {updateStaged.Visibility} '{updateStagedText.Text}', badge {updateBadge?.Visibility}");

            appUpdateRestart = AppUpdateRestartState.CountingDown(DateTimeOffset.UtcNow + AppUpdateAuto.Countdown);
            RenderAppUpdateCountdown();
            root.UpdateLayout();
            string Message(int seconds) => Locale.Get("appUpdate.countdown.message", new Dictionary<string, string> { ["version"] = version, ["seconds"] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            var bar = appUpdateCountdown!;
            var buttons = SettingsElements((Grid)bar.Content).OfType<Button>().ToList();
            Require(bar is { IsOpen: true, Visibility: Visibility.Visible } && bar.ActualHeight > 0 && ReferenceEquals(detailTop.Children[0], bar)
                    && appUpdateCountdownMessage!.Text is var said && (said == Message(60) || said == Message(59))
                    && buttons.Select(b => AutomationProperties.GetAutomationId(b)).SequenceEqual([AppUpdateCountdownId + "-later", AppUpdateCountdownId + "-restart"])
                    && buttons.Select(b => AutomationProperties.GetName(b)).SequenceEqual([Locale.Get("appUpdate.countdown.later"), Locale.Get("appUpdate.countdown.restartNow")]),
                $"a countdown must show the banner over the detail column with '{Message(60)}', 'later' and 'restart now'; got {bar.Visibility} {bar.ActualHeight:F1} high, '{appUpdateCountdownMessage!.Text}', buttons {string.Join(", ", buttons.Select(b => AutomationProperties.GetAutomationId(b)))}");

            appUpdateRestart = AppUpdateRestartState.Idle;
            RenderAppUpdateCountdown();
            AppUpdate.ShowForSmoke(available);
            RefreshStatusBar();
            Require(bar.Visibility == Visibility.Collapsed && updateStaged.Visibility == Visibility.Collapsed && updateBadge?.Visibility == Visibility.Visible,
                $"with no countdown the banner must hide, and a newer version not yet staged must show the badge, not the staged words; got banner {bar.Visibility}, staged {updateStaged.Visibility}, badge {updateBadge?.Visibility}");
        }
        finally
        {
            appUpdateRestart = savedRestart;
            AppUpdate.ShowForSmoke(before);
            if (!savedAutomatic) await service.UpdateAsync(s => s with { AppUpdateAutoCheck = false });
            RefreshStatusBar(); RenderAppUpdateCountdown(); RenderAppUpdateSection();
        }
    }

    /// What the app is doing right now, for `AppUpdateAuto.IdleBlockers`: one
    /// source per row of the idle table (M/AppStore+AppUpdate.swift appIdleFacts).
    /// Everything is read on the dispatcher without awaiting, so a caller can
    /// act on the answer in the same turn.
    private AppIdleFacts CollectIdleFacts(DateTimeOffset now)
    {
        var state = service.Snapshot;
        var panes = views.Values.ToArray();
        var delegation = service.Delegation;
        // Agent sessions running (remote ones too), panes starting a run, sign-in resends held for a CLI update,
        // delegation runs ending, children still being made and the coordinator's own work under way (pending notices and
        // follow-ups, children starting, card panes, a merge going; M/DelegationCoordinator.swift pendingWork).
        var agentRuns = state.Sessions.Count(s => s.Kind != "shell" && (s.Status == "running" || service.IsSessionRunning(s.Id)))
            + panes.Count(p => p.UpdateStarting) + loginResendsAwaitingUpdate.Count
            + (delegation is null ? 0 : state.Sessions.Count(s => delegation.EndingRuns.Contains(s.Id)) + delegation.PendingWork())
            + service.DelegationChildren.Count(row => row.State == ChildState.Creating);
        // Permission, question and plan cards and Antigravity's card per pane, and requests to open a web page.
        var pendingCards = panes.Sum(p => p.DashboardAttention.Total) + views.Keys.Sum(id => agentUrlPrompts.ForPane(id).Count);
        // A shell running a command (the terminal probe keeps its status), agent terminals at work, setup, sign-ins.
        var terminals = state.Sessions.Count(s => s.Kind == "shell" && s.Status == "running")
            + agentTerminals.Values.Count(terminal => terminal.IsRunning) + (SetupRunning ? 1 : 0)
            + loginBusy.Count + accountLoginWaits.Count + accountChanges.Count + (antigravityLoginWindow is { Open: true } ? 1 : 0);
        var cliWork = AnyCliUpdateRunning || manuallyUpdatingProvider is not null || pluginOperations.IsRunning || claudeModelReset || toolkitRunning;
        var host = remoteHost?.Current();
        var dialog = root.XamlRoot is { } xamlRoot && VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot).Any(popup => popup.Child is ContentDialog);
        return new AppIdleFacts
        {
            AgentRuns = agentRuns,
            PendingCards = pendingCards,
            // Only what the person queued: delegation holds live in their own store and survive a restart.
            QueuedInputs = panes.Sum(p => p.MobileQueuedCount),
            Drafts = panes.Count(p => p.HasDraft),
            Terminals = terminals,
            CliWork = cliWork,
            ScreenSharing = screenHub?.Sessions.Count > 0,
            PhoneUploads = mobileRouter?.OpenUploadCount ?? 0,
            RemoteHostRuns = Math.Max(remoteHostRuns, host?.ActiveRuns ?? 0),
            SharingWorkspaces = host?.Enabled == true,
            BlockingModal = dialogOpen || dialog || settingsWindow is not null || setupWindow is not null,
            LastUserInput = LastUserInput(now),
            LastAgentActivity = providerLastActive.Count == 0 ? null : providerLastActive.Values.Max(),
        };
    }

    private sealed partial class PaneView
    {
        /// <summary>Text in the composer, attachments for the next request or attachments still being read in (AppIdleFacts.Drafts).</summary>
        internal bool HasDraft => HasComposerContent || attachmentsLoading;

        /// <summary>
        /// Smoke: with words in this composer, the window's idle facts (read once, as the restart offer reads them) hold the
        /// automatic restart for a draft. Nothing is checked, installed or restarted, and the composer's text is put back unsaved.
        /// </summary>
        internal bool SmokeDraftHoldsRestart()
        {
            var was = input.Text;
            try
            {
                updating = true; input.Text = "draft holds the restart"; updating = false;
                var now = DateTimeOffset.UtcNow;
                return AppUpdateAuto.IdleBlockers(owner.CollectIdleFacts(now), now).Contains(AppIdleBlocker.Draft);
            }
            finally { updating = true; input.Text = was; updating = false; }
        }
    }

    // ── Windows signing out, restarting or shutting down ──
    // The window procedure is subclassed (comctl32 SetWindowSubclass) so the
    // flag is set on the UI thread before anything closes. WM_QUERYENDSESSION
    // reaches every top-level window of an unpackaged WinUI 3 app; whatever its
    // reason (sign-out, restart, shutdown, an installer closing apps), a quit
    // that follows leaves the staged update for the next quit, since the swap
    // could be cut off half-way. WM_ENDSESSION with wParam 0 means the end was
    // called off.
    private const uint WmQueryEndSession = 0x0011, WmEndSession = 0x0016, WmNcDestroy = 0x0082;
    private const nuint SessionEndingSubclassId = 0x4D43_5155;
    private delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
    /// Kept alive as long as the window: the native side only holds a pointer to it.
    private SubclassProc? sessionEndingProc;

    private void WatchSessionEnding()
    {
        if (sessionEndingProc is not null) return;
        sessionEndingProc = (hwnd, message, wParam, lParam, id, _) =>
        {
            if (message == WmQueryEndSession) appUpdatePoweringOff = true;
            else if (message == WmEndSession && wParam == 0) appUpdatePoweringOff = false;
            else if (message == WmNcDestroy) RemoveWindowSubclass(hwnd, sessionEndingProc!, id);
            return DefSubclassProc(hwnd, message, wParam, lParam);
        };
        SetWindowSubclass(WinRT.Interop.WindowNative.GetWindowHandle(this), sessionEndingProc, SessionEndingSubclassId, 0);
    }

    // ── Keyboard and mouse input anywhere in the session (AppIdleBlocker.UserActive) ──
    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint Size; public uint Time; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);

    /// When the last input arrived, from the 32-bit tick counts GetLastInputInfo
    /// and Environment.TickCount share (the difference stays right across their
    /// wrap). A failed call reads as input just now, so the app never restarts on it.
    private static DateTimeOffset LastUserInput(DateTimeOffset now)
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return now;
        var idle = unchecked((uint)Environment.TickCount - info.Time);
        return now - TimeSpan.FromMilliseconds(idle);
    }
}
