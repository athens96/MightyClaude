using System.Text.Json;
using System.Text.Json.Nodes;
using MightyCode.Core;

/// <summary>
/// pi's questions (M/Tests/PiTests.swift): the app's <c>ask_user</c> extension shipped with the app and loaded per run, and the extension dialogs
/// pi sends in RPC mode (<c>extension_ui_request</c>) shown as the question card, against the recordings pi-rpc-question.jsonl and pi-rpc-dialogs.jsonl.
/// </summary>
internal static class PiDialogVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string[] Fixture(string name) => File.ReadAllLines(Path.Combine(AcpVerification.Root(), "native", "contracts", "fixtures", $"pi-rpc-{name}.jsonl")).Where(l => l.Length > 0).ToArray();

    private sealed class Asked
    {
        public readonly List<(string Kind, string Text)> Logs = [];
        public readonly List<AgentActivity> Activities = [];
        public readonly List<ToolPermissionRequest> Cards = [];
        public readonly List<JsonObject> Writes = [];
        public readonly List<string> Cancelled = [], Warnings = [];
        public readonly List<(string Id, double Seconds)> Timers = [];
        public OutputParser Parser = null!;
        public PiDialogChannel Dialogs = null!;
        public List<ToolPermissionRequest> Pending => Cards.Where(card => card.State == "pending" && !Cards.Any(c => c.Id == card.Id && c.State != "pending")).ToList();
    }
    private static Asked Ask()
    {
        var run = new Asked();
        run.Parser = new OutputParser("pi", (kind, text) => run.Logs.Add((kind, text)), _ => { }, activity: a => run.Activities.Add(a), activityNamespace: "run-pi", piReply: run.Cancelled.Add);
        run.Dialogs = new PiDialogChannel("run-pi", text => run.Writes.Add(JsonNode.Parse(text)!.AsObject()), run.Cards.Add, (card, state) => run.Parser.PermissionActivity(card, state), run.Warnings.Add, (id, seconds) => run.Timers.Add((id, seconds)));
        run.Parser.PiDialogs = run.Dialogs;
        return run;
    }
    private static Dictionary<string, UserQuestionAnswer> Answer(ToolPermissionRequest card, string[]? picked = null, string? typed = null) =>
        new() { [UserQuestionnaire.Parse(card.InputJson)!.Questions[0].Question] = new(picked ?? [], typed) };
    private static bool Throws(Action action) { try { action(); return false; } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return true; } }

    internal static Task TheAskUserToolIsTheQuestionCardAndItsAnswerGoesBackAsTheDialogsValue()
    {
        var run = Ask(); var answered = false;
        foreach (var line in Fixture("question"))
        {
            run.Parser.Parse(line);
            if (answered || run.Pending.FirstOrDefault() is not { } card) continue;
            answered = true;
            var questions = UserQuestionnaire.Parse(card.InputJson)!.Questions;
            Check(card.ToolName == "AskUserQuestion" && card.CanAnswerQuestions && !card.CanAllow && card.ToolUseId == "toolu_question_1", "the card: " + JsonSerializer.Serialize(card));
            Check(questions.Select(q => q.Question).SequenceEqual(["What is your favorite color?"]) && questions[0].Options.Select(o => o.Label).SequenceEqual(["Red", "Green", "Blue"]) && !questions[0].MultiSelect, "the question");
            Check(run.Activities.Last().State == "waiting", "the ask_user row waits");
            Check(Throws(() => run.Dialogs.AnswerQuestions(card.Id, Answer(card))), "nothing picked is no answer");
            run.Dialogs.AnswerQuestions(card.Id, Answer(card, ["Red"]));
        }
        Check(answered && run.Cancelled.Count == 0 && run.Warnings.Count == 0, "answered, nothing cancelled");
        Check(run.Writes.Count == 1 && run.Writes[0]["type"]!.GetValue<string>() == "extension_ui_response" && run.Writes[0]["value"]!.GetValue<string>() == "Red" && run.Writes[0]["id"]!.GetValue<string>() == "7dcf54f5-170a-4f79-9fbb-3e627cc9a099", "the dialog's value");
        Check(run.Cards.Select(c => c.State).SequenceEqual(["pending", "answered"]), "pending, answered");
        Check(run.Logs.Where(l => l.Kind == "assistant").Select(l => l.Text).SequenceEqual(["Your favorite color is red."]) && run.Activities.Last(a => a.ToolName == "ask_user").State == "completed", "the turn goes on: " + string.Join(" | ", run.Logs));
        return Task.CompletedTask;
    }

    internal static Task TheAppsToolTakesATypedAnswerAndSeveralPicksAndAnyOtherSelectOnlyItsOptions()
    {
        static (Asked Run, ToolPermissionRequest Card) Asking(bool multiple, string tool)
        {
            var run = Ask();
            run.Parser.Parse("{\"type\":\"tool_execution_start\",\"toolCallId\":\"call-1\",\"toolName\":\"" + tool + "\",\"args\":{\"question\":\"Which fruits?\",\"options\":[\"Apple\",\"Banana\",\"Cherry\"],\"multiple\":" + (multiple ? "true" : "false") + "}}");
            run.Parser.Parse("{\"type\":\"extension_ui_request\",\"id\":\"dialog-1\",\"method\":\"select\",\"title\":\"Which fruits?\",\"options\":[\"Apple\",\"Banana\",\"Cherry\"]}");
            return (run, run.Pending.First());
        }
        var (run, card) = Asking(true, "ask_user");
        Check(UserQuestionnaire.Parse(card.InputJson)!.Questions[0].MultiSelect, "several picks");
        run.Dialogs.AnswerQuestions(card.Id, Answer(card, ["Cherry", "Apple"], " Mango "));
        Check(run.Writes.Last()["value"]!.GetValue<string>() == "Apple, Cherry, Mango", "in the offered order, the typed answer last");
        (run, card) = Asking(false, "ask_user");
        run.Dialogs.AnswerQuestions(card.Id, Answer(card, typed: "Mango"));
        Check(run.Writes.Last()["value"]!.GetValue<string>() == "Mango", "a typed answer");
        (run, card) = Asking(false, "ask_user");
        run.Parser.Parse("{\"type\":\"tool_execution_end\",\"toolCallId\":\"call-1\",\"toolName\":\"ask_user\",\"result\":{\"content\":[]},\"isError\":true}");
        Check(run.Cards.Select(c => c.State).SequenceEqual(["pending", "cancelled"]) && run.Writes.Count == 0, "an aborted tool withdraws its card");
        (run, card) = Asking(true, "pick_fruit");
        Check(!UserQuestionnaire.Parse(card.InputJson)!.Questions[0].MultiSelect && card.ToolUseId == "pi-dialog-dialog-1", "another extension's select");
        Check(Throws(() => run.Dialogs.AnswerQuestions(card.Id, Answer(card, typed: "Mango"))) && run.Writes.Count == 0 && run.Pending.Count == 1, "reads only its options");
        run.Dialogs.AnswerQuestions(card.Id, Answer(card, ["Banana"]));
        Check(run.Writes.Last()["value"]!.GetValue<string>() == "Banana", "one option");
        return Task.CompletedTask;
    }

    internal static Task ConfirmAndInputAreCardsATimedDialogClosesItselfAndAnEditorIsStillCancelled()
    {
        var run = Ask(); var lines = Fixture("dialogs");
        run.Parser.Parse(lines[0]);
        var card = run.Pending.First(); var question = UserQuestionnaire.Parse(card.InputJson)!.Questions[0];
        Check(question.Header == "Clear cache?" && question.Question == "All cached files will be removed." && question.Options.Select(o => o.Label).SequenceEqual([Locale.Get("questionnaire.option.yes"), Locale.Get("questionnaire.option.no")]), "confirm");
        Check(Throws(() => run.Dialogs.AnswerQuestions(card.Id, Answer(card, typed: "maybe"))), "confirm takes yes or no");
        run.Dialogs.AnswerQuestions(card.Id, Answer(card, [Locale.Get("questionnaire.option.no")]));
        Check(run.Writes.Last()["confirmed"]!.GetValue<bool>() == false && run.Writes.Last()["id"]!.GetValue<string>() == "4b4a8f51-a5ac-41bd-a77d-a13763abd897", "confirmed false");
        run.Parser.Parse(lines[1]);
        card = run.Pending.First(); question = UserQuestionnaire.Parse(card.InputJson)!.Questions[0];
        Check(question.Question == "Project name" && question.Options.Count == 0, "input: nothing to pick");
        run.Dialogs.AnswerQuestions(card.Id, Answer(card, typed: "demo"));
        Check(run.Writes.Last()["value"]!.GetValue<string>() == "demo", "the typed text");
        run.Parser.Parse(lines[2]);
        card = run.Pending.First();
        Check(run.Timers.Count == 1 && run.Timers[0].Id == card.Id && run.Timers[0].Seconds == 1.5, "a deadline");
        run.Dialogs.TimedOut(card.Id);
        Check(run.Pending.Count == 0 && run.Cards.Last().State == "cancelled" && run.Warnings.SequenceEqual([Locale.Get("run.notice.piDialogTimedOut", new Dictionary<string, string> { ["title"] = "Pick a color" })]), "pi closed it itself");
        Check(Throws(() => run.Dialogs.AnswerQuestions(card.Id, Answer(card, ["Red"]))), "too late");
        run.Parser.Parse(lines[3]);
        Check(run.Pending.Count == 0 && run.Cancelled.Count == 1 && JsonNode.Parse(run.Cancelled[0])!["cancelled"]!.GetValue<bool>() && JsonNode.Parse(run.Cancelled[0])!["id"]!.GetValue<string>() == "b0b081e4-bba2-4b64-a094-7886256af76f", "an editor is cancelled");
        Check(run.Logs.Last().Text == Locale.Get("run.notice.piDialogDismissed", new Dictionary<string, string> { ["title"] = "Edit notes" }), "and named");
        run.Parser.Parse(lines[4]);
        Check(run.Writes.Count == 2 && run.Cancelled.Count == 1, "notify needs no answer");
        // The card's Cancel dismisses the dialog; a stopped run withdraws it; more options than the card holds is cancelled as before.
        var cancel = Ask();
        cancel.Parser.Parse(lines[0]);
        card = cancel.Pending.First();
        Check(Throws(() => cancel.Dialogs.Respond(card.Id, true)), "a dialog is not allowed");
        cancel.Dialogs.Respond(card.Id, false);
        Check(cancel.Writes.Last()["cancelled"]!.GetValue<bool>() && cancel.Cards.Last().State == "denied", "dismissed");
        cancel.Parser.Parse(lines[1]);
        card = cancel.Pending.First();
        cancel.Dialogs.CancelAll();
        Check(cancel.Cards.Last().State == "cancelled" && cancel.Writes.Count == 1 && Throws(() => cancel.Dialogs.AnswerQuestions(card.Id, Answer(card, typed: "late"))), "withdrawn when the run stops");
        var many = Ask();
        many.Parser.Parse("{\"type\":\"extension_ui_request\",\"id\":\"dialog-9\",\"method\":\"select\",\"title\":\"Pick\",\"options\":[" + string.Join(",", Enumerable.Range(1, 13).Select(i => "\"option " + i + "\"")) + "]}");
        Check(many.Cards.Count == 0 && many.Cancelled.Count == 1 && many.Logs.Last().Text == Locale.Get("run.notice.piDialogDismissed", new Dictionary<string, string> { ["title"] = "Pick" }), "thirteen options are cancelled");
        return Task.CompletedTask;
    }

    internal static Task TheShippedExtensionIsLoadedForEachRunAndTheReadOnlyListNamesItsTool()
    {
        var file = AgentFiles.Find(PiAgent.QuestionExtensionName);
        Check(file is not null && File.ReadAllText(file).Contains("ask_user") && !File.ReadAllText(file).Contains("require("), "the shipped extension registers ask_user with no dependencies");
        var manual = ProviderCatalog.Arguments(new("pane", "space", "claude", "hi", [], Provider: "pi"), "");
        Check(manual.SkipWhile(a => a != "--tools").Skip(1).First() == Wire.PiReadOnlyTools + "," + PiAgent.QuestionTool && manual.SkipWhile(a => a != "--extension").Skip(1).First() == file, "manual: " + string.Join(' ', manual));
        var full = ProviderCatalog.Arguments(new("pane", "space", "claude", "hi", [], Provider: "pi", Settings: new RunSettings(PermissionMode: "fullAccess")), "");
        Check(!full.Contains("--tools") && full.SkipWhile(a => a != "--extension").Skip(1).First() == file, "full access: " + string.Join(' ', full));
        // Without the shipped file pi runs without the tool, as before.
        var folder = Verification.Temp();
        var saved = AgentFiles.Directory;
        try
        {
            AgentFiles.Directory = folder;
            Check(AgentFiles.Find(PiAgent.QuestionExtensionName) is null, "a folder without the file");
            {
                var bare = ProviderCatalog.Arguments(new("pane", "space", "claude", "hi", [], Provider: "pi"), "");
                Check(bare.SkipWhile(a => a != "--tools").Skip(1).First() == Wire.PiReadOnlyTools && !bare.Contains("--extension"), "without the file: " + string.Join(' ', bare));
            }
        }
        finally { AgentFiles.Directory = saved; Directory.Delete(folder, true); }
        return Task.CompletedTask;
    }
}
