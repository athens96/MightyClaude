using System.Diagnostics;
using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

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
            LaunchUpdateHelperAsync);
        coordinator.StateChanged += () => DispatcherQueue.TryEnqueue(RenderAppUpdateSection);
        return coordinator;
    }

    /// Starts the helper detached with a minimal environment and quits the app.
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
        DispatcherQueue.TryEnqueue(Close);
        return Task.CompletedTask;
    }

    // The live controls. Settings builds a fresh set each time it opens, so the
    // section never re-parents a control that still belongs to a closed dialog;
    // these fields point at the newest set for the live phase updates.
    private TextBlock? appUpdateStatus, appUpdateNotes, appUpdateAddressHint, appUpdateSignature;
    private Button? appUpdateButton;
    private TextBox? appUpdateAddress;
    // The rows a build with a key shows (typed address, built-in address, automatic check, signature) and the notes row.
    private Border? appUpdateAddressRow, appUpdateBuiltInRow, appUpdateAutomaticRow, appUpdateSignatureRow, appUpdateNoticeRow, appUpdateNotesRow;
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

        var automatic = SettingsSwitch(AppUpdateStrings.AutoCheckToggle, service.Snapshot.AppUpdateAutoCheck, AppUpdateToggleId);
        async void AutomaticToggled() => await service.UpdateAsync(s => s with { AppUpdateAutoCheck = automatic.IsChecked == true });
        automatic.Checked += (_, _) => AutomaticToggled(); automatic.Unchecked += (_, _) => AutomaticToggled();
        appUpdateAutomaticRow = SettingsRow(rows, SettingsLabeled(SettingsText(AppUpdateStrings.AutoCheckToggle), automatic));

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
        var view = AppUpdatePresentation.Describe(
            AppUpdate.State,
            appUpdateSmokeSignedBuild || AppUpdate.HasPublicKey,
            AppUpdate.BuiltInManifestUrl,
            at => at.ToLocalTime().ToString("HH:mm"));
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

    /// The once-a-day check at app start. It never delays the window: it is
    /// started after the first render and its failures only reach the section.
    internal void BeginAutomaticAppUpdateCheck()
    {
        if (options.SmokeTest) return;
        // A previous replacement could not delete the folder it was running
        // from; this start clears it.
        try { AppUpdateReplacement.PruneBackups(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)); }
        catch (IOException) { /* Housekeeping must not change startup. */ }
        var snapshot = service.Snapshot;
        var last = DateTimeOffset.TryParse(snapshot.AppUpdateLastCheckedAt, out var parsed) ? parsed : (DateTimeOffset?)null;
        if (!AppUpdateCoordinator.IsDue(snapshot.AppUpdateAutoCheck, AppUpdate.HasPublicKey, last, DateTimeOffset.UtcNow)) return;
        _ = Task.Run(async () =>
        {
            await AppUpdate.CheckAsync(snapshot.AppUpdateManifestUrlOverride);
            if (AppUpdate.State.CheckedAt is { } at)
                await service.UpdateAsync(s => s with { AppUpdateLastCheckedAt = at.ToString("O") });
        });
    }

    internal Task ShutdownAppUpdateAsync()
    {
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

        var outcome = await AppUpdateSmoke.RunAsync(
            statuses, buttons,
            !refused.SectionEnabled && !refused.ButtonEnabled && refused.StatusText == AppUpdateStrings.NoPublicKeyNotice,
            () => service.Snapshot.AppUpdateAutoCheck,
            value => service.UpdateAsync(s => s with { AppUpdateAutoCheck = value }));

        // The switch is named for its label, and the label stands beside it in the same row.
        Require(AutomationProperties.GetName(toggle) == AppUpdateStrings.AutoCheckToggle && SettingsElements(panel).OfType<TextBlock>().Any(label => label.Text == AppUpdateStrings.AutoCheckToggle),
            "the app update section must show the automatic check switch");
        return outcome;
    }
}
