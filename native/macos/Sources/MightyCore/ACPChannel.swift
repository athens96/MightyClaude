import Foundation
import CoreFoundation

/// One Agent Client Protocol run (https://agentclientprotocol.com), owned by ProcessRunner's
/// actor: JSON-RPC 2.0 over the agent's stdio, one JSON object per line. The app is the
/// client: `initialize`, then `session/new` (or `session/resume` / `session/load` of the
/// pane's session), the pane's settings as `session/set_config_option`, then one
/// `session/prompt`. What the agent streams (`session/update`) reaches the parser as
/// `acp.*` events (`CLIStreamParser.acp`); a `session/request_permission` becomes the
/// pane's approval card, and only the user's single-call answer leaves the channel. A question
/// (an `elicitation/create` form: opencode's `question` tool, Hermes's clarify; or Hermes's
/// clarify as an approval with one `allow_once` option per choice, for a Hermes that was not
/// told about forms) becomes the pane's question card (`AgentQuestions`).
/// Nothing here is one agent's own: opencode and Hermes use the same channel, and differ only
/// in their argv, how the permission mode reaches them, and their settings' ids.
final class ACPChannel {
    static let maximumFrameBytes = 32 * 1024 * 1024
    static let maximumInputBytes = 65_536
    static let maximumPending = 16
    /// What the client offers: no file system and no terminal, so the agent uses its own tools,
    /// and form elicitation, so its questions arrive as `elicitation/create`.
    static let clientCapabilities: [String: Any] = ["fs": ["readTextFile": false, "writeTextFile": false], "terminal": false, "elicitation": ["form": [String: Any]()]]

    /// How a question's answer travels: an elicitation's `content`, or the option of the picked choice.
    private enum Question { case form(ACPElicitationForm), choices([String: String]) }
    private struct Pending { let rpcID: Any; let allowOption: String?; let rejectOption: String?; var display: ToolPermissionRequest; var question: Question? = nil
                             /// False for a form without a tool call of its own (Hermes's): no activity row to mark.
                             var row = true }
    private let runId: String
    private let request: StartRunRequest
    private let workspacePath: String
    private let attachments: AttachmentPreparation
    /// `session/set_config_option` values sent before the prompt, in order (model, effort, mode).
    private let configValues: [(id: String, value: String)]
    /// Reads the agent's `initialize` answer before a session is opened: a message stops the run
    /// there as a missing sign-in (Hermes names no provider it could use).
    private let admit: (([String: Any]) -> String?)?
    /// The agent reads a typed answer in place of a form's choice (Hermes's clarify).
    private let freeChoices: Bool
    private let write: (Data) -> Void
    private let event: ([String: Any]) -> Void
    private let emit: (ToolPermissionRequest) -> Void
    private let activity: (ToolPermissionRequest, String) -> Void
    private let warning: (String) -> Void
    private let fail: (String) -> Void
    private let completed: () -> Void
    private var lines = LineSplitter(maximumLineBytes: ACPChannel.maximumFrameBytes)
    private var pending: [String: Pending] = [:]
    private var seen = Set<String>()
    private var rpc: [String: String] = [:]
    private var sequence = 0
    private var canResume = false
    private var canLoad = false
    /// `session/load` replays the conversation as updates before it answers; those are not this run's.
    private var replaying = false
    private var configOptions: [[String: Any]] = []
    private var queuedConfig: [(id: String, value: String)] = []
    private var closed = false
    private var started = false
    private(set) var sessionId: String?
    private(set) var initialized = false
    private(set) var ready = false
    private(set) var turnCompleted = false
    private(set) var failed = false
    /// The `session/cancel` notification of the turn in flight, readable off the runner's actor:
    /// an agent whose turn outlives its ACP process (Hermes's gateway owns it) is told to stop
    /// before the process is ended.
    let cancellation = ACPCancellation()

