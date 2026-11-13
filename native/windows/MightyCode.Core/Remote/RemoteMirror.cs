using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MightyCode.Core;

// The remote mirror (docs/remote-workspaces.md "그 컴퓨터의 창 그대로", M/MightyCore/Remote/RemoteMirror.swift): a client connected over
// Tailscale sees the host's shared workspaces with the agent panes already open there, and keeps using them from here. The panes stay
// the host's: a send from the client is the host pane's next request, as the phone's is. The wire is versioned on its own
// (RemoteMirrorProtocol.Version) inside protocol 2, and a Mac and a Windows app read each other's answers.

public static class RemoteMirrorProtocol
{
    /// <summary>Sent as <c>mirror</c> in every mirror answer; a client refuses another.</summary>
    public const int Version = 1;
    /// <summary>The newest transcript entries a pane's detail carries (the phone's page).</summary>
    public const int MaximumEntries = MobileWire.MaximumEntries;
    /// <summary>The newest graph runs a pane's detail carries (the phone's).</summary>
    public const int MaximumGraphRuns = MobileWire.MightyRuns;
    /// <summary>One detail's session JSON stays under this many bytes (a client reads at most <see cref="RemoteProtocol.MaximumResponseBytes"/>).</summary>
    public const int MaximumDetailBytes = RemoteProtocol.MaximumPollBytes;
    /// <summary>Panes listed per host.</summary>
    public const int MaximumSessions = 128;
    /// <summary>Queued requests listed per pane, each cut to <see cref="MaximumQueuedText"/> characters.</summary>
    public const int MaximumQueued = 16, MaximumQueuedText = 500;
    /// <summary>Pending tool requests a pane's detail carries.</summary>
    public const int MaximumPermissions = RemoteProtocol.MaximumPendingPermissions;
    /// <summary>Details a client reads in one pass of its poll.</summary>
    public const int MaximumDetailsPerPass = 16;
    /// <summary>A sent request's text, in UTF-8 bytes.</summary>
    public const int MaximumText = 400_000;
    /// <summary>How often a client reads the host's list: while one of its panes runs or waits or the user looks at one, and otherwise.</summary>
    public static readonly TimeSpan LivePollInterval = TimeSpan.FromSeconds(1), IdlePollInterval = TimeSpan.FromSeconds(5);
    /// <summary>The status words a pane travels with; any other is sent as <c>idle</c>.</summary>
    public static readonly IReadOnlySet<string> Statuses = new HashSet<string>(StringComparer.Ordinal) { "idle", "running", "completed", "error", "stopped" };
}

/// <summary>One host pane in the list (<c>GET /v2/mirror</c>): enough for the sidebar, and a revision that moves whenever its detail changed.</summary>
/// <param name="Pending">Tool, question and plan requests waiting for an answer, and an Antigravity card.</param>
/// <param name="Branch">The pane's own worktree branch, when it works in one.</param>
public sealed record RemoteMirrorSummary(string Id, string WorkspaceId, string Title, string Kind, string Provider, string Model, string Status, string Revision, int Pending, int Queued,
    string? Branch = null);

/// <summary>A request waiting in a host pane's queue.</summary>
public sealed record RemoteMirrorQueued(string Id, string Text);

/// <summary>
/// One host pane in full (<c>GET /v2/mirror/sessions/{id}</c>), in this app's types. On the wire it is the Mac's JSON
/// (<see cref="RemoteMirrorSupport.Write"/>, <see cref="RemoteMirrorSupport.Read"/>): a request's input is <c>inputJSON</c>, pictures and
/// task output files are left out, and times are in the forms the Mac reads.
/// </summary>
/// <param name="Logs">The newest <see cref="RemoteMirrorProtocol.MaximumEntries"/> entries, pictures left out.</param>
/// <param name="HasOlder">The host holds older entries than <paramref name="Logs"/>.</param>
/// <param name="StartedAt">The current request's clock, ISO 8601 in whole seconds (<paramref name="FinishedAt"/> once it is over).</param>
public sealed record RemoteMirrorSession(string Id, string WorkspaceId, string Title, string Kind, string Provider, string Model, string Status, string CreatedAt, RunSettings Settings,
    IReadOnlyList<LogEntry> Logs, bool HasOlder, IReadOnlyList<MightyGraphRun> GraphRuns, IReadOnlyList<ToolPermissionRequest> Permissions, IReadOnlyList<RemoteMirrorQueued> Queued,
    string? TitleMode = null, string? AgentViewMode = null, string? MightyStyle = null, TodoProgress? TodoProgress = null, BackgroundWork? BackgroundWork = null,
    string? StartedAt = null, string? FinishedAt = null, string? Branch = null, string? Base = null);

