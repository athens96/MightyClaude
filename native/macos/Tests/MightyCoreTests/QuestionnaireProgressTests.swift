import Foundation
import Testing
@testable import MightyCore

struct QuestionnaireProgressTests {
    private func questionnaire(_ json: String) throws -> UserQuestionnaire { try #require(UserQuestionnaire.parse(inputJSON: json)) }

    @Test func questionnaireProgressWalksQuestionsAndProducesValidAnswers() throws {
        let form = try questionnaire(#"{"questions":[{"header":"범위","question":"어디까지 할까요?","multiSelect":false,"options":[{"label":"A","description":""},{"label":"B","description":"b"}]},{"header":"대상","question":"무엇을 포함할까요?","multiSelect":true,"options":[{"label":"X","description":""},{"label":"Y","description":""},{"label":"Z","description":""}]}]}"#)
        var progress = QuestionnaireProgress(requestKey: "k")
        #expect(progress.current(in: form)?.header == "범위")
        #expect(progress.commit(customText: "   ", in: form) == nil)                  // nothing to answer with
        #expect(progress.choose("missing", in: form) == nil && progress.index == 0)
        #expect(progress.choose("B", in: form) == .next && progress.index == 1)
        // Multi-select chips toggle; Enter (or 선택 완료) commits them with optional text, in option order.
        #expect(progress.choose("Z", in: form) == nil && progress.choose("X", in: form) == nil && progress.choose("Y", in: form) == nil)
        #expect(progress.choose("Y", in: form) == nil && progress.selected == ["Z", "X"])
        guard case .complete(let answers)? = progress.commit(customText: "그리고 W", in: form) else { Issue.record("expected completion"); return }
        #expect(answers["어디까지 할까요?"] == UserQuestionAnswer(selectedOptions: ["B"]))
        #expect(answers["무엇을 포함할까요?"] == UserQuestionAnswer(selectedOptions: ["X", "Z"], customText: "그리고 W"))
        #expect(try form.validatedAnswers(answers) == ["어디까지 할까요?": "B", "무엇을 포함할까요?": "X, Z, 그리고 W"])
        // Free text alone answers a single-select question; back returns to it.
        var typed = QuestionnaireProgress(requestKey: "k2")
        #expect(typed.commit(customText: "직접 입력", in: form) == .next)
        typed.back(in: form)
        #expect(typed.index == 0 && typed.selected.isEmpty)
        // Going back to a multi-select question shows its earlier picks again.
        var revisit = QuestionnaireProgress(requestKey: "k3")
        let multiFirst = try questionnaire(#"{"questions":[{"header":"대상","question":"무엇을?","multiSelect":true,"options":[{"label":"X","description":""},{"label":"Y","description":""}]},{"header":"범위","question":"어디까지?","multiSelect":false,"options":[{"label":"A","description":""},{"label":"B","description":""}]}]}"#)
        _ = revisit.choose("Y", in: multiFirst)
        #expect(revisit.commit(customText: "", in: multiFirst) == .next)
        revisit.back(in: multiFirst)
        #expect(revisit.index == 0 && revisit.selected == ["Y"])
        #expect(typed.choose("A", in: form) == .next && typed.answers["어디까지 할까요?"] == UserQuestionAnswer(selectedOptions: ["A"]))
        // The pet highlights the recorded pick after 이전; answers given further on survive re-answering an earlier question.
        var pet = QuestionnaireProgress(requestKey: "k4")
        let singles = try questionnaire(#"{"questions":[{"header":"하나","question":"첫째?","multiSelect":false,"options":[{"label":"A","description":""},{"label":"B","description":""}]},{"header":"둘","question":"둘째?","multiSelect":false,"options":[{"label":"C","description":""},{"label":"D","description":""}]}]}"#)
        #expect(pet.choose("A", in: singles) == .next)
        pet.back(in: singles)
        #expect(pet.index == 0 && pet.answers["첫째?"]?.selectedOptions == ["A"])
        pet.back(in: singles)                                                           // already at the first question
        #expect(pet.index == 0)
        #expect(pet.choose("B", in: singles) == .next)
        guard case .complete(let revised)? = pet.choose("D", in: singles) else { Issue.record("expected completion"); return }
        #expect(try singles.validatedAnswers(revised) == ["첫째?": "B", "둘째?": "D"])
        // A failed send leaves the last multi-choice question as it was, ready to send again.
        var retry = QuestionnaireProgress(requestKey: "k5")
        _ = retry.choose("A", in: form); _ = retry.choose("X", in: form)
        guard case .complete(let first)? = retry.commit(customText: "", in: form) else { Issue.record("expected completion"); return }
        #expect(retry.index == 1 && retry.selected == ["X"])
        guard case .complete(let second)? = retry.commit(customText: "", in: form) else { Issue.record("expected a second completion"); return }
        #expect(first == second)
    }
}
