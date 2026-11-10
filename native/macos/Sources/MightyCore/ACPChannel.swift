import Foundation
import CoreFoundation

/// One Agent Client Protocol run (https://agentclientprotocol.com), owned by ProcessRunner's
/// actor: JSON-RPC 2.0 over the agent's stdio, one JSON object per line. The app is the
/// client: `initialize`, then `session/new` (or `session/resume` / `session/load` of the
/// pane's session), the pane's settings as `session/set_config_option`, then one
/// `session/prompt`. What the agent streams (`session/update`) reaches the parser as
/// `acp.*` events (`CLIStreamParser.acp`); a `session/request_permission` becomes the
/// pane's approval card, and only the user's single-call answer leaves the channel.
/// Nothing here is opencode's own: another ACP agent (Hermes) uses the same channel.
final class ACPChannel {
    static let maximumFrameBytes = 32 * 1024 * 1024
    static let maximumInputBytes = 65_536
    static let maximumPending = 16
    /// What the client offers: no file system and no terminal, so the agent uses its own tools.
    static let clientCapabilities: [String: Any] = ["fs": ["readTextFile": false, "writeTextFile": false], "terminal": false]

    private struct Pending { let rpcID: Any; let allowOption: String?; let rejectOption: String?; var display: ToolPermissionRequest }
    private let runId: String
    private let request: StartRunRequest
    private let workspacePath: String
    private let attachments: AttachmentPreparation
    /// `session/set_config_option` values sent before the prompt, in order (model, effort, mode).
    private let configValues: [(id: String, value: String)]
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

    init(runId: String, request: StartRunRequest, workspacePath: String, attachments: AttachmentPreparation, configValues: [(id: String, value: String)],
         write: @escaping (Data) -> Void, event: @escaping ([String: Any]) -> Void,
         emit: @escaping (ToolPermissionRequest) -> Void, activity: @escaping (ToolPermissionRequest, String) -> Void,
         warning: @escaping (String) -> Void, fail: @escaping (String) -> Void, completed: @escaping () -> Void) {
        self.runId = runId; self.request = request; self.workspacePath = workspacePath; self.attachments = attachments; self.configValues = configValues
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
            turnCompleted = true; cancelAll(); completed()
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
            failed = true; cancelAll(); completed()
        }
    }

    private func notification(_ method: String, _ params: [String: Any]) {
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
    func respond(requestId: String, allow: Bool) throws {
        guard !closed, let ask = pending[requestId] else { throw MightyError(L("permission.error.requestGone")) }
        guard !allow || ask.allowOption != nil else { throw MightyError(L("acp.channel.denyOnly")) }
        pending.removeValue(forKey: requestId)
        answer(ask.rpcID, allow ? ask.allowOption : ask.rejectOption)
        var value = ask.display; value.state = allow ? "allowed" : "denied"
        activity(value, allow ? "running" : "error"); emit(value)
    }
    /// The option the user chose; without a reject option the request is cancelled, which the agent reads as a no.
    private func answer(_ id: Any, _ optionId: String?) {
        reply(id, ["outcome": optionId.map { ["outcome": "selected", "optionId": $0] } ?? ["outcome": "cancelled"]])
    }
    private func cancelPending(_ id: String) {
        guard var value = pending.removeValue(forKey: id)?.display else { return }
        value.state = "cancelled"; activity(value, "stopped"); emit(value)
    }
    func cancelAll() {
        guard !closed else { return }; closed = true; _ = lines.finish(); rpc.removeAll()
        let asks = Array(pending.values); pending.removeAll()
        for ask in asks { var value = ask.display; value.state = "cancelled"; activity(value, "stopped"); emit(value) }
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
    /// (`filePath` → `file_path`); anything that is not an object is shown under `input`.
    static func displayInput(_ raw: Any?) -> [String: Any] {
        guard let fields = raw as? [String: Any] else { return raw.map { $0 is NSNull ? [:] : ["input": $0] } ?? [:] }
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
    private static func displayJSON(_ obj: [String: Any]) -> String? {
        guard JSONSerialization.isValidJSONObject(obj), let data = try? JSONSerialization.data(withJSONObject: obj, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]) else { return nil }
        return String(decoding: data, as: UTF8.self).unicodeScalars.map {
            $0.properties.generalCategory == .format ? String($0).utf16.map { String(format: "\\u%04x", $0) }.joined() : String($0)
        }.joined()
    }
}
