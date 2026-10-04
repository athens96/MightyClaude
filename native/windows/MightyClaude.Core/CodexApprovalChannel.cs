using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MightyClaude.Core;

/// One app-server turn. Approvals are ephemeral, bound to its thread, turn and
/// item, and can only grant the single displayed command or diff. Mirrors the
/// macOS CodexApprovalChannel; the CLI remains the owner of authentication.
public sealed class CodexApprovalChannel
{
    public const int MaximumFrameBytes = 32 * 1024 * 1024;
    public const int MaximumInputBytes = 65_536;
    public const int MaximumPending = 16;
    private sealed record Pending(JsonElement RpcId, ToolPermissionRequest Display);
    private readonly object sync = new();
    private readonly StartRunRequest request;
    private readonly string workspace;
    private readonly string prompt;
    private readonly IReadOnlyList<string> images;
    private readonly Action<string> write, consume, warning, fail;
    private readonly Action completed;
    private readonly Action<ToolPermissionRequest> emit;
    private readonly Action<ToolPermissionRequest, string> activity;
    private readonly Dictionary<string, Pending> pending = [];
    private readonly Dictionary<string, string> rpc = [];
    private readonly Dictionary<string, JsonElement> files = [];
    private readonly HashSet<string> seen = [];
    private string? threadId, turnId;
    private int sequence;
    private bool closed, started, ready, awaitingTurn;
    private object usage = new { };
    public bool Initialized { get; private set; }
    public bool TurnCompleted { get; private set; }
    public bool Failed { get; private set; }

    public CodexApprovalChannel(StartRunRequest request, string workspace, string prompt,
        IReadOnlyList<string> images, Action<string> write, Action<string> consume,
        Action<ToolPermissionRequest> emit, Action<ToolPermissionRequest, string> activity,
        Action<string> warning, Action<string> fail, Action completed)
    {
        this.request = request; this.workspace = workspace; this.prompt = prompt; this.images = images;
        this.write = write; this.consume = consume; this.emit = emit; this.activity = activity;
        this.warning = warning; this.fail = fail; this.completed = completed;
    }

