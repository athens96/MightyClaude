import Foundation
import CoreFoundation

/// A question an agent other than Claude asks, in the shape the question card already reads
/// (Claude's AskUserQuestion input): opencode's and Hermes's ACP `elicitation/create` form,
/// Hermes's clarify approval (one `allow_once` option per choice) and a pi extension's dialog
/// (`select`, `confirm`, `input`). The card, the companion and the phone read the request as
/// they read Claude's; only how the answer travels back is each channel's own.
enum AgentQuestions {
    static let toolName = "AskUserQuestion"

    struct Item {
        var header: String
        var question: String
        var multiSelect = false
        var options: [(label: String, description: String)] = []
    }

    /// Text as the card shows it: no control characters, bounded, never blank.
    static func text(_ value: String?, limit: Int, singleLine: Bool = false) -> String? {
        let cleaned = ActivitySupport.clean(value ?? "", maximumBytes: limit, singleLine: singleLine)
        return cleaned.isEmpty ? nil : cleaned
    }

    /// The card's request, or nil when the card cannot show these questions whole (too many
    /// questions or options, a repeated question or label): the caller keeps its old answer.
    static func request(id: String, runId: String, toolUseId: String, items: [Item]) -> ToolPermissionRequest? {
        let object: [String: Any] = ["questions": items.map { item in
            ["header": item.header, "question": item.question, "multiSelect": item.multiSelect,
             "options": item.options.map { ["label": $0.label, "description": $0.description] }] as [String: Any]
        }]
        guard let data = try? JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]) else { return nil }
        let json = String(decoding: data, as: UTF8.self)
        guard UserQuestionnaire.parse(inputJSON: json) != nil, let first = items.first else { return nil }
        return ToolPermissionRequest(id: id, runId: runId, toolUseId: toolUseId, toolName: toolName, inputJSON: json,
                                     summary: ActivitySupport.clean(first.question, maximumBytes: 1_000, singleLine: true), canAllow: false, canAnswerQuestions: true)
    }

    /// The answers checked against the card's own questions: what was picked, in the offered
    /// order, and what was typed (trimmed, empty when nothing was).
    static func answers(_ request: ToolPermissionRequest, _ answers: [String: UserQuestionAnswer]) throws -> [String: (picked: [String], typed: String)] {
        guard request.canAnswerQuestions, let questionnaire = request.questionnaire else { throw MightyError(L("permission.error.noAnswers")) }
        _ = try questionnaire.validatedAnswers(answers)
        var result: [String: (picked: [String], typed: String)] = [:]
        for question in questionnaire.questions {
            let answer = answers[question.question] ?? UserQuestionAnswer()
            let picked = question.options.map(\.label).filter(Set(answer.selectedOptions).contains)
            result[question.question] = (picked, answer.customText?.trimmingCharacters(in: .whitespacesAndNewlines) ?? "")
        }
        return result
    }
}

/// An ACP `elicitation/create` form (https://agentclientprotocol.com, the MCP elicitation
/// schema) as questions. Each property is one question: a string with `oneOf` / `enum` is a
/// single choice, an array of those a multiple choice, a plain string a typed answer, a
/// boolean a yes or no. opencode adds `<key>_custom` (a plain string) beside a choice whose
/// answer may be typed: that is the card's own "write your answer" row, not a question.
/// Numbers and nested objects are not questions the card asks; such a form is declined.
struct ACPElicitationForm {
    enum Kind { case single, multiple, text, boolean }
    struct Field {
        let key: String
        let question: String
        let kind: Kind
        /// The value each shown label stands for.
        let values: [String: Any]
        /// Where a typed answer goes: its own property, or (when the agent takes any string for
        /// a choice) the field itself.
        let customKey: String?
    }
    let fields: [Field]
    let items: [AgentQuestions.Item]

