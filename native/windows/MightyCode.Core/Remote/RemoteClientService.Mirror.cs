using System.Text.Json;

namespace MightyCode.Core;

/// <summary>
/// The remote mirror's requests (docs/remote-workspaces.md "그 컴퓨터의 창 그대로", M/Remote/RemoteClientService.swift mirror…): a connected
/// host's shared panes read and driven from here. A 401 (the host shares with a new key now) leaves the connection disconnected, as a
/// run's poll does; any other refusal is the caller's to read (<see cref="RemoteHttpFailure"/>, its status and the host's words).
/// </summary>
public sealed partial class RemoteClientService
{
    /// <summary>The Mac's: 30 s for a send without attachments.</summary>
    private static readonly TimeSpan MirrorSendTimeout = TimeSpan.FromSeconds(30);

    private async Task<byte[]> MirrorRequestAsync(string connectionId, HttpMethod method, string path, byte[]? body = null, TimeSpan? timeout = null, CancellationToken token = default)
    {
        Entry entry; RemoteTarget target; string key; Guid revision;
        lock (gate)
        {
            if (closing) throw new InvalidOperationException(Locale.Get("common.appClosing"));
            if (!connections.TryGetValue(connectionId, out var known) || known.Info.Status != RemoteConnectionStatus.Connected || known.Target is not { } reached || known.Key is not { } held)
                throw new InvalidOperationException(Locale.Get("remote.run.error.notConnected"));
            entry = known; target = reached; key = held; revision = known.Revision;
        }
        await EnsureRouteAsync(connectionId, entry, revision, target);
        try { return await RemoteTransport.RequestAsync(target, key, method, path, body, timeout, token: token); }
        catch (RemoteHttpFailure failure) when (failure.Status == 401) { await FailAsync(connectionId, failure.Message, entry, revision); throw; }
    }

