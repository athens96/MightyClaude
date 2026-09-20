using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MightyClaude.Core;

/// Ephemeral, single-call consent. Never persisted in a snapshot or forwarded
/// to a remote host. The original input stays inside the run's channel.
public sealed record ToolPermissionRequest(
    string Id, string RunId, string ToolUseId, string ToolName,
    string InputJson, string Summary,
    string? Reason = null, string? BlockedPath = null,
    string State = "pending", bool CanAllow = true, bool CanAnswerQuestions = false);

/// Claude Code's supported SDK stdio control protocol. Only can_use_tool asks
/// reach this surface: the CLI evaluates configured denies and modes before asking.
/// No settings updates, persistent rules or mode changes are ever returned.
/// All methods run on the owning run context (or synchronously in tests).
public sealed class ClaudePermissionChannel
{
    public const int MaximumInputBytes = 65_536;
    public const int MaximumPending = 16;

    private sealed record Pending(ToolPermissionRequest Display, JsonElement Input);

    public string RunId { get; }
    public string InitializationId { get; } = Wire.Id();
    public bool Initialized { get; private set; }
    public bool Failed { get; private set; }
    public IReadOnlyList<ToolPermissionRequest> Waiting => pendingOrder.Select(id => pending[id].Display).ToList();

    private readonly string prompt;
    private readonly Action<string> write;
    private readonly Action<ToolPermissionRequest> emit;
    private readonly Action<ToolPermissionRequest, string> activity;
    private readonly Action<string> warning;
    private readonly Action<string> fail;
    private readonly Dictionary<string, Pending> pending = [];
    private readonly List<string> pendingOrder = [];
    private readonly HashSet<string> seen = [];
    private bool closed;

    public ClaudePermissionChannel(string runId, string prompt, Action<string> write,
        Action<ToolPermissionRequest> emit, Action<ToolPermissionRequest, string> activity,
        Action<string> warning, Action<string> fail)
    {
        RunId = runId; this.prompt = prompt; this.write = write;
        this.emit = emit; this.activity = activity;
        this.warning = warning; this.fail = fail;
    }

    public void Start()
        => Send(new
        {
            type = "control_request", request_id = InitializationId,
            request = new { subtype = "initialize", hooks = new { }, sdkMcpServers = Array.Empty<object>(), supportedDialogKinds = Array.Empty<string>() }
        });

    public void InitializationTimedOut()
    {
        if (closed || Initialized) return;
        FailClosed(ToolPermissionStrings.InitializeTimedOut);
    }

    public void Receive(string data)
    {
        if (closed) return;
        JsonElement envelope;
        try { using var doc = JsonDocument.Parse(data); envelope = doc.RootElement.Clone(); }
        catch { return; }
        if (envelope.ValueKind != JsonValueKind.Object) return;
        var type = envelope.Text("type");
        if (type is null) return;

        if (type == "control_response" &&
            envelope.TryGetProperty("response", out var outerResp) &&
            outerResp.ValueKind == JsonValueKind.Object &&
            outerResp.Text("request_id") == InitializationId)
        {
            if (Initialized) return;
            if (outerResp.Text("subtype") != "success") { FailClosed(ToolPermissionStrings.InitializeFailed); return; }
            Initialized = true;
            if (outerResp.TryGetProperty("pending_permission_requests", out var replay) &&
                replay.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in replay.EnumerateArray())
                {
                    ReceiveRequest(item.Clone());
                    if (closed) break;
                }
            }
            if (!closed) write(prompt);
            return;
        }

        if (type == "control_cancel_request" && envelope.Text("request_id") is string cancelId)
        { Settle(cancelId, "cancelled", "stopped"); return; }