    init(runId: String, request: StartRunRequest, workspacePath: String, attachments: AttachmentPreparation, configValues: [(id: String, value: String)],
         freeChoices: Bool = false, admit: (([String: Any]) -> String?)? = nil, write: @escaping (Data) -> Void, event: @escaping ([String: Any]) -> Void,
         emit: @escaping (ToolPermissionRequest) -> Void, activity: @escaping (ToolPermissionRequest, String) -> Void,
         warning: @escaping (String) -> Void, fail: @escaping (String) -> Void, completed: @escaping () -> Void) {
        self.runId = runId; self.request = request; self.workspacePath = workspacePath; self.attachments = attachments; self.configValues = configValues; self.admit = admit; self.freeChoices = freeChoices
        self.write = write; self.event = event; self.emit = emit; self.activity = activity
        self.warning = warning; self.fail = fail; self.completed = completed
    }

    func start() {
        guard !started, !closed else { return }; started = true
        call("initialize", ["protocolVersion": 1, "clientCapabilities": Self.clientCapabilities,
                            "clientInfo": ["name": "mighty-code", "title": "Mighty Code", "version": "1.0"]])
    }
    func initializationTimedOut() {
        guard !closed, !ready else { return }
        failClosed(L("acp.channel.initTimeout"))
    }
    func receive(_ data: Data) {
        guard !closed else { return }
        for item in lines.push(data) {
            switch item {
            case .line(let frame): receiveFrame(AgentOutputLine(frame).object, raw: frame)
            case .tooLong: frameTooLarge()
            }
            if closed { return }
        }
    }
    /// A frame the runner already cut and parsed (`AgentOutputLines`).
    func receive(_ line: AgentOutputLine) { guard !closed else { return }; receiveFrame(line.object, raw: line.data) }
    func frameTooLarge() { failClosed(L("acp.channel.frameTooLarge")) }
    /// EOF before the prompt's answer is not success.
    func flush() {
        guard !closed else { return }
        if let frame = lines.finish() { receiveFrame(AgentOutputLine(frame).object, raw: frame) }
        if !closed { failClosed(L("acp.channel.closedEarly")) }
    }

