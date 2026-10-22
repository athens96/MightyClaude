using System.Diagnostics;

namespace MightyClaude.Core;

/// One move the replacement performs, in the order it performs them.
/// `Kind` is one of backup, install, rollback.
public sealed record AppUpdateMove(string Kind, string From, string To);

/// Everything the detached helper needs to replace the install after the app
/// has quit. Built in Core so the argument list, the plan of moves and the
/// rollback decision are plain facts a Mac-side check can read.
///
/// `Relaunch` false is the install on quit: the helper replaces the install and
/// does not start the app again (M/AppUpdate.swift `installScript(relaunch:)`).
public sealed record AppUpdateInstallPlan(
    string StagedDirectory,
    string InstallDirectory,
    string BackupDirectory,
    string PackagePath,
    string Sha256,
    long Size,
    int AppProcessId,
    string NewExecutable,
    string PreviousExecutable,
    bool Relaunch = true)
{
    public const string HelperFlag = "--update-helper";
    public const string RelaunchFlag = "--relaunch";

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
        RelaunchFlag, Relaunch ? "1" : "0",
    ];

    /// The minimal environment the helper is started with: no inherited
    /// variables, no token, no credential, nothing that identifies the user.
    public static IReadOnlyDictionary<string, string> HelperEnvironment { get; } =
        new Dictionary<string, string> { ["SystemRoot"] = "C:\\Windows" };

    /// Rebuilds the plan from the argument list the helper was started with.
    /// Returns null when an argument is missing or malformed — the helper then
    /// does nothing and the existing install stays untouched. `--relaunch` may
    /// be missing: the helper is the new version, started by the old one, and an
    /// app from before the install on quit always meant to start it again.
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
        var relaunch = true;
        if (values.TryGetValue(RelaunchFlag, out var relaunchText))
        {
            if (relaunchText is not ("1" or "0")) return null;
            relaunch = relaunchText == "1";
        }

        var plan = new AppUpdateInstallPlan(
            staged, install, backup, package, sha256.ToLowerInvariant(), size, pid,
            Path.Combine(install, AppUpdateService.ExecutableName),
            Path.Combine(backup, AppUpdateService.ExecutableName), relaunch);
        return plan.IsValid ? plan : null;
    }

    /// The executable inside the staged folder that the running app starts as
    /// the detached helper. The helper runs from here — not from the install
    /// folder — so the install folder can be renamed aside freely.
    public string StagedHelperExecutable => Path.Combine(StagedDirectory, AppUpdateService.ExecutableName);

    /// Where the helper copies the staged folder before the two renames: beside
    /// the install, so a copy cut off half-way never touches it
    /// (M/AppUpdate.swift `$DESTINATION.update-new`).
    public string NewDirectory =>
        Path.GetFullPath(InstallDirectory.TrimEnd(Path.DirectorySeparatorChar)) + ".update-new";

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
/// `Skipped`: an install on quit found the app running again and left the
/// install and the staged update as they were, for the next quit.
public sealed record AppUpdateReplacementResult(
    bool Replaced,
    bool RolledBack,
    string? Error,
    IReadOnlyList<AppUpdateMove> Moves,
    bool Started,
    bool BackupRemoved,
    bool Skipped = false);

/// The replacement itself: put back an install an interrupted swap left aside,
/// wait for the app to quit, verify the package again, copy the new folder
/// beside the install, rename the install aside and the new folder in, start
/// the new app (unless the plan says not to relaunch), and put the old one back
/// on any failure.
///
/// The whole sequence is here rather than in a shell script, so the same code
/// that runs on Windows is exercised on macOS with temporary folders by
/// "app update replacement …" in Core.Tests. Nothing here elevates.
public static class AppUpdateReplacement
{
    public static TimeSpan DefaultExitTimeout => TimeSpan.FromMinutes(5);

