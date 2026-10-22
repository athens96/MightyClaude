using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace MightyClaude.Core;

/// The public key and the default manifest address the build carries.
///
/// `scripts/build-windows.ps1` stamps them into the assembly as
/// `AssemblyMetadata` when the build variables are set, the way
/// `scripts/build-macos.sh` stamps `Info.plist` on macOS. A build without the
/// key reads null here, and Windows rule 1 then refuses to check at all.
public static class AppUpdateBuild
{
    public const string PublicKeyMetadata = "MightyUpdatePublicKey";
    public const string ManifestUrlMetadata = "MightyUpdateManifestURL";

    public static byte[]? PublicKey(Assembly? assembly = null) =>
        Decode(Metadata(assembly, PublicKeyMetadata));

    public static string? ManifestUrl(Assembly? assembly = null) =>
        Metadata(assembly, ManifestUrlMetadata) is { Length: > 0 } value && AppUpdateManifest.Allowed(value) ? value : null;

    /// A 32-byte raw Ed25519 key in base64; anything else reads as absent.
    internal static byte[]? Decode(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        Span<byte> buffer = stackalloc byte[64];
        if (!Convert.TryFromBase64String(base64.Trim(), buffer, out var written)) return null;
        return written == Ed25519Verify.PublicKeyBytes ? buffer[..written].ToArray() : null;
    }

    private static string? Metadata(Assembly? assembly, string key) =>
        (assembly ?? Assembly.GetEntryAssembly())?
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == key)?.Value;

    /// The architecture of the package this machine needs.
    public static string Architecture =>
        RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
}

/// What the app update section is showing right now. Not saved state.
public sealed record AppUpdateState
{
    public AppUpdatePhase Phase { get; init; } = AppUpdatePhase.Idle;
    public double DownloadFraction { get; init; }
    public AppUpdateAvailability? Availability { get; init; }
    public string? PackagePath { get; init; }
    public string? StagedDirectory { get; init; }
    public AppUpdateInstallPlan? InstallPlan { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset? CheckedAt { get; init; }
    /// The last failure, kept beside Ready so a failed background check never
    /// takes a staged update away; cleared by the next success.
    public string? LastError { get; init; }
}

/// One rendering of the section: every string WinUI puts on screen and whether
/// the button may be pressed. WinUI adds no copy and makes no decision of its own.
public sealed record AppUpdateSectionView(
    bool SectionEnabled,
    string SignatureNotice,
    string? BuiltInAddressLine,
    bool AddressFieldEnabled,
    string StatusText,
    string ButtonLabel,
    bool ButtonEnabled,
    string? Notes,
    string? KeptError = null,
    string? RetryNotice = null);

public static class AppUpdatePresentation
{
    /// Windows rule 1: with no public key the section shows one sentence and
    /// the button is disabled — there is no check, not even in a dev build.
    /// Windows rule 3: a build that carries an address ignores a typed one, so
    /// the address field is shown disabled with the built-in address beside it.
    /// `automatic` is the saved switch: a staged update reads "installed at the
    /// next launch" only while quitting installs it (M/AppUpdateSettingsView.swift).
    public static AppUpdateSectionView Describe(
        AppUpdateState state, bool hasPublicKey, string? builtInAddress, Func<DateTimeOffset, string> formatTime, bool automatic = true)
    {
        if (!hasPublicKey)
            return new(false, AppUpdateStrings.NoPublicKeyNotice, null, false,
                AppUpdateStrings.NoPublicKeyNotice, AppUpdateStrings.CheckButton, false, null);

        var version = state.Availability?.Manifest.Version ?? "";
        var status = state.Phase switch
        {
            AppUpdatePhase.Checking => AppUpdateStrings.Checking,
            AppUpdatePhase.UpToDate => AppUpdateStrings.UpToDate,
            AppUpdatePhase.Available => AppUpdateStrings.AvailableTemplate.Replace("{version}", version),
            AppUpdatePhase.Downloading => AppUpdateStrings.DownloadingTemplate.Replace(
                "{percent}", ((int)(state.DownloadFraction * 100)).ToString()),
            AppUpdatePhase.Staging => AppUpdateStrings.StagingProgress,
            // Turned off, quitting does not install: the update only waits for "restart now".
            AppUpdatePhase.Ready => (automatic ? AppUpdateStrings.StagedTemplate : AppUpdateStrings.ReadyTemplate).Replace("{version}", version),
            AppUpdatePhase.Installing => AppUpdateStrings.Installing,
            AppUpdatePhase.Failed => state.ErrorMessage ?? AppUpdateStrings.NotCheckedYet,
            _ => state.CheckedAt is { } at
                ? AppUpdateStrings.LastCheckedTemplate.Replace("{time}", formatTime(at))
                : AppUpdateStrings.NotCheckedYet,
        };
        var (label, enabled) = state.Phase switch
        {
            AppUpdatePhase.Available => (AppUpdateStrings.DownloadButton, true),
            AppUpdatePhase.Downloading => (AppUpdateStrings.CancelButton, true),
            AppUpdatePhase.Ready => (AppUpdateStrings.RestartNowButton, true),
            AppUpdatePhase.Checking or AppUpdatePhase.Staging or AppUpdatePhase.Installing =>
                (AppUpdateStrings.InProgressButton, false),
            _ => (AppUpdateStrings.CheckButton, true),
        };
        var notes = state.Phase is AppUpdatePhase.Available or AppUpdatePhase.Ready
            ? state.Availability?.Manifest.Notes
            : null;
        // A failure only shows here: beside a staged update, which stays ready,
        // and with the switch on as the promise that the next check tries again.
        var failed = state.LastError is not null && (state.Phase == AppUpdatePhase.Ready || automatic);
        return new(
            true,
            AppUpdateStrings.SignatureVerified,
            builtInAddress is null ? null : AppUpdateStrings.BuiltInAddressTemplate.Replace("{address}", builtInAddress),
            builtInAddress is null,
            status, label, enabled,
            string.IsNullOrEmpty(notes) ? null : notes,
            failed && state.Phase == AppUpdatePhase.Ready ? state.LastError : null,
            failed && automatic ? AppUpdateStrings.RetryNotice : null);
    }
}

/// Drives the update through its phases. Every decision lives here; WinUI
/// renders <see cref="AppUpdateSectionView"/> and forwards the one button.
///
/// With the "automatic" switch on (read live through `automatic`), a check that
/// finds a newer version goes on to download and stage it, a check that finds
/// the version already staged keeps it, and a failure while an update is staged
/// only lands in `LastError`. `InstallOnQuitAsync` installs the staged update
/// when the app quits, without starting it again (AppUpdateAuto).
public sealed class AppUpdateCoordinator
{
    public event Action? StateChanged;

