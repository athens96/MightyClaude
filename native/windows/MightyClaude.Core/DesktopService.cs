namespace MightyClaude.Core;

/// <summary>Single owner of persisted state and local/remote pane routing.</summary>
public sealed class DesktopService : IAsyncDisposable
{
    private readonly object sync = new();
    private sealed class Route(bool remote) { internal readonly bool Remote = remote; internal bool Started, Cancelled; }
    private readonly Dictionary<string, Route> routes = [];
    private readonly StateStore store;
    private readonly RunManager local;
    private Task saved = Task.CompletedTask;
    private AppSnapshot snapshot = new();
    private bool closing;
    public ProviderCatalog Providers { get; }
    public RemoteController Remote { get; }
    public event Action<RunEvent>? RunEventReceived;
    /// <summary>
    /// A Claude tool-permission request waiting in — or settled by — the local
    /// run pane. It is deliberately not a <see cref="RunEvent"/>: an ephemeral
    /// request never enters <see cref="AppSnapshot"/> and never travels to a
    /// remote peer. Remote panes keep launching with prompts off.
    /// </summary>
    public event Action<ToolPermissionRequest>? ToolPermissionChanged;
    public event Action<Exception>? PersistenceFailed;
    public AppSnapshot Snapshot { get { lock (sync) { var copy = Wire.Clone(snapshot); return copy with { Sessions = copy.Sessions.Select(s => s with { CurrentActivity = snapshot.Sessions.FirstOrDefault(original => original.Id == s.Id)?.CurrentActivity }).ToList() }; } } }
    public DesktopService(string directory, string? legacyDirectory, string pluginDirectory, ProviderCatalog? providers = null, ISecretProtector? protector = null, bool testLoopback = false)
    {
        store = new(directory, legacyDirectory); Providers = providers ?? new(); local = new(ResolveLocal, Providers, pluginDirectory, Receive, value => ToolPermissionChanged?.Invoke(value));
        Remote = new(directory, () => Snapshot.Workspaces, ResolveLocal, () => Providers.GetRuntimeAsync(), emit => new RunManager(ResolveLocal, Providers, pluginDirectory, emit), Receive, protector ?? (OperatingSystem.IsWindows() ? new WindowsSecretProtector() : null), testLoopback, legacyDirectory: legacyDirectory is null ? null : Path.Combine(legacyDirectory, "remote"));
    }
    /// <summary>이번만 허용 / 거부 — the only way a request is ever answered, one request at a time.</summary>
    public void RespondToToolPermission(string sessionId, string requestId, bool allow)
        => local.RespondToToolPermission(sessionId, requestId, allow);
    public async Task InitializeAsync() { var loaded = await store.LoadAsync(); lock (sync) snapshot = loaded; }
    private Task<Workspace> ResolveLocal(string id)
    {
        lock (sync) if (!snapshot.Workspaces.Any(w => w.Id == id && w.Remote is null)) throw new ArgumentException("등록된 로컬 워크스페이스가 아닙니다.");
        return store.ResolveLocalAsync(id);
    }
    public async Task<Workspace> AddWorkspaceAsync(string path)
    {
        var workspace = store.ApproveLocal(path);
        await UpdateAsync(s => s with { Workspaces = s.Workspaces.Any(w => w.Id == workspace.Id) ? s.Workspaces : s.Workspaces.Append(workspace).ToList(), ActiveWorkspaceId = workspace.Id }); return workspace;
    }
    public async Task<Workspace> ImportRemoteAsync(string connectionId, string workspaceId)
    {
        var peer = Remote.GetRemoteWorkspace(connectionId, workspaceId); var state = await Remote.GetStateAsync();
        var workspace = store.ApproveRemote(connectionId, peer, state.Connections.Single(c => c.Id == connectionId).Name);
        await UpdateAsync(s => s with { Workspaces = s.Workspaces.Any(w => w.Id == workspace.Id) ? s.Workspaces : s.Workspaces.Append(workspace).ToList(), ActiveWorkspaceId = workspace.Id }); return workspace;
    }
    public async Task RemoveWorkspaceAsync(string id)
    {
        foreach (var pane in Snapshot.Sessions.Where(s => s.WorkspaceId == id)) await StopAsync(pane.Id);
        await UpdateAsync(s => s with { Workspaces = s.Workspaces.Where(w => w.Id != id).ToList(), Sessions = s.Sessions.Where(p => p.WorkspaceId != id).ToList(), ActiveWorkspaceId = s.ActiveWorkspaceId == id ? s.Workspaces.FirstOrDefault(w => w.Id != id)?.Id : s.ActiveWorkspaceId });
    }
    public Task UpdateAsync(Func<AppSnapshot, AppSnapshot> update)
    {
        lock (sync) { ObjectDisposedException.ThrowIf(closing, this); snapshot = StateStore.Normalize(update(snapshot), false); return QueueSave(); }
    }
    private Task QueueSave()
    {
        var value = Wire.Clone(snapshot);
        return saved = saved.ContinueWith(async _ => { try { await store.SaveAsync(value); } catch (Exception ex) { PersistenceFailed?.Invoke(ex); throw; } }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }
    private void Receive(RunEvent value)
    {
        if (!value.Valid()) return;
        lock (sync) { snapshot = snapshot.Apply(value); if (value.Type == "status" && value.Status is "stopped" or "completed" or "error") routes.Remove(value.SessionId); _ = QueueSave(); }
        RunEventReceived?.Invoke(value);
    }
    public async Task StartAsync(StartRunRequest value)
    {
        var request = value.Validate(); Workspace workspace; Route route;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            workspace = snapshot.Workspaces.FirstOrDefault(w => w.Id == request.WorkspaceId) ?? throw new ArgumentException("등록된 워크스페이스가 아닙니다.");
            if (!snapshot.Sessions.Any(s => s.Id == request.SessionId && s.WorkspaceId == request.WorkspaceId && s.Kind == request.Kind)) throw new ArgumentException("현재 워크스페이스의 실행 창이 아닙니다.");
            route = new(workspace.Remote is not null);
            if (!routes.TryAdd(request.SessionId, route)) throw new InvalidOperationException("이미 실행 중인 창입니다.");
            snapshot = snapshot with { Sessions = snapshot.Sessions.Select(s => s.Id == request.SessionId ? s with { SessionUsage = request.ResumeId is null || s.Provider != request.Provider ? null : s.SessionUsage, Provider = request.Provider, CurrentActivity = null, RunTiming = request.Kind == "shell" ? null : AgentRunTiming.Begin() } : s).ToList() };
            var history = request.Input + (request.Attachments is { Count: > 0 } ? "\n\n" + AttachmentSupport.Summary(request.Attachments) : "");
            snapshot = snapshot.Apply(RunEvent.State(request.SessionId, "running")).Apply(RunEvent.Log(request.SessionId, "user", history, request.Kind == "claude" ? request.Provider : null)); _ = QueueSave();
        }
        RunEventReceived?.Invoke(RunEvent.State(request.SessionId, "running"));
        try
        {
            Task started;
            lock (sync)
            {
                if (closing || route.Cancelled) { Receive(RunEvent.State(request.SessionId, "stopped")); if (request.Attachments is { Count: > 0 }) throw new OperationCanceledException("첨부 전송이 취소되었습니다."); return; }
                route.Started = true; started = workspace.Remote is null ? local.StartAsync(request) : Remote.StartRunAsync(request, workspace);
            }
            await started;
        }
        catch (OperationCanceledException) { Receive(RunEvent.State(request.SessionId, "stopped")); throw; }
        catch (Exception ex) { Receive(RunEvent.Log(request.SessionId, "error", ex.Message)); Receive(RunEvent.State(request.SessionId, "error")); throw; }
    }
    public Task StopAsync(string id)
    {
        Route route; lock (sync) { if (!routes.TryGetValue(id, out route!)) return Task.CompletedTask; route.Cancelled = true; if (!route.Started) return Task.CompletedTask; }
        return route.Remote ? Remote.StopRunAsync(id) : local.StopAsync(id);
    }
    public async ValueTask DisposeAsync()
    {
        lock (sync) { if (closing) return; closing = true; }
        await Providers.DisposeAsync(); await Task.WhenAll(local.DisposeAsync().AsTask(), Remote.DisposeAsync().AsTask());
        Task flush; lock (sync) flush = QueueSave(); await flush;
    }
}
