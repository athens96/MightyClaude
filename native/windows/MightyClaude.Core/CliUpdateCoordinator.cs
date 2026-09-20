namespace MightyClaude.Core;

/// <summary>
/// Owns one CLI update run at a time over all providers in order — exactly what macOS
/// keeps in the AppStore (isUpdatingCLIs, cliUpdateFinishedAt, cliUpdateResults).
/// The updater delegate is injectable so tests and smoke checks use a fake.
/// </summary>
public sealed class CliUpdateCoordinator
{
    private readonly Func<string, CancellationToken, Task<CliUpdateResult>> updater;
    private readonly object gate = new();
    private bool isUpdating;
    private DateTimeOffset? finishedAt;
    private IReadOnlyList<CliUpdateResult> results = [];
    private CancellationTokenSource? cts;
    private Task? running;
    private bool automaticAttempted;
    private bool closing;

    public event Action? StateChanged;

    public bool IsUpdating { get { lock (gate) return isUpdating; } }
    public DateTimeOffset? FinishedAt { get { lock (gate) return finishedAt; } }
    public IReadOnlyList<CliUpdateResult> Results { get { lock (gate) return results; } }

    public CliUpdateCoordinator(Func<string, CancellationToken, Task<CliUpdateResult>> updater)
        => this.updater = updater;

    /// <summary>
    /// Starts one run over all providers in order. Returns false when already running or closing.
    /// </summary>
    public bool Start()
    {
        CancellationTokenSource source;
        lock (gate)
        {
            if (closing || isUpdating) return false;
            source = new CancellationTokenSource();
            cts = source;
            isUpdating = true;
            finishedAt = null;
            results = [];
        }
        StateChanged?.Invoke();
        running = Task.Run(() => RunAsync(source));
        return true;
    }

    /// <summary>
    /// Starts one automatic run when AutoUpdateCLIs is true and this is the first call.
    /// Never runs under --smoke-test; the caller must skip this method in that case.
    /// </summary>
    public void BeginAutomaticIfNeeded(AppSnapshot snapshot)
    {
        lock (gate)
        {
            if (automaticAttempted || closing || snapshot.AutoUpdateCLIs != true) return;
            automaticAttempted = true;
        }
        Start();
    }

    /// <summary>Cancels the running update and waits for it to stop cleanly.</summary>
    public async Task CancelAsync()
    {
        Task? task;
        lock (gate) { cts?.Cancel(); task = running; }
        if (task is not null) try { await task; } catch { }
    }

    /// <summary>Refuses all new work and cancels any current run.</summary>
    public async Task ShutdownAsync()
    {
        lock (gate) closing = true;
        await CancelAsync();
    }

    private async Task RunAsync(CancellationTokenSource source)
    {
        var all = new List<CliUpdateResult>();
        try
        {
            foreach (var provider in Wire.Providers)
            {
                if (source.Token.IsCancellationRequested) break;
                var result = await updater(provider, source.Token);
                all.Add(result);
                lock (gate) results = all.ToList();
                StateChanged?.Invoke();
            }
        }
        finally
        {
            lock (gate) { isUpdating = false; finishedAt = DateTimeOffset.UtcNow; cts = null; }
            StateChanged?.Invoke();
            source.Dispose();
        }
    }
}
