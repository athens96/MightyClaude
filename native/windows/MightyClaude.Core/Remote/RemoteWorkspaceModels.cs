using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MightyClaude.Core;

/// <summary>
/// Tailscale remote workspaces (docs/remote-workspaces.md): one Mighty Claude opens a workspace another computer's Mighty Claude
/// shares and runs agents and commands there. The protocol is the Mac's (M/Remote/RemoteWorkspaceModels.swift), pinned by
/// native/contracts/fixtures/remote-v2/. The host is RemoteHostService, the client RemoteClientService.
/// </summary>
public static class RemoteProtocol
{
    /// <summary>Sent and required as x-mighty-remote-version. The remote workspaces removed on 2026-09-28 spoke version 1 under /v1/.</summary>
    public const int Version = 2;
    public const string Header = "x-mighty-remote-version";
    public const string PathPrefix = "/v2/";
    public const int DefaultPort = 43137;
    /// <summary>A run the client has not polled for this long is stopped on the host.</summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);
    /// <summary>A client asks again this long after a poll failed for a passing reason before it calls the connection lost.</summary>
    public static readonly TimeSpan PollRetryWindow = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(60);
    public const int MaximumSharedWorkspaces = 64, MaximumRuns = 16, MaximumConnectionsPerPeer = 8, MaximumPendingPermissions = 32, MaximumConnections = 16;
    public const int MaximumRequestBytes = 512 * 1024, MaximumResponseBytes = 2 * 1024 * 1024, MaximumPollBytes = 1_536 * 1024, MaximumPollEvents = 256;
    public const int MaximumBufferedBytes = 4 * 1024 * 1024, MaximumBufferedEvents = 4096;
    /// <summary>A Windows client asks a Windows host for graph_run events (MightyGraphRun) instead of graph nodes; a Mac host ignores it.</summary>
    public const string GraphHeader = "x-mighty-remote-graph";
    public const string GraphRuns = "run";
}

/// <summary>A workspace the host shares, as the client lists it.</summary>
public sealed record RemoteSharedWorkspace(string Id, string Name, string Path);

/// <summary>What "이 컴퓨터 공유" shows. Sharing is always off when the app starts.</summary>
public sealed record RemoteHostState
{
    public bool Enabled { get; init; }
    /// <summary>Where clients reach this computer, address:port, one per Tailscale address.</summary>
    public IReadOnlyList<string> Addresses { get; init; } = [];
    public int? Port { get; init; }
    /// <summary>The connection key while sharing; stopping revokes it for good.</summary>
    public string? Key { get; init; }
    public IReadOnlyList<string> WorkspaceIds { get; init; } = [];
    public int ActiveRuns { get; init; }
    /// <summary>"원격에서 전체 권한·명령 실행 허용": off every time sharing starts.</summary>
    public bool AllowsFullAccess { get; init; }
}

public enum RemoteConnectionStatus { Saved, Connected, Disconnected }

/// <summary>One saved "다른 컴퓨터 연결" entry. Its key is in the DPAPI vault, never here.</summary>
public sealed record RemoteConnectionInfo(string Id, string Name, string Address, RemoteConnectionStatus Status = RemoteConnectionStatus.Saved, string? HostName = null,
    IReadOnlyList<RemoteSharedWorkspace>? Workspaces = null, RuntimeInfo? Runtime = null, string? Detail = null)
{
    [JsonIgnore] public IReadOnlyList<RemoteSharedWorkspace> Shared => Workspaces ?? [];
}

// Wire, with the Mac's JSON (Wire.Json writes camelCase and leaves nulls out).

public sealed record RemoteWireInfo(int Protocol, string HostId, string HostName, IReadOnlyList<RemoteSharedWorkspace> Workspaces, RuntimeInfo Runtime);

/// <summary>A pane's request as the Mac's StartRunRequest encodes it: never registeredModels or anything local.</summary>
public sealed record RemoteWireRequest(string SessionId, string WorkspaceId, string Kind, string Input, string Model, string Provider, RunSettings Settings, string? ResumeId = null, IReadOnlyList<RunAttachment>? Attachments = null)
{
    public static RemoteWireRequest From(StartRunRequest request, string hostWorkspaceId) =>
        new(request.SessionId, hostWorkspaceId, request.Kind, request.Input, request.Model, request.Provider, request.Settings ?? new(), request.ResumeId, request.Attachments);
}

