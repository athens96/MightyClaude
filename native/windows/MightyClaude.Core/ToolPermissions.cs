using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MightyClaude.Core;

/// Ephemeral, single-call consent, mirrored from macOS ToolPermissions.swift.
/// It is never written to a snapshot, never logged with its input and never
/// forwarded to a remote host. The original input stays inside the channel.
public sealed record ToolPermissionRequest(
    string Id,
    string RunId,
    string ToolUseId,
    string ToolName,
    string InputJson,
    string Summary,
    string? Reason = null,
    string? BlockedPath = null,
    string State = "pending",
    bool CanAllow = true,
    // Windows cannot render a questionnaire yet, so this is never true here.
    bool CanAnswerQuestions = false);

/// One labelled value of a tool request, shown instead of raw JSON.
public sealed record ToolPermissionField(string Label, string Value, bool Code = false);

/// A readable form of a `can_use_tool` request. It only rearranges the exact
/// input for display; approval still sends the original, unmodified input.
public sealed record ToolPermissionPresentation(string Title, string? Headline, IReadOnlyList<ToolPermissionField> Fields)
{
    public const int MaximumFieldBytes = 4096, MaximumFields = 12;

    /// The first monospaced value, which a compact bar shows.
    public ToolPermissionField? PrimaryCode => Fields.FirstOrDefault(f => f.Code);

    private static readonly string[] Preferred = ["command", "file_path", "path", "pattern", "query", "url", "prompt", "content"];

    public static ToolPermissionPresentation Make(string toolName, string inputJson)
    {
        var name = (toolName ?? "").Trim();
        using var document = ToolPermissionJson.TryParseObject(inputJson);
        var input = document?.RootElement;

        string? Text(string key)
        {
            if (input is not { } root || !root.TryGetProperty(key, out var raw)) return null;
            var value = raw.ValueKind switch
            {
                JsonValueKind.String => raw.GetString() ?? "",
                JsonValueKind.True => ToolPermissionStrings.BooleanYes,
                JsonValueKind.False => ToolPermissionStrings.BooleanNo,
                JsonValueKind.Number => raw.GetRawText(),
                JsonValueKind.Array when raw.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String) => string.Join("\n", raw.EnumerateArray().Select(e => e.GetString())),
                JsonValueKind.Array or JsonValueKind.Object => ToolPermissionJson.Canonical(raw),
                _ => null,
            };
            if (value is null) return null;
            var clean = ActivitySupport.Clean(value, MaximumFieldBytes);
            if (clean.Length == 0) return null;
            return Encoding.UTF8.GetByteCount(value) > MaximumFieldBytes ? clean + "\n…" : clean;
        }
        ToolPermissionField? Field(string key, string label, bool code = false) => Text(key) is { } value ? new ToolPermissionField(label, value, code) : null;