    private readonly AppUpdateService service;
    private readonly string currentVersion;
    private readonly string architecture;
    private readonly string? installDirectory;
    private readonly Func<AppUpdateInstallPlan, Task>? launchHelper;
    private readonly Func<bool> automatic;
    private CancellationTokenSource? work;
    private bool closing;
    private bool checking;

    public AppUpdateState State { get; private set; } = new();
    public string? BuiltInManifestUrl { get; }

    public AppUpdateCoordinator(
        AppUpdateService service,
        string currentVersion,
        string? builtInManifestUrl = null,
        string? architecture = null,
        string? installDirectory = null,
        Func<AppUpdateInstallPlan, Task>? launchHelper = null,
        Func<bool>? automatic = null)
    {
        this.service = service;
        this.automatic = automatic ?? (() => false);
        this.currentVersion = currentVersion;
        this.architecture = architecture ?? AppUpdateBuild.Architecture;
        this.installDirectory = installDirectory;
        this.launchHelper = launchHelper;
        BuiltInManifestUrl = AppUpdateManifest.Allowed(builtInManifestUrl) ? builtInManifestUrl : null;
    }

    public bool HasPublicKey => service.VerifiesSignatures;

    /// The saved "automatic" switch, as the app reports it.
    public bool Automatic => automatic();

    /// The last automatic attempt this run, successful or not. Kept in memory so
    /// a failed check waits for the next cycle instead of retrying every tick.
    public DateTimeOffset? LastAttempt { get; private set; }

    /// Windows rule 3: when the build carries an address, a user-entered
    /// address is ignored rather than merged or preferred.
    public string? EffectiveManifestUrl(string? userUrl) =>
        BuiltInManifestUrl ?? (AppUpdateManifest.Allowed(userUrl?.Trim()) ? userUrl!.Trim() : null);

    /// The automatic check: a saved preference with the macOS default, every
    /// `AppUpdateAuto.CheckInterval` after the later of the last check and the
    /// last attempt, and never when the build cannot verify a signature.
    public static bool IsDue(bool enabled, bool hasPublicKey, DateTimeOffset? lastChecked, DateTimeOffset now, DateTimeOffset? lastAttempt = null) =>
        AppUpdateAuto.CheckDue(enabled, hasPublicKey, lastChecked, lastAttempt, now);