    /// How often each folder rename is tried, and how long apart, before the
    /// failure counts: an antivirus scan, the search indexer or Explorer may
    /// hold a file in the folder for a moment.
    public const int MoveAttempts = 10;
    public static TimeSpan MoveRetryDelay => TimeSpan.FromMilliseconds(300);

    /// A `.update-new` copy written to this recently may still be filling: the
    /// pruning at app start leaves it to the helper that is writing it.
    public static TimeSpan NewCopyGrace => TimeSpan.FromMinutes(10);

    /// The moves a successful replacement performs, in order.
    public static IReadOnlyList<AppUpdateMove> Plan(AppUpdateInstallPlan plan) =>
    [
        new("backup", plan.InstallDirectory, plan.BackupDirectory),
        new("install", plan.NewDirectory, plan.InstallDirectory),
    ];

    /// The move that undoes a failed replacement.
    public static AppUpdateMove Rollback(AppUpdateInstallPlan plan) =>
        new("rollback", plan.BackupDirectory, plan.InstallDirectory);

    /// True when the install was moved aside and the new folder is not in place,
    /// which is the only state that needs the backup put back.
    public static bool ShouldRollback(bool backupTaken, bool installed) => backupTaken && !installed;

    /// Copies a directory tree recursively. The helper uses this to put the
    /// staged folder into the install path: it cannot move the staged folder
    /// because it is running from it.
    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    /// True when the installed tree contains the expected executable.
    public static bool VerifyInstallTree(string installDirectory) =>
        File.Exists(Path.Combine(installDirectory, AppUpdateService.ExecutableName));

