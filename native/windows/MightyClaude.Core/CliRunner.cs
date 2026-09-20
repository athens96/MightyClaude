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

    public CliRunner(Action<string>? log = null, Func<string, string>? redact = null)
    {
        this.log = log;
        this.redact = redact;
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
            var info = ChildProcess.StartInfo(executable, arguments, cwd, env);
            process = ChildProcess.Start(info);
            process.Input.Close();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            linked.CancelAfter(timeout);

            bool timedOut = false;
            int exitCode;
            string output;
            string errorOutput;

            try
            {
                var outTask = ReadCappedAsync(process.Output, OutputCapBytes);
                var errTask = ReadCappedAsync(process.Error, OutputCapBytes);
                await process.Completion.WaitAsync(linked.Token);
                exitCode = process.Completion.Result;
                // Brief drain window for background children that still hold the pipe.
                await Task.WhenAny(Task.WhenAll(outTask, errTask), Task.Delay(500, CancellationToken.None));
                output = outTask.IsCompleted ? outTask.Result : "";
                errorOutput = errTask.IsCompleted ? errTask.Result : "";
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                // Timeout fired; kill the whole process group.
                timedOut = true;
                exitCode = -1;
                process.Kill();
                output = "";
                errorOutput = "";
                try { await Task.WhenAny(Task.WhenAll(ReadCappedAsync(process.Output, OutputCapBytes), ReadCappedAsync(process.Error, OutputCapBytes)), Task.Delay(200, CancellationToken.None)); } catch { }
            }
            // OperationCanceledException from the caller's token propagates; DisposeAsync kills the group.

            Log($"exit={exitCode} timedOut={timedOut} outBytes={Encoding.UTF8.GetByteCount(output)} errBytes={Encoding.UTF8.GetByteCount(errorOutput)}");
            return new CliRunResult(exitCode, output, errorOutput, timedOut);
        }
        finally { if (process is not null) await process.DisposeAsync(); }
    }

    private static async Task<string> ReadCappedAsync(TextReader reader, int maxBytes)
    {
        var sb = new StringBuilder();
        var buf = new char[4096];
        var totalBytes = 0;
        while (true)
        {
            int read;
            try { read = await reader.ReadAsync(buf, 0, buf.Length); } catch { break; }
            if (read == 0) break;
            var chunk = new string(buf, 0, read);
            totalBytes += Encoding.UTF8.GetByteCount(chunk);
            if (totalBytes > maxBytes) { sb.Append(chunk[..Math.Max(0, chunk.Length - (totalBytes - maxBytes) / 2)]); break; }
            sb.Append(chunk);
        }
        return sb.ToString();
    }
}
