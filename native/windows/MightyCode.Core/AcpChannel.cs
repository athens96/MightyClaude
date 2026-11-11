using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MightyCode.Core;

/// <summary>
/// One Agent Client Protocol run (https://agentclientprotocol.com) (M/MightyCore/ACPChannel.swift): JSON-RPC 2.0 over the agent's stdio,
/// one JSON object per line. The app is the client: <c>initialize</c>, then <c>session/new</c> (or <c>session/resume</c> /
/// <c>session/load</c> of the pane's session), the pane's settings as <c>session/set_config_option</c>, then one <c>session/prompt</c>.
/// What the agent streams (<c>session/update</c>) reaches the parser as <c>acp.*</c> events (<see cref="OutputParser"/>); a
/// <c>session/request_permission</c> becomes the pane's approval card, and only the user's single-call answer leaves the channel. A question
/// (an <c>elicitation/create</c> form: opencode's <c>question</c> tool, Hermes's clarify; or Hermes's clarify as an approval with one
/// <c>allow_once</c> option per choice) becomes the pane's question card (<see cref="AgentQuestions"/>). Nothing here is one agent's own:
/// opencode and Hermes use the same channel, and differ only in their argv, how the permission mode reaches them, and their settings' ids.
/// </summary>
public sealed class AcpChannel
{
    public const int MaximumFrameBytes = 32 * 1024 * 1024;
    public const int MaximumInputBytes = 65_536;
    public const int MaximumPending = 16;

