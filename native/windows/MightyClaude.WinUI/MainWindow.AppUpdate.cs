using System.Diagnostics;
using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

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
    /// The helper waits for this process, verifies the package again and only
    /// then touches the install. It never runs elevated.
    private Task LaunchUpdateHelperAsync(AppUpdateInstallPlan plan)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, AppUpdateService.ExecutableName))
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

    private StackPanel BuildAppUpdateSectionFromState() => BuildAppUpdateSection();

    internal StackPanel BuildAppUpdateSection()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = AppUpdateStrings.CurrentVersionLabel + " · " + AppVersionText,
            FontSize = 12,
        });

        var address = new TextBox { PlaceholderText = AppUpdateStrings.ManifestUrlPlaceholder, FontSize = 12 };
        AutomationProperties.SetAutomationId(address, AppUpdateAddressId);
        address.Text = service.Snapshot.AppUpdateManifestUrlOverride ?? "";
        address.TextChanged += async (_, _) =>
        {
            var typed = address.Text;
            await service.UpdateAsync(s => s with { AppUpdateManifestUrlOverride = typed.Length == 0 ? null : typed });
        };
        var addressHint = new TextBlock { FontSize = 11, Opacity = .7, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(address);
        panel.Children.Add(addressHint);

        var automatic = new ToggleSwitch
        {
            Header = AppUpdateStrings.AutoCheckToggle,
            IsOn = service.Snapshot.AppUpdateAutoCheck,
            OffContent = "",
            OnContent = "",
        };
        AutomationProperties.SetAutomationId(automatic, AppUpdateToggleId);
        automatic.Toggled += async (_, _) => await service.UpdateAsync(s => s with { AppUpdateAutoCheck = automatic.IsOn });
        panel.Children.Add(automatic);

        var signature = new TextBlock { FontSize = 11, Opacity = .7, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(signature);

        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var button = new Button();
        AutomationProperties.SetAutomationId(status, AppUpdateStatusId);
        AutomationProperties.SetAutomationId(button, AppUpdateButtonId);
        button.Click += async (_, _) => await AppUpdate.PressAsync(service.Snapshot.AppUpdateManifestUrlOverride);
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(status);
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        panel.Children.Add(row);

        var notes = new TextBlock { FontSize = 11, Opacity = .7, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        panel.Children.Add(notes);

        appUpdateAddress = address;
        appUpdateAddressHint = addressHint;
        appUpdateSignature = signature;
        appUpdateStatus = status;
        appUpdateButton = button;
        appUpdateNotes = notes;
        RenderAppUpdateSection();
        return panel;
    }

    internal static string AppVersionText =>
        typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    /// Puts the Core view on screen. Called when the section is built and again
    /// on every phase change, so the section follows a running check live.
    internal void RenderAppUpdateSection()
    {
        if (closing || appUpdateStatus is null || appUpdateButton is null) return;
        var view = AppUpdate.Describe(at => at.ToLocalTime().ToString("HH:mm"));
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
            appUpdateNotes.Visibility = view.Notes is null ? Visibility.Collapsed : Visibility.Visible;
        }
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
        var status = AppUpdateDescendants(panel).OfType<TextBlock>()
            .First(block => AutomationProperties.GetAutomationId(block) == AppUpdateStatusId);
        var button = AppUpdateDescendants(panel).OfType<Button>()
            .First(control => AutomationProperties.GetAutomationId(control) == AppUpdateButtonId);
        var toggle = AppUpdateDescendants(panel).OfType<ToggleSwitch>()
            .First(control => AutomationProperties.GetAutomationId(control) == AppUpdateToggleId);

        var statuses = new List<string>();
        var buttons = new List<string>();
        var before = AppUpdate.State;
        try
        {
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
            AppUpdate.ShowForSmoke(before);
            RenderAppUpdateSection();
        }

        // Windows rule 1, on the real control: a build with no public key shows
        // the one sentence and the button cannot be pressed.
        using var keyless = new AppUpdateService(Path.GetTempPath(), publicKey: null);
        var refused = AppUpdatePresentation.Describe(
            new AppUpdateState(), keyless.VerifiesSignatures, null, _ => "00:00");

        var outcome = await AppUpdateSmoke.RunAsync(
            statuses, buttons,
            !refused.SectionEnabled && !refused.ButtonEnabled && refused.StatusText == AppUpdateStrings.NoPublicKeyNotice,
            () => service.Snapshot.AppUpdateAutoCheck,
            value => service.UpdateAsync(s => s with { AppUpdateAutoCheck = value }));

        Require(toggle.Header as string == AppUpdateStrings.AutoCheckToggle, "앱 업데이트 섹션에 자동 확인 스위치가 없습니다.");
        return outcome;
    }

    private static IEnumerable<DependencyObject> AppUpdateDescendants(Panel panel)
    {
        foreach (var child in panel.Children)
        {
            yield return child;
            if (child is Panel nested)
                foreach (var grandchild in AppUpdateDescendants(nested)) yield return grandchild;
        }
    }
}