        var headline = Text("description") is { } description ? ActivitySupport.Clean(description, 1000, true) : null;
        var fields = new List<ToolPermissionField?>();
        var consumed = new HashSet<string>(StringComparer.Ordinal) { "description" };
        void Consume(params string[] keys) { foreach (var key in keys) consumed.Add(key); }
        string title;
        switch (name)
        {
            case "Bash":
                title = ToolPermissionStrings.TitleBash;
                fields.AddRange([Field("command", ToolPermissionStrings.FieldCommand, true), Field("timeout", ToolPermissionStrings.FieldTimeoutMs), Field("run_in_background", ToolPermissionStrings.FieldBackground)]);
                Consume("command", "timeout", "run_in_background"); break;
            case "Read":
                title = ToolPermissionStrings.TitleRead;
                fields.AddRange([Field("file_path", ToolPermissionStrings.FieldFile, true), Field("offset", ToolPermissionStrings.FieldOffset), Field("limit", ToolPermissionStrings.FieldLimit)]);
                Consume("file_path", "offset", "limit"); break;
            case "Edit":
            case "MultiEdit":
                title = ToolPermissionStrings.TitleEdit;
                fields.AddRange([Field("file_path", ToolPermissionStrings.FieldFile, true), Field("old_string", ToolPermissionStrings.FieldOldString, true), Field("new_string", ToolPermissionStrings.FieldNewString, true), Field("replace_all", ToolPermissionStrings.FieldReplaceAll), Field("edits", ToolPermissionStrings.FieldEdits, true)]);
                Consume("file_path", "old_string", "new_string", "replace_all", "edits"); break;
            case "Write":
                title = ToolPermissionStrings.TitleWrite;
                fields.AddRange([Field("file_path", ToolPermissionStrings.FieldFile, true), Field("content", ToolPermissionStrings.FieldContent, true)]);
                Consume("file_path", "content"); break;
            case "NotebookEdit":
                title = ToolPermissionStrings.TitleNotebookEdit;
                fields.AddRange([Field("notebook_path", ToolPermissionStrings.FieldNotebook, true), Field("cell_id", ToolPermissionStrings.FieldCell), Field("edit_mode", ToolPermissionStrings.FieldEditMode), Field("new_source", ToolPermissionStrings.FieldNewString, true)]);
                Consume("notebook_path", "cell_id", "edit_mode", "new_source", "cell_type"); break;
            case "Glob":
            case "Grep":
                title = name == "Glob" ? ToolPermissionStrings.TitleGlob : ToolPermissionStrings.TitleGrep;
                fields.AddRange([Field("pattern", ToolPermissionStrings.FieldPattern, true), Field("path", ToolPermissionStrings.FieldPath, true), Field("glob", ToolPermissionStrings.FieldGlob, true)]);
                Consume("pattern", "path", "glob"); break;
            case "WebFetch":
                title = ToolPermissionStrings.TitleWebFetch;
                fields.AddRange([Field("url", ToolPermissionStrings.FieldUrl, true), Field("prompt", ToolPermissionStrings.FieldQuestion)]);
                Consume("url", "prompt"); break;
            case "WebSearch":
                title = ToolPermissionStrings.TitleWebSearch;
                fields.Add(Field("query", ToolPermissionStrings.FieldQuery, true));
                Consume("query"); break;
            case "Agent":
            case "Task":
                title = ToolPermissionStrings.TitleAgent;
                fields.AddRange([Field("subagent_type", ToolPermissionStrings.FieldSubagentType), Field("model", ToolPermissionStrings.FieldModel), Field("prompt", ToolPermissionStrings.FieldInstruction, true)]);
                Consume("subagent_type", "model", "prompt", "name"); break;
            default:
                if (name.StartsWith("mcp__", StringComparison.Ordinal))
                {
                    var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
                    var server = parts.Length >= 2 ? parts[1] : name;
                    title = ToolPermissionStrings.TitleMcpTemplate.Replace("{server}", ActivitySupport.Clean(server, 80, true));
                }
                else title = ToolPermissionStrings.TitleTool;
                break;
        }
        // Remaining keys keep their own names so nothing in the input is hidden.
        if (input is { } remaining)
        {
            var rest = remaining.EnumerateObject().Select(p => p.Name).Where(n => !consumed.Contains(n))
                .OrderBy(n => Array.IndexOf(Preferred, n) < 0 ? Preferred.Length : Array.IndexOf(Preferred, n))
                .ThenBy(n => n, StringComparer.Ordinal).ToArray();
            foreach (var key in rest) fields.Add(Field(key, key, Preferred.Contains(key)));
        }
        return new ToolPermissionPresentation(title, headline is { Length: > 0 } ? headline : null, fields.Where(f => f is not null).Select(f => f!).Take(MaximumFields).ToArray());
    }
}

/// JSON helpers shared by the channel and the presentation. Object keys are
/// sorted so the displayed text is stable for the same input.
internal static class ToolPermissionJson
{
    internal static JsonDocument? TryParseObject(string? text)
    {
        if (text is null) return null;
        try { var document = JsonDocument.Parse(text); if (document.RootElement.ValueKind == JsonValueKind.Object) return document; document.Dispose(); return null; }
        catch (JsonException) { return null; }
    }

    internal static string Canonical(JsonElement value, bool indented = true)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            WriteSorted(writer, value);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteSorted(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); WriteSorted(writer, property.Value); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteSorted(writer, item);
                writer.WriteEndArray(); break;
            default: value.WriteTo(writer); break;
        }
    }

    /// Keeps exact JSON values while making invisible display controls
    /// explicit. Nothing is stripped from the approved original input.
    internal static string Visible(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Format || (category == UnicodeCategory.Control && rune.Value is not (9 or 10 or 13)))
                foreach (var unit in rune.ToString()) result.Append(CultureInfo.InvariantCulture, $"\\u{(int)unit:x4}");
            else result.Append(rune);
        }
        return result.ToString();
    }
}

