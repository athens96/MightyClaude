import Foundation

/// Ephemeral, single-call consent. Never persisted in a snapshot or forwarded
/// to an older remote host. The original input stays inside the run's channel.
public struct ToolPermissionRequest: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var runId: String
    public var toolUseId: String
    public var toolName: String
    public var inputJSON: String
    public var summary: String
    public var reason: String?
    public var blockedPath: String?
    public var state: String
    public var canAllow: Bool

    public init(id: String, runId: String, toolUseId: String, toolName: String, inputJSON: String, summary: String, reason: String? = nil, blockedPath: String? = nil, state: String = "pending", canAllow: Bool = true) {
        self.id = id; self.runId = runId; self.toolUseId = toolUseId
        self.toolName = toolName; self.inputJSON = inputJSON; self.summary = summary
        self.reason = reason; self.blockedPath = blockedPath; self.state = state; self.canAllow = canAllow
    }
}

/// Claude Code's supported SDK stdio protocol. Only `can_use_tool` asks reach
/// this surface: the CLI evaluates configured denies and modes before asking.
/// No settings updates, persistent rules or mode changes are ever returned.
/// All methods run on the owning ProcessRunner actor (or synchronously in tests).
final class ClaudePermissionChannel {
    static let maximumInputBytes = 65_536
    static let maximumPending = 16
    private struct Pending { var display: ToolPermissionRequest; let input: [String: Any] }
    let runId: String
    let initializationId = UUID().uuidString
    private let prompt: Data
    private let write: (Data) -> Void
    private let emit: (ToolPermissionRequest) -> Void
    private let activity: (ToolPermissionRequest, String) -> Void
    private let warning: (String) -> Void
    private let fail: (String) -> Void
    private var pending: [String: Pending] = [:]
    private var seen = Set<String>()
    private var closed = false
    private(set) var initialized = false
    private(set) var failed = false

    init(runId: String, prompt: Data, write: @escaping (Data) -> Void, emit: @escaping (ToolPermissionRequest) -> Void, activity: @escaping (ToolPermissionRequest, String) -> Void, warning: @escaping (String) -> Void, fail: @escaping (String) -> Void) {
        self.runId = runId; self.prompt = prompt; self.write = write; self.emit = emit
        self.activity = activity; self.warning = warning; self.fail = fail
    }

    func start() {
        send(["type": "control_request", "request_id": initializationId,
              "request": ["subtype": "initialize", "hooks": [:], "sdkMcpServers": [], "supportedDialogKinds": []]])
    }

    func initializationTimedOut() {
        guard !closed, !initialized else { return }
        failClosed("Claude 승인 채널 초기화 시간이 초과되었습니다.")
    }

    func receive(_ data: Data) {
        guard !closed, let envelope = try? JSONSerialization.jsonObject(with: data) as? [String: Any], let type = envelope["type"] as? String else { return }
        if type == "control_response", let response = envelope["response"] as? [String: Any], response["request_id"] as? String == initializationId {
            guard !initialized else { return }
            guard response["subtype"] as? String == "success" else { failClosed("Claude 승인 채널을 초기화하지 못했습니다."); return }
            initialized = true
            // Some CLIs replay pending asks in initialize as well as live frames.
            for request in response["pending_permission_requests"] as? [[String: Any]] ?? [] {
                receiveRequest(request)
                if closed { break }
            }
            if !closed { write(prompt) }
            return
        }
        if type == "control_cancel_request", let id = envelope["request_id"] as? String {
            settle(id, state: "cancelled", activityState: "stopped"); return
        }
        if type == "control_request" { receiveRequest(envelope) }
    }

