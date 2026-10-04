using System.Diagnostics;
using MightyClaude.Core;

internal static class StatusLineShutdownVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static StatusLineContext Context(string directory) => new("shutdown-fixture", directory, directory, null, null, null, null, null, null, null, null, null, null, null, null, null, false, null, null, null, null);
    internal static async Task CloseCancelsAndDrainsBeforeReplacement()
    {
        var cancellationSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var user = new StatusLineConfig("fixture", 0, "user", false); var runs = 0; var concurrent = 0; var maximum = 0;
        var old = new StatusLineRefresher(() => new(null, user), () => new(), "workspace", async (_, _, token) =>
        {
            runs++; maximum = Math.Max(maximum, ++concurrent);
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancellationSeen.TrySetResult(); await releaseCleanup.Task; }
            finally { concurrent--; }
            return new([], null, 0, false);
        });
        old.RequestRefresh(Context(Path.GetTempPath())); old.Close();
        await cancellationSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!old.WhenStopped.IsCompleted, "Close must wait for runner cleanup, not only signal cancellation.");
        var replacement = new StatusLineRefresher(() => new(null, user), () => new(), "workspace", (_, _, _) =>
        { runs++; maximum = Math.Max(maximum, ++concurrent); concurrent--; return Task.FromResult(new StatusLineResult([], null, 0, false)); });
        async Task EnableAgain() { await old.WhenStopped; replacement.RequestRefresh(Context(Path.GetTempPath())); }
        var resumed = EnableAgain(); old.RequestRefresh(Context(Path.GetTempPath()), force: true);
        Check(runs == 1, "Closed refresher/replacement cannot overlap the command awaiting cleanup.");
        releaseCleanup.TrySetResult(); await resumed.WaitAsync(TimeSpan.FromSeconds(2));
        Check(runs == 2 && maximum == 1 && old.Result is null, "Only replacement publishes after the prior child has stopped.");
        replacement.Close(); await replacement.WhenStopped;
    }
    internal static async Task RealCommandCancellationAndStderrDrain()
    {
        // This launches only a bounded local shell fixture, never user settings.
        var directory = Verification.Temp();
        try
        {
            var powershell = Path.GetFileNameWithoutExtension(StatusLineSupport.Shell("", OperatingSystem.IsWindows()).Binary).Equals("powershell", StringComparison.OrdinalIgnoreCase);
            var command = powershell ? "Start-Sleep -Seconds 60" : "sleep 60";
            using var cancellation = new CancellationTokenSource(); var timer = Stopwatch.StartNew();
            var run = StatusLineSupport.RunAsync(new(command, 0, "fixture", false), Context(directory), timeout: 30, cancellation: cancellation.Token);
            await Task.Delay(100); cancellation.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Cancellation was ignored."); } catch (OperationCanceledException) { }
            Check(timer.Elapsed < TimeSpan.FromSeconds(5), "Closing must terminate the owned shell tree promptly.");
            var noisy = powershell ? "1..12000 | ForEach-Object { [Console]::Error.WriteLine('discarded-stderr') }; [Console]::WriteLine('done')" : "i=0; while [ $i -lt 12000 ]; do echo discarded-stderr >&2; i=$((i+1)); done; printf done";
            var output = await StatusLineSupport.RunAsync(new(noisy, 0, "fixture", false), Context(directory), timeout: 10);
            Check(!output.TimedOut && output.ExitCode == 0, "Status command stderr must drain past pipe capacity without blocking.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
