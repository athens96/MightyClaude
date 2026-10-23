using System.Diagnostics;
using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace MightyClaude.WinUI;

/// Explicit profiles never import the user's legacy profile. Smoke runs require
/// a new/empty directory so CI cannot change saved conversations or credentials.
/// <c>--perf-scenario</c> runs the timing scenario (MainWindow.PerfScenario.cs) in the smoke's place, so it needs <c>--smoke-test</c>.
public sealed record StartupOptions(string? ProfileDirectory = null, bool SmokeTest = false, bool SmokeExit = false, bool PerfScenario = false)
{
    public static StartupOptions Parse(string[] args)
    {
        string? profile = null;
        bool smoke = false, exit = false, perf = false;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--profile":
                    if (profile is not null || ++index >= args.Length || !Path.IsPathFullyQualified(args[index]))
                        throw new ArgumentException(Locale.Get("windows.startup.profileArgument"));
                    profile = Path.GetFullPath(args[index]);
                    break;
                case "--smoke-test": smoke = true; break;
                case "--smoke-exit": exit = true; break;
                case "--perf-scenario": perf = true; break;
                default: throw new ArgumentException(Locale.Get("windows.startup.unsupportedArgument", new Dictionary<string, string> { ["argument"] = args[index] }));
            }
        }
        if (exit && !smoke) throw new ArgumentException(Locale.Get("windows.startup.smokeExitNeedsSmoke"));
        // A developer's switch, never shown to a user: its refusal is not translated.
        if (perf && !smoke) throw new ArgumentException("--perf-scenario needs --smoke-test.");
        if (smoke && profile is null) throw new ArgumentException(Locale.Get("windows.startup.smokeNeedsProfile"));
        if (profile is not null && File.Exists(profile)) throw new ArgumentException(Locale.Get("windows.startup.profileIsFile"));
        if (smoke && Directory.Exists(profile) && Directory.EnumerateFileSystemEntries(profile!).Any())
            throw new ArgumentException(Locale.Get("windows.startup.smokeProfileNotEmpty"));
        return new(profile, smoke, exit, perf);
    }

    /// <summary>
    /// The perf scenario's switches, as files in its profile before the window reads them at start: <c>perf.log</c> so the
    /// heavy steps are timed (PerfLog), and <c>transcript-web</c> when <c>MIGHTY_PERF_WEB=1</c> so the run measures the web
    /// transcript. Written after <see cref="Parse"/> checked that the profile was empty.
    /// </summary>
    internal void PreparePerfProfile()
    {
        if (!PerfScenario || ProfileDirectory is null) return;
        Directory.CreateDirectory(ProfileDirectory);
        File.WriteAllText(Path.Combine(ProfileDirectory, "perf.log"), "");
        if (Environment.GetEnvironmentVariable("MIGHTY_PERF_WEB") == "1") File.WriteAllText(Path.Combine(ProfileDirectory, "transcript-web"), "");
    }

    /// <summary>
    /// The web shell's smoke switch: with <c>MIGHTY_SMOKE_WEB=1</c> a smoke run's profile gets <c>ui-web</c> before the window
    /// reads it (WebShell.Configure), so the run checks the web shell (MainWindow.WebShell.cs) instead of the WinUI screens.
    /// Written after <see cref="Parse"/> checked that the profile was empty.
    /// </summary>
    internal void PrepareWebShellProfile()
    {
        if (!SmokeTest || ProfileDirectory is null || Environment.GetEnvironmentVariable("MIGHTY_SMOKE_WEB") != "1") return;
        Directory.CreateDirectory(ProfileDirectory);
        File.WriteAllText(Path.Combine(ProfileDirectory, "ui-web"), "");
    }

    internal void TraceStartup(string stage)
    {
        if (!SmokeTest || ProfileDirectory is null) return;
        try
        {
            Directory.CreateDirectory(ProfileDirectory);
            File.AppendAllText(Path.Combine(ProfileDirectory, "startup.log"), $"{DateTimeOffset.UtcNow:O} {stage}\n");
        }
        catch { /* Diagnostics must not change startup behavior. */ }
    }

    [ThreadStatic] private static bool tracingThrown;
    private int thrownTraced;

    /// <summary>
    /// One thrown exception, handled or not, in the smoke's log with the frames that threw it, at most 2,000 per run. An exception
    /// in a dispatcher callback ends a WinUI process as a stowed exception that no unhandled-exception handler sees; this trail is
    /// then the only record of what threw.
    /// </summary>
    internal void TraceThrown(Exception error)
    {
        if (tracingThrown || Interlocked.Increment(ref thrownTraced) > 2000) return;
        tracingThrown = true;
        try
        {
            var frames = string.Join(" < ", (new StackTrace(2, false).GetFrames() ?? []).Take(8).Select(frame => frame.GetMethod() is { } method ? method.DeclaringType?.Name + "." + method.Name : "?"));
            var message = error.Message.ReplaceLineEndings(" ");
            TraceStartup($"thrown {error.GetType().FullName}: {(message.Length > 240 ? message[..240] : message)} @ {frames}");
        }
        catch { /* Diagnostics must not change startup behavior. */ }
        finally { tracingThrown = false; }
    }

    private bool failureWritten;

    /// <summary>The smoke has written its own record of a failed run: nothing thrown afterwards replaces it.</summary>
    internal void KeepFailureRecord() => failureWritten = true;

    internal void WriteStartupFailure(Exception error)
    {
        if (!SmokeTest || ProfileDirectory is null) return;
        // The first failure is the run's. One thrown later, while the failed run is being taken down, goes to the
        // log and leaves the record of the first as it is.
        if (failureWritten) { TraceStartup("later-failure " + error.GetType().FullName + ": " + error.Message); return; }
        failureWritten = true;
        try
        {
            Directory.CreateDirectory(ProfileDirectory);
            File.WriteAllText(Path.Combine(ProfileDirectory, "smoke-result.json"), JsonSerializer.Serialize(new
            {
                passed = false,
                phase = "startup",
                error = error.Message,
                exceptionType = error.GetType().FullName,
                hresult = $"0x{error.HResult:X8}",
                exception = error.ToString(),
                aiRequestSent = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* Preserve the original exit code if the destination is unwritable. */ }
    }
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // "System" reads the display language first, then the user's Windows language list in order.
        Locale.PreferredLanguages = WindowsPreferredLanguages;
        // The update helper is this same executable in a second mode: it is
        // started detached with a minimal environment just before the app quits,
        // waits for the app to exit, verifies the package again and replaces the
        // install. It opens no window and reads no saved state.
        if (args.SequenceEqual(new[] { "--agent-io-mcp" })) return AgentIOMcp.MainAsync().GetAwaiter().GetResult();
        // The delegation MCP server, which a Claude pane's run gets beside mighty-terminal while the hidden switch is on (M/DelegationMCPServer.swift).
        if (args.SequenceEqual(new[] { DelegationMcp.HeadlessArgument })) return DelegationMcp.MainAsync().GetAwaiter().GetResult();
        // The scripted Claude the delegation smoke starts in the CLI's place, the app's own binary in a second mode (M/AppStore+DelegationSmoke.swift:7-12).
        if (args.Length >= 2 && args[0] == DelegationFakeClaude.Argument && Path.IsPathFullyQualified(args[1])) return DelegationFakeClaude.RunAsync(args[1], args[2..]).GetAwaiter().GetResult();
        if (AppUpdateInstallPlan.TryParse(args) is { } plan) return RunUpdateHelper(plan);

        StartupOptions? options = null;
        try
        {
            var startup = StartupOptions.Parse(args);
            options = startup;
            startup.TraceStartup("options-parsed");
            startup.PreparePerfProfile(); startup.PrepareWebShellProfile();
            if (startup.SmokeTest) AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception error) startup.WriteStartupFailure(error);
            };
            if (startup.SmokeTest) AppDomain.CurrentDomain.FirstChanceException += (_, args) => startup.TraceThrown(args.Exception);
            WinRT.ComWrappersSupport.InitializeComWrappers();
            startup.TraceStartup("com-wrappers-ready");
            Application.Start(parameters =>
            {
                startup.TraceStartup("application-start-callback");
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new MightyApplication(startup);
            });
            return Environment.ExitCode;
        }
        catch (Exception error)
        {
            options?.WriteStartupFailure(error);
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    /// The user's Windows languages, most preferred first; none when the list cannot be read
    /// (the display language, which Locale checks first, still names the user's language).
    private static IReadOnlyList<string> WindowsPreferredLanguages()
    {
        try { return Windows.System.UserProfile.GlobalizationPreferences.Languages.ToList(); }
        catch (Exception) { return []; }
    }

    /// The helper half. Never elevated, never interactive: the outcome goes to
    /// install.log beside the package so a failed swap can be read afterwards,
    /// whatever happened (an exception the replacement did not expect is logged
    /// too). With `--relaunch 0` (the install on quit) the app is not started
    /// again, so the helper itself brings the setup program's uninstall entry to
    /// the new version; the helper is the new version's executable, so its own
    /// version is it.
    private static int RunUpdateHelper(AppUpdateInstallPlan plan)
    {
        // One helper per install folder: a second one leaves at once, moving and starting nothing (AppUpdateHelperGuard).
        using var single = new Mutex(false, AppUpdateHelperGuard.MutexName(plan.InstallDirectory));
        bool acquired;
        try { acquired = single.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        AppUpdateReplacementResult result;
        try
        {
            if (AppUpdateHelperGuard.Refusal(acquired) is { } refused) result = refused;
            else result = AppUpdateReplacement.RunAsync(
                plan,
                token => AppUpdateReplacement.WaitForExitAsync(plan.AppProcessId, AppUpdateReplacement.DefaultExitTimeout, token),
                executable =>
                {
                    using (Process.Start(new ProcessStartInfo(executable)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(executable)!,
                    })) { }
                    return Task.CompletedTask;
                },
                runningAgain: token => AppUpdateReplacement.RunningFromInstallAsync(plan.InstallDirectory, TimeSpan.FromSeconds(30), token)).GetAwaiter().GetResult();
            if (result.Replaced && typeof(Program).Assembly.GetName().Version?.ToString(3) is { } version)
                InstallerRegistration.SyncDisplayVersion(plan.InstallDirectory, version);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            result = new(false, false, error.GetType().Name + ": " + error.Message, [], false, false);
        }
        // Appended, so a second helper's line and the first one's outcome are both kept.
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetDirectoryName(plan.PackagePath) ?? Path.GetTempPath(), "install.log"),
                $"{DateTimeOffset.UtcNow:O} replaced={result.Replaced} rolledBack={result.RolledBack} " +
                $"relaunch={plan.Relaunch} skipped={result.Skipped} started={result.Started} backupRemoved={result.BackupRemoved} {result.Error}\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* The outcome must not depend on the log. */ }
        if (acquired) single.ReleaseMutex();
        return result.Replaced || result.Skipped ? 0 : 1;
    }
}

public sealed partial class MightyApplication : Application
{
    private readonly StartupOptions options;
    private Window? window;
    public MightyApplication(StartupOptions options)
    {
        this.options = options;
        options.TraceStartup("application-constructor");
        UnhandledException += (_, args) =>
        {
            if (!options.SmokeTest) return;
            options.WriteStartupFailure(args.Exception);
            // A failed isolated test must not remain as a hung CI desktop app.
            Environment.Exit(1);
        };
        // App.xaml drives the XAML compiler's metadata and merged resources.pri.
        // A hand-written provider alone cannot resolve WinUI's theme dictionaries.
        InitializeComponent();
        options.TraceStartup("xaml-controls-resources-ready");
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        options.TraceStartup("on-launched");
        window = new MainWindow(options);
        options.TraceStartup("main-window-created");
        window.Activate();
        options.TraceStartup("main-window-activated");
    }
}
