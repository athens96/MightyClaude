using System.Text;
using System.Text.RegularExpressions;

namespace MightyCode.Core;

/// <summary>
/// The remote mirror on the host (docs/remote-workspaces.md "그 컴퓨터의 창 그대로", M/Remote/RemoteHostService.swift serveMirror):
/// <c>GET /v2/mirror[?since=]</c>, <c>GET /v2/mirror/sessions/{id}</c>, <c>POST /v2/mirror/sessions/{id}/submit|stop|permission|questions|plan|close</c>
/// and <c>POST /v2/mirror/workspaces/{id}/sessions</c>: the shared workspaces' own agent panes, read and driven as the phone does. A pane
/// of a workspace not shared is unknown here (404), whatever its id. Every request has passed the /v2/ checks (key, version, peer, rate).
/// </summary>
public sealed partial class RemoteHostService
{
    /// <summary>The app's panes for the mirror; null answers those routes 404 (an app that has not handed them over yet). Under <c>gate</c>.</summary>
    private IRemoteMirrorHost? mirror;
    private static readonly Regex SinceQuery = new(@"^\?since=([0-9a-f]{0,64})\z", RegexOptions.CultureInvariant);

    /// <summary>The app's panes a connected client mirrors (docs/remote-workspaces.md "그 컴퓨터의 창 그대로"); null takes them back.</summary>
    public void SetMirror(IRemoteMirrorHost? value) { lock (gate) mirror = value; }

    /// <summary>
    /// What a client may not send into a host pane while full access is off: a pane in full access, and on a Windows host a Codex or
    /// Antigravity pane in any mode (<see cref="FullAccessRefusal"/>'s rules: they cannot be confined here). Null when it may.
    /// </summary>
    public static string? MirrorRefusal(RemoteMirrorPaneInfo pane, bool allowed, bool? windowsHost = null) =>
        FullAccessRefusal(new StartRunRequest(Wire.Id(), pane.WorkspaceId, "claude", "mirror", [], Provider: pane.Provider, Settings: new(PermissionMode: pane.PermissionMode)), allowed, windowsHost);

    private void StillSharing(Guid revision) { lock (gate) if (revision != generation) throw new HostFailure(503, Locale.Get("remote.host.error.stopped")); }

    private static T Body<T>(RemoteHttpRequest request) where T : class
    {
        if (ContentType(request) != "application/json") throw new HostFailure(415, Locale.Get("remote.host.error.jsonOnly"));
        return Decode<T>(request.Body);
    }

