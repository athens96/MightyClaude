using System.Diagnostics;
using System.Text;
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

    private static bool Alive(int id) { try { using var process = Process.GetProcessById(id); return !process.HasExited; } catch (ArgumentException) { return false; } }

    // A command that fails must hand back its own exit code and its error output,
    // not an exception, so a caller can report the installer's own message.
    internal static async Task ReportsExitCodeAndErrorOutput()
    {
        var runner = new CliRunner();
        var cmd = Verification.Self("--fail-child", "installer said no");
        var result = await runner.RunAsync(cmd.Binary, cmd.Prefix, TimeSpan.FromSeconds(10));
        Check(result.ExitCode == 3, $"exit code must be the child's 3, got {result.ExitCode}");
        Check(!result.TimedOut, "a command that exits on its own must not be reported as timed out");
        Check(result.ErrorOutput.Contains("installer said no"), $"error output must be captured, got: {result.ErrorOutput.Trim()}");
        Check(result.Output.Trim().Length == 0, $"standard output must stay empty, got: {result.Output.Trim()}");
    }

    // A command that writes far more than the cap is truncated, and the child still
    // finishes instead of blocking on a pipe nobody drains.
    internal static async Task CapsTheCapturedOutput()
    {
        var runner = new CliRunner(outputCapBytes: 2048);
        var cmd = Verification.Self("--noisy-child", "262144");
        var result = await runner.RunAsync(cmd.Binary, cmd.Prefix, TimeSpan.FromSeconds(20));
        var bytes = Encoding.UTF8.GetByteCount(result.Output);
        Check(result.ExitCode == 0, $"a capped command must still finish, got exit {result.ExitCode}");
        Check(!result.TimedOut, "a capped command must not be reported as timed out");
        Check(bytes > 0, "capped output must still hold the start of the stream");
        Check(bytes <= 2048, $"captured output must not exceed the cap, got {bytes} bytes");
    }

    // Arguments and output pass through one redaction point, so a token handed to a
    // CLI or echoed back by it never lands in a log in the clear.
    internal static async Task KeepsSecretsOutOfTheLog()
    {
        const string secret = "sk-live-000-should-never-be-logged";
        var lines = new List<string>();
        var runner = new CliRunner(lines.Add, text => text.Replace(secret, "[redacted]"));
        var cmd = Verification.Self("--fail-child", secret);
        var result = await runner.RunAsync(cmd.Binary, cmd.Prefix, TimeSpan.FromSeconds(10));
        Check(result.ErrorOutput.Contains(secret), "the caller must still receive the unredacted output");
        Check(lines.Count > 0, "the runner must log through the redaction point");
        Check(lines.All(line => !line.Contains(secret)), "no log line may contain the secret");
        Check(lines.Any(line => line.Contains("[redacted]")), "the argument and the output must reach the log redacted");
    }

    // The fixture starts a child of its own; when the time limit fires, the group kill
    // must take that grandchild down as well.
    internal static async Task TimeoutKillsTheWholeProcessGroup()
    {
        var directory = Verification.Temp();
        var pidFile = Path.Combine(directory, "group.pid");
        try
        {
            var cmd = Verification.Self("--group-child", pidFile);
            var runner = new CliRunner();
            var run = runner.RunAsync(cmd.Binary, cmd.Prefix, TimeSpan.FromSeconds(6));
            await Verification.Until(() => File.Exists(pidFile), 15000);
            var grandchild = int.Parse(await File.ReadAllTextAsync(pidFile));
            Check(Alive(grandchild), "the fixture grandchild must be running before the time limit fires");
            var result = await run;
            Check(result.TimedOut, "timed-out flag must be true");
            await Verification.Until(() => !Alive(grandchild), 10000);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    // Cancelling the caller's token stops the running command and its children too.
    internal static async Task CancellationStopsTheCommandAndItsChildren()
    {
        var directory = Verification.Temp();
        var pidFile = Path.Combine(directory, "cancel.pid");
        try
        {
            var cmd = Verification.Self("--group-child", pidFile);
            var runner = new CliRunner();
            using var cancellation = new CancellationTokenSource();
            var run = runner.RunAsync(cmd.Binary, cmd.Prefix, TimeSpan.FromMinutes(5), cancellation.Token);
            await Verification.Until(() => File.Exists(pidFile), 15000);
            var grandchild = int.Parse(await File.ReadAllTextAsync(pidFile));
            cancellation.Cancel();
            try { await run; throw new InvalidOperationException("cancellation must surface to the caller"); }
            catch (OperationCanceledException) { }
            await Verification.Until(() => !Alive(grandchild), 10000);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
