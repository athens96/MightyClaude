using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MightyCode.Core;

public sealed record MobileImagePreview(string Mime, string Data, int Width, int Height, int ThumbnailWidth, int ThumbnailHeight);
/// <summary>A decoded thumbnail, or why there is none: <c>tooLarge</c> or <c>undecodable</c>.</summary>
public sealed record MobileImageResult(MobileImagePreview? Image, string? Reason = null);
/// <summary>What the revision of a pane is read from beside its record (<see cref="MobileRevisions.Reading"/>).</summary>
public sealed record MobilePaneDigest(bool Editable, int Options, int Mighty);
/// <summary>
/// One row of the phone's <c>queued</c> array. <c>RunNow</c> and <c>RunNowReason</c>: what 바로 실행 does on it (<see cref="QueueRunNow.Wire"/>),
/// both left out when the row has no button (a held row after the first), as an older host's rows are.
/// </summary>
public sealed record MobileQueuedItem(string Id, string Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RunNow = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RunNowReason = null);
/// <summary>
/// What a pane's detail carries beside its record. <c>AskTargets</c>: the composer's "send to" targets, sent only on a pane that offers
/// any (a Claude agent pane with oh-my-claudecode's ask and an installed target); <c>AskTarget</c>: the one its sends go to now, null for Claude.
/// <c>BackgroundWork</c>: the background work still running, as the strip lists it (<see cref="PlanCardSupport.MobileBackgroundWork"/>); absent when none runs.
/// <c>TerminalPrompt</c>: the command in the pane's terminal waiting for input (docs/terminal-prompts.md); absent when none waits.
/// <c>BackgroundDone</c>: how many of the strip's tasks ended, sent with the strip, 0 included (<see cref="PlanCardSupport.MobileBackgroundDone"/>); absent when it does not show.
/// </summary>
public sealed record MobilePaneExtras(IReadOnlyList<MobileQueuedItem> Queued, object? Settings = null, object? Mighty = null, object? StatusLine = null, IReadOnlyList<MobileAskTarget>? AskTargets = null, string? AskTarget = null,
    IReadOnlyList<MobileBackgroundWork>? BackgroundWork = null, int? BackgroundDone = null, TerminalPrompt? TerminalPrompt = null);
/// <summary>One choice of the phone's "send to" switch (<c>AskTargets</c>), in the desktop menu's order.</summary>
public sealed record MobileAskTarget(string Id, string Label);
public sealed record MobileDesktopAction(string SessionId, string Action, JsonElement Body, IReadOnlyList<RunAttachment>? Attachments = null);