/// <summary>The host's list as the client reads it. <paramref name="Unchanged"/>: the client's <c>since</c> was still current, and the lists mean nothing.</summary>
public sealed record RemoteMirrorState(string HostName, string Revision, bool Unchanged, IReadOnlyList<RemoteSharedWorkspace> Workspaces, IReadOnlyList<RemoteMirrorSummary> Sessions);

/// <summary>What ties a client pane to the host pane it mirrors. Never saved: mirrored panes live only while the app runs.</summary>
public sealed record RemoteMirrorReference(string ConnectionId, string HostSessionId, string HostWorkspaceId, string HostName);

/// <summary>A pane's detail and the revision it was read at.</summary>
public sealed record RemoteMirrorPane(string Revision, RemoteMirrorSession Session);

/// <summary>Which workspace a host agent pane is in, the agent it runs and its permission mode (full access, and on a Windows host Codex and Antigravity, need the host's switch).</summary>
public sealed record RemoteMirrorPaneInfo(string WorkspaceId, string PermissionMode, string Provider);

/// <summary>
/// The host side: this app's panes as a connected client reads and drives them. Only panes of shared workspaces are ever asked for; the
/// host service checks that before every call. A failure an action throws is the host's refusal (409, its message the reason).
/// </summary>
public interface IRemoteMirrorHost
{
    /// <summary>Every agent pane of these workspaces, in the order the host lists them.</summary>
    Task<IReadOnlyList<RemoteMirrorSummary>> SummariesAsync(IReadOnlySet<string> workspaceIds);
    /// <summary>One pane in full with its revision; null when it is gone or not in these workspaces.</summary>
    Task<RemoteMirrorPane?> DetailAsync(string sessionId, IReadOnlySet<string> workspaceIds);
    /// <summary>Which workspace an agent pane of this app is in; null when there is no such agent pane.</summary>
    Task<RemoteMirrorPaneInfo?> PaneInfoAsync(string sessionId);
    /// <summary>A request into the host pane, as the phone's send: <c>started</c>, <c>steered</c> or <c>queued</c>.</summary>
    Task<string> SubmitAsync(string sessionId, string text, IReadOnlyList<RunAttachment> attachments, CancellationToken token);
    Task StopAsync(string sessionId, CancellationToken token);
    Task PermissionAsync(string sessionId, string requestId, string runId, bool allow, bool always, CancellationToken token);
    Task AnswersAsync(string sessionId, string requestId, string runId, IReadOnlyDictionary<string, UserQuestionAnswer> answers, CancellationToken token);
    Task PlanAsync(string sessionId, string requestId, string runId, PlanDecision decision, CancellationToken token);
    /// <summary>A new agent pane in a shared workspace (the workspace's worktree mode decides without asking); its id.</summary>
    Task<string> CreateSessionAsync(string workspaceId, string provider, CancellationToken token);
    Task CloseAsync(string sessionId, CancellationToken token);
}

// Wire bodies (M/MightyCore/Remote/RemoteMirror.swift RemoteWireMirror…).

/// <param name="Revision">Moves whenever the list (workspaces, panes, any pane's revision) changed.</param>
/// <param name="Unchanged">The client's <c>since</c> was this revision: nothing else is sent.</param>
public sealed record RemoteWireMirrorState(int Protocol, int Mirror, string HostName, string Revision, IReadOnlyList<RemoteSharedWorkspace> Workspaces, IReadOnlyList<RemoteMirrorSummary> Sessions,
    bool? Unchanged = null);
