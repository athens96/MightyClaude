namespace MightyCode.Core;

/// <summary>How one connection's mirror goes: <c>live</c> (reading the host's list), <c>reconnecting</c> (asking again after a failed read) or <c>off</c>.</summary>
/// <param name="Unsupported">The host answered the mirror's list 404 (an older Mighty Code): its shared workspaces still open as this PC's own remote panes (Settings' 열기).</param>
public sealed record RemoteMirrorLink(string Phase, string? Error = null, bool Unsupported = false);

/// <summary>What a mirrored pane shows beyond its RunSession: the host's waiting work and branch from its list, the queued texts and base from its detail.</summary>
public sealed record RemoteMirrorCounts(int Pending = 0, int Queued = 0, IReadOnlyList<string>? QueuedTexts = null, string? Branch = null, string? Base = null);

/// <summary>
/// The remote mirror's client side in the desktop service (M/MightyCode/AppStore+RemoteMirror.swift): each connected computer's shared
/// workspaces and agent panes, held in the state while connected and never saved (StateStore.SaveAsync leaves them out), kept current by
/// one poll per connection, and driven through the host. Everything here takes <c>sync</c> briefly and calls the client outside it.
/// </summary>
public sealed partial class DesktopService
{
    private sealed class MirrorPoll
    {
        public RemoteMirrorLink Link = new("live");
        public string? Revision;
        public CancellationTokenSource? Cancel;
        public Task? Loop;
        public int Wake;
    }

    /// <summary>Under <c>sync</c>: each connection's poll, and what it last applied of each mirrored pane.</summary>
    private readonly Dictionary<string, MirrorPoll> mirrorPolls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> mirrorSummaryRevisions = new(StringComparer.Ordinal), mirrorDetailRevisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoteMirrorCounts> mirrorCounts = new(StringComparer.Ordinal);
    /// <summary>A mirrored pane's waiting requests as its cards show them (run id the pane's), each with the host's run id to answer with.</summary>
    private readonly Dictionary<string, List<(ToolPermissionRequest Request, string HostRunId)>> mirrorRequests = new(StringComparer.Ordinal);
    /// <summary>The status each connection had when last seen: a connection that comes up starts its mirror, one that goes down stops it.</summary>
    private readonly Dictionary<string, RemoteConnectionStatus> mirrorSeen = new(StringComparer.Ordinal);

    /// <summary>A mirror's state, a mirrored pane's counts or its workspaces changed: the window draws the sidebar and the panes' strips again. Raised off <c>sync</c>, on any thread.</summary>
    public event Action? MirrorChanged;
    /// <summary>An action on a mirrored pane failed on the host; the words are the host's (or the connection's). Raised on any thread.</summary>
    public event Action<string, string>? MirrorFailed;

    /// <summary>How a poll waits between reads (checks shorten it); the Mac's 1 s and 5 s.</summary>
    internal TimeSpan MirrorLiveInterval { get; set; } = RemoteMirrorProtocol.LivePollInterval;
    internal TimeSpan MirrorIdleInterval { get; set; } = RemoteMirrorProtocol.IdlePollInterval;

    public RemoteMirrorLink? MirrorLink(string connectionId) { lock (sync) return mirrorPolls.TryGetValue(connectionId, out var poll) ? poll.Link : null; }
    public RemoteMirrorCounts? MirrorCounts(string paneId) { lock (sync) return mirrorCounts.GetValueOrDefault(paneId); }
    public RemoteMirrorReference? MirrorReference(string paneId) { lock (sync) return snapshot.Sessions.FirstOrDefault(s => s.Id == paneId)?.Mirror; }
    public bool IsMirrorWorkspace(string? workspaceId) { lock (sync) return snapshot.Workspaces.Any(w => w.Id == workspaceId && w.Remote?.Mirror == true); }

    /// <summary>Why a mirrored pane cannot send now: its computer is not connected; null when it can.</summary>
    public string? MirrorBlockedReason(RemoteMirrorReference mirror) =>
        Remote?.Connection(mirror.ConnectionId)?.Status == RemoteConnectionStatus.Connected ? null
            : Locale.Get("remote.mirror.error.disconnected", new Dictionary<string, string> { ["host"] = mirror.HostName });