    private async Task<RemoteHttpResponse> ServeMirrorAsync(RemoteHttpRequest request, string[] route, string query, Guid revision)
    {
        IRemoteMirrorHost host;
        lock (gate) host = mirror ?? throw new HostFailure(404, Locale.Get("remote.mirror.error.unavailable"));
        var workspaces = SharedWorkspaces();
        var shared = workspaces.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        StillSharing(revision);
        if (request.Method == "GET" && route.Length == 0)
        {
            string? since = null;
            if (query.Length > 0)
            {
                // One query, since, hexadecimal and at most 64 long (the Mac's).
                var match = SinceQuery.Match(query);
                if (!match.Success) throw new HostFailure(400, Locale.Get("remote.host.error.badRequest"));
                since = match.Groups[1].Value;
            }
            var sessions = (await host.SummariesAsync(shared)).Where(s => shared.Contains(s.WorkspaceId)).Take(RemoteMirrorProtocol.MaximumSessions).ToList();
            var listed = workspaces.Select(w => new RemoteSharedWorkspace(w.Id, w.Name, w.Path)).ToList();
            var current = RemoteMirrorSupport.StateRevision(listed, sessions);
            StillSharing(revision);
            var name = RemoteText.Cut(options.HostName, 120);
            if (name.Length == 0) name = "Windows";
            return since == current
                ? Reply(200, new RemoteWireMirrorState(RemoteProtocol.Version, RemoteMirrorProtocol.Version, name, current, [], [], Unchanged: true))
                : Reply(200, new RemoteWireMirrorState(RemoteProtocol.Version, RemoteMirrorProtocol.Version, name, current, listed, sessions));
        }
        // No query on any mirror route but the list's (a bare "?" is none, as the Mac reads it).
        if (query.Length > 0 && query != "?") throw new HostFailure(400, Locale.Get("remote.host.error.badPath"));
        if (request.Method == "POST" && route is ["workspaces", var workspaceId, "sessions"])
        {
            if (!shared.Contains(workspaceId)) throw new HostFailure(403, Locale.Get("remote.host.error.notShared"));
            var create = Body<RemoteWireMirrorCreate>(request);
            if (!Wire.Providers.Contains(create.Provider)) throw new HostFailure(400, Locale.Get("remote.host.error.badRequest"));
            try { return Reply(201, new RemoteWireMirrorCreated(RemoteProtocol.Version, await host.CreateSessionAsync(workspaceId, create.Provider, CancellationToken.None))); }
            catch (Exception ex) when (ex is not (HostFailure or OutOfMemoryException)) { throw new HostFailure(409, ex.Message); }
        }
        if (route.Length < 2 || route[0] != "sessions" || !Wire.Identifier(route[1]) || await host.PaneInfoAsync(route[1]) is not { } pane || !shared.Contains(pane.WorkspaceId))
            throw new HostFailure(404, Locale.Get("remote.error.paneNotFound"));
        var id = route[1];
        if (request.Method == "GET" && route.Length == 2)
        {
            var detail = await host.DetailAsync(id, shared) ?? throw new HostFailure(404, Locale.Get("remote.error.paneNotFound"));
            StillSharing(revision);
            return Reply(200, new RemoteWireMirrorDetail(RemoteProtocol.Version, RemoteMirrorProtocol.Version, detail.Revision, RemoteMirrorSupport.Write(detail.Session)));
        }
        if (request.Method != "POST" || route.Length != 3) throw new HostFailure(404, Locale.Get("remote.host.error.badPath"));
        bool allowed; lock (gate) allowed = allowsFullAccess;
        try
        {
            switch (route[2])
            {
                case "submit":
                    var submit = Body<RemoteWireMirrorSubmit>(request);
                    var attachments = submit.Attachments ?? [];
                    if (Encoding.UTF8.GetByteCount(submit.Text) > RemoteMirrorProtocol.MaximumText || submit.Text.Contains('\0') || attachments.Count > AttachmentSupport.MaximumCount
                        || string.IsNullOrWhiteSpace(submit.Text) && attachments.Count == 0) throw new HostFailure(400, Locale.Get("validation.run.badInput"));
                    // The listener read up to 12 MiB for this route; past 512 KiB only a send that brings attachments, as a run's start.
                    if (request.Body.Length > RemoteProtocol.MaximumRequestBytes && attachments.Count == 0) throw new HostFailure(413, Locale.Get("remote.run.error.tooLarge"));
                    try { AttachmentSupport.Validate(attachments); } catch (ArgumentException ex) { throw new HostFailure(400, ex.Message); }
                    // A pane in full access (and on this Windows PC a Codex or Antigravity pane) runs only when this computer lets others run with it.
                    if (MirrorRefusal(pane, allowed, options.WindowsHost) is { } refusal) throw new HostFailure(403, refusal);
                    return Reply(200, new RemoteWireMirrorOutcome(RemoteProtocol.Version, await host.SubmitAsync(id, submit.Text, attachments, CancellationToken.None)));
                // As a run's stop and the Mac's, a stop and a close read no body.
                case "stop": await host.StopAsync(id, CancellationToken.None); break;
                case "close": await host.CloseAsync(id, CancellationToken.None); break;
                case "permission":
                    var permission = Body<RemoteWireMirrorPermission>(request);
                    // "항상 허용" keeps rules on this computer for good: only with its switch on, as a remote run's grant.
                    if (permission.Always == true && !allowed) throw new HostFailure(403, Locale.Get("remote.host.error.antigravityAlways"));
                    await host.PermissionAsync(id, permission.RequestId, permission.RunId, permission.Allow, permission.Always == true, CancellationToken.None); break;
                case "questions":
                    var questions = Body<RemoteWireMirrorQuestions>(request);
                    await host.AnswersAsync(id, questions.RequestId, questions.RunId, questions.Answers, CancellationToken.None); break;
                case "plan":
                    var plan = Body<RemoteWirePlan>(request);
                    var decision = plan.Decision() ?? throw new HostFailure(400, Locale.Get("remote.host.error.badRequest"));
                    await host.PlanAsync(id, plan.RequestId, plan.RunId, decision, CancellationToken.None); break;
                default: throw new HostFailure(404, Locale.Get("remote.host.error.badPath"));
            }
        }
        // What the app refuses (settled, gone, busy, not its kind) is a conflict, its message the reason, as on the Mac.
        catch (Exception ex) when (ex is not (HostFailure or OutOfMemoryException)) { throw new HostFailure(409, ex.Message); }
        return Reply(200, new RemoteWireAnswer(RemoteProtocol.Version, true));
    }
}
