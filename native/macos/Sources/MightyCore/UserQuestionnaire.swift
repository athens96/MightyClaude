import Foundation

/// The bounded, complete AskUserQuestion input. Question strings are protocol
/// keys, so neither parsing nor rendering may shorten or normalize them.
public struct UserQuestionnaire: Codable, Sendable, Equatable {
    public struct Option: Codable, Sendable, Equatable {
        public let label: String
        public let description: String
    }
    public struct Question: Codable, Sendable, Equatable {
        public let header: String
        public let question: String
        public let multiSelect: Bool
        public let options: [Option]
    }
    public let questions: [Question]
    private enum CodingKeys: String, CodingKey { case questions }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        questions = try container.decode([Question].self, forKey: .questions)
        guard (1...4).contains(questions.count), Set(questions.map(\.question)).count == questions.count,
              questions.allSatisfy({ question in
                  Self.textIsValid(question.header, limit: 256) && Self.textIsValid(question.question, limit: 8_192)
                  && (2...4).contains(question.options.count)
                  && Set(question.options.map(\.label)).count == question.options.count
                  && question.options.allSatisfy { Self.textIsValid($0.label, limit: 1_024) && Self.textIsValid($0.description, limit: 8_192, allowEmpty: true) }
              }) else { throw MightyError("선택 요청의 질문 또는 선택지 형식이 올바르지 않습니다.") }
    }

    public static func parse(inputJSON: String) -> UserQuestionnaire? {
        guard inputJSON.utf8.count <= 65_536 else { return nil }
        return try? JSONDecoder().decode(Self.self, from: Data(inputJSON.utf8))
    }

    /// Returns the SDK's question-text -> answer-string shape. Multiple picks
    /// follow the SDK's comma-space convention, in the original option order.
    public func validatedAnswers(_ answers: [String: UserQuestionAnswer]) throws -> [String: String] {
        guard Set(answers.keys) == Set(questions.map(\.question)) else { throw MightyError("모든 질문에 답변해 주세요.") }
        var result: [String: String] = [:]
        for question in questions {
            guard let answer = answers[question.question] else { throw MightyError("답변이 누락되었습니다.") }
            let selections = Set(answer.selectedOptions)
            let custom = answer.customText?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            guard selections.count == answer.selectedOptions.count,
                  selections.isSubset(of: Set(question.options.map(\.label))),
                  Self.textIsValid(custom, limit: 8_192, allowEmpty: true),
                  !selections.isEmpty || !custom.isEmpty,
                  question.multiSelect || selections.count + (custom.isEmpty ? 0 : 1) == 1 else {
                throw MightyError("질문의 선택 방식에 맞게 답변해 주세요.")
            }
            var values = question.options.filter { selections.contains($0.label) }.map(\.label)
            if !custom.isEmpty { values.append(custom) }
            result[question.question] = values.joined(separator: ", ")
        }
        return result
    }

    private static func textIsValid(_ value: String, limit: Int, allowEmpty: Bool = false) -> Bool {
        value.utf8.count <= limit && (allowEmpty || !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            && !value.unicodeScalars.contains { $0.properties.generalCategory == .control && ![9, 10, 13].contains($0.value) }
    }
}

public struct UserQuestionAnswer: Codable, Sendable, Equatable {
    public var selectedOptions: [String]
    public var customText: String?
    public init(selectedOptions: [String] = [], customText: String? = nil) {
        self.selectedOptions = selectedOptions; self.customText = customText
    }
}