    /// <summary>The client's connections changed: one that came up starts its mirror, one that went down stops it, one removed takes its mirror away.</summary>
    private void RemoteConnectionsChanged()
    {
        if (Remote is not { } client) return;
        var list = client.List();
        var start = new List<string>(); var stop = new List<string>(); var removed = new List<string>();
        lock (sync)
        {
            if (closing) return;
            foreach (var connection in list)
            {
                var was = mirrorSeen.GetValueOrDefault(connection.Id, RemoteConnectionStatus.Saved);
                mirrorSeen[connection.Id] = connection.Status;
                // Connected now: a new connection, a refresh after the mirror ended (not an older host's 404), or a key replaced meanwhile.
                if (connection.Status == RemoteConnectionStatus.Connected && (was != RemoteConnectionStatus.Connected || !mirrorPolls.TryGetValue(connection.Id, out var poll)
                    || poll.Loop is { IsCompleted: true } && !poll.Link.Unsupported)) start.Add(connection.Id);
                else if (connection.Status != RemoteConnectionStatus.Connected && was == RemoteConnectionStatus.Connected) stop.Add(connection.Id);
            }
            foreach (var id in mirrorSeen.Keys.Where(id => list.All(c => c.Id != id)).ToList()) { mirrorSeen.Remove(id); removed.Add(id); }
        }
        foreach (var id in start) StartMirror(id);
        foreach (var id in stop) StopMirror(id);
        foreach (var id in removed) RemoveMirrors(id);
    }

    /// <summary>Starts (or wakes) the connection's mirror: its workspaces and panes come into the sidebar and stay current.</summary>
    public void StartMirror(string connectionId)
    {
        lock (sync)
        {
            if (closing || Remote is null) return;
            if (mirrorPolls.TryGetValue(connectionId, out var running) && running.Loop is { IsCompleted: false }) { running.Wake = 1; return; }
            var poll = new MirrorPoll { Cancel = new CancellationTokenSource() };
            mirrorPolls[connectionId] = poll;
            var token = poll.Cancel.Token;
            poll.Loop = Task.Run(() => MirrorLoopAsync(connectionId, poll, token), CancellationToken.None);
        }
        MirrorChanged?.Invoke();
    }

    /// <summary>Stops the connection's mirror (it was disconnected): its panes stay as last shown, and sends wait for a new connect.</summary>
    public void StopMirror(string connectionId)
    {
        lock (sync)
        {
            if (!mirrorPolls.TryGetValue(connectionId, out var poll)) return;
            poll.Cancel?.Cancel();
            poll.Link = poll.Link.Phase == "off" ? poll.Link : new("off");
        }
        MirrorChanged?.Invoke();
    }

    /// <summary>The connection's mirrored workspaces and panes go (the connection was removed).</summary>
    public void RemoveMirrors(string connectionId)
    {
        List<string> gone;
        lock (sync)
        {
            if (mirrorPolls.Remove(connectionId, out var poll)) poll.Cancel?.Cancel();
            var workspaces = snapshot.Workspaces.Where(w => w.Remote is { Mirror: true } r && r.ConnectionId == connectionId).Select(w => w.Id).ToHashSet();
            gone = snapshot.Sessions.Where(s => s.Mirror?.ConnectionId == connectionId || workspaces.Contains(s.WorkspaceId)).Select(s => s.Id).ToList();
            if (workspaces.Count > 0 || gone.Count > 0)
            {
                var next = snapshot with { Workspaces = snapshot.Workspaces.Where(w => !workspaces.Contains(w.Id)).ToList(), Sessions = snapshot.Sessions.Where(s => !gone.Contains(s.Id)).ToList() };
                if (next.ActiveWorkspaceId is { } active && workspaces.Contains(active)) next = next with { ActiveWorkspaceId = next.Workspaces.FirstOrDefault()?.Id, ActiveSessionId = null };
                snapshot = StateStore.Normalize(next, false);
            }
        }
        ForgetMirrorPanes(gone);
        MirrorChanged?.Invoke();
    }

    public void WakeMirror(string connectionId) { lock (sync) if (mirrorPolls.TryGetValue(connectionId, out var poll)) poll.Wake = 1; }

    /// <summary>The user looks at another workspace or pane: a mirror workspace's poll reads at once.</summary>
    private void WakeMirrorFor(string? workspaceId)
    {
        lock (sync)
            if (snapshot.Workspaces.FirstOrDefault(w => w.Id == workspaceId)?.Remote is { Mirror: true } remote && mirrorPolls.TryGetValue(remote.ConnectionId, out var poll)) poll.Wake = 1;
    }