/// <param name="Session">The Mac's JSON of <see cref="RemoteMirrorSession"/> (<see cref="RemoteMirrorSupport.Write"/>).</param>
public sealed record RemoteWireMirrorDetail(int Protocol, int Mirror, string Revision, [property: JsonConverter(typeof(RemoteWire.EventConverter))] JsonObject Session);
/// <summary><c>attachments</c> may be left out (no files).</summary>
public sealed record RemoteWireMirrorSubmit(string Text, IReadOnlyList<RunAttachment>? Attachments = null);
public sealed record RemoteWireMirrorOutcome(int Protocol, string Outcome);
public sealed record RemoteWireMirrorPermission(string RequestId, string RunId, bool Allow, bool? Always = null);
public sealed record RemoteWireMirrorQuestions(string RequestId, string RunId, IReadOnlyDictionary<string, UserQuestionAnswer> Answers);
public sealed record RemoteWireMirrorCreate(string Provider);
public sealed record RemoteWireMirrorCreated(int Protocol, string SessionId);

/// <summary>The rules both sides read (M/MightyCore/Remote/RemoteMirror.swift RemoteMirrorSupport).</summary>
public static class RemoteMirrorSupport
{
    /// <summary>
    /// A stable id on the client for a host's workspace or pane: <c>mirror-</c> and 32 hex digits of SHA-256 over the connection and the
    /// host's id, so two connections never collide and the same host pane keeps one client id while connected. The Mac's exactly.
    /// </summary>
    public static string ClientId(string connectionId, string hostId) =>
        "mirror-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(connectionId + "\0" + hostId)).AsSpan(0, 16));

    /// <summary>
    /// A pane's revision: everything its detail carries that can change, read cheaply (the transcript's tail, not all of it; the graph's
    /// block identities and states, not their streamed text). Equal on the same host process for the same pane; a client only compares it.
    /// </summary>
    public static string Revision(RunSession session, IReadOnlyList<ToolPermissionRequest> permissions, IReadOnlyList<RemoteMirrorQueued> queued, string? branch, string? baseBranch = null)
    {
        var invariant = CultureInfo.InvariantCulture;
        var parts = new List<string> { session.Status, session.Title, session.Provider, session.Model, session.Settings.PermissionMode, session.Settings.Effort,
            session.AgentViewMode ?? "", session.MightyStyle ?? "", session.Logs.Count.ToString(invariant), branch ?? "", baseBranch ?? "", session.TitleMode ?? "",
            JsonSerializer.Serialize(session.Settings, RemoteWire.Json) };
        parts.AddRange(session.Logs.TakeLast(12).Select(l => l.Id + ":" + Encoding.UTF8.GetByteCount(l.Text).ToString(invariant) + ":" + (l.Activity?.State ?? "")));
        parts.Add(MobileMightySupport.Digest(session).ToString(invariant));
        if (session.TodoProgress is { } todos) parts.AddRange(todos.Items.Select(i => (i.Id ?? i.Content) + ":" + i.Status));
        if (session.BackgroundWork is { } work) { parts.AddRange(work.Tasks.Select(t => t.Id + ":" + t.Status)); parts.Add(work.TurnEnded ? "ended" : ""); }
        parts.Add(session.RunTiming is { } timing ? timing.StartedAt.ToUnixTimeMilliseconds().ToString(invariant) + ":" + (timing.FinishedAt?.ToUnixTimeMilliseconds() ?? 0).ToString(invariant) : "");
        parts.AddRange(permissions.Select(p => p.Id + ":" + p.RunId + ":" + p.State));
        parts.AddRange(queued.Select(q => q.Id + ":" + q.Text.Length.ToString(invariant)));
        return Digest(parts);
    }

