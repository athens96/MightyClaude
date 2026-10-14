namespace MightyClaude.Core;

/// <summary>What one answer did (M/MightyCore/QuestionnaireProgress.swift Step): moved to the next question, or completed the set with <paramref name="Answers"/>.</summary>
public sealed record QuestionnaireStep(IReadOnlyDictionary<string, UserQuestionAnswer>? Answers)
{
    public static readonly QuestionnaireStep Next = new((IReadOnlyDictionary<string, UserQuestionAnswer>?)null);
    public bool Complete => Answers is not null;
}

/// <summary>
/// Answering an AskUserQuestion one question at a time (M/MightyCore/QuestionnaireProgress.swift): option
/// tiles and typed text fill <see cref="Answers"/> until every question has one.
/// </summary>
public sealed class QuestionnaireProgress(string requestKey)
{
    private readonly Dictionary<string, UserQuestionAnswer> answers = new(StringComparer.Ordinal);
    private readonly List<string> selected = [];
    public string RequestKey { get; } = requestKey;
    public int Index { get; private set; }
    public IReadOnlyDictionary<string, UserQuestionAnswer> Answers => answers;
    /// <summary>Picks made so far for the current multi-select question.</summary>
    public IReadOnlyList<string> Selected => selected;

    public UserQuestion? Current(UserQuestionnaire questionnaire) => Index < questionnaire.Questions.Count ? questionnaire.Questions[Index] : null;

    /// <summary>A single-select tile answers the question; a multi-select tile toggles, and answers nothing yet.</summary>
    public QuestionnaireStep? Choose(string label, UserQuestionnaire questionnaire)
    {
        if (Current(questionnaire) is not { } question || !question.Options.Any(option => option.Label == label)) return null;
        if (!question.MultiSelect) return Record(new([label]), question, questionnaire);
        if (!selected.Remove(label)) selected.Add(label);
        return null;
    }

    /// <summary>Enter in the composer: the typed text (and any toggled tiles) answer the question.</summary>
    public QuestionnaireStep? Commit(string? customText, UserQuestionnaire questionnaire)
    {
        if (Current(questionnaire) is not { } question) return null;
        var text = customText?.Trim() ?? "";
        if (text.Length == 0 && selected.Count == 0) return null;
        string[] picks = question.MultiSelect ? question.Options.Select(option => option.Label).Where(selected.Contains).ToArray() : [];
        return Record(new(picks, text.Length == 0 ? null : text), question, questionnaire);
    }

    /// <summary>Returns to the previous question with its earlier picks shown again.</summary>
    public void Back(UserQuestionnaire questionnaire)
    {
        if (Index == 0) return;
        Index--;
        var previous = questionnaire.Questions[Index];
        selected.Clear();
        if (previous.MultiSelect && answers.TryGetValue(previous.Question, out var answer)) selected.AddRange(answer.SelectedOptions);
    }

    private QuestionnaireStep Record(UserQuestionAnswer answer, UserQuestion question, UserQuestionnaire questionnaire)
    {
        answers[question.Question] = answer;
        // The last question stays on screen until the send succeeds, so its picks stay too.
        if (Index + 1 >= questionnaire.Questions.Count) return new(new Dictionary<string, UserQuestionAnswer>(answers, StringComparer.Ordinal));
        selected.Clear(); Index++;
        return QuestionnaireStep.Next;
    }
}

/// <summary>The agent's question a pane in a style answers from its style panel (M/AppStore+Styles.swift:470-481, 554-560).</summary>
public static class GuidedQuestions
{
    /// <summary>The first answerable AskUserQuestion waiting in the pane, read whole; null when none waits.</summary>
    public static (ToolPermissionRequest Request, UserQuestionnaire Questionnaire)? Waiting(IEnumerable<ToolPermissionRequest>? requests)
    {
        foreach (var request in requests ?? [])
            if (request is { State: "pending", CanAnswerQuestions: true, ToolName: "AskUserQuestion" } && UserQuestionnaire.Parse(request.InputJson) is { } questionnaire)
                return (request, questionnaire);
        return null;
    }

    /// <summary>What the permission card shows beside a style panel: every waiting request but the question the panel answers.</summary>
    public static IReadOnlyList<ToolPermissionRequest> Visible(IEnumerable<ToolPermissionRequest>? requests)
    {
        var pending = (requests ?? []).Where(request => request.State == "pending").ToList();
        if (Waiting(pending) is { } answering) pending.Remove(answering.Request);
        return pending;
    }
}