    private async Task MirrorLoopAsync(string id, MirrorPoll poll, CancellationToken token)
    {
        DateTimeOffset? failingSince = null;
        while (!token.IsCancellationRequested)
        {
            var client = Remote;
            if (client is null) break;
            try
            {
                string? since; lock (sync) since = poll.Revision;
                var state = await client.MirrorStateAsync(id, since, token);
                token.ThrowIfCancellationRequested();
                failingSince = null;
                if (!state.Unchanged) ApplyMirrorState(id, state);
                bool moved;
                lock (sync) { moved = poll.Link.Phase != "live" || poll.Link.Error is not null; poll.Link = new("live"); poll.Revision = state.Revision; }
                if (moved || !state.Unchanged) MirrorChanged?.Invoke();
                await FetchMirrorDetailsAsync(id, poll, client, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A host that answers but refuses the mirror (an older Mighty Code answers 404) stays connected: its workspaces still take
                // this PC's own remote panes, only the mirror stops.
                if (RemoteClientService.RefusalStatus(ex) is { } status && status != 401)
                {
                    var host = client.Connection(id)?.Name ?? "";
                    lock (sync) { poll.Link = new("off", status == 404 ? Locale.Get("remote.mirror.error.olderHost", new Dictionary<string, string> { ["host"] = host }) : ex.Message, status == 404); poll.Revision = null; }
                    MirrorChanged?.Invoke();
                    break;
                }
                // A busy host or a stumbling network is asked again for ten seconds, as a remote run's poll is.
                var now = DateTimeOffset.UtcNow;
                if (RemoteTransport.Transient(ex) && now - (failingSince ?? now) < RemoteProtocol.PollRetryWindow)
                {
                    failingSince ??= now;
                    lock (sync) poll.Link = poll.Link with { Phase = "reconnecting" };
                    MirrorChanged?.Invoke();
                    try { await Task.Delay(TimeSpan.FromSeconds(1), token); } catch (OperationCanceledException) { break; }
                    continue;
                }
                // Past its retries, or an answer that is not the mirror's: the connection is lost, the mirror stops. A 401 the client already
                // took as disconnected (for the connection the request used), and a connection that went or changed meanwhile stands as it is.
                if (RemoteTransport.Transient(ex) || ex is InvalidDataException)
                    try { await client.MirrorLostAsync(id, ex.Message); } catch (Exception lost) when (lost is not OutOfMemoryException) { }
                lock (sync) { poll.Link = new("off", ex.Message); poll.Revision = null; }
                MirrorChanged?.Invoke();
                break;
            }
            await MirrorPauseAsync(id, poll, token);
        }
    }

    /// <summary>One second while a pane of this connection runs or waits, or the user looks at one of its workspaces; five otherwise. A selection, a send or an answer wakes it at once.</summary>
    private async Task MirrorPauseAsync(string id, MirrorPoll poll, CancellationToken token)
    {
        bool live;
        lock (sync)
        {
            var watching = snapshot.Workspaces.FirstOrDefault(w => w.Id == snapshot.ActiveWorkspaceId)?.Remote is { Mirror: true } remote && remote.ConnectionId == id;
            live = watching || snapshot.Sessions.Any(s => s.Mirror?.ConnectionId == id && (s.Status == "running" || (mirrorCounts.GetValueOrDefault(s.Id)?.Pending ?? 0) > 0));
        }
        var until = DateTimeOffset.UtcNow + (live ? MirrorLiveInterval : MirrorIdleInterval);
        while (DateTimeOffset.UtcNow < until && !token.IsCancellationRequested)
        {
            if (Interlocked.Exchange(ref poll.Wake, 0) == 1) return;
            try { await Task.Delay(50, token); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>The host's list laid over this PC's: a workspace per shared one, a pane per host pane, the ones gone taken away. Never saved.</summary>
    internal void ApplyMirrorState(string id, RemoteMirrorState state)
    {
        List<string> gone;
        lock (sync)
        {
            if (closing) return;
            var workspaceIds = state.Workspaces.ToDictionary(w => w.Id, w => RemoteMirrorSupport.ClientId(id, w.Id), StringComparer.Ordinal);
            var kept = workspaceIds.Values.ToHashSet(StringComparer.Ordinal);
            var dropped = snapshot.Workspaces.Where(w => w.Remote is { Mirror: true } r && r.ConnectionId == id && !kept.Contains(w.Id)).Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
            var workspaces = snapshot.Workspaces.Where(w => !dropped.Contains(w.Id)).ToList();
            foreach (var shared in state.Workspaces)
            {
                var clientId = workspaceIds[shared.Id];
                var reference = new RemoteWorkspaceReference(id, shared.Id, state.HostName) { Mirror = true };
                var at = workspaces.FindIndex(w => w.Id == clientId);
                if (at >= 0) { var updated = workspaces[at] with { Name = shared.Name, Path = shared.Path, Remote = reference }; if (updated != workspaces[at]) workspaces[at] = updated; }
                else workspaces.Add(new Workspace { Id = clientId, Name = shared.Name, Path = shared.Path, Remote = reference });
            }
            var sessions = snapshot.Sessions.ToList(); var keptSessions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var summary in state.Sessions)
            {
                if (!workspaceIds.TryGetValue(summary.WorkspaceId, out var workspaceId)) continue;
                var clientId = RemoteMirrorSupport.ClientId(id, summary.Id);
                keptSessions.Add(clientId);
                var at = sessions.FindIndex(s => s.Id == clientId);
                var pane = RemoteMirrorSupport.Pane(summary, at >= 0 ? sessions[at] : null, clientId, workspaceId, new RemoteMirrorReference(id, summary.Id, summary.WorkspaceId, state.HostName));
                if (at >= 0) { if (sessions[at] != pane) sessions[at] = pane; } else sessions.Add(pane);
                mirrorSummaryRevisions[clientId] = summary.Revision;
                var counts = mirrorCounts.GetValueOrDefault(clientId) ?? new();
                mirrorCounts[clientId] = counts with { Pending = summary.Pending, Queued = summary.Queued, Branch = summary.Branch, QueuedTexts = summary.Queued == 0 ? [] : counts.QueuedTexts };
            }
            gone = sessions.Where(s => s.Mirror?.ConnectionId == id && !keptSessions.Contains(s.Id) || dropped.Contains(s.WorkspaceId)).Select(s => s.Id).ToList();
            sessions.RemoveAll(s => gone.Contains(s.Id));
            var next = snapshot with { Workspaces = workspaces, Sessions = sessions };
            // A workspace the host stopped sharing, or a pane closed there, stops being the one shown.
            if (next.ActiveWorkspaceId is { } active && dropped.Contains(active))
            {
                var shown = workspaces.FirstOrDefault(w => w.Remote is null)?.Id ?? workspaces.FirstOrDefault()?.Id;
                next = next with { ActiveWorkspaceId = shown, ActiveSessionId = sessions.FirstOrDefault(s => s.WorkspaceId == shown)?.Id };
            }
            else if (next.ActiveSessionId is { } pane && gone.Contains(pane)) next = next with { ActiveSessionId = sessions.FirstOrDefault(s => s.WorkspaceId == next.ActiveWorkspaceId)?.Id };
            snapshot = StateStore.Normalize(next, false);
        }
        ForgetMirrorPanes(gone);
    }

    /// <summary>The details this PC needs now: the panes of the workspace the user looks at and any pane waiting for an answer, each only when its revision moved since it was read, at most 16 a pass.</summary>
    private async Task FetchMirrorDetailsAsync(string id, MirrorPoll poll, RemoteClientService client, CancellationToken token)
    {
        List<RunSession> wanted;
        lock (sync)
        {
            var watching = snapshot.ActiveWorkspaceId;
            wanted = snapshot.Sessions.Where(s => s.Mirror?.ConnectionId == id && (s.WorkspaceId == watching || (mirrorCounts.GetValueOrDefault(s.Id)?.Pending ?? 0) > 0)
                && mirrorSummaryRevisions.GetValueOrDefault(s.Id) != mirrorDetailRevisions.GetValueOrDefault(s.Id)).Take(RemoteMirrorProtocol.MaximumDetailsPerPass).ToList();
        }
        foreach (var session in wanted)
        {
            token.ThrowIfCancellationRequested();
            RemoteMirrorPane pane;
            try { pane = await client.MirrorDetailAsync(id, session.Mirror!.HostSessionId, token); }
            catch (Exception ex) when (RemoteClientService.RefusalStatus(ex) is { } status && status != 401)
            {
                // Closed on the host between its list and this read: the list is read whole next time, which takes it away.
                lock (sync) { if (mirrorSummaryRevisions.TryGetValue(session.Id, out var listed)) mirrorDetailRevisions[session.Id] = listed; poll.Revision = null; }
                continue;
            }
            ApplyMirrorDetail(session.Id, pane);
        }
    }

    /// <summary>A host pane's detail laid over its pane here, its waiting requests as the pane's cards.</summary>
    internal void ApplyMirrorDetail(string paneId, RemoteMirrorPane pane)
    {
        var incoming = pane.Session.Permissions.Where(p => p.State == "pending").ToList();
        List<ToolPermissionRequest> withdrawn, added;
        lock (sync)
        {
            if (closing) return;
            var at = snapshot.Sessions.FindIndex(s => s.Id == paneId && s.Mirror is not null);
            if (at < 0) return;
            var sessions = snapshot.Sessions.ToList();
            sessions[at] = RemoteMirrorSupport.Apply(pane.Session, sessions[at]);
            snapshot = StateStore.Normalize(snapshot with { Sessions = sessions }, false);
            var held = mirrorRequests.GetValueOrDefault(paneId) ?? [];
            added = incoming.Where(p => held.All(h => h.Request.Id != p.Id)).Select(p => p with { RunId = paneId }).ToList();
            withdrawn = held.Where(h => incoming.All(p => p.Id != h.Request.Id)).Select(h => h.Request with { State = "cancelled" }).ToList();
            if (incoming.Count == 0) mirrorRequests.Remove(paneId);
            else mirrorRequests[paneId] = incoming.Select(p => (p with { RunId = paneId }, p.RunId)).ToList();
            var counts = mirrorCounts.GetValueOrDefault(paneId) ?? new();
            mirrorCounts[paneId] = counts with { Queued = pane.Session.Queued.Count, QueuedTexts = pane.Session.Queued.Select(q => q.Text).ToList(), Branch = pane.Session.Branch, Base = pane.Session.Base };
            mirrorDetailRevisions[paneId] = pane.Revision;
        }
        foreach (var request in withdrawn) ToolPermissionChanged?.Invoke(request);
        foreach (var request in added) ToolPermissionChanged?.Invoke(request);
        MirrorChanged?.Invoke();
    }

    /// <summary>What this PC held of panes that left: their revisions, counts, and cards (withdrawn).</summary>
    private void ForgetMirrorPanes(IReadOnlyCollection<string> panes)
    {
        if (panes.Count == 0) return;
        var withdrawn = new List<ToolPermissionRequest>();
        lock (sync)
            foreach (var id in panes)
            {
                mirrorSummaryRevisions.Remove(id); mirrorDetailRevisions.Remove(id); mirrorCounts.Remove(id);
                if (mirrorRequests.Remove(id, out var held)) withdrawn.AddRange(held.Select(h => h.Request with { State = "cancelled" }));
            }
        foreach (var request in withdrawn) ToolPermissionChanged?.Invoke(request);
    }

    /// <summary>The host's run id of a mirrored pane's waiting request; null when the pane holds no such request.</summary>
    private string? MirrorHostRun(string paneId, string requestId) { lock (sync) return mirrorRequests.GetValueOrDefault(paneId)?.FirstOrDefault(h => h.Request.Id == requestId).HostRunId; }

    /// <summary>An answer to a mirrored pane's request goes to its host without holding the caller; a failure is said (<see cref="MirrorFailed"/>).</summary>
    private void AnswerMirror(string paneId, RemoteMirrorReference mirror, string requestId, Func<RemoteClientService, string, Task> answer)
    {
        if (Remote is not { } client) return;
        var hostRun = MirrorHostRun(paneId, requestId);
        _ = Task.Run(async () =>
        {
            try
            {
                if (hostRun is null) throw new InvalidOperationException(Locale.Get("remote.error.notPendingPermission"));
                await answer(client, hostRun);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { MirrorFailed?.Invoke(paneId, ex.Message); }
            WakeMirror(mirror.ConnectionId);
        });
    }

    /// <summary>
    /// The composer's text and files as the host pane's next request (the host's word for what it did). A pane whose computer is not
    /// connected is refused before anything goes, and a refusal throws with the host's words, so the caller puts the draft back.
    /// </summary>
    public async Task<string> MirrorSubmitAsync(string paneId, string text, IReadOnlyList<RunAttachment> attachments)
    {
        var mirror = MirrorReference(paneId) ?? throw new InvalidOperationException(Locale.Get("remote.error.paneNotFound"));
        if (MirrorBlockedReason(mirror) is { } reason) throw new InvalidOperationException(reason);
        if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0) throw new ArgumentException(Locale.Get("validation.run.badInput"));
        var client = Remote ?? throw new InvalidOperationException(Locale.Get("remote.run.error.notConnected"));
        var outcome = await client.MirrorSubmitAsync(mirror.ConnectionId, mirror.HostSessionId, text, attachments);
        WakeMirror(mirror.ConnectionId);
        return outcome;
    }

    private async Task MirrorStopAsync(RemoteMirrorReference mirror)
    {
        var client = Remote ?? throw new InvalidOperationException(Locale.Get("remote.run.error.notConnected"));
        try { await client.MirrorStopAsync(mirror.ConnectionId, mirror.HostSessionId); }
        finally { WakeMirror(mirror.ConnectionId); }
    }

    /// <summary>A new agent pane on that computer, in the shared workspace; its id here once the next list shows it (null when it did not within five seconds).</summary>
    public async Task<string?> CreateMirrorPaneAsync(string workspaceId, string kind, string provider)
    {
        RemoteWorkspaceReference remote;
        lock (sync) remote = snapshot.Workspaces.FirstOrDefault(w => w.Id == workspaceId)?.Remote is { Mirror: true } found ? found : throw new InvalidOperationException(Locale.Get("remote.workspace.error.notShared"));
        if (kind != "claude") throw new InvalidOperationException(Locale.Get("remote.mirror.error.commandPane"));
        var client = Remote ?? throw new InvalidOperationException(Locale.Get("remote.run.error.notConnected"));
        var hostId = await client.MirrorCreateSessionAsync(remote.ConnectionId, remote.WorkspaceId, provider);
        lock (sync) if (mirrorPolls.TryGetValue(remote.ConnectionId, out var poll)) { poll.Revision = null; poll.Wake = 1; }
        var clientId = RemoteMirrorSupport.ClientId(remote.ConnectionId, hostId);
        for (var i = 0; i < 50; i++)
        {
            lock (sync) if (snapshot.Sessions.Any(s => s.Id == clientId)) return clientId;
            await Task.Delay(100);
        }
        return null;
    }

    /// <summary>Closes the pane on that computer, as the phone's close does; it leaves here at once.</summary>
    public async Task CloseMirrorPaneAsync(string paneId)
    {
        var mirror = MirrorReference(paneId) ?? throw new InvalidOperationException(Locale.Get("remote.error.paneNotFound"));
        var client = Remote ?? throw new InvalidOperationException(Locale.Get("remote.run.error.notConnected"));
        await client.MirrorCloseAsync(mirror.ConnectionId, mirror.HostSessionId);
        lock (sync)
        {
            var next = snapshot with { Sessions = snapshot.Sessions.Where(s => s.Id != paneId).ToList() };
            if (next.ActiveSessionId == paneId) next = next with { ActiveSessionId = next.Sessions.FirstOrDefault(s => s.WorkspaceId == next.ActiveWorkspaceId)?.Id };
            snapshot = StateStore.Normalize(next, false);
        }
        ForgetMirrorPanes([paneId]);
        WakeMirror(mirror.ConnectionId);
        MirrorChanged?.Invoke();
    }

    /// <summary>The connection's mirror starts after a connect the user asked for (Settings' 보기 and 연결, the sidebar server's 연결), a host that lacks it opening as before.</summary>
    public async Task ConnectMirrorAsync(string connectionId)
    {
        var client = Remote ?? throw new InvalidOperationException(Locale.Get("remote.run.error.notConnected"));
        await client.RefreshAsync(connectionId);
        StartMirror(connectionId);
    }

    private void StopMirrorPolls()
    {
        lock (sync) foreach (var poll in mirrorPolls.Values) poll.Cancel?.Cancel();
    }
}
