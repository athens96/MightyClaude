using System.Diagnostics;

namespace MightyClaude.Core;

/// One move the replacement performs, in the order it performs them.
/// `Kind` is one of backup, install, rollback.
public sealed record AppUpdateMove(string Kind, string From, string To);

/// Everything the detached helper needs to replace the install after the app
/// has quit. Built in Core so the argument list, the plan of moves and the
/// rollback decision are plain facts a Mac-side check can read.
public sealed record AppUpdateInstallPlan(
    string StagedDirectory,
    string InstallDirectory,
    string BackupDirectory,
    string PackagePath,
    string Sha256,
    long Size,
    int AppProcessId,
    string NewExecutable,
    string PreviousExecutable)
{
    public const string HelperFlag = "--update-helper";

    public static AppUpdateInstallPlan Create(
        string stagedDirectory, string installDirectory, string packagePath,
        string sha256, long size, int appProcessId, string? backupDirectory = null)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff");
        var backup = backupDirectory ?? Path.Combine(
            Path.GetDirectoryName(installDirectory.TrimEnd(Path.DirectorySeparatorChar)) ?? Path.GetTempPath(),
            Path.GetFileName(installDirectory.TrimEnd(Path.DirectorySeparatorChar)) + ".backup-" + stamp);
        return new(
            stagedDirectory, installDirectory, backup, packagePath, sha256, size, appProcessId,
            Path.Combine(installDirectory, AppUpdateService.ExecutableName),
            Path.Combine(backup, AppUpdateService.ExecutableName));
    }

    /// The argument list the helper is started with. Every value the helper
    /// needs is here, so the helper reads no saved state and no environment.
    public IReadOnlyList<string> HelperArguments =>
    [
        HelperFlag,
        "--staged", StagedDirectory,
        "--install", InstallDirectory,
        "--backup", BackupDirectory,
        "--package", PackagePath,
        "--sha256", Sha256,
        "--size", Size.ToString(),
        "--wait-pid", AppProcessId.ToString(),
    ];

    /// The minimal environment the helper is started with: no inherited
    /// variables, no token, no credential, nothing that identifies the user.
    public static IReadOnlyDictionary<string, string> HelperEnvironment { get; } =
        new Dictionary<string, string> { ["SystemRoot"] = "C:\\Windows" };

    /// Rebuilds the plan from the argument list the helper was started with.
    /// Returns null when an argument is missing or malformed — the helper then
    /// does nothing and the existing install stays untouched.
    public static AppUpdateInstallPlan? TryParse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0 || arguments[0] != HelperFlag) return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index + 1 < arguments.Count; index += 2)
        {
            if (!arguments[index].StartsWith("--", StringComparison.Ordinal)) return null;
            values[arguments[index]] = arguments[index + 1];
        }
        if (!values.TryGetValue("--staged", out var staged)) return null;
        if (!values.TryGetValue("--install", out var install)) return null;
        if (!values.TryGetValue("--backup", out var backup)) return null;
        if (!values.TryGetValue("--package", out var package)) return null;
        if (!values.TryGetValue("--sha256", out var sha256)) return null;
        if (!values.TryGetValue("--size", out var sizeText) || !long.TryParse(sizeText, out var size) || size <= 0) return null;
        if (!values.TryGetValue("--wait-pid", out var pidText) || !int.TryParse(pidText, out var pid) || pid <= 0) return null;

        var plan = new AppUpdateInstallPlan(
            staged, install, backup, package, sha256.ToLowerInvariant(), size, pid,
            Path.Combine(install, AppUpdateService.ExecutableName),
            Path.Combine(backup, AppUpdateService.ExecutableName));
        return plan.IsValid ? plan : null;
    }

    public bool IsValid =>
        !string.IsNullOrEmpty(StagedDirectory) &&
        !string.IsNullOrEmpty(InstallDirectory) &&
        !string.IsNullOrEmpty(BackupDirectory) &&
        !string.IsNullOrEmpty(PackagePath) &&
        Sha256.Length == 64 && Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        Size > 0 && AppProcessId > 0 &&
        // The backup must not sit inside the folder that is about to be moved.
        !Path.GetFullPath(BackupDirectory).StartsWith(
            Path.GetFullPath(InstallDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.Ordinal);
}

/// What the replacement did. `Replaced` and `RolledBack` are never both true.
public sealed record AppUpdateReplacementResult(
    bool Replaced,
    bool RolledBack,
    string? Error,
    IReadOnlyList<AppUpdateMove> Moves,
    bool Started,
    bool BackupRemoved);

/// The replacement itself: wait for the app to quit, verify the package again,
/// move the install aside, move the new folder in, start the new app, and put
/// the old one back on any failure.
///
/// The whole sequence is here rather than in a shell script, so the same code
/// that runs on Windows is exercised on macOS with temporary folders by
/// "app update replacement …" in Core.Tests. Nothing here elevates.
public static class AppUpdateReplacement
{
    public static TimeSpan DefaultExitTimeout => TimeSpan.FromMinutes(5);