    /// <summary>The list's revision: its workspaces and every pane's id and revision, in order (the Mac's materials).</summary>
    public static string StateRevision(IReadOnlyList<RemoteSharedWorkspace> workspaces, IReadOnlyList<RemoteMirrorSummary> sessions) =>
        Digest(workspaces.Select(w => w.Id + ":" + w.Name).Concat(sessions.Select(s => string.Join(':', s.Id, s.Revision, s.Title, s.Status, s.Pending.ToString(CultureInfo.InvariantCulture), s.Queued.ToString(CultureInfo.InvariantCulture)))));

    /// <summary>SHA-256 of the parts joined by U+0001, its first 12 bytes in hex (24 digits).</summary>
    internal static string Digest(IEnumerable<string> parts) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u0001', parts))).AsSpan(0, 12));

    /// <summary>The host pane as the wire carries it, bounded like the phone's payload and under <see cref="RemoteMirrorProtocol.MaximumDetailBytes"/>.</summary>
    public static RemoteMirrorSession Session(RunSession session, IReadOnlyList<ToolPermissionRequest> permissions, IReadOnlyList<RemoteMirrorQueued> queued, string? branch = null, string? baseBranch = null)
    {
        var logs = MobileRemoteSupport.PhoneEntries(session.Logs.TakeLast(RemoteMirrorProtocol.MaximumEntries));
        var runs = (session.GraphRuns ?? []).TakeLast(RemoteMirrorProtocol.MaximumGraphRuns).ToList();
        var value = new RemoteMirrorSession(session.Id, session.WorkspaceId, RemoteText.Cut(session.Title, 200), session.Kind, session.Provider, RemoteText.Cut(session.Model, 200),
            RemoteMirrorProtocol.Statuses.Contains(session.Status) ? session.Status : "idle", RemoteWireEvents.MacTimestamp(session.CreatedAt, milliseconds: false) ?? session.CreatedAt,
            session.Settings, logs, session.Logs.Count > logs.Count, runs,
            permissions.Where(p => p.State == "pending").TakeLast(RemoteMirrorProtocol.MaximumPermissions).ToList(),
            queued.Take(RemoteMirrorProtocol.MaximumQueued).Select(q => q with { Text = RemoteText.Cut(q.Text, RemoteMirrorProtocol.MaximumQueuedText) }).ToList(),
            session.TitleMode, session.AgentViewMode, session.MightyStyle, session.TodoProgress, session.BackgroundWork,
            session.RunTiming is { } timing ? Whole(timing.StartedAt) : null, session.RunTiming?.FinishedAt is { } finished ? Whole(finished) : null, branch, baseBranch);
        return Fitted(value, RemoteMirrorProtocol.MaximumDetailBytes);
    }

    /// <summary>The time as the Mac's ISO8601DateFormatter writes it (whole seconds, UTC), the only form its <c>startedAt</c> reader takes.</summary>
    private static string Whole(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// <paramref name="value"/> cut until its JSON is at most <paramref name="budget"/> bytes: the oldest graph runs first (keeping one),
    /// then the oldest entries (keeping one, and <c>hasOlder</c> says so), then each remaining text to 32 KiB, and last the graph emptied.
    /// </summary>
    public static RemoteMirrorSession Fitted(RemoteMirrorSession value, int budget)
    {
        static int Size(RemoteMirrorSession session) => JsonSerializer.SerializeToUtf8Bytes(Write(session), RemoteWire.Json).Length;
        var session = value;
        while (Size(session) > budget)
        {
            if (session.GraphRuns.Count > 1) { session = session with { GraphRuns = session.GraphRuns.Skip(1).ToList() }; continue; }
            if (session.Logs.Count > 1) { session = session with { Logs = session.Logs.Skip(1).ToList(), HasOlder = true }; continue; }
            const int cut = 32 * 1024;
            var changed = false;
            if (session.Logs.Any(l => Encoding.UTF8.GetByteCount(l.Text) > cut))
            {
                session = session with { Logs = session.Logs.Select(l => Encoding.UTF8.GetByteCount(l.Text) > cut ? l with { Text = Utf8Prefix(l.Text, cut) } : l).ToList() };
                changed = true;
            }
            if (session.GraphRuns.Count > 0 && Size(session) > budget) { session = session with { GraphRuns = [] }; changed = true; }
            if (!changed) break;
        }
        return session;
    }

    /// <summary>The longest start of <paramref name="text"/> of at most <paramref name="bytes"/> UTF-8 bytes, never part of a character.</summary>
    internal static string Utf8Prefix(string text, int bytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= bytes) return text;
        var used = 0; var end = 0;
        while (end < text.Length)
        {
            var width = char.IsHighSurrogate(text[end]) && end + 1 < text.Length && char.IsLowSurrogate(text[end + 1]) ? 2 : 1;
            var size = Encoding.UTF8.GetByteCount(text.AsSpan(end, width));
            if (used + size > bytes) break;
            used += size; end += width;
        }
        return text[..end];
    }

