using System.Diagnostics;
using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace MightyClaude.WinUI;

/// Explicit profiles never import the user's legacy profile. Smoke runs require
/// a new/empty directory so CI cannot change saved conversations or credentials.
public sealed record StartupOptions(string? ProfileDirectory = null, bool SmokeTest = false, bool SmokeExit = false)
{
    public static StartupOptions Parse(string[] args)
    {
        string? profile = null;
        bool smoke = false, exit = false;
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
                default: throw new ArgumentException(Locale.Get("windows.startup.unsupportedArgument", new Dictionary<string, string> { ["argument"] = args[index] }));
            }
        }
        if (exit && !smoke) throw new ArgumentException(Locale.Get("windows.startup.smokeExitNeedsSmoke"));
        if (smoke && profile is null) throw new ArgumentException(Locale.Get("windows.startup.smokeNeedsProfile"));
        if (profile is not null && File.Exists(profile)) throw new ArgumentException(Locale.Get("windows.startup.profileIsFile"));
        if (smoke && Directory.Exists(profile) && Directory.EnumerateFileSystemEntries(profile!).Any())
            throw new ArgumentException(Locale.Get("windows.startup.smokeProfileNotEmpty"));
        return new(profile, smoke, exit);
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
        if (AppUpdateInstallPlan.TryParse(args) is { } plan) return RunUpdateHelper(plan);

        StartupOptions? options = null;
        try
        {
            var startup = StartupOptions.Parse(args);
            options = startup;
            startup.TraceStartup("options-parsed");
            if (startup.SmokeTest) AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception error) startup.WriteStartupFailure(error);
            };
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
    /// install.log beside the package so a failed swap can be read afterwards.
    private static int RunUpdateHelper(AppUpdateInstallPlan plan)
    {
        var result = AppUpdateReplacement.RunAsync(
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
            }).GetAwaiter().GetResult();
        try
        {
            File.WriteAllText(
                Path.Combine(Path.GetDirectoryName(plan.PackagePath) ?? Path.GetTempPath(), "install.log"),
                $"{DateTimeOffset.UtcNow:O} replaced={result.Replaced} rolledBack={result.RolledBack} " +
                $"started={result.Started} backupRemoved={result.BackupRemoved} {result.Error}\n");
        }
        catch (IOException) { /* The outcome must not depend on the log. */ }
        return result.Replaced ? 0 : 1;
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
