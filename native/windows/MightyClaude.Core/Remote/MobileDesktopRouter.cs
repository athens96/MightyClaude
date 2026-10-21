using System.Text;
using System.Text.Json;

namespace MightyClaude.Core;

public sealed record MobileImagePreview(string Mime, string Data, int Width, int Height, int ThumbnailWidth, int ThumbnailHeight);
/// <summary>A decoded thumbnail, or why there is none: <c>tooLarge</c> or <c>undecodable</c>.</summary>
public sealed record MobileImageResult(MobileImagePreview? Image, string? Reason = null);
/// <summary>What the revision of a pane is read from beside its record (<see cref="MobileRevisions.Reading"/>).</summary>
public sealed record MobilePaneDigest(bool Editable, int Options, int Mighty);
public sealed record MobileQueuedItem(string Id, string Text);
public sealed record MobilePaneExtras(IReadOnlyList<MobileQueuedItem> Queued, object? Settings = null, object? Mighty = null, object? StatusLine = null);
public sealed record MobileDesktopAction(string SessionId, string Action, JsonElement Body, IReadOnlyList<RunAttachment>? Attachments = null);

/// The m1 contract used by the existing phone app. The caller dispatches routes
/// to the desktop UI actor so all mutating actions use its ordinary composer.
public sealed class MobileDesktopRouter : IDisposable
{
    public Func<string[]>? ExtraCapabilities { get; set; }
    public static readonly string[] Capabilities = ["submit-mode", "queue", "pane", "history", "settings", "commands", "mighty", "status", "attachments", "style", "files", "structure", "direct"];
    /// <summary>The direct path's endpoints for /m1/info (docs/mobile-remote.md, "direct"); empty when there is no host or it does not listen.</summary>
    public Func<IReadOnlyList<string>>? DirectEndpoints { get; set; }
    /// <summary>The largest reply body the host sends; a session detail over it sheds its structure first (<see cref="SessionReply"/>).</summary>
    public const int MaximumReplyBytes = MobileWire.MaximumReplyBytes;
    /// <summary>The pane's structure view for a phone that asked for it with <c>?structure=&lt;tag&gt;</c>; null for a pane with no Mighty payload.</summary>
    public Func<RunSession, MobileStructureCache.Built?>? Structure { get; set; }
    private readonly DesktopService service;
    private readonly Func<MobileDesktopAction, CancellationToken, Task<object?>> action;
    private readonly Func<RunSession, MobilePaneExtras> extra;
    private readonly MobileUploads uploads;
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
    /// <summary>The style a pane is really running, null for none: a stored style that is not approved, or a pane outside the Mighty view, answers "cli" (M/MightyClaude/AppStore+MobileRemote.swift:346-351).</summary>
    public Func<RunSession, string?>? ActiveStyle { get; set; }
    /// <summary>Draws an image file's thumbnail from its name and bytes.</summary>
    public Func<string, byte[], CancellationToken, Task<MobileImageResult>>? ImagePreview { get; set; }
    public Func<string, string, JsonElement?, string, CancellationToken, Task<MobileReply>>? ScreenRoute { get; set; }
    public Func<string, int>? QueueCount { get; set; }
    /// <summary>The model list this PC's CLI gave for the pane's provider, for the summary's <c>resolvedModel</c> (phone summaries cover local panes only, <see cref="RemoteWorkspacePhone"/>); null (or none) reads the provider's fallback list.</summary>
    public Func<RunSession, ModelCatalog?>? Catalog { get; set; }
    public string HostId { get; set; } = "";
    public string HostName { get; }
    public string AppVersion { get; init; } = "0.0.0";
    public MobileDesktopRouter(DesktopService service, string directory, string hostName,
        Func<MobileDesktopAction, CancellationToken, Task<object?>> action, Func<RunSession, MobilePaneExtras> extra)
    {
        this.service = service; this.action = action; this.extra = extra; HostName = hostName;
        uploads = new(Path.Combine(directory, "uploads")); service.RunEventReceived += Receive; service.ToolPermissionChanged += Permission; service.DelegationChanged += Changed;
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
            var readings = RemoteWorkspacePhone.Sessions(snapshot.Sessions, snapshot.Workspaces).Where(s => s.Kind != "files").Select(pane =>
            {
                MobilePaneDigest digest;
                try { digest = Digest?.Invoke(pane) ?? Unread(pane); } catch (Exception ex) when (ex is not OutOfMemoryException) { digest = Unread(pane); }
                // The held rows by id, so one swapped for another moves the pane even when their count stays.
                return new MobileRevisions.Reading(pane, digest.Editable, digest.Options, digest.Mighty, JsonSerializer.Serialize(Summary(pane, 0), Wire.Json), string.Join('\n', service.HeldItems(pane.Id).Select(item => item.Id)));
            }).ToArray();
            lock (sync) foreach (var note in revisions.Observe(RemoteWorkspacePhone.Workspaces(snapshot.Workspaces), readings)) { notifications.RemoveAll(n => n.Scope == note.Scope); notifications.Add(note); }
        }
        catch { Changed(); throw; }
    }
    /// <summary>A pane's digest from its record alone: no pickers to compare, and the Mighty identity its transcript gives.</summary>
    private static MobilePaneDigest Unread(RunSession pane) => new(pane.Status != "running", 0, pane.Kind == "claude" ? MobileMightySupport.Digest(pane) : 0);
    /// <summary>The scopes whose revision moved since the last call, <c>state</c> before the sessions, each with its newest revision.</summary>
    public IReadOnlyList<(string Scope, long Revision)> TakeNotifications() { lock (sync) { var taken = notifications.ToArray(); notifications.Clear(); return taken; } }
    private void Receive(RunEvent value) => Changed();
    private void Permission(ToolPermissionRequest value)
    {
        // A remote pane's requests are its host's: never kept here, never answerable from the phone.
        if (service.IsRemoteSession(value.RunId)) return;
        lock (sync) { if (value.State == "pending") permissions[value.Id] = value; else permissions.Remove(value.Id); }
        Changed();
    }
    private ToolPermissionRequest[] Pending(string id) { lock (sync) return permissions.Values.Where(p => p.RunId == id).ToArray(); }
    private RunSession Session(string id)
    {
        var snapshot = service.Snapshot;
        return RemoteWorkspacePhone.Sessions(snapshot.Sessions, snapshot.Workspaces).FirstOrDefault(p => p.Id == id && p.Kind != "files") ?? throw new MobileRequestException(404, "Session not found.");
    }
    private static void Agent(RunSession pane) { if (pane.Kind != "claude") throw new MobileRequestException(409, "Use the desktop for this pane."); }
    private static object Error(int status, string text, string? code = null) => new { protocol = 1, error = text, code };
    private static MobileReply Reply(object body, int status = 200)
        => JsonSerializer.SerializeToUtf8Bytes(body, Wire.Json).Length <= MaximumReplyBytes ? new(status, body) : new(413, Error(413, "Response exceeds the mobile display limit."));
    private static object OK() => new { protocol = 1, ok = true };
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
            if (method == "GET" && route.SequenceEqual(new[] { "m1", "info" })) return Reply(new { protocol = 1, hostId = HostId, hostName = HostName, appVersion = AppVersion, platform = "win32", capabilities = Capabilities.Concat(ExtraCapabilities?.Invoke() ?? []).Distinct().ToArray(), direct = DirectEndpoints?.Invoke() ?? [] });
            if (method == "GET" && (route.SequenceEqual(new[] { "m1", "state" }) || route.Length == 3 && route[1] == "sessions"))
            {
                // `structure` (the tag of the structure the phone holds, 0 for none) is taken only by the session detail.
                if (query.Keys.Any(k => k is not ("since" or "wait") && !(k == "structure" && route[1] == "sessions"))) throw new MobileRequestException(400, "Unexpected poll argument.");
                var since = Number(query, "since", 0, long.MaxValue, 0); var wait = Number(query, "wait", 0, 10, 0);
                long? asked = query.ContainsKey("structure") ? Number(query, "structure", 0, 999_999_999_999, 0) : null;
                // Each scope waits on its own revision: the list on the state's, a pane on that pane's.
                var state = route[1] == "state"; if (!state) _ = Session(route[2]);
                long Current() { Observe(); return state ? StateRevision : SessionRevision(route[2]); }
                if (Current() <= since && wait > 0) { var until = DateTime.UtcNow.AddSeconds(wait); while (Current() <= since && DateTime.UtcNow < until) await Task.Delay(100, token); }
                token.ThrowIfCancellationRequested(); return state ? Reply(State()) : SessionReply(Session(route[2]), asked);
            }
            if (route.Length == 3 && route[1] == "screen-share") { if (ScreenRoute is not null) return await ScreenRoute(method, route[2], body, device, token); throw new MobileRequestException(503, "Screen sharing is not available on this host."); }
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
                    if (query.Keys.Any(k => k != "path")) throw new MobileRequestException(400, "Unexpected file argument.");
                    return await Files(workspace, MobileWorkspaceFiles.ValidatedPath(query.GetValueOrDefault("path"), route[3] == "files"), route[3] == "files", device, token);
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
                            var result = await action(new(pane.Id, operation, value, claim.Attachments), token); accepted = true; Changed(); return Reply(new { protocol = 1, accepted = result }, 202);
                        }
                        finally { if (claim is not null) uploads.Finish(claim, accepted); submitSlots.Release(); }
                    }
                    if (operation is "stop" or "rename" or "close" or "settings" or "command" or "guided")
                    {
                        var value = operation is "stop" or "close" ? JsonSerializer.SerializeToElement(new { }) : Object(body);
                        if (operation == "rename" && value.Text("titleMode") != "auto" && (value.Text("title")?.Trim() is not { Length: >= 1 and <= 80 } title || title.Any(char.IsControl))) throw new MobileRequestException(400, "Invalid pane title.");
                        if (operation == "command" && value.Text("action") is not ("clear" or "usage" or "help")) throw new MobileRequestException(400, "Unknown command.");
                        if (operation == "guided") _ = MobileRemoteSupport.GuidedRequest(value);
                        var result = await action(new(pane.Id, operation, value), token); Changed();
                        if (operation == "close") uploads.DiscardSession(pane.Id);
                        return operation switch { "stop" => Reply(new { protocol = 1, stopped = result }), "guided" => Reply(new { protocol = 1, accepted = result }, 202), "command" => Reply(new { protocol = 1, ok = true, message = result }), _ => Reply(OK()) };
                    }
                }
                if (method == "POST" && route.Length == 5 && route[3] == "queue" && route[4] == "run-next")
                { await action(new(pane.Id, "queue-next", JsonSerializer.SerializeToElement(new { })), token); Changed(); return Reply(OK()); }
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
    private object State()
    {
        var snapshot = service.Snapshot;
        return new { protocol = 1, revision = StateRevision, hostName = HostName, workspaces = RemoteWorkspacePhone.Workspaces(snapshot.Workspaces).Select(w => new { w.Id, w.Name, w.Path }),
            sessions = RemoteWorkspacePhone.Sessions(snapshot.Sessions, snapshot.Workspaces).Where(s => s.Kind != "files").Select(s => Summary(s, SessionRevision(s.Id))).ToArray() };
    }
    private object Summary(RunSession pane, long revision)
    {
        var pending = Pending(pane.Id); var tail = pane.Logs.LastOrDefault(e => e.Kind is "assistant" or "user" or "error" or "output");
        // A pane that is not an agent's has no view and no style; "plain" and "cli" would invite pickers the phone cannot use.
        var agent = pane.Kind == "claude"; var style = !agent ? null : ActiveStyle is null ? pane.MightyStyle : ActiveStyle(pane);
        return new { pane.Id, pane.WorkspaceId, pane.Title, pane.Kind, pane.Provider, pane.Model, pane.Status, revision, updatedAt = pane.Logs.LastOrDefault()?.Timestamp ?? pane.CreatedAt,
            preview = tail is null ? null : new { tail.Kind, text = Wire.Clean(tail.Text, 200) }, pendingPermissions = pending.Count(p => !p.CanAnswerQuestions), pendingQuestions = pending.Count(p => p.CanAnswerQuestions), queued = QueueCount is { } count ? count(pane.Id) + service.HeldItems(pane.Id).Count : extra(pane).Queued.Count,
            pane.ResumeId, terminal = !agent, agentViewMode = agent ? MobileRemoteSupport.ViewMode(pane.AgentViewMode) : null, mightyStyle = agent ? MobileRemoteSupport.Style(style) : null, styleId = agent ? style ?? MobileWire.CliStyle : null, titleMode = pane.TitleMode ?? "auto",
            // The Mac's rules (M/MightyClaude/AppStore+MobileRemote.swift mobileSummary): the model the composer's chip reads the
            // selection through, never a model reported for an earlier one; and steer only where the desktop composer steers.
            resolvedModel = pane.Kind == "shell" ? null : ModelLabel.Resolution(pane.Model, Catalog?.Invoke(pane) ?? ProviderCatalog.Fallback(pane.Provider), ModelLabel.ReportedModel(pane)),
            canSteer = DelegationProviders.CanSteer(pane.Kind, pane.Provider) };
    }
    /// <summary>
    /// The session detail as it goes out (M/MightyCore/Remote/MobileRemoteSupport.swift:59-72). The structure travels only to a
    /// phone that asked for it (<paramref name="asked"/> is the tag it holds) and only when it differs from what that phone
    /// holds; a detail that would pass <see cref="MaximumReplyBytes"/> sheds the structure first, keeping the tag so the phone
    /// knows it is behind.
    /// </summary>
    private MobileReply SessionReply(RunSession pane, long? asked)
    {
        var detail = extra(pane); var built = asked is null || detail.Mighty is null ? null : Structure?.Invoke(pane);
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
            permissions = Pending(pane.Id).Select(MobilePermission.From).ToArray(), queued = detail.Queued,
            usage = usage is null ? null : new { usage.Model, usage.ContextUsedTokens, usage.ContextWindowTokens, usage.ContextPercent, usage.TotalTokens, usage.CostUSD }, elapsedSeconds = pane.RunTiming?.Elapsed(), settings = detail.Settings, mighty = Mighty(detail.Mighty, structure, structureTag), statusLine = detail.StatusLine,
            rateLimits = MobileRemoteSupport.RateLimits(usage?.RateLimits) };
    }
    /// <summary>
    /// The read-only file routes (M/MightyCore/Remote/MobileRemoteService.swift:1048-1104). The folder and the file are read
    /// off the caller's thread, two at a time. Only an image's decode waits for the preview slot, one at a time, and a newer
    /// preview from the same phone takes an older one's place in that queue.
    /// </summary>
    private async Task<MobileReply> Files(Workspace workspace, string relative, bool listing, string device, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); MobileWorkspaceFiles.Stage stage;
        await fileSlots.WaitAsync(token);
        try
        {
            if (listing) return Reply(MobileWorkspaceFiles.Fitted(await Task.Run(() => MobileWorkspaceFiles.Listing(workspace.Id, relative, workspace.Path), token)));
            stage = await Task.Run(() => MobileWorkspaceFiles.Prepare(workspace.Id, relative, workspace.Path), token);
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
    public void Dispose() { service.RunEventReceived -= Receive; service.ToolPermissionChanged -= Permission; service.DelegationChanged -= Changed; uploads.Dispose(); }
}