    /// <summary>
    /// The session as the Mac's JSON: every entry (the transcript's and the graph's) without pictures, an <c>image</c> entry as its
    /// <c>system</c> line; no task's output file (a path on the host); a request's input as <c>inputJSON</c>; the times inside entries,
    /// runs, requests and work in the forms the Mac writes. The session's own clock is whole seconds already (<see cref="Session"/>).
    /// </summary>
    public static JsonObject Write(RemoteMirrorSession session)
    {
        var node = JsonSerializer.SerializeToNode(session, RemoteWire.Json)!.AsObject();
        foreach (var entry in Objects(node["logs"])) PhoneEntry(entry);
        foreach (var run in Objects(node["graphRuns"]))
        {
            foreach (var entry in Objects(run["rootEntries"]).Concat(Objects(run["resultEntries"]))) PhoneEntry(entry);
            foreach (var agent in Objects(run["agents"])) { agent.Remove("outputFile"); foreach (var entry in Objects(agent["entries"])) PhoneEntry(entry); }
        }
        foreach (var permission in Objects(node["permissions"])) Rename(permission, "inputJson", "inputJSON");
        foreach (var key in new[] { "logs", "graphRuns", "permissions", "todoProgress", "backgroundWork" }) RemoteWireEvents.Normalize(node[key]);
        return node;
    }

    /// <summary>
    /// A host's session JSON (a Mac's or a Windows app's) in this app's types; null when it does not read. As the remote run's reader, a
    /// member a Windows pane can do without is left out rather than the whole pane refused: a graph, a checklist or background work that
    /// does not read here, and an entry's activity that does not.
    /// </summary>
    public static RemoteMirrorSession? Read(JsonObject wire)
    {
        try
        {
            var node = wire.DeepClone().AsObject();
            foreach (var permission in Objects(node["permissions"])) { permission.Remove("inputJson"); Rename(permission, "inputJSON", "inputJson"); }
            foreach (var entry in Objects(node["logs"])) { PhoneEntry(entry); Lenient<AgentActivity>(entry, "activity"); }
            if (node["graphRuns"] is JsonArray runs)
            {
                foreach (var run in runs.OfType<JsonObject>())
                {
                    foreach (var entry in Objects(run["rootEntries"]).Concat(Objects(run["resultEntries"]))) PhoneEntry(entry);
                    foreach (var agent in Objects(run["agents"])) foreach (var entry in Objects(agent["entries"])) PhoneEntry(entry);
                }
                if (!Decodes<List<MightyGraphRun>>(runs)) node["graphRuns"] = new JsonArray();
            }
            Lenient<TodoProgress>(node, "todoProgress");
            Lenient<BackgroundWork>(node, "backgroundWork");
            return node.Deserialize<RemoteMirrorSession>(RemoteWire.Json);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException) { return null; }
    }

    private static IEnumerable<JsonObject> Objects(JsonNode? list) => list is JsonArray array ? array.OfType<JsonObject>().ToList() : [];

    private static void PhoneEntry(JsonObject entry)
    {
        entry.Remove("images");
        if (entry["kind"] is JsonValue kind && kind.TryGetValue<string>(out var name) && name == "image") entry["kind"] = "system";
    }