        if (type == "control_request") ReceiveRequest(envelope);
    }

    private void ReceiveRequest(JsonElement envelope)
    {
        if (closed) return;
        var id = envelope.Text("request_id");
        var hasRequest = envelope.TryGetProperty("request", out var req) && req.ValueKind == JsonValueKind.Object;
        var subtype = hasRequest ? req.Text("subtype") : null;
        if (!Wire.Identifier(id) || !hasRequest || subtype is null)
        { FailClosed(ToolPermissionStrings.MalformedControlRequest); return; }

        if (!seen.Add(id!)) return;
        if (seen.Count > 2048) { FailClosed(ToolPermissionStrings.TooManyRequestsInRun); return; }

        if (subtype == "request_user_dialog") { warning(ToolPermissionStrings.UnsupportedDialog); return; }
        if (subtype == "elicitation")
        { SendSuccess(id!, new { action = "decline" }); warning(ToolPermissionStrings.DeclinedElicitation); return; }
        if (subtype != "can_use_tool") { SendError(id!, "This host does not support this control request."); return; }

        var toolName = req.Text("tool_name");
        var toolUseId = req.Text("tool_use_id");
        if (string.IsNullOrEmpty(toolName) || Encoding.UTF8.GetByteCount(toolName) > 256 ||
            !Wire.Identifier(toolUseId) ||
            !req.TryGetProperty("input", out var inputEl) || inputEl.ValueKind != JsonValueKind.Object)
        { SendError(id!, "Invalid tool permission request."); warning(ToolPermissionStrings.DeniedMalformedRequest); return; }

        if (pending.Count >= MaximumPending)
        { Deny(id!, toolUseId!, "Too many pending permission requests."); warning(ToolPermissionStrings.DeniedTooManyPending); return; }

        var displayJson = InputDisplay(inputEl);
        if (Encoding.UTF8.GetByteCount(displayJson) > MaximumInputBytes)
        { Deny(id!, toolUseId!, "Tool input exceeds the host's complete-display limit; permission denied."); warning(ToolPermissionStrings.DeniedOversizedInput); return; }

        var interaction = (req.TryGetProperty("requires_user_interaction", out var ri) && ri.ValueKind == JsonValueKind.True) || toolName == "AskUserQuestion";
        var details = new[] { req.Text("title"), req.Text("description"), req.Text("decision_reason") }.Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToArray();
        var detailText = string.Join("\n", details);
        var originalPath = req.Text("blocked_path");
        var completeMetadata = Encoding.UTF8.GetByteCount(detailText) <= 8192 && Encoding.UTF8.GetByteCount(originalPath ?? "") <= 8192;
        // AskUserQuestion questionnaires are out of scope on Windows (stage 3).
        const bool canAnswerQuestions = false;

        var reason = ActivitySupport.Clean(detailText, 8192);
        if (interaction && !canAnswerQuestions) reason += (reason.Length == 0 ? "" : "\n\n") + ToolPermissionStrings.NeedsSeparateInputScreen;
        if (!completeMetadata) reason += (reason.Length == 0 ? "" : "\n\n") + ToolPermissionStrings.MetadataTooLarge;

        var storedInput = inputEl.Clone();
        var value = new ToolPermissionRequest(
            Id: id!, RunId: RunId, ToolUseId: toolUseId!,
            ToolName: ActivitySupport.Clean(toolName!, 256, true),
            InputJson: displayJson,
            Summary: ActivitySupport.Summary(toolName!, inputEl),
            Reason: reason.Length == 0 ? null : reason,
            BlockedPath: originalPath is null ? null : ActivitySupport.Clean(originalPath, 8192),
            CanAllow: !interaction && completeMetadata,
            CanAnswerQuestions: canAnswerQuestions);
        pending[id!] = new Pending(value, storedInput);
        pendingOrder.Add(id!);
        activity(value, "waiting"); emit(value);
    }

    public void Respond(string requestId, bool allow)
    {
        if (closed || !pending.TryGetValue(requestId, out var request))
            throw new InvalidOperationException(ToolPermissionStrings.AlreadySettled);
        if (allow && !request.Display.CanAllow)
            throw new InvalidOperationException(ToolPermissionStrings.CannotAllow);
        pending.Remove(requestId);
        pendingOrder.Remove(requestId);
        if (allow) SendSuccess(requestId, new { behavior = "allow", updatedInput = request.Input, toolUseID = request.Display.ToolUseId });
        else Deny(requestId, request.Display.ToolUseId, "The user denied this tool request in MightyClaude.");
        var display = request.Display with { State = allow ? "allowed" : "denied" };
        activity(display, allow ? "running" : "error"); emit(display);
    }

    public void CancelAll()
    {
        if (closed) return;
        closed = true;
        foreach (var id in pendingOrder.ToArray()) Settle(id, "cancelled", "stopped");
    }

    private void Settle(string id, string state, string activityState)
    {
        if (!pending.TryGetValue(id, out var p)) return;
        pending.Remove(id);
        pendingOrder.Remove(id);
        var display = p.Display with { State = state };
        activity(display, activityState); emit(display);
    }

    private void FailClosed(string message) { Failed = true; CancelAll(); fail(message); }

    private void SendSuccess(string id, object result) => SendResponse(new { subtype = "success", request_id = id, response = result });
    private void SendError(string id, string message) => SendResponse(new { subtype = "error", request_id = id, error = message });
    private void Deny(string id, string toolUseId, string message) => SendSuccess(id, new { behavior = "deny", message, toolUseID = toolUseId });

    private void SendResponse(object response) => Send(new { type = "control_response", response });

    private void Send(object obj)
    {
        try { write(JsonSerializer.Serialize(obj, Wire.Json) + "\n"); }
        catch { }
    }

    // Use relaxed encoder so U+202E and other non-ASCII chars stay raw for the
    // post-process loop below, which then escapes them with lowercase \uXXXX.
    private static readonly JsonSerializerOptions DisplayBase = new()
        { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string InputDisplay(JsonElement input)
    {
        var json = JsonSerializer.Serialize(input, DisplayBase);
        // Post-process to escape Unicode format characters (e.g. U+202E RIGHT-TO-LEFT OVERRIDE)
        // and non-standard control characters, matching the macOS inputDisplay escape logic.
        var sb = new StringBuilder(json.Length);
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (char.IsHighSurrogate(c) && i + 1 < json.Length && char.IsLowSurrogate(json[i + 1]))
            {
                sb.Append(c); sb.Append(json[++i]);
            }
            else
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(c);
                bool isFormat = cat == UnicodeCategory.Format;
                bool isNonstandardControl = c < '\x20' && c != '\t' && c != '\n' && c != '\r';
                if (isFormat || isNonstandardControl) sb.Append($"\\u{(int)c:x4}");
                else sb.Append(c);
            }
        }
        return sb.ToString();
    }
}