    /// Checks when `IsDue` says so and remembers the attempt. A newer version
    /// is downloaded straight away while the switch is on.
    public async Task<bool> CheckAutomaticallyIfDueAsync(
        string? userUrl, bool enabled, DateTimeOffset? lastChecked, DateTimeOffset now)
    {
        if (closing || Busy || checking) return false;
        if (!IsDue(enabled, HasPublicKey, lastChecked, now, LastAttempt)) return false;
        LastAttempt = now;
        await CheckAsync(userUrl);
        return true;
    }

    public async Task CheckAsync(string? userUrl)
    {
        if (closing || Busy || checking) return;
        if (!HasPublicKey) { FailCheck(AppUpdateStrings.NoPublicKeyNotice); return; }
        if (EffectiveManifestUrl(userUrl) is not { } url)
        {
            FailCheck(Locale.Get("settings.appUpdate.errorNoManifestURL"));
            return;
        }
        // A staged update stays ready (and installable) while the check runs.
        if (State.Phase != AppUpdatePhase.Ready) Mutate(s => s with { Phase = AppUpdatePhase.Checking, ErrorMessage = null });
        checking = true;
        var token = new CancellationTokenSource();
        work = token;
        var download = false;
        try
        {
            var availability = await service.CheckAsync(url, currentVersion, token.Token);
            // "Restart now" was pressed meanwhile: the install owns the state.
            if (State.Phase is not (AppUpdatePhase.Ready or AppUpdatePhase.Checking)) return;
            var staged = State.Phase == AppUpdatePhase.Ready ? State.Availability?.Manifest.Version : null;
            var sameAsStaged = staged is not null && AppVersion.Normalized(staged) is not null
                && AppVersion.Normalized(availability.Manifest.Version) is not null
                && AppVersion.Compare(staged, availability.Manifest.Version) == 0;
            var newerButNotForThisMachine = availability.IsNewer && !availability.Manifest.Windows.ContainsKey(architecture);
            if (staged is not null && (sameAsStaged || newerButNotForThisMachine))
            {
                // The version the manifest names is the one staged, or a newer one this machine cannot
                // install: the staged update stays ready and nothing is downloaded again (M/AppUpdateAutoSteps.swift afterCheck).
                Mutate(s => s with { CheckedAt = DateTimeOffset.UtcNow, LastError = null });
                return;
            }
            if (newerButNotForThisMachine)
            {
                // Nothing staged and the newer version cannot run here: the Mac's "blocked" outcome — the
                // reason is the status, nothing is downloaded, and it is no failure to retry.
                var reason = Locale.Get("appUpdate.error.noPackageForArchitecture", new Dictionary<string, string> { ["architecture"] = architecture });
                Mutate(s => s with
                {
                    Phase = AppUpdatePhase.Failed,
                    Availability = availability,
                    PackagePath = null,
                    StagedDirectory = null,
                    InstallPlan = null,
                    CheckedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = reason,
                    LastError = null,
                });
                return;
            }
            Mutate(s => s with
            {
                Phase = availability.IsNewer ? AppUpdatePhase.Available : AppUpdatePhase.UpToDate,
                Availability = availability,
                PackagePath = null,
                StagedDirectory = null,
                InstallPlan = null,
                CheckedAt = DateTimeOffset.UtcNow,
                ErrorMessage = null,
                LastError = null,
            });
            download = availability.IsNewer && Automatic;
        }
        catch (OperationCanceledException)
        {
            if (State.Phase == AppUpdatePhase.Checking) Mutate(s => s with { Phase = AppUpdatePhase.Idle });
        }
        catch (Exception error)
        {
            if (State.Phase is AppUpdatePhase.Ready or AppUpdatePhase.Checking) FailCheck(error.Message);
        }
        finally
        {
            checking = false;
            if (ReferenceEquals(work, token)) work = null;
            token.Dispose();
        }
        if (download) await DownloadAsync();
    }

