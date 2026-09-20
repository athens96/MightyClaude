using System.Text;

namespace MightyClaude.Core;

public sealed record CliRunResult(int ExitCode, string Output, string ErrorOutput, bool TimedOut);

public interface ICliRunner
{
    Task<CliRunResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellation = default,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null);
}

public sealed class CliRunner : ICliRunner
{
    // 1 MiB cap on captured output — matches the macOS updater limit.
    public const int OutputCapBytes = 1024 * 1024;

    // The single redaction point: every piece of text reaching the log must pass
    // through Log(), which applies redact before forwarding to the log delegate.
    // This ensures secrets in arguments or output never reach a log unredacted.
    private readonly Action<string>? log;
    private readonly Func<string, string>? redact;
    private readonly int cap;

    public CliRunner(Action<string>? log = null, Func<string, string>? redact = null, int outputCapBytes = OutputCapBytes)
    {
        this.log = log;
        this.redact = redact;
        cap = outputCapBytes > 0 ? outputCapBytes : OutputCapBytes;
    }

    private void Log(string text) => log?.Invoke(redact is not null ? redact(text) : text);

    public async Task<CliRunResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellation = default,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null)
    {
        var cwd = workingDirectory is { Length: > 0 } && Directory.Exists(workingDirectory)
            ? workingDirectory
            : Environment.CurrentDirectory;

        IDictionary<string, string>? env = environment is not null
            ? new Dictionary<string, string>(environment)
            : null;

        ChildProcess? process = null;
        try
        {
            Log("run " + executable + " " + string.Join(" ", arguments));
            var info = ChildProcess.StartInfo(executable, arguments, cwd, env);
            process = ChildProcess.Start(info);
            process.Input.Close();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            linked.CancelAfter(timeout);

            bool timedOut = false;
            int exitCode;

            // Both pipes are drained from the first instruction so a talkative child
            // never blocks, and whatever arrived is kept even when the run is killed.
            var outTask = ReadCappedAsync(process.Output, cap);
            var errTask = ReadCappedAsync(process.Error, cap);
            try
            {
                await process.Completion.WaitAsync(linked.Token);
                exitCode = process.Completion.Result;
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                // The time limit ran out; kill the child together with its whole group.
                timedOut = true;
                exitCode = -1;
                process.Kill();
            }
            await Task.WhenAny(Task.WhenAll(outTask, errTask), Task.Delay(timedOut ? 1000 : 500, CancellationToken.None));
            var output = outTask.IsCompletedSuccessfully ? outTask.Result : "";
            var errorOutput = errTask.IsCompletedSuccessfully ? errTask.Result : "";
            // OperationCanceledException from the caller's token propagates; DisposeAsync kills the group.

            Log($"exit={exitCode} timedOut={timedOut} outBytes={Encoding.UTF8.GetByteCount(output)} errBytes={Encoding.UTF8.GetByteCount(errorOutput)}");
            if (output.Length > 0) Log("stdout " + output);
            if (errorOutput.Length > 0) Log("stderr " + errorOutput);
            return new CliRunResult(exitCode, output, errorOutput, timedOut);
        }
        finally { if (process is not null) await process.DisposeAsync(); }
    }

    // Keeps at most maxBytes of text but goes on draining the pipe, so a child that
    // writes more than the cap never blocks on a full pipe and can still exit.
    private static async Task<string> ReadCappedAsync(TextReader reader, int maxBytes)
    {
        var sb = new StringBuilder();
        var buf = new char[4096];
        var totalBytes = 0;
        var capped = false;
        while (true)
        {
            int read;
            try { read = await reader.ReadAsync(buf, 0, buf.Length); } catch { break; }
            if (read == 0) break;
            if (capped) continue;
            var chunk = new string(buf, 0, read);
            var bytes = Encoding.UTF8.GetByteCount(chunk);
            if (totalBytes + bytes > maxBytes)
            {
                var room = maxBytes - totalBytes;
                var take = 0;
                while (take < chunk.Length && Encoding.UTF8.GetByteCount(chunk, 0, take + 1) <= room) take++;
                sb.Append(chunk, 0, take);
                capped = true;
                continue;
            }
            totalBytes += bytes;
            sb.Append(chunk);
        }
        return sb.ToString();
    }
}
