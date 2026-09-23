using System.Text.Json;
using MightyClaude.Core;

// The stdio permission channel, proven on a Mac with injected send and event
// callbacks instead of a CLI process. Every check here is a fail-closed rule:
// nothing is allowed without a user action on that specific request.
internal static class ToolPermissionVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action, string message) { try { action(); } catch (InvalidOperationException) { return; } throw new InvalidOperationException(message); }

    private const string Prompt = "{\"type\":\"user\",\"message\":{\"content\":\"prompt\"}}\n";

    private static string Ask(string id = "ask-1", string tool = "Read", string input = "{\"file_path\":\"~/.claude/CLAUDE.md\"}", bool interaction = false) =>
        "{\"type\":\"control_request\",\"request_id\":\"" + id + "\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"" + tool
        + "\",\"tool_use_id\":\"tool-" + id + "\",\"input\":" + input
        + ",\"decision_reason\":\"Read outside the workspace\",\"blocked_path\":\"~/.claude/CLAUDE.md\",\"requires_user_interaction\":" + (interaction ? "true" : "false") + "}}";

    private static void Initialize(ClaudePermissionChannel channel) =>
        channel.Receive("{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"" + channel.InitializationId + "\",\"response\":{}}}");

    /// The `response.response` payload of the last control_response written.
    private static JsonElement Result(string line) => JsonDocument.Parse(line).RootElement.GetProperty("response").GetProperty("response").Clone();
    private static string Subtype(string line) => JsonDocument.Parse(line).RootElement.GetProperty("response").GetProperty("subtype").GetString()!;

    private sealed class Channel
    {
        internal readonly List<string> Writes = [], States = [], Warnings = [], Failures = [];
        internal readonly List<ToolPermissionRequest> Displays = [];
        internal readonly ClaudePermissionChannel Value;
        internal Channel(string runId = "run-1", string prompt = Prompt)
            => Value = new ClaudePermissionChannel(runId, prompt, Writes.Add, Displays.Add, (_, state) => States.Add(state), Warnings.Add, Failures.Add);
        internal string Last => Writes[^1];
    }

    internal static Task HandshakeRunsBeforeThePrompt()
    {
        var channel = new Channel();
        channel.Value.Start();
        Check(channel.Writes.Count == 1 && channel.Last != Prompt, "start must send initialize, not the prompt");
        var initialize = JsonDocument.Parse(channel.Last).RootElement;
        Check(initialize.GetProperty("type").GetString() == "control_request", "the handshake is a control_request");
        Check(initialize.GetProperty("request").GetProperty("subtype").GetString() == "initialize", "the handshake subtype is initialize");
        Check(initialize.GetProperty("request_id").GetString() == channel.Value.InitializationId, "the handshake carries the channel id");
        Check(!channel.Value.Initialized, "the channel is not initialized before a success");
        Initialize(channel.Value);
        Check(channel.Value.Initialized && channel.Last == Prompt, "the prompt follows a successful handshake");
        Initialize(channel.Value);
        Check(channel.Writes.Count == 2, "a repeated handshake success sends nothing further");
        return Task.CompletedTask;
    }

    internal static Task HandshakeFailureAndTimeoutFailClosed()
    {
        var rejected = new Channel(prompt: "never");
        rejected.Value.Start();
        rejected.Value.Receive("{\"type\":\"control_response\",\"response\":{\"subtype\":\"error\",\"request_id\":\"" + rejected.Value.InitializationId + "\",\"error\":\"not supported\"}}");
        Check(rejected.Value.Failed && rejected.Failures.Count == 1, "a rejected handshake fails the channel");
        Check(!rejected.Writes.Contains("never"), "a rejected handshake never sends the prompt");
        Check(rejected.Failures[0] == ToolPermissionStrings.InitializeFailed, "the failure uses the macOS copy");

        var timed = new Channel(prompt: "never");
        timed.Value.Receive(Ask());
        Check(timed.Displays.Count == 1, "a request before the handshake still shows");
        timed.Value.InitializationTimedOut();
        Check(timed.Value.Failed && timed.Failures[0] == ToolPermissionStrings.InitializeTimedOut, "a timed-out handshake fails closed");
        Check(timed.Displays[^1].State == "cancelled", "a timed-out handshake settles waiting requests");
        Reject(() => timed.Value.Respond("ask-1", true), "a failed channel must not allow anything");
        Initialize(timed.Value);
        Check(!timed.Writes.Contains("never"), "a closed channel never sends the prompt");
        return Task.CompletedTask;
    }

    internal static Task AllowOnceReturnsTheOriginalInput()
    {
        const string input = """{"query":"official documentation","allowed_domains":["example.com"],"nested":{"unchanged":true}}""";
        var channel = new Channel();
        var ask = Ask(tool: "WebSearch", input: input);
        channel.Value.Receive(ask);
        channel.Value.Receive(ask);
        Check(channel.Displays.Count == 1, "a replayed request id is answered once");
        Check(channel.Displays[0].RunId == "run-1" && channel.Displays[0].CanAllow, "a plain tool request can be allowed");
        Check(channel.Displays[0].BlockedPath == "~/.claude/CLAUDE.md", "the blocked path is shown");
        Check(channel.Displays[0].Reason == "Read outside the workspace", "the reason is shown");
        Check(channel.Value.Waiting.Count == 1, "one request waits");

        channel.Value.Respond("ask-1", true);
        var result = Result(channel.Last);
        Check(result.GetProperty("behavior").GetString() == "allow", "이번만 허용 answers allow");
        Check(result.GetProperty("updatedInput").GetRawText() == input, "allow returns the original input unchanged");
        Check(result.GetProperty("toolUseID").GetString() == "tool-ask-1", "allow carries the tool use id");
        Check(!result.TryGetProperty("updatedPermissions", out _), "allow never returns persistent rules");
        Check(!result.TryGetProperty("updatedSettings", out _) && !result.TryGetProperty("mode", out _), "allow never returns settings or a mode");
        Check(channel.Displays.Select(d => d.State).SequenceEqual(["pending", "allowed"]), "the request settles to allowed");
        Check(channel.States.SequenceEqual(["waiting", "running"]), "the activity row follows the answer");
        Reject(() => channel.Value.Respond("ask-1", true), "a settled request cannot be allowed twice");
        channel.Value.Receive(ask);
        Check(channel.Writes.Count == 1 && channel.Value.Waiting.Count == 0, "a settled request is not reopened");
        return Task.CompletedTask;
    }

    internal static Task DenyNeverReturnsAllow()
    {
        var channel = new Channel();
        channel.Value.Receive(Ask());
        channel.Value.Respond("ask-1", false);
        var result = Result(channel.Last);
        Check(result.GetProperty("behavior").GetString() == "deny", "거부 answers deny");
        Check(result.GetProperty("toolUseID").GetString() == "tool-ask-1", "deny carries the tool use id");
        Check(result.GetProperty("message").GetString() is { Length: > 0 }, "deny carries a message");
        Check(!result.TryGetProperty("updatedInput", out _) && !result.TryGetProperty("updatedPermissions", out _), "deny returns no input and no rules");
        Check(channel.Displays[^1].State == "denied" && channel.States[^1] == "error", "the request settles to denied");
        Reject(() => channel.Value.Respond("ask-1", false), "a settled request cannot be denied twice");
        Check(channel.Writes.All(w => Result(w).GetProperty("behavior").GetString() == "deny"), "no write allowed anything");
        return Task.CompletedTask;
    }

    internal static Task OnlyCanUseToolIsAcceptedAndIdentifiersAreValidated()
    {
        var channel = new Channel();
        channel.Value.Receive("""{"type":"control_request","request_id":"other","request":{"subtype":"set_permission_mode","mode":"bypassPermissions"}}""");
        Check(Subtype(channel.Last) == "error", "an unknown subtype is answered with an error");
        Check(channel.Displays.Count == 0 && !channel.Value.Failed, "an unknown subtype shows nothing and keeps the channel open");
        channel.Value.Receive("""{"type":"control_request","request_id":"dialog","request":{"subtype":"request_user_dialog"}}""");
        Check(channel.Writes.Count == 1 && channel.Warnings[^1] == ToolPermissionStrings.UnsupportedDialog, "a dialog request is left to the CLI with a warning");
        channel.Value.Receive("""{"type":"control_request","request_id":"elicit","request":{"subtype":"elicitation"}}""");
        Check(Result(channel.Last).GetProperty("action").GetString() == "decline", "an elicitation is declined");
        channel.Value.Receive(Ask("bad", tool: "", input: "{}"));
        Check(Subtype(channel.Last) == "error" && channel.Warnings[^1] == ToolPermissionStrings.DeniedMalformedRequest, "an empty tool name is refused");
        channel.Value.Receive("""{"type":"control_request","request_id":"noinput","request":{"subtype":"can_use_tool","tool_name":"Read","tool_use_id":"tool-noinput"}}""");
        Check(Subtype(channel.Last) == "error", "a request without an input object is refused");
        channel.Value.Receive("""{"type":"control_request","request_id":"badtool","request":{"subtype":"can_use_tool","tool_name":"Read","tool_use_id":"tool id","input":{}}}""");
        Check(Subtype(channel.Last) == "error", "an invalid tool use id is refused");
        Check(channel.Displays.Count == 0, "nothing malformed ever reaches the bar");
        channel.Value.Receive("not json at all");
        Check(!channel.Value.Failed, "a non-JSON line is ignored");

        var broken = new Channel();
        broken.Value.Receive("""{"type":"control_request","request_id":"bad id","request":{"subtype":"can_use_tool","tool_name":"Read","tool_use_id":"tool-1","input":{}}}""");
        Check(broken.Value.Failed && broken.Failures[0] == ToolPermissionStrings.MalformedControlRequest, "an invalid request id fails the channel closed");
        return Task.CompletedTask;
    }

    internal static Task PendingRequestsAreCappedAtSixteen()
    {
        var channel = new Channel();
        for (var index = 0; index < 17; index++) channel.Value.Receive(Ask("queued-" + index));
        Check(channel.Displays.Count == ClaudePermissionChannel.MaximumPending, "the bar never holds more than 16 requests");
        Check(channel.Value.Waiting.Count == 16, "16 requests wait");
        Check(channel.Writes.Count == 1 && Result(channel.Last).GetProperty("behavior").GetString() == "deny", "the seventeenth request is denied");
        Check(channel.Warnings[^1] == ToolPermissionStrings.DeniedTooManyPending, "the cap warning uses the macOS copy");
        channel.Value.Respond("queued-0", false);
        channel.Value.Receive(Ask("queued-17"));
        Check(channel.Value.Waiting.Count == 16 && channel.Displays[^1].Id == "queued-17", "a freed slot accepts the next request");
        return Task.CompletedTask;
    }

    internal static Task InputTooLargeToShowIsDenied()
    {
        var channel = new Channel();
        var big = JsonSerializer.Serialize(new { command = new string('x', ClaudePermissionChannel.MaximumInputBytes) });
        channel.Value.Receive(Ask("large", tool: "Bash", input: big));
        Check(channel.Displays.Count == 0, "an input that cannot be shown completely never reaches the bar");
        Check(Result(channel.Last).GetProperty("behavior").GetString() == "deny", "an oversized input is denied");
        Check(channel.Warnings[^1] == ToolPermissionStrings.DeniedOversizedInput, "the size warning uses the macOS copy");

        var oversizedReason = new Channel();
        oversizedReason.Value.Receive("{\"type\":\"control_request\",\"request_id\":\"wordy\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"Read\",\"tool_use_id\":\"tool-wordy\",\"input\":{\"file_path\":\"/w/a\"},\"decision_reason\":\"" + new string('r', 9000) + "\"}}");
        Check(oversizedReason.Displays[^1].CanAllow == false, "a reason that cannot be shown completely cannot be allowed");
        Check(oversizedReason.Displays[^1].Reason!.EndsWith(ToolPermissionStrings.MetadataTooLarge, StringComparison.Ordinal), "the bar says why it cannot be allowed");
        Reject(() => oversizedReason.Value.Respond("wordy", true), "an undisplayable reason must not be allowed");
        oversizedReason.Value.Respond("wordy", false);
        Check(Result(oversizedReason.Last).GetProperty("behavior").GetString() == "deny", "it can still be denied");
        return Task.CompletedTask;
    }

    internal static Task QuestionnairesAndExtraScreensCanOnlyBeDenied()
    {
        var channel = new Channel();
        channel.Value.Receive(Ask("question", tool: "AskUserQuestion", input: """{"questions":[{"question":"Choose a region"}]}"""));
        Check(channel.Displays[^1].CanAllow == false && channel.Displays[^1].CanAnswerQuestions == false, "a questionnaire cannot be allowed or answered here");
        Check(channel.Displays[^1].Reason!.Contains(ToolPermissionStrings.NeedsSeparateInputScreen, StringComparison.Ordinal), "the bar shows the macOS sentence for a request it cannot allow");
        Reject(() => channel.Value.Respond("question", true), "a questionnaire must not be allowed");
        channel.Value.Respond("question", false);
        Check(Result(channel.Last).GetProperty("behavior").GetString() == "deny", "a questionnaire can be denied");

        channel.Value.Receive(Ask("special", interaction: true));
        Check(channel.Displays[^1].CanAllow == false, "a request that needs its own screen cannot be allowed");
        Reject(() => channel.Value.Respond("special", true), "an interaction request must not be allowed");
        Check(channel.Writes.All(w => Result(w).GetProperty("behavior").GetString() == "deny"), "nothing was ever allowed");
        return Task.CompletedTask;
    }

    internal static Task StoppingTheRunSettlesEveryWaitingRequest()
    {
        var channel = new Channel();
        channel.Value.Receive(Ask("first"));
        channel.Value.Receive(Ask("second"));
        channel.Value.Receive("""{"type":"control_cancel_request","request_id":"second"}""");
        Check(channel.Displays[^1] is { Id: "second", State: "cancelled" }, "a cancelled request settles");
        Reject(() => channel.Value.Respond("second", true), "a cancelled request cannot be allowed");
        channel.Value.Receive(Ask("third"));
        Check(channel.Value.Waiting.Count == 2, "two requests wait when the run stops");
        channel.Value.CancelAll();
        Check(channel.Value.Waiting.Count == 0, "stopping the run leaves nothing waiting");
        Check(channel.Displays.Where(d => d.State == "cancelled").Select(d => d.Id).SequenceEqual(["second", "first", "third"]), "every waiting request is cancelled");
        Check(channel.States[^1] == "stopped", "the activity rows stop");
        Reject(() => channel.Value.Respond("first", true), "a stopped run allows nothing");
        channel.Value.Receive(Ask("late"));
        Check(channel.Writes.Count == 0, "a stopped channel writes nothing further");
        channel.Value.CancelAll();
        return Task.CompletedTask;
    }

    internal static Task BarShowsTheTitleSummaryReasonPathAndCount()
    {
        var channel = new Channel();
        channel.Value.Receive(Ask("one", tool: "Bash", input: """{"command":"rm -rf build","description":"Clear the build folder"}"""));
        channel.Value.Receive(Ask("two", tool: "Write", input: """{"file_path":"/w/b.md","content":"# Title"}"""));
        var waiting = channel.Value.Waiting;
        Check(waiting.Count == 2 && waiting[0].Id == "one", "the bar shows the oldest waiting request");

        var first = waiting[0];
        var presentation = ToolPermissionPresentation.Make(first.ToolName, first.InputJson);
        Check(presentation.Title == ToolPermissionStrings.TitleBash, "the bar shows what the tool does");
        Check(presentation.Headline == "Clear the build folder", "the bar shows what it wants to do");
        Check(presentation.Fields.Select(f => f.Label).SequenceEqual([ToolPermissionStrings.FieldCommand]), "the command is a named field");
        Check(presentation.Fields[0].Value == "rm -rf build" && presentation.Fields[0].Code, "the command is shown verbatim");
        Check(ToolPermissionStrings.BarTitleTemplate.Replace("{title}", presentation.Title) == "명령 실행 · 승인 요청", "the bar title is the macOS title");
        Check(ToolPermissionStrings.BarWaitingCountTemplate.Replace("{count}", waiting.Count.ToString()) == "2개 대기", "the bar shows the waiting count");
        Check(ToolPermissionStrings.BarPathTemplate.Replace("{path}", first.BlockedPath!) == "접근 경로: ~/.claude/CLAUDE.md", "the bar shows the path");
        Check(first.Summary == "rm -rf build", "the summary is the tool's own target");
        Check(ToolPermissionPresentation.Make(waiting[1].ToolName, waiting[1].InputJson).Title == ToolPermissionStrings.TitleWrite, "each tool has its own title");

        var mcp = ToolPermissionPresentation.Make("mcp__mcp-gcoo__execute_sql", """{"sql":"select 1"}""");
        Check(mcp.Title == "MCP 도구 · mcp-gcoo", "an MCP tool names its server");
        var unknown = ToolPermissionPresentation.Make("Custom", "not json");
        Check(unknown.Title == ToolPermissionStrings.TitleTool && unknown.Headline is null && unknown.Fields.Count == 0, "an unreadable input falls back to the plain title");
        var many = ToolPermissionPresentation.Make("Custom", "{" + string.Join(",", Enumerable.Range(0, 20).Select(i => $"\"k{i}\":\"v\"")) + "}");
        Check(many.Fields.Count == ToolPermissionPresentation.MaximumFields, "the field list is bounded");

        // Invisible controls are made explicit; the approved input is untouched.
        var hidden = new Channel();
        hidden.Value.Receive(Ask("hidden", tool: "WebSearch", input: """{"query":"‮visible"}"""));
        Check(hidden.Displays[^1].InputJson.Contains("\\u202e", StringComparison.Ordinal), "an invisible control is shown as an escape");
        hidden.Value.Respond("hidden", true);
        Check(Result(hidden.Last).GetProperty("updatedInput").GetProperty("query").GetString() == "‮visible", "allow still sends the original input");
        return Task.CompletedTask;
    }

    private static StartRunRequest Claude(string id) => new(id, "workspace-fixture", "claude", "fixture prompt", []);

    /// <summary>
    /// launch_arguments: the bar's own path gets host prompts over stdio; every
    /// other path — remote workspaces, headless hosts, other CLIs — keeps
    /// --permission-prompts none exactly as before.
    /// </summary>
    internal static Task HostPromptsOnlyWhereTheBarExists()
    {
        var quiet = ProviderInput.Prepare(Claude("quiet"), "/fixture-plugin", allowPermissionPrompts: false).Arguments;
        Check(quiet[quiet.IndexOf("--permission-prompts") + 1] == "none", "a host that cannot show the bar keeps prompts off");
        Check(!quiet.Contains("--permission-prompt-tool") && !quiet.Contains("--input-format"), "no stdio prompt tool is offered without the bar");

        var hosted = ProviderInput.Prepare(Claude("hosted"), "/fixture-plugin", allowPermissionPrompts: true).Arguments;
        Check(hosted[hosted.IndexOf("--permission-prompts") + 1] == "host", "the bar's path asks this host for prompts");
        Check(hosted[hosted.IndexOf("--permission-prompt-tool") + 1] == "stdio", "the prompts arrive over stdio");
        Check(hosted[hosted.IndexOf("--input-format") + 1] == "stream-json", "stdin carries stream-json so replies are possible");

        var codex = ProviderInput.Prepare(Claude("codex-run") with { Provider = "codex" }, "/fixture-plugin", allowPermissionPrompts: true).Arguments;
        Check(!codex.Contains("--permission-prompt-tool"), "only Claude speaks this protocol");

        var frame = ProviderInput.PromptFrame("안녕");
        Check(frame.EndsWith("\n", StringComparison.Ordinal), "the prompt frame is newline framed");
        Check(JsonDocument.Parse(frame).RootElement.GetProperty("message").GetProperty("content").GetString() == "안녕", "the prompt frame carries the input");

        Check(ClaudeStream.IsTurnResult("{\"type\":\"result\",\"subtype\":\"success\"}"), "the CLI's own result ends the turn");
        Check(!ClaudeStream.IsTurnResult("{\"type\":\"result\",\"origin\":{\"kind\":\"task-notification\"}}"), "a task notification does not end the turn");
        Check(!ClaudeStream.IsTurnResult("{\"type\":\"result\",\"parent_tool_use_id\":\"tool-1\"}"), "a sub-agent result does not end the turn");
        Check(!ClaudeStream.IsTurnResult("not json"), "an unreadable line does not end the turn");
        return Task.CompletedTask;
    }

    /// <summary>
    /// End to end over a real child process: the handshake precedes the prompt,
    /// the request reaches the host that can show the bar, 거부 answers that one
    /// request, and no rule or settings update is ever returned.
    /// </summary>
    internal static async Task RunLaunchesWithHostPromptsAndAnswersOneRequest()
    {
        var directory = Verification.Temp();
        try
        {
            var workspace = new Workspace { Path = directory };
            var plugin = Path.Combine(directory, "plugin"); Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin"));
            await File.WriteAllTextAsync(Path.Combine(plugin, ".claude-plugin", "plugin.json"), "{}");
            var record = Path.Combine(directory, "hosted");
            await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(Verification.Self("--fake-cli", "claude", record)));
            var seen = new List<ToolPermissionRequest>();
            RunManager? manager = null;
            manager = new RunManager(_ => Task.FromResult(workspace), catalog, plugin, _ => { }, value =>
            {
                seen.Add(value);
                // Answered on the run's own context, as the bar's 거부 button does.
                if (value.State == "pending") manager!.RespondToToolPermission("hosted", value.Id, allow: false);
            });
            await using (manager)
            {
                await manager.StartAsync(new("hosted", workspace.Id, "claude", "fixture prompt", []));
                await Verification.Until(() => !manager.IsRunning("hosted"), 20000);
            }

            var launched = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(record + ".args"), Wire.Json)!;
            Check(launched[Array.IndexOf(launched, "--permission-prompts") + 1] == "host", "the run launched with host prompts");
            Check(launched[Array.IndexOf(launched, "--permission-prompt-tool") + 1] == "stdio", "the run launched with the stdio prompt tool");

            var written = (await File.ReadAllLinesAsync(record + ".input")).Where(l => l.Length > 0).ToArray();
            Check(JsonDocument.Parse(written[0]).RootElement.GetProperty("request").GetProperty("subtype").GetString() == "initialize", "the handshake is written first");
            Check(JsonDocument.Parse(written[1]).RootElement.GetProperty("type").GetString() == "user", "the prompt only follows the handshake");

            Check(seen.Count >= 2 && seen[0].State == "pending" && seen[0].ToolName == "Read", "the waiting request reached the host that can show the bar");
            Check(seen[0].BlockedPath == "~/.claude/CLAUDE.md", "the bar is given the path");
            Check(seen[^1].State == "denied", "the request settles as denied");

            var decision = JsonDocument.Parse(await File.ReadAllTextAsync(record + ".decision")).RootElement.GetProperty("response");
            Check(decision.GetProperty("subtype").GetString() == "success", "the CLI receives an answer");
            var payload = decision.GetProperty("response");
            Check(payload.GetProperty("behavior").GetString() == "deny" && payload.GetProperty("toolUseID").GetString() == "tool-ask-1", "거부 denies that one tool call");
            Check(!payload.TryGetProperty("updatedPermissions", out _) && !payload.TryGetProperty("updatedInput", out _), "no rule or settings update is ever returned");
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }
}