    private func receiveRequest(_ envelope: [String: Any]) {
        guard !closed else { return }
        guard let id = envelope["request_id"] as? String, CoreValidation.identifier(id), let request = envelope["request"] as? [String: Any], let subtype = request["subtype"] as? String else { failClosed("Claude 제어 요청 형식이 올바르지 않습니다."); return }
        guard seen.insert(id).inserted else { return }
        guard seen.count <= 2_048 else { failClosed("한 실행의 Claude 승인 요청 수 제한을 초과했습니다."); return }
        // No dialog kinds are declared in initialize. A host must not settle a
        // future dialog kind it cannot render; the CLI owns its deadline.
        if subtype == "request_user_dialog" { warning("현재 앱에서 표시할 수 없는 Claude 대화상자 요청입니다. 실행을 중지할 수 있습니다."); return }
        if subtype == "elicitation" { success(id, result: ["action": "decline"]); warning("현재 앱에서 지원하지 않는 MCP 입력 요청을 거부했습니다."); return }
        guard subtype == "can_use_tool" else { error(id, message: "This host does not support this control request."); return }
        guard let toolName = request["tool_name"] as? String, !toolName.isEmpty, toolName.utf8.count <= 256,
              let toolUseId = request["tool_use_id"] as? String, CoreValidation.identifier(toolUseId),
              let input = request["input"] as? [String: Any], JSONSerialization.isValidJSONObject(input) else {
            error(id, message: "Invalid tool permission request."); warning("형식이 올바르지 않은 도구 승인 요청을 거부했습니다."); return
        }
        guard pending.count < Self.maximumPending else { deny(id, toolUseId: toolUseId, message: "Too many pending permission requests."); warning("대기 중인 도구 승인 요청이 16개를 넘어 추가 요청을 거부했습니다."); return }
        guard let display = try? Self.inputDisplay(input), display.utf8.count <= Self.maximumInputBytes else {
            deny(id, toolUseId: toolUseId, message: "Tool input exceeds the host's complete-display limit; permission denied.")
            warning("도구 인자가 64 KiB 표시 제한을 넘어 승인하지 않았습니다. 전체 내용을 표시할 수 없는 요청은 허용하지 않습니다."); return
        }
        let interaction = request["requires_user_interaction"] as? Bool == true || toolName == "AskUserQuestion"
        let details = [request["title"] as? String, request["description"] as? String, request["decision_reason"] as? String].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: "\n")
        let originalPath = request["blocked_path"] as? String
        let completeMetadata = details.utf8.count <= 8_192 && (originalPath?.utf8.count ?? 0) <= 8_192
        var reason = ActivitySupport.clean(details, maximumBytes: 8_192)
        if interaction { reason += (reason.isEmpty ? "" : "\n\n") + "이 도구에는 별도의 입력 화면이 필요합니다. 현재 앱에서는 한 번 허용할 수 없으며 거부하거나 실행을 중지할 수 있습니다." }
        if !completeMetadata { reason += "\n\n승인 설명이 표시 한도를 넘어 허용할 수 없습니다." }
        let value = ToolPermissionRequest(id: id, runId: runId, toolUseId: toolUseId,
            toolName: ActivitySupport.clean(toolName, maximumBytes: 256, singleLine: true), inputJSON: display,
            summary: ActivitySupport.summary(tool: toolName, input: input), reason: reason.isEmpty ? nil : reason,
            blockedPath: originalPath.map { ActivitySupport.clean($0, maximumBytes: 8_192) }, canAllow: !interaction && completeMetadata)
        pending[id] = Pending(display: value, input: input)
        activity(value, "waiting"); emit(value)
    }

    func respond(requestId: String, allow: Bool) throws {
        guard !closed, let request = pending[requestId] else { throw MightyError("이미 처리되었거나 종료된 승인 요청입니다.") }
        guard !allow || request.display.canAllow else { throw MightyError("이 요청에는 별도의 입력 화면이 필요하거나 전체 내용을 표시할 수 없어 허용할 수 없습니다.") }
        // Removal precedes callbacks/writes: even a reentrant second click has
        // no request left to approve. updatedInput is the original object.
        pending.removeValue(forKey: requestId)
        if allow { success(requestId, result: ["behavior": "allow", "updatedInput": request.input, "toolUseID": request.display.toolUseId]) }
        else { deny(requestId, toolUseId: request.display.toolUseId, message: "The user denied this tool request in MightyClaude.") }
        var display = request.display; display.state = allow ? "allowed" : "denied"
        activity(display, allow ? "running" : "error"); emit(display)
    }

    func cancelAll() {
        guard !closed else { return }; closed = true
        for id in Array(pending.keys) { settle(id, state: "cancelled", activityState: "stopped") }
    }

    private func settle(_ id: String, state: String, activityState: String) {
        guard var value = pending.removeValue(forKey: id)?.display else { return }
        value.state = state; activity(value, activityState); emit(value)
    }
    private func failClosed(_ message: String) { failed = true; cancelAll(); fail(message) }
    private func success(_ id: String, result: [String: Any]) { send(["type": "control_response", "response": ["subtype": "success", "request_id": id, "response": result]]) }
    private func deny(_ id: String, toolUseId: String, message: String) { success(id, result: ["behavior": "deny", "message": message, "toolUseID": toolUseId]) }
    private func error(_ id: String, message: String) { send(["type": "control_response", "response": ["subtype": "error", "request_id": id, "error": message]]) }
    private func send(_ object: [String: Any]) {
        guard let data = try? JSONSerialization.data(withJSONObject: object, options: [.sortedKeys, .withoutEscapingSlashes]) else { return }
        write(data + Data([10]))
    }
    private static func inputDisplay(_ input: [String: Any]) throws -> String {
        let data = try JSONSerialization.data(withJSONObject: input, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        // Keep exact JSON values while making invisible display controls
        // explicit. Do not strip characters from the approved original input.
        return String(decoding: data, as: UTF8.self).unicodeScalars.map { scalar in
            if scalar.properties.generalCategory == .format || (scalar.properties.generalCategory == .control && ![9, 10, 13].contains(scalar.value)) {
                return String(scalar).utf16.map { String(format: "\\u%04x", $0) }.joined()
            }
            return String(scalar)
        }.joined()
    }
}
