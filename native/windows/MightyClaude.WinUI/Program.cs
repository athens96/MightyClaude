using System.Text.Json;
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
                        throw new ArgumentException("--profile에는 중복되지 않는 절대 폴더 경로가 필요합니다.");
                    profile = Path.GetFullPath(args[index]);
                    break;
                case "--smoke-test": smoke = true; break;
                case "--smoke-exit": exit = true; break;
                default: throw new ArgumentException("지원하지 않는 실행 인자입니다: " + args[index]);
            }
        }
        if (exit && !smoke) throw new ArgumentException("--smoke-exit에는 --smoke-test가 필요합니다.");
        if (smoke && profile is null) throw new ArgumentException("스모크 검증에는 별도 --profile 폴더가 필요합니다.");
        if (profile is not null && File.Exists(profile)) throw new ArgumentException("프로필 경로가 파일입니다.");
        if (smoke && Directory.Exists(profile) && Directory.EnumerateFileSystemEntries(profile!).Any())
            throw new ArgumentException("스모크 프로필은 새 폴더이거나 비어 있어야 합니다.");
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

    internal void WriteStartupFailure(Exception error)
    {
        if (!SmokeTest || ProfileDirectory is null) return;
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