/// <summary>RegisteredModelEntry as the Mac writes it: supportedEffortLevels always present.</summary>
public sealed record RemoteWireModel(string Name, bool SupportsEffort, string[] SupportedEffortLevels)
{
    public static RemoteWireModel From(RegisteredModelEntry entry) => new(entry.Name, entry.SupportsEffort, entry.SupportedEffortLevels ?? []);
    public RegisteredModelEntry ToEntry() => new(Name, SupportsEffort, SupportedEffortLevels);
}

/// <summary>A run request: what a pane saves, plus what the app adds per run beside it.</summary>
public sealed record RemoteWireStart(RemoteWireRequest Request, IReadOnlyList<RemoteWireModel> RegisteredModels, string? PermissionModeOverride = null, TodoProgress? TodoProgress = null, PhaseModelsSnapshot? PhaseModels = null)
{
    public static RemoteWireStart From(StartRunRequest request, string hostWorkspaceId) =>
        new(RemoteWireRequest.From(request, hostWorkspaceId), request.RegisteredModels.Select(RemoteWireModel.From).ToList(), request.PermissionModeOverride, request.TodoProgress, request.Kind == "claude" ? request.PhaseModels : null);

    /// <summary>The host's run: its own job id and workspace id, the extras validated by the caller.</summary>
    public StartRunRequest ToRequest(string sessionId, string workspaceId) =>
        new StartRunRequest(sessionId, workspaceId, Request.Kind, Request.Input, (RegisteredModels ?? []).Take(64).Select(m => m.ToEntry()).ToList(), Request.Model, Request.Provider, Request.Settings, Request.ResumeId, Request.Attachments)
        { PermissionModeOverride = PermissionModeOverride, TodoProgress = TodoProgress, PhaseModels = PhaseModels };
}

public sealed record RemoteWireJob(int Protocol, string JobId);
public sealed record RemoteWireEvent(int Cursor, JsonObject Event);
/// <param name="Cursor">The last cursor in Events (the request's when none).</param>
/// <param name="LastCursor">The run's newest cursor; more is waiting while it is past Cursor.</param>
/// <param name="Gap">Events after the request's cursor were dropped from the host's buffer.</param>
public sealed record RemoteWirePoll(int Protocol, int Cursor, int LastCursor, bool Gap, bool Done, IReadOnlyList<RemoteWireEvent> Events);
public sealed record RemoteWireSteer(string Text);
public sealed record RemoteWirePermission(string RunId, string RequestId, bool Allow);
public sealed record RemoteWireQuestions(string RunId, string RequestId, IReadOnlyDictionary<string, UserQuestionAnswer> Answers);
public sealed record RemoteWirePlan(string RunId, string RequestId, string Outcome, string? Feedback = null)
{
    public static RemoteWirePlan From(string runId, string requestId, PlanDecision decision) => new(runId, requestId, decision.Outcome, decision.Kind == "revise" ? decision.Feedback : null);
    /// <summary>The decision this outcome stands for; null for revised without feedback or an unknown outcome.</summary>
    public PlanDecision? Decision() => Outcome switch
    {
        PlanOutcome.ApprovedAuto => PlanDecision.ApproveAutoEdit,
        PlanOutcome.ApprovedConfirm => PlanDecision.ApproveConfirmEach,
        PlanOutcome.Revised => Feedback is { } feedback ? PlanDecision.Revise(feedback) : null,
        PlanOutcome.Cancelled => PlanDecision.Cancel,
        _ => null,
    };
}
public sealed record RemoteWireAnswer(int Protocol, bool Accepted);
/// <param name="Protocol">Every route's error carries it; the HTTP layer under the routes (a malformed head 400, a body too large 413,
/// headers too large 431) answers <c>{error}</c> alone, which reads as null and is written back without it.</param>
public sealed record RemoteWireError(int? Protocol, string Error);

/// <summary>The host answered with an error status; its message is what the user reads.</summary>
public sealed class RemoteHttpFailure(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
    /// <summary>Busy for a moment (rate limit, a local update holding runs): worth asking again.</summary>
    public bool Transient => Status is 429 or 503;
}

/// <summary>Remote workspaces are not mirrored to the phone: it sees this computer's own workspaces and panes only.</summary>
public static class RemoteWorkspacePhone
{
    public static IReadOnlyList<Workspace> Workspaces(IEnumerable<Workspace> all) => all.Where(w => w.Remote is null).ToList();
    public static IReadOnlyList<RunSession> Sessions(IEnumerable<RunSession> sessions, IEnumerable<Workspace> workspaces)
    {
        var local = workspaces.Where(w => w.Remote is null).Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        return sessions.Where(s => local.Contains(s.WorkspaceId)).ToList();
    }
}
