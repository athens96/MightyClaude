using System.Text;
using System.Text.Json;

namespace MightyClaude.Core;

public sealed record MobileImagePreview(string Mime, string Data, int Width, int Height, int ThumbnailWidth, int ThumbnailHeight);
public sealed record MobileQueuedItem(string Id, string Text);
public sealed record MobilePaneExtras(IReadOnlyList<MobileQueuedItem> Queued, object? Settings = null, object? Mighty = null, object? StatusLine = null);
public sealed record MobileDesktopAction(string SessionId, string Action, JsonElement Body, IReadOnlyList<RunAttachment>? Attachments = null);

/// The m1 contract used by the existing phone app. The caller dispatches routes
/// to the desktop UI actor so all mutating actions use its ordinary composer.
public sealed class MobileDesktopRouter : IDisposable
{
    public Func<string[]>? ExtraCapabilities { get; set; }
    public static readonly string[] Capabilities = ["submit-mode", "queue", "pane", "history", "settings", "commands", "mighty", "status", "attachments", "style", "files"];
    private readonly DesktopService service;
    private readonly Func<MobileDesktopAction, CancellationToken, Task<object?>> action;
    private readonly Func<RunSession, MobilePaneExtras> extra;
    private readonly MobileUploads uploads;
    private readonly object sync = new();
    private readonly Dictionary<string, ToolPermissionRequest> permissions = [];
    private readonly SemaphoreSlim submitSlots = new(2), fileSlots = new(2);
    private long revision = 1;
    public long Revision => Interlocked.Read(ref revision);
    public Func<FilePreviewData, CancellationToken, Task<MobileImagePreview?>>? ImagePreview { get; set; }
    public Func<string, string, JsonElement?, string, CancellationToken, Task<MobileReply>>? ScreenRoute { get; set; }
    public Func<string, int>? QueueCount { get; set; }
    public string HostId { get; set; } = "";
    public string HostName { get; }
    public string AppVersion { get; init; } = "0.0.0";
    public MobileDesktopRouter(DesktopService service, string directory, string hostName,
        Func<MobileDesktopAction, CancellationToken, Task<object?>> action, Func<RunSession, MobilePaneExtras> extra)
    {
        this.service = service; this.action = action; this.extra = extra; HostName = hostName;
        uploads = new(Path.Combine(directory, "uploads")); service.RunEventReceived += Receive; service.ToolPermissionChanged += Permission;
    }
    public void Changed() => Interlocked.Increment(ref revision);
    private void Receive(RunEvent value) => Changed();
    private void Permission(ToolPermissionRequest value)
    { lock (sync) { if (value.State == "pending") permissions[value.Id] = value; else permissions.Remove(value.Id); } Changed(); }
    private ToolPermissionRequest[] Pending(string id) { lock (sync) return permissions.Values.Where(p => p.RunId == id).ToArray(); }
    private RunSession Session(string id) => service.Snapshot.Sessions.FirstOrDefault(p => p.Id == id && p.Kind != "files") ?? throw new MobileRequestException(404, "Session not found.");
    private static void Agent(RunSession pane) { if (pane.Kind != "claude") throw new MobileRequestException(409, "Use the desktop for this pane."); }
    private static object Error(int status, string text) => new { protocol = 1, error = text };
    private static MobileReply Reply(object body, int status = 200)
        => JsonSerializer.SerializeToUtf8Bytes(body, Wire.Json).Length <= 768 * 1024 ? new(status, body) : new(413, Error(413, "Response exceeds the mobile display limit."));
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
            if (method == "GET" && route.SequenceEqual(new[] { "m1", "info" })) return Reply(new { protocol = 1, hostId = HostId, hostName = HostName, appVersion = AppVersion, platform = "win32", capabilities = Capabilities.Concat(ExtraCapabilities?.Invoke() ?? []).Distinct().ToArray() });
            if (method == "GET" && (route.SequenceEqual(new[] { "m1", "state" }) || route.Length == 3 && route[1] == "sessions"))
            {
                if (query.Keys.Any(k => k is not ("since" or "wait"))) throw new MobileRequestException(400, "Unexpected poll argument.");
                var since = Number(query, "since", 0, long.MaxValue, 0); var wait = Number(query, "wait", 0, 10, 0);
                if (Revision <= since && wait > 0) { var until = DateTime.UtcNow.AddSeconds(wait); while (Revision <= since && DateTime.UtcNow < until) await Task.Delay(100, token); }
                token.ThrowIfCancellationRequested(); return Reply(route[1] == "state" ? State() : Detail(Session(route[2])));
            }
            if (route.Length == 3 && route[1] == "screen-share") { if (ScreenRoute is not null) return await ScreenRoute(method, route[2], body, device, token); throw new MobileRequestException(503, "Screen sharing is not available on this host."); }
            if (route.Length == 4 && route[1] == "workspaces")
            {
                var workspace = service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == route[2]) ?? throw new MobileRequestException(404, "Workspace not found.");
                if (method == "POST" && route[3] == "sessions")
                {
                    var value = Object(body); if (Required(value, "kind") != "claude") throw new MobileRequestException(409, "Create terminal panes on the desktop.");
                    var provider = value.Text("provider") ?? "claude"; if (!Wire.Providers.Contains(provider)) throw new MobileRequestException(400, "Unknown provider.");
                    var created = await action(new(workspace.Id, "create", value), token); Changed(); return Reply(new { protocol = 1, sessionId = created }, 201);
                }
                if (method == "GET" && route[3] is "files" or "file")
                {
                    if (query.Keys.Any(k => k != "path")) throw new MobileRequestException(400, "Unexpected file argument.");
                    var relative = query.GetValueOrDefault("path", "");
                    if (relative.Split('/').Any(p => p == "..") || relative.Contains('\\') || relative.Contains(':') || Encoding.UTF8.GetByteCount(relative) > 4096 || relative.StartsWith('/')) throw new MobileRequestException(403, "Path is outside the workspace.");
                    await fileSlots.WaitAsync(token); try { return Reply(await Files(workspace, relative, route[3] == "files", token)); } finally { fileSlots.Release(); }
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
                    var limit = (int)Number(query, "limit", 1, 100, 50); var entries = pane.Logs; var before = query.GetValueOrDefault("before"); var end = before is null ? entries.Count : entries.FindIndex(e => e.Id == before);
                    var start = Math.Max(0, end - limit); return Reply(new { protocol = 1, entries = end < 0 ? [] : entries.Skip(start).Take(end - start).ToArray(), hasMore = start > 0 });
                }
                Agent(pane);
                if (method == "GET" && route.Length == 4 && route[3] == "commands" && query.Count == 0)
                {
                    var workspace = service.Snapshot.Workspaces.First(w => w.Id == pane.WorkspaceId);
                    var commands = SlashCommandCatalog.Commands(pane.Provider, workspace.Path).Where(c => c.Action is not (SlashCommandAction.OpenPlugins or SlashCommandAction.OpenSettings or SlashCommandAction.SetModel or SlashCommandAction.SetPermission)).Select(c => new { name = c.Invocation, description = c.Description, source = c.Origin.ToString().ToLowerInvariant(), argumentHint = c.Argument?.ToString().ToLowerInvariant(), action = c.Action switch { SlashCommandAction.NewConversation => "clear", SlashCommandAction.ShowUsage => "usage", SlashCommandAction.Help => "help", SlashCommandAction.Rename => "rename", _ => c.Argument?.ToString().ToLowerInvariant() } });
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
                        var result = await action(new(pane.Id, operation, value), token); Changed();
                        if (operation == "close") uploads.DiscardSession(pane.Id);
                        return operation switch { "stop" => Reply(new { protocol = 1, stopped = result }), "guided" => Reply(new { protocol = 1, accepted = result }, 202), "command" => Reply(new { protocol = 1, ok = true, message = result }), _ => Reply(OK()) };
                    }
                }
                if (method == "POST" && route.Length == 5 && route[3] == "queue" && route[4] == "run-next")
                { await action(new(pane.Id, "queue-next", JsonSerializer.SerializeToElement(new { })), token); Changed(); return Reply(OK()); }
                if (method == "POST" && route.Length == 6 && route[3] == "queue" && route[5] == "remove")
                { await action(new(pane.Id, "queue-remove", JsonSerializer.SerializeToElement(new { id = route[4] })), token); Changed(); return Reply(OK()); }
            }
            throw new MobileRequestException(404, "Mobile route not found.");
        }
        catch (MobileRequestException ex) { return new(ex.Status, Error(ex.Status, ex.Message)); }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException) { return new(400, Error(400, "Invalid mobile request.")); }
        catch (InvalidOperationException) { return new(409, Error(409, "The pane changed; refresh and try again.")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(403, Error(403, "The requested file is unavailable.")); }
    }
    private static long Number(Dictionary<string, string> query, string key, long minimum, long maximum, long fallback)
        => !query.TryGetValue(key, out var text) ? fallback : long.TryParse(text, out var number) && number >= minimum && number <= maximum ? number : throw new MobileRequestException(400, "Invalid " + key + ".");
    private object State()
    {
        var snapshot = service.Snapshot;
        return new { protocol = 1, revision = Revision, hostName = HostName, workspaces = snapshot.Workspaces.Select(w => new { w.Id, w.Name, w.Path }), sessions = snapshot.Sessions.Where(s => s.Kind != "files").Select(Summary).ToArray() };
    }
    private object Summary(RunSession pane)
    {
        var pending = Pending(pane.Id); var tail = pane.Logs.LastOrDefault(e => e.Kind is "assistant" or "user" or "error" or "output");
        return new { pane.Id, pane.WorkspaceId, pane.Title, pane.Kind, pane.Provider, pane.Model, pane.Status, revision = Revision, updatedAt = pane.Logs.LastOrDefault()?.Timestamp ?? pane.CreatedAt,
            preview = tail is null ? null : new { tail.Kind, text = Wire.Clean(tail.Text, 200) }, pendingPermissions = pending.Count(p => !p.CanAnswerQuestions), pendingQuestions = pending.Count(p => p.CanAnswerQuestions), queued = QueueCount?.Invoke(pane.Id) ?? extra(pane).Queued.Count,
            pane.ResumeId, terminal = pane.Kind != "claude", agentViewMode = pane.AgentViewMode == "mighty" ? "mighty" : "plain", mightyStyle = pane.MightyStyle is "ouroboros" or "paperthin" ? pane.MightyStyle : "cli", styleId = pane.MightyStyle ?? "cli", titleMode = pane.TitleMode ?? "auto", resolvedModel = pane.SessionUsage?.Model };
    }
    private object Detail(RunSession pane)
    {
        var detail = extra(pane); var usage = pane.SessionUsage;
        return new { protocol = 1, revision = Revision, session = Summary(pane), entries = pane.Logs.TakeLast(80).ToArray(), hasOlder = pane.Logs.Count > 80,
            permissions = Pending(pane.Id).Select(p => p.CanAnswerPlan && p.Plan is { } plan
                ? (object)new { p.Id, p.RunId, p.ToolName, title = Locale.Get("plan.card.title"), headline = (string?)null, fields = new[] { new { label = Locale.Get("plan.card.title"), value = plan } }, p.Summary, p.CanAllow, questionnaire = (UserQuestionnaire?)null, plan, receivedAt = p.ReceivedAt }
                : new { p.Id, p.RunId, p.ToolName, title = p.ToolName, headline = p.Reason, fields = new[] { new { label = "Input", value = p.InputJson } }, p.Summary, p.CanAllow, questionnaire = p.CanAnswerQuestions ? UserQuestionnaire.Parse(p.InputJson) : null }).ToArray(), queued = detail.Queued,
            usage = usage is null ? null : new { usage.Model, usage.ContextUsedTokens, usage.ContextWindowTokens, usage.ContextPercent, usage.TotalTokens, usage.CostUSD }, elapsedSeconds = pane.RunTiming?.Elapsed(), settings = detail.Settings, mighty = detail.Mighty, statusLine = detail.StatusLine,
            rateLimits = usage?.RateLimits?.Select(r => new { label = r.Kind, usedPercent = r.PercentUsed, r.ResetsAt }) };
    }
    private async Task<object> Files(Workspace workspace, string relative, bool listing, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (listing)
        {
            var value = await Task.Run(() => WorkspaceFiles.List(relative, workspace.Path), token);
            return new { protocol = 1, workspaceId = workspace.Id, path = relative, entries = value.Entries.Take(2000).Select(e => new { e.Name, e.RelativePath, kind = (e.IsSymlink ? "symlink-" : "") + (e.IsDirectory ? "folder" : "file"), noise = e.IsNoise }), truncated = value.Truncated || value.Entries.Count > 2000 };
        }
        if (relative.Length == 0) throw new MobileRequestException(400, "A file path is required.");
        var preview = await Task.Run(() => FilePreviewLoader.Load(workspace.Path, relative, token), token);
        if (preview.Failure is not null) throw new MobileRequestException(404, "File not found.");
        var image = ImagePreview is null && preview.ImageBytes is { Length: <= 512 * 1024 } raw && AttachmentSupport.DetectMediaType(raw) is ("image/png" or "image/jpeg") ? raw : null;
        var thumbnail = ImagePreview is not null && preview.ImageBytes is not null ? await ImagePreview(preview, token) : null;
        var text = preview.Text is { } content ? Wire.Clean(content, 100_000) : null;
        return new { protocol = 1, workspaceId = workspace.Id, path = relative, name = preview.Name, size = preview.Size, modified = preview.Modified?.ToString("O"), type = text is not null ? preview.MarkdownRenderable ? "markdown" : "source" : thumbnail is not null || image is not null ? "image" : "unsupported", text,
            truncated = preview.Truncated || preview.Text?.Length > 100_000, language = Path.GetExtension(preview.Name).TrimStart('.'), encoding = preview.Encoding?.ToString().ToLowerInvariant(), data = thumbnail?.Data ?? (image is null ? null : Convert.ToBase64String(image)), mime = thumbnail?.Mime ?? (image is null ? null : AttachmentSupport.DetectMediaType(image)), width = thumbnail?.Width, height = thumbnail?.Height, thumbnailWidth = thumbnail?.ThumbnailWidth, thumbnailHeight = thumbnail?.ThumbnailHeight, reason = thumbnail is not null || image is not null ? null : preview.ImageBytes is not null ? "tooLarge" : preview.Reason };
    }
    public void ResetUploads() => uploads.Clear();
    public void Dispose() { service.RunEventReceived -= Receive; service.ToolPermissionChanged -= Permission; uploads.Dispose(); }
}
