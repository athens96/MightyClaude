using System.Text.Json;

namespace MightyCode.Core;

/// <summary>
/// The host side of the remote mirror on Windows (M/MightyCode/AppStore+RemoteMirror.swift RemoteMirrorBridge): this PC's own agent panes as a
/// connected client reads and drives them. Reads take the panes as they are now (the service's state, the phone's waiting requests, the
/// window's queues); every action goes through the phone's m1 handlers (<see cref="MobileDesktopRouter"/>), so what a phone can and cannot
/// do here a mirroring client can and cannot do. Everything runs through <paramref name="onWindow"/> (the window's thread in the app).
/// </summary>
/// <param name="router">The phone's router; null (not made yet, or the window closing) answers every action as unavailable.</param>
/// <param name="queued">A pane's queued requests as its composer holds them, in order; empty when it holds none.</param>
/// <param name="onWindow">Runs the work where the router and the window's panes may be touched.</param>
public sealed class RemoteMirrorDesktopHost(DesktopService service, Func<MobileDesktopRouter?> router, Func<string, IReadOnlyList<RemoteMirrorQueued>> queued,
    Func<Func<Task<object?>>, Task<object?>> onWindow) : IRemoteMirrorHost
{
    /// <summary>The device the phone router is told for the mirror's actions: never a paired phone's id, so no upload or push is ever another's.</summary>
    internal const string Device = "remote-mirror";

    private async Task<T> Window<T>(Func<Task<T>> work) => (T)(await onWindow(async () => await work()))!;
    private Task<T> Read<T>(Func<T> work) => Window(() => Task.FromResult(work()));

    /// <summary>This PC's own agent panes the mirror may show: of these workspaces, never another computer's.</summary>
    private static IEnumerable<RunSession> Mirrorable(AppSnapshot state, IReadOnlySet<string> workspaceIds) =>
        state.Sessions.Where(s => s.Kind == "claude" && s.Mirror is null && workspaceIds.Contains(s.WorkspaceId) && state.Workspaces.Any(w => w.Id == s.WorkspaceId && w.Remote is null));

    private IReadOnlyList<ToolPermissionRequest> Pending(string id) => router()?.PendingRequests(id).Where(p => p.State == "pending").ToArray() ?? [];

    public Task<IReadOnlyList<RemoteMirrorSummary>> SummariesAsync(IReadOnlySet<string> workspaceIds) => Read<IReadOnlyList<RemoteMirrorSummary>>(() =>
        Mirrorable(service.Snapshot, workspaceIds).Select(session =>
        {
            var permissions = Pending(session.Id); var waiting = queued(session.Id); var branch = session.PaneWorktree?.Branch;
            return new RemoteMirrorSummary(session.Id, session.WorkspaceId, RemoteText.Cut(session.Title, 200), session.Kind, session.Provider, RemoteText.Cut(session.Model, 200),
                RemoteMirrorProtocol.Statuses.Contains(session.Status) ? session.Status : "idle",
                RemoteMirrorSupport.Revision(session, permissions, waiting, branch, session.PaneWorktree?.BaseBranch),
                permissions.Count + (service.AntigravityCard(session.Id) is null ? 0 : 1), waiting.Count, branch);
        }).ToList());

    public async Task<RemoteMirrorPane?> DetailAsync(string sessionId, IReadOnlySet<string> workspaceIds)
    {
        // The pane is read on the window; the detail (bounded and fitted under 1.5 MiB, which writes its JSON) is built off it.
        var read = await Read<(RunSession Session, IReadOnlyList<ToolPermissionRequest> Permissions, IReadOnlyList<RemoteMirrorQueued> Queued)?>(() =>
            Mirrorable(service.Snapshot, workspaceIds).FirstOrDefault(s => s.Id == sessionId) is { } session ? (session, Pending(sessionId), queued(sessionId)) : null);
        if (read is not { } pane) return null;
        var branch = pane.Session.PaneWorktree?.Branch; var baseBranch = pane.Session.PaneWorktree?.BaseBranch;
        return await Task.Run(() => new RemoteMirrorPane(RemoteMirrorSupport.Revision(pane.Session, pane.Permissions, pane.Queued, branch, baseBranch),
            RemoteMirrorSupport.Session(pane.Session, pane.Permissions, pane.Queued, branch, baseBranch)));
    }

    public Task<RemoteMirrorPaneInfo?> PaneInfoAsync(string sessionId) => Read<RemoteMirrorPaneInfo?>(() =>
    {
        var state = service.Snapshot;
        return state.Sessions.FirstOrDefault(s => s.Id == sessionId && s.Kind == "claude" && s.Mirror is null) is { } session && state.Workspaces.Any(w => w.Id == session.WorkspaceId && w.Remote is null)
            ? new RemoteMirrorPaneInfo(session.WorkspaceId, session.Settings.PermissionMode, session.Provider) : null;
    });

    public async Task<string> SubmitAsync(string sessionId, string text, IReadOnlyList<RunAttachment> attachments, CancellationToken token)
    {
        var result = await Window(async () => await Router().SubmitInlineAsync(sessionId, text, attachments, token));
        // The phone's words for what happened; anything else the composer answered (blocked, not started) is a refusal.
        return result is SubmitReceipt { Outcome: "started" or "steered" or "queued" } receipt ? receipt.Outcome : throw new InvalidOperationException(Locale.Get("remote.error.startFailed"));
    }

    public Task StopAsync(string sessionId, CancellationToken token) => Route($"/m1/sessions/{sessionId}/stop", new { }, token);
    public Task CloseAsync(string sessionId, CancellationToken token) => Route($"/m1/sessions/{sessionId}/close", new { }, token);

    public Task PermissionAsync(string sessionId, string requestId, string runId, bool allow, bool always, CancellationToken token) =>
        Route($"/m1/sessions/{sessionId}/permission", always ? new { requestId, runId, allow, always } : new { requestId, runId, allow, always = false }, token);

    public Task AnswersAsync(string sessionId, string requestId, string runId, IReadOnlyDictionary<string, UserQuestionAnswer> answers, CancellationToken token) =>
        Route($"/m1/sessions/{sessionId}/answers", new { requestId, runId, answers }, token);

    public Task PlanAsync(string sessionId, string requestId, string runId, PlanDecision decision, CancellationToken token) =>
        Route($"/m1/sessions/{sessionId}/plan", new { requestId, runId, decision = decision.Kind, feedback = decision.Feedback }, token);

    public async Task<string> CreateSessionAsync(string workspaceId, string provider, CancellationToken token)
    {
        var answer = await Route($"/m1/workspaces/{workspaceId}/sessions", new { kind = "claude", provider }, token);
        return answer.Text("sessionId") is { } id && Wire.Identifier(id) ? id : throw new InvalidOperationException(Locale.Get("remote.error.startFailed"));
    }

    private MobileDesktopRouter Router() => router() ?? throw new InvalidOperationException(Locale.Get("remote.mirror.error.unavailable"));

    /// <summary>One m1 action as a phone's; a refusal throws with the router's words for it.</summary>
    private async Task<JsonElement> Route(string path, object body, CancellationToken token)
    {
        var reply = await Window(async () => await Router().RouteAsync("POST", path, JsonSerializer.SerializeToElement(body, Wire.Json), Device, token));
        var answer = JsonSerializer.SerializeToElement(reply.Body, Wire.Json);
        if (reply.Status >= 400) throw new InvalidOperationException(answer.ValueKind == JsonValueKind.Object && answer.Text("error") is { Length: > 0 } error ? error : Locale.Get("remote.error.startFailed"));
        return answer;
    }
}