    /// Downloads the package for this machine's architecture, verifies it and
    /// unpacks it. A manifest with no asset for this architecture is refused.
    public async Task DownloadAsync()
    {
        if (closing || checking || State.Phase != AppUpdatePhase.Available) return;
        if (State.Availability?.Manifest is not { } manifest) return;
        if (!manifest.Windows.TryGetValue(architecture, out var asset))
        {
            Fail(Locale.Get("appUpdate.error.noPackageForArchitecture", new Dictionary<string, string> { ["architecture"] = architecture }));
            return;
        }
        Mutate(s => s with { Phase = AppUpdatePhase.Downloading, DownloadFraction = 0, ErrorMessage = null });
        work = new();
        try
        {
            var package = await service.DownloadAsync(
                asset, manifest.Version, fraction => Mutate(s => s with { DownloadFraction = fraction }), work.Token);
            Mutate(s => s with { Phase = AppUpdatePhase.Staging, PackagePath = package });

            var staged = AppUpdateService.Stage(package, architecture);
            var plan = installDirectory is null
                ? null
                : AppUpdateInstallPlan.Create(
                    staged, installDirectory, package, asset.Sha256, asset.Size, Environment.ProcessId);
            Mutate(s => s with { Phase = AppUpdatePhase.Ready, StagedDirectory = staged, InstallPlan = plan, LastError = null });
        }
        catch (OperationCanceledException)
        {
            Mutate(s => s with { Phase = AppUpdatePhase.Available, DownloadFraction = 0, PackagePath = null });
        }
        catch (Exception error) { Fail(error.Message); }
        finally { work?.Dispose(); work = null; }
    }

    public void CancelDownload()
    {
        service.CancelDownload();
        work?.Cancel();
    }

    /// Hands the swap to the detached helper and reports the phase. The app is
    /// quit by the caller; the helper waits for it before touching the install.
    /// `relaunch` false is the install on quit: the helper does not start the
    /// app again (`--relaunch 0`).
    public async Task InstallAsync(bool relaunch = true)
    {
        if (closing || State.Phase != AppUpdatePhase.Ready) return;
        if (State.InstallPlan is not { } staged || launchHelper is null)
        {
            Fail(Locale.Get("appUpdate.error.noInstallPlan"));
            return;
        }
        var plan = staged with { Relaunch = relaunch };
        Mutate(s => s with { Phase = AppUpdatePhase.Installing, ErrorMessage = null });
        try { await launchHelper(plan); }
        catch (Exception error) { Fail(error.Message); }
    }

    /// Called as the app quits, before `ShutdownAsync`: a staged update is
    /// installed once the app has gone, without starting it again. Skipped with
    /// the switch off, while an install is under way, while Windows is signing
    /// out or shutting down, and in a build that cannot verify updates
    /// (`AppUpdateAuto.InstallsOnQuit`). True when the helper was started.
    public async Task<bool> InstallOnQuitAsync(bool poweringOff = false)
    {
        // A dev or portable run has no install plan: quitting it says and changes nothing.
        if (closing || State.InstallPlan is null || launchHelper is null) return false;
        if (!AppUpdateAuto.InstallsOnQuit(State.Phase == AppUpdatePhase.Ready, Automatic, HasPublicKey, poweringOff)) return false;
        await InstallAsync(relaunch: false);
        return State.Phase == AppUpdatePhase.Installing;
    }

    public Task ShutdownAsync()
    {
        closing = true;
        work?.Cancel();
        service.CancelDownload();
        return Task.CompletedTask;
    }

    /// Puts a fixture phase on the real control for the smoke run. It reaches
    /// no network and no disk — it only makes the section render that phase,
    /// and the smoke run puts the previous state back.
    public void ShowForSmoke(AppUpdateState state) => Mutate(_ => state);

    public AppUpdateSectionView Describe(Func<DateTimeOffset, string> formatTime) =>
        AppUpdatePresentation.Describe(State, HasPublicKey, BuiltInManifestUrl, formatTime, Automatic);

    /// The one button the section shows, routed by the phase it is in.
    public Task PressAsync(string? userUrl) => State.Phase switch
    {
        AppUpdatePhase.Available => DownloadAsync(),
        AppUpdatePhase.Downloading => Cancelled(),
        AppUpdatePhase.Ready => InstallAsync(),
        AppUpdatePhase.Checking or AppUpdatePhase.Staging or AppUpdatePhase.Installing => Task.CompletedTask,
        _ => CheckAsync(userUrl),
    };

    private Task Cancelled() { CancelDownload(); return Task.CompletedTask; }

    private bool Busy => State.Phase
        is AppUpdatePhase.Checking or AppUpdatePhase.Downloading or AppUpdatePhase.Staging or AppUpdatePhase.Installing;

    private void Fail(string message) =>
        Mutate(s => s with { Phase = AppUpdatePhase.Failed, ErrorMessage = message, LastError = message });

    /// A failed check never drops a staged update: beside Ready it is only `LastError`.
    private void FailCheck(string message)
    {
        if (State.Phase == AppUpdatePhase.Ready) Mutate(s => s with { LastError = message });
        else Fail(message);
    }

    private void Mutate(Func<AppUpdateState, AppUpdateState> apply)
    {
        State = apply(State);
        StateChanged?.Invoke();
    }
}

/// What the GUI smoke run records under the key "appUpdateSection".
/// Nothing here is a token, a path or a prompt.
public sealed record AppUpdateSmokeOutcome
{
    public const string ResultKey = "appUpdateSection";

