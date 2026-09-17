import Testing
import Foundation
@testable import MightyCore

struct UserQuestionnaireTests {
    private var question: [String: Any] { ["header": "대상 앱", "question": "어느 앱인가요?", "multiSelect": false, "options": [["label": "Swift", "description": "macOS 네이티브"], ["label": "WinUI", "description": "Windows 네이티브"]]] }
    private func string(_ value: Any) throws -> String { String(decoding: try JSONSerialization.data(withJSONObject: value), as: UTF8.self) }
    private func envelope(_ id: String, tool: String = "AskUserQuestion", input: [String: Any], details: String = "") throws -> Data {
        try JSONSerialization.data(withJSONObject: ["type": "control_request", "request_id": id, "request": ["subtype": "can_use_tool", "tool_name": tool, "tool_use_id": "tool-" + id, "input": input, "description": details, "requires_user_interaction": true]])
    }

    @Test func parserRequiresACompleteBoundedQuestionnaire() throws {
        let input = try string(["questions": [question]])
        let form = try #require(UserQuestionnaire.parse(inputJSON: input))
        #expect(form.questions.first?.header == "대상 앱")
        #expect(try JSONDecoder().decode(UserQuestionnaire.self, from: JSONEncoder().encode(form)) == form)
        #expect(UserQuestionnaire.parse(inputJSON: "prefix " + input) == nil)
        #expect(UserQuestionnaire.parse(inputJSON: try string(["questions": [question, question]])) == nil)
        #expect(UserQuestionnaire.parse(inputJSON: try string(["questions": []])) == nil)
        for field in ["header", "question", "multiSelect", "options"] {
            var invalid = question; invalid.removeValue(forKey: field)
            #expect(UserQuestionnaire.parse(inputJSON: try string(["questions": [question, invalid]])) == nil)
        }
        var invalid = question; invalid["multiSelect"] = 1
        #expect(UserQuestionnaire.parse(inputJSON: try string(["questions": [invalid]])) == nil)
        invalid = question; invalid["options"] = [["label": "Swift", "description": "1"], ["label": "Swift", "description": "2"]]
        #expect(UserQuestionnaire.parse(inputJSON: try string(["questions": [invalid]])) == nil)
        invalid = question; invalid["question"] = String(repeating: "x", count: 8_193)
        #expect(UserQuestionnaire.parse(inputJSON: try string(["questions": [invalid]])) == nil)
        #expect(UserQuestionnaire.parse(inputJSON: input + String(repeating: " ", count: 65_536)) == nil)
    }

    @Test func validatesSingleMultipleCustomAndExactQuestionKeys() throws {
        var multi = question; multi["multiSelect"] = true; multi["question"] = "추가 앱은?"
        let form = try #require(UserQuestionnaire.parse(inputJSON: try string(["questions": [question, multi]])))
        let valid = ["어느 앱인가요?": UserQuestionAnswer(selectedOptions: ["Swift"]), "추가 앱은?": UserQuestionAnswer(selectedOptions: ["WinUI", "Swift"], customText: " Linux ")]
        #expect(try form.validatedAnswers(valid) == ["어느 앱인가요?": "Swift", "추가 앱은?": "Swift, WinUI, Linux"])
        for invalid in [UserQuestionAnswer(), UserQuestionAnswer(selectedOptions: ["Swift", "WinUI"]), UserQuestionAnswer(selectedOptions: ["Swift", "Swift"]), UserQuestionAnswer(selectedOptions: ["unknown"]), UserQuestionAnswer(selectedOptions: ["Swift"], customText: "other"), UserQuestionAnswer(customText: String(repeating: "x", count: 8_193))] {
            var answers = valid; answers["어느 앱인가요?"] = invalid
            #expect(throws: MightyError.self) { try form.validatedAnswers(answers) }
        }
        #expect(throws: MightyError.self) { try form.validatedAnswers([:]) }
        var answers = valid; answers["unknown key"] = .init(customText: "no")
        #expect(throws: MightyError.self) { try form.validatedAnswers(answers) }
        answers = valid; answers["어느 앱인가요?"] = .init(customText: "직접 답변")
        #expect(try form.validatedAnswers(answers)["어느 앱인가요?"] == "직접 답변")
    }

