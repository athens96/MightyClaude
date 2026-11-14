using System.Text.Json;
using System.Text.Json.Nodes;
using MightyCode.Core;


/// <summary>
/// The ACP client (opencode, Hermes) against the Mac's fixtures (native/contracts/fixtures/opencode-acp-*.jsonl, hermes-acp-*.jsonl) with the
/// Mac's results (M/Tests/OpencodeTests.swift, HermesTests.swift): the channel's requests and answers, the transcript, the activity rows,
/// usage, the approval and question cards.
/// </summary>
internal static class AcpVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal const string OpencodeSession = "ses_9f3c2a1b7e4dQx7Lm2Np8Rt5Vw";
    internal const string HermesSession = "local-3869e20bb2f7a486741b466fd3689c6722764393b7a2f50bca1fa86853cd1f9b";
    internal const string HermesResumed = "local-a8a2537a19ce1905c0a99559ca0f2e86197234e68b0931bf190fc6517e91386e";
    internal const string HermesStale = "local-b3d62fb4c0780d2c11596ef324e3582b301cb89d9993a5daaa93c6431187c278";
    private const string HermesModel = "custom:example-model-2.5", HermesNamedModel = "custom:llm.example.com:example-model-2.5";

    internal static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "native", "contracts", "fixtures", "setup-catalog.json"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("the repository root was not found");
    }
    internal static string FixturePath(string provider, string name) => Path.Combine(Root(), "native", "contracts", "fixtures", $"{provider}-acp-{name}.jsonl");
    internal static string[] Fixture(string provider, string name) => File.ReadAllLines(FixturePath(provider, name)).Where(line => line.Length > 0).ToArray();

    /// <summary>What a channel and its parser did with a fixture.</summary>
    internal sealed class Replay
    {
        public readonly List<(string Kind, string Text)> Logs = [];
        public readonly List<AgentActivity> Activities = [];
        public readonly List<string> Resumes = [];
        public readonly List<SessionUsage> Usage = [];
        public readonly List<JsonObject> Writes = [];
        public readonly List<ToolPermissionRequest> Permissions = [];
        public readonly List<string> Warnings = [], Failures = [];
        public int Completed;
        public OutputParser Parser = null!;
        public AcpChannel Channel = null!;
        public List<string> Methods => Writes.Select(w => w["method"]?.GetValue<string>()).OfType<string>().ToList();
        public List<JsonObject> Answers => Writes.Where(w => w["result"] is not null).ToList();
        public static string? Option(JsonObject answer) => answer["result"]?["outcome"]?["optionId"]?.GetValue<string>();
        public JsonObject? Result(int id) => Writes.FirstOrDefault(w => w["id"] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.GetValue<int>() == id)?["result"] as JsonObject;
        public void Receive(string line) => Channel.Receive(line);
    }

    internal static StartRunRequest Request(string provider, string input = "hi there", string model = "default", RunSettings? settings = null, string? resume = null) =>
        new("pane", "workspace", "claude", input, [], model, provider, settings ?? new RunSettings(), resume);

    internal static Replay Channel(StartRunRequest request, Func<JsonObject, string?>? admit = null, string? note = null)
    {
        var run = new Replay();
        run.Parser = new OutputParser(request.Provider, (kind, text) => run.Logs.Add((kind, text)), id => run.Resumes.Add(id), activity: a => run.Activities.Add(a), usage: u => run.Usage.Add(u), activityNamespace: "run-" + request.Provider);
        var hermes = request.Provider == "hermes";
        run.Channel = new AcpChannel("run-" + request.Provider, request.ResumeId, "/tmp/project", AcpChannel.PromptText(request.Input, [], note), [],
            hermes ? HermesAgent.ConfigValues(request) : OpencodeAgent.ConfigValues(request),
            frame => run.Writes.Add(JsonNode.Parse(frame)!.AsObject()), line => run.Parser.Parse(line), p => run.Permissions.Add(p), (p, state) => run.Parser.PermissionActivity(p, state),
            w => run.Warnings.Add(w), f => run.Failures.Add(f), () => run.Completed++, admit, freeChoices: hermes);
        return run;
    }

    internal static Replay Play(string provider, string name, StartRunRequest? request = null, Action<Replay>? answer = null)
    {
        var run = Channel(request ?? Request(provider));
        run.Channel.Start();
        foreach (var line in Fixture(provider, name)) { run.Receive(line); answer?.Invoke(run); }
        run.Parser.Flush(); run.Parser.FinishActivities(false);
        return run;
    }

    /// <summary>Answers each card once, as it appears.</summary>
    private static Action<Replay> Answering(Action<Replay, ToolPermissionRequest> answer) => run =>
    {
        if (run.Permissions.LastOrDefault() is { State: "pending" } card && run.Permissions.Count(p => p.Id == card.Id) == 1) answer(run, card);
    };
    private static Action<Replay> Allowing(bool allow) => run => { if (run.Permissions.LastOrDefault() is { State: "pending" } ask) run.Channel.Respond(ask.Id, allow); };
    private static UserQuestionnaire Questionnaire(ToolPermissionRequest card) => UserQuestionnaire.Parse(card.InputJson) ?? throw new InvalidOperationException("the card's questions did not parse");
    private static Dictionary<string, UserQuestionAnswer> Answer(ToolPermissionRequest card, string[]? picked = null, string? typed = null) =>
        new() { [Questionnaire(card).Questions[0].Question] = new(picked ?? [], typed) };
    private static bool Throws(Action action) { try { action(); return false; } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return true; } }
    private static string Text(JsonNode? node) => node?.GetValue<string>() ?? "";

    // ── opencode ────────────────────────────────────────────────────────────

    internal static Task ANewOpencodeSessionIsInitializeThenSessionNewThenOnePrompt()
    {
        var run = Play("opencode", "answer");
        Check(run.Methods.SequenceEqual(["initialize", "session/new", "session/prompt"]), "methods: " + string.Join(", ", run.Methods));
        Check(run.Writes.All(w => Text(w["jsonrpc"]) == "2.0"), "JSON-RPC 2.0");
        var initialize = run.Writes[0]["params"]!;
        Check(initialize["protocolVersion"]!.GetValue<int>() == 1 && initialize["clientCapabilities"]!["terminal"]!.GetValue<bool>() == false
            && initialize["clientCapabilities"]!["elicitation"]!["form"] is JsonObject { Count: 0 }, "initialize offers no terminal and form elicitation");
        Check(Text(run.Writes[1]["params"]!["cwd"]) == "/tmp/project" && run.Writes[1]["params"]!["mcpServers"] is JsonArray { Count: 0 }, "session/new");
        Check(Text(run.Writes[2]["params"]!["sessionId"]) == OpencodeSession && Text(run.Writes[2]["params"]!["prompt"]![0]!["text"]) == "hi there", "the prompt");
        Check(run.Channel.TurnCompleted && !run.Channel.Failed && run.Completed == 1 && run.Failures.Count == 0, "the turn completed");
        Check(run.Logs.Select(l => l.Kind).SequenceEqual(["assistant"]) && run.Logs[0].Text == "Hello! How can I help with this project?" && !run.Logs.Any(l => l.Text.Contains("greets")), "streamed text once, no thoughts: " + string.Join(" | ", run.Logs));
        Check(run.Resumes.SequenceEqual([OpencodeSession]) && !run.Parser.Failed, "the session is the pane's resume id");
        var last = run.Usage.Last();
        Check(last.Provider == "opencode" && last.Source == "opencode.acp" && last.ProviderSessionId == OpencodeSession && last.Model == "opencode/big-pickle", "usage identity: " + JsonSerializer.Serialize(last, Wire.Json));
        Check(last.CostUSD == 0.0123 && last.CostScope == "session" && last.ContextUsedTokens == 1221 && last.ContextWindowTokens == 200000, "the session's cost and the context");
        Check(last.InputTokens == 1203 && last.OutputTokens == 18 && last.TotalTokens == 1221 && last.TokenScope == "run", "the turn's tokens");
        return Task.CompletedTask;
    }

    internal static Task OpencodeSettingsGoAsConfigOptionsAndOneTheAgentLacksIsNamedAndSkipped()
    {
        var run = Channel(Request("opencode", "plan it", "anthropic/claude-sonnet-4-5", new RunSettings(Effort: "low", PermissionMode: "plan")));
        run.Channel.Start();
        var lines = Fixture("opencode", "answer");
        run.Receive(lines[0]); run.Receive(lines[1]);
        // The model first; the session's effort option has no "low"; the mode differs from build.
        Check(run.Methods.SequenceEqual(["initialize", "session/new", "session/set_config_option"]) && Text(run.Writes[2]["params"]!["configId"]) == "model"
            && Text(run.Writes[2]["params"]!["value"]) == "anthropic/claude-sonnet-4-5" && run.Warnings.Count == 0, "the model goes first");
        run.Receive("{\"jsonrpc\":\"2.0\",\"id\":\"mighty-3\",\"result\":{}}");
        Check(run.Warnings.Count == 1 && run.Warnings[0].Contains("effort") && Text(run.Writes[3]["params"]!["configId"]) == "mode" && Text(run.Writes[3]["params"]!["value"]) == "plan", "an effort the session lacks is named; plan follows");
        // A setting the agent refuses is named; the request still goes.
        run.Receive("{\"jsonrpc\":\"2.0\",\"id\":\"mighty-4\",\"error\":{\"code\":-32602,\"message\":\"Invalid value\"}}");
        Check(run.Methods.Last() == "session/prompt" && run.Warnings.Count == 2, "a refused setting is named and the prompt goes");
        return Task.CompletedTask;
    }

    internal static Task AMissingOpencodeSessionToResumeStartsANewOneWithANotice()
    {
        var run = Play("opencode", "resume-missing", Request("opencode", "go on", resume: "ses_0000000000000000000000dead"));
        Check(run.Methods.SequenceEqual(["initialize", "session/resume", "session/new", "session/prompt"]) && Text(run.Writes[1]["params"]!["sessionId"]) == "ses_0000000000000000000000dead", "resume, then new");
        Check(run.Warnings.SequenceEqual([Locale.Get("acp.channel.newSession")]) && run.Resumes.SequenceEqual([OpencodeSession]), "the notice and the new session");
        Check(run.Logs.Where(l => l.Kind == "assistant").Select(l => l.Text).SequenceEqual(["Starting fresh."]) && run.Channel.TurnCompleted && !run.Parser.Failed, "the answer");
        return Task.CompletedTask;
    }

    internal static Task OpencodesQuestionToolIsTheQuestionCard()
    {
        ToolPermissionRequest? shown = null;
        var run = Play("opencode", "question", answer: Answering((run, card) =>
        {
            shown = card;
            Check(run.Activities.Last().State == "waiting", "the call's row waits");
            run.Channel.AnswerQuestions(card.Id, Answer(card, ["Green"]));
        }));
        var card = shown ?? throw new InvalidOperationException("no card");
        var question = Questionnaire(card).Questions[0];
        Check(card.ToolName == "AskUserQuestion" && card.CanAnswerQuestions && !card.CanAllow && card.ToolUseId == "call_question_1", "the card");
        // The property's title is the header, its description the question, each `oneOf` entry an option.
        Check(question.Header == "Favorite color" && question.Question == "What's your favorite color?" && !question.MultiSelect
            && question.Options.Select(o => o.Label).SequenceEqual(["Red", "Green", "Blue"]) && question.Options[0].Description == "You prefer red.", "the question");
        var result = run.Result(0)!;
        Check(Text(result["action"]) == "accept" && result["content"]!.ToJsonString() == "{\"q0\":\"Green\"}", "the pick is the form's content: " + result.ToJsonString());
        Check(run.Permissions.Select(p => p.State).SequenceEqual(["pending", "answered"]) && run.Warnings.Count == 0 && run.Failures.Count == 0 && run.Channel.TurnCompleted && !run.Parser.Failed && run.Completed == 1, "answered once");
        Check(run.Activities.Last(a => a.Kind != "turn").State == "completed", "the call's row settles with opencode's own update");
        // A typed answer travels in the custom property; several picks as a list, in the offered order, with the typed one.
        run = Play("opencode", "question", answer: Answering((run, card) => run.Channel.AnswerQuestions(card.Id, Answer(card, typed: " Purple "))));
        Check(run.Result(0)!["content"]!.ToJsonString() == "{\"q0_custom\":\"Purple\"}", "a typed answer");
        run = Play("opencode", "question-multiple", answer: Answering((run, card) =>
        {
            Check(Questionnaire(card).Questions[0].MultiSelect && Questionnaire(card).Questions[0].Options.Select(o => o.Label).SequenceEqual(["Apple", "Banana", "Cherry"]), "a multiple choice");
            run.Channel.AnswerQuestions(card.Id, Answer(card, ["Cherry", "Apple"], "Mango"));
        }));
        var content = run.Result(0)!["content"]!.AsObject();
        Check(content["q0"]!.ToJsonString() == "[\"Apple\",\"Cherry\"]" && Text(content["q0_custom"]) == "Mango" && content.Count == 2, "picks and a typed answer: " + content.ToJsonString());
        // The card's Cancel declines the form; opencode ends the turn `cancelled`, which is not a failed run.
        run = Play("opencode", "question-dismissed", answer: Answering((run, card) =>
        {
            Check(Throws(() => run.Channel.Respond(card.Id, true)), "a question cannot be allowed");
            run.Channel.Respond(card.Id, false);
        }));
        Check(Text(run.Result(0)!["action"]) == "decline" && run.Result(0)!.Count == 1 && run.Permissions.Select(p => p.State).SequenceEqual(["pending", "denied"]), "declined");
        Check(run.Activities.Last(a => a.Kind != "turn").State == "error" && run.Channel.TurnCompleted && !run.Parser.Failed && run.Failures.Count == 0, "the dismissed call fails, the run does not");
        return Task.CompletedTask;
    }

    internal static Task AFormTheCardCannotAskIsDeclinedAndAWithdrawnQuestionLeavesNoCard()
    {
        static Replay Asked(string properties)
        {
            var run = Channel(Request("opencode", "hi"));
            run.Channel.Start();
            var lines = Fixture("opencode", "answer");
            run.Receive(lines[0]); run.Receive(lines[1]);
            run.Receive("{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"elicitation/create\",\"params\":{\"mode\":\"form\",\"sessionId\":\"" + OpencodeSession + "\",\"toolCallId\":\"call-9\",\"message\":\"Questions\",\"requestedSchema\":{\"type\":\"object\",\"properties\":" + properties + ",\"required\":[]}}}");
            return run;
        }
        var run = Asked("{\"q0\":{\"type\":\"integer\",\"title\":\"Retries\",\"description\":\"How many retries?\"}}");
        Check(run.Permissions.Count == 0 && Text(run.Result(0)?["action"]) == "decline" && run.Warnings.SequenceEqual([Locale.Get("acp.channel.formDeclined", new Dictionary<string, string> { ["question"] = "Questions" })]), "a number is declined and named");
        var options = string.Join(",", Enumerable.Range(1, 13).Select(i => "{\"const\":\"o" + i + "\",\"title\":\"Option " + i + "\"}"));
        run = Asked("{\"q0\":{\"type\":\"string\",\"title\":\"Pick\",\"description\":\"Which one?\",\"oneOf\":[" + options + "]}}");
        Check(run.Permissions.Count == 0 && Text(run.Result(0)?["action"]) == "decline", "more options than the card holds");
        var twelve = string.Join(",", Enumerable.Range(1, 12).Select(i => "{\"const\":\"o" + i + "\",\"title\":\"Option " + i + "\"}"));
        run = Asked("{\"q0\":{\"type\":\"string\",\"title\":\"Pick\",\"description\":\"Which one?\",\"oneOf\":[" + twelve + "]}}");
        Check(run.Permissions.Count == 1 && Questionnaire(run.Permissions[0]).Questions[0].Options.Count == 12, "twelve options fit the card");
        run = Asked("{\"q0\":{\"type\":\"string\",\"title\":\"Pick\",\"description\":\"Which one?\",\"oneOf\":[{\"const\":\"a\",\"title\":\"First\"},{\"const\":\"b\",\"title\":\"Second\"}]},\"q1\":{\"type\":\"boolean\",\"title\":\"Sure\",\"description\":\"Are you sure?\"}}");
        var card = run.Permissions[0]; var questions = Questionnaire(card).Questions;
        Check(questions.Select(q => q.Question).SequenceEqual(["Which one?", "Are you sure?"]) && questions[1].Options.Select(o => o.Label).SequenceEqual([Locale.Get("questionnaire.option.yes"), Locale.Get("questionnaire.option.no")]), "a choice and a yes or no");
        // A choice without the custom property takes only its options; the card stays answerable.
        Check(Throws(() => run.Channel.AnswerQuestions(card.Id, new Dictionary<string, UserQuestionAnswer> { ["Which one?"] = new([], "Third"), ["Are you sure?"] = new([Locale.Get("questionnaire.option.yes")]) })) && run.Result(0) is null, "a typed answer is refused");
        run.Channel.AnswerQuestions(card.Id, new Dictionary<string, UserQuestionAnswer> { ["Which one?"] = new(["Second"]), ["Are you sure?"] = new([Locale.Get("questionnaire.option.no")]) });
        var content = run.Result(0)!["content"]!;
        Check(Text(content["q0"]) == "b" && content["q1"]!.GetValue<bool>() == false, "the label's value travels; a boolean as a boolean: " + content.ToJsonString());
        const string pick = "{\"q0\":{\"type\":\"string\",\"title\":\"Pick\",\"description\":\"Which one?\",\"oneOf\":[{\"const\":\"a\",\"title\":\"First\"},{\"const\":\"b\",\"title\":\"Second\"}]}}";
        run = Asked(pick);
        run.Receive("{\"jsonrpc\":\"2.0\",\"method\":\"$/cancel_request\",\"params\":{\"requestId\":0}}");
        Check(run.Permissions.Select(p => p.State).SequenceEqual(["pending", "cancelled"]) && run.Result(0) is null, "a withdrawn request takes its card away");
        run = Asked(pick);
        run.Receive("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"" + OpencodeSession + "\",\"update\":{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"call-9\",\"status\":\"failed\"}}}");
        Check(run.Permissions.Select(p => p.State).SequenceEqual(["pending", "cancelled"]) && run.Result(0) is null, "a settled call takes its card away");
        run = Asked(pick);
        run.Channel.CancelAll();
        Check(run.Permissions.Select(p => p.State).SequenceEqual(["pending", "cancelled"]) && run.Result(0) is null, "a stopped run withdraws the card");
        return Task.CompletedTask;
    }

    internal static Task OpencodeToolsAreRowsAndAnAskIsTheApprovalCardAnsweredOnce()
    {
        var run = Play("opencode", "tools", answer: Allowing(true));
        // A sub-agent's own reply (a child session's update) is not part of the answer.
        Check(run.Logs.Where(l => l.Kind == "assistant").Select(l => l.Text).SequenceEqual(["Let me run the tests.", "One test fails because main returns 1."]), "replies: " + string.Join(" | ", run.Logs));
        var read = run.Activities.Where(a => a.ToolName == "Read").ToList();
        Check(read.Last().State == "completed" && read.Last().Kind == "read" && read.Last().Summary == "src/main.ts" && read.Last().Output?.Contains("export function main()") == true, "the read row: " + JsonSerializer.Serialize(read.Last()));
        var bash = run.Activities.Where(a => a.ToolName == "Bash").ToList();
        Check(bash.Any(a => a.State == "waiting") && bash.Last().State == "completed" && bash.Last().Kind == "command" && bash.Last().Output == "1 failing", "the command row: " + JsonSerializer.Serialize(bash.Last()));
        var edit = run.Activities.Where(a => a.ToolName == "Edit").ToList();
        Check(edit.Last().State == "error" && edit.Last().Output?.Contains("rejected") == true, "the rejected edit");
        var card = run.Permissions[0];
        Check(card.ToolName == "Bash" && card.ToolUseId == "call_02ShellB" && card.Summary == "npm test" && card.CanAllow, "the card: " + JsonSerializer.Serialize(card));
        Check(card.InputJson.Contains("\"command\": \"npm test\"") && card.InputJson.Contains("\"cwd\": \"/tmp/project\""), "the card's input: " + card.InputJson);
        Check(ToolPermissionPresentation.Make(card.ToolName, card.InputJson).Fields.Any(f => f.Code && f.Value == "npm test"), "the card shows the command");
        Check(run.Permissions.Select(p => p.State).SequenceEqual(["pending", "allowed"]), "allowed once");
        var answer = run.Answers[0];
        Check(answer["id"]!.GetValue<int>() == 0 && Text(answer["result"]!["outcome"]!["outcome"]) == "selected" && Replay.Option(answer) == "once", "answered with opencode's own option, id 0 as a number: " + answer.ToJsonString());
        Check(Throws(() => run.Channel.Respond(card.Id, true)) && !run.Parser.Failed && run.Channel.TurnCompleted, "answered once");
        var last = run.Usage.Last();
        Check(last.InputTokens == 5148 && last.CacheReadTokens == 2048 && last.TotalTokens == 5243 && last.CostUSD == 0.0411, "cached input is inside input: " + JsonSerializer.Serialize(last, Wire.Json));
        var denied = Play("opencode", "tools", answer: Allowing(false));
        Check(Replay.Option(denied.Answers[0]) == "reject", "a denial is opencode's reject");
        var ignored = Play("opencode", "tools");
        Check(ignored.Permissions.Select(p => p.State).SequenceEqual(["pending", "cancelled"]), "an unanswered ask goes when its call settles");
        return Task.CompletedTask;
    }

    internal static Task ASignedOutOpencodeRunFailsAsALostSignIn()
    {
        var run = Play("opencode", "auth");
        Check(run.Parser.Failed && run.Parser.AuthenticationFailure && run.Channel.Failed && run.Completed == 1, "a lost sign-in");
        Check(run.Logs.Any(l => l.Kind == "error" && l.Text.Contains("provider authentication required")), "its words: " + string.Join(" | ", run.Logs));
        // An agent request this client does not offer is refused; EOF before the answer is a failure.
        var refused = Channel(Request("opencode", "hi"));
        refused.Channel.Start();
        refused.Receive("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"fs/read_text_file\",\"params\":{\"path\":\"/etc/hosts\"}}");
        Check(refused.Writes.Last()["error"]!["code"]!.GetValue<int>() == -32601, "fs is not offered");
        refused.Channel.Flush();
        Check(refused.Channel.Failed && refused.Failures.SequenceEqual([Locale.Get("acp.channel.closedEarly")]), "closed early");
        return Task.CompletedTask;
    }

    internal static Task OpencodesPermissionModesAreItsRulesForTheRun()
    {
        Check(ProviderCatalog.PermissionModes("opencode").SequenceEqual(["manual", "plan", "acceptEdits", "fullAccess"]), "opencode's modes");
        string? Effect(string mode, string action) => OpencodeAgent.PermissionRules(mode).LastOrDefault(r => r.Action == action || r.Action == "*").Effect;
        Check(Effect("manual", "edit") == "ask" && Effect("manual", "shell") == "ask" && Effect("manual", "external_directory") == "ask", "manual asks");
        Check(Effect("acceptEdits", "edit") == "allow" && Effect("acceptEdits", "shell") == "ask", "accept edits");
        Check(Effect("plan", "edit") is null && Effect("plan", "webfetch") == "ask", "plan leaves the plan agent's own edit rule");
        Check(OpencodeAgent.PermissionRules("fullAccess") is [("*", "allow")], "full access allows all");
        var content = JsonNode.Parse(OpencodeAgent.ConfigContent("manual", @"C:\Program Files\Mighty Code\Agents\opencode-questions.md"))!;
        Check(content["permissions"]![0]!.ToJsonString() == "{\"action\":\"edit\",\"effect\":\"ask\",\"resource\":\"*\"}" && Text(content["instructions"]![0]) == @"C:\Program Files\Mighty Code\Agents\opencode-questions.md", "the run's config: " + content.ToJsonString());
        Check(JsonNode.Parse(OpencodeAgent.ConfigContent("plan", null))!["instructions"] is null, "no instructions file, no instructions");
        Check(OpencodeAgent.ConfigValues(Request("opencode", model: "anthropic/x", settings: new RunSettings(Effort: "high", PermissionMode: "plan"))).SequenceEqual([("model", "anthropic/x"), ("effort", "high"), ("mode", "plan")]), "config values");
        Check(OpencodeAgent.IsSupported("opencode v2.2.7") && !OpencodeAgent.IsSupported("1.19.5") && !OpencodeAgent.IsSupported(""), "OpenCode 2 only");
        Check(AgentFiles.Find(OpencodeAgent.QuestionInstructionsName) is { } file && File.ReadAllText(file).Contains("`question` tool"), "the question instructions ship with the app");
        return Task.CompletedTask;
    }

    // ── Hermes ──────────────────────────────────────────────────────────────

    internal static Task ANewHermesSessionIsInitializeThenSessionNewThenOnePrompt()
    {
        var run = Play("hermes", "answer");
        Check(run.Methods.SequenceEqual(["initialize", "session/new", "session/prompt"]), "methods: " + string.Join(", ", run.Methods));
        Check(Text(run.Writes[2]["params"]!["sessionId"]) == HermesSession && Wire.SessionIdentifier(HermesSession) && Text(run.Writes[2]["params"]!["prompt"]![0]!["text"]) == "hi there", "the prompt");
        Check(run.Channel.TurnCompleted && !run.Channel.Failed && run.Completed == 1 && run.Failures.Count == 0 && run.Warnings.Count == 0, "the turn");
        Check(run.Channel.Cancellation.Take() is null, "nothing is left to cancel");
        Check(run.Logs.Select(l => l.Kind).SequenceEqual(["assistant"]) && run.Logs[0].Text == "Hi." && run.Resumes.SequenceEqual([HermesSession]), "logged once");
        var last = run.Usage.Last();
        Check(last.Provider == "hermes" && last.Source == "hermes.acp" && last.ProviderSessionId == HermesSession && run.Usage[0].Model == HermesModel && last.Model == HermesNamedModel, "the model settles: " + JsonSerializer.Serialize(last, Wire.Json));
        Check(last.CostUSD is null && last.ContextUsedTokens == 11194 && last.ContextWindowTokens == 256000, "no cost, the context");
        Check(last.TokenScope == "session" && last.InputTokens == 11166 && last.OutputTokens == 28 && last.TotalTokens == 11194, "the session's running counts");
        var cancelled = Play("hermes", "cancel");
        Check(cancelled.Channel.TurnCompleted && !cancelled.Parser.Failed && cancelled.Usage.All(u => u.TotalTokens is null), "a cancelled turn's zeros are not counts");
        return Task.CompletedTask;
    }

    internal static Task HermessModelEffortAndEditPolicyGoAsConfigOptions()
    {
        var request = Request("hermes", "do it", HermesNamedModel, new RunSettings(Effort: "high", PermissionMode: "acceptEdits"));
        Check(HermesAgent.ConfigValues(request).Select(v => v.Id).SequenceEqual(["model", "reasoning_effort", "edit_approval_policy"]), "the three settings");
        var run = Channel(request);
        run.Channel.Start();
        var lines = Fixture("hermes", "answer");
        run.Receive(lines[0]); run.Receive(lines[1]);
        string[] Sent(int index) => [Text(run.Writes[index]["params"]!["configId"]), Text(run.Writes[index]["params"]!["value"])];
        Check(Sent(2).SequenceEqual(["model", HermesNamedModel]), "model");
        run.Receive("{\"jsonrpc\":\"2.0\",\"id\":\"mighty-3\",\"result\":{}}");
        Check(Sent(3).SequenceEqual(["reasoning_effort", "high"]), "effort");
        run.Receive("{\"jsonrpc\":\"2.0\",\"id\":\"mighty-4\",\"result\":{}}");
        Check(Sent(4).SequenceEqual(["edit_approval_policy", "workspace_session"]), "edit policy");
        run.Receive("{\"jsonrpc\":\"2.0\",\"id\":\"mighty-5\",\"error\":{\"code\":-32603,\"message\":\"Mode changes apply when the session is idle; try again after this turn.\"}}");
        Check(run.Methods.Last() == "session/prompt" && run.Warnings.Count == 1 && run.Warnings[0].Contains("idle"), "a busy session's refusal is named");
        var same = Channel(Request("hermes", "do it", HermesModel, new RunSettings(Effort: "max")));
        same.Channel.Start(); same.Receive(lines[0]); same.Receive(lines[1]);
        Check(same.Methods.SequenceEqual(["initialize", "session/new", "session/prompt"]) && same.Warnings.Count == 0, "the session's own values are not sent again");
        var unknown = Channel(Request("hermes", "do it", "openrouter:gone/model"));
        unknown.Channel.Start(); unknown.Receive(lines[0]); unknown.Receive(lines[1]);
        Check(unknown.Methods.SequenceEqual(["initialize", "session/new", "session/prompt"]) && unknown.Warnings.Count == 1 && unknown.Warnings[0].Contains("openrouter:gone/model"), "a model the picker lacks is named");
        return Task.CompletedTask;
    }

    internal static Task AHermesResumeIgnoresTheReplayedConversation()
    {
        var run = Play("hermes", "resume", Request("hermes", "go on", settings: new RunSettings(PermissionMode: "acceptEdits"), resume: HermesResumed));
        Check(run.Methods.SequenceEqual(["initialize", "session/resume", "session/set_config_option", "session/prompt"]) && Text(run.Writes[2]["params"]!["value"]) == "workspace_session", "methods: " + string.Join(", ", run.Methods));
        Check(run.Logs.Where(l => l.Kind == "assistant").Select(l => l.Text).SequenceEqual(["Bye."]) && run.Activities.All(a => a.Kind == "turn"), "the replay is not this run's: " + string.Join(" | ", run.Logs));
        Check(run.Resumes.SequenceEqual([HermesResumed]) && run.Channel.TurnCompleted && !run.Parser.Failed && run.Usage.Last().TotalTokens == 46130, "the counters carry on");
        var missing = Play("hermes", "resume-missing", Request("hermes", "go on", resume: "local-" + new string('0', 64)));
        Check(missing.Methods.SequenceEqual(["initialize", "session/resume", "session/new", "session/prompt"]) && missing.Warnings.SequenceEqual([Locale.Get("acp.channel.newSession")]), "a missing session starts a new one");
        Check(missing.Resumes.Count == 1 && missing.Resumes[0] != "local-" + new string('0', 64) && missing.Logs.Where(l => l.Kind == "assistant").Select(l => l.Text).SequenceEqual(["Hello."]), "the new session answers");
        return Task.CompletedTask;
    }

    internal static Task WithoutAProviderHermesStopsAtInitializeAsAMissingSignIn()
    {
        var run = Channel(Request("hermes", "hi"), admit: result => HermesAgent.Provider(result) is null ? Locale.Get("run.error.hermesNoProvider") : null);
        run.Channel.Start();
        run.Receive(Fixture("hermes", "unconfigured")[0]);
        Check(run.Methods.SequenceEqual(["initialize"]) && run.Channel.Failed && run.Completed == 1 && run.Resumes.Count == 0, "no session is opened");
        Check(run.Parser.Failed && run.Parser.AuthenticationFailure && run.Logs.Select(l => l.Text).SequenceEqual([Locale.Get("run.error.hermesNoProvider")]), "a missing sign-in: " + string.Join(" | ", run.Logs));
        var configured = Channel(Request("hermes", "hi"), admit: result => HermesAgent.Provider(result) is null ? "no" : null);
        configured.Channel.Start();
        configured.Receive(Fixture("hermes", "answer")[0]);
        Check(configured.Methods.SequenceEqual(["initialize", "session/new"]), "a configured Hermes passes");
        // A turn that fails for a missing provider: the reply says why, the error is the gateway's word.
        var stale = Play("hermes", "auth", Request("hermes", "hi", resume: HermesStale));
        Check(stale.Parser.Failed && stale.Parser.AuthenticationFailure && stale.Channel.Failed && stale.Completed == 1, "a lost sign-in");
        Check(stale.Logs.First(l => l.Kind == "assistant").Text.StartsWith("⚠️ provider 'auto' has no endpoint configured", StringComparison.Ordinal) && stale.Logs.Last(l => l.Kind == "error").Text == Locale.Get("run.error.hermesTurnFailed"), "its words");
        var other = Channel(Request("hermes", "hi"));
        other.Channel.Start(); other.Receive(Fixture("hermes", "answer")[0]);
        other.Receive("{\"jsonrpc\":\"2.0\",\"id\":\"mighty-2\",\"error\":{\"code\":-32603,\"message\":\"Internal error\",\"data\":{\"details\":\"Gateway could not start: port in use\"}}}");
        Check(other.Parser.Failed && !other.Parser.AuthenticationFailure && other.Logs.Last().Text == "Gateway could not start: port in use", "data.details is the error's words");
        return Task.CompletedTask;
    }

    internal static Task HermesToolsAreRowsAndAnEditAndADangerousCommandAreCards()
    {
        var run = Play("hermes", "tools", answer: Allowing(false));
        var replies = run.Logs.Where(l => l.Kind == "assistant").Select(l => l.Text).ToList();
        Check(replies.Count == 2 && replies[0] == "[tool call]" && replies[1].StartsWith("Done what could be done;", StringComparison.Ordinal) && replies[1].Split("Done what could be done;").Length == 2, "the reply sent again is logged once: " + string.Join(" | ", replies));
        var read = run.Activities.Where(a => a.ToolName == "Read").ToList();
        Check(read.Last().State == "completed" && read.Last().Summary == "/etc/shells" && read.Last().Output?.Contains("/bin/zsh") == true, "the read row: " + JsonSerializer.Serialize(read.Last()));
        var edit = run.Activities.Where(a => a.ToolName == "Edit" && a.Summary == "note.txt").ToList();
        Check(edit.First().State == "running" && edit.Last().State == "error" && edit.Last().Output?.Contains("Edit approval denied") == true, "the denied edit");
        var bash = run.Activities.Where(a => a.ToolName == "Bash" && a.Summary == "rm -rf ./nothing-here").ToList();
        Check(bash.Last().State == "error" && bash.Last().Kind == "command" && bash.Last().Output?.Contains("Command denied by user") == true, "the denied command");
        Check(run.Activities.Last(a => a.ToolName == "tool_search").State == "completed", "a tool named by its title");
        var cards = run.Permissions.Where(p => p.State == "pending").ToList();
        Check(cards.Select(c => c.ToolUseId).SequenceEqual(["edit-approval-1", "perm-check-1"]) && cards.All(c => c.CanAllow), "two cards");
        Check(cards[0].ToolName == "Edit" && cards[0].Summary == "Approve edit: /private/tmp/project/note.txt" && cards[0].InputJson.Contains("\"path\": \"note.txt\"") && cards[0].InputJson.Contains("\"content\": \"hi\"") && !cards[0].InputJson.Contains("\"tool\""), "the edit's arguments unwrapped: " + cards[0].InputJson);
        Check(cards[1].ToolName == "Bash" && cards[1].Summary == "recursive delete: rm -rf ./nothing-here" && ToolPermissionPresentation.Make(cards[1].ToolName, cards[1].InputJson).Fields.Any(f => f.Code && f.Value == "rm -rf ./nothing-here"), "the command card");
        Check(run.Permissions.Select(p => p.State).SequenceEqual(["pending", "denied", "pending", "denied"]), "both denied");
        Check(run.Answers.Select(a => a["id"]!.GetValue<int>()).SequenceEqual([0, 1]) && run.Answers.Select(Replay.Option).SequenceEqual(["deny", "deny"]), "Hermes's own option ids, number ids as numbers");
        Check(!run.Parser.Failed && run.Channel.TurnCompleted && run.Usage.Last().TotalTokens == 24646, "the turn completes");
        var allowed = Play("hermes", "tools", answer: Allowing(true));
        Check(allowed.Answers.Select(Replay.Option).SequenceEqual(["allow_once", "allow_once"]), "allowed once, never for the session");
        return Task.CompletedTask;
    }

    /// <summary>The frames Hermes 0.23.1's own builders (acp_adapter/elicitation.py) make for clarify (M/Tests/HermesTests.swift builtClarify).</summary>
    private static readonly string[] BuiltClarify =
    [
        "{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"elicitation/create\",\"params\":{\"sessionId\":\"" + HermesSession + "\",\"mode\":\"form\",\"message\":\"Commit the change now?\",\"requestedSchema\":{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\",\"enum\":[\"Yes\",\"Not yet\"]}},\"required\":[\"answer\"]}}}",
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"elicitation/create\",\"params\":{\"sessionId\":\"" + HermesSession + "\",\"mode\":\"form\",\"message\":\"Which files should be staged?\",\"requestedSchema\":{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"array\",\"items\":{\"type\":\"string\",\"enum\":[\"README.md\",\"notes.txt\",\"src/main.py\"]}}},\"required\":[\"answer\"]}}}",
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"elicitation/create\",\"params\":{\"sessionId\":\"" + HermesSession + "\",\"mode\":\"form\",\"message\":\"What should the commit message say?\",\"requestedSchema\":{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"]}}}",
        "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"session/request_permission\",\"params\":{\"sessionId\":\"" + HermesSession + "\",\"toolCall\":{\"content\":[{\"content\":{\"text\":\"Commit the change now?\",\"type\":\"text\"},\"type\":\"content\"}],\"kind\":\"other\",\"rawInput\":{\"question\":\"Commit the change now?\",\"choices\":[\"Yes\",\"Not yet\"]},\"status\":\"pending\",\"title\":\"Commit the change now?\",\"toolCallId\":\"clarify-7c1d\",\"sessionUpdate\":\"tool_call_update\"},\"options\":[{\"kind\":\"allow_once\",\"name\":\"Yes\",\"optionId\":\"choice-0\"},{\"kind\":\"allow_once\",\"name\":\"Not yet\",\"optionId\":\"choice-1\"},{\"kind\":\"reject_once\",\"name\":\"Skip\",\"optionId\":\"skip\"}]}}",
    ];
    private static (Replay Run, ToolPermissionRequest? Card) Clarify(int index)
    {
        var run = Channel(Request("hermes", "hi"));
        run.Channel.Start();
        var lines = Fixture("hermes", "answer");
        run.Receive(lines[0]); run.Receive(lines[1]); run.Receive(BuiltClarify[index]);
        return (run, run.Permissions.FirstOrDefault());
    }

    internal static Task HermessClarifyIsTheQuestionCard()
    {
        // Recorded: one clarify call of three questions, each its own elicitation/create (ids 0, 1, 2).
        var asked = new List<UserQuestion>();
        var run = Play("hermes", "clarify", answer: Answering((run, card) =>
        {
            var question = Questionnaire(card).Questions[0]; asked.Add(question);
            Check(card.ToolUseId == "call_clarify_1" && run.Activities.Last().State == "waiting", "the form is the open clarify call's question");
            run.Channel.AnswerQuestions(card.Id, Answer(card, [question.Options[0].Label]));
        }));
        Check(asked.Count == 3 && asked.All(q => !q.MultiSelect && q.Header == Locale.Get("questionnaire.header.agent")), "three cards, one at a time");
        Check(asked[0].Options.Select(o => o.Label).SequenceEqual(["작은 웹 앱/CLI 툴 (Recommended)", "라이브러리/패키지", "대충 뭐라도 — 아이디어를 던져보고 같이 다듬기", "아직 모르겠음"]), "Hermes's own choice texts");
        var replies = run.Writes.Where(w => w["result"]?["action"] is not null).ToList();
        Check(replies.Select(r => r["id"]!.GetValue<int>()).SequenceEqual([0, 1, 2]) && Text(replies[0]["result"]!["content"]!["answer"]) == "작은 웹 앱/CLI 툴 (Recommended)" && replies[0]["result"]!["content"]!.AsObject().Count == 1, "zero is an id like any other: " + replies[0].ToJsonString());
        Check(run.Permissions.Select(p => p.State).SequenceEqual(["pending", "answered", "pending", "answered", "pending", "answered"]) && run.Warnings.Count == 0 && run.Channel.TurnCompleted && !run.Parser.Failed, "the turn goes on");
        // Declined: Hermes reads a skip and goes on; unanswered: Hermes stops waiting and the card goes with a line.
        var declined = Play("hermes", "clarify-declined", answer: Answering((run, card) => run.Channel.Respond(card.Id, false)));
        Check(Text(declined.Result(0)!["action"]) == "decline" && declined.Result(0)!.Count == 1 && declined.Permissions.Select(p => p.State).SequenceEqual(["pending", "denied"]) && declined.Channel.TurnCompleted && declined.Logs.Any(l => l.Kind == "assistant"), "declined");
        var waited = Play("hermes", "clarify-declined");
        Check(waited.Permissions.Select(p => p.State).SequenceEqual(["pending", "cancelled"]) && waited.Result(0) is null
            && waited.Warnings.SequenceEqual([Locale.Get("acp.channel.questionWithdrawn", new Dictionary<string, string> { ["question"] = waited.Permissions[0].Summary })]), "withdrawn with a line");
        // The built frames: one choice, a typed answer in the same property, several picks, no choices, Cancel.
        var (one, card) = Clarify(0);
        Check(card is not null && one.Activities.All(a => a.Kind == "turn") && Questionnaire(card).Questions[0].Question == "Commit the change now?" && Questionnaire(card).Questions[0].Options.Select(o => o.Label).SequenceEqual(["Yes", "Not yet"]), "a clarify form");
        one.Channel.AnswerQuestions(card!.Id, Answer(card, ["Not yet"]));
        Check(one.Result(0)!["content"]!.ToJsonString() == "{\"answer\":\"Not yet\"}", "the pick");
        (one, card) = Clarify(0);
        one.Channel.AnswerQuestions(card!.Id, Answer(card, typed: "After the tests pass"));
        Check(one.Result(0)!["content"]!.ToJsonString() == "{\"answer\":\"After the tests pass\"}", "a typed answer in the same property");
        (one, card) = Clarify(1);
        one.Channel.AnswerQuestions(card!.Id, Answer(card, ["notes.txt", "README.md"], "docs/"));
        Check(one.Result(1)!["content"]!.ToJsonString() == "{\"answer\":[\"README.md\",\"notes.txt\",\"docs/\"]}", "several picks, the typed one last");
        (one, card) = Clarify(2);
        Check(Questionnaire(card!).Questions[0].Options.Count == 0 && Throws(() => one.Channel.AnswerQuestions(card!.Id, Answer(card))), "no choices: an answer must be typed");
        one.Channel.AnswerQuestions(card!.Id, Answer(card, typed: "Fix the parser"));
        Check(one.Result(2)!["content"]!.ToJsonString() == "{\"answer\":\"Fix the parser\"}", "the typed answer");
        // A clarify approval (a Hermes not told about forms): the pick is its option; nothing typed can travel; Cancel is Skip.
        (one, card) = Clarify(3);
        Check(card is { CanAnswerQuestions: true, CanAllow: false, ToolUseId: "clarify-7c1d" } && Throws(() => one.Channel.AnswerQuestions(card.Id, Answer(card, typed: "Later"))) && one.Answers.Count == 0, "typed answers are refused");
        one.Channel.AnswerQuestions(card!.Id, Answer(card, ["Not yet"]));
        Check(one.Answers.Select(Replay.Option).SequenceEqual(["choice-1"]) && one.Answers[0]["id"]!.GetValue<int>() == 3, "the picked option");
        (one, card) = Clarify(3);
        one.Channel.Respond(card!.Id, false);
        Check(one.Answers.Select(Replay.Option).SequenceEqual(["skip"]), "Cancel is Skip");
        return Task.CompletedTask;
    }

    internal static Task TheNoteAboutTheCardFollowsTheRequest()
    {
        var run = Channel(Request("hermes", "선택하는 문제를 내"), note: HermesQuestionTool.PromptNote);
        run.Channel.Start();
        var lines = Fixture("hermes", "answer");
        run.Receive(lines[0]); run.Receive(lines[1]);
        var text = Text(run.Writes.Last(w => Text(w["method"]) == "session/prompt")["params"]!["prompt"]![0]!["text"]);
        Check(text == "선택하는 문제를 내\n\n" + HermesQuestionTool.PromptNote, "after the request: " + text);
        Check(HermesQuestionTool.WithoutNote(text) == "선택하는 문제를 내" && HermesQuestionTool.WithoutNote("plain") == "plain" && HermesQuestionTool.PromptNote.Contains("clarify"), "taken off for the list");
        return Task.CompletedTask;
    }

    internal static Task HermessLogAndTracebacksOnStderrStayOutOfThePane()
    {
        var filter = new HermesAgent.StderrFilter();
        string[] recorded =
        [
            "2026-11-11 01:18:54 [INFO] acp_adapter.entry: Starting hermes-agent ACP adapter",
            "2026-11-11 01:18:54 [ERROR] root: Background task failed",
            "Traceback (most recent call last):",
            "  File \"/x/venv/lib/python3.14/site-packages/acp/connection.py\", line 208, in _run_request",
            "    raise err from None",
            "acp.exceptions.RequestError: Internal error",
            "",
        ];
        Check(recorded.All(line => !filter.Keep(line)), "log lines and tracebacks stay out");
        var fresh = new HermesAgent.StderrFilter();
        Check(fresh.Keep("The ACP server can't start: its protocol packages are missing from this install.") && fresh.Keep("From the Hermes environment, run: hermes pm install --extra acp"), "a start-up failure's words pass");
        Check(HermesAgent.VersionLine("Hermes Agent v0.23.1 (2026.9.24) · stable · local 3b4215785882\nInstall: C:\\x\nPython 3.14\n") == "Hermes Agent v0.23.1 (2026.9.24) · stable · local 3b4215785882", "the first line is the version");
        var environment = new Dictionary<string, string> { ["HERMES_YOLO_MODE"] = "1" };
        HermesAgent.Apply("manual", environment);
        Check(!environment.ContainsKey("HERMES_YOLO_MODE"), "an exported yolo does not override the pane");
        HermesAgent.Apply("fullAccess", environment);
        Check(environment["HERMES_YOLO_MODE"] == "1" && HermesAgent.EditPolicy("fullAccess") == "session" && HermesAgent.EditPolicy("acceptEdits") == "workspace_session" && HermesAgent.EditPolicy("manual") == "ask", "full access is yolo and the session policy");
        Check(ProviderCatalog.PermissionModes("hermes").SequenceEqual(["manual", "acceptEdits", "fullAccess"]), "Hermes has no plan mode");
        return Task.CompletedTask;
    }

    internal static Task TheHermesMenuIsThePickerTheLastSessionOffered()
    {
        var folder = Verification.Temp();
        try
        {
            var path = Path.Combine(folder, HermesAgent.ModelCacheFileName);
            Check(HermesAgent.CachedCatalog(path) is { Models: [{ Value: "default" }] } empty && empty.Detail == Locale.Get("provider.catalog.hermesEmpty"), "before a session only Hermes's own default");
            var session = JsonNode.Parse(Fixture("hermes", "answer")[1])!["result"]!["configOptions"];
            HermesAgent.SaveModels(session, path);
            var catalog = HermesAgent.CachedCatalog(path);
            Check(catalog.Source == "cli" && catalog.Models[0].Value == "default" && catalog.Models.Any(m => m.Value == HermesModel) && catalog.Detail == Locale.Get("provider.catalog.hermesSource"), "the picker: " + string.Join(", ", catalog.Models.Select(m => m.Value)));
            HermesAgent.ForgetModels(path);
            Check(!File.Exists(path) && HermesAgent.CachedCatalog(path).Models.Count == 1, "forgotten without a provider");
            HermesAgent.SaveModels(JsonNode.Parse("[]"), path);
            Check(!File.Exists(path), "an empty picker changes nothing");
        }
        finally { Directory.Delete(folder, true); }
        return Task.CompletedTask;
    }

    /// <summary>
    /// S-B11 (M/Tests/MightyCoreTests/HermesTests.swift aCancelledTurnAnswersItsWaitingPermissionsCancelled): a turn being cancelled answers
    /// the permission requests waiting on the user <c>cancelled</c> ("The Client MUST respond to all pending session/request_permission requests
    /// with the cancelled outcome"), answers one that comes later so at once, and lets no answer of the user's out after that.
    /// </summary>
    internal static Task ACancelledTurnAnswersItsWaitingPermissionsCancelled()
    {
        var run = Channel(Request("hermes", "edit the note"));
        run.Channel.Start();
        var lines = Fixture("hermes", "tools");
        var first = Array.FindIndex(lines, l => l.Contains("\"session/request_permission\""));
        var second = Array.FindIndex(lines, first + 1, l => l.Contains("\"session/request_permission\""));
        foreach (var line in lines.Take(first + 1)) run.Receive(line);
        var card = run.Permissions.Single(p => p.State == "pending");
        var written = run.Writes.Count;
        run.Channel.CancelTurn();
        static bool Cancelled(JsonObject frame, int id) => frame["id"] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.GetValue<int>() == id
            && frame["result"]?["outcome"]?["outcome"]?.GetValue<string>() == "cancelled";
        Check(run.Writes.Count == written + 1 && Cancelled(run.Writes[^1], 0), "the waiting request is answered cancelled: " + run.Writes[^1].ToJsonString());
        Check(run.Permissions[^1].Id == card.Id && run.Permissions[^1].State == "cancelled", "its card is cancelled");
        Check(Throws(() => run.Channel.Respond(card.Id, true)) && run.Writes.Count == written + 1, "the user's answer no longer leaves");
        run.Receive(lines[second]);
        Check(run.Writes.Count == written + 2 && Cancelled(run.Writes[^1], 1) && run.Permissions.Count(p => p.State == "pending") == 1, "a later request is answered cancelled at once, with no card");
        run.Channel.CancelTurn();
        Check(run.Writes.Count == written + 2 && !run.Channel.TurnCompleted, "cancelling again sends nothing");
        return Task.CompletedTask;
    }

    internal static Task AHermesSessionIsContinuedOnlyWhereItsYoloAndAnswerAllow()
    {
        const string id = HermesResumed;
        Check(HermesAgent.Resume(id, "manual", 0, false) == (null, null) && HermesAgent.Resume(id, "fullAccess", 0, true) == (null, null), "a session that never answered is not continued");
        Check(HermesAgent.Resume(id, "manual", 3, true) == (null, "run.notice.hermesFullAccessSession"), "a yolo session is not continued by a pane that asks");
        Check(HermesAgent.Resume(id, "acceptEdits", 3, true) == (null, "run.notice.hermesFullAccessSession"), "nor by one that accepts edits");
        Check(HermesAgent.Resume(id, "fullAccess", 3, false) == (id, "run.notice.hermesAskingSession"), "an asking session is continued and still asks");
        Check(HermesAgent.Resume(id, "fullAccess", 3, true) == (id, null) && HermesAgent.Resume(id, "manual", 3, false) == (id, null), "matching sessions are continued");
        // S-B12: a session whose policy receipt cannot be read may have started with --yolo; only a full-access pane continues it.
        Check(HermesAgent.Resume(id, "manual", 3, null) == (null, "run.notice.hermesUnknownSession") && HermesAgent.Resume(id, "acceptEdits", null, null) == (null, "run.notice.hermesUnknownSession"),
            "a pane that asks starts a new session when the receipt cannot be read");
        Check(HermesAgent.Resume(id, "fullAccess", 3, null) == (id, null) && HermesAgent.Resume(id, "fullAccess", null, null) == (id, null), "a full-access pane continues it");
        Check(Locale.Get("run.notice.hermesUnknownSession") != "run.notice.hermesUnknownSession", "the pane is told why");
        return Task.CompletedTask;
    }

    internal static Task AttachedCopiesAreNamedInThePromptAndPicturesAreImageBlocks()
    {
        var writes = new List<JsonObject>();
        const string reference = "Attached file \"notes.txt\": \"C:\\Users\\me\\AppData\\Local\\Temp\\m\\0-notes.txt\" (read this copy as needed).";
        var channel = new AcpChannel("run-opencode", null, "/tmp/project", AcpChannel.PromptText(" ", [reference], null), [("image/png", "iVBORw0KGgo=")], [],
            frame => writes.Add(JsonNode.Parse(frame)!.AsObject()), _ => { }, _ => { }, (_, _) => { }, _ => { }, _ => { }, () => { });
        channel.Start();
        var lines = Fixture("opencode", "answer");
        channel.Receive(lines[0]); channel.Receive(lines[1]);
        var blocks = writes.Last(w => Text(w["method"]) == "session/prompt")["params"]!["prompt"]!.AsArray();
        // No words of the user's own: the attachments' default words, then each copy by its plain path.
        Check(Text(blocks[0]!["text"]) == Locale.Get("attachments.prompt.default") + "\n\n" + reference, "the text block: " + Text(blocks[0]!["text"]));
        Check(blocks.Count == 2 && Text(blocks[1]!["type"]) == "image" && Text(blocks[1]!["data"]) == "iVBORw0KGgo=" && Text(blocks[1]!["mimeType"]) == "image/png", "a picture is an image block");
        return Task.CompletedTask;
    }

    internal static Task TheClarifyConfigValueIsTheMacs()
    {
        Check(HermesQuestionTool.Value([]).SequenceEqual(["hermes-acp", "clarify", "x_search"]) && HermesQuestionTool.Value(["kanban"]).SequenceEqual(["hermes-acp", "clarify", "x_search", "kanban"]), "the saved list");
        Check(HermesQuestionTool.Names("[\"hermes-acp\",\"clarify\"]")!.SequenceEqual(["hermes-acp", "clarify"]) && HermesQuestionTool.Names("\"['hermes-acp', 'clarify']\"")!.SequenceEqual(["hermes-acp", "clarify"]) && HermesQuestionTool.Names("{}") is null, "lists as Hermes prints them");
        return Task.CompletedTask;
    }
}