    /// `freeChoices`: the agent reads a typed answer in place of a choice (Hermes's clarify
    /// does; opencode does only where it sent the `_custom` property).
    init?(params: [String: Any], freeChoices: Bool) {
        guard (params["mode"] as? String ?? "form") == "form", let schema = params["requestedSchema"] as? [String: Any],
              (schema["type"] as? String ?? "object") == "object", let properties = schema["properties"] as? [String: Any], !properties.isEmpty else { return nil }
        let message = AgentQuestions.text(params["message"] as? String, limit: 8_192)
        // A JSON object has no order here; `q0, q1, … q10` sort as numbers do.
        let keys = properties.keys.sorted { $0.compare($1, options: .numeric) == .orderedAscending }
        func plainString(_ property: Any?) -> Bool {
            guard let property = property as? [String: Any], property["type"] as? String == "string" else { return false }
            return property["enum"] == nil && property["oneOf"] == nil && property["anyOf"] == nil
        }
        let customKeys = Set(keys.filter { key in
            guard key.hasSuffix("_custom"), plainString(properties[key]), let base = properties[String(key.dropLast(7))] as? [String: Any] else { return false }
            return Self.choices(base) != nil || Self.choices(base["items"] as? [String: Any] ?? [:]) != nil
        })
        var fields: [Field] = [], items: [AgentQuestions.Item] = []
        for key in keys where !customKeys.contains(key) {
            guard key.utf8.count <= 256, let property = properties[key] as? [String: Any] else { return nil }
            let title = AgentQuestions.text(property["title"] as? String, limit: 256, singleLine: true)
            let description = AgentQuestions.text(property["description"] as? String, limit: 8_192)
            // opencode: the title is the question's short header and the description its text.
            // Hermes: the property says nothing and the form's message is the question.
            guard let question = description ?? (properties.count - customKeys.count == 1 ? message : nil) ?? title else { return nil }
            let header = (description != nil ? title : nil) ?? L("questionnaire.header.agent")
            var kind = Kind.text, options: [(label: String, description: String, value: Any)] = []
            switch property["type"] as? String {
            case "string":
                if let choices = Self.choices(property) { kind = .single; options = choices }
            case "array":
                guard let choices = Self.choices(property["items"] as? [String: Any] ?? [:]) else { return nil }
                kind = .multiple; options = choices
            case "boolean":
                kind = .boolean
                options = [(L("questionnaire.option.yes"), "", true), (L("questionnaire.option.no"), "", false)]
            default: return nil
            }
            guard Set(options.map(\.label)).count == options.count else { return nil }
            let custom = customKeys.contains(key + "_custom") ? key + "_custom" : (kind == .text || (freeChoices && kind != .boolean)) ? key : nil
            fields.append(Field(key: key, question: question, kind: kind, values: Dictionary(uniqueKeysWithValues: options.map { ($0.label, $0.value) }), customKey: custom))
            items.append(AgentQuestions.Item(header: header, question: question, multiSelect: kind == .multiple, options: options.map { ($0.label, $0.description) }))
        }
        guard !fields.isEmpty else { return nil }
        self.fields = fields; self.items = items
    }

    /// The choices of a `oneOf` / `anyOf` list of `{const, title, description}` or of an `enum`
    /// (with `enumNames`); nil when the schema offers none.
    private static func choices(_ schema: [String: Any]) -> [(label: String, description: String, value: Any)]? {
        func usable(_ value: Any) -> Bool { value is String || (value is NSNumber && CFGetTypeID(value as CFTypeRef) != CFBooleanGetTypeID()) }
        if let list = (schema["oneOf"] ?? schema["anyOf"]) as? [[String: Any]], !list.isEmpty {
            var result: [(label: String, description: String, value: Any)] = []
            for entry in list {
                guard let value = entry["const"], usable(value),
                      let label = AgentQuestions.text(entry["title"] as? String ?? "\(value)", limit: 1_024, singleLine: true) else { return nil }
                result.append((label, AgentQuestions.text(entry["description"] as? String, limit: 8_192) ?? "", value))
            }
            return result
        }
        if let list = schema["enum"] as? [Any], !list.isEmpty {
            let names = schema["enumNames"] as? [String] ?? []
            var result: [(label: String, description: String, value: Any)] = []
            for (index, value) in list.enumerated() {
                guard usable(value), let label = AgentQuestions.text(names.indices.contains(index) ? names[index] : "\(value)", limit: 1_024, singleLine: true) else { return nil }
                result.append((label, "", value))
            }
            return result
        }
        return nil
    }