/// Claude Code's supported SDK stdio control protocol. Only `can_use_tool`
/// asks reach this surface: the CLI evaluates configured denies and modes
/// before asking. No settings updates, persistent rules or mode changes are
/// ever returned, and nothing is allowed without a user action on that
/// request. Send and event callbacks are injected so the behaviour can be
/// proven on a Mac without a CLI process.
public sealed class ClaudePermissionChannel
{
    public const int MaximumInputBytes = 65536, MaximumPending = 16, MaximumRequestsPerRun = 2048, MaximumMetadataBytes = 8192;
    private static readonly JsonSerializerOptions Protocol = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed record Pending(ToolPermissionRequest Display, JsonElement Input);

    private readonly string runId;
    private readonly string prompt;
    private readonly Action<string> write;
    private readonly Action<ToolPermissionRequest> emit;
    private readonly Action<ToolPermissionRequest, string> activity;
    private readonly Action<string> warning;
    private readonly Action<string> fail;
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private readonly List<string> order = [];
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private bool closed;

    public string InitializationId { get; } = Wire.Id();
    public bool Initialized { get; private set; }
    public bool Failed { get; private set; }
    public int PendingCount => pending.Count;

    public ClaudePermissionChannel(string runId, string prompt, Action<string> write, Action<ToolPermissionRequest> emit,
        Action<ToolPermissionRequest, string>? activity = null, Action<string>? warning = null, Action<string>? fail = null)
    {
        this.runId = runId; this.prompt = prompt; this.write = write; this.emit = emit;
        this.activity = activity ?? ((_, _) => { }); this.warning = warning ?? (_ => { }); this.fail = fail ?? (_ => { });
    }

    /// The waiting requests in arrival order; the bar shows the first and the count.
    public IReadOnlyList<ToolPermissionRequest> Waiting => order.Where(pending.ContainsKey).Select(id => pending[id].Display).ToArray();

    public void Start() => Send(new { type = "control_request", request_id = InitializationId, request = new { subtype = "initialize", hooks = new { }, sdkMcpServers = Array.Empty<object>(), supportedDialogKinds = Array.Empty<string>() } });

    public void InitializationTimedOut() { if (!closed && !Initialized) FailClosed(ToolPermissionStrings.InitializeTimedOut); }

    public void Receive(string line)
    {
        if (closed) return;
        JsonDocument document;
        try { document = JsonDocument.Parse(line); } catch (JsonException) { return; }
        using (document)
        {
            var envelope = document.RootElement;
            if (envelope.ValueKind != JsonValueKind.Object || envelope.Text("type") is not { } type) return;
            if (type == "control_response" && envelope.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object && response.Text("request_id") == InitializationId)
            {
                if (Initialized) return;
                if (response.Text("subtype") != "success") { FailClosed(ToolPermissionStrings.InitializeFailed); return; }
                Initialized = true;
                // Some CLIs replay pending asks in initialize as well as live frames.
                if (response.TryGetProperty("pending_permission_requests", out var replay) && replay.ValueKind == JsonValueKind.Array)
                    foreach (var request in replay.EnumerateArray()) { ReceiveRequest(request); if (closed) break; }
                if (!closed) write(prompt);
                return;
            }
            if (type == "control_cancel_request" && envelope.Text("request_id") is { } cancelled) { Settle(cancelled, "cancelled", "stopped"); return; }
            if (type == "control_request") ReceiveRequest(envelope);
        }
    }