    /// Removes install-folder backups a crashed or killed helper could not
    /// delete, and a copy it left beside the install. The new app clears them
    /// on its next start, after `RestoreInterruptedInstall`. A copy written to
    /// within `NewCopyGrace` of `now` is kept: an install on quit's helper may
    /// still be filling it while the app was opened again. Returns how many
    /// backups were removed.
    public static int PruneBackups(string installDirectory, DateTime? now = null)
    {
        var install = Path.GetFullPath(installDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var parent = Path.GetDirectoryName(install);
        if (parent is null || !Directory.Exists(parent)) return 0;
        var prefix = Path.GetFileName(install) + ".backup-";
        var copy = install + ".update-new";
        if (!WrittenSince(copy, (now ?? DateTime.UtcNow) - NewCopyGrace)) RemoveFolder(copy);
        var removed = 0;
        foreach (var folder in Directory.GetDirectories(parent))
        {
            if (!Path.GetFileName(folder).StartsWith(prefix, StringComparison.Ordinal)) continue;
            try { Directory.Delete(folder, true); removed++; }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    /// True when the folder, or anything in it, was created or written after
    /// `since` (UTC). Copied files keep their source's write time, so the
    /// creation times count too. A folder that cannot be read counts as written,
    /// so nothing is removed on a guess.
    private static bool WrittenSince(string folder, DateTime since)
    {
        try
        {
            if (!Directory.Exists(folder)) return false;
            static DateTime Latest(FileSystemInfo entry) =>
                entry.CreationTimeUtc > entry.LastWriteTimeUtc ? entry.CreationTimeUtc : entry.LastWriteTimeUtc;
            var root = new DirectoryInfo(folder);
            return Latest(root) > since || root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Any(entry => Latest(entry) > since);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    /// Verifies the package SHA-256 again immediately before the swap.
    /// Returns null when it matches, the refusal sentence otherwise.
    public static async Task<string?> VerifyBeforeSwapAsync(
        string packagePath, string expectedSha256, CancellationToken cancellation = default)
    {
        if (!File.Exists(packagePath)) return Locale.Get("appUpdate.error.packageMissingBeforeSwap");
        var digest = await AppUpdateService.Sha256Async(packagePath, cancellation);
        return string.Equals(digest, expectedSha256, StringComparison.OrdinalIgnoreCase)
            ? null
            : Locale.Get("appUpdate.error.packageRecheckFailed");
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

    /// Waits up to `timeout` while a process other than this one runs an image
    /// from the install folder (the app opened again, or one of its headless
    /// children still ending). True when one is still there: the install on
    /// quit then leaves the update for the next quit. Windows cannot rename a
    /// folder an executable runs from, and the command lines that would tell
    /// the headless children apart are not readable without WMI, so they are
    /// waited out rather than told apart.
    public static async Task<bool> RunningFromInstallAsync(string installDirectory, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            if (!AnyRunningFrom(installDirectory)) return false;
            if (DateTimeOffset.UtcNow >= deadline) return true;
            await Task.Delay(500, cancellation);
        }
    }

    /// True while the app must not stage an update: a process other than this
    /// one runs an image from under `updatesDirectory` (an install on quit's
    /// helper runs from `updates/<version>/staged`, and keeps polling up to 30
    /// seconds after the app opened again), or the copy beside the install was
    /// written within `NewCopyGrace` (a helper may still be filling it). Staging
    /// deletes and rewrites the staged folder, which would break either. A check
    /// that cannot tell reads as busy.
    public static bool StagingBusy(string updatesDirectory, string? installDirectory, DateTime? now = null)
    {
        try
        {
            if (Directory.Exists(updatesDirectory) && AnyRunningFrom(updatesDirectory)) return true;
            return installDirectory is not null
                && WrittenSince(Path.GetFullPath(installDirectory.TrimEnd(Path.DirectorySeparatorChar)) + ".update-new", (now ?? DateTime.UtcNow) - NewCopyGrace);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return true; }
    }

    /// Why the copy at `actual` is not the staged tree at `expected` (the same
    /// relative files with the same sizes), or null when it is. The helper
    /// checks this right before the renames: a copy cut short, or a staged
    /// folder changed under it, is never swapped in.
    public static string? TreeMismatch(string expected, string actual)
    {
        static Dictionary<string, long> Files(string root) =>
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(file => Path.GetRelativePath(root, file), file => new FileInfo(file).Length, StringComparer.Ordinal);
        var want = Files(expected);
        var have = Files(actual);
        if (want.Keys.FirstOrDefault(file => !have.ContainsKey(file)) is { } missing) return "missing " + missing;
        if (have.Keys.FirstOrDefault(file => !want.ContainsKey(file)) is { } extra) return "unexpected " + extra;
        if (want.FirstOrDefault(file => have[file.Key] != file.Value) is { Key: { } differs }) return "size differs for " + differs;
        return null;
    }

    private static bool AnyRunningFrom(string installDirectory)
    {
        var folder = Path.GetFullPath(installDirectory.TrimEnd(Path.DirectorySeparatorChar)) + Path.DirectorySeparatorChar;
        var name = Path.GetFileNameWithoutExtension(AppUpdateService.ExecutableName);
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.HasExited) continue;
                    if (process.MainModule?.FileName is { } image && image.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        return false;
    }

    /// Puts back an install an interrupted swap left aside: when the install
    /// folder is missing, or holds no executable, and a backup with one exists
    /// beside it, the newest such backup is renamed back (a folder without the
    /// executable is removed first). True when it put one back; never throws.
    ///
    /// Best effort. It can only help when something still runs: with the
    /// install folder gone or half, the app cannot start from it, so in practice
    /// it is the next helper (which runs from the staged folder) that puts the
    /// install back, and the call at app start finds a whole install and does
    /// nothing. It is still called there, before old backups are pruned, so a
    /// pruning never removes the only whole copy.
    public static bool RestoreInterruptedInstall(string installDirectory)
    {
        try
        {
            var install = Path.GetFullPath(installDirectory.TrimEnd(Path.DirectorySeparatorChar));
            if (Directory.Exists(install) && VerifyInstallTree(install)) return false;
            var parent = Path.GetDirectoryName(install);
            if (parent is null || !Directory.Exists(parent)) return false;
            var prefix = Path.GetFileName(install) + ".backup-";
            var backup = Directory.GetDirectories(parent)
                .Where(folder => Path.GetFileName(folder).StartsWith(prefix, StringComparison.Ordinal) && VerifyInstallTree(folder))
                .OrderByDescending(folder => Path.GetFileName(folder), StringComparer.Ordinal)
                .FirstOrDefault();
            if (backup is null) return false;
            if (Directory.Exists(install)) Directory.Delete(install, true);
            Directory.Move(backup, install);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /// The whole sequence. `waitForExit`, `startApp`, `copier`, `mover` and
    /// `runningAgain` are injected so the Mac-side check can run it end to end
    /// against temporary folders without a real process; on Windows the helper
    /// passes the real implementations (null means CopyDirectory, Directory.Move
    /// and no process check). Each rename through `mover`, the rollback's too,
    /// is tried `MoveAttempts` times `moveRetryDelay` apart (`MoveRetryDelay`
    /// when null) while it fails with an IOException or an
    /// UnauthorizedAccessException. A `runningAgain` that throws counts as the
    /// app running again: the swap is skipped, the staged update kept, and the
    /// result's `Error` says why.
    ///
    /// The order mirrors M/AppUpdate.swift `installScript`: an install an
    /// earlier swap left aside is put back first; after the app has quit and the
    /// package is verified again, the staged folder is copied to
    /// `NewDirectory` beside the install while the install is still whole; then
    /// two renames (install → backup, new → install), undone on failure. With
    /// `plan.Relaunch` false, the swap is skipped (the staged update kept, the
    /// result `Skipped`) when the app runs from the install folder again, and
    /// `startApp` is never called.
    public static async Task<AppUpdateReplacementResult> RunAsync(
        AppUpdateInstallPlan plan,
        Func<CancellationToken, Task<bool>> waitForExit,
        Func<string, Task> startApp,
        Action<string, string>? copier = null,
        CancellationToken cancellation = default,
        Action<string, string>? mover = null,
        Func<CancellationToken, Task<bool>>? runningAgain = null,
        TimeSpan? moveRetryDelay = null)
    {
        var doCopy = copier ?? CopyDirectory;
        var move = mover ?? Directory.Move;
        var delay = moveRetryDelay ?? MoveRetryDelay;
        Task DoMove(string from, string to, CancellationToken token) => MoveWithRetryAsync(move, from, to, delay, token);
        var moves = new List<AppUpdateMove>();
        if (!plan.IsValid)
            return new(false, false, Locale.Get("appUpdate.error.planInvalid"), moves, false, false);

        RestoreInterruptedInstall(plan.InstallDirectory);

        if (!await waitForExit(cancellation))
            return new(false, false, Locale.Get("appUpdate.error.appStillRunning"), moves, false, false);

        // The package is verified again right here, not when it was downloaded.
        if (await VerifyBeforeSwapAsync(plan.PackagePath, plan.Sha256, cancellation) is { } refusal)
            return new(false, false, refusal, moves, false, false);

        if (!Directory.Exists(plan.StagedDirectory))
            return new(false, false, Locale.Get("appUpdate.error.stagedFolderMissing"), moves, false, false);

        // Copy (not move): the helper runs from the staged folder and cannot
        // move the directory it is running from. The copy lands beside the
        // install, so a copy cut off half-way never touches the install.
        try
        {
            RemoveFolder(plan.NewDirectory);
            doCopy(plan.StagedDirectory, plan.NewDirectory);
            if (!VerifyInstallTree(plan.NewDirectory))
                throw new InvalidOperationException(Locale.Get("appUpdate.error.installedExecutableMissing"));
        }
        catch (Exception error)
        {
            RemoveFolder(plan.NewDirectory);
            var startedOld = plan.Relaunch && await TryStartAsync(startApp, plan.NewExecutable);
            return new(false, false, error.Message, moves, startedOld, false);
        }

        if (!plan.Relaunch && runningAgain is not null)
        {
            // The app was opened again: the update waits for the next quit. A
            // check that cannot tell is read the same way, and its reason logged.
            string? unsure = null;
            bool again;
            try { again = await runningAgain(cancellation); }
            catch (Exception error) when (error is not OutOfMemoryException) { again = true; unsure = error.Message; }
            if (again)
            {
                RemoveFolder(plan.NewDirectory);
                return new(false, false, unsure, moves, false, false, Skipped: true);
            }
        }

        // Right before the renames: the copy must be the staged tree, file for file and size for size. A copy cut short or a
        // staged folder changed under it (the app staging again) is never swapped in; the staged update is kept. The reason
        // goes to install.log only, so it is plain text.
        string? mismatch;
        try { mismatch = TreeMismatch(plan.StagedDirectory, plan.NewDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { mismatch = error.Message; }
        if (mismatch is not null)
        {
            RemoveFolder(plan.NewDirectory);
            var startedOld = plan.Relaunch && await TryStartAsync(startApp, plan.NewExecutable);
            return new(false, false, "the copy beside the install does not match the staged update: " + mismatch, moves, startedOld, false);
        }

        var backupTaken = false;
        var installed = false;
        try
        {
            if (Directory.Exists(plan.InstallDirectory))
            {
                await DoMove(plan.InstallDirectory, plan.BackupDirectory, cancellation);
                moves.Add(Plan(plan)[0]);
                backupTaken = true;
            }
            await DoMove(plan.NewDirectory, plan.InstallDirectory, cancellation);
            moves.Add(Plan(plan)[1]);
            installed = true;

            if (plan.Relaunch) await startApp(plan.NewExecutable);
        }
        catch (Exception error)
        {
            RemoveFolder(plan.NewDirectory);
            // The rollback is never cancelled: the machine must end with a whole install.
            return await UndoAsync(plan, moves, backupTaken, installed, startApp, error.Message,
                (from, to) => DoMove(from, to, CancellationToken.None));
        }

        // The backup is only removed once the new app has started, or, without
        // a relaunch, once the new folder is in place.
        var removed = false;
        try { if (Directory.Exists(plan.BackupDirectory)) { Directory.Delete(plan.BackupDirectory, true); removed = true; } }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        return new(true, false, null, moves, plan.Relaunch, removed);
    }

    private static void RemoveFolder(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// One rename through `mover`, tried up to `MoveAttempts` times `delay`
    /// apart while it fails with an IOException or an
    /// UnauthorizedAccessException; the last failure is thrown.
    private static async Task MoveWithRetryAsync(
        Action<string, string> mover, string from, string to, TimeSpan delay, CancellationToken cancellation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { mover(from, to); return; }
            catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && attempt < MoveAttempts)
            {
                await Task.Delay(delay, cancellation);
            }
        }
    }

    private static async Task<bool> TryStartAsync(Func<string, Task> startApp, string executable)
    {
        try { await startApp(executable); return true; }
        catch (Exception) { return false; }
    }

    private static async Task<AppUpdateReplacementResult> UndoAsync(
        AppUpdateInstallPlan plan, List<AppUpdateMove> moves, bool backupTaken, bool installed,
        Func<string, Task> startApp, string reason, Func<string, string, Task> move)
    {
        // Whatever failed, the machine ends with the version the user had.
        if (installed && backupTaken) RemoveFolder(plan.InstallDirectory);

        var restored = false;
        if (backupTaken && Directory.Exists(plan.BackupDirectory))
        {
            try
            {
                if (Directory.Exists(plan.InstallDirectory)) Directory.Delete(plan.InstallDirectory, true);
                await move(plan.BackupDirectory, plan.InstallDirectory);
                moves.Add(Rollback(plan));
                restored = true;
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        var started = (restored || !backupTaken && Directory.Exists(plan.InstallDirectory)) && plan.Relaunch
            && await TryStartAsync(startApp, plan.NewExecutable);
        return new(false, restored, Locale.Get("appUpdate.error.rolledBack", new Dictionary<string, string> { ["reason"] = reason }), moves, started, false);
    }
}
