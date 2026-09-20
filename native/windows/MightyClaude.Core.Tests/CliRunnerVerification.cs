using System.Diagnostics;
using MightyClaude.Core;

internal static class CliRunnerVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    // Runs the test binary in --fake-cli --version mode: exits immediately with "2.1.271".
    // Verifies exit code 0, non-empty output, and TimedOut=false.
    internal static async Task RealShortCommand()
    {
        var directory = Verification.Temp();
        try
        {
            var record = Path.Combine(directory, "record");
            var cmd = Verification.Self("--fake-cli", "claude", record, "--version");
            var runner = new CliRunner();
            var result = await runner.RunAsync(cmd.Binary, cmd.Prefix, TimeSpan.FromSeconds(5));
            Check(result.ExitCode == 0, $"exit code must be 0, got {result.ExitCode}");
            Check(!result.TimedOut, "short command must not time out");
            Check(result.Output.Contains("2.1.271"), $"output must contain version string, got: {result.Output.Trim()}");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    // Starts the test binary in --long-child mode (sleeps 60 s), fires the timeout at 2 s,
    // verifies TimedOut=true and that the result arrives well before the sleep expires.
    internal static async Task KillsCommandThatExceedsTimeout()
    {
        var directory = Verification.Temp();
        var pidFile = Path.Combine(directory, "timeout.pid");
        try
        {
            var cmd = Verification.Self("--long-child", pidFile);
            var runner = new CliRunner();
            var sw = Stopwatch.StartNew();
            var result = await runner.RunAsync(cmd.Binary, cmd.Prefix, TimeSpan.FromSeconds(2));
            sw.Stop();
            Check(result.TimedOut, "timed-out flag must be true");
            Check(sw.Elapsed.TotalSeconds < 15, $"must finish well before the 60 s sleep, took {sw.Elapsed.TotalSeconds:F1}s");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
