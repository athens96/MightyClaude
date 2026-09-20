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
    string? Notes);

public static class AppUpdatePresentation
{
    /// Windows rule 1: with no public key the section shows one sentence and
    /// the button is disabled — there is no check, not even in a dev build.
    /// Windows rule 3: a build that carries an address ignores a typed one, so
    /// the address field is shown disabled with the built-in address beside it.
    public static AppUpdateSectionView Describe(
        AppUpdateState state, bool hasPublicKey, string? builtInAddress, Func<DateTimeOffset, string> formatTime)
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
            AppUpdatePhase.Ready => AppUpdateStrings.ReadyTemplate.Replace("{version}", version),
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
            AppUpdatePhase.Ready => (AppUpdateStrings.InstallButton, true),
            AppUpdatePhase.Checking or AppUpdatePhase.Staging or AppUpdatePhase.Installing =>
                (AppUpdateStrings.InProgressButton, false),
            _ => (AppUpdateStrings.CheckButton, true),
        };
        var notes = state.Phase is AppUpdatePhase.Available or AppUpdatePhase.Ready
            ? state.Availability?.Manifest.Notes
            : null;
        return new(
            true,
            AppUpdateStrings.SignatureVerified,
            builtInAddress is null ? null : AppUpdateStrings.BuiltInAddressTemplate.Replace("{address}", builtInAddress),
            builtInAddress is null,
            status, label, enabled,
            string.IsNullOrEmpty(notes) ? null : notes);
    }
}

/// Drives the update through its phases. Every decision lives here; WinUI
/// renders <see cref="AppUpdateSectionView"/> and forwards the one button.
public sealed class AppUpdateCoordinator
{
    public const double CheckIntervalHours = 24;

    public event Action? StateChanged;

    private readonly AppUpdateService service;
    private readonly string currentVersion;
    private readonly string architecture;
    private readonly string? installDirectory;
    private readonly Func<AppUpdateInstallPlan, Task>? launchHelper;
    private CancellationTokenSource? work;
    private bool closing;

    public AppUpdateState State { get; private set; } = new();
    public string? BuiltInManifestUrl { get; }

    public AppUpdateCoordinator(
        AppUpdateService service,
        string currentVersion,
        string? builtInManifestUrl = null,
        string? architecture = null,
        string? installDirectory = null,
        Func<AppUpdateInstallPlan, Task>? launchHelper = null)
    {
        this.service = service;
        this.currentVersion = currentVersion;
        this.architecture = architecture ?? AppUpdateBuild.Architecture;
        this.installDirectory = installDirectory;
        this.launchHelper = launchHelper;
        BuiltInManifestUrl = AppUpdateManifest.Allowed(builtInManifestUrl) ? builtInManifestUrl : null;
    }

    public bool HasPublicKey => service.VerifiesSignatures;

    /// Windows rule 3: when the build carries an address, a user-entered
    /// address is ignored rather than merged or preferred.
    public string? EffectiveManifestUrl(string? userUrl) =>
        BuiltInManifestUrl ?? (AppUpdateManifest.Allowed(userUrl?.Trim()) ? userUrl!.Trim() : null);

    /// The automatic check: a saved preference with the macOS default, at most
    /// once a day, and never when the build cannot verify a signature.
    public static bool IsDue(bool enabled, bool hasPublicKey, DateTimeOffset? lastChecked, DateTimeOffset now) =>
        enabled && hasPublicKey &&
        (lastChecked is not { } last || (now - last).TotalHours >= CheckIntervalHours);

    public async Task<bool> CheckAutomaticallyIfDueAsync(
        string? userUrl, bool enabled, DateTimeOffset? lastChecked, DateTimeOffset now)
    {
        if (!IsDue(enabled, HasPublicKey, lastChecked, now)) return false;
        await CheckAsync(userUrl);
        return true;
    }

    public async Task CheckAsync(string? userUrl)
    {
        if (closing || Busy) return;
        if (!HasPublicKey) { Fail(AppUpdateStrings.NoPublicKeyNotice); return; }
        if (EffectiveManifestUrl(userUrl) is not { } url)
        {
            Fail("업데이트 정보 주소가 설정되지 않았습니다. 설정에서 https 주소를 입력하세요.");
            return;
        }
        Mutate(s => s with { Phase = AppUpdatePhase.Checking, ErrorMessage = null });
        work = new();
        try
        {
            var availability = await service.CheckAsync(url, currentVersion, work.Token);
            Mutate(s => s with
            {
                Phase = availability.IsNewer ? AppUpdatePhase.Available : AppUpdatePhase.UpToDate,
                Availability = availability,
                CheckedAt = DateTimeOffset.UtcNow,
                ErrorMessage = null,
            });
        }
        catch (OperationCanceledException) { Mutate(s => s with { Phase = AppUpdatePhase.Idle }); }
        catch (Exception error) { Fail(error.Message); }
        finally { work?.Dispose(); work = null; }
    }

    /// Downloads the package for this machine's architecture, verifies it and
    /// unpacks it. A manifest with no asset for this architecture is refused.
    public async Task DownloadAsync()
    {
        if (closing || State.Phase != AppUpdatePhase.Available) return;
        if (State.Availability?.Manifest is not { } manifest) return;
        if (!manifest.Windows.TryGetValue(architecture, out var asset))
        {
            Fail($"이 업데이트에는 {architecture} 패키지가 없습니다.");
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
            Mutate(s => s with { Phase = AppUpdatePhase.Ready, StagedDirectory = staged, InstallPlan = plan });
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
    public async Task InstallAsync()
    {
        if (closing || State.Phase != AppUpdatePhase.Ready) return;
        if (State.InstallPlan is not { } plan || launchHelper is null)
        {
            Fail("설치 계획이 없어 교체를 시작할 수 없습니다.");
            return;
        }
        Mutate(s => s with { Phase = AppUpdatePhase.Installing, ErrorMessage = null });
        try { await launchHelper(plan); }
        catch (Exception error) { Fail(error.Message); }
    }

    public Task ShutdownAsync()
    {
        closing = true;
        work?.Cancel();
        service.CancelDownload();
        return Task.CompletedTask;
    }

    public AppUpdateSectionView Describe(Func<DateTimeOffset, string> formatTime) =>
        AppUpdatePresentation.Describe(State, HasPublicKey, BuiltInManifestUrl, formatTime);

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
        Mutate(s => s with { Phase = AppUpdatePhase.Failed, ErrorMessage = message });

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