    /// The `content` of an `accept` answer. A typed answer where the agent takes only its
    /// choices is refused, and the card stays answerable.
    func content(_ answers: [String: (picked: [String], typed: String)]) throws -> [String: Any] {
        var content: [String: Any] = [:]
        for field in fields {
            guard let answer = answers[field.question] else { throw MightyError(L("questionnaire.error.missingAnswer")) }
            let picked = answer.picked.compactMap { field.values[$0] }
            if !answer.typed.isEmpty {
                guard let customKey = field.customKey else { throw MightyError(L("questionnaire.error.choicesOnly")) }
                if customKey != field.key {
                    content[customKey] = answer.typed
                    if field.kind == .multiple, !picked.isEmpty { content[field.key] = picked }
                    continue
                }
                content[field.key] = field.kind == .multiple ? picked + [answer.typed] : answer.typed
                continue
            }
            guard !picked.isEmpty else { throw MightyError(L("questionnaire.error.missingAnswer")) }
            content[field.key] = field.kind == .multiple ? picked : picked[0]
        }
        return content
    }
}

/// The dialogs a pi extension opens in RPC mode (`extension_ui_request`, pi's
/// docs/rpc-extension-ui.md), owned by ProcessRunner's actor: `select`, `confirm` and `input`
/// become the question card, and the answer leaves as an `extension_ui_response`. The app's
/// own `ask_user` tool (Resources/Agents/pi-ask-user.js) asks through `select` and `input`,
/// and takes a typed answer, or several picks, in place of one choice; any other extension's
/// `select` is answered with one of its options only. An `editor` dialog (a text to edit) is
/// not a question: the parser cancels it as before.
final class PiDialogChannel {
    static let maximumPending = 16
    /// The `ask_user` call a dialog belongs to, when its title is that call's question.
    struct Asking { let toolCallId: String; let multiple: Bool }
    private struct Pending { let dialogId: String; let method: String; let asking: Asking?; var display: ToolPermissionRequest }
    private let runId: String
    private let write: (Data) -> Void
    private let emit: (ToolPermissionRequest) -> Void
    private let activity: (ToolPermissionRequest, String) -> Void
    private let warning: (String) -> Void
    /// Asks to be told (`timedOut`) once a dialog's own deadline has passed: pi answers it itself then.
    private let timer: (String, Double) -> Void
    private var pending: [String: Pending] = [:]
    private var closed = false

    init(runId: String, write: @escaping (Data) -> Void, emit: @escaping (ToolPermissionRequest) -> Void, activity: @escaping (ToolPermissionRequest, String) -> Void,
         warning: @escaping (String) -> Void, timer: @escaping (String, Double) -> Void = { _, _ in }) {
        self.runId = runId; self.write = write; self.emit = emit; self.activity = activity; self.warning = warning; self.timer = timer
    }

    /// Shows one dialog as the card; false when the card cannot ask it (the caller cancels it).
    func receive(_ value: [String: Any], asking: Asking?) -> Bool {
        guard !closed, pending.count < Self.maximumPending, let dialogId = value["id"] as? String, !dialogId.isEmpty, dialogId.utf8.count <= 256,
              let method = value["method"] as? String, let title = AgentQuestions.text(value["title"] as? String, limit: 8_192) else { return false }
        var item = AgentQuestions.Item(header: L("questionnaire.header.agent"), question: title)
        switch method {
        case "select":
            let options = (value["options"] as? [Any] ?? []).map { AgentQuestions.text($0 as? String, limit: 1_024, singleLine: true) }
            // The option itself is the answer pi reads: one the card would have to shorten is not asked.
            guard !options.isEmpty, options.elementsEqual(value["options"] as? [String] ?? [], by: { $0 == $1 }) else { return false }
            item.options = options.compactMap { $0 }.map { ($0, "") }
            item.multiSelect = asking?.multiple == true
        case "confirm":
            if let message = AgentQuestions.text(value["message"] as? String, limit: 8_192) {
                item.header = ActivitySupport.clean(title, maximumBytes: 256, singleLine: true); item.question = message
            }
            item.options = [(L("questionnaire.option.yes"), ""), (L("questionnaire.option.no"), "")]
        case "input": break
        default: return false
        }
        let id = UUID().uuidString
        guard let display = AgentQuestions.request(id: id, runId: runId, toolUseId: asking?.toolCallId ?? "pi-dialog-\(dialogId)", items: [item]) else { return false }
        pending[id] = Pending(dialogId: dialogId, method: method, asking: asking, display: display)
        if asking != nil { activity(display, "waiting") }
        emit(display)
        if let milliseconds = (value["timeout"] as? NSNumber)?.doubleValue, milliseconds > 0, milliseconds.isFinite { timer(id, milliseconds / 1_000) }
        return true
    }