    public void Start()
    {
        lock (sync)
        {
            if (started || closed) return; started = true;
            Call("initialize", new { clientInfo = new { name = "mightyclaude", title = "Mighty Claude", version = "1.0" }, capabilities = new { experimentalApi = false } });
        }
    }
    public void InitializationTimedOut() { lock (sync) if (!closed && !ready) FailClosed("Codex approval channel initialization timed out."); }
    public void Flush() { lock (sync) if (!closed) FailClosed("Codex exited before completing the approval turn."); }
    public void Receive(string frame)
    {
        lock (sync)
        {
            if (closed) return;
            if (Encoding.UTF8.GetByteCount(frame) > MaximumFrameBytes) { FailClosed("Codex response frame exceeded the size limit."); return; }
            try
            {
                using var doc = JsonDocument.Parse(frame);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) { FailClosed("Invalid Codex response."); return; }
                ReceiveFrame(doc.RootElement);
            }
            catch (JsonException) { FailClosed("Invalid Codex response JSON."); }
        }
    }
    private static JsonElement Get(JsonElement obj, string name) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) ? value : default;
    private void ReceiveFrame(JsonElement obj)
    {
        if (obj.Text("method") is { } method)
        {
            var parameters = Get(obj, "params"); var id = Get(obj, "id");
            if (parameters.ValueKind != JsonValueKind.Object)
            {
                if (id.ValueKind != JsonValueKind.Undefined) RpcError(id, "Invalid request parameters."); else FailClosed("Invalid Codex notification.");
                return;
            }
            if (id.ValueKind != JsonValueKind.Undefined) ServerRequest(id, method, parameters); else Notification(method, parameters);
            return;
        }
        if (IdKey(Get(obj, "id")) is not { } key || !rpc.Remove(key, out var responseMethod)) return;
        var result = Get(obj, "result");
        if (Get(obj, "error").ValueKind != JsonValueKind.Undefined || result.ValueKind != JsonValueKind.Object)
        { FailClosed("Codex " + responseMethod + " failed. Check CLI version, authentication and permissions."); return; }
        switch (responseMethod)
        {
            case "initialize":
                Initialized = true; Send(new { method = "initialized" });
                var parameters = new Dictionary<string, object?> { ["cwd"] = workspace, ["approvalPolicy"] = "on-request", ["approvalsReviewer"] = "user", ["sandbox"] = "workspace-write" };
                if (request.Model != "default") parameters["model"] = request.Model;
                if (request.ResumeId is { } resume) { parameters["threadId"] = resume; parameters["excludeTurns"] = true; Call("thread/resume", parameters); }
                else Call("thread/start", parameters);
                break;
            case "thread/start": case "thread/resume":
                var thread = Get(result, "thread").Text("id");
                if (!Wire.Identifier(thread) || request.ResumeId is not null && request.ResumeId != thread) { FailClosed("Codex thread identifier did not match."); return; }
                threadId = thread; Legacy(new { type = "thread.started", thread_id = thread }); StartTurn(); break;
            case "turn/start":
                var turn = Get(result, "turn").Text("id");
                if (!Wire.Identifier(turn) || turnId is not null && turnId != turn) { FailClosed("Codex turn identifier did not match."); return; }
                if (turnId is null) { turnId = turn; Legacy(new { type = "turn.started" }); }
                awaitingTurn = false; ready = true; break;
        }
    }
    private void StartTurn()
    {
        var input = new List<object> { new { type = "text", text = prompt } };
        input.AddRange(images.Select(path => (object)new { type = "localImage", path }));
        var parameters = new Dictionary<string, object?>
        {
            ["threadId"] = threadId, ["input"] = input, ["cwd"] = workspace,
            ["approvalPolicy"] = "on-request", ["approvalsReviewer"] = "user",
            ["sandboxPolicy"] = new { type = "workspaceWrite", writableRoots = new[] { workspace }, networkAccess = request.Settings!.NetworkAccess }
        };
        if (request.Model != "default") parameters["model"] = request.Model;
        if (request.Settings!.Effort != "default") parameters["effort"] = request.Settings.Effort;
        if (request.Settings.FastMode) parameters["serviceTier"] = "fast";
        awaitingTurn = true; Call("turn/start", parameters);
    }
    private void Notification(string method, JsonElement parameters)
    {
        if (threadId is null || parameters.Text("threadId") != threadId) return;
        if (method == "serverRequest/resolved" && IdKey(Get(parameters, "requestId")) is { } key)
        { foreach (var id in pending.Where(p => IdKey(p.Value.RpcId) == key).Select(p => p.Key).ToArray()) CancelPending(id); return; }
        if (method == "turn/started" && awaitingTurn && Get(parameters, "turn").Text("id") is { } turn && Wire.Identifier(turn))
        {
            if (turnId is not null && turnId != turn) { FailClosed("Codex turn notification did not match."); return; }
            if (turnId is null) { turnId = turn; Legacy(new { type = "turn.started" }); } return;
        }
        if (method == "turn/completed")
        {
            var result = Get(parameters, "turn");
            if (turnId is null || result.Text("id") != turnId) return;
            if (result.Text("status") != "completed") { FailClosed("Codex turn failed or was interrupted."); return; }
            Legacy(new { type = "turn.completed", usage }); TurnCompleted = true; CancelAll(); completed(); return;
        }
        if (turnId is null || parameters.Text("turnId") != turnId) return;
        if (method == "error" && Get(parameters, "willRetry").ValueKind != JsonValueKind.True) { FailClosed("Codex turn returned an error."); return; }
        if (method == "thread/tokenUsage/updated" && Get(Get(parameters, "tokenUsage"), "last") is { ValueKind: JsonValueKind.Object } last)
        {
            usage = new { input_tokens = Count(last, "inputTokens"), cached_input_tokens = Count(last, "cachedInputTokens"), output_tokens = Count(last, "outputTokens") }; return;
        }
        var item = Get(parameters, "item");
        if (method is not ("item/started" or "item/completed") || item.Text("id") is not { } itemId) return;
        if (method == "item/completed") foreach (var id in pending.Where(p => p.Value.Display.ToolUseId == itemId).Select(p => p.Key).ToArray()) CancelPending(id);
        if (item.Text("type") == "fileChange")
        {
            if (method == "item/started" && files.Count < 128 && Encoding.UTF8.GetByteCount(item.GetRawText()) <= MaximumInputBytes) files[itemId] = item.Clone();
            if (method == "item/completed") files.Remove(itemId);
        }
        if (LegacyItem(item) is { } mapped) Legacy(new { type = method == "item/started" ? "item.started" : "item.completed", item = mapped });
    }
    private static long Count(JsonElement obj, string name) => Get(obj, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var count) ? Math.Max(0, count) : 0;
    private void ServerRequest(JsonElement rawId, string method, JsonElement parameters)
    {
        if (IdKey(rawId) is not { } key) { FailClosed("Invalid Codex request identifier."); return; }
        if (!seen.Add(key)) { FailClosed("Duplicate Codex approval request."); return; }
        if (seen.Count > 2048) { FailClosed("Too many Codex approval requests."); return; }
        if (threadId is null || turnId is null || parameters.Text("threadId") != threadId || parameters.Text("turnId") != turnId)
        { RpcError(rawId, "Request does not belong to the active thread and turn."); return; }
        if (method == "item/permissions/requestApproval") { Reply(rawId, new { permissions = new { }, scope = "turn" }); return; }
        if (method == "mcpServer/elicitation/request") { Reply(rawId, new Dictionary<string, object?> { ["action"] = "decline", ["content"] = null, ["_meta"] = null }); return; }
        if (method is not ("item/commandExecution/requestApproval" or "item/fileChange/requestApproval")) { RpcError(rawId, "This host does not support this request."); warning("Unsupported Codex input request was declined."); return; }
        var itemId = parameters.Text("itemId");
        if (!Wire.Identifier(itemId) || pending.Count >= MaximumPending) { Reply(rawId, new { decision = "decline" }); return; }
        var command = method == "item/commandExecution/requestApproval";
        var input = JsonNode.Parse(parameters.GetRawText())!.AsObject();
        bool canAllow;
        if (command)
            canAllow = parameters.Text("command") is { Length: > 0 } && AbsolutePath(parameters.Text("cwd")) && (Get(parameters, "kind").ValueKind == JsonValueKind.Undefined || parameters.Text("kind") == "command");
        else
        {
            var changes = files.TryGetValue(itemId!, out var file) ? Get(file, "changes") : default;
            input["changes"] = changes.ValueKind == JsonValueKind.Array ? JsonNode.Parse(changes.GetRawText()) : new JsonArray();
            canAllow = changes.ValueKind == JsonValueKind.Array && changes.GetArrayLength() > 0 && changes.EnumerateArray().All(c => c.Text("path") is { Length: > 0 } && Get(c, "diff").ValueKind == JsonValueKind.String && Get(c, "kind").ValueKind == JsonValueKind.Object);
            if (Get(parameters, "grantRoot").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) canAllow = false;
        }
        var decisions = Get(parameters, "availableDecisions");
        if (decisions.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) canAllow &= decisions.ValueKind == JsonValueKind.Array && decisions.EnumerateArray().Any(d => d.ValueKind == JsonValueKind.String && d.GetString() == "accept");
        // Default encoder visibly escapes Unicode format characters, including bidi overrides.
        var display = input.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (Encoding.UTF8.GetByteCount(display) > MaximumInputBytes) { Reply(rawId, new { decision = "decline" }); warning("Codex approval contents exceeded the complete-display limit."); return; }
        var value = new ToolPermissionRequest(Wire.Id(), request.SessionId, itemId!, command ? "command_execution" : "file_change", display,
            command ? "Command execution approval" : "File change approval",
            canAllow ? ActivitySupport.Clean(parameters.Text("reason") ?? "", 8192) : "The full command or diff is unavailable, or this request does not support a one-time approval.", CanAllow: canAllow);
        pending[value.Id] = new(rawId.Clone(), value); activity(value, "waiting"); emit(value);
    }
    public void Respond(string id, bool allow)
    {
        lock (sync)
        {
            if (closed || !pending.TryGetValue(id, out var ask)) throw new InvalidOperationException(ToolPermissionStrings.AlreadySettled);
            if (allow && !ask.Display.CanAllow) throw new InvalidOperationException(ToolPermissionStrings.CannotAllow);
            pending.Remove(id); Reply(ask.RpcId, new { decision = allow ? "accept" : "decline" });
            var value = ask.Display with { State = allow ? "allowed" : "denied" }; activity(value, allow ? "running" : "error"); emit(value);
        }
    }
    private void CancelPending(string id) { if (pending.Remove(id, out var ask)) { var value = ask.Display with { State = "cancelled" }; activity(value, "stopped"); emit(value); } }
    public void CancelAll() { lock (sync) { if (closed) return; closed = true; files.Clear(); rpc.Clear(); foreach (var id in pending.Keys.ToArray()) CancelPending(id); } }
    private void FailClosed(string message) { if (closed) return; Failed = true; CancelAll(); fail(message); }
    private void Call(string method, object parameters) { var id = "mighty-" + ++sequence; rpc["s:" + id] = method; Send(new { id, method, @params = parameters }); }
    private void Reply(JsonElement id, object result) => Send(new { id, result });
    private void RpcError(JsonElement id, string message) => Send(new { id, error = new { code = -32601, message } });
    private void Send(object value) => write(JsonSerializer.Serialize(value, Wire.Json) + "\n");
    private void Legacy(object value) => consume(JsonSerializer.Serialize(value, Wire.Json));
    private static string? IdKey(JsonElement raw) => raw.ValueKind switch
    {
        JsonValueKind.String when raw.GetString() is { Length: > 0 } text && Encoding.UTF8.GetByteCount(text) <= 256 => "s:" + text,
        JsonValueKind.Number when raw.TryGetInt64(out var number) => "n:" + number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => null
    };
    // Also accepts Windows roots during cross-platform tests; rejects drive-relative C:foo.
    public static bool AbsolutePath(string? value) => value is { Length: > 0 } && !value.Contains('\0') && (value.StartsWith('/') || value.StartsWith("\\\\", StringComparison.Ordinal) || value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/');
    private static JsonObject? LegacyItem(JsonElement raw)
    {
        var mapped = raw.Text("type") switch { "agentMessage" => "agent_message", "commandExecution" => "command_execution", "fileChange" => "file_change", "mcpToolCall" => "mcp_tool_call", "webSearch" => "web_search", "collabAgentToolCall" => "collab_agent_tool_call", "contextCompaction" => "context_compaction", "imageView" => "image_view", "imageGeneration" => "image_generation", _ => null };
        if (mapped is null) return null;
        var item = JsonNode.Parse(raw.GetRawText())!.AsObject(); item["type"] = mapped;
        foreach (var (from, to) in new[] { ("aggregatedOutput", "aggregated_output"), ("exitCode", "exit_code"), ("senderThreadId", "sender_thread_id"), ("receiverThreadIds", "receiver_thread_ids"), ("agentsStates", "agents_states"), ("savedPath", "saved_path") })
            if (item.Remove(from, out var value)) item[to] = value;
        if (raw.Text("status") == "inProgress") item["status"] = "in_progress";
        if (mapped == "collab_agent_tool_call" && raw.Text("tool") is { } tool) item["tool"] = tool switch { "spawnAgent" => "spawn_agent", "sendInput" => "send_input", "closeAgent" => "close_agent", _ => tool };
        return item;
    }
}
