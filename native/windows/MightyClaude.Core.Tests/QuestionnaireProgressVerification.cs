using MightyClaude.Core;

/// <summary>Answering one question at a time, in a style's panel and in the pet's bubble (macOS QuestionnaireProgressTests, M/AgentCompanion.swift:295-332).</summary>
internal static class QuestionnaireProgressVerification
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static UserQuestionnaire Form(string json) => UserQuestionnaire.Parse(json) ?? throw new InvalidOperationException("fixture questionnaire did not parse");
    private static bool Same(UserQuestionAnswer? answer, string[] picks, string? text = null) => answer is not null && answer.SelectedOptions.SequenceEqual(picks) && answer.CustomText == text;
    private const string Mixed = """{"questions":[{"header":"범위","question":"어디까지 할까요?","multiSelect":false,"options":[{"label":"A","description":""},{"label":"B","description":"b"}]},{"header":"대상","question":"무엇을 포함할까요?","multiSelect":true,"options":[{"label":"X","description":""},{"label":"Y","description":""},{"label":"Z","description":""}]}]}""";
    private const string MultiFirst = """{"questions":[{"header":"대상","question":"무엇을?","multiSelect":true,"options":[{"label":"X","description":""},{"label":"Y","description":""}]},{"header":"범위","question":"어디까지?","multiSelect":false,"options":[{"label":"A","description":""},{"label":"B","description":""}]}]}""";
    private const string Singles = """{"questions":[{"header":"하나","question":"첫째?","multiSelect":false,"options":[{"label":"A","description":""},{"label":"B","description":""}]},{"header":"둘","question":"둘째?","multiSelect":false,"options":[{"label":"C","description":""},{"label":"D","description":""}]}]}""";
    private const string Lone = """{"questions":[{"header":"하나","question":"하나만?","multiSelect":false,"options":[{"label":"A","description":""},{"label":"B","description":""}]}]}""";
    private const string LoneMulti = """{"questions":[{"header":"여럿","question":"여럿?","multiSelect":true,"options":[{"label":"A","description":""},{"label":"B","description":""}]}]}""";

    internal static Task ProgressWalksQuestionsAndProducesValidAnswers()
    {
        var form = Form(Mixed);
        var progress = new QuestionnaireProgress("k");
        Check(progress.Current(form)?.Header == "범위", "the first question is shown first");
        Check(progress.Commit("   ", form) is null, "nothing to answer with");
        Check(progress.Choose("missing", form) is null && progress.Index == 0, "a label that was not offered answers nothing");
        Check(progress.Choose("B", form) is { Complete: false } && progress.Index == 1, "a single-select tile answers and moves on");
        // Multi-select tiles toggle; Enter (or 선택 완료) commits them with optional text, in option order.
        Check(progress.Choose("Z", form) is null && progress.Choose("X", form) is null && progress.Choose("Y", form) is null, "a multi-select tile only toggles");
        Check(progress.Choose("Y", form) is null && progress.Selected.SequenceEqual(["Z", "X"]), "a second tap takes the pick back");
        var answers = progress.Commit("그리고 W", form)?.Answers ?? throw new InvalidOperationException("expected completion");
        Check(Same(answers["어디까지 할까요?"], ["B"]) && Same(answers["무엇을 포함할까요?"], ["X", "Z"], "그리고 W"), "picks in option order, the text with them");
        var validated = form.ValidateAnswers(answers);
        Check(validated["어디까지 할까요?"] == "B" && validated["무엇을 포함할까요?"] == "X, Z, 그리고 W", "the answers are the ones the CLI accepts");
        // Free text alone answers a single-select question; back returns to it.
        var typed = new QuestionnaireProgress("k2");
        Check(typed.Commit("직접 입력", form) is { Complete: false }, "typed text answers a single-select question");
        typed.Back(form);
        Check(typed.Index == 0 && typed.Selected.Count == 0, "back returns to the first question");
        // Going back to a multi-select question shows its earlier picks again.
        var revisit = new QuestionnaireProgress("k3"); var multiFirst = Form(MultiFirst);
        revisit.Choose("Y", multiFirst);
        Check(revisit.Commit("", multiFirst) is { Complete: false }, "picks alone answer a multi-select question");
        revisit.Back(multiFirst);
        Check(revisit.Index == 0 && revisit.Selected.SequenceEqual(["Y"]), "the earlier picks are shown again");
        Check(typed.Choose("A", form) is { Complete: false } && Same(typed.Answers["어디까지 할까요?"], ["A"]), "a tile replaces the typed answer");
        // The pet highlights the recorded pick after 이전; answers given further on survive re-answering an earlier question.
        var pet = new QuestionnaireProgress("k4"); var singles = Form(Singles);
        Check(pet.Choose("A", singles) is { Complete: false }, "the first of two answers moves on");
        pet.Back(singles);
        Check(pet.Index == 0 && pet.Answers["첫째?"].SelectedOptions.SequenceEqual(["A"]), "the recorded pick is kept");
        pet.Back(singles);
        Check(pet.Index == 0, "already at the first question");
        Check(pet.Choose("B", singles) is { Complete: false }, "re-answering moves on again");
        var revised = pet.Choose("D", singles)?.Answers ?? throw new InvalidOperationException("expected completion");
        Check(singles.ValidateAnswers(revised) is var sent && sent["첫째?"] == "B" && sent["둘째?"] == "D", "the revised answers are sent");
        // A failed send leaves the last multi-choice question as it was, ready to send again.
        var retry = new QuestionnaireProgress("k5");
        retry.Choose("A", form); retry.Choose("X", form);
        var first = retry.Commit("", form)?.Answers ?? throw new InvalidOperationException("expected completion");
        Check(retry.Index == 1 && retry.Selected.SequenceEqual(["X"]), "the last question stays as it was");
        var second = retry.Commit("", form)?.Answers ?? throw new InvalidOperationException("expected a second completion");
        Check(first.Keys.Order().SequenceEqual(second.Keys.Order()) && first.All(pair => Same(second[pair.Key], pair.Value.SelectedOptions.ToArray(), pair.Value.CustomText)), "the same answers are ready to send again");
        return Task.CompletedTask;
    }

    /// <summary>The question a style's panel answers, and what is left for the permission card (M/AppStore+Styles.swift:470-481, 554-560).</summary>
    internal static Task TheStylePanelTakesTheWaitingQuestion()
    {
        var bash = new ToolPermissionRequest("perm-1", "pane", "toolu_b", "Bash", "{\"command\":\"ls\"}", "ls");
        var ask = new ToolPermissionRequest("ask-1", "pane", "toolu_a", "AskUserQuestion", Mixed, "ask", CanAllow: false, CanAnswerQuestions: true);
        var later = ask with { Id = "ask-2", InputJson = Lone };
        Check(GuidedQuestions.Waiting(null) is null && GuidedQuestions.Waiting([bash]) is null, "no question, nothing for the panel");
        Check(GuidedQuestions.Waiting([bash, ask, later]) is { } waiting && waiting.Request.Id == "ask-1" && waiting.Questionnaire.Questions.Count == 2, "the first waiting question, wherever it stands in the queue");
        Check(GuidedQuestions.Waiting([ask with { CanAnswerQuestions = false }]) is null && GuidedQuestions.Waiting([ask with { State = "answered" }]) is null
            && GuidedQuestions.Waiting([ask with { InputJson = "{}" }]) is null && GuidedQuestions.Waiting([ask with { ToolName = "Other" }]) is null, "only a pending question that can be answered whole");
        Check(GuidedQuestions.Visible([bash, ask, later]).Select(r => r.Id).SequenceEqual(["perm-1", "ask-2"]), "the card shows every other request, the next question among them");
        Check(GuidedQuestions.Visible([bash with { State = "allowed" }, ask]).Count == 0 && GuidedQuestions.Visible(null).Count == 0, "settled requests are not shown");
        return Task.CompletedTask;
    }

    /// <summary>The pet's bubble: a tap answers a lone single-choice question, several questions go back, next and send (M/AgentCompanion.swift:295-332).</summary>
    internal static Task ThePetAnswersFromItsBubble()
    {
        string Ids(UserQuestionnaire form, QuestionnaireProgress progress) => string.Join(",", CompanionBubble.Question(form, progress).Select(b => b.Id + (b.Disabled ? "-off" : "")));
        // One single-choice question: a tap sends it; there is nothing to commit.
        var lone = Form(Lone); var one = new QuestionnaireProgress("one");
        Check(Ids(lone, one) == "open,questions,deny" && !CompanionBubble.NeedsCommit(lone, one), "a lone single-choice question needs no Send");
        Check(CompanionBubble.Choose("missing", lone, one) is null, "a label that was not offered sends nothing");
        Check(CompanionBubble.Choose("B", lone, one) is { } sent && lone.ValidateAnswers(sent)["하나만?"] == "B" && CompanionBubble.Picked("B", lone, one) && !CompanionBubble.Picked("A", lone, one), "a tap answers it");
        // One multi-choice question: taps toggle, Send answers.
        var loneMulti = Form(LoneMulti); var many = new QuestionnaireProgress("many");
        Check(Ids(loneMulti, many) == "open,questions,question-commit-off,deny" && CompanionBubble.Commit(loneMulti, many) is null, "Send waits for a pick");
        Check(CompanionBubble.Choose("B", loneMulti, many) is null && CompanionBubble.Choose("A", loneMulti, many) is null && CompanionBubble.Picked("A", loneMulti, many), "a multi-choice tap only toggles");
        Check(Ids(loneMulti, many) == "open,questions,question-commit,deny" && CompanionBubble.Question(loneMulti, many).Single(b => b.Id == CompanionBubble.Next).Label == Locale.Get("composer.send.name"), "Send once something is picked");
        Check(CompanionBubble.Commit(loneMulti, many) is { } picks && loneMulti.ValidateAnswers(picks)["여럿?"] == "A, B", "Send answers with the picks in option order");
        // Several questions: nothing is sent from a tap, the last one waits for Send, Back shows the recorded pick.
        var singles = Form(Singles); var two = new QuestionnaireProgress("two");
        Check(CompanionBubble.QuestionTitle(singles, two) == Locale.Get("companion.question.titleProgress", new Dictionary<string, string> { ["current"] = "1", ["total"] = "2" })
            && CompanionBubble.QuestionTitle(lone, one) == Locale.Get("phone.questionnaire.title"), "the title counts only several questions");
        Check(Ids(singles, two) == "open,questions,deny", "the first single-choice question of several answers at a tap");
        Check(CompanionBubble.Choose("A", singles, two) is null && two.Index == 1, "a tap records and moves on, sending nothing");
        Check(Ids(singles, two) == "open,questions,question-back,question-commit-off,deny", "the last question shows Back and a Send that waits for its answer");
        Check(CompanionBubble.Choose("D", singles, two) is null && two.Index == 1 && CompanionBubble.Picked("D", singles, two), "the last tap is recorded, not sent");
        two.Back(singles);
        Check(two.Index == 0 && CompanionBubble.Picked("A", singles, two) && Ids(singles, two) == "open,questions,deny", "Back shows the recorded pick");
        CompanionBubble.Choose("B", singles, two);
        Check(CompanionBubble.Commit(singles, two) is { } both && singles.ValidateAnswers(both) is var final && final["첫째?"] == "B" && final["둘째?"] == "D", "Send sends every answer");
        // A multi-choice question first: Next records its picks and moves on.
        var multiFirst = Form(MultiFirst); var mixed = new QuestionnaireProgress("mixed");
        CompanionBubble.Choose("Y", multiFirst, mixed);
        Check(CompanionBubble.Question(multiFirst, mixed).Single(b => b.Id == CompanionBubble.Next) is { Prominent: true, Disabled: false } next && next.Label == Locale.Get("companion.question.next"), "Next under a multi-choice question that is not the last");
        Check(CompanionBubble.Commit(multiFirst, mixed) is null && mixed.Index == 1, "Next moves on without sending");
        return Task.CompletedTask;
    }
}
