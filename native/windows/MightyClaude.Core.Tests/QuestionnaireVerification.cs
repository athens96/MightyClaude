using System.Text.Json;
using MightyClaude.Core;

internal static class QuestionnaireVerification
{
    private const string Input = """{"questions":[{"header":"방식","question":"어떻게 처리할까요?","multiSelect":false,"options":[{"label":"첫 선택","description":"설명"},{"label":"다음 선택","description":""}]},{"header":"범위","question":"범위는?","multiSelect":true,"options":[{"label":"A","description":"첫 범위"},{"label":"B","description":"다음 범위"}]}],"response":"model-supplied response must not win","extra":"preserved"}""";
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected the answer to be refused");
    }

    internal static Task QuestionnaireParsingRequiresCompleteBoundedChoices()
    {
        var parsed = UserQuestionnaire.Parse(Input);
        Check(parsed?.Questions.Count == 2 && parsed.Questions[0].Question == "어떻게 처리할까요?", "complete original Korean question text stays a protocol key");
        Check(UserQuestionnaire.Parse("""{"questions":[{"question":"Incomplete"}]}""") is null, "partial inputs cannot create an answer form");
        Check(UserQuestionnaire.Parse(Input.Replace("다음 선택", "첫 선택", StringComparison.Ordinal)) is null, "duplicate option labels are refused");
        Check(UserQuestionnaire.Parse(Input.Replace("범위는?", "어떻게 처리할까요?", StringComparison.Ordinal)) is null, "duplicate question keys are refused");
        Check(UserQuestionnaire.Parse(Input.Replace("설명", new string('가', 30_000), StringComparison.Ordinal)) is null, "complete-display UTF-8 bound is enforced");
        return Task.CompletedTask;
    }

    internal static Task QuestionnaireAnswersEnforceSelectionModeAndOriginalOrder()
    {
        var questionnaire = UserQuestionnaire.Parse(Input)!;
        var answers = new Dictionary<string, UserQuestionAnswer> { ["어떻게 처리할까요?"] = new(["첫 선택"]), ["범위는?"] = new(["B", "A"], "  자유 응답  ") };
        var validated = questionnaire.ValidateAnswers(answers);
        Check(validated["범위는?"] == "A, B, 자유 응답", "multiple answers follow displayed order, not click order");
        Reject(() => questionnaire.ValidateAnswers(new Dictionary<string, UserQuestionAnswer>()));
        answers["어떻게 처리할까요?"] = new(["첫 선택", "다음 선택"]); Reject(() => questionnaire.ValidateAnswers(answers));
        answers["어떻게 처리할까요?"] = new(["첫 선택"], "other"); Reject(() => questionnaire.ValidateAnswers(answers));
        answers["어떻게 처리할까요?"] = new([], "내 답변"); Check(questionnaire.ValidateAnswers(answers)["어떻게 처리할까요?"] == "내 답변", "free text may replace a single option");
        answers["범위는?"] = new(["unoffered"]); Reject(() => questionnaire.ValidateAnswers(answers));
        return Task.CompletedTask;
    }

    internal static Task AnswerRpcSettlesOnceAndReplacesModelSuppliedResponse()
    {
        var writes = new List<string>(); var shown = new List<ToolPermissionRequest>();
        var channel = new ClaudePermissionChannel("run", "prompt", writes.Add, shown.Add, (_, _) => { }, _ => { }, _ => { });
        channel.Receive("""{"type":"control_request","request_id":"q1","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","tool_use_id":"tool-q1","input":INPUT}}""".Replace("INPUT", Input, StringComparison.Ordinal));
        Check(shown[^1].CanAnswerQuestions && !shown[^1].CanAllow, "question has answer UI, never generic allow");
        Reject(() => channel.AnswerQuestions("q1", new Dictionary<string, UserQuestionAnswer>()));
        Check(channel.Waiting.Count == 1 && writes.Count == 0, "invalid draft stays pending and emits no reply");
        var answers = new Dictionary<string, UserQuestionAnswer> { ["어떻게 처리할까요?"] = new(["다음 선택"]), ["범위는?"] = new(["B"]) };
        channel.AnswerQuestions("q1", answers);
        using var reply = JsonDocument.Parse(writes.Single());
        var result = reply.RootElement.GetProperty("response").GetProperty("response"); var input = result.GetProperty("updatedInput");
        Check(result.GetProperty("behavior").GetString() == "allow" && result.GetProperty("toolUseID").GetString() == "tool-q1", "answer is scoped to the specific tool call");
        Check(input.GetProperty("answers").GetProperty("어떻게 처리할까요?").GetString() == "다음 선택" && !input.TryGetProperty("response", out _) && input.GetProperty("extra").GetString() == "preserved", "only user answers replace response, original extra fields preserved");
        Check(channel.Waiting.Count == 0 && shown[^1].State == "answered", "request settles immediately");
        Reject(() => channel.AnswerQuestions("q1", answers)); Check(writes.Count == 1, "double submit never emits a second answer");
        channel.CancelAll(); Reject(() => channel.AnswerQuestions("q1", answers));
        return Task.CompletedTask;
    }

    internal static Task BrokenQuestionnaireReplyFailsWithoutClaimingDelivery()
    {
        var shown = new List<ToolPermissionRequest>(); var failed = false;
        var channel = new ClaudePermissionChannel("run", "prompt", _ => throw new IOException("stdin closed"), shown.Add, (_, _) => { }, _ => { }, _ => failed = true);
        channel.Receive("""{"type":"control_request","request_id":"q1","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","tool_use_id":"tool-q1","input":INPUT}}""".Replace("INPUT", Input, StringComparison.Ordinal));
        var answers = new Dictionary<string, UserQuestionAnswer> { ["어떻게 처리할까요?"] = new(["다음 선택"]), ["범위는?"] = new(["B"]) };
        Reject(() => channel.AnswerQuestions("q1", answers));
        Check(failed && channel.Failed && channel.Waiting.Count == 0, "a broken response channel fails closed");
        Check(shown.All(request => request.State != "answered") && shown[^1].State == "cancelled", "a failed write never claims the answer was delivered");
        return Task.CompletedTask;
    }
}
