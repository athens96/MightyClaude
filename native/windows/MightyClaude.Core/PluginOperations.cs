namespace MightyClaude.Core;

/// What the running app owns so that the two plugin mutations macOS has —
/// install one plugin, refresh the registered marketplaces — really run.
///
/// It holds the one shared CLI runner every plugin read and every mutation of
/// the app goes through, so the window never builds a runner of its own, and it
/// keeps the app-wide one-at-a-time gate: the Claude window and the Codex
/// window are two browsers, and macOS runs one plugin operation at a time for
/// the whole app, not one per window. A second operation is refused with the
/// macOS sentence instead of starting a second CLI.
///
/// The running app makes one of these at start (MainWindow, a field of the
/// window, not of the smoke harness) with the real runner. Core.Tests and the
/// smoke run make one from a fake runner or hand the window a fake reader, so
/// no claude or codex process is ever started by a check or by the smoke run.
public sealed class PluginOperations
{
    /// The one shared runner: one-shot, arguments handed over as a list, the
    /// macOS listing cap on captured output.
    public ICliRunner Runner { get; }

    /// True only for the instance the running app makes for itself — the one
    /// that speaks to the installed CLI. A fake-runner instance says false.
    public bool UsesInstalledCli { get; }

    private int running;

    /// The running app's instance.
    public PluginOperations()
        : this(new CliRunner(outputCapBytes: ClaudePluginSupport.MaximumListingBytes), true) { }

    /// A check's instance: any runner, nothing installed is ever started.
    public PluginOperations(ICliRunner runner) : this(runner, false) { }

    private PluginOperations(ICliRunner runner, bool usesInstalledCli)
    {
        Runner = runner ?? throw new ArgumentNullException(nameof(runner));
        UsesInstalledCli = usesInstalledCli;
    }

    /// The reader the provider's window reads and mutates through, built on the
    /// shared runner. Both answer with the same ClaudePluginSnapshot.
    public IPluginReader Reader(string provider) =>
        provider == ClaudePluginBrowser.CodexProvider
            ? new CodexPluginReader(Runner)
            : new ClaudePluginReader(Runner);

    /// True while an operation this object started is still running.
    public bool IsRunning => Volatile.Read(ref running) != 0;

    /// The install button's call: the browser assembles the arguments from the
    /// values the list just returned, runs them through the reader and reads
    /// the result back; this object only decides that it may start at all.
    public Task<ClaudePluginOperationResult> InstallAsync(
        ClaudePluginBrowser browser, IPluginReader reader, string pluginId,
        CancellationToken cancellation = default) =>
        OneAtATime(() => browser.InstallAsync(reader, pluginId, cancellation));

    /// 마켓플레이스 새로고침's call, by the same rule.
    public Task<ClaudePluginOperationResult> RefreshMarketplacesAsync(
        ClaudePluginBrowser browser, IPluginReader reader,
        CancellationToken cancellation = default) =>
        OneAtATime(() => browser.RefreshMarketplacesAsync(reader, cancellation));

    private async Task<ClaudePluginOperationResult> OneAtATime(Func<Task<ClaudePluginOperationResult>> operation)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            return new ClaudePluginOperationResult(ClaudePluginStatus.Busy, PluginStrings.OperationBusy);
        try { return await operation(); }
        finally { Volatile.Write(ref running, 0); }
    }
}