    [JsonPropertyName("statuses")] public IReadOnlyList<string> Statuses { get; init; } = [];
    [JsonPropertyName("buttons")] public IReadOnlyList<string> Buttons { get; init; } = [];
    [JsonPropertyName("refusedWithoutPublicKey")] public bool RefusedWithoutPublicKey { get; init; }
    [JsonPropertyName("toggledTo")] public bool ToggledTo { get; init; }
    [JsonPropertyName("restored")] public bool Restored { get; init; }
}

public static class AppUpdateSmoke
{
    private static AppUpdateAvailability FixtureAvailability { get; } = new("0.1.0", new AppUpdateManifest(
        "9.9.9", 1, null, null,
        new Dictionary<string, AppUpdateAsset>
        {
            ["x64"] = new("https://example.invalid/MightyClaude-windows-x64.zip", new string('a', 64), 1024),
            ["arm64"] = new("https://example.invalid/MightyClaude-windows-arm64.zip", new string('b', 64), 1024),
        }));

    /// The phases the smoke run walks the real control through, with fixture
    /// data only: no manifest is fetched and no package is downloaded.
    public static IReadOnlyList<AppUpdateState> FixtureStates { get; } =
    [
        new() { Phase = AppUpdatePhase.Checking },
        new() { Phase = AppUpdatePhase.Available, Availability = FixtureAvailability },
        new() { Phase = AppUpdatePhase.Downloading, DownloadFraction = 0.42, Availability = FixtureAvailability },
        new() { Phase = AppUpdatePhase.Staging, Availability = FixtureAvailability },
        new() { Phase = AppUpdatePhase.Ready, Availability = FixtureAvailability },
        new() { Phase = AppUpdatePhase.Installing, Availability = FixtureAvailability },
        new() { Phase = AppUpdatePhase.UpToDate },
        new() { Phase = AppUpdatePhase.Failed, ErrorMessage = Locale.Get("appUpdate.error.checkFailed") },
        // A failed background check beside a staged update: it stays ready.
        new() { Phase = AppUpdatePhase.Ready, Availability = FixtureAvailability, LastError = Locale.Get("appUpdate.error.checkFailed") },
    ];

    /// The status sentences and button labels the section must show for the
    /// fixture phases, in order.
    public static IReadOnlyList<string> ExpectedStatuses { get; } =
        [.. FixtureStates.Select(state => AppUpdatePresentation.Describe(state, true, null, Format).StatusText)];

    public static IReadOnlyList<string> ExpectedButtons { get; } =
        [.. FixtureStates.Select(state => AppUpdatePresentation.Describe(state, true, null, Format).ButtonLabel)];

    private static string Format(DateTimeOffset value) => value.ToLocalTime().ToString("HH:mm");

    /// Drives the smoke decision: the section rendered every fixture phase, a
    /// key-less build refused to check, and the saved switch was put back.
    public static async Task<AppUpdateSmokeOutcome> RunAsync(
        IReadOnlyList<string> renderedStatuses,
        IReadOnlyList<string> renderedButtons,
        bool refusedWithoutPublicKey,
        Func<bool> readAutoCheck,
        Func<bool, Task> writeAutoCheck)
    {
        if (!renderedStatuses.SequenceEqual(ExpectedStatuses))
            throw new InvalidOperationException(
                "the app update section must show every fixture status: expected " +
                string.Join(" | ", ExpectedStatuses) + " but showed " + string.Join(" | ", renderedStatuses));
        if (!renderedButtons.SequenceEqual(ExpectedButtons))
            throw new InvalidOperationException(
                "the app update section must show every fixture button: expected " +
                string.Join(" | ", ExpectedButtons) + " but showed " + string.Join(" | ", renderedButtons));
        if (!refusedWithoutPublicKey)
            throw new InvalidOperationException("a build without a public key must refuse to check");

        var original = readAutoCheck();
        var flipped = !original;
        try
        {
            await writeAutoCheck(flipped);
            if (readAutoCheck() != flipped)
                throw new InvalidOperationException("the automatic check switch did not persist to saved state");
        }
        finally { await writeAutoCheck(original); }

        if (readAutoCheck() != original)
            throw new InvalidOperationException("the automatic check switch was not restored");

        return new()
        {
            Statuses = [.. renderedStatuses],
            Buttons = [.. renderedButtons],
            RefusedWithoutPublicKey = true,
            ToggledTo = flipped,
            Restored = true,
        };
    }
}