    /// <summary>What the client offers: no file system and no terminal, so the agent uses its own tools, and form elicitation, so its questions arrive as <c>elicitation/create</c>.</summary>
    public static JsonObject ClientCapabilities() => new()
    {
        ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false },
        ["terminal"] = false,
        ["elicitation"] = new JsonObject { ["form"] = new JsonObject() },
    };

    /// <summary>How a question's answer travels: an elicitation's <c>content</c>, or the option of the picked choice.</summary>
    private sealed record Question(AcpElicitationForm? Form, IReadOnlyDictionary<string, string>? Choices);
    /// <param name="Row">False for a form without a tool call of its own (Hermes's): no activity row to mark.</param>
    private sealed record Pending(JsonNode? RpcId, string? AllowOption, string? RejectOption, ToolPermissionRequest Display, Question? Asked = null, bool Row = true);

    private readonly object sync = new();
    private readonly string runId;
    private readonly string? resumeId;
    private readonly string workspacePath;
    private readonly string promptText;
    private readonly IReadOnlyList<(string MediaType, string Data)> images;
    private readonly IReadOnlyList<(string Id, string Value)> configValues;
    private readonly Func<JsonObject, string?>? admit;
    private readonly bool freeChoices;
    private readonly Action<string> write, consume, warning, fail;
    private readonly Action<ToolPermissionRequest> emit;
    private readonly Action<ToolPermissionRequest, string> activity;
    private readonly Action completed;
    private readonly Dictionary<string, Pending> pending = [];
    private readonly HashSet<string> seen = [];
    private readonly Dictionary<string, string> rpc = [];
    private readonly Queue<(string Id, string Value)> queuedConfig = [];
    private JsonArray configOptions = [];
    private int sequence;
    private bool canResume, canLoad, replaying, closed, started;
    /// <summary>The tool call the agent opened last and has not settled: a form that names no call of its own (Hermes's clarify) is that call's question.</summary>
    private string? openCall;

    public string? SessionId { get; private set; }
    public bool Initialized { get; private set; }
    public bool Ready { get; private set; }
    public bool TurnCompleted { get; private set; }
    public bool Failed { get; private set; }
    /// <summary>
    /// The <c>session/cancel</c> notification of the turn in flight: an agent whose turn outlives its ACP process (Hermes's gateway owns it) is
    /// told to stop before the process is ended.
    /// </summary>
    public AcpCancellation Cancellation { get; } = new();

    /// <param name="promptText">The prompt's text block: the request (or the attachments' default words), the attachments' copies and any note for the agent.</param>
    /// <param name="images">Pictures sent as image blocks (base64).</param>
    /// <param name="configValues"><c>session/set_config_option</c> values sent before the prompt, in order (model, effort, mode).</param>
    /// <param name="admit">Reads the agent's <c>initialize</c> answer before a session is opened: a message stops the run there as a missing sign-in (Hermes names no provider it could use).</param>
    /// <param name="freeChoices">The agent reads a typed answer in place of a form's choice (Hermes's clarify).</param>
    /// <param name="consume">One <c>acp.*</c> event, as a JSON line, for the parser, the usage reader and the graph.</param>
    public AcpChannel(string runId, string? resumeId, string workspacePath, string promptText, IReadOnlyList<(string MediaType, string Data)> images,
        IReadOnlyList<(string Id, string Value)> configValues, Action<string> write, Action<string> consume, Action<ToolPermissionRequest> emit,
        Action<ToolPermissionRequest, string> activity, Action<string> warning, Action<string> fail, Action completed, Func<JsonObject, string?>? admit = null, bool freeChoices = false)
    {
        this.runId = runId; this.resumeId = resumeId; this.workspacePath = workspacePath; this.promptText = promptText; this.images = images; this.configValues = configValues;
        this.write = write; this.consume = consume; this.emit = emit; this.activity = activity; this.warning = warning; this.fail = fail; this.completed = completed;
        this.admit = admit; this.freeChoices = freeChoices;
    }

    /// <summary>
    /// The prompt's text block (M/MightyCore/ACPChannel.swift prompt): the request (or the attachments' default words), each attached copy that
    /// is not a picture, and the words for the agent that follow the user's request (Hermes's clarify note), a blank line apart.
    /// </summary>
    public static string PromptText(string input, IEnumerable<string> references, string? note) =>
        string.Join("\n\n", new[] { string.IsNullOrWhiteSpace(input) ? Locale.Get("attachments.prompt.default") : input }.Concat(references).Concat(note is null ? [] : [note]));

    public void Start()
    {
        lock (sync)
        {
            if (started || closed) return; started = true;
            Call("initialize", new JsonObject { ["protocolVersion"] = 1, ["clientCapabilities"] = ClientCapabilities(), ["clientInfo"] = new JsonObject { ["name"] = "mighty-code", ["title"] = "Mighty Code", ["version"] = "1.0" } });
        }
    }
    public void InitializationTimedOut() { lock (sync) if (!closed && !Ready) FailClosed(Locale.Get("acp.channel.initTimeout")); }
    public void FrameTooLarge() { lock (sync) FailClosed(Locale.Get("acp.channel.frameTooLarge")); }
    /// <summary>EOF before the prompt's answer is not success.</summary>
    public void Flush() { lock (sync) if (!closed) FailClosed(Locale.Get("acp.channel.closedEarly")); }

    /// <summary>One line of the agent's standard output.</summary>
    public void Receive(string line)
    {
        lock (sync)
        {
            if (closed) return;
            JsonNode? node = null;
            try { if (line.TrimStart().StartsWith('{')) node = JsonNode.Parse(line, documentOptions: new JsonDocumentOptions { MaxDepth = 256 }); }
            catch (JsonException) { }
            if (node is not JsonObject frame)
            {
                // An agent may print a plain line (a warning) before or beside its frames: shown, not fatal.
                var text = ActivitySupport.Clean(line.Length > 4_096 ? line[..4_096] : line, 4_096);
                if (text.Length > 0) Event(new JsonObject { ["type"] = "acp.output", ["text"] = text });
                return;
            }
            if (Text(frame["method"]) is { } method)
            {
                var parameters = frame["params"] as JsonObject ?? [];
                if (frame["id"] is { } id && id.GetValueKind() != JsonValueKind.Null) AgentRequest(id, method, parameters);
                else Notification(method, parameters);
                return;
            }
            if (Key(frame["id"]) is not { } key || !rpc.Remove(key, out var called)) return;
            if (frame["error"] is JsonObject error) { Failure(called, error); return; }
            Answered(called, frame["result"] as JsonObject ?? []);
        }
    }

    private void Answered(string method, JsonObject result)
    {
        switch (method)
        {
            case "initialize":
                Initialized = true;
                var capabilities = result["agentCapabilities"] as JsonObject ?? [];
                canLoad = capabilities["loadSession"] is JsonValue load && load.GetValueKind() == JsonValueKind.True;
                canResume = (capabilities["sessionCapabilities"] as JsonObject)?["resume"] is not null;
                if (admit?.Invoke(result) is { } refusal)
                {
                    Event(new JsonObject { ["type"] = "acp.error", ["method"] = method, ["code"] = -32000, ["message"] = refusal, ["signIn"] = true });
                    Failed = true; CancelAllLocked(); completed(); return;
                }
                OpenSession();
                break;
            case "session/new":
                if (Text(result["sessionId"]) is not { } id || !Wire.SessionIdentifier(id)) { FailClosed(Locale.Get("acp.channel.badSession")); return; }
                SessionOpened(id, result);
                break;
            case "session/resume" or "session/load":
                replaying = false;
                if (resumeId is null) { FailClosed(Locale.Get("acp.channel.badSession")); return; }
                SessionOpened(resumeId, result);
                break;
            case "session/set_config_option":
                if (result["configOptions"] is JsonArray options) configOptions = (JsonArray)options.DeepClone();
                NextConfig();
                break;
            case "session/prompt":
                if (SessionId is null) return;
                var value = new JsonObject { ["type"] = "acp.prompt_result", ["sessionId"] = SessionId, ["stopReason"] = Text(result["stopReason"]) ?? "end_turn" };
                if (result["usage"] is { } usage) value["usage"] = usage.DeepClone();
                Event(value);
                TurnCompleted = true; Cancellation.Set(null); CancelAllLocked(); completed();
                break;
        }
    }

    private void OpenSession()
    {
        var parameters = new JsonObject { ["cwd"] = workspacePath, ["mcpServers"] = new JsonArray() };
        if (resumeId is not null && (canResume || canLoad))
        {
            parameters["sessionId"] = resumeId;
            if (canResume) Call("session/resume", parameters); else { replaying = true; Call("session/load", parameters); }
        }
        else Call("session/new", parameters);
    }

    private void SessionOpened(string id, JsonObject result)
    {
        SessionId = id;
        configOptions = result["configOptions"] is JsonArray options ? (JsonArray)options.DeepClone() : [];
        var value = new JsonObject { ["type"] = "acp.session", ["sessionId"] = id };
        if (result["configOptions"] is { } offered) value["configOptions"] = offered.DeepClone();
        Event(value);
        queuedConfig.Clear(); foreach (var config in configValues) queuedConfig.Enqueue(config);
        NextConfig();
    }

    /// <summary>One setting at a time; one the agent does not offer is named and left out.</summary>
    private void NextConfig()
    {
        if (SessionId is not { } session) return;
        while (queuedConfig.TryDequeue(out var next))
        {
            var option = configOptions.OfType<JsonObject>().FirstOrDefault(o => Text(o["id"]) == next.Id);
            if (option is not null)
            {
                var values = OptionValues(option);
                if (values.Count > 0 && !values.Contains(next.Value)) { Unavailable(next); continue; }
                if (Text(option["currentValue"]) == next.Value) continue;
            }
            else if (configOptions.Count > 0) { Unavailable(next); continue; }
            Call("session/set_config_option", new JsonObject { ["sessionId"] = session, ["configId"] = next.Id, ["value"] = next.Value });
            return;
        }
        Prompt(session);
    }
    private void Unavailable((string Id, string Value) setting) =>
        warning(Locale.Get("acp.channel.optionUnavailable", new Dictionary<string, string> { ["option"] = setting.Id, ["value"] = ActivitySupport.Clean(setting.Value, 120, singleLine: true) }));

    /// <summary>The values of a <c>select</c> option, flat or grouped.</summary>
    public static List<string> OptionValues(JsonObject option)
    {
        var values = new List<string>();
        foreach (var entry in (option["options"] as JsonArray ?? []).OfType<JsonObject>().Take(2_048))
        {
            if (Text(entry["value"]) is { } value) values.Add(value);
            foreach (var inner in (entry["options"] as JsonArray ?? []).OfType<JsonObject>().Take(2_048)) if (Text(inner["value"]) is { } grouped) values.Add(grouped);
        }
        return values;
    }

    private void Prompt(string session)
    {
        if (Ready) return;
        Ready = true;
        var blocks = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = promptText } };
        foreach (var (mediaType, data) in images) blocks.Add(new JsonObject { ["type"] = "image", ["data"] = data, ["mimeType"] = mediaType });
        Event(new JsonObject { ["type"] = "acp.prompt", ["sessionId"] = session });
        Cancellation.Set(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "session/cancel", ["params"] = new JsonObject { ["sessionId"] = session } }.ToJsonString(Frames));
        Call("session/prompt", new JsonObject { ["sessionId"] = session, ["prompt"] = blocks });
    }

    private void Failure(string method, JsonObject error)
    {
        var message = ActivitySupport.Clean(Text(error["message"]) ?? "", 8_192);
        var code = error["code"] is JsonValue number && number.TryGetValue<double>(out var c) && double.IsFinite(c) && Math.Abs(c) < int.MaxValue ? (int)c : 0;
        switch (method)
        {
            case "session/resume" or "session/load":
                // The session is gone (never answered, deleted): the pane starts a new one.
                replaying = false;
                warning(Locale.Get("acp.channel.newSession"));
                Call("session/new", new JsonObject { ["cwd"] = workspacePath, ["mcpServers"] = new JsonArray() });
                break;
            case "session/set_config_option":
                warning(Locale.Get("acp.channel.optionFailed", new Dictionary<string, string> { ["error"] = message.Length == 0 ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : message }));
                NextConfig();
                break;
            default:
                // The parser shows the agent's words and reads a lost sign-in from them.
                var value = new JsonObject { ["type"] = "acp.error", ["method"] = method, ["code"] = code, ["message"] = message };
                if (error["data"] is { } data) value["data"] = data.DeepClone();
                Event(value);
                // The agent answered the prompt with an error: no turn is left to cancel.
                if (method == "session/prompt") Cancellation.Set(null);
                Failed = true; CancelAllLocked(); completed();
                break;
        }
    }

    private void Notification(string method, JsonObject parameters)
    {
        // The agent withdrew a request of its own (its turn was cancelled while it asked).
        if (method == "$/cancel_request" && Key(parameters["requestId"]) is { } withdrawn)
        {
            foreach (var id in pending.Where(p => Key(p.Value.RpcId) == withdrawn).Select(p => p.Key).ToList()) CancelPending(id);
            return;
        }
        if (method != "session/update" || replaying || SessionId is not { } session || Text(parameters["sessionId"]) != session
            || parameters["update"] is not JsonObject update || Text(update["sessionUpdate"]) is not { } kind) return;
        var settled = Text(update["status"]) is "completed" or "failed";
        if (kind is "tool_call" or "tool_call_update" && Text(update["toolCallId"]) is { Length: > 0 } call && Encoding.UTF8.GetByteCount(call) <= 256)
        {
            if (settled) { if (openCall == call) openCall = null; }
            else if (kind == "tool_call") openCall = call;
        }
        // A call that settled no longer waits for an answer. A question that goes this way was not answered in time (Hermes stops waiting
        // after `agent.clarify_timeout`): the pane says so.
        if (kind == "tool_call_update" && settled && Text(update["toolCallId"]) is { } settledCall)
            foreach (var key in pending.Where(p => p.Value.Display.ToolUseId == settledCall).Select(p => p.Key).ToList())
            {
                if (pending[key].Asked is not null) warning(Locale.Get("acp.channel.questionWithdrawn", new Dictionary<string, string> { ["question"] = pending[key].Display.Summary }));
                CancelPending(key);
            }
        Event(new JsonObject { ["type"] = "acp.update", ["sessionId"] = session, ["update"] = update.DeepClone() });
    }

    private void AgentRequest(JsonNode rawId, string method, JsonObject parameters)
    {
        if (Key(rawId) is not { } key) { FailClosed(Locale.Get("acp.channel.badRequestId")); return; }
        if (!seen.Add(key)) { FailClosed(Locale.Get("acp.channel.duplicateRequest")); return; }
        if (seen.Count > 2_048) { FailClosed(Locale.Get("acp.channel.tooManyRequests")); return; }
        if (method == "elicitation/create") { Elicitation(rawId, key, parameters); return; }
        // No file system or terminal was offered: anything else is not this client's.
        if (method != "session/request_permission") { Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = rawId.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not supported by this client." } }); return; }
        if (SessionId is not { } session || Text(parameters["sessionId"]) != session || parameters["toolCall"] is not JsonObject call)
        {
            Reply(rawId, new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } }); return;
        }
        var options = (parameters["options"] as JsonArray ?? []).OfType<JsonObject>().Take(16).ToList();
        string? Option(params string[] kinds)
        {
            foreach (var kind in kinds)
                if (options.FirstOrDefault(o => Text(o["kind"]) == kind) is { } found && Text(found["optionId"]) is { Length: > 0 } id && Encoding.UTF8.GetByteCount(id) <= 256) return id;
            return null;
        }
        string? allow = Option("allow_once"), reject = Option("reject_once", "reject_always");
        // Several "allow once" options are a question's choices (Hermes's clarify), not an approval: they are the question card's choices.
        // One the card cannot show is skipped and named instead of answered by guess.
        if (options.Count(o => Text(o["kind"]) == "allow_once") > 1)
        {
            if (pending.Count < MaximumPending && ChoiceQuestion(rawId, key, call, options, reject) is { } ask)
            {
                pending[ask.Display.Id] = ask;
                activity(ask.Display, "waiting"); emit(ask.Display); return;
            }
            Answer(rawId, reject);
            var question = ActivitySupport.Clean(Text(call["title"]) ?? "", 1_000, singleLine: true);
            var choices = string.Join(" / ", options.Where(o => Text(o["kind"]) == "allow_once").Select(o => Text(o["name"])).OfType<string>().Take(8).Select(name => ActivitySupport.Clean(name, 120, singleLine: true)));
            warning(Locale.Get("acp.channel.questionSkipped", new Dictionary<string, string> { ["question"] = question, ["choices"] = choices })); return;
        }
        if (pending.Count >= MaximumPending) { Answer(rawId, reject); warning(Locale.Get("acp.channel.tooManyPending")); return; }
        var callId = Text(call["toolCallId"]) is { Length: > 0 } named && Encoding.UTF8.GetByteCount(named) <= 256 && named.All(ch => ch > ' ' && ch != '\x7f') ? named : "permission-" + key;
        var display = DisplayJson(DisplayInput(call["rawInput"]));
        if (display is null || Encoding.UTF8.GetByteCount(display) > MaximumInputBytes) { Answer(rawId, reject); warning(Locale.Get("acp.channel.requestTooLarge")); return; }
        var title = ActivitySupport.Clean(Text(call["title"]) ?? "", 1_000, singleLine: true);
        var value = new ToolPermissionRequest(Wire.Id(), runId, callId, ToolName(Text(call["kind"]), Text(call["name"]) ?? Text(call["title"])), display,
            title.Length == 0 ? Locale.Get("acp.channel.permissionSummary") : title, Reason: allow is null ? Locale.Get("acp.channel.denyOnly") : null, CanAllow: allow is not null, ReceivedAt: Wire.Now());
        pending[value.Id] = new(rawId.DeepClone(), allow, reject, value);
        activity(value, "waiting"); emit(value);
    }

    /// <summary>Hermes's clarify as an approval: the tool call's title is the question and each <c>allow_once</c> option one choice. The answer is the picked option; nothing typed can travel.</summary>
    private Pending? ChoiceQuestion(JsonNode rawId, string key, JsonObject call, List<JsonObject> options, string? reject)
    {
        if (AgentQuestions.Text(Text((call["rawInput"] as JsonObject)?["question"]) ?? Text(call["title"]), 8_192) is not { } question) return null;
        var item = new AgentQuestions.Item(Locale.Get("questionnaire.header.agent"), question);
        var choices = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var option in options.Where(o => Text(o["kind"]) == "allow_once"))
        {
            if (Text(option["optionId"]) is not { Length: > 0 } id || Encoding.UTF8.GetByteCount(id) > 256
                || AgentQuestions.Text(Text(option["name"]), 1_024, singleLine: true) is not { } label || choices.ContainsKey(label)) return null;
            choices[label] = id; item.Options.Add((label, ""));
        }
        var callId = Text(call["toolCallId"]) is { Length: > 0 } named && Encoding.UTF8.GetByteCount(named) <= 256 ? named : "question-" + key;
        if (AgentQuestions.Request(Wire.Id(), runId, callId, [item]) is not { } display) return null;
        return new(rawId.DeepClone(), null, reject, display with { ReceivedAt = Wire.Now() }, new Question(null, choices));
    }

    /// <summary><c>elicitation/create</c>: a form the question card asks, or one it cannot (a number, a nested object, more than the card holds), which is declined and named.</summary>
    private void Elicitation(JsonNode rawId, string key, JsonObject parameters)
    {
        if (SessionId is not { } session || Text(parameters["sessionId"]) != session) { Reply(rawId, new JsonObject { ["action"] = "cancel" }); return; }
        var callId = Text(parameters["toolCallId"]) is { Length: > 0 } named && Encoding.UTF8.GetByteCount(named) <= 256 ? named : openCall;
        if (pending.Count >= MaximumPending || AcpElicitationForm.Read(parameters, freeChoices) is not { } form
            || AgentQuestions.Request(Wire.Id(), runId, callId ?? "question-" + key, form.Items) is not { } display)
        {
            Reply(rawId, new JsonObject { ["action"] = "decline" });
            warning(Locale.Get("acp.channel.formDeclined", new Dictionary<string, string> { ["question"] = ActivitySupport.Clean(Text(parameters["message"]) ?? "", 1_000, singleLine: true) })); return;
        }
        var value = display with { ReceivedAt = Wire.Now() };
        pending[value.Id] = new(rawId.DeepClone(), null, null, value, new Question(form, null), callId is not null);
        if (callId is not null) activity(value, "waiting");
        emit(value);
    }

    /// <summary>The question card's answers, sent as the form's content or the picked choice's option.</summary>
    public void AnswerQuestions(string requestId, IReadOnlyDictionary<string, UserQuestionAnswer> answers)
    {
        lock (sync)
        {
            if (closed || !pending.TryGetValue(requestId, out var ask) || ask.Asked is not { } asked) throw new InvalidOperationException(Locale.Get("permission.error.questionGone"));
            var checkedAnswers = AgentQuestions.Answers(ask.Display, answers);
            if (asked.Form is { } form)
            {
                // Checked first: an answer the form cannot carry leaves the card answerable.
                var content = form.Content(checkedAnswers);
                pending.Remove(requestId);
                Reply(ask.RpcId, new JsonObject { ["action"] = "accept", ["content"] = content });
            }
            else
            {
                var answer = checkedAnswers.Values.FirstOrDefault();
                if (answer.Typed is not { Length: 0 } || answer.Picked is not [var picked] || asked.Choices?.GetValueOrDefault(picked) is not { } option)
                    throw new ArgumentException(Locale.Get("questionnaire.error.choicesOnly"));
                pending.Remove(requestId);
                Answer(ask.RpcId, option);
            }
            var value = ask.Display with { State = "answered" };
            if (ask.Row) activity(value, "running");
            emit(value);
        }
    }

    /// <summary>이번만 허용 / 거부, or a question's Cancel: the form is declined, the clarify approval skipped.</summary>
    public void Respond(string requestId, bool allow)
    {
        lock (sync)
        {
            if (closed || !pending.TryGetValue(requestId, out var ask)) throw new InvalidOperationException(ToolPermissionStrings.AlreadySettled);
            if (allow && ask.AllowOption is null) throw new InvalidOperationException(Locale.Get("acp.channel.denyOnly"));
            pending.Remove(requestId);
            if (ask.Asked?.Form is not null) Reply(ask.RpcId, new JsonObject { ["action"] = "decline" });
            else Answer(ask.RpcId, allow ? ask.AllowOption : ask.RejectOption);
            var value = ask.Display with { State = allow ? "allowed" : "denied" };
            // A dismissed question's call is settled by the agent's own update, not marked failed here.
            if (ask.Row) activity(value, allow || ask.Asked is not null ? "running" : "error");
            emit(value);
        }
    }

    /// <summary>The option the user chose; without a reject option the request is cancelled, which the agent reads as a no.</summary>
    private void Answer(JsonNode? id, string? optionId) =>
        Reply(id, new JsonObject { ["outcome"] = optionId is null ? new JsonObject { ["outcome"] = "cancelled" } : new JsonObject { ["outcome"] = "selected", ["optionId"] = optionId } });

    private void CancelPending(string id)
    {
        if (!pending.Remove(id, out var ask)) return;
        var value = ask.Display with { State = "cancelled" };
        if (ask.Asked is null) activity(value, "stopped");
        emit(value);
    }

    public void CancelAll() { lock (sync) CancelAllLocked(); }
    private void CancelAllLocked()
    {
        if (closed) return; closed = true; rpc.Clear();
        var asks = pending.Values.ToList(); pending.Clear();
        foreach (var ask in asks) { var value = ask.Display with { State = "cancelled" }; if (ask.Row) activity(value, "stopped"); emit(value); }
    }
    private void FailClosed(string message) { if (closed) return; Failed = true; CancelAllLocked(); fail(message); }

    private void Call(string method, JsonObject parameters)
    {
        sequence++; var id = "mighty-" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        rpc["s:" + id] = method;
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
    }
    private void Reply(JsonNode? id, JsonObject result) => Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result });
    private void Send(JsonObject frame) => write(frame.ToJsonString(Frames) + "\n");
    private void Event(JsonObject value) => consume(value.ToJsonString(Frames));

    private static readonly JsonSerializerOptions Frames = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string? Text(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>A JSON-RPC id as a key: a string (1–256 bytes) or a whole number; 0 is an id like any other.</summary>
    public static string? Key(JsonNode? raw)
    {
        if (raw is not JsonValue value) return null;
        if (value.GetValueKind() == JsonValueKind.String) return value.GetValue<string>() is { Length: > 0 } text && Encoding.UTF8.GetByteCount(text) <= 256 ? "s:" + text : null;
        if (value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<double>(out var number) && double.IsFinite(number) && Math.Round(number) == number && Math.Abs(number) < 1e15)
            return "n:" + ((long)number).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return null;
    }

    /// <summary>ACP's tool kinds named as the approval card and the activity rows already read them.</summary>
    public static string ToolName(string? kind, string? title) => kind switch
    {
        "execute" => "Bash",
        "edit" or "delete" or "move" => "Edit",
        "read" => "Read",
        "search" => "Grep",
        "fetch" => "WebFetch",
        _ => ActivitySupport.Clean(title ?? "", 80, singleLine: true) is { Length: > 0 } name ? name : "Tool",
    };

    /// <summary>
    /// A tool's input with camelCase keys in the snake_case the card and the activity summary read (<c>filePath</c> → <c>file_path</c>); anything
    /// that is not an object is shown under <c>input</c>. An input wrapped as <c>{tool, arguments}</c> (Hermes's edit approvals) is shown as its arguments.
    /// </summary>
    public static JsonObject DisplayInput(JsonNode? raw)
    {
        if (raw is not JsonObject fields) return raw is null || raw.GetValueKind() == JsonValueKind.Null ? [] : new JsonObject { ["input"] = raw.DeepClone() };
        if (fields.Count == 2 && Text(fields["tool"]) is not null && fields["arguments"] is JsonObject arguments) fields = arguments;
        var result = new JsonObject();
        foreach (var (key, value) in fields.Take(64))
        {
            var snake = new StringBuilder();
            foreach (var character in key)
            {
                if (char.IsUpper(character) && snake.Length > 0 && snake[^1] != '_') snake.Append('_');
                snake.Append(char.ToLowerInvariant(character));
            }
            var name = snake.ToString() == "filepath" ? "file_path" : snake.ToString();
            result[name] = value?.DeepClone();
        }
        return result;
    }

    /// <summary>What a tool call without <c>rawInput</c> still says about itself: its first location's path, a shell line its content shows (<c>$ ls</c>), or the title's part after <c>tool: </c>.</summary>
    public static JsonObject? DescribedInput(JsonObject call)
    {
        if ((call["locations"] as JsonArray)?.FirstOrDefault() is JsonObject location && Text(location["path"]) is { Length: > 0 } path) return new JsonObject { ["file_path"] = path };
        foreach (var block in (call["content"] as JsonArray ?? []).OfType<JsonObject>().Take(4))
            if ((block["content"] as JsonObject) is { } inner && Text(inner["text"]) is { } text && text.StartsWith("$ ", StringComparison.Ordinal))
                return new JsonObject { ["command"] = ActivitySupport.PrefixUtf8(text[2..], 8_192) };
        if (Text(call["title"]) is not { } title || title.IndexOf(": ", StringComparison.Ordinal) is var colon && colon < 0) return null;
        var rest = title[(colon + 2)..];
        return rest.Length == 0 ? null : new JsonObject { ["description"] = rest };
    }

    /// <summary>A title of the form <c>tool: preview</c> names its tool before the colon; any other title is the name.</summary>
    public static string? TitledTool(string? title)
    {
        if (title is null || title.IndexOf(": ", StringComparison.Ordinal) is var colon && colon < 0) return title;
        var name = title[..colon];
        return Regex.IsMatch(name, @"^[A-Za-z0-9_.-]{1,64}\z") ? name : title;
    }

    /// <summary>The card's JSON: indented, keys sorted, format characters (bidirectional overrides and the like) escaped so they cannot hide text.</summary>
    private static string? DisplayJson(JsonObject value)
    {
        try
        {
            var sorted = Sorted(value)!;
            var text = sorted.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var result = new StringBuilder();
            foreach (var rune in text.EnumerateRunes())
            {
                if (Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.Format) foreach (var unit in rune.ToString()) result.Append("\\u").Append(((int)unit).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                else result.Append(rune);
            }
            return result.ToString();
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or ArgumentException) { return null; }
    }
    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
        JsonArray array => new JsonArray([.. array.Select(Sorted)]),
        _ => node?.DeepClone(),
    };
}

/// <summary>A turn's <c>session/cancel</c> frame behind a lock, so the stop (sent before the process is ended) can read it once.</summary>
public sealed class AcpCancellation
{
    private readonly object gate = new();
    private string? frame;
    public void Set(string? value) { lock (gate) frame = value is null ? null : value + "\n"; }
    /// <summary>The frame, once: a second stop does not send it again.</summary>
    public string? Take() { lock (gate) { var value = frame; frame = null; return value; } }
}
