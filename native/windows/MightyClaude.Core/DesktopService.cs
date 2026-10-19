namespace MightyClaude.Core;

/// <summary>Single owner of persisted state and local pane routing.</summary>
public sealed partial class DesktopService : IAsyncDisposable
{
    private readonly object sync = new();
    private sealed class Route { internal bool Started, Cancelled; internal readonly TaskCompletionSource Ended = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    private readonly Dictionary<string, Route> routes = [];
    private readonly StateStore store;
    private readonly RunManager local;
    private readonly string pluginDirectory;
    private Task saved = Task.CompletedTask;
    private AppSnapshot snapshot = new();
    private bool closing;
    /// <summary>Starts the save a run's events asked for, once <see cref="SaveSchedule"/> says so.</summary>
    private readonly Timer saveTimer;
    /// <summary>When the latest save began, on the monotonic <see cref="System.Diagnostics.Stopwatch"/> clock; the next one keeps <see cref="SaveSchedule"/>'s gap from it.</summary>
    private long? lastSaveStart;
    /// <summary>When the save now waiting was first asked for, on the same clock; null while none waits.</summary>
    private long? pendingSaveSince;
    /// <summary>The save timer is disposed: nothing schedules on it any more, and the final save has been queued.</summary>
    private bool saveTimerDisposed;
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
    /// <summary>One pane's status alone, for the terminal that reads its shell twice a second and needs no copy of the state.</summary>
    public string? SessionStatus(string id) { lock (sync) return snapshot.Sessions.FirstOrDefault(s => s.Id == id)?.Status; }
    /// <summary>The newest queued save: "pending", "saved", or "faulted: reason" — for a check that reads the file back.</summary>
    public string SaveStatus { get { lock (sync) return !saved.IsCompleted ? "pending" : saved.IsFaulted ? "faulted: " + saved.Exception?.GetBaseException().Message : "saved"; } }
    /// <param name="delegationEnabled">The hidden delegation switch; null reads <see cref="DelegationSwitch.IsOn()"/>.</param>
    /// <param name="worktrees">Makes delegated children's worktrees; null makes them under <see cref="ChildWorktreeMaker.DefaultRoot"/>.</param>
    public DesktopService(string directory, string? legacyDirectory, string pluginDirectory, ProviderCatalog? providers = null, Func<bool>? delegationEnabled = null, ChildWorktreeMaker? worktrees = null)
    {
        store = new(directory, legacyDirectory); Providers = providers ?? new(); Images = new(Path.Combine(directory, "image-cache")); this.pluginDirectory = pluginDirectory;
        local = new(ResolveLocal, Providers, pluginDirectory, Receive, ReceiveToolPermission, Images);
        this.delegationEnabled = delegationEnabled ?? DelegationSwitch.IsOn; this.worktrees = worktrees; local.DelegationEnabled = this.delegationEnabled;
        // A save already asked for is still written while the app closes: closing only stops new work.
        saveTimer = new(_ => { lock (sync) if (pendingSaveSince is not null) _ = QueueSave(); });
    }
    /// <summary>Requests a style is allowing by itself, by run and request: their settling is not news for the pane.</summary>
    private readonly HashSet<string> styleAllowing = [];
    /// <summary>
    /// A request the pane's style lists as its own is allowed here, once, without a card (M/AppStore.swift:1160,
    /// <see cref="StyleRunPermissions.AutoAllows"/>); every other request, and one whose allowance did not go through,
    /// reaches the pane as before.
    /// </summary>
    private void ReceiveToolPermission(ToolPermissionRequest value)
    {
        var key = value.RunId + "|" + value.Id;
        if (value.State != "pending") { lock (styleAllowing) if (styleAllowing.Remove(key)) return; ToolPermissionChanged?.Invoke(value); return; }
        RunSession? pane; string? workspace;
        lock (sync) { pane = snapshot.Sessions.FirstOrDefault(s => s.Id == value.RunId); workspace = snapshot.Workspaces.FirstOrDefault(w => w.Id == pane?.WorkspaceId)?.Path; }
        if (workspace is not null && StyleRunPermissions.AutoAllows(value, pane, store.DirectoryPath, workspace))
        {
            lock (styleAllowing) styleAllowing.Add(key);
            try { local.RespondToToolPermission(value.RunId, value.Id, true); }
            catch (InvalidOperationException) { }
            // Still marked: nothing settled it, so it waits for the user like any other.
            lock (styleAllowing) if (!styleAllowing.Remove(key)) return;
        }
        ToolPermissionChanged?.Invoke(value);
    }
    /// <summary>Whether a pane of this PC runs <paramref name="provider"/>'s CLI now: another computer's runs, in a remote workspace, never hold this PC's CLI.</summary>
    public bool HasActiveProvider(string provider)
    {
        lock (sync) return snapshot.Sessions.Any(s => s.Kind == "claude" && s.Provider == provider && !snapshot.Workspaces.Any(w => w.Id == s.WorkspaceId && w.Remote is not null) && (s.Status == "running" || routes.ContainsKey(s.Id)));
    }
    /// <summary>이번만 허용 / 거부 — the only way a request is ever answered, one request at a time.</summary>
    public void RespondToToolPermission(string sessionId, string requestId, bool allow)
    {
        if (RemotePane(sessionId)) { AnswerRemote(sessionId, client => client.RespondToPermissionAsync(sessionId, requestId, allow)); return; }
        local.RespondToToolPermission(sessionId, requestId, allow);
    }
    public void ConfigureAgentIO(Func<StartRunRequest, Workspace, AgentIOBinding?>? factory) => local.AgentIOBindingFactory = factory;
    public bool IsSessionRunning(string id) => local.IsRunning(id) || Remote?.IsRunning(id) == true;
    public async Task<bool> TrySteerAsync(string sessionId, string text)
    {
        if (!(RemotePane(sessionId) ? Remote is { } client && await client.SteerAsync(sessionId, text) : await local.TrySteerAsync(sessionId, text))) return false;
        Receive(RunEvent.Log(sessionId, "user", text, "claude")); return true;
    }
    public void AnswerQuestionnaire(string sessionId, string requestId, IReadOnlyDictionary<string, UserQuestionAnswer> answers)
    {
        if (RemotePane(sessionId)) { AnswerRemote(sessionId, client => client.AnswerQuestionsAsync(sessionId, requestId, answers)); return; }
        local.AnswerQuestionnaire(sessionId, requestId, answers);
    }
    /// <summary>
    /// Answers one pending ExitPlanMode request (approve auto-edit / confirm-each, revise, cancel). Null for a remote pane: its answer goes
    /// to the host, and the record comes back as the host's event.
    /// </summary>
    public PlanRecord? AnswerPlan(string sessionId, string requestId, PlanDecision decision)
    {
        if (RemotePane(sessionId)) { AnswerRemote(sessionId, client => client.AnswerPlanAsync(sessionId, requestId, decision)); return null; }
        return local.AnswerPlan(sessionId, requestId, decision);
    }
    public async Task InitializeAsync()
    {
        StartDelegation();
        var loaded = await store.LoadAsync(); lock (sync) snapshot = loaded;
        // Children whose runs quitting killed come back interrupted, before any pane runs again (M/AppStore.swift:373-374).
        // Whatever goes wrong there, the app starts all the same.
        if (delegation is { } coordinator)
            try { await coordinator.InterruptRunsKilledByQuit(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
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
        // Refused, changing nothing, while delegation has open children or child worktrees not cleaned up there (M/AppStore.swift:627-642).
        if (WorkspaceRemovalRefusal(id) is { } reason) throw new InvalidOperationException(Locale.Get("delegation.workspace.hasChildren", new Dictionary<string, string> { ["code"] = DelegationNames.Of(reason) }));
        foreach (var pane in Snapshot.Sessions.Where(s => s.WorkspaceId == id)) await StopAsync(pane.Id);
        await UpdateAsync(s => s with { Workspaces = s.Workspaces.Where(w => w.Id != id).ToList(), Sessions = s.Sessions.Where(p => p.WorkspaceId != id).ToList(), ActiveWorkspaceId = s.ActiveWorkspaceId == id ? s.Workspaces.FirstOrDefault(w => w.Id != id)?.Id : s.ActiveWorkspaceId });
    }
    /// <summary>
    /// A shell pane's status from its own terminal (M/AppStore+Terminals.swift:25-27): idle at the prompt, running while a
    /// command runs, completed once the shell has exited, error when it could not start. It is not a run event: no run
    /// starts or ends with it. Only an exit or a failure is saved: a restart reads running as idle (M/MightyCore/StateStore.swift:201).
    /// False when nothing changed (no such shell pane, or the status it already has).
    /// </summary>
    public bool SetShellStatus(string id, string status)
    {
        lock (sync)
        {
            if (closing || status is not ("idle" or "running" or "completed" or "error") || !snapshot.Sessions.Any(s => s.Id == id && s.Kind == "shell" && s.Status != status)) return false;
            snapshot = snapshot with { Sessions = snapshot.Sessions.Select(s => s.Id == id ? s with { Status = status } : s).ToList() };
            if (status is "completed" or "error") _ = QueueSave();
            return true;
        }
    }
    public Task RenameWorkspaceAsync(string id, string name) => UpdateAsync(s => RenameSupport.RenameWorkspace(s, id, name));
    public Task RenameSessionAsync(string id, string name) => UpdateAsync(s => RenameSupport.RenameSession(s, id, name));
    public Task SetSessionAutoTitleAsync(string id) => UpdateAsync(s => PaneTitle.SetAutomatic(s, id));
    public Task UpdateAsync(Func<AppSnapshot, AppSnapshot> update)
    {
        List<RunSession> closed; Task save;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            var next = StateStore.Normalize(update(snapshot), false); var open = next.Sessions.Select(s => s.Id).ToHashSet();
            closed = snapshot.Sessions.Where(s => !open.Contains(s.Id)).ToList(); snapshot = next; save = QueueSave();
        }
        foreach (var pane in closed) PaneClosed(pane);
        return save;
    }
    /// <summary>
    /// Under <see cref="sync"/>: a save of the state as it is now, after every save queued before it. It covers any save a run's
    /// events left waiting (<see cref="ScheduleSave"/>).
    /// </summary>
    private Task QueueSave()
    {
        pendingSaveSince = null; lastSaveStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var value = Wire.Clone(snapshot);
        return saved = saved.ContinueWith(async _ => { try { await store.SaveAsync(value); } catch (Exception ex) { PersistenceFailed?.Invoke(ex); throw; } }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }
    private void Receive(RunEvent value)
    {
        if (!value.Valid()) return;
        var ended = false; var remembers = false;
        lock (sync)
        {
            snapshot = snapshot.Apply(value);
            // A remote pane's conversation id is its host's record: this PC's known sessions never list it, and the client's poll thread
            // never writes that file.
            if (value.Type == "resume") remembers = !(snapshot.Sessions.FirstOrDefault(s => s.Id == value.SessionId) is { } pane && snapshot.Workspaces.Any(w => w.Id == pane.WorkspaceId && w.Remote is not null));
            if (value.Type == "status" && value.Status is "stopped" or "completed" or "error") { if (routes.Remove(value.SessionId, out var route)) route.Ended.TrySetResult(); ended = DelegationRunEnded(value.SessionId, value.Status!); }
            // A streaming run sends many events a second: each asks for a save, which starts at most every 1.5 s (M/AppStore.swift
            // scheduleSave). A run's end is saved at once.
            if (value.Type == "status" && value.Status is "stopped" or "completed" or "error") _ = QueueSave(); else ScheduleSave();
        }
        if (remembers && value.ResumeId is { } resumed) RememberSession(resumed);
        RunEventReceived?.Invoke(value);
        // A run that is up takes what its start could not, and a pane's end offers again what it could not take while ending (M/AppStore.swift:1023, M/AppStore+Delegation.swift:82-92).
        if (ended || value.Type == "status" && value.Status == "running") OfferDelegationPending();
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
    public Task StartAsync(StartRunRequest value) => Launch(value, null, false);
    /// <summary>
    /// Starts a run. <paramref name="titleFrom"/> is what an automatic title follows when it is not the input: a run delegation
    /// starts on its own sets none (""). With <paramref name="reach"/> it returns only once the run's process is up with its input open (<see cref="RunManager.StartReachedAsync"/>), or
    /// throws why it never got it. The run's id as delegation names it; null when it was stopped before it started (M/AppStore.swift:938-1052).
    /// </summary>
    private async Task<string?> Launch(StartRunRequest value, string? titleFrom, bool reach)
    {
        var request = value.Validate(); Route route; string runId; Workspace workspace; var remote = false;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            if (!snapshot.Workspaces.Any(w => w.Id == request.WorkspaceId)) throw new ArgumentException(Locale.Get("run.error.workspaceNotRegistered"));
            workspace = snapshot.Workspaces.First(w => w.Id == request.WorkspaceId); remote = workspace.Remote is not null;
            if (snapshot.Sessions.FirstOrDefault(s => s.Id == request.SessionId && s.WorkspaceId == request.WorkspaceId && s.Kind == request.Kind) is not { } pane) throw new ArgumentException(Locale.Get("run.error.sessionNotInWorkspace"));
            // A delegated child runs only in its own worktree, never in its parent's checkout (M/AppStore.swift:490-491).
            if (DelegationPanes.RunRefusal(pane) is not null) throw new InvalidOperationException(Locale.Get("run.blocked.worktreeMissing"));
            // A resumed conversation continues its checklist (task tools keep their ids).
            request = request with { PhaseModels = PhaseModelPreferences.Normalize(snapshot.PhaseModels), TodoProgress = request.ResumeId is null ? null : pane.TodoProgress, WorkingFolder = pane.WorkingFolder, DelegationWorktreeRoot = DelegationWorktreeRoot(pane) };
            route = new();
            if (!routes.TryAdd(request.SessionId, route)) throw new InvalidOperationException(Locale.Get("run.error.alreadyRunning"));
            runId = DelegationRunBegan(pane);
            snapshot = snapshot with { Sessions = snapshot.Sessions.Select(s => s.Id == request.SessionId ? PaneTitle.Requested(s, titleFrom ?? request.Input) with { SessionUsage = request.ResumeId is null || s.Provider != request.Provider ? null : s.SessionUsage, Provider = request.Provider, CurrentActivity = null, RunTiming = request.Kind == "shell" ? null : AgentRunTiming.Begin() } : s).ToList() };
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
                if (closing || route.Cancelled) { Receive(RunEvent.State(request.SessionId, "stopped")); if (request.Attachments is { Count: > 0 }) throw new OperationCanceledException(Locale.Get("run.error.attachmentSendCancelled")); return null; }
                route.Started = true; started = remote ? Task.CompletedTask : reach ? local.StartReachedAsync(request) : local.StartAsync(request);
            }
            if (remote)
            {
                // A remote workspace's run goes to its host through the client, never to this PC's runner, and never under this lock: the
                // client checks the route, may lose the connection and then waits on each run's lock, which a poll holds while it reaches Receive.
                var reached = Task.Run(() => StartRemoteAsync(request, workspace, route));
                if (!await reached)
                {
                    // Stopped before the host took it; the client's stop may already have ended it, and a newer send may have begun.
                    SayIfOwned(request.SessionId, route, RunEvent.State(request.SessionId, "stopped"));
                    return null;
                }
                return runId;
            }
            await started;
            return runId;
        }
        // A remote run's last word is said only while the route is still its own: a stop already ended it, and a newer send's route is never ended here.
        catch (OperationCanceledException) { if (remote) SayIfOwned(request.SessionId, route, RunEvent.State(request.SessionId, "stopped")); else Receive(RunEvent.State(request.SessionId, "stopped")); throw; }
        catch (Exception ex)
        {
            if (remote) SayIfOwned(request.SessionId, route, RunEvent.Log(request.SessionId, "error", ex.Message), RunEvent.State(request.SessionId, "error"));
            else { Receive(RunEvent.Log(request.SessionId, "error", ex.Message)); Receive(RunEvent.State(request.SessionId, "error")); }
            throw;
        }
    }
    /// <summary>Applies <paramref name="events"/> only while <paramref name="route"/> is still the pane's, checked and applied under one lock.</summary>
    private void SayIfOwned(string sessionId, Route route, params RunEvent[] events)
    {
        lock (sync)
        {
            if (!routes.TryGetValue(sessionId, out var current) || !ReferenceEquals(current, route)) return;
            foreach (var value in events) Receive(value);
        }
    }
    public Task StopAsync(string id)
    {
        Route route; lock (sync) { if (!routes.TryGetValue(id, out route!)) return Task.CompletedTask; route.Cancelled = true; if (!route.Started) return Task.CompletedTask; }
        return RemotePane(id) && Remote is { } client ? client.StopAsync(id) : local.StopAsync(id);
    }
    /// <summary>
    /// Under <see cref="sync"/>: asks for a save of the state, which starts after <see cref="SaveSchedule.Delay"/>. A save already
    /// waiting stays as it is once <see cref="SaveSchedule.KeepsPending"/> says so, so a steady stream of changes never pushes it
    /// back for ever; it writes the state as it is when it starts.
    /// </summary>
    private void ScheduleSave()
    {
        // Closing, the final save covers everything; it may already have disposed the timer.
        if (closing || saveTimerDisposed) return;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        double? since = lastSaveStart is { } last ? System.Diagnostics.Stopwatch.GetElapsedTime(last, now).TotalSeconds : null;
        if (pendingSaveSince is { } waiting && SaveSchedule.KeepsPending(since, System.Diagnostics.Stopwatch.GetElapsedTime(waiting, now).TotalSeconds)) return;
        pendingSaveSince ??= now;
        saveTimer.Change(TimeSpan.FromSeconds(SaveSchedule.Delay(since)), Timeout.InfiniteTimeSpan);
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync) { if (closing) return; closing = true; }
        // The client's runs stop first, and whatever that throws never keeps this PC's runs and delegation from shutting down.
        if (Remote is { } client)
            try { await client.DisposeAsync(); } catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.TraceWarning("remote client shutdown: " + ex.GetType().Name); }
        try { await Providers.DisposeAsync(); await local.DisposeAsync(); await StopDelegation(); }
        finally
        {
            // The final save runs whatever the shutdown above threw.
            Task flush; lock (sync) { saveTimerDisposed = true; flush = QueueSave(); }
            try { await flush; }
            finally { await saveTimer.DisposeAsync(); }
        }
    }
}