    private func receiveFrame(_ object: Any?, raw: Data) {
        guard let obj = object as? [String: Any] else {
            // An agent may print a plain line (a warning) before or beside its frames: shown, not fatal.
            let text = String(decoding: raw.prefix(4_096), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
            if !text.isEmpty { event(["type": "acp.output", "text": text]) }
            return
        }
        if let method = obj["method"] as? String {
            let params = obj["params"] as? [String: Any] ?? [:]
            if let id = obj["id"], !(id is NSNull) { agentRequest(id, method, params) }
            else { notification(method, params) }
            return
        }
        guard let id = Self.idKey(obj["id"]), let method = rpc.removeValue(forKey: id) else { return }
        if let error = obj["error"] as? [String: Any] { failure(method, error); return }
        let result = obj["result"] as? [String: Any] ?? [:]
        switch method {
        case "initialize":
            initialized = true
            let capabilities = result["agentCapabilities"] as? [String: Any] ?? [:]
            canLoad = capabilities["loadSession"] as? Bool == true
            canResume = (capabilities["sessionCapabilities"] as? [String: Any])?["resume"] != nil
            if let refusal = admit?(result) {
                event(["type": "acp.error", "method": method, "code": -32000, "message": refusal, "signIn": true])
                failed = true; cancelAll(); completed(); return
            }
            openSession()
        case "session/new":
            guard let id = result["sessionId"] as? String, CoreValidation.sessionIdentifier(id) else { failClosed(L("acp.channel.badSession")); return }
            sessionOpened(id, result)
        case "session/resume", "session/load":
            replaying = false
            guard let id = request.resumeId else { failClosed(L("acp.channel.badSession")); return }
            sessionOpened(id, result)
        case "session/set_config_option":
            if let options = result["configOptions"] as? [[String: Any]] { configOptions = options }
            nextConfig()
        case "session/prompt":
            guard let sessionId else { return }
            var value: [String: Any] = ["type": "acp.prompt_result", "sessionId": sessionId, "stopReason": result["stopReason"] as? String ?? "end_turn"]
            if let usage = result["usage"] { value["usage"] = usage }
            event(value)
            turnCompleted = true; cancellation.set(nil); cancelAll(); completed()
        default: break
        }
    }

    private func openSession() {
        let params: [String: Any] = ["cwd": workspacePath, "mcpServers": [Any]()]
        if let resume = request.resumeId, canResume || canLoad {
            var params = params; params["sessionId"] = resume
            if canResume { call("session/resume", params) } else { replaying = true; call("session/load", params) }
        } else { call("session/new", params) }
    }
    private func sessionOpened(_ id: String, _ result: [String: Any]) {
        sessionId = id
        configOptions = result["configOptions"] as? [[String: Any]] ?? []
        var value: [String: Any] = ["type": "acp.session", "sessionId": id]
        if let options = result["configOptions"] { value["configOptions"] = options }
        event(value)
        queuedConfig = configValues
        nextConfig()
    }
    /// One setting at a time; one the agent does not offer is named and left out.
    private func nextConfig() {
        guard let sessionId else { return }
        while !queuedConfig.isEmpty {
            let next = queuedConfig.removeFirst()
            if let option = configOptions.first(where: { $0["id"] as? String == next.id }) {
                let values = Self.optionValues(option)
                guard values.isEmpty || values.contains(next.value) else {
                    warning(L("acp.channel.optionUnavailable", ["option": next.id, "value": ActivitySupport.clean(next.value, maximumBytes: 120, singleLine: true)])); continue
                }
                if option["currentValue"] as? String == next.value { continue }
            } else if !configOptions.isEmpty {
                warning(L("acp.channel.optionUnavailable", ["option": next.id, "value": ActivitySupport.clean(next.value, maximumBytes: 120, singleLine: true)])); continue
            }
            call("session/set_config_option", ["sessionId": sessionId, "configId": next.id, "value": next.value]); return
        }
        prompt(sessionId)
    }
    /// The values of a `select` option, flat or grouped.
    static func optionValues(_ option: [String: Any]) -> [String] {
        var values: [String] = []
        for entry in (option["options"] as? [[String: Any]] ?? []).prefix(2_048) {
            if let value = entry["value"] as? String { values.append(value) }
            for inner in (entry["options"] as? [[String: Any]] ?? []).prefix(2_048) { if let value = inner["value"] as? String { values.append(value) } }
        }
        return values
    }
    private func prompt(_ sessionId: String) {
        guard !ready else { return }
        ready = true
        let prompt = request.input.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? L("attachments.prompt.default") : request.input
        let references = attachments.files.filter { !$0.attachment.mediaType.hasPrefix("image/") }.map { AttachmentSupport.reference($0).replacingOccurrences(of: "\\/", with: "/") }
        var blocks: [[String: Any]] = [["type": "text", "text": ([prompt] + references).joined(separator: "\n\n")]]
        blocks += attachments.files.filter { $0.attachment.mediaType.hasPrefix("image/") }.map { ["type": "image", "data": $0.attachment.dataBase64, "mimeType": $0.attachment.mediaType] }
        event(["type": "acp.prompt", "sessionId": sessionId])
        cancellation.set(try? JSONSerialization.data(withJSONObject: ["jsonrpc": "2.0", "method": "session/cancel", "params": ["sessionId": sessionId]], options: [.sortedKeys, .withoutEscapingSlashes]))
        call("session/prompt", ["sessionId": sessionId, "prompt": blocks])
    }

    private func failure(_ method: String, _ error: [String: Any]) {
        let message = ActivitySupport.clean(error["message"] as? String ?? "", maximumBytes: 8_192)
        let code = (error["code"] as? NSNumber)?.intValue ?? 0
        switch method {
        case "session/resume", "session/load":
            // The session is gone (never answered, deleted): the pane starts a new one.
            replaying = false
            warning(L("acp.channel.newSession"))
            call("session/new", ["cwd": workspacePath, "mcpServers": [Any]()])
        case "session/set_config_option":
            warning(L("acp.channel.optionFailed", ["error": message.isEmpty ? "\(code)" : message]))
            nextConfig()
        default:
            // The parser shows the agent's words and reads a lost sign-in from them.
            var value: [String: Any] = ["type": "acp.error", "method": method, "code": code, "message": message]
            if let data = error["data"] { value["data"] = data }
            event(value)
            // The agent answered the prompt with an error: no turn is left to cancel.
            if method == "session/prompt" { cancellation.set(nil) }
            failed = true; cancelAll(); completed()
        }
    }

    private func notification(_ method: String, _ params: [String: Any]) {
        // The agent withdrew a request of its own (its turn was cancelled while it asked).
        if method == "$/cancel_request", let key = Self.idKey(params["requestId"]) {
            for id in pending.filter({ Self.idKey($0.value.rpcID) == key }).map(\.key) { cancelPending(id) }
            return
        }
        guard method == "session/update", !replaying, let sessionId, params["sessionId"] as? String == sessionId,
              let update = params["update"] as? [String: Any], let kind = update["sessionUpdate"] as? String else { return }
        // A call that settled no longer waits for an answer.
        if kind == "tool_call_update", let id = update["toolCallId"] as? String, ["completed", "failed"].contains(update["status"] as? String ?? "") {
            for key in pending.filter({ $0.value.display.toolUseId == id }).map(\.key) { cancelPending(key) }
        }
        event(["type": "acp.update", "sessionId": sessionId, "update": update])
    }

    private func agentRequest(_ rawID: Any, _ method: String, _ params: [String: Any]) {
        guard let key = Self.idKey(rawID) else { failClosed(L("acp.channel.badRequestId")); return }
        guard seen.insert(key).inserted else { failClosed(L("acp.channel.duplicateRequest")); return }
        guard seen.count <= 2_048 else { failClosed(L("acp.channel.tooManyRequests")); return }
        if method == "elicitation/create" { elicitation(rawID, key, params); return }
        guard method == "session/request_permission" else {
            // No file system or terminal was offered: anything else is not this client's.
            rpcError(rawID, -32601, "Method not supported by this client."); return
        }
        guard let sessionId, params["sessionId"] as? String == sessionId, let call = params["toolCall"] as? [String: Any] else {
            reply(rawID, ["outcome": ["outcome": "cancelled"]]); return
        }
        let options = (params["options"] as? [[String: Any]] ?? []).prefix(16)
        func option(_ kinds: [String]) -> String? {
            for kind in kinds { if let id = options.first(where: { $0["kind"] as? String == kind })?["optionId"] as? String, !id.isEmpty, id.utf8.count <= 256 { return id } }
            return nil
        }
        let allow = option(["allow_once"]), reject = option(["reject_once", "reject_always"])
        // Several "allow once" options are a question's choices (Hermes's clarify), not an approval:
        // they are the question card's choices. One the card cannot show is skipped and named
        // instead of answered by guess.
        if options.filter({ $0["kind"] as? String == "allow_once" }).count > 1 {
            if pending.count < Self.maximumPending, let ask = choiceQuestion(rawID, key, call, Array(options), reject: reject) {
                pending[ask.display.id] = ask
                activity(ask.display, "waiting"); emit(ask.display); return
            }
            answer(rawID, reject)
            let question = ActivitySupport.clean(call["title"] as? String ?? "", maximumBytes: 1_000, singleLine: true)
            let choices = options.filter { $0["kind"] as? String == "allow_once" }.compactMap { $0["name"] as? String }.prefix(8).map { ActivitySupport.clean($0, maximumBytes: 120, singleLine: true) }.joined(separator: " / ")
            warning(L("acp.channel.questionSkipped", ["question": question, "choices": choices])); return
        }
        guard pending.count < Self.maximumPending else { answer(rawID, reject); warning(L("acp.channel.tooManyPending")); return }
        let callID = (call["toolCallId"] as? String).flatMap { !$0.isEmpty && $0.utf8.count <= 256 && $0.unicodeScalars.allSatisfy { $0.value > 32 && $0.value != 127 } ? $0 : nil } ?? "permission-\(key)"
        let input = Self.displayInput(call["rawInput"])
        guard let display = Self.displayJSON(input), display.utf8.count <= Self.maximumInputBytes else {
            answer(rawID, reject); warning(L("acp.channel.requestTooLarge")); return
        }
        let title = ActivitySupport.clean(call["title"] as? String ?? "", maximumBytes: 1_000, singleLine: true)
        let value = ToolPermissionRequest(id: UUID().uuidString, runId: runId, toolUseId: callID, toolName: Self.toolName(kind: call["kind"] as? String, title: (call["name"] ?? call["title"]) as? String),
                                          inputJSON: display, summary: title.isEmpty ? L("acp.channel.permissionSummary") : title,
                                          reason: allow == nil ? L("acp.channel.denyOnly") : nil, canAllow: allow != nil)
        pending[value.id] = Pending(rpcID: rawID, allowOption: allow, rejectOption: reject, display: value)
        activity(value, "waiting"); emit(value)
    }
    /// Hermes's clarify as an approval: the tool call's title is the question and each
    /// `allow_once` option one choice. The answer is the picked option; nothing typed can travel.
    private func choiceQuestion(_ rawID: Any, _ key: String, _ call: [String: Any], _ options: [[String: Any]], reject: String?) -> Pending? {
        var choices: [String: String] = [:], item = AgentQuestions.Item(header: L("questionnaire.header.agent"), question: "")
        guard let question = AgentQuestions.text((call["rawInput"] as? [String: Any])?["question"] as? String ?? call["title"] as? String, limit: 8_192) else { return nil }
        item.question = question
        for option in options where option["kind"] as? String == "allow_once" {
            guard let id = option["optionId"] as? String, !id.isEmpty, id.utf8.count <= 256,
                  let label = AgentQuestions.text(option["name"] as? String, limit: 1_024, singleLine: true), choices[label] == nil else { return nil }
            choices[label] = id; item.options.append((label, ""))
        }
        let callID = (call["toolCallId"] as? String).flatMap { !$0.isEmpty && $0.utf8.count <= 256 ? $0 : nil } ?? "question-\(key)"
        guard let display = AgentQuestions.request(id: UUID().uuidString, runId: runId, toolUseId: callID, items: [item]) else { return nil }
        return Pending(rpcID: rawID, allowOption: nil, rejectOption: reject, display: display, question: .choices(choices))
    }
    /// `elicitation/create`: a form the question card asks, or one it cannot (a number, a nested
    /// object, more than the card holds), which is declined and named.
    private func elicitation(_ rawID: Any, _ key: String, _ params: [String: Any]) {
        guard let sessionId, params["sessionId"] as? String == sessionId else { reply(rawID, ["action": "cancel"]); return }
        let callID = (params["toolCallId"] as? String).flatMap { !$0.isEmpty && $0.utf8.count <= 256 ? $0 : nil }
        guard pending.count < Self.maximumPending, let form = ACPElicitationForm(params: params, freeChoices: freeChoices),
              let display = AgentQuestions.request(id: UUID().uuidString, runId: runId, toolUseId: callID ?? "question-\(key)", items: form.items) else {
            reply(rawID, ["action": "decline"])
            warning(L("acp.channel.formDeclined", ["question": ActivitySupport.clean(params["message"] as? String ?? "", maximumBytes: 1_000, singleLine: true)])); return
        }
        pending[display.id] = Pending(rpcID: rawID, allowOption: nil, rejectOption: nil, display: display, question: .form(form), row: callID != nil)
        if callID != nil { activity(display, "waiting") }
        emit(display)
    }
    /// The question card's answers, sent as the form's content or the picked choice's option.
    func answerQuestions(requestId: String, answers: [String: UserQuestionAnswer]) throws {
        guard !closed, let ask = pending[requestId], let question = ask.question else { throw MightyError(L("permission.error.questionGone")) }
        let checked = try AgentQuestions.answers(ask.display, answers)
        switch question {
        case .form(let form):
            // Checked first: an answer the form cannot carry leaves the card answerable.
            let content = try form.content(checked)
            pending.removeValue(forKey: requestId)
            reply(ask.rpcID, ["action": "accept", "content": content])
        case .choices(let options):
            guard let answer = checked.values.first, answer.typed.isEmpty, answer.picked.count == 1, let option = options[answer.picked[0]] else { throw MightyError(L("questionnaire.error.choicesOnly")) }
            pending.removeValue(forKey: requestId)
            self.answer(ask.rpcID, option)
        }
        var value = ask.display; value.state = "answered"
        if ask.row { activity(value, "running") }
        emit(value)
    }
    func respond(requestId: String, allow: Bool) throws {
        guard !closed, let ask = pending[requestId] else { throw MightyError(L("permission.error.requestGone")) }
        guard !allow || ask.allowOption != nil else { throw MightyError(L("acp.channel.denyOnly")) }
        pending.removeValue(forKey: requestId)
        // A question's Cancel: the form is declined, the clarify approval skipped.
        if case .form = ask.question { reply(ask.rpcID, ["action": "decline"]) }
        else { answer(ask.rpcID, allow ? ask.allowOption : ask.rejectOption) }
        var value = ask.display; value.state = allow ? "allowed" : "denied"
        // A dismissed question's call is settled by the agent's own update, not marked failed here.
        if ask.row { activity(value, allow || ask.question != nil ? "running" : "error") }
        emit(value)
    }
    /// The option the user chose; without a reject option the request is cancelled, which the agent reads as a no.
    private func answer(_ id: Any, _ optionId: String?) {
        reply(id, ["outcome": optionId.map { ["outcome": "selected", "optionId": $0] } ?? ["outcome": "cancelled"]])
    }
    private func cancelPending(_ id: String) {
        guard let ask = pending.removeValue(forKey: id) else { return }
        var value = ask.display
        value.state = "cancelled"; if ask.question == nil { activity(value, "stopped") }; emit(value)
    }
    func cancelAll() {
        guard !closed else { return }; closed = true; _ = lines.finish(); rpc.removeAll()
        let asks = Array(pending.values); pending.removeAll()
        for ask in asks { var value = ask.display; value.state = "cancelled"; if ask.row { activity(value, "stopped") }; emit(value) }
    }
    private func failClosed(_ message: String) { guard !closed else { return }; failed = true; cancelAll(); fail(message) }
    private func call(_ method: String, _ params: [String: Any]) {
        sequence += 1; let id = "mighty-\(sequence)"; rpc[Self.idKey(id)!] = method
        send(["jsonrpc": "2.0", "id": id, "method": method, "params": params])
    }
    private func reply(_ id: Any, _ result: [String: Any]) { send(["jsonrpc": "2.0", "id": id, "result": result]) }
    private func rpcError(_ id: Any, _ code: Int, _ message: String) { send(["jsonrpc": "2.0", "id": id, "error": ["code": code, "message": message]]) }
    private func send(_ obj: [String: Any]) { if let data = try? JSONSerialization.data(withJSONObject: obj, options: [.sortedKeys, .withoutEscapingSlashes]) { write(data + Data([10])) } }

    static func idKey(_ raw: Any?) -> String? {
        if let text = raw as? String, !text.isEmpty, text.utf8.count <= 256 { return "s:" + text }
        if let number = raw as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(), number.doubleValue.isFinite, number.doubleValue.rounded() == number.doubleValue { return "n:" + number.stringValue }
        return nil
    }
    /// ACP's tool kinds named as the approval card and the activity rows already read them.
    static func toolName(kind: String?, title: String?) -> String {
        switch kind {
        case "execute": return "Bash"
        case "edit", "delete", "move": return "Edit"
        case "read": return "Read"
        case "search": return "Grep"
        case "fetch": return "WebFetch"
        default:
            let title = ActivitySupport.clean(title ?? "", maximumBytes: 80, singleLine: true)
            return title.isEmpty ? "Tool" : title
        }
    }
    /// A tool's input with camelCase keys in the snake_case the card and the activity summary read
    /// (`filePath` → `file_path`); anything that is not an object is shown under `input`. An input
    /// wrapped as `{tool, arguments}` (Hermes's edit approvals) is shown as its arguments.
    static func displayInput(_ raw: Any?) -> [String: Any] {
        guard var fields = raw as? [String: Any] else { return raw.map { $0 is NSNull ? [:] : ["input": $0] } ?? [:] }
        if fields.count == 2, fields["tool"] is String, let arguments = fields["arguments"] as? [String: Any] { fields = arguments }
        var result: [String: Any] = [:]
        for (key, value) in fields.prefix(64) {
            var snake = ""
            for character in key {
                if character.isUppercase, !snake.isEmpty, snake.last != "_" { snake.append("_") }
                snake += character.lowercased()
            }
            result[snake == "filepath" ? "file_path" : snake] = value
        }
        return result
    }
    /// What a tool call without `rawInput` still says about itself: its first location's path, a
    /// shell line its content shows (`$ ls`), or the title's part after `tool: `.
    static func describedInput(_ call: [String: Any]) -> [String: Any]? {
        if let path = (call["locations"] as? [[String: Any]])?.first?["path"] as? String, !path.isEmpty { return ["file_path": path] }
        for block in (call["content"] as? [[String: Any]] ?? []).prefix(4) {
            if let inner = block["content"] as? [String: Any], let text = inner["text"] as? String, text.hasPrefix("$ ") { return ["command": String(text.dropFirst(2).prefix(8_192))] }
        }
        guard let title = call["title"] as? String, let colon = title.range(of: ": ") else { return nil }
        let rest = String(title[colon.upperBound...])
        return rest.isEmpty ? nil : ["description": rest]
    }
    /// A title of the form `tool: preview` names its tool before the colon; any other title is the name.
    static func titledTool(_ title: String?) -> String? {
        guard let title, let colon = title.range(of: ": ") else { return title }
        let name = String(title[..<colon.lowerBound])
        return name.range(of: "^[A-Za-z0-9_.-]{1,64}$", options: .regularExpression) != nil ? name : title
    }
    private static func displayJSON(_ obj: [String: Any]) -> String? {
        guard JSONSerialization.isValidJSONObject(obj), let data = try? JSONSerialization.data(withJSONObject: obj, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]) else { return nil }
        return String(decoding: data, as: UTF8.self).unicodeScalars.map {
            $0.properties.generalCategory == .format ? String($0).utf16.map { String(format: "\\u%04x", $0) }.joined() : String($0)
        }.joined()
    }
}

/// A turn's `session/cancel` frame behind a lock, so the stop signal (sent off the runner's
/// actor, before the process is ended) can read it.
final class ACPCancellation: @unchecked Sendable {
    private let lock = NSLock()
    private var frame: Data?
    func set(_ value: Data?) { lock.lock(); frame = value.map { $0 + Data([10]) }; lock.unlock() }
    /// The frame, once: a second stop does not send it again.
    func take() -> Data? { lock.lock(); defer { frame = nil; lock.unlock() }; return frame }
}