    @Test func answersUseOriginalInputAndAreOneShotWithoutPermissionChanges() throws {
        var writes: [Data] = []; var displays: [ToolPermissionRequest] = []
        let channel = ClaudePermissionChannel(runId: "run", prompt: Data(), write: { writes.append($0) }, emit: { displays.append($0) }, activity: { _, _ in }, warning: { _ in }, fail: { _ in })
        let input: [String: Any] = ["questions": [question], "extra": ["keep": true], "answers": ["old": "model text"], "response": "model supplied response"]
        let request = try envelope("q", input: input)
        channel.receive(request); channel.receive(request)
        #expect(displays.count == 1); #expect(displays[0].canAnswerQuestions); #expect(!displays[0].canAllow)
        #expect(throws: MightyError.self) { try channel.respond(requestId: "q", allow: true) }
        #expect(throws: MightyError.self) { try channel.answerQuestions(requestId: "q", answers: [:]) }
        #expect(writes.isEmpty)
        let answers = ["어느 앱인가요?": UserQuestionAnswer(selectedOptions: ["Swift"])]
        try channel.answerQuestions(requestId: "q", answers: answers)
        let firstWrite = try #require(writes.first)
        let object = try #require(JSONSerialization.jsonObject(with: firstWrite) as? [String: Any])
        let response = try #require((object["response"] as? [String: Any])?["response"] as? [String: Any])
        let updated = try #require(response["updatedInput"] as? [String: Any])
        #expect(response["behavior"] as? String == "allow"); #expect(response["toolUseID"] as? String == "tool-q")
        #expect(response["updatedPermissions"] == nil)
        #expect((updated["questions"] as? NSArray)?.isEqual(to: [question]) == true)
        #expect((updated["extra"] as? [String: Bool]) == ["keep": true])
        #expect(updated["answers"] as? [String: String] == ["어느 앱인가요?": "Swift"])
        #expect(updated["response"] == nil)
        #expect(displays.map(\.state) == ["pending", "answered"])
        #expect(throws: MightyError.self) { try channel.answerQuestions(requestId: "q", answers: answers) }
        channel.receive(request); #expect(writes.count == 1)
    }

    @Test func unsupportedIncompleteCancelledAndClosedRequestsCannotBeAnswered() throws {
        var writes: [Data] = []; var displays: [ToolPermissionRequest] = []
        let channel = ClaudePermissionChannel(runId: "run", prompt: Data(), write: { writes.append($0) }, emit: { displays.append($0) }, activity: { _, _ in }, warning: { _ in }, fail: { _ in })
        let input: [String: Any] = ["questions": [question]]
        let answers = ["어느 앱인가요?": UserQuestionAnswer(selectedOptions: ["Swift"])]
        channel.receive(try envelope("other", tool: "OtherTool", input: input))
        channel.receive(try envelope("malformed", input: ["questions": [question, question]]))
        channel.receive(try envelope("metadata", input: input, details: String(repeating: "x", count: 8_193)))
        for id in ["other", "malformed", "metadata"] {
            #expect(throws: MightyError.self) { try channel.answerQuestions(requestId: id, answers: answers) }
        }
        #expect(displays.allSatisfy { !$0.canAnswerQuestions && !$0.canAllow })
        channel.receive(try envelope("cancelled", input: input))
        channel.receive(try JSONSerialization.data(withJSONObject: ["type": "control_cancel_request", "request_id": "cancelled"]))
        #expect(throws: MightyError.self) { try channel.answerQuestions(requestId: "cancelled", answers: answers) }
        channel.receive(try envelope("closed", input: input)); channel.cancelAll()
        #expect(throws: MightyError.self) { try channel.answerQuestions(requestId: "closed", answers: answers) }
        #expect(writes.isEmpty)
    }

    @Test func olderPermissionEventsDoNotAcquireAnswerCapability() throws {
        let old: [String: Any] = ["id": "q", "runId": "run", "toolUseId": "tool", "toolName": "AskUserQuestion", "inputJSON": try string(["questions": [question]]), "summary": "question", "state": "pending", "canAllow": false]
        let decoded = try JSONDecoder().decode(ToolPermissionRequest.self, from: JSONSerialization.data(withJSONObject: old))
        #expect(decoded.questionnaire != nil); #expect(!decoded.canAnswerQuestions)
    }
}