/// The m1 contract used by the existing phone app. The caller dispatches routes
/// to the desktop UI actor so all mutating actions use its ordinary composer.
public sealed class MobileDesktopRouter : IDisposable
{
    public Func<string[]>? ExtraCapabilities { get; set; }
    public static readonly string[] Capabilities = ["submit-mode", "queue", "pane", "history", "settings", "commands", "mighty", "status", "attachments", "style", "files", "structure", "direct", "push", "fileChanges", "workMap", "appUpdate", "pi", "opencode", "hermes", "roles"];
    /// <summary>The direct path's endpoints for /m1/info (docs/mobile-remote.md, "direct"); empty when there is no host or it does not listen.</summary>
    public Func<IReadOnlyList<string>>? DirectEndpoints { get; set; }
    /// <summary>The largest reply body the host sends; a session detail over it sheds its structure first (<see cref="SessionReply"/>).</summary>
    public const int MaximumReplyBytes = MobileWire.MaximumReplyBytes;
    /// <summary>The pane's structure view for a phone that asked for it with <c>?structure=&lt;tag&gt;</c>; null for a pane with no Mighty payload.</summary>
    public Func<RunSession, MobileStructureCache.Built?>? Structure { get; set; }
    /// <summary>The pane's extras with the role view in its Mighty payload (<see cref="MobileFlow"/>: who each block is, a Claude pane's fan-out and
    /// log) for a phone that asked for it with <c>?roles=1</c>; without it such a phone gets the plain extras.</summary>
    public Func<RunSession, MobilePaneExtras>? RoleExtras { get; set; }
    private readonly DesktopService service;
    private readonly Func<MobileDesktopAction, CancellationToken, Task<object?>> action;
    private readonly Func<RunSession, MobilePaneExtras> extra;
    private readonly MobileUploads uploads;
    /// Files the phone is uploading or has uploaded and not yet sent (M openUploadCount).
    public int OpenUploadCount => uploads.Count;
    private readonly object sync = new();
    private readonly Dictionary<string, ToolPermissionRequest> permissions = [];
    private readonly SemaphoreSlim submitSlots = new(2), fileSlots = new(2);
    private readonly MobilePreviewSlot previews = new();
    private readonly MobileRevisions revisions = new();
    private readonly List<(string Scope, long Revision)> notifications = [];
    private int moved = 1;
    public long StateRevision { get { lock (sync) return revisions.State; } }
    public long SessionRevision(string id) { lock (sync) return revisions.Session(id); }
    /// <summary>A pane's settings and Mighty identity for its revision; without it (or null) a pane is read from its record alone (<see cref="Unread"/>).</summary>
    public Func<RunSession, MobilePaneDigest?>? Digest { get; set; }
    /// <summary>The style a pane is really running, null for none: a stored style that is not approved, or a pane outside the Mighty view, answers "cli" (M/MightyCode/AppStore+MobileRemote.swift:346-351).</summary>
    public Func<RunSession, string?>? ActiveStyle { get; set; }
    /// <summary>The desktop app's update (docs/phone-desktop-update.md); null when this build cannot update itself, and without the function the phone gets no update state and 409 from <c>POST /m1/app-update</c>.</summary>
    public Func<AppUpdateState?>? AppUpdateRead { get; set; }
    /// <summary>The phone's button: <c>true</c> installs (checking and downloading first as needed), <c>false</c> only checks.</summary>
    public Func<bool, Task>? AppUpdateAct { get; set; }
    /// <summary>Draws an image file's thumbnail from its name and bytes.</summary>
    public Func<string, byte[], CancellationToken, Task<MobileImageResult>>? ImagePreview { get; set; }
    public Func<string, string, JsonElement?, string, CancellationToken, Task<MobileReply>>? ScreenRoute { get; set; }
    /// <summary>
    /// Whether screen share is on for this host (<see cref="ScreenShareAvailability.Enabled"/>; the screen-share checks pass true). Off,
    /// <see cref="ScreenRoute"/> is ignored, so <c>/m1/screen-share</c> answers 503, and <c>screenShare</c> is never advertised.
    /// </summary>
    public bool ScreenShareAvailable { get; init; } = ScreenShareAvailability.Enabled;
    public Func<string, int>? QueueCount { get; set; }
    /// <summary>Where <c>/m1/push/*</c> keeps a phone's handle and view; null answers them as unknown routes.</summary>
    public MobilePushHub? Push { get; set; }
    /// <summary>The model list this PC's CLI gave for the pane's provider, for the summary's <c>resolvedModel</c> (phone summaries cover local panes only, <see cref="RemoteWorkspacePhone"/>); null (or none) reads the provider's fallback list.</summary>
    public Func<RunSession, ModelCatalog?>? Catalog { get; set; }
    /// <summary>The work map of one of this PC's workspaces (docs/work-map.md), as <see cref="WorkMapService.ModelAsync"/> reads it; without it the work map routes answer 404.</summary>
    public Func<Workspace, Task<WorkMapModel>>? WorkMapRead { get; set; }
    /// <summary>Starts the topic summary of one version of a workspace (<see cref="WorkMapService.StartAsync"/>).</summary>
    public Func<Workspace, string, Task<WorkMapStart>>? WorkMapSummarize { get; set; }
    public string HostId { get; set; } = "";
    public string HostName { get; }
    public string AppVersion { get; init; } = "0.0.0";
    public MobileDesktopRouter(DesktopService service, string directory, string hostName,
        Func<MobileDesktopAction, CancellationToken, Task<object?>> action, Func<RunSession, MobilePaneExtras> extra)
    {
        this.service = service; this.action = action; this.extra = extra; HostName = hostName;
        uploads = new(Path.Combine(directory, "uploads")); service.RunEventReceived += Receive; service.ToolPermissionChanged += Permission; service.DelegationChanged += Changed;
        service.AntigravityCardChanged += CardChanged;
    }
    /// <summary>Something a phone may show has moved; the panes are read again at the next <see cref="Observe"/>.</summary>
    public void Changed() => Volatile.Write(ref moved, 1);
    /// <summary>
    /// Reads the panes again when something was reported since the last pass and moves the revisions of what changed
    /// (<see cref="MobileRevisions"/>). The phones to tell are then in <see cref="TakeNotifications"/>. A pane whose digest
    /// cannot be read (closed meanwhile) is read from its record; a pass that fails all the same is made again at the next call.
    /// </summary>
    public void Observe()
    {
        if (Interlocked.Exchange(ref moved, 0) == 0) return;
        try
        {
            // A remote workspace and its panes are another computer's: the phone is never told of them (M/AppStore+MobileRemote.swift mobilePane).
            var snapshot = service.Snapshot;
            var readings = RemoteWorkspacePhone.Sessions(snapshot.Sessions, snapshot.Workspaces).Where(s => s.Kind is not ("files" or "workmap")).Select(pane =>
            {
                MobilePaneDigest digest;
                try { digest = Digest?.Invoke(pane) ?? Unread(pane); } catch (Exception ex) when (ex is not OutOfMemoryException) { digest = Unread(pane); }
                // The held rows by id, so one swapped for another moves the pane even when their count stays.
                return new MobileRevisions.Reading(pane, digest.Editable, digest.Options, digest.Mighty, JsonSerializer.Serialize(Summary(pane, 0), Wire.Json), string.Join('\n', service.HeldItems(pane.Id).Select(item => item.Id)));
            }).ToArray();
            lock (sync) foreach (var note in revisions.Observe(RemoteWorkspacePhone.Workspaces(snapshot.Workspaces), readings, JsonSerializer.Serialize(AppUpdatePhone(), Wire.Json))) { notifications.RemoveAll(n => n.Scope == note.Scope); notifications.Add(note); }
        }
        catch { Changed(); throw; }
    }
    /// <summary>A pane's digest from its record alone: no pickers to compare, and the Mighty identity its transcript gives.</summary>
    private static MobilePaneDigest Unread(RunSession pane) => new(pane.Status != "running", 0, pane.Kind == "claude" ? MobileMightySupport.Digest(pane) : 0);
    /// <summary>The scopes whose revision moved since the last call, <c>state</c> before the sessions, each with its newest revision.</summary>
    public IReadOnlyList<(string Scope, long Revision)> TakeNotifications() { lock (sync) { var taken = notifications.ToArray(); notifications.Clear(); return taken; } }
    private void Receive(RunEvent value) => Changed();
    /// <summary>A pane's Antigravity card came, went or changed: the pane moves (M/AppStore+MobileRemote.swift mobileObserve antigravity).</summary>
    private void CardChanged(string sessionId) => Changed();
    private void Permission(ToolPermissionRequest value)
    {
        // A remote pane's requests are its host's: never kept here, never answerable from the phone.
        if (service.IsRemoteSession(value.RunId)) return;
        lock (sync) { if (value.State == "pending") permissions[value.Id] = value; else permissions.Remove(value.Id); }
        Changed();
    }
    private ToolPermissionRequest[] Pending(string id) { lock (sync) return permissions.Values.Where(p => p.RunId == id).ToArray(); }
    /// <summary>The pane's tool, question and plan requests waiting for an answer, as the phone sees them (the remote mirror reads them too, RemoteMirrorDesktopHost).</summary>
    public IReadOnlyList<ToolPermissionRequest> PendingRequests(string id) => Pending(id).OrderBy(p => p.ReceivedAt, StringComparer.Ordinal).ToArray();
    /// <summary>
    /// A send into one of this PC's agent panes with its files already here (the remote mirror's <c>submit</c>, docs/remote-workspaces.md):
    /// the phone's submit after its uploads, through the same desktop action and slots. The desktop's answer (a <see cref="SubmitReceipt"/>).
    /// </summary>
    public async Task<object?> SubmitInlineAsync(string sessionId, string text, IReadOnlyList<RunAttachment> attachments, CancellationToken token)
    {
        var pane = Session(sessionId); Agent(pane);
        var body = JsonSerializer.SerializeToElement(new { text }, Wire.Json);
        await submitSlots.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await action(new(pane.Id, "submit", body, attachments.Count == 0 ? null : attachments), token);
            Changed(); return result;
        }
        finally { submitSlots.Release(); }
    }
    private RunSession Session(string id)
    {
        var snapshot = service.Snapshot;
        return RemoteWorkspacePhone.Sessions(snapshot.Sessions, snapshot.Workspaces).FirstOrDefault(p => p.Id == id && p.Kind is not ("files" or "workmap")) ?? throw new MobileRequestException(404, "Session not found.");
    }
    private static void Agent(RunSession pane) { if (pane.Kind != "claude") throw new MobileRequestException(409, "Use the desktop for this pane."); }
    private static object Error(int status, string text, string? code = null) => new { protocol = 1, error = text, code };
    private static MobileReply Reply(object body, int status = 200)
        => JsonSerializer.SerializeToUtf8Bytes(body, Wire.Json).Length <= MaximumReplyBytes ? new(status, body) : new(413, Error(413, "Response exceeds the mobile display limit."));
    /// <summary>The phone's work map: the model without each commit's file list (its count stays), as docs/work-map.md says.</summary>
    internal static object PhoneWorkMap(WorkMapModel model)
    {
        var node = JsonSerializer.SerializeToNode(model, WorkMap.Json)!.AsObject();
        foreach (var version in node["versions"]!.AsArray())
            foreach (var commit in version!["commits"]!.AsArray()) commit!.AsObject().Remove("files");
        node["protocol"] = 1;
        return node;
    }
    private static object OK() => new { protocol = 1, ok = true };
    /// <summary>
    /// A submit's or guided action's reply (natural submit B5): a <see cref="SubmitReceipt"/> answers with the outcome word in
    /// <c>accepted</c> (older phones read it), <c>outcome</c>, and the request's ids when it started at once; any other answer as before.
    /// </summary>
    private static object SubmitReply(object? result) => result is SubmitReceipt receipt ? receipt.MobileReply() : new { protocol = 1, accepted = result };
    private static JsonElement Object(JsonElement? value, int maximum = 65536)
    {
        if (value is not { ValueKind: JsonValueKind.Object } body || Encoding.UTF8.GetByteCount(body.GetRawText()) > maximum) throw new MobileRequestException(400, "Invalid request body.");
        return body;
    }
    private static string Required(JsonElement body, string key) => body.Text(key) ?? throw new MobileRequestException(400, "Missing " + key + ".");
    private static string RequestText(JsonElement body, bool allowEmpty)
    {
        var text = Required(body, "text");
        if ((!allowEmpty && string.IsNullOrWhiteSpace(text)) || Encoding.UTF8.GetByteCount(text) > 32768 || text.Contains('\0')) throw new MobileRequestException(400, "Invalid request text.");
        return text;
    }
    public async Task<MobileReply> RouteAsync(string method, string path, JsonElement? body, string device, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (!path.StartsWith("/m1/", StringComparison.Ordinal) || path.Contains('#') || path.Contains('\\') || !Uri.TryCreate("http://mobile.invalid" + path, UriKind.Absolute, out var url) || url.Host != "mobile.invalid") throw new MobileRequestException(404, "Mobile route not found.");
            var route = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
            var query = new Dictionary<string, string>();
            foreach (var item in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            { var pair = item.Split('=', 2); if (!query.TryAdd(Uri.UnescapeDataString(pair[0]), Uri.UnescapeDataString(pair.Length == 2 ? pair[1] : ""))) throw new MobileRequestException(400, "Repeated query argument."); }
            if (method == "POST" && query.Count != 0) throw new MobileRequestException(400, "Unexpected query argument.");
            // Always the authenticated device, never a body field (M/MobileRemoteService.swift push routes).
            if (method == "POST" && route.Length == 3 && route[1] == "push" && Push is { } push)
            {
                switch (route[2])
                {
                    case "register":
                        var value = Object(body);
                        var handle = Required(value, "handle");
                        if (!MobilePushWire.ValidHandle(handle)) throw new MobileRequestException(400, Locale.Get("remote.error.badPushHandle"));
                        if (!value.TryGetProperty("kinds", out var list) || list.ValueKind != JsonValueKind.Array) throw new MobileRequestException(400, Locale.Get("remote.error.badBody"));
                        var kinds = MobilePushWire.Kinds(list.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.String).Select(k => k.GetString()!));
                        return push.Register(device, handle, kinds) switch
                        {
                            MobilePushRegistrationResult.Saved => Reply(OK()),
                            MobilePushRegistrationResult.NeedsToken => throw new MobileRequestException(409, Locale.Get("remote.error.pushNeedsToken")),
                            _ => throw new MobileRequestException(503, Locale.Get("mobileRemote.device.saveFailed")),
                        };
                    case "unregister":
                        if (!push.Unregister(device)) throw new MobileRequestException(503, Locale.Get("mobileRemote.device.saveFailed"));
                        return Reply(OK());
                    case "viewing":
                        // No body, no sessionId and sessionId: null all clear the view.
                        if (body is null or { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null } || !Object(body).TryGetProperty("sessionId", out var session) || session.ValueKind == JsonValueKind.Null) push.View(device, null);
                        else if (session.ValueKind == JsonValueKind.String && session.GetString() is { } shown && Wire.Identifier(shown)) push.View(device, shown);
                        else throw new MobileRequestException(404, Locale.Get("remote.error.paneNotFound"));
                        return Reply(OK());
                }
            }
            if (method == "GET" && route.SequenceEqual(new[] { "m1", "info" })) return Reply(new { protocol = 1, hostId = HostId, hostName = HostName, appVersion = AppVersion, platform = "win32", capabilities = Capabilities.Concat(ScreenShareAvailability.Extra(ExtraCapabilities, ScreenShareAvailable)).Distinct().ToArray(), direct = DirectEndpoints?.Invoke() ?? [] });
            if (method == "GET" && (route.SequenceEqual(new[] { "m1", "state" }) || route.Length == 3 && route[1] == "sessions"))
            {
                // `structure` (the tag of the structure the phone holds, 0 for none) and `roles` (1: the phone draws the role view, MobileFlow) are
                // taken only by the session detail.
                if (query.Keys.Any(k => k is not ("since" or "wait") && !(k is "structure" or "roles" && route[1] == "sessions"))) throw new MobileRequestException(400, "Unexpected poll argument.");
                var since = Number(query, "since", 0, long.MaxValue, 0); var wait = Number(query, "wait", 0, 10, 0);
                long? asked = query.ContainsKey("structure") ? Number(query, "structure", 0, 999_999_999_999, 0) : null;
                var roles = query.ContainsKey("roles") && Number(query, "roles", 0, 999_999_999_999, 0) > 0;
                // Each scope waits on its own revision: the list on the state's, a pane on that pane's.
                var state = route[1] == "state"; if (!state) _ = Session(route[2]);
                long Current() { Observe(); return state ? StateRevision : SessionRevision(route[2]); }
                if (Current() <= since && wait > 0) { var until = DateTime.UtcNow.AddSeconds(wait); while (Current() <= since && DateTime.UtcNow < until) await Task.Delay(100, token); }
                token.ThrowIfCancellationRequested(); return state ? Reply(State()) : SessionReply(Session(route[2]), asked, roles);
            }
            if (method == "POST" && route.SequenceEqual(new[] { "m1", "app-update" }) && query.Count == 0)
            {
                var value = Object(body); var install = value.Text("action") == "install";
                if (!install && value.Text("action") != "check") throw new MobileRequestException(400, Locale.Get("appUpdate.mobile.badAction"));
                var state = AppUpdatePhone();
                if (state is null || AppUpdateAct is null) throw new MobileRequestException(409, Locale.Get("appUpdate.mobile.unavailable"));
                if (install && state.Running > 0 && !(value.TryGetProperty("confirm", out var confirm) && confirm.ValueKind == JsonValueKind.True))
                    throw new MobileRequestException(409, Locale.Get("appUpdate.mobile.runningRequests", new Dictionary<string, string> { ["count"] = state.Running.ToString() }));
                await AppUpdateAct(install);
                return Reply(AppUpdatePhone() ?? state);
            }
            if (route.Length == 3 && route[1] == "screen-share") { if (ScreenShareAvailable && ScreenRoute is not null) return await ScreenRoute(method, route[2], body, device, token); throw new MobileRequestException(503, "Screen sharing is not available on this host."); }
            if (route.Length is 4 or 5 && route[1] == "workspaces" && route[3] == "workmap")
            {
                // Only this PC's workspaces: a remote one's history is on its host.
                var mapped = RemoteWorkspacePhone.Workspaces(service.Snapshot.Workspaces).FirstOrDefault(w => w.Id == route[2]);
                if (mapped is null || WorkMapRead is null || WorkMapSummarize is null) throw new MobileRequestException(404, "Workspace not found.");
                if (method == "GET" && route.Length == 4 && query.Count == 0) return Reply(PhoneWorkMap(await WorkMapRead(mapped)));
                if (method == "POST" && route.Length == 5 && route[4] == "summarize")
                    return await WorkMapSummarize(mapped, Required(Object(body), "key")) switch
                    {
                        WorkMapStart.Started => Reply(OK(), 202),
                        WorkMapStart.Busy => throw new MobileRequestException(409, Locale.Get("workMap.error.busy")),
                        WorkMapStart.UnknownKey => throw new MobileRequestException(400, "Unknown version."),
                        _ => throw new MobileRequestException(404, "Workspace not found."),
                    };
            }
            if (route.Length == 4 && route[1] == "workspaces")
            {
                var files = method == "GET" && route[3] is "files" or "file";
                // Only this PC's workspaces: a remote one's path is its host's, never a folder here (M/AppStore+MobileRemote.swift createPane).
                var workspace = RemoteWorkspacePhone.Workspaces(service.Snapshot.Workspaces).FirstOrDefault(w => w.Id == route[2]) ?? throw (files ? MobileWorkspaceFiles.WorkspaceNotFound : new MobileRequestException(404, "Workspace not found."));
                if (method == "POST" && route[3] == "sessions")
                {
                    var value = Object(body); if (Required(value, "kind") != "claude") throw new MobileRequestException(409, "Create terminal panes on the desktop.");
                    var provider = value.Text("provider") ?? "claude"; if (!Wire.Providers.Contains(provider)) throw new MobileRequestException(400, "Unknown provider.");
                    var created = await action(new(workspace.Id, "create", value), token); Changed(); return Reply(new { protocol = 1, sessionId = created }, 201);
                }
                if (files)
                {
                    // "fileChanges": the preview's changes=1, with the pane and run whose originals count, each optional.
                    var changes = route[3] == "file" && query.TryGetValue("changes", out var flag) ? flag == "1" ? true : throw new MobileRequestException(400, "Unexpected file argument.") : false;
                    if (query.Keys.Any(k => k != "path" && !(changes && k is "changes" or "session" or "run"))) throw new MobileRequestException(400, "Unexpected file argument.");
                    if (query.TryGetValue("session", out var paneId) && !Wire.Identifier(paneId) || query.TryGetValue("run", out var runId) && !Wire.Identifier(runId)) throw new MobileRequestException(400, "Unexpected file argument.");
                    return await Files(workspace, MobileWorkspaceFiles.ValidatedPath(query.GetValueOrDefault("path"), route[3] == "files"), route[3] == "files", device, token,
                        changes ? new MobileFileChangesQuery(query.GetValueOrDefault("session"), query.GetValueOrDefault("run")) : null);
                }
            }
            if (route.Length >= 4 && route[1] == "uploads" && method == "POST")
            {
                if (route.Length == 5 && route[3] == "chunks" && int.TryParse(route[4], out var index))
                {
                    var value = Object(body, 300 * 1024); var data = Convert.FromBase64String(Required(value, "dataBase64"));
                    if (data.Length > MobileUploads.ChunkSize) throw new MobileRequestException(413, "Chunk exceeds limit.");
                    return Reply(new { protocol = 1, ok = true, received = uploads.Append(route[2], device, index, data) });
                }
                if (route.Length == 4 && route[3] == "complete") return Reply(new { protocol = 1, attachment = uploads.Complete(route[2], device) });
                if (route.Length == 4 && route[3] == "cancel") { uploads.Cancel(route[2], device); return Reply(OK()); }
            }
            if (route.Length >= 4 && route[1] == "sessions")
            {
                var pane = Session(route[2]);
                if (method == "GET" && route.Length == 4 && route[3] == "entries")
                {
                    if (query.Keys.Any(k => k is not ("before" or "limit"))) throw new MobileRequestException(400, "Unexpected history argument.");
                    var limit = (int)Number(query, "limit", 1, 100, 50); var entries = pane.Logs;
                    var page = query.GetValueOrDefault("before") is { } before ? MobileRemoteSupport.Page(entries, before, limit) : (entries.TakeLast(limit).ToArray(), entries.Count > limit);
                    return Reply(new { protocol = 1, entries = MobileRemoteSupport.PhoneEntries(page.Entries), hasMore = page.HasMore });
                }
                Agent(pane);
                if (method == "GET" && route.Length == 4 && route[3] == "commands" && query.Count == 0)
                {
                    var workspace = service.Snapshot.Workspaces.First(w => w.Id == pane.WorkspaceId);
                    var commands = SlashCommandCatalog.Commands(pane.Provider, workspace.Path).Where(c => c.Action is not (SlashCommandAction.OpenPlugins or SlashCommandAction.OpenSettings or SlashCommandAction.SetModel or SlashCommandAction.SetPermission)).Select(c => new { name = c.Invocation, description = c.Description, source = c.Origin.ToString().ToLowerInvariant(), argumentHint = MobileRemoteSupport.ArgumentHint(c.Argument), action = c.Action switch { SlashCommandAction.NewConversation => "clear", SlashCommandAction.ShowUsage => "usage", SlashCommandAction.Help => "help", SlashCommandAction.Rename => "rename", _ => c.Argument?.ToString().ToLowerInvariant() } });
                    return Reply(new { protocol = 1, commands });
                }
                if (method == "POST" && route.Length == 4)
                {
                    var operation = route[3];
                    if (operation == "permission")
                    {
                        // An Antigravity card is answered here too (docs/mobile-remote.md, "Antigravity 권한 카드"): allow alone is
                        // 이번만 허용, with always 항상 허용; always never comes without allow, and only on such a card.
                        var value = Object(body); var requestId = Required(value, "requestId"); var runId = Required(value, "runId");
                        if (!Wire.Identifier(requestId) || !Wire.Identifier(runId)) throw new MobileRequestException(400, Locale.Get("remote.error.badPermissionId"));
                        if (!value.TryGetProperty("allow", out var allow) || allow.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new MobileRequestException(400, "Missing permission choice.");
                        var always = !value.TryGetProperty("always", out var flag) ? false : flag.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False or JsonValueKind.Null => false, _ => throw new MobileRequestException(400, "Invalid permission choice.") };
                        if (always && !allow.GetBoolean()) throw new MobileRequestException(400, Locale.Get("remote.error.alwaysNeedsAllow"));
                        if (service.PendingAntigravityCard(pane.Id, requestId, runId) is not null)
                        {
                            token.ThrowIfCancellationRequested();
                            var decision = AntigravityPermissionFlow.Decision(allow.GetBoolean(), always);
                            // The pane answers as its own buttons do; what it says back is why nothing happened.
                            var answer = JsonSerializer.SerializeToElement(new { requestId, runId, decision = decision.ToString().ToLowerInvariant() });
                            if (await action(new(pane.Id, "antigravityPermission", answer), token) is string failure) throw new MobileRequestException(409, failure);
                            Changed(); return Reply(OK());
                        }
                        if (always) throw new MobileRequestException(409, Locale.Get("remote.error.notAntigravityPermission"));
                    }
                    if (operation == "permission" || operation == "answers")
                    {
                        var value = Object(body); var requestId = Required(value, "requestId"); var runId = Required(value, "runId");
                        var pending = Pending(pane.Id).FirstOrDefault(p => p.Id == requestId && p.RunId == runId);
                        if (pending is null || !service.IsSessionRunning(pane.Id)) throw new MobileRequestException(409, "Request is no longer pending.");
                        token.ThrowIfCancellationRequested();
                        if (operation == "permission") { if (!value.TryGetProperty("allow", out var allow) || allow.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new MobileRequestException(400, "Missing permission choice."); service.RespondToToolPermission(pane.Id, requestId, allow.GetBoolean()); }
                        else
                        {
                            if (!value.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object || answers.EnumerateObject().Count() > 16) throw new MobileRequestException(400, "Invalid answers.");
                            var parsed = JsonSerializer.Deserialize<Dictionary<string, UserQuestionAnswer>>(answers, Wire.Json) ?? throw new MobileRequestException(400, "Invalid answers.");
                            if (parsed.Values.Any(a => a is null || a.SelectedOptions is null)) throw new MobileRequestException(400, "Invalid answers.");
                            service.AnswerQuestionnaire(pane.Id, requestId, parsed);
                        }
                        Changed(); return Reply(OK());
                    }
                    if (operation == "plan")
                    {
                        // One of the plan card's four answers (macOS MobilePlanAnswer).
                        var value = Object(body); var requestId = Required(value, "requestId"); var runId = Required(value, "runId");
                        if (!Wire.Identifier(requestId) || !Wire.Identifier(runId)) throw new MobileRequestException(400, Locale.Get("plan.error.invalidRequest"));
                        var feedback = value.TryGetProperty("feedback", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
                        PlanDecision decision = (value.TryGetProperty("decision", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString() : null) switch
                        {
                            "approveAutoEdit" => PlanDecision.ApproveAutoEdit,
                            "approveConfirmEach" => PlanDecision.ApproveConfirmEach,
                            "revise" => PlanDecision.Revise(feedback ?? ""),
                            "cancel" => PlanDecision.Cancel,
                            _ => throw new MobileRequestException(400, Locale.Get("plan.error.invalidDecision")),
                        };
                        if (decision.Kind == "revise")
                        {
                            try { ClaudePlanMode.ValidatedFeedback(feedback); }
                            catch (ArgumentException ex) { throw new MobileRequestException(400, ex.Message); }
                        }
                        var pending = Pending(pane.Id).FirstOrDefault(p => p.Id == requestId && p.RunId == runId);
                        if (pending is null || !service.IsSessionRunning(pane.Id)) throw new MobileRequestException(409, "Request is no longer pending.");
                        if (!pending.CanAnswerPlan) throw new MobileRequestException(409, Locale.Get("plan.error.notPlan"));
                        token.ThrowIfCancellationRequested();
                        try { service.AnswerPlan(pane.Id, requestId, decision); }
                        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { throw new MobileRequestException(409, ex.Message); }
                        Changed(); return Reply(OK());
                    }
                    if (operation == "terminal-prompt")
                    {
                        // The answer to the card of a command waiting for input (docs/terminal-prompts.md); the text of a secret is passed on and never kept.
                        var value = Object(body);
                        var typed = TerminalPromptAnswer.FromPhone(value.Text("promptId"), value.Text("answer"), value.Text("text")) ?? throw new MobileRequestException(400, Locale.Get("terminalPrompt.error.badAnswer"));
                        var forwarded = JsonSerializer.SerializeToElement(new { promptId = value.Text("promptId"), answer = typed.Name, text = typed.Line });
                        if (await action(new(pane.Id, operation, forwarded), token) is not true) throw new MobileRequestException(409, Locale.Get("terminalPrompt.error.gone"));
                        Changed(); return Reply(OK());
                    }
                    if (operation == "uploads")
                    {
                        var value = Object(body); if (!value.TryGetProperty("size", out var size) || !size.TryGetInt32(out var count)) throw new MobileRequestException(400, "Invalid size.");
                        return Reply(uploads.Begin(pane.Id, device, Required(value, "name"), count), 201);
                    }
                    if (operation == "submit")
                    {
                        var value = Object(body); var ids = value.TryGetProperty("attachments", out var list) ? JsonSerializer.Deserialize<string[]>(list, Wire.Json) ?? [] : Array.Empty<string>();
                        _ = RequestText(value, ids.Length > 0); if (value.Text("mode") is { } mode && mode is not ("queue" or "steer")) throw new MobileRequestException(400, "Unknown submit mode.");
                        await submitSlots.WaitAsync(token); MobileUploadClaim? claim = null; var accepted = false;
                        try
                        {
                            token.ThrowIfCancellationRequested(); claim = uploads.Claim(ids, pane.Id, device);
                            var result = await action(new(pane.Id, operation, value, claim.Attachments), token); accepted = true; Changed(); return Reply(SubmitReply(result), 202);
                        }
                        finally { if (claim is not null) uploads.Finish(claim, accepted); submitSlots.Release(); }
                    }
                    if (operation == "setAskTarget")
                    {
                        // The pane's "send to" choice: a target id it offers, or null for Claude (docs/mobile-remote.md). Which ids it offers is the desktop's.
                        var value = Object(body);
                        if (value.TryGetProperty("target", out var target) && target.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new MobileRequestException(400, Locale.Get("remote.error.badBody"));
                        await action(new(pane.Id, operation, value), token); Changed(); return Reply(OK());
                    }
                    if (operation is "stop" or "rename" or "close" or "settings" or "command" or "guided")
                    {
                        var value = operation is "stop" or "close" ? JsonSerializer.SerializeToElement(new { }) : Object(body);
                        if (operation == "rename" && value.Text("titleMode") != "auto" && (value.Text("title")?.Trim() is not { Length: >= 1 and <= 80 } title || title.Any(char.IsControl))) throw new MobileRequestException(400, "Invalid pane title.");
                        if (operation == "command" && value.Text("action") is not ("clear" or "usage" or "help")) throw new MobileRequestException(400, "Unknown command.");
                        if (operation == "guided") _ = MobileRemoteSupport.GuidedRequest(value);
                        var result = await action(new(pane.Id, operation, value), token); Changed();
                        if (operation == "close") uploads.DiscardSession(pane.Id);
                        return operation switch { "stop" => Reply(new { protocol = 1, stopped = result }), "guided" => Reply(SubmitReply(result), 202), "command" => Reply(new { protocol = 1, ok = true, message = result }), _ => Reply(OK()) };
                    }
                }
                if (method == "POST" && route.Length == 5 && route[3] == "queue" && route[4] == "run-next")
                { await action(new(pane.Id, "queue-next", JsonSerializer.SerializeToElement(new { })), token); Changed(); return Reply(OK()); }
                // 바로 실행 on one row (M/MobileRemoteService.swift): "worktree" or "here" in ran; 404 for a row no longer queued, 409 for one that cannot run now.
                if (method == "POST" && route.Length == 6 && route[3] == "queue" && route[5] == "run-now")
                {
                    if (!Wire.Identifier(route[4])) throw new MobileRequestException(404, Locale.Get("remote.error.queueItemNotFound"));
                    var ran = await action(new(pane.Id, "queue-run-now", JsonSerializer.SerializeToElement(new { id = route[4] })), token); Changed();
                    return Reply(new { protocol = 1, ok = true, ran });
                }
                if (method == "POST" && route.Length == 6 && route[3] == "queue" && route[5] == "remove")
                {
                    // A row held for the pane is refused, and nothing changes (M/AppStore+MobileRemote.swift:676-684).
                    if (DelegationQueueRows.RemoveRefusal(route[4], service.HeldItems(pane.Id)) is { } refusal) throw new MobileRequestException(409, Locale.Get("queue.heldNotRemovable"), DelegationNames.Of(refusal));
                    await action(new(pane.Id, "queue-remove", JsonSerializer.SerializeToElement(new { id = route[4] })), token); Changed(); return Reply(OK());
                }
            }
            throw new MobileRequestException(404, "Mobile route not found.");
        }
        catch (MobileRequestException ex) { return new(ex.Status, Error(ex.Status, ex.Message, ex.Code)); }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException) { return new(400, Error(400, "Invalid mobile request.")); }
        catch (InvalidOperationException) { return new(409, Error(409, "The pane changed; refresh and try again.")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(403, Error(403, "The requested file is unavailable.")); }
    }
    private static long Number(Dictionary<string, string> query, string key, long minimum, long maximum, long fallback)
        => !query.TryGetValue(key, out var text) ? fallback : long.TryParse(text, out var number) && number >= minimum && number <= maximum ? number : throw new MobileRequestException(400, "Invalid " + key + ".");
    /// <summary>The app update as the phone sees it, with the requests running now; null when there is none.</summary>
    private MobileAppUpdate? AppUpdatePhone()
    {
        var snapshot = service.Snapshot;
        var running = RemoteWorkspacePhone.Sessions(snapshot.Sessions, snapshot.Workspaces).Count(s => s.Kind is not ("files" or "workmap") && (s.Status == "running" || service.IsSessionRunning(s.Id)));
        return PhoneAppUpdate.Map(AppUpdateRead?.Invoke(), AppVersion, running);
    }
    private object State()
    {
        var snapshot = service.Snapshot;
        return new { protocol = 1, revision = StateRevision, hostName = HostName, workspaces = RemoteWorkspacePhone.Workspaces(snapshot.Workspaces).Select(w => new { w.Id, w.Name, w.Path }),
            sessions = RemoteWorkspacePhone.Sessions(snapshot.Sessions, snapshot.Workspaces).Where(s => s.Kind is not ("files" or "workmap")).Select(s => Summary(s, SessionRevision(s.Id))).ToArray(), appUpdate = AppUpdatePhone() };
    }
    private object Summary(RunSession pane, long revision)
    {
        var pending = Pending(pane.Id); var tail = pane.Logs.LastOrDefault(e => e.Kind is "assistant" or "user" or "error" or "output");
        // A pane that is not an agent's has no view and no style; "plain" and "cli" would invite pickers the phone cannot use.
        var agent = pane.Kind == "claude"; var style = !agent ? null : ActiveStyle is null ? pane.MightyStyle : ActiveStyle(pane);
        return new { pane.Id, pane.WorkspaceId, pane.Title, pane.Kind, pane.Provider, pane.Model, pane.Status, revision, updatedAt = pane.Logs.LastOrDefault()?.Timestamp ?? pane.CreatedAt,
            preview = tail is null ? null : new { tail.Kind, text = Wire.Clean(tail.Text, 200) }, pendingPermissions = pending.Count(p => !p.CanAnswerQuestions) + (service.AntigravityCard(pane.Id) is null ? 0 : 1), pendingQuestions = pending.Count(p => p.CanAnswerQuestions), queued = QueueCount is { } count ? count(pane.Id) + service.HeldItems(pane.Id).Count : extra(pane).Queued.Count,
            pane.ResumeId, terminal = !agent, agentViewMode = agent ? MobileRemoteSupport.ViewMode(pane.AgentViewMode) : null, mightyStyle = agent ? MobileRemoteSupport.Style(style) : null, styleId = agent ? style ?? MobileWire.CliStyle : null, titleMode = pane.TitleMode ?? "auto",
            // The Mac's rules (M/MightyCode/AppStore+MobileRemote.swift mobileSummary): the model the composer's chip reads the
            // selection through, never a model reported for an earlier one. canSteer is always false since 0.4.35 (a busy pane only
            // queues), kept for older phones.
            resolvedModel = pane.Kind == "shell" ? null : ModelLabel.Resolution(pane.Model, Catalog?.Invoke(pane) ?? ProviderCatalog.Fallback(pane.Provider), ModelLabel.ReportedModel(pane)),
            canSteer = false };
    }
    /// <summary>
    /// The session detail as it goes out (M/MightyCore/Remote/MobileRemoteSupport.swift:59-72). The structure travels only to a
    /// phone that asked for it (<paramref name="asked"/> is the tag it holds) and only when it differs from what that phone
    /// holds; a detail that would pass <see cref="MaximumReplyBytes"/> sheds the structure first, keeping the tag so the phone
    /// knows it is behind.
    /// </summary>
    private MobileReply SessionReply(RunSession pane, long? asked, bool roles = false)
    {
        var detail = roles && RoleExtras is { } withRoles ? withRoles(pane) : extra(pane); var built = asked is null || detail.Mighty is null ? null : Structure?.Invoke(pane);
        var structure = built is not null && built.Tag != asked ? built.Value : null;
        var reply = Reply(Detail(pane, detail, structure, built?.Tag));
        return reply.Status == 413 && structure is not null ? Reply(Detail(pane, detail, null, built?.Tag)) : reply;
    }
    /// <summary>The Mighty payload with the structure fields a phone asked for: the tag always, the value when it travels.</summary>
    private static object? Mighty(object? mighty, MobileStructure? structure, long? tag)
    {
        if (mighty is null || tag is not { } current) return mighty;
        var value = JsonSerializer.SerializeToNode(mighty, Wire.Json)!.AsObject();
        if (structure is not null) value["structure"] = JsonSerializer.SerializeToNode(structure, Wire.Json);
        value["structureTag"] = current;
        return value;
    }
    private object Detail(RunSession pane, MobilePaneExtras detail, MobileStructure? structure, long? structureTag)
    {
        var usage = pane.SessionUsage; var revision = SessionRevision(pane.Id);
        return new { protocol = 1, revision, session = Summary(pane, revision), entries = MobileRemoteSupport.PhoneEntries(pane.Logs.TakeLast(MobileWire.MaximumEntries)), hasOlder = pane.Logs.Count > MobileWire.MaximumEntries,
            permissions = Pending(pane.Id).Select(MobilePermission.From).Concat(service.AntigravityCard(pane.Id) is { } card ? [MobilePermission.FromAntigravity(card)] : []).ToArray(), queued = detail.Queued,
            usage = usage is null ? null : new { usage.Model, usage.ContextUsedTokens, usage.ContextWindowTokens, usage.ContextPercent, usage.TotalTokens, usage.CostUSD }, elapsedSeconds = pane.RunTiming?.Elapsed(), settings = detail.Settings, mighty = Mighty(detail.Mighty, structure, structureTag), statusLine = detail.StatusLine,
            rateLimits = MobileRemoteSupport.RateLimits(usage?.RateLimits),
            // No targets: neither field is sent, so the phone draws no switch (M/MightyCore/Remote/MobileRemoteModels.swift MobileSessionDetail).
            askTargets = detail.AskTargets is { Count: > 0 } targets ? targets : null, askTarget = detail.AskTargets is { Count: > 0 } ? detail.AskTarget : null,
            // Running background work only, never an output file's path (M/MightyCore/Remote/MobileRemoteModels.swift MobileSessionDetail.backgroundWork).
            backgroundWork = detail.BackgroundWork is { Count: > 0 } work ? work : null,
            // How many of the strip's tasks ended, 0 included, while it shows (M/MightyCore/Remote/MobileRemoteModels.swift MobileSessionDetail.backgroundDone).
            backgroundDone = detail.BackgroundDone,
            // A command in the pane's terminal waiting for input, answered with POST …/terminal-prompt (docs/terminal-prompts.md); absent when none waits.
            terminalPrompt = detail.TerminalPrompt is { } prompt ? new { prompt.Id, prompt.Handle, prompt.Text, prompt.Options, prompt.Kind, prompt.DefaultYes } : null };
    }
    /// <summary>
    /// The read-only file routes (M/MightyCore/Remote/MobileRemoteService.swift:1048-1104). The folder and the file are read
    /// off the caller's thread, two at a time. Only an image's decode waits for the preview slot, one at a time, and a newer
    /// preview from the same phone takes an older one's place in that queue.
    /// </summary>
    private async Task<MobileReply> Files(Workspace workspace, string relative, bool listing, string device, CancellationToken token, MobileFileChangesQuery? changes = null)
    {
        token.ThrowIfCancellationRequested(); MobileWorkspaceFiles.Stage stage;
        await fileSlots.WaitAsync(token);
        try
        {
            if (listing) return Reply(MobileWorkspaceFiles.Fitted(await Task.Run(() => MobileWorkspaceFiles.Listing(workspace.Id, relative, workspace.Path), token)));
            // A pane of another workspace never lends this one its originals; a delegated child's file and its originals are read
            // in its worktree folder, where they were captured, so the full view and the changes are the same file.
            var pane = changes?.Session is { } id && service.Snapshot.Sessions.Any(s => s.Id == id && s.WorkspaceId == workspace.Id) ? id : null;
            var root = pane is not null && service.RunFolder(pane) is { } folder ? folder : workspace.Path;
            stage = await Task.Run(() => MobileWorkspaceFiles.Prepare(workspace.Id, relative, root), token);
            // Only text has changes, worked out in the same two slots.
            if (changes is not null && stage.Image is null && stage.Preview.Type is "source" or "markdown")
            {
                try { stage = stage with { Preview = stage.Preview with { Changes = await service.FileChangesAsync(root, relative, pane, pane is null ? null : service.OriginalsRun(pane, changes.Run), token) } }; }
                catch { stage.Dispose(); throw; }
            }
        }
        finally { fileSlots.Release(); }
        using (stage)
        {
            var preview = stage.Preview;
            if (stage.Image is not { } file) return Reply(MobileWorkspaceFiles.Fitted(preview));
            if (!await previews.Acquire(device, token)) throw MobileWorkspaceFiles.Superseded;
            try
            {
                var bytes = await Task.Run(() => MobileWorkspaceFiles.ImageBytes(file, token), token);
                if (bytes is null) return Reply(preview with { Reason = "tooLarge" });
                // An svg that would load something besides itself is never drawn.
                if (FilePreviewClassifier.FileExtension(preview.Name) == "svg" && FilePreviewClassifier.SvgLoadsExternalContent(bytes)) return Reply(preview with { Reason = "undecodable" });
                if (ImagePreview is null)
                {
                    // Without a decoder only a small picture the phone can draw as it is travels.
                    var mime = bytes.Length <= MobileWorkspaceFiles.MaximumThumbnailBytes ? AttachmentSupport.DetectMediaType(bytes) : null;
                    return Reply(mime is "image/png" or "image/jpeg" ? preview with { Type = "image", Mime = mime, Data = Convert.ToBase64String(bytes) } : preview with { Reason = "tooLarge" });
                }
                var drawn = await ImagePreview(preview.Name, bytes, token); token.ThrowIfCancellationRequested();
                return Reply(drawn.Image is { } image ? preview with { Type = "image", Mime = image.Mime, Width = image.Width, Height = image.Height, ThumbnailWidth = image.ThumbnailWidth, ThumbnailHeight = image.ThumbnailHeight, Data = image.Data } : preview with { Reason = drawn.Reason ?? "undecodable" });
            }
            finally { previews.Release(); }
        }
    }
    public void ResetUploads() => uploads.Clear();
    public void Dispose() { service.RunEventReceived -= Receive; service.ToolPermissionChanged -= Permission; service.DelegationChanged -= Changed; service.AntigravityCardChanged -= CardChanged; uploads.Dispose(); }
}