    /// The moves a successful replacement performs, in order.
    public static IReadOnlyList<AppUpdateMove> Plan(AppUpdateInstallPlan plan) =>
    [
        new("backup", plan.InstallDirectory, plan.BackupDirectory),
        new("install", plan.StagedDirectory, plan.InstallDirectory),
    ];

    /// The move that undoes a failed replacement.
    public static AppUpdateMove Rollback(AppUpdateInstallPlan plan) =>
        new("rollback", plan.BackupDirectory, plan.InstallDirectory);

    /// True when the install was moved aside and the new folder is not in place,
    /// which is the only state that needs the backup put back.
    public static bool ShouldRollback(bool backupTaken, bool installed) => backupTaken && !installed;

    /// Verifies the package SHA-256 again immediately before the swap.
    /// Returns null when it matches, the refusal sentence otherwise.
    public static async Task<string?> VerifyBeforeSwapAsync(
        string packagePath, string expectedSha256, CancellationToken cancellation = default)
    {
        if (!File.Exists(packagePath)) return "교체 직전 확인할 패키지 파일이 없어 설치를 중단합니다.";
        var digest = await AppUpdateService.Sha256Async(packagePath, cancellation);
        return string.Equals(digest, expectedSha256, StringComparison.OrdinalIgnoreCase)
            ? null
            : "교체 직전 패키지 SHA-256 재검증에 실패해 설치를 중단합니다.";
    }

    /// Waits for the app process to exit, bounded. Returns false on timeout.
    public static async Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!IsRunning(processId)) return true;
            await Task.Delay(200, cancellation);
        }
        return !IsRunning(processId);
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// The whole sequence. `waitForExit` and `startApp` are injected so the
    /// Mac-side check can run it end to end against temporary folders without
    /// a real process; on Windows the helper passes the real implementations.
    public static async Task<AppUpdateReplacementResult> RunAsync(
        AppUpdateInstallPlan plan,
        Func<CancellationToken, Task<bool>> waitForExit,
        Func<string, Task> startApp,
        CancellationToken cancellation = default)
    {
        var moves = new List<AppUpdateMove>();
        if (!plan.IsValid)
            return new(false, false, "설치 계획이 올바르지 않아 설치하지 않습니다.", moves, false, false);

        if (!await waitForExit(cancellation))
            return new(false, false, "앱이 종료되지 않아 업데이트를 건너뜁니다.", moves, false, false);

        // The package is verified again right here, not when it was downloaded.
        if (await VerifyBeforeSwapAsync(plan.PackagePath, plan.Sha256, cancellation) is { } refusal)
            return new(false, false, refusal, moves, false, false);

        if (!Directory.Exists(plan.StagedDirectory))
            return new(false, false, "설치할 새 앱 폴더가 없습니다.", moves, false, false);

        var backupTaken = false;
        var installed = false;
        try
        {
            if (Directory.Exists(plan.InstallDirectory))
            {
                Directory.Move(plan.InstallDirectory, plan.BackupDirectory);
                moves.Add(Plan(plan)[0]);
                backupTaken = true;
            }
            Directory.Move(plan.StagedDirectory, plan.InstallDirectory);
            moves.Add(Plan(plan)[1]);
            installed = true;

            await startApp(plan.NewExecutable);
        }
        catch (Exception error)
        {
            return await UndoAsync(plan, moves, backupTaken, installed, startApp, error.Message);
        }

        // The backup is only removed once the new app has started.
        var removed = false;
        try { if (Directory.Exists(plan.BackupDirectory)) { Directory.Delete(plan.BackupDirectory, true); removed = true; } }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        return new(true, false, null, moves, true, removed);
    }

    private static async Task<AppUpdateReplacementResult> UndoAsync(
        AppUpdateInstallPlan plan, List<AppUpdateMove> moves, bool backupTaken, bool installed,
        Func<string, Task> startApp, string reason)
    {
        // Whatever failed, the machine ends with the version the user had.
        try { if (installed && Directory.Exists(plan.InstallDirectory)) Directory.Delete(plan.InstallDirectory, true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }

        var restored = false;
        if (backupTaken && Directory.Exists(plan.BackupDirectory))
        {
            try
            {
                if (Directory.Exists(plan.InstallDirectory)) Directory.Delete(plan.InstallDirectory, true);
                Directory.Move(plan.BackupDirectory, plan.InstallDirectory);
                moves.Add(Rollback(plan));
                restored = true;
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        var started = false;
        if (restored)
        {
            try { await startApp(plan.NewExecutable); started = true; }
            catch (Exception) { started = false; }
        }
        return new(false, restored, "업데이트에 실패해 이전 버전으로 되돌렸습니다: " + reason, moves, started, false);
    }
}
