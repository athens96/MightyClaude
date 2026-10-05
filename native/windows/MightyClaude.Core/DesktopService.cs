namespace MightyClaude.Core;

/// <summary>Single owner of persisted state and local pane routing.</summary>
public sealed class DesktopService : IAsyncDisposable
{
    private readonly object sync = new();
    private sealed class Route { internal bool Started, Cancelled; }
    private readonly Dictionary<string, Route> routes = [];
    private readonly StateStore store;
    private readonly RunManager local;
    private Task saved = Task.CompletedTask;
    private AppSnapshot snapshot = new();
    private bool closing;
    public ProviderCatalog Providers { get; }
    /// <summary>Pictures agents showed, under the app data folder (macOS <c>image-cache</c>); transcripts hold only references.</summary>
    public AgentImageCache Images { get; }
    public event Action<RunEvent>? RunEventReceived;
    public event Action<StartRunRequest>? RequestStarting;
    /// <summary>
    /// A Claude tool-permission request waiting in — or settled by — the local
    /// run pane. It is deliberately not a <see cref="RunEvent"/>: an ephemeral
    /// request never enters <see cref="AppSnapshot"/>.
    /// </summary>
    public event Action<ToolPermissionRequest>? ToolPermissionChanged;
    public event Action<Exception>? PersistenceFailed;
    public AppSnapshot Snapshot { get { lock (sync) { var copy = Wire.Clone(snapshot); return copy with { Sessions = copy.Sessions.Select(s => s with { CurrentActivity = snapshot.Sessions.FirstOrDefault(original => original.Id == s.Id)?.CurrentActivity }).ToList() }; } } }
    /// <summary>The active pane's id alone, for a caller that asks on every pointer press and needs no copy of the state.</summary>
    public string? ActiveSessionId { get { lock (sync) return snapshot.ActiveSessionId; } }
    public DesktopService(string directory, string? legacyDirectory, string pluginDirectory, ProviderCatalog? providers = null)
    {
        store = new(directory, legacyDirectory); Providers = providers ?? new(); Images = new(Path.Combine(directory, "image-cache"));
        local = new(ResolveLocal, Providers, pluginDirectory, Receive, value => ToolPermissionChanged?.Invoke(value), Images);
    }
    /// <summary>이번만 허용 / 거부 — the only way a request is ever answered, one request at a time.</summary>
    public bool HasActiveProvider(string provider)
    {
        lock (sync) return snapshot.Sessions.Any(s => s.Kind == "claude" && s.Provider == provider && (s.Status == "running" || routes.ContainsKey(s.Id)));
    }
    public void RespondToToolPermission(string sessionId, string requestId, bool allow)
        => local.RespondToToolPermission(sessionId, requestId, allow);
    public void ConfigureAgentIO(Func<StartRunRequest, Workspace, AgentIOBinding?>? factory) => local.AgentIOBindingFactory = factory;
    public bool IsSessionRunning(string id) => local.IsRunning(id);
    public async Task<bool> TrySteerAsync(string sessionId, string text)
    {
        if (!await local.TrySteerAsync(sessionId, text)) return false;
        Receive(RunEvent.Log(sessionId, "user", text, "claude")); return true;
    }
    public void AnswerQuestionnaire(string sessionId, string requestId, IReadOnlyDictionary<string, UserQuestionAnswer> answers)
        => local.AnswerQuestionnaire(sessionId, requestId, answers);
    public async Task InitializeAsync() { var loaded = await store.LoadAsync(); lock (sync) snapshot = loaded; }
    private Task<Workspace> ResolveLocal(string id)
    {
        lock (sync) if (!snapshot.Workspaces.Any(w => w.Id == id)) throw new ArgumentException(Locale.Get("run.error.localWorkspaceNotRegistered"));
        return store.ResolveLocalAsync(id);
    }
    public async Task<Workspace> AddWorkspaceAsync(string path)
    {
        var workspace = store.ApproveLocal(path);
        await UpdateAsync(s => WorkspaceDisclosure.Open(s with { Workspaces = s.Workspaces.Any(w => w.Id == workspace.Id) ? s.Workspaces : s.Workspaces.Append(workspace).ToList() }, workspace.Id) with { ActiveWorkspaceId = workspace.Id }); return workspace;
    }
    public async Task RemoveWorkspaceAsync(string id)
    {
        foreach (var pane in Snapshot.Sessions.Where(s => s.WorkspaceId == id)) await StopAsync(pane.Id);
        await UpdateAsync(s => s with { Workspaces = s.Workspaces.Where(w => w.Id != id).ToList(), Sessions = s.Sessions.Where(p => p.WorkspaceId != id).ToList(), ActiveWorkspaceId = s.ActiveWorkspaceId == id ? s.Workspaces.FirstOrDefault(w => w.Id != id)?.Id : s.ActiveWorkspaceId });
    }
    public Task RenameWorkspaceAsync(string id, string name) => UpdateAsync(s => RenameSupport.RenameWorkspace(s, id, name));
    public Task RenameSessionAsync(string id, string name) => UpdateAsync(s => RenameSupport.RenameSession(s, id, name));
    public Task SetSessionAutoTitleAsync(string id) => UpdateAsync(s => PaneTitle.SetAutomatic(s, id));
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
        if (value.Type == "resume" && value.ResumeId is { } resumed) RememberSession(resumed);
        RunEventReceived?.Invoke(value);
    }
    // Session ids this app's panes started or resumed (macOS KnownSessionIDs):
    // the "기존 세션 이어가기" list never hides them as automated runs.
    private readonly object knownSync = new();
    private List<string>? knownSessions;
    public string KnownSessionsPath => Path.Combine(store.DirectoryPath, KnownSessionIDs.FileName);
    public IReadOnlySet<string> KnownSessions()
    {
        lock (knownSync) return (knownSessions ??= KnownSessionIDs.Load(KnownSessionsPath)).Select(id => id.ToLowerInvariant()).ToHashSet();
    }
    public void RememberSession(string id)
    {
        lock (knownSync)
        {
            knownSessions ??= KnownSessionIDs.Load(KnownSessionsPath);
            if (KnownSessionIDs.Adding(id, knownSessions) is not { } next) return;
            knownSessions = next;
            try { KnownSessionIDs.Save(next, KnownSessionsPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { PersistenceFailed?.Invoke(ex); }
        }
    }
    public async Task StartAsync(StartRunRequest value)
    {
        var request = value.Validate(); Route route;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            if (!snapshot.Workspaces.Any(w => w.Id == request.WorkspaceId)) throw new ArgumentException(Locale.Get("run.error.workspaceNotRegistered"));
            if (!snapshot.Sessions.Any(s => s.Id == request.SessionId && s.WorkspaceId == request.WorkspaceId && s.Kind == request.Kind)) throw new ArgumentException(Locale.Get("run.error.sessionNotInWorkspace"));
            request = request with { PhaseModels = PhaseModelPreferences.Normalize(snapshot.PhaseModels) };
            route = new();
            if (!routes.TryAdd(request.SessionId, route)) throw new InvalidOperationException(Locale.Get("run.error.alreadyRunning"));
            snapshot = snapshot with { Sessions = snapshot.Sessions.Select(s => s.Id == request.SessionId ? PaneTitle.Requested(s, request.Input) with { SessionUsage = request.ResumeId is null || s.Provider != request.Provider ? null : s.SessionUsage, Provider = request.Provider, CurrentActivity = null, RunTiming = request.Kind == "shell" ? null : AgentRunTiming.Begin() } : s).ToList() };
            var history = request.Input + (request.Attachments is { Count: > 0 } ? "\n\n" + AttachmentSupport.Summary(request.Attachments) : "");
            snapshot = snapshot.Apply(RunEvent.State(request.SessionId, "running")).Apply(RunEvent.Log(request.SessionId, "user", history, request.Kind == "claude" ? request.Provider : null)); _ = QueueSave();
        }
        try
        {
            RequestStarting?.Invoke(request);
            RunEventReceived?.Invoke(RunEvent.State(request.SessionId, "running"));
            Task started;
            lock (sync)
            {
                if (closing || route.Cancelled) { Receive(RunEvent.State(request.SessionId, "stopped")); if (request.Attachments is { Count: > 0 }) throw new OperationCanceledException(Locale.Get("run.error.attachmentSendCancelled")); return; }
                route.Started = true; started = local.StartAsync(request);
            }
            await started;
        }
        catch (OperationCanceledException) { Receive(RunEvent.State(request.SessionId, "stopped")); throw; }
        catch (Exception ex) { Receive(RunEvent.Log(request.SessionId, "error", ex.Message)); Receive(RunEvent.State(request.SessionId, "error")); throw; }
    }
    public Task StopAsync(string id)
    {
        Route route; lock (sync) { if (!routes.TryGetValue(id, out route!)) return Task.CompletedTask; route.Cancelled = true; if (!route.Started) return Task.CompletedTask; }
        return local.StopAsync(id);
    }
    public async ValueTask DisposeAsync()
    {
        lock (sync) { if (closing) return; closing = true; }
        await Providers.DisposeAsync(); await local.DisposeAsync();
        Task flush; lock (sync) flush = QueueSave(); await flush;
    }
}