    private void ReceiveRequest(JsonElement envelope)
    {
        if (closed) return;
        if (envelope.ValueKind != JsonValueKind.Object || envelope.Text("request_id") is not { } id || !Wire.Identifier(id)
            || !envelope.TryGetProperty("request", out var request) || request.ValueKind != JsonValueKind.Object || request.Text("subtype") is not { } subtype)
        { FailClosed(ToolPermissionStrings.MalformedControlRequest); return; }
        if (!seen.Add(id)) return;
        if (seen.Count > MaximumRequestsPerRun) { FailClosed(ToolPermissionStrings.TooManyRequestsInRun); return; }
        // No dialog kinds are declared in initialize. A host must not settle a
        // future dialog kind it cannot render; the CLI owns its deadline.
        if (subtype == "request_user_dialog") { warning(ToolPermissionStrings.UnsupportedDialog); return; }
        if (subtype == "elicitation") { Success(id, new { action = "decline" }); warning(ToolPermissionStrings.DeclinedElicitation); return; }
        if (subtype != "can_use_tool") { Error(id, "This host does not support this control request."); return; }

        var toolName = request.Text("tool_name");
        var toolUseId = request.Text("tool_use_id");
        var hasInput = request.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object;
        if (toolName is not { Length: > 0 } || Encoding.UTF8.GetByteCount(toolName) > 256 || !Wire.Identifier(toolUseId) || !hasInput)
        { Error(id, "Invalid tool permission request."); warning(ToolPermissionStrings.DeniedMalformedRequest); return; }
        if (pending.Count >= MaximumPending) { Deny(id, toolUseId!, "Too many pending permission requests."); warning(ToolPermissionStrings.DeniedTooManyPending); return; }

        var display = ToolPermissionJson.Visible(ToolPermissionJson.Canonical(input));
        if (Encoding.UTF8.GetByteCount(display) > MaximumInputBytes)
        {
            Deny(id, toolUseId!, "Tool input exceeds the host's complete-display limit; permission denied.");
            warning(ToolPermissionStrings.DeniedOversizedInput); return;
        }

        // A questionnaire needs its own screen, which Windows does not have yet.
        var interaction = (request.TryGetProperty("requires_user_interaction", out var flag) && flag.ValueKind == JsonValueKind.True) || toolName == "AskUserQuestion";
        var details = string.Join("\n", new[] { request.Text("title"), request.Text("description"), request.Text("decision_reason") }.Where(v => !string.IsNullOrEmpty(v)));
        var originalPath = request.Text("blocked_path");
        var completeMetadata = Encoding.UTF8.GetByteCount(details) <= MaximumMetadataBytes && Encoding.UTF8.GetByteCount(originalPath ?? "") <= MaximumMetadataBytes;
        var reason = ActivitySupport.Clean(details, MaximumMetadataBytes);
        if (interaction) reason += (reason.Length == 0 ? "" : "\n\n") + ToolPermissionStrings.NeedsSeparateInputScreen;
        if (!completeMetadata) reason += "\n\n" + ToolPermissionStrings.MetadataTooLarge;

        var value = new ToolPermissionRequest(id, runId, toolUseId!, ActivitySupport.Clean(toolName, 256, true), display,
            ActivitySupport.Summary(toolName, input), reason.Length == 0 ? null : reason,
            originalPath is null ? null : ActivitySupport.Clean(originalPath, MaximumMetadataBytes),
            CanAllow: !interaction && completeMetadata);
        pending[id] = new Pending(value, input.Clone());
        order.Add(id);
        activity(value, "waiting"); emit(value);
    }

    /// 이번만 허용 / 거부. Allow is refused for anything the bar cannot show
    /// completely or that needs a screen this host does not have.
    public void Respond(string requestId, bool allow)
    {
        if (closed || !pending.TryGetValue(requestId, out var request)) throw new InvalidOperationException(ToolPermissionStrings.AlreadySettled);
        if (allow && !request.Display.CanAllow) throw new InvalidOperationException(ToolPermissionStrings.CannotAllow);
        // Removal precedes callbacks/writes: even a reentrant second click has
        // no request left to approve. updatedInput is the original object.
        pending.Remove(requestId);
        if (allow) Success(requestId, new { behavior = "allow", updatedInput = request.Input, toolUseID = request.Display.ToolUseId });
        else Deny(requestId, request.Display.ToolUseId, "The user denied this tool request in MightyClaude.");
        var display = request.Display with { State = allow ? "allowed" : "denied" };
        activity(display, allow ? "running" : "error"); emit(display);
    }

    /// The run stopped: every waiting request is settled, none is allowed.
    public void CancelAll()
    {
        if (closed) return;
        closed = true;
        foreach (var id in order.ToArray()) Settle(id, "cancelled", "stopped");
    }

    private void Settle(string id, string state, string activityState)
    {
        if (!pending.Remove(id, out var value)) return;
        var display = value.Display with { State = state };
        activity(display, activityState); emit(display);
    }

    private void FailClosed(string message) { Failed = true; CancelAll(); fail(message); }
    private void Success(string id, object result) => Send(new { type = "control_response", response = new { subtype = "success", request_id = id, response = result } });
    private void Deny(string id, string toolUseId, string message) => Success(id, new { behavior = "deny", message, toolUseID = toolUseId });
    private void Error(string id, string message) => Send(new { type = "control_response", response = new { subtype = "error", request_id = id, error = message } });
    private void Send(object value) => write(JsonSerializer.Serialize(value, Protocol) + "\n");
}
