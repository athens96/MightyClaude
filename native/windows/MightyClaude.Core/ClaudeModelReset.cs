using System.Globalization;

namespace MightyClaude.Core;

/// <summary>
/// Settings › CLI accounts' "모델 초기화·다시 불러오기" (M/AppStore+ClaudeModelReset.swift:7-47): every Claude
/// pane goes back to the default model and effort, each workspace's model list is read from the CLI
/// again, and one sentence says how that went.
/// </summary>
public static class ClaudeModelReset
{
    /// <summary>How many lists are read at once, and how long one read may take before it counts as timed out.</summary>
    public const int Concurrency = 3;
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(7);

    /// <summary>The state with every Claude pane on the default model and effort; conversations are kept.</summary>
    public static AppSnapshot Apply(AppSnapshot state) => state with
    {
        Sessions = state.Sessions.Select(pane => pane.Kind == "claude" && pane.Provider == "claude" ? pane with { Model = "default", Settings = pane.Settings with { Effort = "default" } } : pane).ToList(),
    };

    /// <summary>
    /// Reads every workspace's list, since each folder can have its own CLI settings, and returns the result sentence.
    /// Each read starts on the thread pool (a CLI starts there, never on the caller's thread), at most
    /// <see cref="Concurrency"/> at a time, and is given a token that ends with <paramref name="cancellation"/> or after
    /// <paramref name="timeout"/> (<see cref="ReadTimeout"/>). A list that did not come from the CLI counts as not read:
    /// as timed out when its time ran out, else as a sign-in or setup the CLI still needs. A cancelled reset throws.
    /// </summary>
    public static async Task<string> ReloadAsync(IReadOnlyList<Workspace> workspaces, Func<Workspace, CancellationToken, Task<ModelCatalog>> read, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        using var gate = new SemaphoreSlim(Concurrency);
        async Task<(bool Read, bool TimedOut)> One(Workspace workspace)
        {
            await gate.WaitAsync(cancellation);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(timeout ?? ReadTimeout);
                var catalog = await Task.Run(() => read(workspace, deadline.Token), CancellationToken.None);
                cancellation.ThrowIfCancellationRequested();
                return (catalog.Source == "cli", catalog.Source != "cli" && deadline.IsCancellationRequested);
            }
            finally { gate.Release(); }
        }
        var results = await Task.WhenAll(workspaces.Select(One));
        return Result(workspaces.Count, results.Count(r => !r.Read && !r.TimedOut), results.Count(r => r.TimedOut));
    }

    /// <summary>The sentence for <paramref name="failed"/> lists the CLI would not give and <paramref name="timedOut"/> it did not give in time, of <paramref name="workspaces"/>.</summary>
    public static string Result(int workspaces, int failed, int timedOut = 0)
    {
        Dictionary<string, string> Count(int value) => new() { ["count"] = value.ToString(CultureInfo.InvariantCulture) };
        if (workspaces == 0) return Locale.Get("settings.cliAccounts.modelReset.cacheCleared");
        if (failed == 0 && timedOut == 0) return Locale.Get("settings.cliAccounts.modelReset.done", Count(workspaces));
        var said = new List<string>();
        if (failed > 0) said.Add(Locale.Get("settings.cliAccounts.modelReset.partial", Count(failed)));
        if (timedOut > 0) said.Add(Locale.Get("settings.cliAccounts.modelReset.timedOut", Count(timedOut)));
        return string.Join(" ", said);
    }
}
