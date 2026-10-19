namespace MightyClaude.Core;

/// <summary>
/// Remote workspaces in the desktop service (M/AppStore+RemoteWorkspaces.swift): a remote pane's runs go through the client, never
/// RunManager; and "이 컴퓨터 공유"'s host is wired to this PC's state, CLIs and runner here.
/// </summary>
public sealed partial class DesktopService
{
    /// <summary>How long one reading of this PC's CLIs answers every /info and client start before it is read again.</summary>
    private static readonly TimeSpan HostRuntimeFresh = TimeSpan.FromSeconds(30);
    private readonly object hostRuntimeGate = new();
    private Task<RuntimeInfo>? hostRuntime;
    private long hostRuntimeAt;

    /// <summary>The client of "다른 컴퓨터 연결"; null until <see cref="AttachRemote"/>.</summary>
    public RemoteClientService? Remote { get; private set; }

    /// <summary>
    /// Creates the client once. Its events and tool requests arrive as a local run's do, on the client's threads inside a run's lock:
    /// what they reach here takes <c>sync</c> briefly and never waits on the window. The client keeps the newest 32 waiting requests of a
    /// pane and withdraws older ones itself, so nothing here keeps a second count.
    /// </summary>
    public RemoteClientService AttachRemote(string dataDirectory, IRemoteKeyVault keys, RemoteClientOptions? options = null)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            return Remote ??= new RemoteClientService(store, dataDirectory, keys, Receive, value => ToolPermissionChanged?.Invoke(value), options);
        }
    }

    public bool IsRemoteWorkspace(string? workspaceId) { lock (sync) return snapshot.Workspaces.Any(w => w.Id == workspaceId && w.Remote is not null); }
    public bool IsRemoteSession(string sessionId)
    {
        lock (sync) return snapshot.Sessions.FirstOrDefault(s => s.Id == sessionId) is { } pane && snapshot.Workspaces.Any(w => w.Id == pane.WorkspaceId && w.Remote is not null);
    }

    /// <summary>
    /// Lists a connected computer's shared workspace in the sidebar and makes it active. Opened again, the saved entry is replaced by the
    /// one the store approved now (StateStore.ApproveRemote keeps its id), so the snapshot holds what the store holds.
    /// </summary>
    public async Task<Workspace> OpenRemoteWorkspaceAsync(string connectionId, string hostWorkspaceId)
    {
        var client = Remote ?? throw new InvalidOperationException(Locale.Get("remote.run.error.notConnected"));
        var workspace = client.OpenWorkspace(connectionId, hostWorkspaceId);
        await UpdateAsync(s => WorkspaceDisclosure.Open(s with { Workspaces = s.Workspaces.Any(w => w.Id == workspace.Id) ? s.Workspaces.Select(w => w.Id == workspace.Id ? workspace : w).ToList() : [.. s.Workspaces, workspace] }, workspace.Id) with { ActiveWorkspaceId = workspace.Id });
        return workspace;
    }

    /// <summary>A workspace a sharing host may serve: registered, local, its folder present.</summary>
    public Task<Workspace> ResolveSharedAsync(string id) => ResolveLocal(id);

    /// <summary>
    /// The runner a sharing host's client runs go through: this PC's CLIs, looking workspaces up only through the host's shared check, with
    /// no pane MCP (no agent I/O binding: the --plugin-dir is the bridge's hooks alone), no delegation and no picture cache.
    /// </summary>
    public IRemoteRunner CreateRemoteRunner(RemoteRunnerSinks sinks) =>
        new RunManager(sinks.Workspace, Providers, pluginDirectory, sinks.Event, sinks.Permission, images: null) { PowerShellCommands = true, GraphNodes = sinks.GraphNode, DelegationEnabled = () => false };

    /// <summary>
    /// "이 컴퓨터 공유"'s host wired to this service: the saved workspaces, the shared check, this PC's CLIs (one reading at a time, kept
    /// <see cref="HostRuntimeFresh"/>), and the runner above. <paramref name="admission"/> runs on a request's thread and must answer at
    /// once; <paramref name="runsChanged"/> is told from request and pool threads and must hand the count on without waiting (the window's
    /// TryEnqueue), never Invoke or Wait.
    /// </summary>
    public RemoteHostOptions HostOptions(string hostName, string appVersion, Func<StartRunRequest, string?> admission, Action<int> runsChanged, bool allowLoopbackForTests = false) => new()
    {
        Workspaces = () => { lock (sync) return snapshot.Workspaces.ToList(); },
        ResolveLocal = ResolveSharedAsync,
        Runtime = () => HostRuntimeAsync(appVersion),
        CreateRunner = CreateRemoteRunner,
        HostName = hostName,
        Admission = admission,
        RunsChanged = runsChanged,
        AllowLoopbackForTests = allowLoopbackForTests,
    };

    /// <summary>
    /// This PC's CLIs as a sharing host reports them. Every /info and every client agent start asks; one reading is shared by all who ask
    /// while it goes, and kept for <see cref="HostRuntimeFresh"/>, so callers that give up on a slow reading never leave more behind them.
    /// A reading that failed is tried again by the next caller.
    /// </summary>
    internal Task<RuntimeInfo> HostRuntimeAsync(string appVersion)
    {
        lock (hostRuntimeGate)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (hostRuntime is { } known && (!known.IsCompleted || known.IsCompletedSuccessfully && System.Diagnostics.Stopwatch.GetElapsedTime(hostRuntimeAt, now) < HostRuntimeFresh)) return known;
            hostRuntimeAt = now;
            return hostRuntime = Task.Run(async () => (await Providers.GetRuntimeAsync()) with { AppVersion = appVersion });
        }
    }

    /// <summary>
    /// A remote pane's run: connected first (the first send after launch reads the key from the vault), on a connection that still
    /// shares the workspace, unless stopped meanwhile. False when a stop came before the host took it (it ended stopped); with
    /// attachments that is thrown instead, as a local start's is, so the caller keeps them.
    /// </summary>
    private async Task<bool> StartRemoteAsync(StartRunRequest request, Workspace workspace, Route route)
    {
        var client = Remote ?? throw new InvalidOperationException(Locale.Get("remote.run.error.notConnected"));
        var reference = workspace.Remote!;
        var info = await client.EnsureConnectedAsync(reference.ConnectionId);
        if (!info.Shared.Any(w => w.Id == reference.WorkspaceId)) throw new InvalidOperationException(Locale.Get("remote.workspace.error.notShared"));
        try
        {
            lock (sync) if (closing || route.Cancelled) throw new OperationCanceledException();
            await client.StartAsync(request, workspace);
        }
        catch (OperationCanceledException)
        {
            if (request.Attachments is { Count: > 0 }) throw new OperationCanceledException(Locale.Get("run.error.attachmentSendCancelled"));
            return false;
        }
        // A stop that came after the check above but before the client listed the run found nothing to stop there: it is stopped now.
        bool cancelled; lock (sync) cancelled = route.Cancelled || closing;
        if (cancelled) await client.StopAsync(request.SessionId);
        return true;
    }

    /// <summary>An answer to a remote pane's request goes out without holding the caller (often the UI thread); a failure is said in the pane.</summary>
    private void AnswerRemote(string sessionId, Func<RemoteClientService, Task> answer)
    {
        if (Remote is not { } client) return;
        _ = Task.Run(async () =>
        {
            try { await answer(client); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Receive(RunEvent.Log(sessionId, "error", ex.Message)); }
        });
    }
}