    func answerQuestions(requestId: String, answers: [String: UserQuestionAnswer]) throws {
        guard !closed, let ask = pending[requestId] else { throw MightyError(L("permission.error.questionGone")) }
        guard let answer = try AgentQuestions.answers(ask.display, answers).values.first else { throw MightyError(L("questionnaire.error.missingAnswer")) }
        var response: [String: Any] = ["type": "extension_ui_response", "id": ask.dialogId]
        switch ask.method {
        case "confirm":
            guard answer.typed.isEmpty, let picked = answer.picked.first else { throw MightyError(L("questionnaire.error.choicesOnly")) }
            response["confirmed"] = picked == L("questionnaire.option.yes")
        case "select":
            // Only the app's own tool reads anything but one of its options.
            guard ask.asking != nil || (answer.typed.isEmpty && answer.picked.count == 1) else { throw MightyError(L("questionnaire.error.choicesOnly")) }
            response["value"] = (answer.picked + (answer.typed.isEmpty ? [] : [answer.typed])).joined(separator: ", ")
        default:
            guard !answer.typed.isEmpty else { throw MightyError(L("questionnaire.error.missingAnswer")) }
            response["value"] = answer.typed
        }
        pending.removeValue(forKey: requestId)
        send(response)
        var display = ask.display; display.state = "answered"
        if ask.asking != nil { activity(display, "running") }
        emit(display)
    }

    /// The card's Cancel: pi reads the dialog as dismissed.
    func respond(requestId: String, allow: Bool) throws {
        guard !closed, let ask = pending[requestId] else { throw MightyError(L("permission.error.requestGone")) }
        guard !allow else { throw MightyError(L("permission.error.noAnswers")) }
        pending.removeValue(forKey: requestId)
        send(["type": "extension_ui_response", "id": ask.dialogId, "cancelled": true])
        var display = ask.display; display.state = "denied"
        if ask.asking != nil { activity(display, "running") }
        emit(display)
    }

    /// pi answered the dialog itself when its deadline passed.
    func timedOut(requestId: String) {
        guard let ask = pending[requestId] else { return }
        withdraw(requestId)
        warning(L("run.notice.piDialogTimedOut", ["title": ActivitySupport.clean(ask.display.summary, maximumBytes: 256, singleLine: true)]))
    }
    /// The tool that asked has ended (pi aborted it): its card has nothing left to answer.
    func toolEnded(_ toolCallId: String) {
        for id in pending.filter({ $0.value.asking?.toolCallId == toolCallId }).map(\.key) { withdraw(id) }
    }
    func cancelAll() {
        guard !closed else { return }; closed = true
        for id in Array(pending.keys) { withdraw(id) }
    }
    private func withdraw(_ id: String) {
        guard var display = pending.removeValue(forKey: id)?.display else { return }
        display.state = "cancelled"; emit(display)
    }
    private func send(_ object: [String: Any]) {
        guard let data = try? JSONSerialization.data(withJSONObject: object, options: [.sortedKeys, .withoutEscapingSlashes]) else { return }
        write(data + Data([10]))
    }
}