    private static void Rename(JsonObject value, string from, string to)
    {
        if (!value.TryGetPropertyValue(from, out var member)) return;
        value.Remove(from);
        value[to] = member;
    }

    private static bool Decodes<T>(JsonNode node)
    {
        try { return node.Deserialize<T>(RemoteWire.Json) is not null; }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException)) { return false; }
    }

    private static void Lenient<T>(JsonObject value, string key)
    {
        if (value[key] is { } member && !Decodes<T>(member)) value.Remove(key);
    }

    private static int Characters(string? value) => RemoteText.Count(value);

    /// <summary>A host's list as the client takes it (the Mac's <c>valid</c>): this version, bounded counts and identifiers, panes only of listed workspaces.</summary>
    public static bool Valid(RemoteWireMirrorState? state)
    {
        if (state is null || state.Protocol != RemoteProtocol.Version || state.Mirror != RemoteMirrorProtocol.Version || state.HostName.Length == 0 || Characters(state.HostName) > 120
            || Characters(state.Revision) > 64) return false;
        if (state.Unchanged == true) return true;
        var workspaces = state.Workspaces.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        return state.Workspaces.Count <= RemoteProtocol.MaximumSharedWorkspaces && state.Workspaces.All(RemoteWorkspaceValidation.SharedWorkspace)
            && workspaces.Count == state.Workspaces.Count && state.Sessions.Count <= RemoteMirrorProtocol.MaximumSessions
            && state.Sessions.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() == state.Sessions.Count
            && state.Sessions.All(s => Wire.Identifier(s.Id) && workspaces.Contains(s.WorkspaceId) && s.Kind == "claude" && Wire.Providers.Contains(s.Provider)
                && Characters(s.Title) <= 200 && Characters(s.Model) <= 200 && Characters(s.Revision) <= 64 && RemoteMirrorProtocol.Statuses.Contains(s.Status)
                && s.Pending is >= 0 and <= 10_000 && s.Queued is >= 0 and <= 10_000 && Characters(s.Branch) <= 200);
    }

    /// <summary>One pane's detail as the client takes it (the Mac's <c>valid(_:sessionId:)</c>).</summary>
    public static bool Valid(RemoteWireMirrorDetail? detail, RemoteMirrorSession? session, string sessionId)
    {
        if (detail is null || session is null || detail.Protocol != RemoteProtocol.Version || detail.Mirror != RemoteMirrorProtocol.Version || Characters(detail.Revision) > 64
            || session.Id != sessionId || session.Kind != "claude" || !Wire.Providers.Contains(session.Provider) || Characters(session.Title) > 200
            || !RemoteMirrorProtocol.Statuses.Contains(session.Status) || session.Logs.Count > RemoteMirrorProtocol.MaximumEntries || session.GraphRuns.Count > RemoteMirrorProtocol.MaximumGraphRuns
            || session.Permissions.Count > RemoteMirrorProtocol.MaximumPermissions || session.Queued.Count > RemoteMirrorProtocol.MaximumQueued) return false;
        return session.Logs.All(l => Wire.Identifier(l.Id) && LogEntry.Stored.Contains(l.Kind) && Encoding.UTF8.GetByteCount(l.Text) <= 131_072)
            && session.Permissions.All(p => Wire.Identifier(p.RunId) && p.Id.Length > 0 && Characters(p.Id) <= 256 && Encoding.UTF8.GetByteCount(p.InputJson) <= 65_536);
    }

    /// <summary>
    /// The client pane for a host pane: a new one from its summary, or the one it already has with the host's fields laid over it. What
    /// the client keeps of its own (block sizes, view choices made here) stays.
    /// </summary>
    public static RunSession Pane(RemoteMirrorSummary summary, RunSession? existing, string clientId, string workspaceId, RemoteMirrorReference reference)
    {
        var session = existing ?? new RunSession { Id = clientId, WorkspaceId = workspaceId, Title = summary.Title, Kind = "claude", Provider = summary.Provider };
        return session with { WorkspaceId = workspaceId, Title = summary.Title, Provider = summary.Provider, Model = summary.Model, Status = summary.Status, Mirror = reference };
    }

    /// <summary>The host pane's detail laid over the client pane (the view mode only when the host sent one).</summary>
    public static RunSession Apply(RemoteMirrorSession detail, RunSession existing, DateTimeOffset? now = null)
    {
        return existing with
        {
            Title = detail.Title, TitleMode = detail.TitleMode, Provider = detail.Provider, Model = detail.Model, Status = detail.Status, Settings = detail.Settings,
            AgentViewMode = detail.AgentViewMode ?? existing.AgentViewMode, MightyStyle = detail.MightyStyle, Logs = [.. detail.Logs],
            GraphRuns = detail.GraphRuns.Count == 0 ? null : [.. detail.GraphRuns], TodoProgress = detail.TodoProgress, BackgroundWork = detail.BackgroundWork,
            RunTiming = Timing(detail.StartedAt, detail.FinishedAt, now ?? DateTimeOffset.UtcNow), CurrentActivity = null,
        };
    }

    private static AgentRunTiming? Timing(string? started, string? finished, DateTimeOffset now)
    {
        if (!Parse(started, out var start)) return null;
        DateTimeOffset? end = Parse(finished, out var stop) && stop >= start ? stop : null;
        return new AgentRunTiming(start, end ?? (now > start ? now : start), end);
        static bool Parse(string? value, out DateTimeOffset at) =>
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at);
    }
}