/// The Claude arguments (and initial stdin frame) needed to launch with host
/// permission prompts, and the fallback for paths that keep prompts off.
public sealed record ProviderInput(List<string> Arguments, byte[] StandardInput)
{
    public static ProviderInput Prepare(StartRunRequest value, string pluginDirectory, bool allowPermissionPrompts)
    {
        var request = value.Validate();
        var args = ProviderCatalog.Arguments(request, pluginDirectory);
        if (allowPermissionPrompts && request.Provider == "claude") HostPrompts(args);
        return new(args, System.Text.Encoding.UTF8.GetBytes(PromptFrame(request.Input)));
    }

    /// <summary>
    /// Rewrites a Claude argument list in place so permission prompts come to
    /// this host over stdio. Only a launch path that can show the approval bar
    /// calls this; every other path keeps --permission-prompts none as today.
    /// </summary>
    public static List<string> HostPrompts(List<string> arguments)
    {
        var noneIdx = arguments.IndexOf("none");
        if (noneIdx > 0 && arguments[noneIdx - 1] == "--permission-prompts")
        {
            arguments[noneIdx] = "host";
            arguments.AddRange(["--permission-prompt-tool", "stdio"]);
        }
        if (!arguments.Contains("--input-format")) arguments.AddRange(["--input-format", "stream-json"]);
        return arguments;
    }

    /// <summary>The single stream-json user frame the channel sends once the handshake succeeds.</summary>
    public static string PromptFrame(string input)
        => JsonSerializer.Serialize(new { type = "user", message = new { content = input } }, Wire.Json) + "\n";
}
