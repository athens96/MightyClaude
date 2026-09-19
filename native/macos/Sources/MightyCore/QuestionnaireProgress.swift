import Foundation

/// Answering an AskUserQuestion one question at a time from the composer:
/// option chips and free text fill `answers` until every question has one.
/// The question flow is not a style rule, so it only moved file (§5.8).
public struct QuestionnaireProgress: Sendable, Equatable {
    public enum Step: Sendable, Equatable { case next, complete([String: UserQuestionAnswer]) }
    public let requestKey: String
    public private(set) var index = 0
    public private(set) var answers: [String: UserQuestionAnswer] = [:]
    /// Picks made so far for the current multi-select question.
    public private(set) var selected: [String] = []
    public init(requestKey: String) { self.requestKey = requestKey }

    public func current(in questionnaire: UserQuestionnaire) -> UserQuestionnaire.Question? {
        questionnaire.questions.indices.contains(index) ? questionnaire.questions[index] : nil
    }

    /// A single-select chip answers the question; a multi-select chip toggles.
    public mutating func choose(_ label: String, in questionnaire: UserQuestionnaire) -> Step? {
        guard let question = current(in: questionnaire), question.options.contains(where: { $0.label == label }) else { return nil }
        if question.multiSelect {
            if let position = selected.firstIndex(of: label) { selected.remove(at: position) } else { selected.append(label) }
            return nil
        }
        return record(UserQuestionAnswer(selectedOptions: [label]), for: question, in: questionnaire)
    }

    /// Enter in the composer: the typed text (and any toggled chips) answer the question.
    public mutating func commit(customText: String, in questionnaire: UserQuestionnaire) -> Step? {
        guard let question = current(in: questionnaire) else { return nil }
        let text = customText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty || !selected.isEmpty else { return nil }
        let picks = question.multiSelect ? question.options.map(\.label).filter(selected.contains) : []
        return record(UserQuestionAnswer(selectedOptions: picks, customText: text.isEmpty ? nil : text), for: question, in: questionnaire)
    }

    /// Returns to the previous question with its earlier picks shown again.
    public mutating func back(in questionnaire: UserQuestionnaire) {
        guard index > 0 else { return }
        index -= 1
        let previous = questionnaire.questions[index]
        selected = previous.multiSelect ? (answers[previous.question]?.selectedOptions ?? []) : []
    }

    private mutating func record(_ answer: UserQuestionAnswer, for question: UserQuestionnaire.Question, in questionnaire: UserQuestionnaire) -> Step {
        answers[question.question] = answer
        // The last question stays on screen until the send succeeds, so its picks stay too.
        guard index + 1 < questionnaire.questions.count else { return .complete(answers) }
        selected = []
        index += 1
        return .next
    }
}