public static class RemoteMirrorSnapshots
{
    /// <summary>
    /// The snapshot as it is saved: without the remote mirror's workspaces, their panes and what refers to them (they live only while
    /// connected, and the host's transcripts never land in this app's state file).
    /// </summary>
    public static AppSnapshot WithoutMirrors(this AppSnapshot value)
    {
        var mirrored = value.Workspaces.Where(w => w.Remote?.Mirror == true).Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        if (mirrored.Count == 0 && !value.Sessions.Any(s => s.Mirror is not null)) return value;
        var gone = value.Sessions.Where(s => s.Mirror is not null || mirrored.Contains(s.WorkspaceId)).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var workspaces = value.Workspaces.Where(w => !mirrored.Contains(w.Id)).ToList();
        Dictionary<string, TValue>? Without<TValue>(Dictionary<string, TValue>? map) => map?.Where(p => !mirrored.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        return value with
        {
            Workspaces = workspaces,
            Sessions = value.Sessions.Where(s => !gone.Contains(s.Id)).ToList(),
            PaneLayouts = Without(value.PaneLayouts), PaneLayoutModes = Without(value.PaneLayoutModes), PaneLayoutActiveSessionIds = Without(value.PaneLayoutActiveSessionIds),
            ExpandedWorkspaceIds = value.ExpandedWorkspaceIds?.Where(id => !mirrored.Contains(id)).ToList(),
            ActiveWorkspaceId = value.ActiveWorkspaceId is { } active && mirrored.Contains(active) ? workspaces.FirstOrDefault()?.Id : value.ActiveWorkspaceId,
            ActiveSessionId = value.ActiveSessionId is { } pane && gone.Contains(pane) ? null : value.ActiveSessionId,
        };
    }

    /// <summary>This PC's own panes, the ones the 128-pane limit (and the state file) counts; mirrored panes are the host's.</summary>
    public static int OwnSessionCount(this AppSnapshot value) => value.Sessions.Count(s => s.Mirror is null);
}

/// <summary>The mirror's paths under <c>/v2/</c>.</summary>
public static class RemoteMirrorRoutes
{
    public const string Prefix = RemoteProtocol.PathPrefix + "mirror";
    /// <summary><c>POST /v2/mirror/sessions/{id}/submit</c>: the one mirror request that may carry attachments.</summary>
    public static bool IsSubmit(string target)
    {
        var parts = target.Split('/');
        return parts.Length == 6 && parts[0].Length == 0 && parts[1] == "v2" && parts[2] == "mirror" && parts[3] == "sessions" && parts[5] == "submit" && Wire.Identifier(parts[4]);
    }
}