    private static T? Answer<T>(byte[] data) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(data, RemoteWire.Json); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException) { return null; }
    }

    private static InvalidDataException BadAnswer() => new(Locale.Get("remote.mirror.error.badAnswer"));

    /// <summary>The host's shared workspaces and their agent panes (<c>GET /v2/mirror</c>); <c>Unchanged</c> when <paramref name="since"/> is still current.</summary>
    public async Task<RemoteMirrorState> MirrorStateAsync(string connectionId, string? since, CancellationToken token = default)
    {
        if (since is not null && !System.Text.RegularExpressions.Regex.IsMatch(since, "^[0-9a-f]{0,64}\\z")) since = null;
        var data = await MirrorRequestAsync(connectionId, HttpMethod.Get, RemoteMirrorRoutes.Prefix + (since is null ? "" : "?since=" + since), token: token);
        var state = Answer<RemoteWireMirrorState>(data);
        if (!RemoteMirrorSupport.Valid(state)) throw BadAnswer();
        return new RemoteMirrorState(state!.HostName, state.Revision, state.Unchanged == true, state.Workspaces, state.Sessions);
    }

    /// <summary>One host pane in full (<c>GET /v2/mirror/sessions/{id}</c>), in this app's types.</summary>
    public async Task<RemoteMirrorPane> MirrorDetailAsync(string connectionId, string hostSessionId, CancellationToken token = default)
    {
        if (!Wire.Identifier(hostSessionId)) throw BadAnswer();
        var data = await MirrorRequestAsync(connectionId, HttpMethod.Get, RemoteMirrorRoutes.Prefix + "/sessions/" + hostSessionId, token: token);
        var detail = Answer<RemoteWireMirrorDetail>(data);
        var session = detail is null ? null : RemoteMirrorSupport.Read(detail.Session);
        if (!RemoteMirrorSupport.Valid(detail, session, hostSessionId)) throw BadAnswer();
        return new RemoteMirrorPane(detail!.Revision, session!);
    }

    /// <summary>A request into the host pane: the host's word for what it did (<c>started</c>, <c>queued</c>, …).</summary>
    public async Task<string> MirrorSubmitAsync(string connectionId, string hostSessionId, string text, IReadOnlyList<RunAttachment> attachments)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new RemoteWireMirrorSubmit(text, attachments.Count == 0 ? null : attachments), RemoteWire.Json);
        if (body.Length > AttachmentSupport.MaximumRequestBytes || attachments.Count == 0 && body.Length > RemoteProtocol.MaximumRequestBytes) throw new InvalidOperationException(Locale.Get("remote.run.error.tooLarge"));
        var data = await MirrorRequestAsync(connectionId, HttpMethod.Post, Session(hostSessionId, "submit"), body, attachments.Count == 0 ? MirrorSendTimeout : RemoteProtocol.UploadTimeout);
        return Answer<RemoteWireMirrorOutcome>(data)?.Outcome ?? "accepted";
    }

    public Task MirrorStopAsync(string connectionId, string hostSessionId) => MirrorRequestAsync(connectionId, HttpMethod.Post, Session(hostSessionId, "stop"), "{}"u8.ToArray());
    public Task MirrorCloseAsync(string connectionId, string hostSessionId) => MirrorRequestAsync(connectionId, HttpMethod.Post, Session(hostSessionId, "close"), "{}"u8.ToArray());

    /// <summary>An answer to a host pane's tool request; <paramref name="always"/> (an Antigravity card's 항상 허용) only when set, as the Mac sends it.</summary>
    public Task MirrorPermissionAsync(string connectionId, string hostSessionId, string requestId, string runId, bool allow, bool always = false) =>
        MirrorRequestAsync(connectionId, HttpMethod.Post, Session(hostSessionId, "permission"), JsonSerializer.SerializeToUtf8Bytes(new RemoteWireMirrorPermission(requestId, runId, allow, always ? true : null), RemoteWire.Json));

    public Task MirrorAnswersAsync(string connectionId, string hostSessionId, string requestId, string runId, IReadOnlyDictionary<string, UserQuestionAnswer> answers) =>
        MirrorRequestAsync(connectionId, HttpMethod.Post, Session(hostSessionId, "questions"), JsonSerializer.SerializeToUtf8Bytes(new RemoteWireMirrorQuestions(requestId, runId, answers), RemoteWire.Json));

    public Task MirrorPlanAsync(string connectionId, string hostSessionId, string requestId, string runId, PlanDecision decision) =>
        MirrorRequestAsync(connectionId, HttpMethod.Post, Session(hostSessionId, "plan"), JsonSerializer.SerializeToUtf8Bytes(RemoteWirePlan.From(runId, requestId, decision), RemoteWire.Json));

    /// <summary>A new agent pane in the host's shared workspace; the host's id for it.</summary>
    public async Task<string> MirrorCreateSessionAsync(string connectionId, string hostWorkspaceId, string provider)
    {
        if (!Wire.Identifier(hostWorkspaceId)) throw new InvalidOperationException(Locale.Get("remote.workspace.error.notShared"));
        var data = await MirrorRequestAsync(connectionId, HttpMethod.Post, RemoteMirrorRoutes.Prefix + "/workspaces/" + hostWorkspaceId + "/sessions", JsonSerializer.SerializeToUtf8Bytes(new RemoteWireMirrorCreate(provider), RemoteWire.Json));
        return Answer<RemoteWireMirrorCreated>(data) is { } created && Wire.Identifier(created.SessionId) ? created.SessionId : throw BadAnswer();
    }

    /// <summary>Marks the connection lost from the mirror's poll (it failed past its retries); its runs end as a failed poll's do.</summary>
    public async Task MirrorLostAsync(string connectionId, string message)
    {
        bool connected; lock (gate) connected = connections.TryGetValue(connectionId, out var entry) && entry.Info.Status == RemoteConnectionStatus.Connected;
        if (connected) await FailAsync(connectionId, message);
    }

    private static string Session(string hostSessionId, string action) =>
        Wire.Identifier(hostSessionId) ? RemoteMirrorRoutes.Prefix + "/sessions/" + hostSessionId + "/" + action : throw new InvalidOperationException(Locale.Get("remote.error.paneNotFound"));

    /// <summary>The status a host's refusal carries, when it answered with one that is not a passing busy (429, 503); null for any other failure.</summary>
    public static int? RefusalStatus(Exception error) => error is RemoteHttpFailure { Transient: false } failure ? failure.Status : null;
}
